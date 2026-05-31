namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT quantizer (ISO/IEC 18181-1 §K.4): maps DCT coefficients to/from integer quanta using
/// the global scale, the per-block HF multiplier, the per-channel dequant weight matrix, and the
/// adaptive quant-bias dead-zone. Ported from jxl-oxide's <c>dequant_hf_varblock</c> /
/// <c>copy_lf_dequant</c> (jxl-render) with the all-default OpsinInverseMatrix bias constants.
/// </summary>
internal sealed class JxlQuantizer
{
    /// <summary>Default OpsinInverseMatrix <c>quant_bias</c> (X, Y, B) — the small-coefficient reconstruction nudge.</summary>
    public static readonly float[] DefaultQuantBias =
    {
        1f - 0.05465007330715401f,
        1f - 0.07005449891748593f,
        1f - 0.049935103337343655f,
    };

    /// <summary>Default OpsinInverseMatrix <c>quant_bias_numerator</c>, for |q| &gt; 1.</summary>
    public const float DefaultQuantBiasNumerator = 0.145f;

    // LfChannelDequantization defaults — the RAW m_*_lf the LF dequant uses. NOTE: copy_lf_dequant
    // (jxl-render) multiplies by the raw m_x_lf, NOT the /128 "unscaled" form (that /128 value is only
    // used for the "weight too small" validation). Using /128 here makes the dequant 128× too small.
    public const float MxLf = 1f / 32f;
    public const float MyLf = 1f / 4f;
    public const float MbLf = 1f / 2f;

    public int GlobalScale { get; }
    public int QuantLf { get; }

    public JxlQuantizer(int globalScale, int quantLf)
    {
        GlobalScale = globalScale;
        QuantLf = quantLf;
    }

    /// <summary>
    /// The HF coefficient dequant scale (the <c>mul</c> term): <c>65536 / (global_scale · hf_mul) · qm_scale</c>.
    /// Per channel, <c>qm_scale</c> = (0.8^(x_qm_scale−2), 1, 0.8^(b_qm_scale−2)).
    /// </summary>
    public float HfScale(int hfMul, float qmScale)
        => 65536f / (GlobalScale * (float)hfMul) * qmScale;

    /// <summary>Dequantize one HF coefficient: integer quantum → float coefficient.</summary>
    public static float DequantizeHf(int q, float weight, float scale, int channel)
    {
        float qf;
        if (Math.Abs(q) <= 1)
            qf = q * DefaultQuantBias[channel];
        else
            qf = q - DefaultQuantBiasNumerator / q;
        return qf * weight * scale;
    }

    /// <summary>
    /// Forward-quantize one HF coefficient: float coefficient → integer quantum. The dead-zone bias
    /// is a decode-side reconstruction offset, so the encoder uses plain rounding.
    /// </summary>
    public static int QuantizeHf(float coeff, float weight, float scale)
        => (int)MathF.Round(coeff / (weight * scale));

    /// <summary>
    /// The LF (DC) dequant scale: <c>m_lf · 2^(9−extra_precision) / (global_scale · quant_lf)</c>,
    /// where <paramref name="mLf"/> is the raw <c>m_*_lf</c> (e.g. 1/4 for Y) — matching jxl-render
    /// <c>copy_lf_dequant</c>.
    /// </summary>
    public float LfScale(float mLf, int extraPrecision)
        => (float)((double)mLf * (1 << (9 - extraPrecision)) / ((double)GlobalScale * QuantLf));
}
