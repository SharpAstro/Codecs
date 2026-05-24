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
        if (format == JxrInternalColorFormat.YUV420 || format == JxrInternalColorFormat.YUV422)
            throw new NotSupportedException(
                "MbLp.EncodeMb: YUV420 / YUV422 chroma-subsampling LP coding " +
                "(2-bit CBPLP via Table 56) not yet wired. YUV444 IS supported via " +
                "the CBPLP_YUV1/2 joint-VLC path below.");
        if (lp.Length < numComponents * 16)
            throw new ArgumentException($"lp must hold {numComponents} × 16 ints", nameof(lp));

        // Per-component CBPLP_CH_BIT bitmap, then per-component encoding.
        var modelBitsLum = state.Model.MBits0;
        var modelBitsChr = state.Model.MBits1;
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;
        var isYuv444 = format == JxrInternalColorFormat.YUV444;

        // First emit all CBPLP flags. CBPLP_CH_BIT (Rgb/NComponent/YOnly/YUVK)
        // is one raw bit per component. YUV444 uses the joint-VLC dispatch
        // between CBPLP_YUV1 (Table 55 VLC) and CBPLP_YUV2 (raw u(3)) selected
        // by the CountZero/CountMax state — see WriteCbplpYuv444.
        Span<int> cbplpFlags = stackalloc int[16]; // generous; numComponents ≤ 16
        if (isYuv444)
        {
            // Compute the 3 component bits first, then emit them jointly.
            for (var c = 0; c < 3; c++)
            {
                var mb = c == 0 ? modelBitsLum : modelBitsChr;
                var componentLp = lp.Slice(c * 16, 16);
                cbplpFlags[c] = HasNonZeroHigh(componentLp, mb) ? 1 : 0;
            }
            var iCBPLP = cbplpFlags[0] | (cbplpFlags[1] << 1) | (cbplpFlags[2] << 2);
            WriteCbplpYuv444(writer, state, iCBPLP);
            UpdateCbplpCounts(state, iCBPLP, iMax: 7);
        }
        else
        {
            for (var c = 0; c < numComponents; c++)
            {
                var componentLp = lp.Slice(c * 16, 16);
                var mb = c == 0 ? modelBitsLum : modelBitsChr;
                var hasHigh = HasNonZeroHigh(componentLp, mb);
                cbplpFlags[c] = hasHigh ? 1 : 0;
                writer.WriteBit(hasHigh);
            }
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
        if (format == JxrInternalColorFormat.YUV420 || format == JxrInternalColorFormat.YUV422)
            throw new NotSupportedException(
                "MbLp.DecodeMb: YUV420 / YUV422 chroma-subsampling LP decoding " +
                "(2-bit CBPLP via Table 56) not yet wired. YUV444 IS supported via " +
                "the CBPLP_YUV1/2 joint-VLC path.");
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
        var isYuv444 = format == JxrInternalColorFormat.YUV444;

        // Read CBPLP flags — joint dispatch for YUV444, per-component bits otherwise.
        Span<int> cbplpFlags = stackalloc int[16];
        if (isYuv444)
        {
            var iCBPLP = ReadCbplpYuv444(ref reader, state);
            cbplpFlags[0] = iCBPLP & 1;
            cbplpFlags[1] = (iCBPLP >> 1) & 1;
            cbplpFlags[2] = (iCBPLP >> 2) & 1;
            UpdateCbplpCounts(state, iCBPLP, iMax: 7);
        }
        else
        {
            for (var c = 0; c < numComponents; c++)
                cbplpFlags[c] = reader.ReadBit() ? 1 : 0;
        }

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

    /// <summary>
    /// True iff any of <paramref name="componentLp"/>'s positions 1..15 has a
    /// non-zero "high" part when shifted right by <paramref name="iModelBits"/>.
    /// </summary>
    private static bool HasNonZeroHigh(ReadOnlySpan<int> componentLp, int iModelBits)
    {
        for (var p = 1; p < 16; p++)
        {
            var abs = componentLp[p] < 0 ? -componentLp[p] : componentLp[p];
            if (iModelBits > 0 ? (abs >> iModelBits) > 0 : abs > 0)
                return true;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // CBPLP_YUV1 / CBPLP_YUV2 joint dispatch — T.832 §8.7.16.3, Tables 55,
    // 103, 104. Used by INTERNAL_CLR_FMT = YUV444 (3 components, iMax = 7).
    //
    // Per-MB state machine:
    //   if (CountZeroCBPLP <= 0 || CountMaxCBPLP < 0)
    //     CBPLP_YUV1: VLC-coded value (Table 55), optionally inverted to
    //                 favour the most-common extreme
    //   else
    //     CBPLP_YUV2: raw u(3) bits
    //
    // Counts initialize to (1, 1) at the top-left MB of each tile (= fresh
    // MbLpState) and clamp to [-8, 7] after each update.
    // -----------------------------------------------------------------------

    /// <summary>Table 55: CBPLP_YUV1 codes for YUV444 (iMax=7). Indexed by value 0..7.</summary>
    private static readonly (uint code, int length)[] CbplpYuv1Yuv444 =
    [
        (0b0,    1),  // 0
        (0b100,  3),  // 1
        (0b1010, 4),  // 2
        (0b1011, 4),  // 3
        (0b1100, 4),  // 4
        (0b1101, 4),  // 5
        (0b1110, 4),  // 6
        (0b1111, 4),  // 7
    ];

    private static void WriteCbplpYuv444(BitWriter writer, MbLpState state, int iCBPLP)
    {
        const int iMax = 7;
        if (state.CountZeroCBPLP <= 0 || state.CountMaxCBPLP < 0)
        {
            // VLC path — invert iCBPLP when Max is the more common extreme so
            // that "max" maps to code 0 (1 bit), zero to a longer code. The
            // decoder mirrors this dispatch using the same counts.
            var raw = state.CountMaxCBPLP < state.CountZeroCBPLP ? (iMax - iCBPLP) : iCBPLP;
            var (code, len) = CbplpYuv1Yuv444[raw];
            writer.WriteBits(code, len);
        }
        else
        {
            // Fixed-width 3-bit raw — no inversion on this path.
            writer.WriteBits((uint)iCBPLP, 3);
        }
    }

    private static int ReadCbplpYuv444(ref BitReader reader, MbLpState state)
    {
        const int iMax = 7;
        if (state.CountZeroCBPLP <= 0 || state.CountMaxCBPLP < 0)
        {
            var raw = DecodeCbplpYuv1Yuv444(ref reader);
            return state.CountMaxCBPLP < state.CountZeroCBPLP ? (iMax - raw) : raw;
        }
        return (int)reader.ReadBits(3);
    }

    /// <summary>Prefix-matched VLC decode of Table 55 (YUV444 CBPLP_YUV1).</summary>
    private static int DecodeCbplpYuv1Yuv444(ref BitReader reader)
    {
        // Code lengths are 1, 3, or 4 bits. Peek up to 4 and dispatch.
        // 0xxx       → 0 (consume 1)
        // 100x       → 1 (consume 3)
        // 101x       → 2 or 3 (consume 4) — distinguished by bit 3
        // 11xx       → 4..7 (consume 4)
        if (!reader.ReadBit())
            return 0;                          // code "0"

        var b1 = reader.ReadBit();             // bit 1
        if (!b1)
        {
            // "10" so far — code "100" (value 1) needs the next bit to be 0.
            var b2 = reader.ReadBit();
            if (!b2)
                return 1;                      // "100"
            // "101x" → value 2 (101 0) or 3 (101 1) — read bit 3
            var b3 = reader.ReadBit();
            return b3 ? 3 : 2;
        }
        // "11" → 4..7. Read two more bits as low nibble (xx).
        var hi = reader.ReadBit() ? 1 : 0;
        var lo = reader.ReadBit() ? 1 : 0;
        return 4 + (hi << 1) + lo;
    }

    /// <summary>
    /// T.832 Table 104 — adapt the CountZero / CountMax CBPLP counters after
    /// decoding each MB. The counts persist for the whole tile.
    /// </summary>
    private static void UpdateCbplpCounts(MbLpState state, int iCBPLP, int iMax)
    {
        state.CountZeroCBPLP = Clamp(state.CountZeroCBPLP + 1 - 4 * (iCBPLP == 0 ? 1 : 0), -8, 7);
        state.CountMaxCBPLP  = Clamp(state.CountMaxCBPLP  + 1 - 4 * (iCBPLP == iMax ? 1 : 0), -8, 7);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
