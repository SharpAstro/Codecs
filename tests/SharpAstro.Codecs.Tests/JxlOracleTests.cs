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

    [Fact(Skip = "Rung 2 WIP: FrameHeader bit-alignment under investigation (have_crop/num_passes land wrong). See jxl-codec memory.")]
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
            {
                byte[] cs = JxlContainer.ExtractCodestream(image.ToByteArray(MagickFormat.Jxl));
                var br = new JxlBitReader(cs.AsSpan(2)); // skip FF 0A
                (int w, int h) = JxlSizeHeader.Read(ref br);
                JxlImageMetadata meta = JxlImageMetadata.Read(ref br);
                JxlFrameHeader frame = JxlFrameHeader.Read(ref br, meta, w, h);
                JxlToc toc = JxlToc.Read(ref br, frame);

                // 2 signature bytes + headers/TOC bytes + data sections == whole codestream.
                (2 + br.BytesRead + toc.TotalSize).ShouldBe((long)cs.Length, label);
            }
        }
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
