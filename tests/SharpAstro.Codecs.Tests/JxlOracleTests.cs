using ImageMagick;
using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// JPEG XL oracle — cross-checks our codestream parsing against Magick.NET (ImageMagick
/// Q16-HDRI, which links libjxl). The ISO/IEC 18181 spec is the judge of correctness;
/// Magick.NET is the empirical oracle. Rung 0: container unwrap + SizeHeader dimensions.
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
}
