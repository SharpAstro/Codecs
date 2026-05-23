using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Pixel-level round-trip tests for the JXR encoder/decoder facade. These
/// are the first JXR tests that go all the way from raw sample bytes,
/// through the entire transform + prediction + per-MB coding + codestream
/// framing pipeline, and back to sample bytes.
/// </summary>
/// <remarks>
/// DcOnly mode is lossless only for macroblocks whose AC content is zero
/// (uniform 16×16 blocks). Tests with frequency content live in the
/// NoFlexbits suite when that lands.
/// </remarks>
public sealed class JxrPixelRoundTripTests
{
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)50)]
    [InlineData((byte)128)]
    [InlineData((byte)200)]
    [InlineData((byte)254)]
    [InlineData((byte)255)]
    public void Uniform_16x16_DcOnly_IsLossless(byte fill)
    {
        // A uniform 16×16 patch has zero AC content; DcOnly should preserve it exactly.
        var src = new byte[16 * 16];
        Array.Fill(src, fill);

        var bytes = JxrEncoder.EncodeBd8GrayscaleDcOnly(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8GrayscaleDcOnly(bytes, out var w, out var h);

        w.ShouldBe(16);
        h.ShouldBe(16);
        decoded.Length.ShouldBe(src.Length);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(fill, $"pixel {i}");
    }

    [Fact]
    public void Uniform_32x32_DcOnly_IsLossless()
    {
        // 2×2 MB grid, still uniform — DC prediction kicks in across MBs but
        // all neighbouring DC values match, so residuals stay zero.
        var src = new byte[32 * 32];
        Array.Fill(src, (byte)77);

        var bytes = JxrEncoder.EncodeBd8GrayscaleDcOnly(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd8GrayscaleDcOnly(bytes, out var w, out var h);

        w.ShouldBe(32);
        h.ShouldBe(32);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe((byte)77, $"pixel {i}");
    }

    [Fact]
    public void Uniform_64x48_DcOnly_IsLossless()
    {
        // Non-square multi-MB image: 4×3 MB grid.
        var src = new byte[64 * 48];
        Array.Fill(src, (byte)200);

        var bytes = JxrEncoder.EncodeBd8GrayscaleDcOnly(src, 64, 48);
        var decoded = JxrDecoder.DecodeBd8GrayscaleDcOnly(bytes, out var w, out var h);

        w.ShouldBe(64);
        h.ShouldBe(48);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe((byte)200);
    }

    [Fact]
    public void DistinctPerMb_DcOnly_PreservesEachMbAverage()
    {
        // 4 MBs each with a different uniform value — DcOnly should still
        // give back exactly those values (each MB is internally uniform).
        var src = new byte[32 * 32];
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            // Quadrant fills: 50, 100, 150, 200.
            var qx = x >> 4;
            var qy = y >> 4;
            src[y * 32 + x] = (byte)(50 + (qy * 2 + qx) * 50);
        }

        var bytes = JxrEncoder.EncodeBd8GrayscaleDcOnly(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd8GrayscaleDcOnly(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void NonMultipleOf16_Throws()
    {
        var src = new byte[17 * 17];
        var threw = false;
        try { JxrEncoder.EncodeBd8GrayscaleDcOnly(src, 17, 17); }
        catch (ArgumentException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void RoundTrip_ProducesNonEmptyCodestream()
    {
        // Sanity: the codestream must contain at least the IMAGE_HEADER (10+ bytes).
        var src = new byte[16 * 16];
        var bytes = JxrEncoder.EncodeBd8GrayscaleDcOnly(src, 16, 16);
        bytes.Length.ShouldBeGreaterThan(10);
        // First 8 bytes are the GDI signature "WMPHOTO\0".
        bytes[0].ShouldBe((byte)'W');
        bytes[7].ShouldBe((byte)0);
    }

    // ----------------------------------------------------------------------
    // NoFlexbits — full DC + LP + HP path. Lossless for arbitrary pixel
    // content at OverlapMode = 0.
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)50)]
    [InlineData((byte)128)]
    [InlineData((byte)200)]
    [InlineData((byte)255)]
    public void Uniform_16x16_NoFlexbits_IsLossless(byte fill)
    {
        var src = new byte[16 * 16];
        Array.Fill(src, fill);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out var w, out var h);

        w.ShouldBe(16);
        h.ShouldBe(16);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(fill, $"pixel {i}");
    }

    [Fact]
    public void HorizontalGradient_16x16_NoFlexbits_IsLossless()
    {
        // Pixel value increases with x — non-trivial AC content. Lossless
        // round-trip requires the entire DC + LP + HP pipeline to compose correctly.
        var src = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            src[y * 16 + x] = (byte)(x * 16);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void VerticalGradient_16x16_NoFlexbits_IsLossless()
    {
        var src = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            src[y * 16 + x] = (byte)(y * 16);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Checkerboard_16x16_NoFlexbits_IsLossless()
    {
        // Maximum high-frequency content for a single MB.
        var src = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            src[y * 16 + x] = (byte)(((x + y) & 1) == 0 ? 0 : 255);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Random_16x16_NoFlexbits_IsLossless()
    {
        var rng = new Random(unchecked((int)0xFEEDFACE));
        var src = new byte[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void HorizontalGradient_32x32_NoFlexbits_IsLossless()
    {
        // 2×2 MB grid: exercises cross-MB DC + LP prediction.
        var src = new byte[32 * 32];
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
            src[y * 32 + x] = (byte)(x * 8);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Random_64x64_NoFlexbits_IsLossless()
    {
        // 4×4 MB grid with fully random content — most demanding round-trip.
        var rng = new Random(unchecked((int)0xCAFEBABE));
        var src = new byte[64 * 64];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 64, 64);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void HpPredictionAlone_HorizontalGradient_RoundTrips()
    {
        // Isolate the HP-prediction layer from the codestream. Build mbHp
        // and mbDcLp as the FCT pyramid would produce for a 16×16 horizontal
        // gradient, run HpPrediction.Encode then HpPrediction.Decode, and
        // verify mbHp returns to its pre-encode state.
        var mbW = 1;
        var mbH = 1;
        var mbHp = new int[mbW, mbH, 1, 16, 16];
        var mbDcLp = new int[mbW, mbH, 1, 16];

        // Plant a horizontal-frequency pattern in mbDcLp positions 1,2,3 so
        // CalcMode picks mode 1.
        mbDcLp[0, 0, 0, 1] = 100;
        mbDcLp[0, 0, 0, 2] = 50;
        mbDcLp[0, 0, 0, 3] = 25;

        // Plant non-zero HP coefficients at positions 1,2,3 of every sub-block.
        for (var blk = 0; blk < 16; blk++)
        {
            mbHp[0, 0, 0, blk, 1] = 17 + blk;
            mbHp[0, 0, 0, blk, 2] = 11 - blk;
            mbHp[0, 0, 0, blk, 3] = 5;
        }

        // Snapshot original.
        var original = (int[,,,,])mbHp.Clone();

        var mbHpMode = new int[mbW, mbH];
        mbHpMode[0, 0] = HpPrediction.CalcMode(mbDcLp, 0, 0, JxrInternalColorFormat.YOnly, 1);
        mbHpMode[0, 0].ShouldBe(1, "horizontal pattern in LP should pick mode 1");

        HpPrediction.Encode(mbHp, mbHpMode, JxrInternalColorFormat.YOnly);
        HpPrediction.Decode(mbHp, mbHpMode, JxrInternalColorFormat.YOnly);

        for (var blk = 0; blk < 16; blk++)
        for (var p = 0; p < 16; p++)
            mbHp[0, 0, 0, blk, p].ShouldBe(original[0, 0, 0, blk, p], $"block {blk} pos {p}");
    }
}
