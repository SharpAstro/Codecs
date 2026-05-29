using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8c — BD32F float grayscale (mono), the HDR headline. The float↔pixel mapping
/// quantizes the mantissa to <c>lenMantissa</c> bits, so a codec round-trip reproduces
/// <see cref="FloatPixel.Requantize"/>(x) exactly (NOT x — that's the format's deliberate
/// precision/size trade-off), and the codec itself is lossless on that representation.
/// Values are carried verbatim (un-normalized), spanning the HDR range the astrophotography
/// consumer needs. (Self round-trip proves enc↔dec symmetry + float mapping; the oracle
/// tests prove conformance with jxrlib.)
/// </summary>
public sealed class JxrBd32FTests
{
    [Theory]
    [InlineData(16, 16, "gradient", 8)]
    [InlineData(48, 32, "gradient", 8)]
    [InlineData(64, 48, "hdr", 8)]
    [InlineData(80, 80, "gradient", 13)]
    [InlineData(64, 48, "hdr", 13)]
    [InlineData(272, 16, "gradient", 8)]
    public void EncodeGrayF32_DecodeGrayF32_RoundTripsToRequantized(int w, int h, string kind, int lenMantissa)
    {
        const int expBias = 0;
        var y = Pattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGrayF32(y, w, h, lenMantissa, expBias);

        var (dw, dh, dy) = JxrImageCodec.DecodeGrayF32(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
        {
            float expected = FloatPixel.Requantize(y[i], expBias, lenMantissa);
            BitsOf(dy[i]).ShouldBe(BitsOf(expected), $"Y[{i}] ({kind} {w}x{h} lm{lenMantissa})");
        }
    }

    [Theory]
    [InlineData(48, 32, 1)]
    [InlineData(80, 80, 2)]
    public void EncodeGrayF32_Overlap_RoundTripsToRequantized(int w, int h, int overlap)
    {
        const int lm = 8, expBias = 0;
        var y = Pattern(w, h, "hdr");
        var jxr = JxrImageCodec.EncodeGrayF32(y, w, h, lm, expBias, overlap: overlap);

        var (_, _, dy) = JxrImageCodec.DecodeGrayF32(jxr);
        for (var i = 0; i < w * h; i++)
            BitsOf(dy[i]).ShouldBe(BitsOf(FloatPixel.Requantize(y[i], expBias, lm)), $"Y[{i}] (OL{overlap} {w}x{h})");
    }

    [Fact]
    public void EncodeGrayF32_NonZeroExpBias_RoundTrips()
    {
        const int w = 48, h = 32, lm = 10, expBias = 4;
        var y = Pattern(w, h, "hdr");
        var jxr = JxrImageCodec.EncodeGrayF32(y, w, h, lm, expBias);

        var (_, _, dy) = JxrImageCodec.DecodeGrayF32(jxr);
        for (var i = 0; i < w * h; i++)
            BitsOf(dy[i]).ShouldBe(BitsOf(FloatPixel.Requantize(y[i], expBias, lm)), $"Y[{i}] (expBias {expBias})");
    }

    [Fact]
    public void EncodeGrayF32_TagsAsGrayFloat32Bpp()
    {
        var jxr = JxrImageCodec.EncodeGrayF32(Pattern(48, 32, "hdr"), 48, 32, 8);
        JxrContainer.Read(jxr).PixelFormat.ShouldBe(JxrPixelFormat.GrayFloat32Bpp);
    }

    // FloatPixel round-trip is idempotent: requantizing an already-requantized value is a no-op.
    [Theory]
    [InlineData(8)]
    [InlineData(13)]
    public void FloatPixel_Requantize_IsIdempotent(int lm)
    {
        var rng = new Random(0x32F + lm);
        for (var i = 0; i < 5000; i++)
        {
            float f = (float)(rng.NextDouble() * 80000.0 - 100.0); // HDR-ish range incl. negatives
            float q1 = FloatPixel.Requantize(f, 0, lm);
            float q2 = FloatPixel.Requantize(q1, 0, lm);
            BitsOf(q2).ShouldBe(BitsOf(q1), $"requantize not idempotent for {f} (lm{lm})");
        }
    }

    internal static float[] Pattern(int w, int h, string kind)
    {
        var y = new float[w * h];
        switch (kind)
        {
            case "hdr":
                // Mix sub-1.0, ~1.0, and large HDR values (star cores overshoot, FITS spans tens of thousands).
                var rng = new Random(0x8C + w * 31 + h);
                for (var i = 0; i < y.Length; i++)
                {
                    double r = rng.NextDouble();
                    y[i] = r < 0.2 ? (float)(r * 0.5) : r < 0.6 ? (float)(0.5 + r) : (float)(r * 60000.0);
                }
                break;
            default: // gradient — smooth float ramp up into the thousands
                for (var yy = 0; yy < h; yy++)
                    for (var xx = 0; xx < w; xx++)
                        y[yy * w + xx] = (xx * 11 + yy * 7) * 1.5f + 0.25f;
                break;
        }
        return y;
    }

    private static int BitsOf(float f) => BitConverter.SingleToInt32Bits(f);
}
