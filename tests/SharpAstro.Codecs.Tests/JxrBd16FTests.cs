using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8d — BD16F half-float grayscale + RGB. The half is preserved as its raw
/// sign-magnitude bit pattern (no mantissa quantization, unlike BD32F), so a codec
/// round-trip reproduces the half bit-exact — except −0 collapses to +0, which
/// <see cref="FloatPixel.PixelToHalf"/>∘<see cref="FloatPixel.HalfToPixel"/> models.
/// RGB runs YCoCg-R over the raw half magnitudes (reversibly). (Self round-trip proves
/// enc↔dec symmetry; the oracle tests prove conformance with jxrlib.)
/// </summary>
public sealed class JxrBd16FTests
{
    [Theory]
    [InlineData(16, 16, "gradient")]
    [InlineData(48, 32, "gradient")]
    [InlineData(64, 48, "hdr")]
    [InlineData(80, 80, "gradient")]
    [InlineData(272, 16, "gradient")]
    public void EncodeGrayF16_DecodeGrayF16_RoundTripsLossless(int w, int h, string kind)
    {
        var y = GrayPattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGrayF16(y, w, h);

        var (dw, dh, dy) = JxrImageCodec.DecodeGrayF16(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
            Bits(dy[i]).ShouldBe(Bits(Expected(y[i])), $"Y[{i}] ({kind} {w}x{h})");
    }

    [Theory]
    [InlineData(48, 32, 1)]
    [InlineData(80, 80, 2)]
    public void EncodeGrayF16_Overlap_RoundTripsLossless(int w, int h, int overlap)
    {
        var y = GrayPattern(w, h, "hdr");
        var jxr = JxrImageCodec.EncodeGrayF16(y, w, h, overlap: overlap);

        var (_, _, dy) = JxrImageCodec.DecodeGrayF16(jxr);
        for (var i = 0; i < w * h; i++)
            Bits(dy[i]).ShouldBe(Bits(Expected(y[i])), $"Y[{i}] (OL{overlap} {w}x{h})");
    }

    [Theory]
    [InlineData(16, 16, "gradient")]
    [InlineData(48, 32, "gradient")]
    [InlineData(64, 48, "hdr")]
    [InlineData(80, 80, "gradient")]
    public void EncodeRgbF16_DecodeRgbF16_RoundTripsLossless(int w, int h, string kind)
    {
        var rgb = RgbPattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeRgbF16(rgb, w, h);

        var (dw, dh, drgb) = JxrImageCodec.DecodeRgbF16(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h * 3; i++)
            Bits(drgb[i]).ShouldBe(Bits(Expected(rgb[i])), $"RGB[{i}] ({kind} {w}x{h})");
    }

    [Theory]
    [InlineData(48, 32, 1)]
    [InlineData(64, 48, 2)]
    public void EncodeRgbF16_Overlap_RoundTripsLossless(int w, int h, int overlap)
    {
        var rgb = RgbPattern(w, h, "hdr");
        var jxr = JxrImageCodec.EncodeRgbF16(rgb, w, h, overlap: overlap);

        var (_, _, drgb) = JxrImageCodec.DecodeRgbF16(jxr);
        for (var i = 0; i < w * h * 3; i++)
            Bits(drgb[i]).ShouldBe(Bits(Expected(rgb[i])), $"RGB[{i}] (OL{overlap} {w}x{h})");
    }

    [Fact]
    public void EncodeGrayF16_TagsAsGrayHalf16Bpp()
    {
        var jxr = JxrImageCodec.EncodeGrayF16(GrayPattern(48, 32, "hdr"), 48, 32);
        JxrContainer.Read(jxr).PixelFormat.ShouldBe(JxrPixelFormat.GrayHalf16Bpp);
    }

    [Fact]
    public void EncodeRgbF16_TagsAsRgbHalf48Bpp()
    {
        var jxr = JxrImageCodec.EncodeRgbF16(RgbPattern(48, 32, "hdr"), 48, 32);
        JxrContainer.Read(jxr).PixelFormat.ShouldBe(JxrPixelFormat.RgbHalf48Bpp);
    }

    internal static Half[] GrayPattern(int w, int h, string kind)
    {
        var y = new Half[w * h];
        var rng = new Random(0x16F + w * 31 + h);
        for (var yy = 0; yy < h; yy++)
            for (var xx = 0; xx < w; xx++)
            {
                float f = kind == "hdr"
                    ? (float)(rng.NextDouble() * 50000.0 - 50.0)            // HDR incl. negatives
                    : (xx * 11 + yy * 7) * 1.5f + 0.25f;                    // smooth ramp
                y[yy * w + xx] = (Half)f;
            }
        return y;
    }

    internal static Half[] RgbPattern(int w, int h, string kind)
    {
        var rgb = new Half[w * h * 3];
        var rng = new Random(0x4B6 + w * 31 + h);
        for (var i = 0; i < w * h; i++)
            for (var c = 0; c < 3; c++)
            {
                float f = kind == "hdr"
                    ? (float)(rng.NextDouble() * 40000.0)
                    : ((i * 7 + c * 137) % 4096) * 0.75f + 0.5f;
                rgb[i * 3 + c] = (Half)f;
            }
        return rgb;
    }

    // The value a lossless BD16F round-trip reproduces (identity for everything but −0 → +0).
    internal static Half Expected(Half h) => FloatPixel.PixelToHalf(FloatPixel.HalfToPixel(h));

    private static short Bits(Half h) => BitConverter.HalfToInt16Bits(h);
}
