using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8a — BD8 grayscale (Y-only). Self round-trip through the full façade
/// (EncodeGray8 → container → DecodeGray8): a lossless encode/decode must come
/// back pixel-identical, exercising the Y-only DC / LP-CBP / HP-CBP entropy
/// branches, the 1-channel signal path (no colour transform), and the YONLY
/// codestream headers. (Self round-trip proves enc↔dec symmetry; the oracle
/// tests prove conformance with jxrlib.)
/// </summary>
public sealed class JxrGrayscaleTests
{
    [Theory]
    [InlineData(16, 16, "flat")]
    [InlineData(16, 16, "gradient")]
    [InlineData(32, 16, "gradient")]
    [InlineData(48, 32, "gradient")]
    [InlineData(64, 48, "gradient")]
    [InlineData(64, 48, "random")]
    [InlineData(96, 64, "random")]
    [InlineData(80, 80, "gradient")]
    [InlineData(272, 16, "gradient")] // crosses a 16-MB group boundary
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient")]
    [InlineData(33, 40, "random")]
    [InlineData(100, 60, "gradient")]
    public void EncodeGray8_DecodeGray8_RoundTripsLossless(int w, int h, string kind)
    {
        var y = Pattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGray8(y, w, h);

        var (dw, dh, dy) = JxrImageCodec.DecodeGray8(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
            dy[i].ShouldBe(y[i], $"Y[{i}] ({kind} {w}x{h})");
    }

    [Theory]
    [InlineData(16, 16, 1)]
    [InlineData(48, 32, 1)]
    [InlineData(80, 80, 1)]
    [InlineData(48, 32, 2)]
    [InlineData(80, 80, 2)]
    public void EncodeGray8_Overlap_RoundTripsLossless(int w, int h, int overlap)
    {
        var y = Pattern(w, h, "gradient");
        var jxr = JxrImageCodec.EncodeGray8(y, w, h, overlap: overlap);

        var (dw, dh, dy) = JxrImageCodec.DecodeGray8(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
            dy[i].ShouldBe(y[i], $"Y[{i}] (OL{overlap} {w}x{h})");
    }

    /// <summary>The container must round-trip the grayscale pixel-format GUID.</summary>
    [Fact]
    public void EncodeGray8_TagsAsGray8Bpp()
    {
        var y = Pattern(48, 32, "gradient");
        var jxr = JxrImageCodec.EncodeGray8(y, 48, 32);
        var file = JxrContainer.Read(jxr);
        file.PixelFormat.ShouldBe(JxrPixelFormat.Gray8Bpp);
        file.Width.ShouldBe(48u);
        file.Height.ShouldBe(32u);
    }

    // ----------------------------------------------------------------- helpers

    internal static int[] Pattern(int w, int h, string kind)
    {
        var y = new int[w * h];
        switch (kind)
        {
            case "flat":
                Array.Fill(y, 137);
                break;
            case "random":
                var rng = new Random(0x6A1 + w * 31 + h);
                for (var i = 0; i < y.Length; i++) y[i] = rng.Next(256);
                break;
            default: // gradient
                for (var yy = 0; yy < h; yy++)
                    for (var xx = 0; xx < w; xx++)
                        y[yy * w + xx] = (xx * 2 + yy * 3) & 0xff;
                break;
        }
        return y;
    }
}
