using System.Runtime.InteropServices;

namespace SharpAstro.Jxl;

/// <summary>
/// XYB to the image's own colour encoding, as integer samples (ISO/IEC 18181-1 §L.2 and Annex D). The XYB step is
/// jxl-oxide's <c>jxl-color/src/xyb.rs</c>: undo the cube root about the opsin bias, scale by
/// <c>255 / intensity_target</c>, and apply the opsin inverse matrix, which gives linear light in sRGB primaries
/// with 1.0 at the intensity target. Other primaries are reached through CIE XYZ (both white points D65, so no
/// adaptation), as jxl-oxide's <c>ColorTransform</c> does; unlike it, nothing is gamut-mapped or clipped before
/// the matrix, so a colour outside sRGB keeps its place in BT.2020 as it does in libjxl. The transfer functions are
/// the exact formulas. Against libjxl (Magick.NET) the sRGB output agrees to one 16-bit code; the PQ output does
/// too except in a channel near black, where PQ is steep enough that libjxl's colour-managed path (an ICC transform
/// with fixed-point colorants) lands up to a few hundred codes away. libjxl's rational approximation of PQ
/// (jxl-oxide tf/pq.rs) was tried there and moved nothing, so it is not used.
/// </summary>
internal static class JxlColorOutput
{
    // Default opsin inverse matrix and bias (the all_default OpsinInverseMatrix, jxl-image color.rs).
    private static readonly float[] OpsinInverse =
    [
        11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
        -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
        -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
    ];
    private const float OpsinBias = -0.0037930732552754493f;

    /// <summary>Refuses an encoding this converter cannot produce, naming what is missing.</summary>
    public static void CheckSupported(in JxlColorEncoding enc)
    {
        if (enc.ColorSpace is not (JxlColorSpace.Rgb or JxlColorSpace.Gray))
            throw new NotSupportedException($"JPEG XL VarDCT output in colour space {enc.ColorSpace} is not yet supported.");
        if (enc.WhitePoint != JxlWhitePoint.D65)
            throw new NotSupportedException($"JPEG XL VarDCT output with white point {enc.WhitePoint} is not yet supported.");
        if (enc.ColorSpace == JxlColorSpace.Rgb && enc.Primaries is not (JxlPrimaries.Srgb or JxlPrimaries.Bt2100 or JxlPrimaries.P3))
            throw new NotSupportedException($"JPEG XL VarDCT output with primaries {enc.Primaries} is not yet supported.");
        if (enc.Transfer is not (JxlTransferFunction.Srgb or JxlTransferFunction.Linear or JxlTransferFunction.Pq
            or JxlTransferFunction.Bt709 or JxlTransferFunction.Dci or JxlTransferFunction.Gamma))
            throw new NotSupportedException($"JPEG XL VarDCT output with transfer function {enc.Transfer} is not yet supported.");
    }

    /// <summary>
    /// Converts the three XYB planes (floats stored in <paramref name="planes"/>, row length
    /// <paramref name="stride"/>) to integer samples of <paramref name="bits"/> bits, returning one row-major
    /// <paramref name="width"/> x <paramref name="height"/> array per channel: three for RGB, one for grey (the
    /// luminance of the linear sRGB, jxl-oxide's RGB to XYZ then <c>XyzToLuma</c>). When the planes are exactly
    /// that size the conversion is in place and the planes themselves are returned.
    /// </summary>
    public static int[][] XybToInteger(
        int[][] planes, int stride, int planeHeight, int width, int height,
        in JxlColorEncoding enc, float intensityTarget, int bits)
    {
        CheckSupported(enc);
        bool grey = enc.ColorSpace == JxlColorSpace.Gray;
        float[] m = BuildMatrix(grey ? JxlPrimaries.Srgb : enc.Primaries, intensityTarget);
        double[] srgbToXyz = PrimariesToXyz(Chromaticities(JxlPrimaries.Srgb));
        float lumR = (float)srgbToXyz[3], lumG = (float)srgbToXyz[4], lumB = (float)srgbToXyz[5];
        JxlTransferFunction tf = enc.Transfer;
        float gamma = tf switch
        {
            JxlTransferFunction.Gamma => enc.Gamma,
            JxlTransferFunction.Dci => 1f / 2.6f,
            _ => 1f,
        };
        float maxValue = (1 << bits) - 1;
        float cbrtBias = MathF.Cbrt(OpsinBias);

        bool inPlace = stride == width && planeHeight == height;
        int[][] output = grey
            ? [inPlace ? planes[0] : new int[width * height]]
            : inPlace
                ? planes
                : [new int[width * height], new int[width * height], new int[width * height]];

        Parallel.For(0, height, y =>
        {
            Span<float> px = MemoryMarshal.Cast<int, float>(planes[0].AsSpan(y * stride, width));
            Span<float> py = MemoryMarshal.Cast<int, float>(planes[1].AsSpan(y * stride, width));
            Span<float> pb = MemoryMarshal.Cast<int, float>(planes[2].AsSpan(y * stride, width));
            Span<int> o0 = output[0].AsSpan(y * width, width);
            Span<int> o1 = grey ? default : output[1].AsSpan(y * width, width);
            Span<int> o2 = grey ? default : output[2].AsSpan(y * width, width);
            for (int x = 0; x < width; x++)
            {
                float gl = py[x] + px[x] - cbrtBias;
                float gm = py[x] - px[x] - cbrtBias;
                float gs = pb[x] - cbrtBias;
                float l = gl * gl * gl + OpsinBias;
                float mm = gm * gm * gm + OpsinBias;
                float s = gs * gs * gs + OpsinBias;
                float r = m[0] * l + m[1] * mm + m[2] * s;
                float g = m[3] * l + m[4] * mm + m[5] * s;
                float b = m[6] * l + m[7] * mm + m[8] * s;
                if (grey)
                {
                    o0[x] = Quantize(Encode(lumR * r + lumG * g + lumB * b, tf, gamma, intensityTarget), maxValue);
                    continue;
                }
                o0[x] = Quantize(Encode(r, tf, gamma, intensityTarget), maxValue);
                o1[x] = Quantize(Encode(g, tf, gamma, intensityTarget), maxValue);
                o2[x] = Quantize(Encode(b, tf, gamma, intensityTarget), maxValue);
            }
        });
        return output;
    }

    private static int Quantize(float v, float maxValue)
    {
        if (!(v > 0f)) return 0;
        if (v >= 1f) return (int)maxValue;
        return (int)(v * maxValue + 0.5f);
    }

    /// <summary>Linear (1.0 at the intensity target) to the encoded value.</summary>
    private static float Encode(float v, JxlTransferFunction tf, float gamma, float intensityTarget)
    {
        switch (tf)
        {
            case JxlTransferFunction.Linear:
                return v;
            case JxlTransferFunction.Srgb:
                if (v <= 0.0031308f) return v * 12.92f;
                return 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
            case JxlTransferFunction.Bt709:
                if (v < 0.018053968510807f) return v * 4.5f;
                return 1.099296826809442f * MathF.Pow(v, 0.45f) - 0.099296826809442f;
            case JxlTransferFunction.Pq:
            {
                if (!(v > 0f)) return 0f;
                const double M1 = 2610.0 / 16384.0, M2 = 2523.0 / 4096.0 * 128.0;
                const double C1 = 3424.0 / 4096.0, C2 = 2413.0 / 4096.0 * 32.0, C3 = 2392.0 / 4096.0 * 32.0;
                double yp = Math.Pow(v * (double)intensityTarget / 10000.0, M1);
                return (float)Math.Pow((C1 + C2 * yp) / (1.0 + C3 * yp), M2);
            }
            default: // Gamma, DCI
                if (!(v > 0f)) return 0f;
                return MathF.Pow(v, gamma);
        }
    }

    /// <summary>
    /// The opsin inverse matrix scaled by <c>255 / intensity_target</c>, followed by sRGB to the target primaries
    /// through XYZ when they differ.
    /// </summary>
    private static float[] BuildMatrix(JxlPrimaries primaries, float intensityTarget)
    {
        double itscale = 255.0 / intensityTarget;
        var inv = new double[9];
        for (int i = 0; i < 9; i++)
            inv[i] = OpsinInverse[i] * itscale;

        double[] m = inv;
        if (primaries != JxlPrimaries.Srgb)
        {
            double[] srgbToXyz = PrimariesToXyz(Chromaticities(JxlPrimaries.Srgb));
            double[] xyzToTarget = Invert(PrimariesToXyz(Chromaticities(primaries)));
            m = Mul(Mul(xyzToTarget, srgbToXyz), inv);
        }
        var f = new float[9];
        for (int i = 0; i < 9; i++)
            f[i] = (float)m[i];
        return f;
    }

    private static double[] Chromaticities(JxlPrimaries p) => p switch
    {
        JxlPrimaries.Srgb => [0.64, 0.33, 0.30, 0.60, 0.15, 0.06],
        JxlPrimaries.Bt2100 => [0.708, 0.292, 0.170, 0.797, 0.131, 0.046],
        JxlPrimaries.P3 => [0.680, 0.320, 0.265, 0.690, 0.150, 0.060],
        _ => throw new NotSupportedException($"JPEG XL primaries {p} are not yet supported."),
    };

    /// <summary>RGB to XYZ for the given primaries and D65 (jxl-oxide <c>primaries_to_xyz_mat</c>).</summary>
    private static double[] PrimariesToXyz(double[] c)
    {
        double[] p =
        [
            c[0], c[2], c[4],
            c[1], c[3], c[5],
            1 - c[0] - c[1], 1 - c[2] - c[3], 1 - c[4] - c[5],
        ];
        const double wx = 0.3127, wy = 0.3290;
        double[] w = [wx / wy, 1.0, (1 - wx - wy) / wy];
        double[] pinv = Invert(p);
        double[] mul =
        [
            pinv[0] * w[0] + pinv[1] * w[1] + pinv[2] * w[2],
            pinv[3] * w[0] + pinv[4] * w[1] + pinv[5] * w[2],
            pinv[6] * w[0] + pinv[7] * w[1] + pinv[8] * w[2],
        ];
        for (int i = 0; i < 9; i++)
            p[i] *= mul[i % 3];
        return p;
    }

    private static double[] Mul(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }

    private static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        double k = 1.0 / det;
        return
        [
            (e * i - f * h) * k, (c * h - b * i) * k, (b * f - c * e) * k,
            (f * g - d * i) * k, (a * i - c * g) * k, (c * d - a * f) * k,
            (d * h - e * g) * k, (b * g - a * h) * k, (a * e - b * d) * k,
        ];
    }
}
