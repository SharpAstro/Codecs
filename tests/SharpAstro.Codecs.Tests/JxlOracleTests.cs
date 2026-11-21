using ImageMagick;
using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// JPEG XL oracle — cross-checks our codestream parsing against Magick.NET (ImageMagick
/// Q16-HDRI, which links libjxl). The ISO/IEC 18181 spec is the judge of correctness;
/// Magick.NET is the empirical oracle.
///   Rung 0: container + SizeHeader dimensions.
///   Rung 1: ImageMetadata (bit depth, alpha, colour space).
///   Rung 2: FrameHeader + TOC, validated by byte accounting.
/// </summary>
public sealed class JxlOracleTests
{
    [Theory]
    [InlineData(240, 160)] // 3:2 -> aspect-ratio shortcut + div8 path
    [InlineData(64, 64)]   // 1:1 ratio + div8
    [InlineData(256, 256)] // div8 upper edge (8 * 32)
    [InlineData(13, 7)]    // arbitrary -> div8=0, explicit U32 for both dims
    [InlineData(100, 60)]  // arbitrary
    [InlineData(1, 1)]     // minimum
    [InlineData(1000, 1)]  // extreme aspect, explicit
    [InlineData(101, 97)]  // no matching fixed ratio
    public void MagickEncoded_Jxl_DimensionsMatch(int w, int h)
    {
        using var m = new MagickImage(MagickColors.SteelBlue, (uint)w, (uint)h);
        byte[] bytes = m.ToByteArray(MagickFormat.Jxl);

        JxlImageInfo info = JxlFile.ReadInfo(bytes);

        info.Width.ShouldBe(w);
        info.Height.ShouldBe(h);
    }

    /// <summary>
    /// extra_fields (#103): libjxl writes the orientation into ImageMetadata, ReadInfo reports it with the
    /// stored size, and Decode returns the image turned as Magick's AutoOrient turns libjxl's own decode.
    /// </summary>
    [Theory]
    [InlineData(OrientationType.TopRight, 2)]
    [InlineData(OrientationType.BottomRight, 3)]
    [InlineData(OrientationType.BottomLeft, 4)]
    [InlineData(OrientationType.LeftTop, 5)]
    [InlineData(OrientationType.RightTop, 6)]
    [InlineData(OrientationType.RightBottom, 7)]
    [InlineData(OrientationType.LeftBottom, 8)]
    public void MagickEncoded_Jxl_Orientation_IsReadAndApplied(OrientationType orientation, int code)
    {
        using var m = new MagickImage(MagickColors.Black, 3, 2) { Depth = 8, Quality = 100, Orientation = orientation };
        using (var pixels = m.GetPixels())
        {
            for (var y = 0; y < 2; y++)
                for (var x = 0; x < 3; x++)
                    pixels.SetPixel(x, y, [(x * 40 + y * 100) * 257f, x * 7 * 257f, y * 9 * 257f]);
        }
        byte[] bytes = m.ToByteArray(MagickFormat.Jxl);

        JxlImageInfo info = JxlFile.ReadInfo(bytes);
        (info.Width, info.Height, info.Orientation).ShouldBe((3, 2, code));

        JxlImage ours = JxlImageCodec.Decode(bytes);
        using var reference = new MagickImage(bytes);
        reference.AutoOrient();
        (ours.Width, ours.Height).ShouldBe(((int)reference.Width, (int)reference.Height));
        var expected = reference.GetPixels().ToShortArray(0, 0, reference.Width, reference.Height, "RGB");
        expected.ShouldNotBeNull();
        for (var p = 0; p < ours.Width * ours.Height; p++)
            for (var c = 0; c < 3; c++)
                ours.Channels[c][p].ShouldBe(expected[3 * p + c] * ((1 << ours.BitsPerSample) - 1) / 65535, $"pixel {p} channel {c}");
    }

    /// <summary>
    /// The default ImageMetadata (no extra_fields) reports orientation 1 and the default intensity target.
    /// </summary>
    [Fact]
    public void MagickEncoded_Jxl_WithoutExtraFields_HasTheDefaults()
    {
        using var m = new MagickImage(MagickColors.SteelBlue, 8, 8);
        JxlImageInfo info = JxlFile.ReadInfo(m.ToByteArray(MagickFormat.Jxl));
        (info.Orientation, info.IntensityTarget).ShouldBe((1, 255f));
    }

    [Fact]
    public void MagickEncoded_Jxl_MetadataMatches()
    {
        // Magick Q16-HDRI encodes every sample at 16-bit, always sRGB.
        AssertMeta(BuildRgb(), bits: 16, alpha: false, gray: false, "rgb-lossless");
        AssertMeta(BuildRgba(), bits: 16, alpha: true, gray: false, "rgba-lossless");
        AssertMeta(BuildGray(), bits: 16, alpha: false, gray: true, "gray-lossless");
        AssertMeta(BuildRgbLossy(), bits: 16, alpha: false, gray: false, "rgb-lossy");

        static void AssertMeta(MagickImage image, int bits, bool alpha, bool gray, string label)
        {
            using (image)
            {
                JxlImageInfo info = JxlFile.ReadInfo(image.ToByteArray(MagickFormat.Jxl));
                info.BitsPerSample.ShouldBe(bits, label);
                info.HasAlpha.ShouldBe(alpha, label);
                info.IsGrayscale.ShouldBe(gray, label);
            }
        }
    }

    [Fact]
    public void MagickEncoded_Jxl_FrameHeaderAndToc_ByteAccountingConsistent()
    {
        // FrameHeader exposes no field Magick reports back, so we validate structurally:
        // the TOC's declared section sizes plus the byte offset where data begins must
        // exactly account for the whole codestream. Any misaligned parse breaks this.
        Check(BuildRgb(), "rgb-lossless");
        Check(BuildRgba(), "rgba-lossless");
        Check(BuildGray(), "gray-lossless");
        Check(BuildRgbLossy(), "rgb-lossy");

        static void Check(MagickImage image, string label)
        {
            using (image)
                CheckByteAccounting(image.ToByteArray(MagickFormat.Jxl), label);
        }
    }

    // The single-group boundary lies just past 256 px (VarDCT group_dim) — small images carry
    // one TOC entry; once the frame spills into multiple groups the TOC grows to
    // 1 (LfGlobal) + numLfGroups + 1 (HfGlobal) + numGroups*numPasses entries, exercising the
    // permutation/section-size loop. Sweeping both sides of that boundary, for VarDCT (lossy)
    // and Modular (lossless), guards the FrameHeader+TOC parse against multi-group misalignment.
    [Theory]
    [InlineData(1, 1, false)]        // minimal, single group
    [InlineData(256, 256, false)]    // largest single-group VarDCT (group_dim 256)
    [InlineData(257, 200, false)]    // first spill -> 2 groups
    [InlineData(512, 384, false)]    // 4 groups
    [InlineData(1000, 700, false)]   // 12 groups, 15 TOC entries
    [InlineData(1, 1, true)]         // minimal Modular
    [InlineData(300, 200, true)]     // single Modular group
    [InlineData(512, 384, true)]     // 4 Modular groups
    [InlineData(1000, 700, true)]    // 12 Modular groups
    public void MagickEncoded_Jxl_ByteAccounting_AcrossGroupBoundary(int w, int h, bool lossless)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)w, (uint)h);
        if (lossless)
            image.Quality = 100;
        CheckByteAccounting(image.ToByteArray(MagickFormat.Jxl), $"{w}x{h} {(lossless ? "lossless" : "lossy")}");
    }

    internal static JxlToc CheckByteAccounting(byte[] bytes, string label)
    {
        byte[] cs = JxlContainer.ExtractCodestream(bytes);
        var br = new JxlBitReader(cs.AsSpan(2)); // skip FF 0A
        (int w, int h) = JxlSizeHeader.Read(ref br);
        JxlImageMetadata meta = JxlImageMetadata.Read(ref br);
        JxlFrameHeader frame = JxlFrameHeader.Read(ref br, meta, w, h);
        JxlToc toc = JxlToc.Read(ref br, frame);

        // TOC entry count must follow the section layout implied by the group geometry.
        long expectedEntries = frame.NumGroups == 1 && frame.NumPasses == 1
            ? 1
            : 1 + frame.NumLfGroups + 1 + (long)frame.NumGroups * frame.NumPasses;
        ((long)toc.EntryCount).ShouldBe(expectedEntries, label);

        // 2 signature bytes + headers/TOC bytes + data sections == whole codestream.
        (2 + br.BytesRead + toc.TotalSize).ShouldBe((long)cs.Length, label);
        return toc;
    }

    private static MagickImage BuildRgb()
    {
        var m = new MagickImage(MagickColors.SteelBlue, 32, 24);
        m.Quality = 100;
        return m;
    }

    private static MagickImage BuildRgba()
    {
        MagickImage m = BuildRgb();
        m.Alpha(AlphaOption.Opaque);
        return m;
    }

    private static MagickImage BuildGray()
    {
        MagickImage m = BuildRgb();
        m.ColorSpace = ColorSpace.Gray;
        return m;
    }

    private static MagickImage BuildRgbLossy() => new(MagickColors.SteelBlue, 32, 24);

    /// <summary>
    /// libjxl's lossy output decodes to libjxl's own pixels: Magick.NET encodes a synthetic image (smooth ramps for
    /// the large transforms, sharp edges and a fine checker for the small ones, noise for everything between) at a
    /// range of sizes and qualities, and every sample of our decode is compared with Magick's decode of the same
    /// bytes. At 16 bits no sample may differ by more than one code (measured: at most one, in about 1.5% of the
    /// samples, the rounding of float noise; mean error under 0.02 codes). The sizes cover a single 8x8 block, sizes
    /// that are not whole blocks, several PassGroups, and two LF groups; the low qualities make cjxl pick the large
    /// DCTs, the high ones the small transforms.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 90)]
    [InlineData(7, 5, 90)]
    [InlineData(17, 3, 80)]
    [InlineData(256, 256, 90)]
    [InlineData(333, 217, 50)]
    [InlineData(600, 400, 75)]
    [InlineData(640, 480, 10)]
    [InlineData(640, 480, 30)]
    [InlineData(512, 512, 99)]
    [InlineData(1000, 700, 90)]
    [InlineData(2100, 300, 85)]
    public void MagickEncodedVarDct_16Bit_MatchesLibjxl(int w, int h, int quality)
    {
        using MagickImage source = Synthetic(w, h, seed: w * 31 + h, depth: 16, grey: false);
        source.Quality = (uint)quality;
        byte[] bytes = source.ToByteArray(MagickFormat.Jxl);

        JxlImage ours = JxlImageCodec.Decode(bytes);
        (ours.Width, ours.Height, ours.BitsPerSample, ours.ColorChannels).ShouldBe((w, h, 16, 3));
        CompareWithLibjxl(ours, bytes, maxCodeDifference: 1, $"{w}x{h} q{quality}");
    }

    /// <summary>
    /// An 8-bit source gives an 8-bit image. libjxl dithers when it writes 8-bit samples (here about a quarter of the
    /// samples land one code up or down, as many each way), while this decoder rounds, so the samples agree to one
    /// code; the 16-bit cases above show the values before quantisation agree.
    /// </summary>
    [Theory]
    [InlineData(300, 200, 85, false)]
    [InlineData(300, 300, 70, true)]
    public void MagickEncodedVarDct_8Bit_MatchesLibjxlToOneCode(int w, int h, int quality, bool grey)
    {
        using MagickImage source = Synthetic(w, h, seed: 7, depth: 8, grey);
        source.Quality = (uint)quality;
        byte[] bytes = source.ToByteArray(MagickFormat.Jxl);

        JxlImage ours = JxlImageCodec.Decode(bytes);
        (ours.Width, ours.Height, ours.BitsPerSample, ours.ColorChannels).ShouldBe((w, h, 8, grey ? 1 : 3));
        CompareWithLibjxl(ours, bytes, maxCodeDifference: 1, $"{w}x{h} q{quality} {(grey ? "grey" : "rgb")}");
    }

    /// <summary>The facade decodes libjxl's lossy output at its bit depth and reports its colour (sRGB here).</summary>
    [Fact]
    public void MagickEncodedVarDct_ThroughTheFacade_KeepsDepthAndColour()
    {
        using MagickImage source = Synthetic(96, 64, seed: 3, depth: 16, grey: false);
        source.Quality = 90;
        byte[] bytes = source.ToByteArray(MagickFormat.Jxl);

        ImageCodecs.TryDecode(bytes, out var image).ShouldBeTrue();
        image.ShouldNotBeNull();
        (image.Width, image.Height, image.Channels, image.SampleFormat).ShouldBe((96, 64, 3, SharpAstro.Codecs.Abstractions.SampleFormat.UInt16));
        image.ColorEncoding.ShouldBe(SharpAstro.Codecs.Abstractions.ColorEncoding.AssumedSrgb);
    }

    private static void CompareWithLibjxl(JxlImage ours, byte[] bytes, int maxCodeDifference, string label)
    {
        using var reference = new MagickImage(bytes);
        var expected = reference.GetPixels().ToShortArray(0, 0, reference.Width, reference.Height, "RGB");
        expected.ShouldNotBeNull();
        int max = (1 << ours.BitsPerSample) - 1;
        int worst = 0;
        double sumAbs = 0;
        long n = 0;
        for (var p = 0; p < ours.Width * ours.Height; p++)
            for (var c = 0; c < 3; c++)
            {
                int ourCode = ours.Channels[Math.Min(c, ours.ColorChannels - 1)][p];
                int refCode = (int)Math.Round(expected[3 * p + c] * (double)max / 65535);
                int d = Math.Abs(ourCode - refCode);
                worst = Math.Max(worst, d);
                sumAbs += d;
                n++;
            }
        TestContext.Current.TestOutputHelper?.WriteLine($"{label}: worst {worst} codes, mean {sumAbs / n:0.0000} codes");
        worst.ShouldBeLessThanOrEqualTo(maxCodeDifference, label);
    }

    /// <summary>A deterministic test picture: ramps, a sine pattern, hard-edged bars, a fine checker and noise.</summary>
    private static MagickImage Synthetic(int w, int h, int seed, uint depth, bool grey)
    {
        var rng = new Random(seed);
        var values = new float[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                double u = x / (double)Math.Max(1, w - 1), v = y / (double)Math.Max(1, h - 1);
                double smooth = 0.5 + 0.35 * Math.Sin(5 * u + 3 * v);
                double bars = (x / 23 + y / 17) % 2 == 0 ? 0.15 : -0.15;
                double checker = x < w / 3 && y < h / 3 ? (((x ^ y) & 1) == 0 ? 0.1 : -0.1) : 0;
                for (var c = 0; c < 3; c++)
                {
                    double tint = grey ? 0 : 0.1 * Math.Cos(4 * u * (c + 1) + 2 * v);
                    double noise = (rng.NextDouble() - 0.5) * 0.04;
                    double value = Math.Clamp(smooth + bars + checker + tint + noise, 0, 1);
                    values[(y * w + x) * 3 + c] = (float)(value * 65535);
                }
                if (grey)
                    values[(y * w + x) * 3 + 1] = values[(y * w + x) * 3 + 2] = values[(y * w + x) * 3];
            }
        var image = new MagickImage(MagickColors.Black, (uint)w, (uint)h) { Depth = depth };
        using (var pixels = image.GetPixels())
            pixels.SetArea(0, 0, (uint)w, (uint)h, values);
        if (grey)
            image.ColorSpace = ColorSpace.Gray;
        return image;
    }
}
