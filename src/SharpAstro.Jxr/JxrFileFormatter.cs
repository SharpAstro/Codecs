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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
    {
        var codestream = JxrEncoder.EncodeBd8GrayscaleNoFlexbits(pixels, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
    {
        var codestream = JxrEncoder.EncodeBd8RgbNoFlexbits(pixels, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
    {
        var codestream = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(pixels, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
    {
        var codestream = JxrEncoder.EncodeBd16RgbNoFlexbits(pixels, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
    {
        var codestream = JxrEncoder.EncodeBd16FGrayscaleNoFlexbits(halfBits, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
    {
        var codestream = JxrEncoder.EncodeBd16FRgbNoFlexbits(halfBits, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
        => SaveBd16FGrayscaleNoFlexbits(HalfArrayToUshort(pixels), width, height, iccProfile, xmpMetadata, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);

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
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false)
        => SaveBd16FRgbNoFlexbits(HalfArrayToUshort(pixels), width, height, iccProfile, xmpMetadata, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);

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

    // ---- BD32F (single-precision float) ------------------------------------

    /// <summary>
    /// Save a 32-bit float grayscale image as a real <c>.jxr</c> file.
    /// See <see cref="JxrEncoder.EncodeBd32FGrayscaleNoFlexbits"/> for the
    /// LEN_MANTISSA precision tradeoff.
    /// </summary>
    public static byte[] SaveBd32FGrayscaleNoFlexbits(float[] pixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1, int overlapMode = 0,
        bool frequencyMode = false, byte lenMantissa = 8)
    {
        var codestream = JxrEncoder.EncodeBd32FGrayscaleNoFlexbits(pixels, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode, lenMantissa);
        return WrapInContainer((uint)width, (uint)height, JxrPixelFormat.GrayFloat32Bpp, codestream, iccProfile, xmpMetadata);
    }

    public static float[] LoadBd32FGrayscaleNoFlexbits(ReadOnlySpan<byte> fileBytes,
        out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.GrayFloat32Bpp);
        return JxrDecoder.DecodeBd32FGrayscaleNoFlexbits(container.Codestream, out width, out height);
    }

    // BD32F RGB has no T.832 Table A.6 pixel format GUID for 3-channel float32
    // (only 4-channel RgbFloat128Bpp / RgbaFloat128Bpp / PrgbaFloat128Bpp), so
    // we don't expose an Rgb file-level wrapper here. Callers that want
    // BD32F RGB can use JxrEncoder.EncodeBd32FRgbNoFlexbits directly to get a
    // raw codestream and pick their own container pixel format.

    // ---- Alpha-plane support: RGB + separate alpha codestream ----------------
    //
    // The JXR convention for "RGBA" pixel formats (Rgba64Bpp / RgbaHalf64Bpp /
    // Bgra32Bpp) is to encode the colour channels in the primary codestream and
    // the alpha channel in a separate one. The container holds both and tags
    // the file with the RGBA pixel-format GUID so external decoders know the
    // logical layout.

    /// <summary>
    /// Save 16-bit RGB + alpha as a real <c>.jxr</c> file. <paramref name="rgbPixels"/>
    /// is <c>w × h × 3</c> interleaved RGB ushorts; <paramref name="alphaPixels"/>
    /// is <c>w × h</c> ushorts. The output container has
    /// <see cref="JxrPixelFormat.Rgba64Bpp"/>.
    /// </summary>
    public static byte[] SaveBd16RgbWithAlphaNoFlexbits(
        ushort[] rgbPixels, ushort[] alphaPixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false)
    {
        var primary = JxrEncoder.EncodeBd16RgbNoFlexbits(rgbPixels, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
        var alpha = JxrEncoder.EncodeBd16GrayscaleNoFlexbits(alphaPixels, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
        return WrapWithAlpha((uint)width, (uint)height, JxrPixelFormat.Rgba64Bpp, primary, alpha, iccProfile, xmpMetadata);
    }

    /// <summary>Inverse of <see cref="SaveBd16RgbWithAlphaNoFlexbits"/> — returns separate RGB + alpha arrays.</summary>
    public static (ushort[] rgb, ushort[] alpha) LoadBd16RgbWithAlphaNoFlexbits(
        ReadOnlySpan<byte> fileBytes, out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.Rgba64Bpp);
        if (container.AlphaCodestream is null)
            throw new InvalidDataException("Rgba64Bpp file is missing its alpha codestream");

        var rgb = JxrDecoder.DecodeBd16RgbNoFlexbits(container.Codestream, out width, out height);
        var alpha = JxrDecoder.DecodeBd16GrayscaleNoFlexbits(container.AlphaCodestream, out _, out _);
        return (rgb, alpha);
    }

    /// <summary>
    /// Save 16-bit half-float RGB + alpha as a real <c>.jxr</c> file — full
    /// HDR-master shape with alpha for compositing workflows.
    /// </summary>
    public static byte[] SaveBd16FRgbWithAlphaNoFlexbits(
        ushort[] rgbHalfBits, ushort[] alphaHalfBits, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false)
    {
        var primary = JxrEncoder.EncodeBd16FRgbNoFlexbits(rgbHalfBits, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
        var alpha = JxrEncoder.EncodeBd16FGrayscaleNoFlexbits(alphaHalfBits, width, height, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);
        return WrapWithAlpha((uint)width, (uint)height, JxrPixelFormat.RgbaHalf64Bpp, primary, alpha, iccProfile, xmpMetadata);
    }

    /// <summary>Inverse of <see cref="SaveBd16FRgbWithAlphaNoFlexbits"/>.</summary>
    public static (ushort[] rgb, ushort[] alpha) LoadBd16FRgbWithAlphaNoFlexbits(
        ReadOnlySpan<byte> fileBytes, out int width, out int height, out JxrFile container)
    {
        container = JxrContainer.Read(fileBytes);
        EnsurePixelFormat(container, JxrPixelFormat.RgbaHalf64Bpp);
        if (container.AlphaCodestream is null)
            throw new InvalidDataException("RgbaHalf64Bpp file is missing its alpha codestream");

        var rgb = JxrDecoder.DecodeBd16FRgbNoFlexbits(container.Codestream, out width, out height);
        var alpha = JxrDecoder.DecodeBd16FGrayscaleNoFlexbits(container.AlphaCodestream, out _, out _);
        return (rgb, alpha);
    }

    /// <summary>Half-typed overload for BD16F RGBA — accepts and returns <see cref="Half"/>[].</summary>
    public static byte[] SaveBd16FRgbWithAlphaNoFlexbits(
        Half[] rgbPixels, Half[] alphaPixels, int width, int height,
        byte[]? iccProfile = null, byte[]? xmpMetadata = null, JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false)
        => SaveBd16FRgbWithAlphaNoFlexbits(
            HalfArrayToUshort(rgbPixels), HalfArrayToUshort(alphaPixels),
            width, height, iccProfile, xmpMetadata, tiling, dcQp, lpQp, hpQp, overlapMode, frequencyMode);

    /// <summary>Half-typed decode pair for BD16F RGBA.</summary>
    public static (Half[] rgb, Half[] alpha) LoadBd16FRgbWithAlphaNoFlexbitsAsHalf(
        ReadOnlySpan<byte> fileBytes, out int width, out int height, out JxrFile container)
    {
        var (rgbBits, alphaBits) = LoadBd16FRgbWithAlphaNoFlexbits(fileBytes, out width, out height, out container);
        return (UshortArrayToHalf(rgbBits), UshortArrayToHalf(alphaBits));
    }

    private static byte[] WrapWithAlpha(uint width, uint height, JxrPixelFormat pixelFormat,
        byte[] primaryCodestream, byte[] alphaCodestream, byte[]? iccProfile, byte[]? xmpMetadata)
    {
        var file = new JxrFile(
            Width: width,
            Height: height,
            PixelFormat: pixelFormat,
            Codestream: primaryCodestream,
            AlphaCodestream: alphaCodestream,
            IccProfile: iccProfile,
            XmpMetadata: xmpMetadata);
        return JxrContainer.Write(file);
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
