namespace SharpAstro.Jxr;

/// <summary>
/// Lossy quantization helpers — T.832 §9.5 (QP_INDEX derivation) plus the
/// per-band coefficient division/multiplication that gives JPEG XR its
/// compression vs fidelity tradeoff.
/// </summary>
/// <remarks>
/// <para>QP_INDEX is the byte stored in <c>DcQuant</c> / <c>LpQuant</c> /
/// <c>HpQuant</c> of the <see cref="ImagePlaneHeader"/>. It maps to an actual
/// divisor via the T.832 mantissa+exponent formula:</para>
/// <code>
/// 0     → 1 (treated as lossless)
/// 1..15 → QP_INDEX directly (small steps)
/// ≥16   → ((QP_INDEX &amp; 0xF) | 0x10) &lt;&lt; ((QP_INDEX &gt;&gt; 4) - 1)
/// </code>
/// <para>Quantization happens BEFORE the prediction cascade so that prediction
/// operates on quantized values consistently on both encoder and decoder.
/// Dequantization happens AFTER inverse prediction on the decode side. With
/// <c>qpIndex = 1</c> the divisor is 1 and these calls become no-ops, preserving
/// the lossless behaviour the existing encoder/decoder pairs target by default.</para>
/// </remarks>
public static class JxrQuant
{
    /// <summary>
    /// Convert a JXR QP_INDEX (the byte stored in the plane header) to an
    /// integer divisor. <c>QP_INDEX = 0</c> is treated as 1 (lossless);
    /// 1..15 map directly; higher values use the mantissa+exponent encoding.
    /// </summary>
    public static int QpIndexToDivisor(byte qpIndex)
    {
        if (qpIndex == 0) return 1;
        if (qpIndex < 16) return qpIndex;
        var man = (qpIndex & 0x0F) | 0x10;
        var exp = (qpIndex >> 4) - 1;
        return man << exp;
    }

    /// <summary>Quantize a single value with round-half-away-from-zero.</summary>
    public static int Quantize(int value, int divisor)
    {
        if (divisor <= 1) return value;
        var half = divisor >> 1;
        return value >= 0 ? (value + half) / divisor : -((-value + half) / divisor);
    }

    /// <summary>Dequantize by multiplication — the decoder's inverse of <see cref="Quantize"/>.</summary>
    public static int Dequantize(int q, int divisor) => q * divisor;

    /// <summary>
    /// Quantize the super-DC values in place. Skipped entirely when
    /// <paramref name="divisor"/> is 1, so lossless callers pay nothing.
    /// </summary>
    public static void QuantizeDc(int[,,] mbDc, int divisor)
    {
        if (divisor <= 1) return;
        var mbW = mbDc.GetLength(0);
        var mbH = mbDc.GetLength(1);
        var nc = mbDc.GetLength(2);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < nc; c++)
            mbDc[mbx, mby, c] = Quantize(mbDc[mbx, mby, c], divisor);
    }

    /// <summary>Dequantize the super-DC values in place — decoder inverse of <see cref="QuantizeDc"/>.</summary>
    public static void DequantizeDc(int[,,] mbDc, int divisor)
    {
        if (divisor <= 1) return;
        var mbW = mbDc.GetLength(0);
        var mbH = mbDc.GetLength(1);
        var nc = mbDc.GetLength(2);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < nc; c++)
            mbDc[mbx, mby, c] *= divisor;
    }

    /// <summary>
    /// Quantize LP coefficients (positions 1..15 of <paramref name="mbDcLp"/>).
    /// Position 0 is the super-DC slot and is handled separately by
    /// <see cref="QuantizeDc"/>.
    /// </summary>
    public static void QuantizeLp(int[,,,] mbDcLp, int divisor)
    {
        if (divisor <= 1) return;
        var mbW = mbDcLp.GetLength(0);
        var mbH = mbDcLp.GetLength(1);
        var nc = mbDcLp.GetLength(2);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < nc; c++)
        for (var p = 1; p < 16; p++)
            mbDcLp[mbx, mby, c, p] = Quantize(mbDcLp[mbx, mby, c, p], divisor);
    }

    public static void DequantizeLp(int[,,,] mbDcLp, int divisor)
    {
        if (divisor <= 1) return;
        var mbW = mbDcLp.GetLength(0);
        var mbH = mbDcLp.GetLength(1);
        var nc = mbDcLp.GetLength(2);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < nc; c++)
        for (var p = 1; p < 16; p++)
            mbDcLp[mbx, mby, c, p] *= divisor;
    }

    /// <summary>
    /// Quantize HP coefficients (positions 1..15 of each block in
    /// <paramref name="mbHp"/>). Position 0 of each block is unused — the
    /// sub-block DC is already represented in <c>mbDcLp</c>.
    /// </summary>
    public static void QuantizeHp(int[,,,,] mbHp, int divisor)
    {
        if (divisor <= 1) return;
        var mbW = mbHp.GetLength(0);
        var mbH = mbHp.GetLength(1);
        var nc = mbHp.GetLength(2);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < nc; c++)
        for (var blk = 0; blk < 16; blk++)
        for (var p = 1; p < 16; p++)
            mbHp[mbx, mby, c, blk, p] = Quantize(mbHp[mbx, mby, c, blk, p], divisor);
    }

    public static void DequantizeHp(int[,,,,] mbHp, int divisor)
    {
        if (divisor <= 1) return;
        var mbW = mbHp.GetLength(0);
        var mbH = mbHp.GetLength(1);
        var nc = mbHp.GetLength(2);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < nc; c++)
        for (var blk = 0; blk < 16; blk++)
        for (var p = 1; p < 16; p++)
            mbHp[mbx, mby, c, blk, p] *= divisor;
    }
}
