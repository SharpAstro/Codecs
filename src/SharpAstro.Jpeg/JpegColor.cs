namespace SharpAstro.Jpeg;

/// <summary>
/// Colour-conversion row kernels — 1:1 ports of stb_image's fixed-point
/// <c>stbi__YCbCr_to_RGB_row</c> (CCIR 601 coefficients in 12.4-shifted-to-20
/// fixed point) and the Blinn 8×8 byte multiply used for Adobe CMYK / YCCK.
/// </summary>
internal static class JpegColor
{
    /// <summary>Converts one row of Y/Cb/Cr samples to packed RGBA (alpha = 255).</summary>
    public static void YCbCrToRgbaRow(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int count)
    {
        var o = 0;
        for (var i = 0; i < count; ++i)
        {
            var yFixed = (y[i] << 20) + (1 << 19);
            var crv = cr[i] - 128;
            var cbv = cb[i] - 128;
            var r = yFixed + crv * ((int)(1.40200f * 4096.0f + 0.5f) << 8);
            var g = yFixed + crv * -((int)(0.71414f * 4096.0f + 0.5f) << 8) +
                    ((cbv * -((int)(0.34414f * 4096.0f + 0.5f) << 8)) & unchecked((int)0xffff0000));
            var b = yFixed + cbv * ((int)(1.77200f * 4096.0f + 0.5f) << 8);
            r >>= 20;
            g >>= 20;
            b >>= 20;
            if ((uint)r > 255)
                r = r < 0 ? 0 : 255;
            if ((uint)g > 255)
                g = g < 0 ? 0 : 255;
            if ((uint)b > 255)
                b = b < 0 ? 0 : 255;

            dst[o + 0] = (byte)r;
            dst[o + 1] = (byte)g;
            dst[o + 2] = (byte)b;
            dst[o + 3] = 255;
            o += 4;
        }
    }

    /// <summary>
    /// Blinn's accurate byte multiply: round(x * y / 255) without a divide.
    /// Used to fold the K plane into CMY (and the alpha-style inversion for YCCK).
    /// </summary>
    public static byte Blinn8x8(byte x, byte y)
    {
        var t = (uint)(x * y + 128);
        return (byte)((t + (t >> 8)) >> 8);
    }
}
