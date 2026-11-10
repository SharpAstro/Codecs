using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 3: <see cref="HeifDecoder"/> against libheif (Magick.NET), which composes, converts and applies the
/// transformative properties the same way. Compared EXACTLY at the image's own bit depth: the YCbCr
/// conversion was measured to match libheif sample for sample (see <see cref="HeifDecoder"/>), so any
/// difference is a bug. libheif returns the coded size and does not crop to <c>ispe</c>, so its output is
/// compared over the region this decoder returns.
/// </summary>
public class HeifDecodeTests(ITestOutputHelper output)
{
    // ---- the Canon pictures ------------------------------------------------------------------------

    [Theory]
    [InlineData("THMB")]
    [InlineData("PRVW")]
    public void R6_CrPreview_MatchesLibheif(string name)
    {
        var file = HeifCanonFileTests.Load("canon-r6-hdrpq.cr3");
        var preview = Cr3HevcPreviews.Find(file).Single(p => p.Name == name);
        var heif = HeifBuilder.WrapHevcPicture(preview.HvcC, preview.NalUnits, (uint)preview.Width, (uint)preview.Height, preview.Colr, preview.Pixi);
        var rgb = HeifDecoder.Decode(heif);
        (rgb.Width, rgb.Height).ShouldBe((preview.Width, preview.Height), "cropped to ispe, not the coded size");
        rgb.ColorEncoding.ShouldBe(new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Pq, FullRange = true });
        AssertMatchesLibheif(name, heif, rgb);
    }

    [Fact]
    public void R7_Thumbnails_MatchLibheif()
    {
        var file = HeifCanonFileTests.Load("canon-r7-hdrpq.hif");
        var heif = HeifReader.Read(file);
        foreach (var thumbnail in heif.ThumbnailsOf(heif.PrimaryItem))
        {
            var rgb = HeifDecoder.Decode(file, thumbnail);
            var described = heif.Describe(file, thumbnail);
            (rgb.Width, rgb.Height).ShouldBe(((int)described.Width, (int)described.Height));
            // Magick decodes the primary item; wrap the thumbnail as a file of its own for it.
            var wrapped = HeifBuilder.WrapHevcPicture(RawProperty(file, thumbnail, "hvcC")!, heif.ReadItemData(file, thumbnail),
                described.Width, described.Height, RawProperty(file, thumbnail, "colr"), RawProperty(file, thumbnail, "pixi"));
            AssertMatchesLibheif(thumbnail.ToString(), wrapped, rgb);
        }
    }

    [Fact]
    public void R7_WholeGrid_MatchesLibheif()
    {
        var file = HeifCanonFileTests.Load("canon-r7-hdrpq.hif");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rgb = HeifDecoder.Decode(file);
        output.WriteLine($"6960x4640 grid decoded in {watch.ElapsedMilliseconds} ms");
        (rgb.Width, rgb.Height).ShouldBe((6960, 4640));
        AssertMatchesLibheif("R7 grid", file, rgb);
    }

    // ---- the x265 fixtures, wrapped with chosen colour ---------------------------------------------

    /// <summary>Full-range 4:2:2, the Canon format: libheif and H.273 agree exactly, at every depth and matrix tried.</summary>
    public static TheoryData<string, int> ExactColourCases => new()
    {
        { "x265-422p10-modes", 9 },  // BT.2020 NCL: the Canon combination
        { "x265-422p10-modes", 1 },  // BT.709
        { "x265-422p12", 1 },        // 12-bit
        { "x265-422p8-ctu16", 5 },   // BT.601, 8-bit
        { "x265-422p8-ctu16", 9 },
        { "x265-444p8-modes", 1 },          // 4:4:4: no chroma to upsample
        { "x265-400p8", 1 },                // monochrome: R' = G' = B' = Y'
        { "x265-400p10", 9 },
    };

    [Theory]
    [MemberData(nameof(ExactColourCases))]
    public void X265Fixture_FullRange422_MatchesLibheifExactly(string name, int matrix)
    {
        var (heif, width, height) = Wrap(name, HeifBuilder.Nclx(1, 13, matrix, true));
        var rgb = HeifDecoder.Decode(heif);
        (rgb.Width, rgb.Height).ShouldBe((width, height));
        AssertMatchesLibheif($"{name} matrix {matrix} full", heif, rgb);
    }

    /// <summary>
    /// Narrow range, 4:2:0 at 8 bits and no colour box at all: libheif takes integer shortcuts of its own
    /// there, so this decoder (H.273's arithmetic) is held to a measured tolerance instead: never more than 2
    /// codes apart, and on under half the samples (measured: 1.2-45 %, max 1 except BT.709 narrow 10-bit at 2).
    /// </summary>
    public static TheoryData<string, int, bool> TolerantColourCases => new()
    {
        { "x265-422p10-modes", 1, false },       // BT.709, narrow range
        { "x265-420p8-window", 6, false },       // BT.601, narrow, 4:2:0 8-bit with a conformance window
        { "x265-420p8-window", 1, true },        // 4:2:0 8-bit, full range
        { "x265-420p10-lowqp", 9, false },
        { "x265-422p8-ctu16", -1, true },        // no colr at all: the VUI (narrow range here)
        { "x265-444p12-loopfilters", 9, true },  // 4:4:4 12-bit BT.2020 full: 23 of 147,456 samples 1 apart (its 4:4:4 path)
    };

    [Theory]
    [MemberData(nameof(TolerantColourCases))]
    public void X265Fixture_OtherColour_IsWithinTwoCodesOfLibheif(string name, int matrix, bool fullRange)
    {
        var (heif, width, height) = Wrap(name, matrix < 0 ? null : HeifBuilder.Nclx(1, 13, matrix, fullRange));
        var rgb = HeifDecoder.Decode(heif);
        (rgb.Width, rgb.Height).ShouldBe((width, height));
        AssertMatchesLibheif($"{name} matrix {matrix} full {fullRange}", heif, rgb, tolerance: 2, maxShare: 0.5);
    }

    // ---- transformative properties -----------------------------------------------------------------

    /// <summary>
    /// The geometry, on 4:2:0 with an nclx equal to the stream's VUI (BT.601 narrow): libheif rotates the
    /// Y'CbCr planes and converts with the VUI when it rotates, so only then do both paths convert alike. A
    /// transform moves pixels, so the narrow-range residual (a code) is all that may differ; a geometry error
    /// shows as differences in the hundreds.
    /// </summary>
    public static TheoryData<string> TransformCases => ["irot90", "irot180", "irot270", "imir0", "imir1", "clap", "clap+irot90+imir0", "clap+imir1+irot270"];

    [Theory]
    [MemberData(nameof(TransformCases))]
    public void Transforms_MatchLibheif(string transforms)
    {
        var (configuration, item) = HevcFixtureTests.Load("x265-420p8-window");
        var builder = new HeifBuilder().AddItem(new HeifBuilder.Item(1, "hvc1", item));
        builder.Associate(1, "hvcC", HvcC(configuration), essential: true);
        builder.Associate(1, "ispe", HeifBuilder.Ispe(230, 166));
        builder.Associate(1, "colr", HeifBuilder.Nclx(1, 13, 6, false));
        foreach (var t in transforms.Split('+'))
        {
            if (t == "clap")
            {
                // Alone: 151x100, 7 right and 5 up of centre, whose left edge falls on a half pixel (46.5) to
                // exercise the rounding. Combined with a rotation: 150x100, 6 right, 5 up, on whole pixels
                // (46, 28). libheif rounds a half-pixel window differently once it also rotates (a pixel away),
                // which no camera file exercises, so the combined cases test the geometry, not that rounding.
                var alone = transforms == "clap";
                var clap = HeifBuilder.Concat(HeifBuilder.U32(alone ? 151u : 150u), HeifBuilder.U32(1), HeifBuilder.U32(100), HeifBuilder.U32(1),
                    HeifBuilder.U32(alone ? 7u : 6u), HeifBuilder.U32(1), HeifBuilder.U32(unchecked((uint)-5)), HeifBuilder.U32(1));
                builder.Associate(1, "clap", clap, essential: true);
            }
            else if (t.StartsWith("irot", StringComparison.Ordinal)) builder.Associate(1, "irot", HeifBuilder.Irot(int.Parse(t[4..])), essential: true);
            else builder.Associate(1, "imir", HeifBuilder.Imir(int.Parse(t[4..])), essential: true);
        }
        var heif = builder.Build();

        var rgb = HeifDecoder.Decode(heif);
        HeifImageDecoder.TryReadInfo(heif, out var info).ShouldBeTrue();
        (info.Width, info.Height).ShouldBe((rgb.Width, rgb.Height), "TryReadInfo predicts the transformed size");
        AssertMatchesLibheif(transforms, heif, rgb, sameSize: true, tolerance: 1, maxShare: 0.5);
    }

    /// <summary>
    /// The conformance window's left and top offsets. No fixture has them (x265 pads only on the right and
    /// bottom, Canon sets none), so this drives the conversion over a synthetic 4:2:2 picture whose window
    /// trims every side, against the expected samples computed here from the window by hand. (The mutation
    /// check found the gap: ignoring the left offset passed every other test.)
    /// </summary>
    [Fact]
    public void ConformanceWindow_EverySide_SelectsTheRightSamples()
    {
        const int width = 24, height = 10, chromaWidth = 12;
        var random = new Random(66);
        var luma = new ushort[width * height];
        var cb = new ushort[chromaWidth * height];
        var cr = new ushort[chromaWidth * height];
        for (var i = 0; i < luma.Length; i++) luma[i] = (ushort)random.Next(1024);
        for (var i = 0; i < cb.Length; i++) { cb[i] = (ushort)random.Next(1024); cr[i] = (ushort)random.Next(1024); }
        var window = (Left: 4, Right: 2, Top: 3, Bottom: 1);
        var picture = new HevcPicture(width, height, 2, 10, 10, luma, cb, cr, chromaWidth, height, window, null);
        var colour = HeifDecoder.Colour.Create(10, 2, ColorPrimaries.BT2020, TransferFunction.Pq, MatrixCoefficients.BT2020NCL, true);

        int usedWidth = width - 6, usedHeight = height - 4;
        var canvas = new ushort[usedWidth * usedHeight * 3];
        HeifDecoder.Convert(picture, colour, usedWidth, usedHeight, canvas, usedWidth, usedHeight, 0, 0);

        for (var y = 0; y < usedHeight; y++)
        {
            for (var x = 0; x < usedWidth; x++)
            {
                // Coded sample (x + 4, y + 3); its chroma is column (x + 4) / 2 of row y + 3.
                int Y = luma[(y + 3) * width + x + 4], Cb = cb[(y + 3) * chromaWidth + (x + 4) / 2] - 512, Cr = cr[(y + 3) * chromaWidth + (x + 4) / 2] - 512;
                double r = Y + 2 * (1 - 0.2627) * Cr, b = Y + 2 * (1 - 0.0593) * Cb, g = (Y - 0.2627 * r - 0.0593 * b) / (1 - 0.2627 - 0.0593);
                ushort Code(double v) => (ushort)Math.Clamp(Math.Floor(v + 0.5), 0, 1023);
                var at = 3 * (y * usedWidth + x);
                (canvas[at], canvas[at + 1], canvas[at + 2]).ShouldBe((Code(r), Code(g), Code(b)), $"pixel ({x}, {y})");
            }
        }
    }

    // ---- facade and float tier ---------------------------------------------------------------------

    [Fact]
    public void Facade_DecodesHeif_AsUInt16WithItsColour_AndRefusesRgba8ForPq()
    {
        var file = HeifCanonFileTests.Load("canon-r6-hdrpq.cr3");
        var preview = Cr3HevcPreviews.Find(file).Single(p => p.Name == "THMB");
        var heif = HeifBuilder.WrapHevcPicture(preview.HvcC, preview.NalUnits, (uint)preview.Width, (uint)preview.Height, preview.Colr, preview.Pixi);

        ImageCodecs.TryReadInfo(heif, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(320, 214, 3, SampleFormat.UInt16));
        ImageCodecs.TryDecode(heif, out var image).ShouldBeTrue();
        (image!.Width, image.Height, image.Channels, image.SampleFormat).ShouldBe((320, 214, 3, SampleFormat.UInt16));
        image.ColorEncoding.Transfer.ShouldBe(TransferFunction.Pq);

        // Codes scaled to fill: 10-bit 1023 is 65535, 0 is 0, and the scale is monotone.
        var rgb = HeifDecoder.Decode(heif);
        var wide = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.Pixels);
        for (var i = 0; i < rgb.Samples.Length; i += 97)
            wide[i].ShouldBe((ushort)((rgb.Samples[i] * 65535 + 511) / 1023));

        ImageCodecs.TryDecodeIntoRgba8(heif, new byte[320 * 214 * 4]).ShouldBeFalse("PQ has no canonical 8-bit projection");
    }

    /// <summary>
    /// A clap of 32-bit fractions can name a width of 4 billion; cast to int it saturated and an int sum
    /// wrapped, so Decode, TryDecode and ImageCodecs.TryDecode threw OverflowException (out of bool-returning
    /// Try methods) and TryReadInfo reported a width of 2147483647 (found by the PR #69 review). Now the
    /// rectangle is checked before any cast, in one place for the decode and the facade's size.
    /// </summary>
    [Fact]
    public void CleanApertureOutsideThe32BitRange_IsInvalidData_AndTheFacadeSaysNo()
    {
        var (configuration, item) = HevcFixtureTests.Load("x265-420p8-window");
        var builder = new HeifBuilder().AddItem(new HeifBuilder.Item(1, "hvc1", item));
        builder.Associate(1, "hvcC", HvcC(configuration), essential: true);
        builder.Associate(1, "ispe", HeifBuilder.Ispe(230, 166));
        builder.Associate(1, "clap", HeifBuilder.Concat(
            HeifBuilder.U32(4294967295), HeifBuilder.U32(1), HeifBuilder.U32(1), HeifBuilder.U32(1),
            HeifBuilder.U32(2147483647), HeifBuilder.U32(1), HeifBuilder.U32(0), HeifBuilder.U32(1)), essential: true);
        var heif = builder.Build();

        Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(heif)).Message.ShouldContain("'clap'");
        ImageCodecs.TryDecode(heif, out _).ShouldBeFalse();
        ImageCodecs.TryReadInfo(heif, out _).ShouldBeFalse();
        HeifImageDecoder.TryReadInfo(heif, out _).ShouldBeFalse();
        HeifImageDecoder.TryDecodeRgbaFloat(heif, out _, out _, out _, out _).ShouldBeFalse();
    }

    /// <summary>
    /// A VUI that codes a chroma location but no signal type states no colour or range, so it converts as no
    /// VUI does (the full-range default), not as narrow range (the PR #69 review's question).
    /// </summary>
    [Fact]
    public void VuiWithOnlyAChromaLocation_ConvertsAsNoVuiDoes()
    {
        var (heif, _, _) = Wrap("x265-420p8-window", null);
        var image = HeifReader.Read(heif).DescribePrimary(heif);
        var none = HeifDecoder.Colour.For(image, null, 8, 1);
        var locationOnly = HeifDecoder.Colour.For(image, new SharpAstro.Heif.Hevc.HevcVideoSignal(5, false, null, null, null, 2) { SignalTypePresent = false }, 8, 1);
        (locationOnly.FullRange, locationOnly.Kr, locationOnly.Kb).ShouldBe((none.FullRange, none.Kr, none.Kb));
        none.FullRange.ShouldBeTrue();
        HeifDecoder.Colour.For(image, new SharpAstro.Heif.Hevc.HevcVideoSignal(5, false, null, null, null, null), 8, 1).FullRange
            .ShouldBeFalse("a coded signal type that says narrow range is still narrow");
    }

    [Fact]
    public void Facade_DecodesAnSdrHeif_ToRgba8()
    {
        var (heif, width, height) = Wrap("x265-420p8-window", HeifBuilder.Nclx(1, 13, 6, false));
        var rgba = new byte[width * height * 4];
        ImageCodecs.TryDecodeIntoRgba8(heif, rgba).ShouldBeTrue();
        var rgb = HeifDecoder.Decode(heif);
        rgb.BitDepth.ShouldBe(8);
        for (var p = 0; p < width * height; p++)
        {
            // Every pixel, every channel: a swapped G and B or an offset slip would show here.
            rgba[4 * p].ShouldBe((byte)rgb.Samples[3 * p], $"R at {p}");
            rgba[4 * p + 1].ShouldBe((byte)rgb.Samples[3 * p + 1], $"G at {p}");
            rgba[4 * p + 2].ShouldBe((byte)rgb.Samples[3 * p + 2], $"B at {p}");
            rgba[4 * p + 3].ShouldBe((byte)255, $"alpha at {p}");
        }
    }

    [Fact]
    public void FloatProjection_IsThePqCurveOnThe203Scale()
    {
        var file = HeifCanonFileTests.Load("canon-r7-hdrpq.hif");
        var heif = HeifReader.Read(file);
        var rgb = HeifDecoder.Decode(file, heif.ThumbnailsOf(heif.PrimaryItem)[0]);
        var floats = rgb.ToLinearRgbaFloat(out var encoding);
        encoding.ShouldBe(new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Linear, FullRange = true, Float = FloatSemantics.DisplayReferred });
        floats.Length.ShouldBe(rgb.Width * rgb.Height * 4);
        for (var p = 0; p < rgb.Width * rgb.Height; p += 53)
        {
            for (var c = 0; c < 3; c++)
            {
                var nits = HdrTransfer.PqInverseEotf(rgb.Samples[3 * p + c] / 1023f);
                floats[4 * p + c].ShouldBe(nits / 203f, 1e-6f * Math.Max(1f, nits / 203f));
            }
            floats[4 * p + 3].ShouldBe(1f);
        }
        var peak = floats.Where((_, i) => i % 4 != 3).Max();
        output.WriteLine($"thumbnail peak {peak:0.000} x 203 cd/m2 = {peak * 203:0} cd/m2");
    }

    [Fact]
    public void FacadeFloat_MatchesTheRgbImagesProjection()
    {
        var file = HeifCanonFileTests.Load("canon-r6-hdrpq.cr3");
        var preview = Cr3HevcPreviews.Find(file).Single(p => p.Name == "THMB");
        var heif = HeifBuilder.WrapHevcPicture(preview.HvcC, preview.NalUnits, (uint)preview.Width, (uint)preview.Height, preview.Colr, preview.Pixi);
        HeifImageDecoder.TryDecodeRgbaFloat(heif, out var rgba, out var w, out var h, out var encoding).ShouldBeTrue();
        (w, h).ShouldBe((320, 214));
        rgba.ShouldBe(HeifDecoder.Decode(heif).ToLinearRgbaFloat(out _));
        encoding.Float.ShouldBe(FloatSemantics.DisplayReferred);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Our codes against libheif's, which Magick stores as code &lt;&lt; (16 - bits) above 8 bits and code * 257 at 8.</summary>
    private void AssertMatchesLibheif(string what, byte[] heif, HeifRgbImage rgb, bool sameSize = false, int tolerance = 0, double maxShare = 0)
    {
        var (differing, max, first, total) = MeasureAgainstLibheif(what, heif, rgb, sameSize);
        output.WriteLine($"{what}: {rgb.Width}x{rgb.Height} {rgb.BitDepth}-bit, {differing} samples differ from libheif (max {max})");
        if (max > tolerance || differing > maxShare * total)
            Assert.Fail($"{what}: {differing} of {total} samples differ from libheif, by up to {max} (allowed {tolerance}, on up to {maxShare:P0}); the first at ({first % rgb.Width}, {first / rgb.Width}).");
    }

    /// <summary>
    /// How many of <paramref name="rgb"/>'s samples differ from libheif's decode of <paramref name="heif"/>, and
    /// by how much at most (shared with the phone-file tests, which hold what it measures to their own bounds).
    /// libheif's pixels are read a band of rows at a time, so a very large image is not held twice.
    /// </summary>
    internal static (long Differing, int Max, int First, long Total) MeasureAgainstLibheif(string what, byte[] heif, HeifRgbImage rgb, bool sameSize = false)
    {
        using var magick = new MagickImage(heif, MagickFormat.Heic);
        if (sameSize) ((int)magick.Width, (int)magick.Height).ShouldBe((rgb.Width, rgb.Height), $"{what}: libheif's size");
        magick.Width.ShouldBeGreaterThanOrEqualTo((uint)rgb.Width);
        magick.Height.ShouldBeGreaterThanOrEqualTo((uint)rgb.Height);
        var channels = (int)magick.ChannelCount;
        var stride = (int)magick.Width;
        long differing = 0;
        int first = -1, max = 0;
        const int band = 128;
        using var pixelsOf = magick.GetPixelsUnsafe();
        for (var y0 = 0; y0 < rgb.Height; y0 += band)
        {
            var rows = Math.Min(band, rgb.Height - y0);
            var pixels = pixelsOf.GetArea(0, y0, (int)magick.Width, rows)!;
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < rgb.Width; x++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        var value = (int)Math.Round(pixels[(y * stride + x) * channels + c]); // the Q16-HDRI build stores floats
                        var theirs = rgb.BitDepth == 8 ? value / 257 : value >> (16 - rgb.BitDepth);
                        var ours = rgb.Samples[3 * ((y0 + y) * rgb.Width + x) + c];
                        if (theirs == ours) continue;
                        differing++;
                        max = Math.Max(max, Math.Abs(theirs - ours));
                        if (first < 0) first = (y0 + y) * rgb.Width + x;
                    }
                }
            }
        }
        return (differing, max, first, (long)rgb.Width * rgb.Height * 3);
    }

    private static (byte[] Heif, int Width, int Height) Wrap(string fixture, byte[]? colr)
    {
        var (configuration, item) = HevcFixtureTests.Load(fixture);
        var picture = HevcDecoder.Decode(configuration, item);
        var window = picture.ConformanceWindow;
        int width = picture.Width - window.Left - window.Right, height = picture.Height - window.Top - window.Bottom;
        return (HeifBuilder.WrapHevcPicture(HvcC(configuration), item, (uint)width, (uint)height, colr, null), width, height);
    }

    internal static byte[] HvcC(HevcDecoderConfiguration c) =>
        HeifBuilder.HvcC((int)c.ChromaFormat, c.BitDepthLuma, c.BitDepthChroma, c.LengthSize, c.ProfileIdc, c.LevelIdc,
            [.. c.NalUnitArrays.Select(a => (a.NalUnitType, a.NalUnits.ToArray()))]);

    /// <summary>An item's property box body as the file has it, for re-wrapping an item into a file of its own.</summary>
    private static byte[]? RawProperty(byte[] file, HeifItem item, string type)
    {
        var property = item.Properties.Select(a => a.Property).FirstOrDefault(p => p.Type == type);
        return property switch
        {
            null => null,
            HevcDecoderConfiguration => HvcCBody(file, item),
            HeifNclxColour n => HeifBuilder.Nclx((int)n.Primaries, (int)n.Transfer, (int)n.Matrix, n.FullRange),
            HeifPixelInformation p => HeifBuilder.Pixi([.. p.BitsPerChannel]),
            _ => null,
        };
    }

    private static byte[] HvcCBody(byte[] file, HeifItem item)
    {
        var c = item.Property<HevcDecoderConfiguration>()!;
        return HvcC(c);
    }
}
