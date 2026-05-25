namespace SharpAstro.Jxr;

/// <summary>
/// MB-level CBPHP signalling — T.832 §8.7.17 (MB_CBPHP) plus REFINE_CBPHP
/// (§8.7.17.3) and PredCBPHP444 (§8.7.17.5.2). Encodes/decodes the
/// "which 4×4 blocks within an MB have any non-zero HP coefficient" bitmap.
/// </summary>
/// <remarks>
/// Two algorithms:
/// <list type="bullet">
///   <item>YOnly / YUVK / NComponent — iComponent = NumComponents, one
///         independent NUM_CBPHP / REFINE_CBPHP / NUM_BLKCBPHP / CODE_INC
///         chain per component. Uses Table 60 NUM_BLKCBPHP (5 values).</item>
///   <item>YUV444 — iComponent = 1, a single chain emits luma + both
///         chroma components together. Uses Table 61 NUM_BLKCBPHP (9
///         values), with CHR_CBPHP / VAL_INC / NUM_CH_BLK extending the
///         luma pattern to cover the chroma blocks within each block
///         group. YUV422 / YUV420 follow similar shapes but with
///         different per-group block counts and aren't implemented yet.</item>
/// </list>
/// PredCBPHP444 (§8.7.17.5.2) wraps both paths: the entropy-coded value
/// is the residual <c>iDiffCBPHP</c>, the actual macroblock CBPHP is
/// recovered by applying a state-dependent transform against the
/// neighbour-MBCBPHP values.
/// </remarks>
public static class MbCbphp
{
    // T.832 §8.7.17.2 constants.
    private static ReadOnlySpan<byte> IFlc => [0, 2, 1, 2, 2, 0];
    private static ReadOnlySpan<byte> IOff => [0, 4, 2, 8, 12, 1];
    private static ReadOnlySpan<byte> IOut => [0, 15, 3, 12, 1, 2, 4, 8, 5, 6, 9, 10, 7, 11, 13, 14];

    // Inverse of IOut: maps a 4-bit pattern back to its iCode (computed at load).
    private static readonly byte[] BlkCbphpToICode = BuildInverseIOut();

    private static byte[] BuildInverseIOut()
    {
        var inv = new byte[16];
        for (var iCode = 0; iCode < 16; iCode++)
            inv[IOut[iCode]] = (byte)iCode;
        return inv;
    }

    /// <summary>
    /// Encode the per-component CBPHP bitmap for one MB, then apply the
    /// inverse PredCBPHP444 transform and emit the residual into the
    /// bitstream. <paramref name="cbphp"/> values are the actual MBCBPHP
    /// (16-bit per-component pattern) — the encoder converts them to the
    /// residual <c>iDiffCBPHP</c> the spec calls for.
    /// </summary>
    public static void EncodeMb(
        BitWriter writer,
        MbCbphpState state,
        JxrInternalColorFormat format,
        int numComponents,
        int mbX, int mbY,
        bool isLeftEdge, bool isTopEdge,
        ReadOnlySpan<int> cbphp)
    {
        if (cbphp.Length < numComponents)
            throw new ArgumentException($"cbphp must hold ≥ {numComponents} ints", nameof(cbphp));

        // Convert actual MBCBPHP[i] → residual iDiffCBPHP[i] via the inverse
        // predictor transform. (Decode reverses this; both sides keep the
        // predictor state in lock-step.)
        Span<int> iDiff = new int[16];
        for (var c = 0; c < numComponents; c++)
        {
            var top = state.LoadMbCbphp(mbX, mbY - 1, c);
            var left = state.LoadMbCbphp(mbX - 1, mbY, c);
            iDiff[c] = CbphpPrediction.Encode(cbphp[c], state.Predictor, c, isLeftEdge, isTopEdge, top, left);
            state.StoreMbCbphp(mbX, mbY, c, cbphp[c]);
        }

        if (format == JxrInternalColorFormat.YUV444)
        {
            EncodeYuv444(writer, state, iDiff);
        }
        else if (format == JxrInternalColorFormat.YUV422 || format == JxrInternalColorFormat.YUV420)
        {
            throw new NotSupportedException(
                $"MbCbphp.EncodeMb: {format} CBPHP encoding not yet implemented");
        }
        else
        {
            for (var c = 0; c < numComponents; c++)
                EncodeComponentYuvk(writer, state, iDiff[c]);
        }
    }

    /// <summary>
    /// Decode the per-component CBPHP bitmap for one MB. Reverses the
    /// PredCBPHP444 transform so callers see the actual MBCBPHP (not the
    /// residual that's coded on the wire).
    /// </summary>
    public static void DecodeMb(
        ref BitReader reader,
        MbCbphpState state,
        JxrInternalColorFormat format,
        int numComponents,
        int mbX, int mbY,
        bool isLeftEdge, bool isTopEdge,
        Span<int> cbphpOut)
    {
        if (cbphpOut.Length < numComponents)
            throw new ArgumentException($"cbphpOut must hold ≥ {numComponents} ints", nameof(cbphpOut));

        Span<int> iDiff = new int[16];
        if (format == JxrInternalColorFormat.YUV444)
        {
            DecodeYuv444(ref reader, state, iDiff);
        }
        else if (format == JxrInternalColorFormat.YUV422 || format == JxrInternalColorFormat.YUV420)
        {
            throw new NotSupportedException(
                $"MbCbphp.DecodeMb: {format} CBPHP decoding not yet implemented");
        }
        else
        {
            for (var c = 0; c < numComponents; c++)
                iDiff[c] = DecodeComponentYuvk(ref reader, state);
        }

        for (var c = 0; c < numComponents; c++)
        {
            var top = state.LoadMbCbphp(mbX, mbY - 1, c);
            var left = state.LoadMbCbphp(mbX - 1, mbY, c);
            cbphpOut[c] = CbphpPrediction.Decode(iDiff[c], state.Predictor, c, isLeftEdge, isTopEdge, top, left);
            state.StoreMbCbphp(mbX, mbY, c, cbphpOut[c]);
        }
    }

    // -----------------------------------------------------------------------
    // YONLY / YUVK / NCOMPONENT per-component encode / decode
    // -----------------------------------------------------------------------

    private static void EncodeComponentYuvk(BitWriter writer, MbCbphpState state, int diffCbphp)
    {
        var groupBits = 0;
        Span<int> blkCbphpPerGroup = stackalloc int[4];
        for (var g = 0; g < 4; g++)
        {
            var pattern = (diffCbphp >> (g * 4)) & 0xF;
            blkCbphpPerGroup[g] = pattern;
            if (pattern != 0) groupBits |= 1 << g;
        }

        var numCbphp = PopCount(groupBits);
        VlcTables.NumCbphp[state.NumCbphp.TableIndex].Encode(writer, numCbphp);
        DeltaDiscTables.AccumulateTable1(ref state.NumCbphp, DeltaDiscTables.NumCbphp, numCbphp);

        EmitRefineCbphp(writer, numCbphp, groupBits);

        for (var g = 0; g < 4; g++)
        {
            if ((groupBits & (1 << g)) == 0) continue;
            EncodeBlkCbphpYuvk(writer, state, blkCbphpPerGroup[g]);
        }
    }

    private static int DecodeComponentYuvk(ref BitReader reader, MbCbphpState state)
    {
        var numCbphp = VlcTables.NumCbphp[state.NumCbphp.TableIndex].Decode(ref reader);
        DeltaDiscTables.AccumulateTable1(ref state.NumCbphp, DeltaDiscTables.NumCbphp, numCbphp);

        var groupBits = ParseRefineCbphp(ref reader, numCbphp);

        var diffCbphp = 0;
        for (var g = 0; g < 4; g++)
        {
            if ((groupBits & (1 << g)) == 0) continue;
            var pattern = DecodeBlkCbphpYuvk(ref reader, state);
            diffCbphp |= pattern << (g * 4);
        }
        return diffCbphp;
    }

    private static void EncodeBlkCbphpYuvk(BitWriter writer, MbCbphpState state, int blkPattern)
    {
        if (blkPattern is < 1 or > 15)
            throw new ArgumentOutOfRangeException(nameof(blkPattern), "block pattern must be in [1, 15]");

        var iCode = BlkCbphpToICode[blkPattern];
        int iVal = iCode switch
        {
            1 => 5,
            >= 2 and <= 3 => 2,
            >= 4 and <= 7 => 1,
            >= 8 and <= 11 => 3,
            >= 12 and <= 15 => 4,
            _ => throw new InvalidOperationException($"iCode {iCode} out of range for blkPattern {blkPattern}"),
        };
        var numBlkCbphp = iVal - 1;
        VlcTables.NumBlkCbphpYuvk[state.NumBlkCbphp.TableIndex].Encode(writer, numBlkCbphp);
        DeltaDiscTables.AccumulateTable1(ref state.NumBlkCbphp, DeltaDiscTables.NumBlkCbphpYuvk, numBlkCbphp);

        var fixedLen = IFlc[iVal];
        if (fixedLen > 0)
        {
            var codeInc = iCode - IOff[iVal];
            writer.WriteBits((uint)codeInc, fixedLen);
        }
    }

    private static int DecodeBlkCbphpYuvk(ref BitReader reader, MbCbphpState state)
    {
        var numBlkCbphp = VlcTables.NumBlkCbphpYuvk[state.NumBlkCbphp.TableIndex].Decode(ref reader);
        DeltaDiscTables.AccumulateTable1(ref state.NumBlkCbphp, DeltaDiscTables.NumBlkCbphpYuvk, numBlkCbphp);

        var iVal = numBlkCbphp + 1;
        if (iVal < 1 || iVal > 5)
            throw new InvalidDataException($"NUM_BLKCBPHP+1 = {iVal} out of range for YOnly/YUVK/NComponent (expected 1..5)");

        var iCode = (int)IOff[iVal];
        if (IFlc[iVal] > 0)
            iCode += (int)reader.ReadBits(IFlc[iVal]);
        return IOut[iCode];
    }

    // -----------------------------------------------------------------------
    // YUV444 joint encode / decode (T.832 §8.7.17.2, YUV444 branch)
    // -----------------------------------------------------------------------

    private static void DecodeYuv444(ref BitReader reader, MbCbphpState state, Span<int> iDiffOut)
    {
        var numCbphp = VlcTables.NumCbphp[state.NumCbphp.TableIndex].Decode(ref reader);
        DeltaDiscTables.AccumulateTable1(ref state.NumCbphp, DeltaDiscTables.NumCbphp, numCbphp);
        var groupBits = ParseRefineCbphp(ref reader, numCbphp);

        var diffY = 0;
        var diffU = 0;
        var diffV = 0;

        for (var iBlock = 0; iBlock < 4; iBlock++)
        {
            if ((groupBits & (1 << iBlock)) == 0) continue;

            // NUM_BLKCBPHP using the colour table (9 values).
            var numBlkCbphp = VlcTables.NumBlkCbphpColour[state.NumBlkCbphp.TableIndex].Decode(ref reader);
            DeltaDiscTables.AccumulateTable1(ref state.NumBlkCbphp, DeltaDiscTables.NumBlkCbphpColour, numBlkCbphp);
            var iVal = numBlkCbphp + 1;       // 1..9
            var iBlkCbphp = 0;

            if (iVal >= 6)
            {
                var chrCbphp = VlcTables.ChrCbphp.Decode(ref reader);   // 0=U, 1=V, 2=both
                iBlkCbphp = 0x10 * (chrCbphp + 1);
                if (iVal >= 9)
                {
                    var valInc = VlcTables.ChrCbphp.Decode(ref reader);
                    iVal += valInc;             // 9, 10, or 11
                }
                iVal -= 6;                      // now 0..5
            }

            var iCode = (int)IOff[iVal];
            if (IFlc[iVal] > 0)
                iCode += (int)reader.ReadBits(IFlc[iVal]);
            iBlkCbphp += IOut[iCode];           // luma 4-bit pattern in low nibble

            diffY |= (iBlkCbphp & 0x0F) << (iBlock * 4);

            // Chroma block-group selectors (NUM_CH_BLK + REFINE_CBPHP).
            for (var k = 0; k < 2; k++)
            {
                if (((iBlkCbphp >> (k + 4)) & 1) == 0) continue;
                var numChBlk = VlcTables.NumChBlk.Decode(ref reader);
                var chrPattern = ParseRefineCbphp(ref reader, numChBlk + 1);
                if (k == 0) diffU |= chrPattern << (iBlock * 4);
                else        diffV |= chrPattern << (iBlock * 4);
            }
        }

        iDiffOut[0] = diffY;
        iDiffOut[1] = diffU;
        iDiffOut[2] = diffV;
    }

    private static void EncodeYuv444(BitWriter writer, MbCbphpState state, ReadOnlySpan<int> iDiff)
    {
        var diffY = iDiff[0];
        var diffU = iDiff[1];
        var diffV = iDiff[2];

        // groupBits = any non-zero data anywhere in the group across Y / U / V.
        var groupBits = 0;
        for (var g = 0; g < 4; g++)
        {
            var yg = (diffY >> (g * 4)) & 0xF;
            var ug = (diffU >> (g * 4)) & 0xF;
            var vg = (diffV >> (g * 4)) & 0xF;
            if ((yg | ug | vg) != 0) groupBits |= 1 << g;
        }

        var numCbphp = PopCount(groupBits);
        VlcTables.NumCbphp[state.NumCbphp.TableIndex].Encode(writer, numCbphp);
        DeltaDiscTables.AccumulateTable1(ref state.NumCbphp, DeltaDiscTables.NumCbphp, numCbphp);
        EmitRefineCbphp(writer, numCbphp, groupBits);

        for (var g = 0; g < 4; g++)
        {
            if ((groupBits & (1 << g)) == 0) continue;

            var yg = (diffY >> (g * 4)) & 0xF;
            var ug = (diffU >> (g * 4)) & 0xF;
            var vg = (diffV >> (g * 4)) & 0xF;
            var chrCbphpPlus1 = (ug != 0 ? 1 : 0) | (vg != 0 ? 2 : 0); // 0=none,1=U,2=V,3=both

            int iCode = yg == 0 ? 0 : BlkCbphpToICode[yg];

            int iValFinal;     // post-adjustment iVal (0..5) used for iOff/iFLC dispatch
            int numBlkRaw;     // NUM_BLKCBPHP value written to the bitstream
            int valInc = 0;
            bool emitValInc = false;

            if (chrCbphpPlus1 == 0)
            {
                // Luma-only path: iVal final = 1..5 (luma table iOff/iFLC).
                iValFinal = iCode switch
                {
                    1 => 5,
                    >= 2 and <= 3 => 2,
                    >= 4 and <= 7 => 1,
                    >= 8 and <= 11 => 3,
                    >= 12 and <= 15 => 4,
                    _ => throw new InvalidOperationException($"iCode {iCode} unexpected for luma-only encode (Y_g={yg})"),
                };
                numBlkRaw = iValFinal - 1;
            }
            else
            {
                // Chroma case. iVal final (= post-subtract-6) selects iOff/iFLC; original
                // iVal (pre-subtract) selects NUM_BLKCBPHP + VAL_INC sub-table.
                iValFinal = iCode switch
                {
                    0 => 0,                      // Y_g empty: iVal pre = 6 (NUM=5)
                    1 => 5,                      // iVal pre = 11 (NUM=8, VAL_INC=2)
                    >= 2 and <= 3 => 2,         // iVal pre = 8 (NUM=7)
                    >= 4 and <= 7 => 1,         // iVal pre = 7 (NUM=6)
                    >= 8 and <= 11 => 3,        // iVal pre = 9 (NUM=8, VAL_INC=0)
                    >= 12 and <= 15 => 4,       // iVal pre = 10 (NUM=8, VAL_INC=1)
                    _ => throw new InvalidOperationException($"iCode {iCode} unexpected for chroma encode"),
                };

                var iValPre = iValFinal switch
                {
                    0 => 6,
                    1 => 7,
                    2 => 8,
                    3 => 9,
                    4 => 9,
                    5 => 9,
                    _ => throw new InvalidOperationException(),
                };
                if (iValFinal >= 3)
                {
                    valInc = iValFinal - 3;     // 0, 1, or 2 → VAL_INC
                    emitValInc = true;
                }
                numBlkRaw = iValPre - 1;        // NUM_BLKCBPHP final value (5..8)
            }

            VlcTables.NumBlkCbphpColour[state.NumBlkCbphp.TableIndex].Encode(writer, numBlkRaw);
            DeltaDiscTables.AccumulateTable1(ref state.NumBlkCbphp, DeltaDiscTables.NumBlkCbphpColour, numBlkRaw);

            if (chrCbphpPlus1 != 0)
            {
                VlcTables.ChrCbphp.Encode(writer, chrCbphpPlus1 - 1);
                if (emitValInc) VlcTables.ChrCbphp.Encode(writer, valInc);
            }

            var fixedLen = IFlc[iValFinal];
            if (fixedLen > 0)
            {
                var codeInc = iCode - IOff[iValFinal];
                writer.WriteBits((uint)codeInc, fixedLen);
            }

            // NUM_CH_BLK + REFINE_CBPHP for each chroma component with non-zero pattern.
            if (ug != 0)
            {
                EmitChromaBlockSelector(writer, ug);
            }
            if (vg != 0)
            {
                EmitChromaBlockSelector(writer, vg);
            }
        }
    }

    private static void EmitChromaBlockSelector(BitWriter writer, int pattern)
    {
        // pattern is a 4-bit value 1..15 — emit NUM_CH_BLK (= numCbphp - 1) + REFINE_CBPHP.
        var numBlk = PopCount(pattern);
        if (numBlk == 0)
            throw new InvalidOperationException("chroma pattern must be non-zero");
        VlcTables.NumChBlk.Encode(writer, numBlk - 1);
        EmitRefineCbphp(writer, numBlk, pattern);
    }

    // -----------------------------------------------------------------------
    // REFINE_CBPHP — T.832 §8.7.17.3 / Table 58
    // -----------------------------------------------------------------------

    private static void EmitRefineCbphp(BitWriter writer, int iNum, int groupBits)
    {
        switch (iNum)
        {
            case 0:
                if (groupBits != 0) throw new ArgumentException("iNum=0 must mean groupBits=0");
                break;
            case 1:
                writer.WriteBits((uint)BitPosOf(groupBits), 2);
                break;
            case 2:
                VlcTables.RefCbphp1.Encode(writer, groupBits);
                break;
            case 3:
                writer.WriteBits((uint)BitPosOf(0xF ^ groupBits), 2);
                break;
            case 4:
                if (groupBits != 0xF) throw new ArgumentException("iNum=4 must mean groupBits=0xF");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(iNum));
        }
    }

    private static int ParseRefineCbphp(ref BitReader reader, int iNum) => iNum switch
    {
        0 => 0,
        1 => 1 << (int)reader.ReadBits(2),
        2 => VlcTables.RefCbphp1.Decode(ref reader),
        3 => 0xF ^ (1 << (int)reader.ReadBits(2)),
        4 => 0xF,
        _ => throw new InvalidDataException($"invalid NUM_CBPHP value {iNum}"),
    };

    private static int PopCount(int x)
    {
        var c = 0;
        for (var i = 0; i < 4; i++)
            if ((x & (1 << i)) != 0) c++;
        return c;
    }

    private static int BitPosOf(int x)
    {
        for (var i = 0; i < 4; i++)
            if (x == 1 << i) return i;
        throw new ArgumentException($"value {x} is not a single-bit mask in low nibble");
    }
}
