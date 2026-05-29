namespace SharpAstro.Jxr;

/// <summary>
/// Top-level façade: BD8 RGB pixels ⟷ a complete <c>.jxr</c> file. Wraps the
/// SPATIAL OL_NONE YUV444 codestream (<see cref="JxrCodestream"/>) in the
/// tag-based container (<see cref="JxrContainer"/>) with a 24bpp-RGB pixel
/// format, producing files that open in jxrlib's <c>JxrDecApp</c> and Windows
/// Photo / WIC.
/// </summary>
public static class JxrImageCodec
{
    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> BD8 RGB image
    /// (each channel <c>width*height</c> samples in raster order, values 0..255)
    /// into a <c>.jxr</c> byte stream. Dimensions must be multiples of 16. QP
    /// indices default to 0 (lossless). <paramref name="overlap"/> is the Photo
    /// Overlap level (0 = none, 1 = one level — jxrlib's default, 2 = two levels).
    /// </summary>
    public static byte[] EncodeRgb24(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
                                     int width, int height, int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        var codestream = JxrCodestream.Encode(r, g, b, width, height, qpDc, qpLp, qpHp, overlap);
        var file = new JxrFile(
            Width: (uint)width,
            Height: (uint)height,
            PixelFormat: JxrPixelFormat.Rgb24Bpp,
            Codestream: codestream);
        return JxrContainer.Write(file);
    }

    /// <summary>Decode a <c>.jxr</c> file produced by <see cref="EncodeRgb24"/> (or jxrlib in the
    /// matching SPATIAL OL_NONE YUV444 BD8 configuration) back into BD8 RGB channels.</summary>
    public static (int width, int height, int[] r, int[] g, int[] b) DecodeRgb24(ReadOnlySpan<byte> jxr)
    {
        var file = JxrContainer.Read(jxr);
        return JxrCodestream.Decode(file.Codestream);
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> BD8 grayscale image
    /// (<c>width*height</c> samples in raster order, values 0..255) into a <c>.jxr</c> byte
    /// stream — a single-channel Y-only codestream (no colour transform). Dimensions must be
    /// multiples of 16. QP indices default to 0 (lossless). <paramref name="overlap"/> is the
    /// Photo Overlap level (0 = none, 1 = jxrlib's default, 2 = two levels).
    /// </summary>
    public static byte[] EncodeGray8(ReadOnlySpan<int> y, int width, int height,
                                     int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        var codestream = JxrCodestream.EncodeGray(y, width, height, qpDc, qpLp, qpHp, overlap);
        var file = new JxrFile(
            Width: (uint)width,
            Height: (uint)height,
            PixelFormat: JxrPixelFormat.Gray8Bpp,
            Codestream: codestream);
        return JxrContainer.Write(file);
    }

    /// <summary>Decode a <c>.jxr</c> file produced by <see cref="EncodeGray8"/> (or jxrlib in the
    /// matching SPATIAL Y-only BD8 configuration) back into a BD8 grayscale channel.</summary>
    public static (int width, int height, int[] y) DecodeGray8(ReadOnlySpan<byte> jxr)
    {
        var file = JxrContainer.Read(jxr);
        return JxrCodestream.DecodeGray(file.Codestream);
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD16</b> grayscale image
    /// (<c>width*height</c> samples in raster order, values 0..65535) into a <c>.jxr</c> byte
    /// stream — a single-channel Y-only codestream, full-precision lossless (SHIFT_BITS 0).
    /// Dimensions must be multiples of 16; QP indices default to 0 (lossless).
    /// </summary>
    public static byte[] EncodeGray16(ReadOnlySpan<int> y, int width, int height,
                                      int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        var codestream = JxrCodestream.EncodeGray(y, width, height, qpDc, qpLp, qpHp, overlap, JxrOutputBitDepth.Bd16);
        var file = new JxrFile(
            Width: (uint)width,
            Height: (uint)height,
            PixelFormat: JxrPixelFormat.Gray16Bpp,
            Codestream: codestream);
        return JxrContainer.Write(file);
    }

    /// <summary>Decode a <c>.jxr</c> file produced by <see cref="EncodeGray16"/> (or jxrlib in the
    /// matching SPATIAL Y-only BD16 configuration) back into a BD16 grayscale channel (0..65535).</summary>
    public static (int width, int height, int[] y) DecodeGray16(ReadOnlySpan<byte> jxr)
    {
        var file = JxrContainer.Read(jxr);
        return JxrCodestream.DecodeGray(file.Codestream);
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD16</b> RGB image (each
    /// channel <c>width*height</c> samples, raster order, values 0..65535) into a <c>.jxr</c> byte
    /// stream — YCoCg-R + InternalClrFmt=YUV444, full-precision lossless (SHIFT_BITS 0). Dimensions
    /// must be multiples of 16; QP indices default to 0 (lossless).
    /// </summary>
    public static byte[] EncodeRgb48(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
                                     int width, int height, int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        var codestream = JxrCodestream.Encode(r, g, b, width, height, qpDc, qpLp, qpHp, overlap, JxrOutputBitDepth.Bd16);
        var file = new JxrFile(
            Width: (uint)width,
            Height: (uint)height,
            PixelFormat: JxrPixelFormat.Rgb48Bpp,
            Codestream: codestream);
        return JxrContainer.Write(file);
    }

    /// <summary>Decode a <c>.jxr</c> file produced by <see cref="EncodeRgb48"/> (or jxrlib in the
    /// matching SPATIAL YUV444 BD16 configuration) back into BD16 RGB channels (0..65535).</summary>
    public static (int width, int height, int[] r, int[] g, int[] b) DecodeRgb48(ReadOnlySpan<byte> jxr)
    {
        var file = JxrContainer.Read(jxr);
        return JxrCodestream.Decode(file.Codestream);
    }
}
