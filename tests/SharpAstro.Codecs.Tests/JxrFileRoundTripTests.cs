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
        BitConverter.HalfToUInt16Bits(decoded[1]).ShouldBe(BitConverter.HalfToUInt16Bits(src[1]));
        decoded[2].ShouldBe(Half.PositiveInfinity);
        decoded[3].ShouldBe(Half.NegativeInfinity);
        BitConverter.HalfToUInt16Bits(decoded[4]).ShouldBe(BitConverter.HalfToUInt16Bits(src[4]));
        decoded[5].ShouldBe(src[5]);
    }

    // ----------------------------------------------------------------------
    // Realistic-size stress test — closest to actual astro file shape.
    // ----------------------------------------------------------------------

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
