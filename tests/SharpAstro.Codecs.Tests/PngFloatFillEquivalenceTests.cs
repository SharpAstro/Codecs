using SharpAstro.Codecs.Abstractions;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="PngFloatFill.Fill"/> (the table fast path) against <see cref="PngFloatFill.FillReference"/> (the
/// scalar row loop it replaced, kept as the specification), compared <b>bit for bit</b>: a lookup table is built
/// with the reference's own arithmetic and the HLG OOTF is the same call, so there is no tolerance to state.
/// <para>
/// Because the dispatch picks one path by image size, a machine only ever runs one of them for a given image, so
/// these tests drive both directly over every colour type and depth, every transfer the projection supports, both
/// ranges, and sizes either side of the table break-even (samples &lt; table length falls back to the reference).
/// The samples carry the rails (0, the maximum, and the narrow-range foot/head room edges), where a clamp or a
/// range expansion is most likely to differ.
/// </para>
/// </summary>
public sealed class PngFloatFillEquivalenceTests
{
    private static readonly string FixturesRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cicp");

    private static PngImage Image(int w, int h, int bitDepth, int colorType, int seed, byte[]? palette = null, byte[]? paletteAlpha = null)
    {
        int spp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new ArgumentOutOfRangeException(nameof(colorType)) };
        int bytesPerSample = bitDepth == 16 ? 2 : 1;
        var pixels = new byte[w * h * spp * bytesPerSample];
        var rnd = new Random(seed);
        rnd.NextBytes(pixels);

        // Rails: plant the codes where range expansion and clamping change behaviour.
        ushort[] rails16 = [0, 1, 4095, 4096, 4097, 32768, 60159, 60160, 60161, 65534, 65535];
        byte[] rails8 = [0, 1, 15, 16, 17, 128, 234, 235, 236, 254, 255];
        for (int i = 0, k = 0; i < w * h * spp && k < 400; i += 1 + rnd.Next(7), k++)
        {
            if (bitDepth == 16)
            {
                var v = rails16[rnd.Next(rails16.Length)];
                pixels[i * 2] = (byte)(v >> 8);
                pixels[i * 2 + 1] = (byte)v;
            }
            else
            {
                pixels[i] = rails8[rnd.Next(rails8.Length)];
            }
        }

        if (colorType == 3)
        {
            int entries = palette is null ? 0 : palette.Length / 3;
            for (int i = 0; i < pixels.Length && entries > 0; i++)
                pixels[i] = (byte)(pixels[i] % entries);
        }

        return new PngImage
        {
            Width = w,
            Height = h,
            BitDepth = bitDepth,
            ColorType = colorType,
            Pixels = pixels,
            Palette = palette,
            PaletteAlpha = paletteAlpha,
        };
    }

    private static void ShouldBeIdentical(PngImage png, ColorEncoding enc, string what)
    {
        var expected = new float[png.Width * png.Height * 4];
        var actual = new float[expected.Length];
        // Poison the fast path's destination so a pixel it never writes cannot pass by matching zero.
        Array.Fill(actual, float.NaN);

        PngFloatFill.FillReference(png, enc, expected);
        PngFloatFill.Fill(png, enc, actual);

        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                throw new ShouldAssertException($"{what}: float {i} (pixel {i / 4}, channel {i % 4}) is {actual[i]:R} ({BitConverter.SingleToInt32Bits(actual[i]):X8}), reference {expected[i]:R} ({BitConverter.SingleToInt32Bits(expected[i]):X8})");
        }
    }

    public static TheoryData<TransferFunction, ColorPrimaries> Curves => new()
    {
        { TransferFunction.Pq, ColorPrimaries.BT2020 },
        { TransferFunction.Hlg, ColorPrimaries.BT2020 },
        { TransferFunction.Hlg, ColorPrimaries.BT709 },
        { TransferFunction.Hlg, ColorPrimaries.DisplayP3 },
        { TransferFunction.Srgb, ColorPrimaries.BT709 },
        { TransferFunction.BT709, ColorPrimaries.BT709 },
        { TransferFunction.BT2020_10, ColorPrimaries.BT2020 },
        { TransferFunction.Linear, ColorPrimaries.BT709 },
    };

    // ---- 8-bit and 16-bit, every colour type, both ranges -------------------------------------

    [Theory]
    [MemberData(nameof(Curves))]
    public void Table8Bit_EqualsReference_EveryColorType_BothRanges(TransferFunction transfer, ColorPrimaries primaries)
    {
        foreach (var fullRange in new[] { true, false })
        foreach (var colorType in new[] { 0, 2, 4, 6 })
        {
            var enc = new ColorEncoding { Transfer = transfer, Primaries = primaries, FullRange = fullRange, Float = FloatSemantics.DisplayReferred };
            // 61x37 is well above the 256-sample break-even for every colour type.
            ShouldBeIdentical(Image(61, 37, 8, colorType, seed: 100 + colorType), enc, $"8-bit type {colorType} {transfer}/{primaries} fullRange={fullRange}");
        }
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void Table16Bit_EqualsReference_EveryColorType_BothRanges(TransferFunction transfer, ColorPrimaries primaries)
    {
        foreach (var fullRange in new[] { true, false })
        foreach (var colorType in new[] { 0, 2, 4, 6 })
        {
            var enc = new ColorEncoding { Transfer = transfer, Primaries = primaries, FullRange = fullRange, Float = FloatSemantics.DisplayReferred };
            // 260x260 is 67 600 pixels: above the 65 536-sample break-even even for grey.
            ShouldBeIdentical(Image(260, 260, 16, colorType, seed: 200 + colorType), enc, $"16-bit type {colorType} {transfer}/{primaries} fullRange={fullRange}");
        }
    }

    // ---- the break-even: both sides of it, and the exact boundary ------------------------------

    [Theory]
    [InlineData(8, 0, 15, 17)]    // 255 samples: reference path
    [InlineData(8, 0, 16, 16)]    // 256 samples: first size on the table path
    [InlineData(8, 2, 9, 9)]      // 243 samples: reference
    [InlineData(8, 2, 10, 9)]     // 270 samples: table
    [InlineData(8, 6, 1, 1)]      // one pixel
    [InlineData(8, 4, 300, 1)]    // one row
    [InlineData(8, 6, 1, 300)]    // one column
    [InlineData(16, 0, 255, 257)] // 65 535 samples: reference
    [InlineData(16, 0, 256, 256)] // 65 536 samples: first size on the table path
    [InlineData(16, 2, 148, 148)] // 65 712 samples (RGB): table
    [InlineData(16, 6, 3, 2)]     // tiny
    public void BothSidesOfTheBreakEven_EqualReference(int bitDepth, int colorType, int w, int h)
    {
        foreach (var (transfer, primaries) in new[] { (TransferFunction.Pq, ColorPrimaries.BT2020), (TransferFunction.Hlg, ColorPrimaries.BT2020), (TransferFunction.Srgb, ColorPrimaries.BT709) })
        foreach (var fullRange in new[] { true, false })
        {
            var enc = new ColorEncoding { Transfer = transfer, Primaries = primaries, FullRange = fullRange, Float = FloatSemantics.DisplayReferred };
            ShouldBeIdentical(Image(w, h, bitDepth, colorType, seed: w * 31 + h), enc, $"{bitDepth}-bit type {colorType} {w}x{h} {transfer} fullRange={fullRange}");
        }
    }

    // ---- indexed ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1, 1, false)]
    [InlineData(4, 3, 2, true)]
    [InlineData(37, 29, 16, true)]
    [InlineData(64, 64, 256, true)]
    [InlineData(64, 64, 256, false)]
    public void Indexed_EqualsReference(int w, int h, int entries, bool shortTrns)
    {
        var rnd = new Random(entries);
        var palette = new byte[entries * 3];
        rnd.NextBytes(palette);
        // tRNS may be shorter than the palette: the tail is opaque.
        var trns = new byte[shortTrns ? Math.Max(1, entries / 2) : entries];
        rnd.NextBytes(trns);

        foreach (var (transfer, primaries) in new[] { (TransferFunction.Pq, ColorPrimaries.BT2020), (TransferFunction.Hlg, ColorPrimaries.BT2020), (TransferFunction.Srgb, ColorPrimaries.BT709), (TransferFunction.Linear, ColorPrimaries.BT709) })
        foreach (var fullRange in new[] { true, false })
        {
            var enc = new ColorEncoding { Transfer = transfer, Primaries = primaries, FullRange = fullRange, Float = FloatSemantics.DisplayReferred };
            ShouldBeIdentical(Image(w, h, 8, 3, seed: w + h, palette, trns), enc, $"indexed {w}x{h} entries={entries} {transfer} fullRange={fullRange}");
            ShouldBeIdentical(Image(w, h, 8, 3, seed: w + h, palette, paletteAlpha: null), enc, $"indexed (no tRNS) {w}x{h} entries={entries} {transfer} fullRange={fullRange}");
        }
    }

    [Fact]
    public void Indexed_RefusesWhatTheReferenceRefuses()
    {
        var enc = new ColorEncoding { Transfer = TransferFunction.Pq, Primaries = ColorPrimaries.BT2020, Float = FloatSemantics.DisplayReferred };
        var noPalette = Image(4, 4, 8, 3, seed: 1);
        var dst = new float[4 * 4 * 4];

        Should.Throw<InvalidDataException>(() => PngFloatFill.FillReference(noPalette, enc, dst));
        Should.Throw<InvalidDataException>(() => PngFloatFill.Fill(noPalette, enc, dst));

        // An index past the palette's end.
        byte[] palette = [0, 0, 0, 255, 255, 255];
        var bad = Image(4, 4, 8, 3, seed: 2, palette);
        bad.Pixels[9] = 2;
        var refMessage = Should.Throw<InvalidDataException>(() => PngFloatFill.FillReference(bad, enc, dst)).Message;
        var fastMessage = Should.Throw<InvalidDataException>(() => PngFloatFill.Fill(bad, enc, dst)).Message;
        fastMessage.ShouldBe(refMessage);
    }

    // ---- the real files ------------------------------------------------------------------------

    [Theory]
    [InlineData("PNG-HLG-FancyColorBars-16bit-cICP-FR.png")]
    [InlineData("PNG-HLG-FancyColorBars-16bit-cICP-NR.png")]
    [InlineData("PNG-PQ-BT.2111-ColorBars-16bit-cICP-FR.png")]
    [InlineData("PNG-PQ-BT.2111-ColorBars-16bit-cICP-mDCV-max1Knit-min.0005nit-cLLI-FR.png")]
    [InlineData("PNG-SDR-BT.709-ColorBars-Tent-Valley-Grayscale-16bit-cICP-FR.png")]
    [InlineData("PNG-SDR-BT.709-ColorBars-Tent-Valley-Grayscale-16bit-cICP-NR.png")]
    [InlineData("SDR-Color-Bars-BT.709-16bit-1080-FR-BT.2020-12bitTest.png")]
    public void ConformanceFiles_FastEqualsReference(string name)
    {
        var path = Path.Combine(FixturesRoot, name);
        File.Exists(path).ShouldBeTrue($"fixture missing: {path}");
        var png = PngReader.Decode(File.ReadAllBytes(path));
        var enc = PngImageDecoder.MapColorEncoding(png);

        var source = enc with { Float = FloatSemantics.DisplayReferred };
        ShouldBeIdentical(png, source, name);
    }

    // ---- the table builders themselves ---------------------------------------------------------

    [Theory]
    [InlineData(8, true)]
    [InlineData(8, false)]
    [InlineData(12, true)]
    [InlineData(16, true)]
    [InlineData(16, false)]
    public void LinearTable_IsTheScalarCurveOfEveryCode(int bitDepth, bool fullRange)
    {
        foreach (var transfer in new[] { TransferFunction.Pq, TransferFunction.Hlg, TransferFunction.Srgb, TransferFunction.BT709, TransferFunction.Linear })
        {
            var table = new float[1 << bitDepth];
            PngHdr.BuildLinearTable(transfer, ColorPrimaries.BT2020, bitDepth, fullRange, table);
            for (int c = 0; c < table.Length; c++)
            {
                Span<float> one = [PngHdr.CodeToSignal(c, bitDepth, fullRange)];
                PngHdr.ToLinear(transfer, ColorPrimaries.BT2020, one, 1, 1);
                BitConverter.SingleToInt32Bits(table[c]).ShouldBe(BitConverter.SingleToInt32Bits(one[0]), $"{transfer} code {c}");
            }
        }
    }

    [Fact]
    public void TableBuilders_RejectAWrongLengthTable()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PngHdr.BuildLinearTable(TransferFunction.Pq, ColorPrimaries.BT2020, 8, true, new float[255]));
        Should.Throw<ArgumentOutOfRangeException>(() => PngHdr.BuildHlgSceneLightTable(8, true, new float[257]));
        Should.Throw<NotSupportedException>(() => PngHdr.BuildLinearTable(TransferFunction.Log100, ColorPrimaries.BT709, 8, true, new float[256]));
    }

    [Fact]
    public void HlgSceneToLinear_IsToLinearForARgbPixel()
    {
        var weights = LumaWeights.For(ColorPrimaries.BT2020);
        var rnd = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            Span<float> px = [rnd.NextSingle(), rnd.NextSingle(), rnd.NextSingle()];
            float er = PngHdr.HlgInverseOetf(px[0]), eg = PngHdr.HlgInverseOetf(px[1]), eb = PngHdr.HlgInverseOetf(px[2]);
            PngHdr.HlgSceneToLinear(er, eg, eb, weights, out var r, out var g, out var b);
            PngHdr.ToLinear(TransferFunction.Hlg, ColorPrimaries.BT2020, px, 3, 3);
            BitConverter.SingleToInt32Bits(r).ShouldBe(BitConverter.SingleToInt32Bits(px[0]));
            BitConverter.SingleToInt32Bits(g).ShouldBe(BitConverter.SingleToInt32Bits(px[1]));
            BitConverter.SingleToInt32Bits(b).ShouldBe(BitConverter.SingleToInt32Bits(px[2]));
        }
    }
}
