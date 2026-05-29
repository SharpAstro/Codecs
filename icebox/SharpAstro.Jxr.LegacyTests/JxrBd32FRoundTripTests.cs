using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Round-trip tests for BD32F (single-precision float) — the full-mantissa
/// HDR target. T.832 D.2.5 converts each float into a sign-magnitude integer
/// whose mantissa is truncated to <c>LEN_MANTISSA</c> bits before the FCT
/// pipeline, so the round-trip is bit-exact only at the chosen precision —
/// not at the full 23-bit IEEE mantissa unless the inputs are tame enough
/// that the FCT cascade stays inside int32.
/// </summary>
public sealed class JxrBd32FRoundTripTests
{
    [Fact]
    public void Bd32F_Grayscale_Uniform_IsLossless_AtDefaultLenMantissa()
    {
        // Uniform value — the most-favourable case: trivially round-trips at any LEN_MANTISSA.
        var src = new float[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = 0.5f;

        var bytes = JxrEncoder.EncodeBd32FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd32FGrayscaleNoFlexbits(bytes, out var w, out var h);

        w.ShouldBe(16);
        h.ShouldBe(16);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Bd32F_Grayscale_ConstrainedFloats_RoundTrip_AtDefaultLenMantissa()
    {
        // LEN_MANTISSA = 8 (default) preserves 8 bits of mantissa precision.
        // Construct floats that have ≤ 8 bits of mantissa to start with — those
        // round-trip bit-exact through the int pipeline.
        var src = new float[16 * 16];
        var rng = new Random(unchecked((int)0xB32FBEEF));
        for (var i = 0; i < src.Length; i++)
        {
            // exp in [120, 130], mantissa with only top 8 bits set: bit-exact survivable.
            var sign = rng.Next(0, 2);
            var exp = rng.Next(120, 131);
            var mant8 = rng.Next(0, 256);
            var raw = (uint)((sign << 31) | (exp << 23) | (mant8 << 15));
            src[i] = BitConverter.UInt32BitsToSingle(raw);
        }

        var bytes = JxrEncoder.EncodeBd32FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd32FGrayscaleNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Bd32F_Rgb_ConstrainedFloats_RoundTrip()
    {
        var src = new float[16 * 16 * 3];
        var rng = new Random(unchecked((int)0xB32FA111));
        for (var i = 0; i < src.Length; i++)
        {
            var sign = rng.Next(0, 2);
            var exp = rng.Next(120, 131);
            var mant8 = rng.Next(0, 256);
            var raw = (uint)((sign << 31) | (exp << 23) | (mant8 << 15));
            src[i] = BitConverter.UInt32BitsToSingle(raw);
        }

        var bytes = JxrEncoder.EncodeBd32FRgbNoFlexbits(src, 16, 16);
        var decoded = JxrDecoder.DecodeBd32FRgbNoFlexbits(bytes, out _, out _);

        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Bd32F_LenMantissaTruncatesPrecision()
    {
        // A float with high-precision mantissa bits loses the bits below
        // LEN_MANTISSA. Verify the truncation behaviour directly via the helper.
        var raw = (uint)((0u << 31) | (128u << 23) | 0x7FFFFFu); // sign=+, exp=128, mant=0x7FFFFF (all bits set)
        var f = BitConverter.UInt32BitsToSingle(raw);

        var ints = JxrEncoder.Bd32FToIntArray([f], lenMantissa: 8, numComponents: 1, width: 1, height: 1);
        // (exp << 8) | (mant >> 15) = (128 << 8) | (0x7FFFFF >> 15) = 0x8000 | 0xFF = 0x80FF
        ints[0].ShouldBe(0x80FF);

        // Round-trip via IntArrayToBd32F restores the top 8 bits of the mantissa,
        // zero-extending the lower 15 bits.
        var back = JxrEncoder.IntArrayToBd32F(ints, lenMantissa: 8);
        var backRaw = BitConverter.SingleToUInt32Bits(back[0]);
        // Mantissa preserved: top 8 = 0xFF (= 0b11111111); padded with 15 zeros.
        var expectedMant = 0xFFu << 15; // 0x7F8000
        ((backRaw >> 23) & 0xFF).ShouldBe(128u);
        (backRaw & 0x7FFFFFu).ShouldBe(expectedMant);
    }

    [Fact]
    public void Bd32F_HigherLenMantissa_PreservesMorePrecision()
    {
        // At LEN_MANTISSA = 16 we preserve 16 bits of mantissa.
        var raw = (uint)((0u << 31) | (128u << 23) | 0x7FFFFFu);
        var f = BitConverter.UInt32BitsToSingle(raw);
        var ints = JxrEncoder.Bd32FToIntArray([f], lenMantissa: 16, numComponents: 1, width: 1, height: 1);
        // (exp << 16) | (mant >> 7) = (128 << 16) | (0x7FFFFF >> 7) = 0x800000 | 0xFFFF = 0x80FFFF
        ints[0].ShouldBe(0x80FFFF);

        var back = JxrEncoder.IntArrayToBd32F(ints, lenMantissa: 16);
        var backRaw = BitConverter.SingleToUInt32Bits(back[0]);
        ((backRaw >> 23) & 0xFF).ShouldBe(128u);
        (backRaw & 0x7FFFFFu).ShouldBe(0xFFFFu << 7); // top 16 mantissa bits preserved
    }

    [Fact]
    public void Bd32F_NegativeFloats_RoundTrip()
    {
        var src = new float[16];
        for (var i = 0; i < src.Length; i++) src[i] = -0.25f * (i + 1);

        var ints = JxrEncoder.Bd32FToIntArray(src, lenMantissa: 8, numComponents: 1, width: 16, height: 1);
        var back = JxrEncoder.IntArrayToBd32F(ints, lenMantissa: 8);

        for (var i = 0; i < src.Length; i++)
            back[i].ShouldBe(src[i], $"i={i}");
    }

    [Fact]
    public void Bd32F_HeaderEncodesBd32F_AndLenMantissa()
    {
        var src = new float[16 * 16];
        var bytes = JxrEncoder.EncodeBd32FGrayscaleNoFlexbits(src, 16, 16, lenMantissa: 12);
        var img = CodedImage.Decode(bytes);

        img.ImageHeader.OutputBitDepth.ShouldBe(JxrOutputBitDepth.Bd32F);
        img.PlaneHeader.LenMantissa.ShouldBe((byte)12);
        // EXP_BIAS stored as raw u(8) byte; we picked -128 (= 0x80 = sbyte -128) so
        // the decoder reads it back as the same value.
        img.PlaneHeader.ExpBias.ShouldBe(unchecked((sbyte)-128));
    }

    [Fact]
    public void Bd32F_NonAlignedSize_RoundTrips()
    {
        var src = new float[33 * 47];
        var rng = new Random(unchecked((int)0x3347B32F));
        for (var i = 0; i < src.Length; i++)
        {
            var sign = rng.Next(0, 2);
            var exp = rng.Next(120, 131);
            var mant8 = rng.Next(0, 256);
            var raw = (uint)((sign << 31) | (exp << 23) | (mant8 << 15));
            src[i] = BitConverter.UInt32BitsToSingle(raw);
        }

        var bytes = JxrEncoder.EncodeBd32FGrayscaleNoFlexbits(src, 33, 47);
        var decoded = JxrDecoder.DecodeBd32FGrayscaleNoFlexbits(bytes, out var w, out var h);
        w.ShouldBe(33);
        h.ShouldBe(47);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"i={i}");
    }

    [Fact]
    public void Bd32F_Tiled_RoundTrips()
    {
        var src = new float[32 * 32 * 3];
        var rng = new Random(unchecked((int)0xB32F7100));
        for (var i = 0; i < src.Length; i++)
        {
            var sign = rng.Next(0, 2);
            var exp = rng.Next(120, 131);
            var mant8 = rng.Next(0, 256);
            var raw = (uint)((sign << 31) | (exp << 23) | (mant8 << 15));
            src[i] = BitConverter.UInt32BitsToSingle(raw);
        }
        // 32×32 image = 2×2 MB grid; split into 2×2 tiles of 1×1 MB each.
        var tiling = JxrTileLayout.Uniform(totalWidthInMb: 2, totalHeightInMb: 2, cols: 2, rows: 2);
        var bytes = JxrEncoder.EncodeBd32FRgbNoFlexbits(src, 32, 32, tiling: tiling);
        var decoded = JxrDecoder.DecodeBd32FRgbNoFlexbits(bytes, out _, out _);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Bd32F_FrequencyMode_RoundTrips()
    {
        var src = new float[16 * 16 * 3];
        var rng = new Random(unchecked((int)0xB32F40DE));
        for (var i = 0; i < src.Length; i++)
        {
            var sign = rng.Next(0, 2);
            var exp = rng.Next(120, 131);
            var mant8 = rng.Next(0, 256);
            var raw = (uint)((sign << 31) | (exp << 23) | (mant8 << 15));
            src[i] = BitConverter.UInt32BitsToSingle(raw);
        }
        var bytes = JxrEncoder.EncodeBd32FRgbNoFlexbits(src, 16, 16, frequencyMode: true);
        var decoded = JxrDecoder.DecodeBd32FRgbNoFlexbits(bytes, out _, out _);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Bd32F_LenMantissaOutOfRange_Throws()
    {
        var src = new float[16];
        Should.Throw<ArgumentOutOfRangeException>(() =>
            JxrEncoder.EncodeBd32FGrayscaleNoFlexbits(src, 4, 4, lenMantissa: 0));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            JxrEncoder.EncodeBd32FGrayscaleNoFlexbits(src, 4, 4, lenMantissa: 24));
    }

    [Fact]
    public void TransformsLong_FctIctAreExactInverse()
    {
        // Verify the int64 FCT/ICT round-trip at values that would overflow int32.
        Span<long> input = stackalloc long[16];
        for (var i = 0; i < 16; i++) input[i] = (long)i * 1_000_000_000L; // ~9.5 * 10^9, well past int32
        Span<long> work = stackalloc long[16];
        input.CopyTo(work);

        Transforms.FCT4x4(work);
        Transforms.ICT4x4(work);

        for (var i = 0; i < 16; i++)
            work[i].ShouldBe(input[i], $"i={i}");
    }
}
