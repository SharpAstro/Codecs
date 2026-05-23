namespace SharpAstro.Jxr;

/// <summary>
/// Macroblock-level LP-band encoder/decoder — T.832 §8.7.16 (MB_LP) +
/// §8.7.16.2 (REFINE_LP).
/// </summary>
/// <remarks>
/// LP has one "block" per component per MB — the 15 AC coefficients of
/// the second-level DC-LP super-block. Each AC coefficient is split
/// into a high part coded by the regular <see cref="BlockAdaptive"/>
/// flow and a low FLC refinement of <c>iModelBits</c> bits via REFINE_LP.
/// <para>
/// This first cut handles the CBPLP_CH_BIT signalling path (per-component
/// 1-bit "has any non-zero LP" flag) covering INTERNAL_CLR_FMT ∈
/// {YOnly, YUVK, NComponent, Rgb}. The CBPLP_YUV1/YUV2 joint VLC path
/// for YUV420/422/444 needs additional state (CountZero/Max CBPLP)
/// and lands separately.
/// </para>
/// </remarks>
public static class MbLp
{
    /// <summary>
    /// iTranspose444 from T.832 §8.7.16.1 — the order in which REFINE_LP
    /// iterates LP positions. Position 0 (DC) is skipped; positions
    /// 1..15 are visited in this column-major sequence.
    /// </summary>
    public static ReadOnlySpan<byte> Transpose444 =>
        [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    /// <summary>
    /// Encode the LP-band coefficients of one macroblock for a CBPLP_CH_BIT
    /// format. <paramref name="lp"/> is <c>numComponents × 16</c> ints
    /// — per component, position 0 is the super-DC (untouched here),
    /// positions 1..15 are the AC LP coefficients.
    /// </summary>
    public static void EncodeMb(
        BitWriter writer,
        MbLpState state,
        JxrInternalColorFormat format,
        int numComponents,
        ReadOnlySpan<int> lp)
    {
        EnsureCbplpChBitFormat(format);
        if (lp.Length < numComponents * 16)
            throw new ArgumentException($"lp must hold {numComponents} × 16 ints", nameof(lp));

        // Per-component CBPLP_CH_BIT bitmap, then per-component encoding.
        var modelBitsLum = state.Model.MBits0;
        var modelBitsChr = state.Model.MBits1;
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;

        // First emit all CBPLP_CH_BIT flags (one bit per component).
        Span<int> cbplpFlags = stackalloc int[16]; // generous; numComponents ≤ 16
        for (var c = 0; c < numComponents; c++)
        {
            var componentLp = lp.Slice(c * 16, 16);
            var mb = c == 0 ? modelBitsLum : modelBitsChr;
            // CBPLP_CH_BIT is set if any coefficient has high bits beyond iModelBits.
            var hasHigh = false;
            for (var p = 1; p < 16 && !hasHigh; p++)
            {
                var abs = componentLp[p] < 0 ? -componentLp[p] : componentLp[p];
                if (mb > 0 ? (abs >> mb) > 0 : abs > 0)
                    hasHigh = true;
            }
            cbplpFlags[c] = hasHigh ? 1 : 0;
            writer.WriteBit(hasHigh);
        }

        // Then per-component data: block-encoded high bits + REFINE_LP refinement.
        Span<int> highBlock = stackalloc int[16];
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var mb = bChroma ? modelBitsChr : modelBitsLum;
            var componentLp = lp.Slice(c * 16, 16);

            // Build the high-bits block for BlockAdaptive.Encode.
            highBlock.Clear();
            for (var p = 1; p < 16; p++)
            {
                var v = componentLp[p];
                var absV = v < 0 ? -v : v;
                var high = mb > 0 ? absV >> mb : absV;
                if (high == 0)
                    highBlock[p] = 0;
                else
                    highBlock[p] = v < 0 ? -high : high;
            }

            // Encode the high-bits block via BlockAdaptive when CBPLP says non-zero.
            if (cbplpFlags[c] == 1)
            {
                var n = EncodeBlock(writer, state, bChroma, highBlock);
                if (bChroma) iLapMeanChr += n;
                else         iLapMeanLum += n;
            }

            // REFINE_LP for every position 1..15 (in iTranspose444 order) when iModelBits > 0.
            if (mb > 0)
            {
                for (var k = 1; k < 16; k++)
                {
                    var pos = (int)Transpose444[k];
                    var blockHigh = highBlock[pos];
                    var actual = componentLp[pos];
                    EmitRefineLp(writer, blockHigh, actual, mb);
                }
            }
        }

        CoefficientModel.Update(
            ref state.Model,
            iLapMeanLum,
            iLapMeanChr,
            CoefficientModel.Band.Lp,
            format,
            numComponents);
    }

    /// <summary>Decode the LP-band coefficients of one macroblock — dual of <see cref="EncodeMb"/>.</summary>
    public static void DecodeMb(
        ref BitReader reader,
        MbLpState state,
        JxrInternalColorFormat format,
        int numComponents,
        Span<int> lpOut)
    {
        EnsureCbplpChBitFormat(format);
        if (lpOut.Length < numComponents * 16)
            throw new ArgumentException($"lpOut must hold {numComponents} × 16 ints", nameof(lpOut));

        // Zero AC positions; DC position 0 is caller's responsibility.
        for (var c = 0; c < numComponents; c++)
            for (var p = 1; p < 16; p++)
                lpOut[c * 16 + p] = 0;

        var modelBitsLum = state.Model.MBits0;
        var modelBitsChr = state.Model.MBits1;
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;

        // Read all CBPLP_CH_BIT flags up front.
        Span<int> cbplpFlags = stackalloc int[16];
        for (var c = 0; c < numComponents; c++)
            cbplpFlags[c] = reader.ReadBit() ? 1 : 0;

        var highBlockBuf = new int[16];
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var mb = bChroma ? modelBitsChr : modelBitsLum;
            var componentLp = lpOut.Slice(c * 16, 16);

            Array.Clear(highBlockBuf, 0, 16);
            Span<int> highBlock = highBlockBuf;
            if (cbplpFlags[c] == 1)
            {
                var n = DecodeBlock(ref reader, state, bChroma, highBlock);
                if (bChroma) iLapMeanChr += n;
                else         iLapMeanLum += n;
            }

            // REFINE_LP refinement when iModelBits > 0.
            if (mb > 0)
            {
                for (var k = 1; k < 16; k++)
                {
                    var pos = (int)Transpose444[k];
                    componentLp[pos] = ApplyRefineLp(ref reader, highBlock[pos], mb);
                }
            }
            else
            {
                // No refinement — high-bits block IS the final LP block.
                for (var p = 1; p < 16; p++)
                    componentLp[p] = highBlock[p];
            }
        }

        CoefficientModel.Update(
            ref state.Model,
            iLapMeanLum,
            iLapMeanChr,
            CoefficientModel.Band.Lp,
            format,
            numComponents);
    }

    // -----------------------------------------------------------------------
    // REFINE_LP: T.832 §8.7.16.2 / Table 54.
    // Encoder writes the iModelBits-wide COEFF_REF (and a SIGN_FLAG when the
    // block-coded high part is zero but the refined LP is non-zero); decoder
    // reverses that.
    // -----------------------------------------------------------------------

    private static void EmitRefineLp(BitWriter writer, int blockHigh, int actual, int iModelBits)
    {
        var absActual = actual < 0 ? -actual : actual;
        var coeffRef = absActual & ((1 << iModelBits) - 1);
        writer.WriteBits((uint)coeffRef, iModelBits);
        if (blockHigh == 0 && coeffRef != 0)
            writer.WriteBit(actual < 0);
    }

    private static int ApplyRefineLp(ref BitReader reader, int blockHigh, int iModelBits)
    {
        var coeffRef = (int)reader.ReadBits(iModelBits);
        if (blockHigh > 0)
            return (blockHigh << iModelBits) + coeffRef;
        if (blockHigh < 0)
            return (blockHigh << iModelBits) - coeffRef;
        // blockHigh == 0
        if (coeffRef == 0)
            return 0;
        return reader.ReadBit() ? -coeffRef : coeffRef;
    }

    // -----------------------------------------------------------------------
    // BlockCodingContext snapshot/restore around BlockAdaptive — same shape
    // as MbHp but with bChroma controlling FirstIndex/Index dispatch and
    // AbsLevel0/1 always shared (per spec).
    // -----------------------------------------------------------------------

    private static int EncodeBlock(BitWriter writer, MbLpState state, bool bChroma, ReadOnlySpan<int> block)
    {
        var ctx = MakeBlockCtx(state, bChroma);
        var n = BlockAdaptive.Encode(writer, ref ctx, state.Scan, block);
        WriteBackCtx(state, bChroma, ref ctx);
        return n;
    }

    private static int DecodeBlock(ref BitReader reader, MbLpState state, bool bChroma, Span<int> block)
    {
        var ctx = MakeBlockCtx(state, bChroma);
        var n = BlockAdaptive.Decode(ref reader, ref ctx, state.Scan, block);
        WriteBackCtx(state, bChroma, ref ctx);
        return n;
    }

    private static BlockCodingContext MakeBlockCtx(MbLpState state, bool bChroma) => new()
    {
        FirstIndex = bChroma ? state.FirstIndexChr : state.FirstIndexLum,
        Index0 = bChroma ? state.IndexChr0 : state.IndexLum0,
        Index1 = bChroma ? state.IndexChr1 : state.IndexLum1,
        AbsLevel0 = state.AbsLevel0,
        AbsLevel1 = state.AbsLevel1,
    };

    private static void WriteBackCtx(MbLpState state, bool bChroma, ref BlockCodingContext ctx)
    {
        if (bChroma)
        {
            state.FirstIndexChr = ctx.FirstIndex;
            state.IndexChr0 = ctx.Index0;
            state.IndexChr1 = ctx.Index1;
        }
        else
        {
            state.FirstIndexLum = ctx.FirstIndex;
            state.IndexLum0 = ctx.Index0;
            state.IndexLum1 = ctx.Index1;
        }
        state.AbsLevel0 = ctx.AbsLevel0;
        state.AbsLevel1 = ctx.AbsLevel1;
    }

    private static void EnsureCbplpChBitFormat(JxrInternalColorFormat f)
    {
        if (f == JxrInternalColorFormat.YUV420 || f == JxrInternalColorFormat.YUV422 || f == JxrInternalColorFormat.YUV444)
            throw new NotSupportedException(
                "MbLp.EncodeMb / DecodeMb currently handles only CBPLP_CH_BIT formats " +
                "(YOnly / YUVK / NComponent / Rgb). The CBPLP_YUV1/YUV2 joint VLC path " +
                "for YUV420/422/444 needs the CountZero/Max CBPLP state and lands separately.");
    }
}
