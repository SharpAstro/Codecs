namespace SharpAstro.Jxr;

/// <summary>
/// File-level encode / decode facade that produces and consumes complete
/// <c>.jxr</c> byte streams (T.832 Annex A container around the codestream),
/// suitable for writing directly to disk or feeding to other JXR-capable tools.
/// </summary>
/// <remarks>
/// <para>For each supported sample format there's a paired
/// <c>Save<i>X</i>NoFlexbits</c> + <c>Load<i>X</i>NoFlexbits</c> method. The
/// underlying codestream uses the lossless NoFlexbits configuration from
/// <see cref="JxrEncoder"/> / <see cref="JxrDecoder"/>.</para>
/// <para>Optional metadata (ICC profile, XMP) can be passed at save time and
/// is reported back via the <see cref="JxrFile"/> at load time.</para>
/// </remarks>
public static class JxrFileFormatter
{
    // ---- 8-bit grayscale ----------------------------------------------------

    public static byte[] SaveBd8GrayscaleNoFlexbits(byte[] pixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
    {
        var codestream = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(pixels, width, height);
        return WrapInContainer((uint)width, (uint)height, JxrPixelFormat.Gray8Bpp, codestream, iccProfile, xmpMetadata);
    }

    public static byte[] LoadBd8GrayscaleNoFlexbits(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.Gray8Bpp);
        var pixels = JxrDecoder.DecodeBd8GrayscaleNoFlexbits(container.Codestream, out width, out height);
        return pixels;
    }

    // ---- 8-bit RGB ----------------------------------------------------------

    public static byte[] SaveBd8RgbNoFlexbits(byte[] pixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
    {
        var codestream = JxrEncoder.EncodeBd8RgbNoFlexbits(pixels, width, height);
        return WrapInContainer((uint)width, (uint)height, JxrPixelFormat.Rgb24Bpp, codestream, iccProfile, xmpMetadata);
    }

    public static byte[] LoadBd8RgbNoFlexbits(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.Rgb24Bpp);
        var pixels = JxrDecoder.DecodeBd8RgbNoFlexbits(container.Codestream, out width, out height);
        return pixels;
    }

    // ---- 16-bit grayscale ---------------------------------------------------

    public static byte[] SaveBd16GrayscaleNoFlexbits(ushort[] pixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
    {
        var codestream = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(pixels, width, height);
        return WrapInContainer((uint)width, (uint)height, JxrPixelFormat.Gray16Bpp, codestream, iccProfile, xmpMetadata);
    }

    public static ushort[] LoadBd16GrayscaleNoFlexbits(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.Gray16Bpp);
        var pixels = JxrDecoder.DecodeBd16GrayscaleNoFlexbits(container.Codestream, out width, out height);
        return pixels;
    }

    // ---- 16-bit RGB (HDR-master target) -------------------------------------

    public static byte[] SaveBd16RgbNoFlexbits(ushort[] pixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
    {
        var codestream = JxrEncoder.EncodeBd16RgbNoFlexbits(pixels, width, height);
        return WrapInContainer((uint)width, (uint)height, JxrPixelFormat.Rgb48Bpp, codestream, iccProfile, xmpMetadata);
    }

    public static ushort[] LoadBd16RgbNoFlexbits(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.Rgb48Bpp);
        var pixels = JxrDecoder.DecodeBd16RgbNoFlexbits(container.Codestream, out width, out height);
        return pixels;
    }

    // ---- BD16F grayscale (half-float) ---------------------------------------

    public static byte[] SaveBd16FGrayscaleNoFlexbits(ushort[] halfBits, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
    {
        var codestream = JxrEncoder.EncodeBd16FGrayscaleNoFlexbits(halfBits, width, height);
        return WrapInContainer((uint)width, (uint)height, JxrPixelFormat.GrayHalf16Bpp, codestream, iccProfile, xmpMetadata);
    }

    public static ushort[] LoadBd16FGrayscaleNoFlexbits(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.GrayHalf16Bpp);
        var pixels = JxrDecoder.DecodeBd16FGrayscaleNoFlexbits(container.Codestream, out width, out height);
        return pixels;
    }

    // ---- BD16F RGB (HDR-master target) --------------------------------------

    public static byte[] SaveBd16FRgbNoFlexbits(ushort[] halfBits, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
    {
        var codestream = JxrEncoder.EncodeBd16FRgbNoFlexbits(halfBits, width, height);
        return WrapInContainer((uint)width, (uint)height, JxrPixelFormat.RgbHalf48Bpp, codestream, iccProfile, xmpMetadata);
    }

    public static ushort[] LoadBd16FRgbNoFlexbits(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.RgbHalf48Bpp);
        var pixels = JxrDecoder.DecodeBd16FRgbNoFlexbits(container.Codestream, out width, out height);
        return pixels;
    }

    // -------------------------------------------------------------------------

    private static byte[] WrapInContainer(uint width, uint height, JxrPixelFormat pixelFormat,
        byte[] codestream, byte[]? iccProfile, byte[]? xmpMetadata)
    {
        var file = new JxrFile(
            Width: width,
            Height: height,
            PixelFormat: pixelFormat,
            Codestream: codestream,
            IccProfile: iccProfile,
            XmpMetadata: xmpMetadata);
        return JxrContainer.Write(file);
    }

    private static void EnsurePixelFormat(JxrFile container, JxrPixelFormat expected)
    {
        if (container.PixelFormat != expected)
            throw new NotSupportedException(
                $"Expected PixelFormat {expected}, got {container.PixelFormat} — " +
                "use the matching Load* method for the actual format");
    }
}
