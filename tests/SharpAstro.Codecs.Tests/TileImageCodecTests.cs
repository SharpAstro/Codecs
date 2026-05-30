using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 7c — the full OL_NONE codec. Round-trips whole BD8 RGB images through
/// <see cref="TileImageCodec"/> (signal path + frequency-domain tile coder, no
/// overlap, no container) pixels → band bitstreams → pixels. At QP index 0 the
/// codec is lossless, so the decoded image must equal the input bit-for-bit across
/// multi-macroblock grids — the first end-to-end pixels-in/pixels-out milestone.
/// </summary>
public sealed class TileImageCodecTests
{
    private static void RoundTrip(int w, int h, int[] r, int[] g, int[] b)
    {
        var streams = TileImageCodec.Encode(r, g, b, w, h);
        var r2 = new int[w * h];
        var g2 = new int[w * h];
        var b2 = new int[w * h];
        TileImageCodec.Decode(streams, w, h, r2, g2, b2);

        for (var i = 0; i < w * h; i++)
        {
            r2[i].ShouldBe(r[i], $"R[{i}] ({w}x{h})");
            g2[i].ShouldBe(g[i], $"G[{i}] ({w}x{h})");
            b2[i].ShouldBe(b[i], $"B[{i}] ({w}x{h})");
        }
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(48, 16)]
    [InlineData(16, 48)]
    [InlineData(64, 48)]
    public void RandomImage_Lossless_RoundTrip(int w, int h)
    {
        var rng = new Random(0xC0DEC ^ (w * 31 + h));
        var r = new int[w * h];
        var g = new int[w * h];
        var b = new int[w * h];
        for (var trial = 0; trial < 12; trial++)
        {
            for (var i = 0; i < w * h; i++) { r[i] = rng.Next(256); g[i] = rng.Next(256); b[i] = rng.Next(256); }
            RoundTrip(w, h, r, g, b);
        }
    }

    [Fact]
    public void SmoothImage_Lossless_RoundTrip()
    {
        // A smooth image is the realistic case where DC/AD/AC prediction actually fires.
        const int w = 64, h = 64;
        var r = new int[w * h];
        var g = new int[w * h];
        var b = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = (x * 4 + y) & 0xff;
                g[i] = (x + y * 3) & 0xff;
                b[i] = (200 - x - y) & 0xff;
            }
        RoundTrip(w, h, r, g, b);
    }

    [Fact]
    public void FlatImage_Lossless_RoundTrip()
    {
        const int w = 32, h = 48;
        var r = new int[w * h];
        var g = new int[w * h];
        var b = new int[w * h];
        Array.Fill(r, 73); Array.Fill(g, 150); Array.Fill(b, 222);
        RoundTrip(w, h, r, g, b);
    }
}
