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

    // ----------------------------------------------------------------------
    // BD8 RGB NoFlexbits — multi-component pipeline.
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData((byte)0,   (byte)0,   (byte)0)]
    [InlineData((byte)50,  (byte)100, (byte)150)]
    [InlineData((byte)255, (byte)255, (byte)255)]
    [InlineData((byte)200, (byte)50,  (byte)25)]
    public void Uniform_16x16_RgbNoFlexbits_IsLossless(byte r, byte g, byte b)
    {
        var src = new byte[16 * 16 * 3];
        for (var i = 0; i < src.Length; i += 3)
        {
            src[i + 0] = r;
            src[i + 1] = g;
            src[i + 2] = b;
        }

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out var w, out var h);

        w.ShouldBe(16);
        h.ShouldBe(16);
        decoded.Length.ShouldBe(src.Length);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void RgbHorizontalGradient_16x16_NoFlexbits_IsLossless()
    {
        // Different gradient per channel — exercises cross-component DC prediction
        // and per-component LP+HP.
        var src = new byte[16 * 16 * 3];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var i = (y * 16 + x) * 3;
            src[i + 0] = (byte)(x * 16);         // R: horizontal sweep
            src[i + 1] = (byte)(255 - x * 16);   // G: reverse sweep
            src[i + 2] = (byte)((x * 8) & 0xff); // B: faster sweep
        }

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void RgbVerticalGradient_16x16_NoFlexbits_IsLossless()
    {
        var src = new byte[16 * 16 * 3];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var i = (y * 16 + x) * 3;
            src[i + 0] = (byte)(y * 16);
            src[i + 1] = (byte)(y * 8);
            src[i + 2] = (byte)(255 - y * 16);
        }

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void RgbCheckerboard_16x16_NoFlexbits_IsLossless()
    {
        var src = new byte[16 * 16 * 3];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var i = (y * 16 + x) * 3;
            var on = ((x + y) & 1) == 0;
            src[i + 0] = on ? (byte)0 : (byte)255;
            src[i + 1] = on ? (byte)255 : (byte)0;
            src[i + 2] = on ? (byte)128 : (byte)64;
        }

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void RgbRandom_16x16_NoFlexbits_IsLossless()
    {
        var rng = new Random(unchecked((int)0xDEADC0DE));
        var src = new byte[16 * 16 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void RgbRandom_32x32_NoFlexbits_IsLossless()
    {
        // 2×2 MB grid with RGB content — exercises cross-MB DC + LP prediction
        // for all three components in parallel.
        var rng = new Random(unchecked((int)0xBA5EBA11));
        var src = new byte[32 * 32 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void RgbRandom_64x64_NoFlexbits_IsLossless()
    {
        var rng = new Random(unchecked((int)0x12345678));
        var src = new byte[64 * 64 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 64, 64);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void RgbPureRedImage_RoundTrips()
    {
        // Extreme cross-channel asymmetry: red maxed, green/blue zero.
        var src = new byte[32 * 32 * 3];
        for (var i = 0; i < src.Length; i += 3)
        {
            src[i + 0] = 255;
            src[i + 1] = 0;
            src[i + 2] = 0;
        }

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i]);
    }

    // ----------------------------------------------------------------------
    // BD16 — the actual HDR-master target. Same pipeline, wider samples.
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)32768)]
    [InlineData((ushort)50000)]
    [InlineData((ushort)65535)]
    public void Uniform_16x16_Bd16Grayscale_IsLossless(ushort fill)
    {
        var src = new ushort[16 * 16];
        Array.Fill(src, fill);

        var bytes = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16GrayscaleNoFlexbits(bytes, out var w, out var h);

        w.ShouldBe(16);
        h.ShouldBe(16);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(fill, $"pixel {i}");
    }

    [Fact]
    public void Bd16Gradient_16x16_IsLossless()
    {
        // Full-range 16-bit horizontal gradient.
        var src = new ushort[16 * 16];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            src[y * 16 + x] = (ushort)(x * 4096);

        var bytes = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Bd16Random_16x16_IsLossless()
    {
        var rng = new Random(unchecked((int)0xABCD1234));
        var src = new ushort[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Bd16Random_32x32_IsLossless()
    {
        var rng = new Random(unchecked((int)0xBADC0FFE));
        var src = new ushort[32 * 32];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd16GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Bd16ExtremeValues_RoundTrip()
    {
        // Mix of 0, 65535, and mid-range — stresses sign bit, MSB, and dynamic range.
        var src = new ushort[16 * 16];
        for (var i = 0; i < src.Length; i++)
            src[i] = (i & 3) switch
            {
                0 => (ushort)0,
                1 => (ushort)65535,
                2 => (ushort)32768,
                _ => (ushort)1,
            };

        var bytes = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    // ----------------------------------------------------------------------
    // BD16 RGB — the actual HDR-master deliverable.
    // ----------------------------------------------------------------------

    [Fact]
    public void Bd16RgbUniform_RoundTrip()
    {
        var src = new ushort[16 * 16 * 3];
        for (var i = 0; i < src.Length; i += 3)
        {
            src[i + 0] = 12345;
            src[i + 1] = 23456;
            src[i + 2] = 34567;
        }

        var bytes = JxrEncoder.EncodeBd16RgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16RgbNoFlexbits(bytes, out var w, out var h);

        w.ShouldBe(16);
        h.ShouldBe(16);
        decoded.Length.ShouldBe(src.Length);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void Bd16RgbGradient_16x16_IsLossless()
    {
        // Per-channel gradient, each at different rates.
        var src = new ushort[16 * 16 * 3];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var i = (y * 16 + x) * 3;
            src[i + 0] = (ushort)(x * 4096);
            src[i + 1] = (ushort)((15 - x) * 4096);
            src[i + 2] = (ushort)(y * 4096);
        }

        var bytes = JxrEncoder.EncodeBd16RgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Bd16RgbRandom_32x32_IsLossless()
    {
        // Closest yet to the HDR-master shape: multi-MB 16-bit RGB random.
        var rng = new Random(unchecked((int)0xC0DEFACE));
        var src = new ushort[32 * 32 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16RgbNoFlexbits(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd16RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Bd16RgbRandom_64x64_IsLossless()
    {
        var rng = new Random(unchecked((int)0x5A11B0AD));
        var src = new ushort[64 * 64 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16RgbNoFlexbits(src, 64, 64);
        var decoded = JxrDecoder.DecodeBd16RgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
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
