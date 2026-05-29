using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// End-to-end round-trip tests at the <c>.jxr</c> file level —
/// <see cref="JxrFileFormatter"/> wraps the lossless codestream in the
/// T.832 Annex A container so other tools can decode the result. Every
/// format covered by <see cref="JxrEncoder"/> gets a corresponding file
/// round-trip test here.
/// </summary>
public sealed class JxrFileRoundTripTests
{
    [Fact]
    public void Bd8Grayscale_FileRoundTrip()
    {
        var src = new byte[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)(i & 0xFF);

        var fileBytes = JxrFileFormatter.SaveBd8GrayscaleNoFlexbits(src, 16, 16);
        // Container magic is the TIFF II marker (0x49, 0x49) followed by
        // the JXR-specific version (0xBC, 0x01) — quick sanity that we
        // produced a valid Annex A wrapper.
        fileBytes[0].ShouldBe((byte)'I');
        fileBytes[1].ShouldBe((byte)'I');
        fileBytes[2].ShouldBe((byte)0xBC);
        fileBytes[3].ShouldBe((byte)0x01);

        var decoded = JxrFileFormatter.LoadBd8GrayscaleNoFlexbits(fileBytes, out var w, out var h, out var container);
        w.ShouldBe(16);
        h.ShouldBe(16);
        container.PixelFormat.ShouldBe(JxrPixelFormat.Gray8Bpp);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i]);
    }

    [Fact]
    public void Bd8Rgb_FileRoundTrip()
    {
        var rng = new Random(unchecked((int)0xDD12FE34));
        var src = new byte[32 * 32 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)rng.Next(0, 256);

        var fileBytes = JxrFileFormatter.SaveBd8RgbNoFlexbits(src, 32, 32);
        var decoded = JxrFileFormatter.LoadBd8RgbNoFlexbits(fileBytes, out var w, out var h, out var container);
        w.ShouldBe(32);
        h.ShouldBe(32);
        container.PixelFormat.ShouldBe(JxrPixelFormat.Rgb24Bpp);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i]);
    }

    [Fact]
    public void Bd16Grayscale_FileRoundTrip()
    {
        var rng = new Random(unchecked((int)0xEEDDCC11));
        var src = new ushort[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var fileBytes = JxrFileFormatter.SaveBd16GrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrFileFormatter.LoadBd16GrayscaleNoFlexbits(fileBytes, out var w, out var h, out var container);
        w.ShouldBe(16);
        h.ShouldBe(16);
        container.PixelFormat.ShouldBe(JxrPixelFormat.Gray16Bpp);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i]);
    }

    [Fact]
    public void Bd16Rgb_FileRoundTrip_HdrTarget()
    {
        // This is the SharpAstro HDR-master deliverable shape: BD16 Rgb in
        // a proper .jxr file readable by external tools.
        var rng = new Random(unchecked((int)0xFE12AB34));
        var src = new ushort[32 * 32 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var fileBytes = JxrFileFormatter.SaveBd16RgbNoFlexbits(src, 32, 32);
        var decoded = JxrFileFormatter.LoadBd16RgbNoFlexbits(fileBytes, out var w, out var h, out var container);
        w.ShouldBe(32);
        h.ShouldBe(32);
        container.PixelFormat.ShouldBe(JxrPixelFormat.Rgb48Bpp);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i]);
    }

    [Fact]
    public void Bd16FGrayscale_FileRoundTrip()
    {
        var rng = new Random(unchecked((int)0x11223344));
        var src = new ushort[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var fileBytes = JxrFileFormatter.SaveBd16FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrFileFormatter.LoadBd16FGrayscaleNoFlexbits(fileBytes, out var w, out var h, out var container);
        w.ShouldBe(16);
        h.ShouldBe(16);
        container.PixelFormat.ShouldBe(JxrPixelFormat.GrayHalf16Bpp);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i]);
    }

    [Fact]
    public void Bd16FRgb_FileRoundTrip_HdrFloatTarget()
    {
        // Half-float RGB HDR-master deliverable.
        var rng = new Random(unchecked((int)0x55667788));
        var src = new ushort[32 * 32 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var fileBytes = JxrFileFormatter.SaveBd16FRgbNoFlexbits(src, 32, 32);
        var decoded = JxrFileFormatter.LoadBd16FRgbNoFlexbits(fileBytes, out var w, out var h, out var container);
        w.ShouldBe(32);
        h.ShouldBe(32);
        container.PixelFormat.ShouldBe(JxrPixelFormat.RgbHalf48Bpp);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i]);
    }

    [Fact]
    public void Bd32FGrayscale_FileRoundTrip()
    {
        // BD32F grayscale via the file-level wrapper. LEN_MANTISSA = 8 default
        // round-trips bit-exact for floats with ≤ 8 bits of mantissa precision.
        var src = new float[16 * 16];
        var rng = new Random(unchecked((int)0xB32FF11E));
        for (var i = 0; i < src.Length; i++)
        {
            var sign = rng.Next(0, 2);
            var exp = rng.Next(120, 131);
            var mant8 = rng.Next(0, 256);
            var raw = (uint)((sign << 31) | (exp << 23) | (mant8 << 15));
            src[i] = BitConverter.UInt32BitsToSingle(raw);
        }

        var fileBytes = JxrFileFormatter.SaveBd32FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrFileFormatter.LoadBd32FGrayscaleNoFlexbits(fileBytes, out var w, out var h, out var container);
        w.ShouldBe(16);
        h.ShouldBe(16);
        container.PixelFormat.ShouldBe(JxrPixelFormat.GrayFloat32Bpp);
        for (var i = 0; i < src.Length; i++) decoded[i].ShouldBe(src[i], $"i={i}");
    }

    [Fact]
    public void IccProfile_RoundTrip()
    {
        // ICC blob attached at save time must survive the round-trip.
        var src = new byte[16 * 16 * 3];
        var icc = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42, 0x42, 0x42, 0x42 };

        var fileBytes = JxrFileFormatter.SaveBd8RgbNoFlexbits(src, 16, 16, iccProfile: icc);
        JxrFileFormatter.LoadBd8RgbNoFlexbits(fileBytes, out _, out _, out var container);

        container.IccProfile.ShouldNotBeNull();
        container.IccProfile!.ShouldBe(icc);
    }

    [Fact]
    public void XmpMetadata_RoundTrip()
    {
        var src = new ushort[16 * 16 * 3];
        var xmp = "<?xpacket begin=\"\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">test</x:xmpmeta>"u8.ToArray();

        var fileBytes = JxrFileFormatter.SaveBd16RgbNoFlexbits(src, 16, 16, xmpMetadata: xmp);
        JxrFileFormatter.LoadBd16RgbNoFlexbits(fileBytes, out _, out _, out var container);

        container.XmpMetadata.ShouldNotBeNull();
        container.XmpMetadata!.ShouldBe(xmp);
    }

    [Fact]
    public void MismatchedPixelFormat_ThrowsOnLoad()
    {
        // Save as BD8 grayscale, attempt to load as BD16 RGB — should fail loudly.
        var src = new byte[16 * 16];
        var fileBytes = JxrFileFormatter.SaveBd8GrayscaleNoFlexbits(src, 16, 16);

        var threw = false;
        try
        {
            JxrFileFormatter.LoadBd16RgbNoFlexbits(fileBytes, out _, out _, out _);
        }
        catch (NotSupportedException) { threw = true; }
        threw.ShouldBeTrue();
    }

    // ----------------------------------------------------------------------
    // System.Half overloads — the natural API shape for HDR float pipelines.
    // ----------------------------------------------------------------------

    [Fact]
    public void Half_GrayscaleRoundTrip()
    {
        var src = new Half[16 * 16];
        // Mix of typical HDR values: 0, 1, 2, 0.5, large, small.
        var samples = new[] { (Half)0f, (Half)1f, (Half)2f, (Half)0.5f, (Half)100f, (Half)0.001f };
        for (var i = 0; i < src.Length; i++) src[i] = samples[i % samples.Length];

        var bytes = JxrFileFormatter.SaveBd16FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrFileFormatter.LoadBd16FGrayscaleNoFlexbitsAsHalf(bytes, out var w, out var h, out _);
        w.ShouldBe(16);
        h.ShouldBe(16);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"pixel {i}");
    }

    [Fact]
    public void Half_RgbRoundTrip_HdrPipelineShape()
    {
        // Closest to what an actual HDR astro pipeline would emit:
        // half-float RGB radiance values with mixed magnitudes.
        var rng = new Random(unchecked((int)0xDDEEFF11));
        var src = new Half[32 * 32 * 3];
        for (var i = 0; i < src.Length; i++)
            src[i] = (Half)((rng.NextSingle() - 0.5f) * 100f);

        var bytes = JxrFileFormatter.SaveBd16FRgbNoFlexbits(src, 32, 32);
        var decoded = JxrFileFormatter.LoadBd16FRgbNoFlexbitsAsHalf(bytes, out var w, out var h, out _);
        w.ShouldBe(32);
        h.ShouldBe(32);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Half_PreservesSpecialValues()
    {
        // ±0, ±Inf, NaN, denormals — the integer pipeline preserves bit patterns
        // so these must all round-trip exactly even though FCT arithmetic doesn't
        // "understand" the float meaning.
        var src = new Half[16 * 16];
        src[0] = (Half)0f;
        src[1] = -(Half)0f;
        src[2] = Half.PositiveInfinity;
        src[3] = Half.NegativeInfinity;
        src[4] = Half.NaN;
        src[5] = (Half)6.0e-8f; // smallest normal half
        // Fill rest with 1.0 to keep AC content non-zero.
        for (var i = 6; i < src.Length; i++) src[i] = (Half)1f;

        var bytes = JxrFileFormatter.SaveBd16FGrayscaleNoFlexbits(src, 16, 16);
        var decoded = JxrFileFormatter.LoadBd16FGrayscaleNoFlexbitsAsHalf(bytes, out _, out _, out _);

        // For ±0 and NaN we need to compare bit patterns rather than values
        // (NaN != NaN by IEEE rules).
        BitConverter.HalfToUInt16Bits(decoded[0]).ShouldBe(BitConverter.HalfToUInt16Bits(src[0]));
        // -0 collapses to +0 — jxrlib's forwardHalf treats sign-magnitude
        // conversion as dropping the sign bit on a zero magnitude. WIC and
        // every other JXR decoder will do the same, so this is the spec
        // behaviour rather than a precision loss specific to our path.
        BitConverter.HalfToUInt16Bits(decoded[1]).ShouldBe((ushort)0x0000);
        decoded[2].ShouldBe(Half.PositiveInfinity);
        decoded[3].ShouldBe(Half.NegativeInfinity);
        BitConverter.HalfToUInt16Bits(decoded[4]).ShouldBe(BitConverter.HalfToUInt16Bits(src[4]));
        decoded[5].ShouldBe(src[5]);
    }

    // ----------------------------------------------------------------------
    // Realistic-size stress test — closest to actual astro file shape.
    // ----------------------------------------------------------------------

    // ----------------------------------------------------------------------
    // Tiled file-level round-trip — multi-tile JXR files with the tile grid
    // exposed at the JxrFileFormatter API.
    // ----------------------------------------------------------------------

    [Fact]
    public void Tiled_Bd16Rgb_FileRoundTrip()
    {
        // 64×64 BD16 RGB random content into a 2×2 tile grid, wrapped in a real
        // .jxr container. Other JXR-aware tools should be able to decode this.
        var rng = new Random(unchecked((int)0xDEFA17ED));
        var src = new ushort[64 * 64 * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var layout = JxrTileLayout.Uniform(totalWidthInMb: 4, totalHeightInMb: 4, cols: 2, rows: 2);
        var fileBytes = JxrFileFormatter.SaveBd16RgbNoFlexbits(src, 64, 64, tiling: layout);
        var decoded = JxrFileFormatter.LoadBd16RgbNoFlexbits(fileBytes, out var w, out var h, out var container);

        w.ShouldBe(64);
        h.ShouldBe(64);
        container.PixelFormat.ShouldBe(JxrPixelFormat.Rgb48Bpp);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    [Fact]
    public void Tiled_Bd16FRgb_HalfApi_FileRoundTrip()
    {
        // Closest end-to-end shape to the user's HDR-master pipeline:
        // Half[] in, multi-tile half-float JXR file out, Half[] back.
        var rng = new Random(unchecked((int)0xCAFED00D));
        var src = new Half[64 * 64 * 3];
        for (var i = 0; i < src.Length; i++)
            src[i] = (Half)((rng.NextSingle() - 0.5f) * 50f);

        var layout = JxrTileLayout.Uniform(4, 4, cols: 2, rows: 2);
        var fileBytes = JxrFileFormatter.SaveBd16FRgbNoFlexbits(src, 64, 64, tiling: layout);
        var decoded = JxrFileFormatter.LoadBd16FRgbNoFlexbitsAsHalf(fileBytes, out var w, out var h, out var container);

        w.ShouldBe(64);
        h.ShouldBe(64);
        container.PixelFormat.ShouldBe(JxrPixelFormat.RgbHalf48Bpp);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }

    // ----------------------------------------------------------------------
    // Alpha-plane round-trip — RGB + separate alpha codestream wrapped in a
    // real container, matching the JXR convention for Rgba64Bpp /
    // RgbaHalf64Bpp / Bgra32Bpp pixel formats.
    // ----------------------------------------------------------------------

    [Fact]
    public void Bd16RgbWithAlpha_FileRoundTrip()
    {
        // 32×32 BD16 RGBA: separate RGB + alpha arrays in, real .jxr file out,
        // both arrays back via the load helper. The seagull-nebula shape (minus
        // the frequency-mode codestream layout we don't yet support).
        var rng = new Random(unchecked((int)0xA17D8A1A));
        var rgb = new ushort[32 * 32 * 3];
        var alpha = new ushort[32 * 32];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (ushort)rng.Next(0, 65536);
        for (var i = 0; i < alpha.Length; i++) alpha[i] = (ushort)rng.Next(0, 65536);

        var fileBytes = JxrFileFormatter.SaveBd16RgbWithAlphaNoFlexbits(rgb, alpha, 32, 32);
        var (decodedRgb, decodedAlpha) = JxrFileFormatter.LoadBd16RgbWithAlphaNoFlexbits(
            fileBytes, out var w, out var h, out var container);

        w.ShouldBe(32);
        h.ShouldBe(32);
        container.PixelFormat.ShouldBe(JxrPixelFormat.Rgba64Bpp);
        container.AlphaCodestream.ShouldNotBeNull();

        for (var i = 0; i < rgb.Length; i++)
            decodedRgb[i].ShouldBe(rgb[i], $"rgb sample {i}");
        for (var i = 0; i < alpha.Length; i++)
            decodedAlpha[i].ShouldBe(alpha[i], $"alpha sample {i}");
    }

    [Fact]
    public void Bd16FRgbWithAlpha_HalfApi_FileRoundTrip()
    {
        // Full HDR-with-alpha shape: half-float RGB + half-float alpha.
        var rng = new Random(unchecked((int)0xA1A1A1A1));
        var rgb = new Half[16 * 16 * 3];
        var alpha = new Half[16 * 16];
        for (var i = 0; i < rgb.Length; i++)
            rgb[i] = (Half)((rng.NextSingle() - 0.5f) * 10f);
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = (Half)rng.NextSingle();

        var fileBytes = JxrFileFormatter.SaveBd16FRgbWithAlphaNoFlexbits(rgb, alpha, 16, 16);
        var (decodedRgb, decodedAlpha) = JxrFileFormatter.LoadBd16FRgbWithAlphaNoFlexbitsAsHalf(
            fileBytes, out var w, out var h, out var container);

        w.ShouldBe(16);
        h.ShouldBe(16);
        container.PixelFormat.ShouldBe(JxrPixelFormat.RgbaHalf64Bpp);

        for (var i = 0; i < rgb.Length; i++)
            decodedRgb[i].ShouldBe(rgb[i], $"rgb sample {i}");
        for (var i = 0; i < alpha.Length; i++)
            decodedAlpha[i].ShouldBe(alpha[i], $"alpha sample {i}");
    }

    [Fact]
    public void RgbWithAlpha_Tiled_RoundTrips()
    {
        // Alpha + multi-tile compose: encoder applies the same tile grid to
        // both the primary and alpha codestreams.
        var rng = new Random(unchecked((int)0x71735071));
        var rgb = new ushort[64 * 64 * 3];
        var alpha = new ushort[64 * 64];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (ushort)rng.Next(0, 65536);
        for (var i = 0; i < alpha.Length; i++) alpha[i] = (ushort)rng.Next(0, 65536);

        var layout = JxrTileLayout.Uniform(4, 4, cols: 2, rows: 2);
        var fileBytes = JxrFileFormatter.SaveBd16RgbWithAlphaNoFlexbits(rgb, alpha, 64, 64, tiling: layout);
        var (decodedRgb, decodedAlpha) = JxrFileFormatter.LoadBd16RgbWithAlphaNoFlexbits(
            fileBytes, out _, out _, out _);

        for (var i = 0; i < rgb.Length; i++) decodedRgb[i].ShouldBe(rgb[i], $"rgb {i}");
        for (var i = 0; i < alpha.Length; i++) decodedAlpha[i].ShouldBe(alpha[i], $"a {i}");
    }

    [Fact]
    public void LargeBd16Rgb_512x512_RoundTrips()
    {
        // 0.75 MPix BD16 RGB — exercise the pipeline at a size representative
        // of mid-resolution astro crops. Not the full 9 MPix the user's pipeline
        // produces, but enough to catch any size-dependent bugs without making
        // the test suite slow.
        const int size = 512;
        var rng = new Random(unchecked((int)0x51251251));
        var src = new ushort[size * size * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var fileBytes = JxrFileFormatter.SaveBd16RgbNoFlexbits(src, size, size);
        var decoded = JxrFileFormatter.LoadBd16RgbNoFlexbits(fileBytes, out var w, out var h, out _);
        w.ShouldBe(size);
        h.ShouldBe(size);
        for (var i = 0; i < src.Length; i++)
            decoded[i].ShouldBe(src[i], $"sample {i}");
    }
}
