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
}
