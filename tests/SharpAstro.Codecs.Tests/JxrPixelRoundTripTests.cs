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
    public void NonMultipleOf16_DcOnly_Encodes()
    {
        // After Phase 8 the encoder pads sample reads to the image edge so any
        // dimension works. DcOnly stays lossy for non-uniform content so we
        // just verify it produces a valid codestream and decodes to the
        // declared dimensions.
        var src = new byte[17 * 17];
        var bytes = JxrEncoder.EncodeBd8GrayscaleDcOnly(src, 17, 17);
        var decoded = JxrDecoder.DecodeBd8GrayscaleDcOnly(bytes, out var w, out var h);
        w.ShouldBe(17);
        h.ShouldBe(17);
        decoded.Length.ShouldBe(17 * 17);
    }

    [Fact]
    public void NonAligned_17x17_NoFlexbits_IsLossless()
    {
        // The killer feature: real astrophoto dimensions are rarely 16-aligned.
        var rng = new Random(unchecked((int)0xA17A17));
        var src = new byte[17 * 17];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 17, 17);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out var w, out var h);
        w.ShouldBe(17);
        h.ShouldBe(17);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void NonAligned_33x47_RgbNoFlexbits_IsLossless()
    {
        var rng = new Random(unchecked((int)0x3347));
        var src = new byte[33 * 47 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var bytes = JxrEncoder.EncodeBd8RgbNoFlexbits(src, 33, 47);
        var decoded = JxrDecoder.DecodeBd8RgbNoFlexbits(bytes, out var w, out var h);
        w.ShouldBe(33);
        h.ShouldBe(47);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i], $"byte {i}");
    }

    [Fact]
    public void NonAligned_50x50_Bd16Rgb_IsLossless_HdrTarget()
    {
        // Mid-sized non-aligned BD16 RGB — closest test to actual astro file shape.
        var rng = new Random(unchecked((int)0x50505050));
        var src = new ushort[50 * 50 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16RgbNoFlexbits(src, 50, 50);
        var decoded = JxrDecoder.DecodeBd16RgbNoFlexbits(bytes, out var w, out var h);
        w.ShouldBe(50);
        h.ShouldBe(50);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i], $"sample {i}");
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

    // ----------------------------------------------------------------------
    // BD16F — IEEE binary16 (half-float). The HDR-master target for linear-
    // light / radiance pipelines. Same integer pipeline as BD16; the bit
    // patterns round-trip exactly.
    // ----------------------------------------------------------------------

    [Fact]
    public void Bd16FGrayscale_Zeroes_RoundTrips()
    {
        var src = new ushort[16 * 16]; // all zeros = +0.0 half-floats
        var bytes = JxrEncoder.EncodeBd16FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16FGrayscaleNoFlexbits(bytes, out var w, out var h);
        w.ShouldBe(16);
        h.ShouldBe(16);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe((ushort)0);
    }

    [Theory]
    [InlineData((ushort)0x0000)] // +0.0
    [InlineData((ushort)0x8000)] // -0.0
    [InlineData((ushort)0x3C00)] // +1.0
    [InlineData((ushort)0xBC00)] // -1.0
    [InlineData((ushort)0x7BFF)] // +max normal
    [InlineData((ushort)0xFBFF)] // -max normal
    public void Bd16FGrayscale_UniformHalfBits_RoundTrip(ushort halfPattern)
    {
        var src = new ushort[16 * 16];
        Array.Fill(src, halfPattern);
        var bytes = JxrEncoder.EncodeBd16FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16FGrayscaleNoFlexbits(bytes, out _, out _);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(halfPattern, $"pixel {i}");
    }

    [Fact]
    public void Bd16FGrayscale_RandomBits_IsLossless()
    {
        // Random 16-bit patterns — includes some NaN, Inf, denormals. The integer
        // FCT pipeline must preserve them as bit patterns.
        var rng = new Random(unchecked((int)0xF10A75));
        var src = new ushort[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16FGrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Bd16FRgb_UniformWhite_RoundTrips()
    {
        // +1.0 across all three channels: 0x3C00 = IEEE half-float 1.0.
        const ushort one = 0x3C00;
        var src = new ushort[16 * 16 * 3];
        Array.Fill(src, one);

        var bytes = JxrEncoder.EncodeBd16FRgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd16FRgbNoFlexbits(bytes, out var w, out var h);
        w.ShouldBe(16);
        h.ShouldBe(16);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(one);
    }

    [Fact]
    public void Bd16FRgb_Random_32x32_IsLossless()
    {
        var rng = new Random(unchecked((int)0xF10AF10A));
        var src = new ushort[32 * 32 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16FRgbNoFlexbits(src, 32, 32);
        var decoded = JxrDecoder.DecodeBd16FRgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Bd16FRgb_Random_64x64_IsLossless()
    {
        // The full HDR-master deliverable shape: 64×64 half-float RGB.
        var rng = new Random(unchecked((int)0xAAAA5555));
        var src = new ushort[64 * 64 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var bytes = JxrEncoder.EncodeBd16FRgbNoFlexbits(src, 64, 64);
        var decoded = JxrDecoder.DecodeBd16FRgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Bd16FRgb_PlaneHeaderCarriesHalfFloatMetadata()
    {
        // Verify the IMAGE_PLANE_HEADER actually advertises the half-float layout
        // (LEN_MANTISSA=10, EXP_BIAS=15-128) so downstream tools can interpret.
        var src = new ushort[16 * 16 * 3];
        var bytes = JxrEncoder.EncodeBd16FRgbNoFlexbits(src, 16, 16);

        var img = CodedImage.Decode(bytes);
        img.ImageHeader.OutputBitDepth.ShouldBe(JxrOutputBitDepth.Bd16F);
        img.PlaneHeader.LenMantissa.ShouldBe((byte)10);
        img.PlaneHeader.ExpBias.ShouldBe((sbyte)(15 - 128));
    }

    // ----------------------------------------------------------------------
    // Multi-tile pixel round-trip — JxrTileLayout enables tile-isolated
    // prediction in encoder + decoder.
    // ----------------------------------------------------------------------

    [Fact]
    public void Tiled_2x2_Bd8Grayscale_Random_RoundTrips()
    {
        // 64×64 image (4×4 MB grid) split into 2×2 tiles of 2×2 MBs each.
        // Each tile gets its own DC/LP prediction context — masks suppress
        // prediction across the tile boundary so encoder and decoder agree.
        var rng = new Random(unchecked((int)0xBADBADBAD));
        var src = new byte[64 * 64];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var layout = JxrTileLayout.Uniform(totalWidthInMb: 4, totalHeightInMb: 4, cols: 2, rows: 2);
        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 64, 64, layout);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out var w, out var h);

        w.ShouldBe(64);
        h.ShouldBe(64);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Tiled_2x2_VerifiedInHeader()
    {
        var src = new byte[64 * 64];
        var layout = JxrTileLayout.Uniform(4, 4, cols: 2, rows: 2);
        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 64, 64, layout);

        var img = CodedImage.Decode(bytes);
        img.ImageHeader.TilingFlag.ShouldBeTrue();
        img.ImageHeader.NumVerTilesMinus1.ShouldBe(1);
        img.ImageHeader.NumHorTilesMinus1.ShouldBe(1);
        img.ImageHeader.TileWidthInMb.ShouldBe(new[] { 2 });
        img.ImageHeader.TileHeightInMb.ShouldBe(new[] { 2 });
    }

    [Fact]
    public void Tiled_NonUniformTiles_RoundTrips()
    {
        // 64×64 (4×4 MB grid) with asymmetric tile sizes: column widths [1, 2, 1],
        // row heights [3, 1]. Stresses the mask plumbing.
        var rng = new Random(unchecked((int)0xAA55AA55));
        var src = new byte[64 * 64];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var layout = new JxrTileLayout([1, 2], [3]);
        var bytes = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(src, 64, 64, layout);
        var decoded = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void JxrTileLayout_BuildMasks_MarksTileBoundaries()
    {
        // 4-MB-wide image, tile widths [2] → 2 tile columns at MB cols {0, 2}.
        var layout = new JxrTileLayout([2], []);
        var (left, top) = layout.BuildMasks(widthInMb: 4, heightInMb: 1);

        left[0, 0].ShouldBeTrue("MB col 0 always tile-left");
        left[1, 0].ShouldBeFalse();
        left[2, 0].ShouldBeTrue("MB col 2 is tile-left for the second tile column");
        left[3, 0].ShouldBeFalse();

        top[0, 0].ShouldBeTrue("MB row 0 always tile-top");
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
