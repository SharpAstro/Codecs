using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8b — BD16 integer (16-bit unsigned) grayscale + RGB. Self round-trip through
/// the façade: a lossless encode/decode must come back sample-identical across the full
/// 0..65535 range. BD16 reuses the validated BD8 pipeline unchanged except for the luma
/// level bias (32768 vs 128), the 8-bit SHIFT_BITS plane-header field (0 = full precision),
/// and OutputBitDepth=BD16. (Self round-trip proves enc↔dec symmetry; the oracle tests
/// prove conformance with jxrlib.)
/// </summary>
public sealed class JxrBd16Tests
{
    [Theory]
    [InlineData(16, 16, "flat")]
    [InlineData(16, 16, "gradient")]
    [InlineData(48, 32, "gradient")]
    [InlineData(64, 48, "random")]
    [InlineData(80, 80, "gradient")]
    [InlineData(272, 16, "gradient")]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient")]
    [InlineData(33, 40, "random")]
    public void EncodeGray16_DecodeGray16_RoundTripsLossless(int w, int h, string kind)
    {
        var y = Pattern(w, h, kind, 7);
        var jxr = JxrImageCodec.EncodeGray16(y, w, h);

        var (dw, dh, dy) = JxrImageCodec.DecodeGray16(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
            dy[i].ShouldBe(y[i], $"Y[{i}] ({kind} {w}x{h})");
    }

    [Theory]
    [InlineData(48, 32, 1)]
    [InlineData(80, 80, 2)]
    public void EncodeGray16_Overlap_RoundTripsLossless(int w, int h, int overlap)
    {
        var y = Pattern(w, h, "gradient", 7);
        var jxr = JxrImageCodec.EncodeGray16(y, w, h, overlap: overlap);

        var (dw, dh, dy) = JxrImageCodec.DecodeGray16(jxr);
        for (var i = 0; i < w * h; i++)
            dy[i].ShouldBe(y[i], $"Y[{i}] (OL{overlap} {w}x{h})");
    }

    [Theory]
    [InlineData(16, 16, "gradient")]
    [InlineData(48, 32, "gradient")]
    [InlineData(64, 48, "random")]
    [InlineData(80, 80, "gradient")]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient")]
    [InlineData(33, 40, "random")]
    public void EncodeRgb48_DecodeRgb48_RoundTripsLossless(int w, int h, string kind)
    {
        var r = Pattern(w, h, kind, 1);
        var g = Pattern(w, h, kind, 2);
        var b = Pattern(w, h, kind, 3);
        var jxr = JxrImageCodec.EncodeRgb48(r, g, b, w, h);

        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb48(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
        {
            dr[i].ShouldBe(r[i], $"R[{i}] ({kind} {w}x{h})");
            dg[i].ShouldBe(g[i], $"G[{i}] ({kind} {w}x{h})");
            db[i].ShouldBe(b[i], $"B[{i}] ({kind} {w}x{h})");
        }
    }

    [Theory]
    [InlineData(48, 32, 1)]
    [InlineData(64, 48, 2)]
    public void EncodeRgb48_Overlap_RoundTripsLossless(int w, int h, int overlap)
    {
        var r = Pattern(w, h, "gradient", 1);
        var g = Pattern(w, h, "random", 2);
        var b = Pattern(w, h, "gradient", 3);
        var jxr = JxrImageCodec.EncodeRgb48(r, g, b, w, h, overlap: overlap);

        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb48(jxr);
        for (var i = 0; i < w * h; i++)
        {
            dr[i].ShouldBe(r[i], $"R[{i}] (OL{overlap} {w}x{h})");
            dg[i].ShouldBe(g[i], $"G[{i}] (OL{overlap} {w}x{h})");
            db[i].ShouldBe(b[i], $"B[{i}] (OL{overlap} {w}x{h})");
        }
    }

    [Fact]
    public void EncodeGray16_TagsAsGray16Bpp()
    {
        var jxr = JxrImageCodec.EncodeGray16(Pattern(48, 32, "gradient", 7), 48, 32);
        JxrContainer.Read(jxr).PixelFormat.ShouldBe(JxrPixelFormat.Gray16Bpp);
    }

    [Fact]
    public void EncodeRgb48_TagsAsRgb48Bpp()
    {
        var jxr = JxrImageCodec.EncodeRgb48(Pattern(48, 32, "gradient", 1), Pattern(48, 32, "gradient", 2),
                                            Pattern(48, 32, "gradient", 3), 48, 32);
        JxrContainer.Read(jxr).PixelFormat.ShouldBe(JxrPixelFormat.Rgb48Bpp);
    }

    // 16-bit patterns spanning the full 0..65535 range.
    internal static int[] Pattern(int w, int h, string kind, int salt)
    {
        var y = new int[w * h];
        switch (kind)
        {
            case "flat":
                Array.Fill(y, 40000 + salt * 137);
                break;
            case "random":
                var rng = new Random(0x16B + salt * 101 + w * 31 + h);
                for (var i = 0; i < y.Length; i++) y[i] = rng.Next(65536);
                break;
            default: // gradient — scale 8-bit ramp up into 16-bit
                for (var yy = 0; yy < h; yy++)
                    for (var xx = 0; xx < w; xx++)
                        y[yy * w + xx] = (((xx * 2 + yy * 3 + salt * 17) & 0xff) << 8) | ((xx + yy) & 0xff);
                break;
        }
        return y;
    }
}
