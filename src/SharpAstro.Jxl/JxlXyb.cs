namespace SharpAstro.Jxl;

/// <summary>
/// The XYB ("opsin") colour transform used by JPEG XL VarDCT (ISO/IEC 18181-1 §K.5), together with
/// the sRGB transfer function. XYB is a perceptual, roughly-LMS opponent space: linear RGB is mixed
/// into long/medium/short cone responses, gamma-compressed by a cube root, then recast as
/// X = (L−M)/2 (red-green), Y = (L+M)/2 (luma), B = S (blue).
///
/// <para>
/// The <b>inverse</b> (XYB → linear RGB) is a faithful port of jxl-oxide's
/// <c>jxl-color/src/xyb.rs</c> plus the opsin inverse matrix applied as a follow-on colour-matrix
/// op. The <b>forward</b> (linear RGB → XYB) is the algebraic inverse, using libjxl's published
/// forward opsin absorbance matrix so that libjxl/Magick decode our XYB to the expected colours.
/// </para>
///
/// All constants are the JPEG XL defaults (the <c>all_default</c> OpsinInverseMatrix); the SDR
/// intensity target (255 nits) is assumed, so the decode <c>itscale = 255 / intensity_target</c> is
/// 1 and the linear domain is display-referred [0, 1].
/// </summary>
internal static class JxlXyb
{
    // Forward opsin absorbance matrix (linear RGB → mixed LMS), libjxl kOpsinAbsorbanceMatrix.
    private static readonly float[] OpsinForward =
    {
        0.30f, 0.622f, 0.078f,
        0.23f, 0.692f, 0.078f,
        0.24342268924547819f, 0.20476744424496821f, 0.55180986650955362f,
    };

    // Default opsin inverse matrix (mixed LMS → linear RGB), the all_default OpsinInverseMatrix.
    private static readonly float[] OpsinInverse =
    {
        11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
        -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
        -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
    };

    // opsin_bias (the all_default value, repeated for the three channels). Negative; cube-root below.
    private const float OpsinBias = -0.0037930732552754493f;
    private static readonly float CbrtBias = MathF.Cbrt(OpsinBias);

    /// <summary>Linear RGB (display-referred [0,1]) → XYB.</summary>
    public static (float X, float Y, float B) LinearToXyb(float r, float g, float b)
    {
        // Mixed LMS = forward absorbance matrix · linear RGB.
        float l = OpsinForward[0] * r + OpsinForward[1] * g + OpsinForward[2] * b;
        float m = OpsinForward[3] * r + OpsinForward[4] * g + OpsinForward[5] * b;
        float s = OpsinForward[6] * r + OpsinForward[7] * g + OpsinForward[8] * b;

        // Gamma compress (cube root) about the opsin bias, recovering the "raw gamma" channels.
        float gl = MathF.Cbrt(l - OpsinBias) + CbrtBias;
        float gm = MathF.Cbrt(m - OpsinBias) + CbrtBias;
        float gs = MathF.Cbrt(s - OpsinBias) + CbrtBias;

        return ((gl - gm) * 0.5f, (gl + gm) * 0.5f, gs);
    }

    /// <summary>XYB → linear RGB (display-referred [0,1]).</summary>
    public static (float R, float G, float B) XybToLinear(float x, float y, float b)
    {
        // Recover the gamma channels (matrix [1,1,0; -1,1,0; 0,0,1]) and remove the cube-root bias.
        float gl = y + x - CbrtBias;
        float gm = y - x - CbrtBias;
        float gs = b - CbrtBias;

        // Expand the cube root and re-apply the opsin bias to get mixed LMS.
        float l = gl * gl * gl + OpsinBias;
        float m = gm * gm * gm + OpsinBias;
        float s = gs * gs * gs + OpsinBias;

        // Mixed LMS → linear RGB via the opsin inverse matrix.
        float r = OpsinInverse[0] * l + OpsinInverse[1] * m + OpsinInverse[2] * s;
        float gg = OpsinInverse[3] * l + OpsinInverse[4] * m + OpsinInverse[5] * s;
        float bb = OpsinInverse[6] * l + OpsinInverse[7] * m + OpsinInverse[8] * s;
        return (r, gg, bb);
    }

    /// <summary>sRGB-encoded value [0,1] → linear-light [0,1] (the IEC 61966-2-1 EOTF).</summary>
    public static float SrgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>Linear-light value [0,1] → sRGB-encoded [0,1] (the IEC 61966-2-1 OETF).</summary>
    public static float LinearToSrgb(float l)
        => l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
}
