namespace SharpAstro.Jxl;

/// <summary>
/// VarDCT chroma-from-luma (CfL) decorrelation (ISO/IEC 18181-1 §K.5.3). The X and B chroma channels
/// are predicted from luma Y with per-region correlation factors, so only the residual is coded.
/// Ported from jxl-oxide's <c>chroma_from_luma_lf</c> / <c>chroma_from_luma_hf_grouped</c>.
///
/// <para>
/// The factor is <c>k = base_correlation + raw / colour_factor</c>, where <c>raw</c> is the signed
/// per-region factor (the LF factor is offset by 128; HF factors are signed directly). Decode adds
/// <c>k·Y</c> back; encode subtracts it. Defaults (LfChannelCorrelation all_default): base_x = 0,
/// base_b = 1, colour_factor = 84, x/b factor_lf = 128 → kx = 0, kb = 1.
/// </para>
/// </summary>
internal static class JxlChromaFromLuma
{
    public const float DefaultBaseCorrelationX = 0f;
    public const float DefaultBaseCorrelationB = 1f;
    public const int DefaultColourFactor = 84;
    public const int DefaultFactorLf = 128;

    /// <summary>LF correlation factors (kx, kb) from the per-channel LF factors.</summary>
    public static (float Kx, float Kb) LfFactors(int xFactorLf, int bFactorLf, int colourFactor, float baseX, float baseB)
        => (baseX + (xFactorLf - 128) / (float)colourFactor,
            baseB + (bFactorLf - 128) / (float)colourFactor);

    /// <summary>HF correlation factors (kx, kb) from a per-64×64-block signed (x_from_y, b_from_y).</summary>
    public static (float Kx, float Kb) HfFactors(int xFromY, int bFromY, int colourFactor, float baseX, float baseB)
        => (baseX + xFromY / (float)colourFactor,
            baseB + bFromY / (float)colourFactor);

    /// <summary>Decode: restore X and B from their luma-correlated residuals (X += kx·Y, B += kb·Y).</summary>
    public static void Restore(Span<float> x, ReadOnlySpan<float> y, Span<float> b, float kx, float kb)
    {
        for (int i = 0; i < x.Length; i++)
        {
            float yi = y[i];
            x[i] += kx * yi;
            b[i] += kb * yi;
        }
    }

    /// <summary>Encode: decorrelate X and B against luma (X -= kx·Y, B -= kb·Y).</summary>
    public static void Decorrelate(Span<float> x, ReadOnlySpan<float> y, Span<float> b, float kx, float kb)
    {
        for (int i = 0; i < x.Length; i++)
        {
            float yi = y[i];
            x[i] -= kx * yi;
            b[i] -= kb * yi;
        }
    }
}
