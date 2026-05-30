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

    private static void CheckByteAccounting(byte[] bytes, string label)
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
}
