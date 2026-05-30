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
    /// into a <c>.jxr</c> byte stream. Arbitrary dimensions are allowed (partial macroblocks
    /// edge-replicated). QP indices default to 0 (lossless). <paramref name="overlap"/> is the Photo
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
    /// any positive size (partial macroblocks edge-replicated). QP indices default to 0 (lossless).
    /// <paramref name="overlap"/> is the Photo Overlap level (0 = none, 1 = jxrlib's default, 2 = two levels).
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
    /// Arbitrary dimensions are allowed (partial macroblocks edge-replicated); QP indices default to 0 (lossless).
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
    /// stream — YCoCg-R + InternalClrFmt=YUV444, full-precision lossless (SHIFT_BITS 0). Arbitrary
    /// dimensions are allowed (partial macroblocks edge-replicated); QP indices default to 0 (lossless).
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

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD32F</b> grayscale (mono)
    /// image — the HDR headline. Floats are written <b>verbatim, not normalized</b> (post-stretch
    /// star cores may overshoot 1.0; raw FITS data spans tens of thousands). <paramref name="lenMantissa"/>
    /// is the stored mantissa-bit count (jxrlib default 13; astrophotography commonly uses 8 to trade
    /// precision for size) and <paramref name="expBias"/> the exponent bias (jxrlib default 4). BD32F
    /// is mono-only by design (T.832 has no Table A.6 GUID for BD32F RGB). Arbitrary dimensions are
    /// allowed (partial macroblocks edge-replicated); the codec is lossless on the float-pixel representation (QP 0, OL_NONE by default).
    /// </summary>
    public static byte[] EncodeGrayF32(ReadOnlySpan<float> y, int width, int height,
                                       int lenMantissa = 13, int expBias = 4,
                                       int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        var codestream = JxrCodestream.EncodeGrayF32(y, width, height, lenMantissa, expBias, qpDc, qpLp, qpHp, overlap);
        var file = new JxrFile(
            Width: (uint)width,
            Height: (uint)height,
            PixelFormat: JxrPixelFormat.GrayFloat32Bpp,
            Codestream: codestream);
        return JxrContainer.Write(file);
    }

    /// <summary>Decode a <c>.jxr</c> file produced by <see cref="EncodeGrayF32"/> (or jxrlib in the
    /// matching SPATIAL Y-only BD32F configuration) back into a grayscale float channel.</summary>
    public static (int width, int height, float[] y) DecodeGrayF32(ReadOnlySpan<byte> jxr)
    {
        var file = JxrContainer.Read(jxr);
        return JxrCodestream.DecodeGrayF32(file.Codestream);
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD16F</b> half-float grayscale
    /// image (<c>width*height</c> halves, raster order) into a <c>.jxr</c> byte stream. The half is
    /// preserved bit-exact (no mantissa quantization). Arbitrary dimensions are allowed (partial macroblocks edge-replicated); lossless.
    /// </summary>
    public static byte[] EncodeGrayF16(ReadOnlySpan<Half> y, int width, int height,
                                       int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        var codestream = JxrCodestream.EncodeGrayHalf(y, width, height, qpDc, qpLp, qpHp, overlap);
        var file = new JxrFile(
            Width: (uint)width,
            Height: (uint)height,
            PixelFormat: JxrPixelFormat.GrayHalf16Bpp,
            Codestream: codestream);
        return JxrContainer.Write(file);
    }

    /// <summary>Decode a <c>.jxr</c> file produced by <see cref="EncodeGrayF16"/> back into a half-float grayscale channel.</summary>
    public static (int width, int height, Half[] y) DecodeGrayF16(ReadOnlySpan<byte> jxr)
    {
        var file = JxrContainer.Read(jxr);
        return JxrCodestream.DecodeGrayHalf(file.Codestream);
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD16F</b> half-float RGB image
    /// from an <b>interleaved</b> <c>Half[width*height*3]</c> (RGBRGB…) — the consumer's HDR RGB
    /// shape — into a <c>.jxr</c> byte stream (YCoCg-R + InternalClrFmt=YUV444, bit-exact). Arbitrary
    /// dimensions are allowed (partial macroblocks edge-replicated); lossless.
    /// </summary>
    public static byte[] EncodeRgbF16(ReadOnlySpan<Half> rgb, int width, int height,
                                      int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        int n = width * height;
        if (rgb.Length < n * 3) throw new ArgumentException("Interleaved RGB half buffer must hold width*height*3 samples.", nameof(rgb));
        var (r, g, b) = (new Half[n], new Half[n], new Half[n]);
        for (var i = 0; i < n; i++) { r[i] = rgb[i * 3]; g[i] = rgb[i * 3 + 1]; b[i] = rgb[i * 3 + 2]; }

        var codestream = JxrCodestream.EncodeRgbHalf(r, g, b, width, height, qpDc, qpLp, qpHp, overlap);
        var file = new JxrFile(
            Width: (uint)width,
            Height: (uint)height,
            PixelFormat: JxrPixelFormat.RgbHalf48Bpp,
            Codestream: codestream);
        return JxrContainer.Write(file);
    }

    /// <summary>Decode a <c>.jxr</c> file produced by <see cref="EncodeRgbF16"/> into an interleaved
    /// <c>Half[width*height*3]</c> (RGBRGB…) buffer.</summary>
    public static (int width, int height, Half[] rgb) DecodeRgbF16(ReadOnlySpan<byte> jxr)
    {
        var file = JxrContainer.Read(jxr);
        var (w, h, r, g, b) = JxrCodestream.DecodeRgbHalf(file.Codestream);
        int n = w * h;
        var rgb = new Half[n * 3];
        for (var i = 0; i < n; i++) { rgb[i * 3] = r[i]; rgb[i * 3 + 1] = g[i]; rgb[i * 3 + 2] = b[i]; }
        return (w, h, rgb);
    }
}
