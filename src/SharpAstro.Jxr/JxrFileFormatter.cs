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

    // ---- BD16F overloads taking System.Half directly -----------------------

    /// <summary>
    /// <see cref="SaveBd16FGrayscaleNoFlexbits(ushort[],int,int,byte[]?,byte[]?)"/>
    /// overload that accepts <see cref="Half"/> samples directly — useful when the
    /// caller's HDR pipeline already produces <c>Half[]</c>. The half-float bit
    /// patterns are forwarded unchanged.
    /// </summary>
    public static byte[] SaveBd16FGrayscaleNoFlexbits(Half[] pixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
        => SaveBd16FGrayscaleNoFlexbits(HalfArrayToUshort(pixels), width, height, iccProfile, xmpMetadata);

    /// <summary>
    /// Decode a BD16F grayscale codestream and return the samples as
    /// <see cref="Half"/> values for direct use in HDR pipelines.
    /// </summary>
    public static Half[] LoadBd16FGrayscaleNoFlexbitsAsHalf(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        var bits = LoadBd16FGrayscaleNoFlexbits(fileBytes, out width, out height, out container);
        return UshortArrayToHalf(bits);
    }

    /// <summary>
    /// <see cref="SaveBd16FRgbNoFlexbits(ushort[],int,int,byte[]?,byte[]?)"/>
    /// overload taking <see cref="Half"/> samples — full HDR-master deliverable
    /// shape directly from the calling pipeline.
    /// </summary>
    public static byte[] SaveBd16FRgbNoFlexbits(Half[] pixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null)
        => SaveBd16FRgbNoFlexbits(HalfArrayToUshort(pixels), width, height, iccProfile, xmpMetadata);

    /// <summary>
    /// Decode a BD16F RGB codestream and return interleaved <see cref="Half"/>
    /// R, G, B samples.
    /// </summary>
    public static Half[] LoadBd16FRgbNoFlexbitsAsHalf(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        var bits = LoadBd16FRgbNoFlexbits(fileBytes, out width, out height, out container);
        return UshortArrayToHalf(bits);
    }

    private static ushort[] HalfArrayToUshort(Half[] halves)
    {
        var result = new ushort[halves.Length];
        for (var i = 0; i < halves.Length; i++)
            result[i] = BitConverter.HalfToUInt16Bits(halves[i]);
        return result;
    }

    private static Half[] UshortArrayToHalf(ushort[] bits)
    {
        var result = new Half[bits.Length];
        for (var i = 0; i < bits.Length; i++)
            result[i] = BitConverter.UInt16BitsToHalf(bits[i]);
        return result;
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
