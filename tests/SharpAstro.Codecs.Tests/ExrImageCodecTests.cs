using ImageMagick;
using SharpAstro.Exr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// EXR Rung 5a — the <see cref="ExrImageCodec"/> façade (HDR float ⟷ .exr), mirroring
/// JxrImageCodec. Verbatim (non-normalized) values must survive a round-trip exactly for
/// the lossless schemes; the façade files also open in OpenEXR (Magick.NET).
/// </summary>
public sealed class ExrImageCodecTests
{
    private readonly ITestOutputHelper _out;
    public ExrImageCodecTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(ExrCompression.None)]
    [InlineData(ExrCompression.Rle)]
    [InlineData(ExrCompression.Zip)]
    [InlineData(ExrCompression.Zips)]
    [InlineData(ExrCompression.Piz)]
    public void MonoFloat_RoundTripsVerbatim(ExrCompression comp)
    {
        const int w = 40, h = 33;
        var px = new float[w * h];
        // HDR semantics: values overshoot 1.0 and span a wide range, written verbatim.
        for (var i = 0; i < px.Length; i++) px[i] = MathF.Sin(i * 0.05f) * 12000f + (i % 7) * 0.001f;

        var (dw, dh, dp) = ExrImageCodec.DecodeMonoFloat(ExrImageCodec.EncodeMonoFloat(px, w, h, comp));
        dw.ShouldBe(w); dh.ShouldBe(h);
        dp.ShouldBe(px); // exact float bits
    }

    [Theory]
    [InlineData(ExrCompression.Zip)]
    [InlineData(ExrCompression.Piz)]
    public void RgbHalf_Interleaved_RoundTripsVerbatim(ExrCompression comp)
    {
        const int w = 37, h = 41;
        var rgb = new Half[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = (Half)(i * 0.01f);
            rgb[i * 3 + 1] = (Half)(i * 0.02f + 1.5f);
            rgb[i * 3 + 2] = (Half)(100f - i * 0.005f);
        }
        var (dw, dh, drgb) = ExrImageCodec.DecodeRgbHalf(ExrImageCodec.EncodeRgbHalf(rgb, w, h, comp));
        dw.ShouldBe(w); dh.ShouldBe(h);
        drgb.ShouldBe(rgb);
    }

    [Fact]
    public void MonoHalf_And_RgbFloat_RoundTrip()
    {
        const int w = 20, h = 16;
        var mh = new Half[w * h];
        for (var i = 0; i < mh.Length; i++) mh[i] = (Half)(i * 0.1f);
        var (_, _, dmh) = ExrImageCodec.DecodeMonoHalf(ExrImageCodec.EncodeMonoHalf(mh, w, h, ExrCompression.Piz));
        dmh.ShouldBe(mh);

        var rgb = new float[w * h * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = i * 1.25f - 500f;
        var (_, _, drgb) = ExrImageCodec.DecodeRgbFloat(ExrImageCodec.EncodeRgbFloat(rgb, w, h, ExrCompression.Zip));
        drgb.ShouldBe(rgb);
    }

    [Fact]
    public void EncodeMonoFloat_OpensInMagick_WithCorrectValues()
    {
        const int w = 24, h = 18;
        float F(int x, int y) => x * 0.05f - y * 0.02f + 0.3f;
        var px = new float[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) px[y * w + x] = F(x, y);

        using var m = new MagickImage(ExrImageCodec.EncodeMonoFloat(px, w, h, ExrCompression.Piz));
        m.Width.ShouldBe((uint)w); m.Height.ShouldBe((uint)h);
        using var pix = m.GetPixels();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float got = pix.GetPixel(x, y).ToArray()![0];
                float expected = F(x, y) * 65535f;
                Math.Abs(got - expected).ShouldBeLessThanOrEqualTo(Math.Max(1f, Math.Abs(expected) * 1e-3f), $"({x},{y})");
            }
    }
}
