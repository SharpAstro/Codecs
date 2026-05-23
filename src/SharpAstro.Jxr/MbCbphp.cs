namespace SharpAstro.Jxr;

/// <summary>
/// MB-level CBPHP signalling — T.832 §8.7.17 (MB_CBPHP) plus
/// REFINE_CBPHP (§8.7.17.3). Encodes/decodes the "which 4×4 blocks
/// within an MB have any non-zero HP coefficient" bitmap.
/// </summary>
/// <remarks>
/// The structure is hierarchical: an MB's 16 blocks are grouped into
/// 4 block-groups of 2×2 blocks each. NUM_CBPHP encodes how many
/// groups have any non-zero block; REFINE_CBPHP picks which specific
/// groups. For each non-zero group, NUM_BLKCBPHP + CODE_INC encode
/// the 4-bit per-group pattern via the iOff / iFLC / iOut tables.
/// <para>
/// This first cut handles the luma-only / per-component path
/// (INTERNAL_CLR_FMT ∈ {YOnly, YUVK, NComponent}) without CBPHP
/// prediction — produces and consumes the residual iDiffCBPHP
/// directly. Adding the prediction layer (PredCBPHP444/422/420 + its
/// per-tile state) is a separate concern.
/// </para>
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
    /// Encode the per-component CBPHP bitmap for one MB
    /// (T.832 §8.7.17.2 for INTERNAL_CLR_FMT ∈ {YOnly, YUVK, NComponent}).
    /// Each <paramref name="cbphp"/> value is a 16-bit pattern: bit <c>k</c>
    /// marks the <c>k</c>-th iteration block as "any non-zero HP" in the
    /// hierarchical scan order (see <see cref="MbHp.HierScanOrder"/>).
    /// </summary>
    public static void EncodeMb(
        BitWriter writer,
        MbCbphpState state,
        int numComponents,
        ReadOnlySpan<int> cbphp)
    {
        if (cbphp.Length < numComponents)
            throw new ArgumentException($"cbphp must hold ≥ {numComponents} ints", nameof(cbphp));

        for (var c = 0; c < numComponents; c++)
            EncodeComponent(writer, state, cbphp[c]);
    }

    /// <summary>
    /// Decode the per-component CBPHP bitmap for one MB. Writes 16-bit
    /// patterns into <paramref name="cbphpOut"/> (one per component).
    /// </summary>
    public static void DecodeMb(
        ref BitReader reader,
        MbCbphpState state,
        int numComponents,
        Span<int> cbphpOut)
    {
        if (cbphpOut.Length < numComponents)
            throw new ArgumentException($"cbphpOut must hold ≥ {numComponents} ints", nameof(cbphpOut));

        for (var c = 0; c < numComponents; c++)
            cbphpOut[c] = DecodeComponent(ref reader, state);
    }

    // -----------------------------------------------------------------------
    // Per-component encode/decode — the hierarchical NUM_CBPHP → REFINE_CBPHP
    // → NUM_BLKCBPHP → CODE_INC chain that produces the 16-bit pattern.
    // -----------------------------------------------------------------------

    private static void EncodeComponent(BitWriter writer, MbCbphpState state, int diffCbphp)
    {
        // Group-level: which of the 4 block-groups have any non-zero blocks?
        // Each group corresponds to 4 contiguous bits of diffCbphp:
        //   group 0 = bits 0..3, group 1 = bits 4..7, group 2 = bits 8..11, group 3 = bits 12..15.
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

        // REFINE_CBPHP: encode the specific 4-bit groupBits given NUM_CBPHP.
        EmitRefineCbphp(writer, numCbphp, groupBits);

        // For each non-zero group, encode NUM_BLKCBPHP + CODE_INC.
        for (var g = 0; g < 4; g++)
        {
            if ((groupBits & (1 << g)) == 0) continue;
            var pattern = blkCbphpPerGroup[g];
            EncodeBlkCbphp(writer, state, pattern);
        }
    }

    private static int DecodeComponent(ref BitReader reader, MbCbphpState state)
    {
        var numCbphp = VlcTables.NumCbphp[state.NumCbphp.TableIndex].Decode(ref reader);
        DeltaDiscTables.AccumulateTable1(ref state.NumCbphp, DeltaDiscTables.NumCbphp, numCbphp);

        var groupBits = ParseRefineCbphp(ref reader, numCbphp);

        var diffCbphp = 0;
        for (var g = 0; g < 4; g++)
        {
            if ((groupBits & (1 << g)) == 0) continue;
            var pattern = DecodeBlkCbphp(ref reader, state);
            diffCbphp |= pattern << (g * 4);
        }
        return diffCbphp;
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
                // groupBits = 1 << bitPos, where bitPos in 0..3
                writer.WriteBits((uint)BitPosOf(groupBits), 2); // REF_CBPHP u(2)
                break;
            case 2:
                // 6 distinct 2-bit patterns from {3,5,6,9,10,12} — use REF_CBPHP1 VLC.
                VlcTables.RefCbphp1.Encode(writer, groupBits);
                break;
            case 3:
                // groupBits = 0xF ^ (1 << bitPos)
                writer.WriteBits((uint)BitPosOf(0xF ^ groupBits), 2);
                break;
            case 4:
                // groupBits = 0xF — no bits to emit.
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

    // -----------------------------------------------------------------------
    // NUM_BLKCBPHP + CODE_INC — encodes/decodes a 4-bit per-group pattern via
    // the iVal → iOff/iFLC → iCode → iOut indirection table.
    // -----------------------------------------------------------------------

    private static void EncodeBlkCbphp(BitWriter writer, MbCbphpState state, int blkPattern)
    {
        if (blkPattern is < 1 or > 15)
            throw new ArgumentOutOfRangeException(nameof(blkPattern), "block pattern must be in [1, 15] (non-zero 4-bit pattern)");

        var iCode = BlkCbphpToICode[blkPattern];
        // Determine iVal (1..5) from iCode range.
        int iVal = iCode switch
        {
            1 => 5,
            >= 2 and <= 3 => 2,
            >= 4 and <= 7 => 1,
            >= 8 and <= 11 => 3,
            >= 12 and <= 15 => 4,
            _ => throw new InvalidOperationException($"iCode {iCode} out of expected range for blkPattern {blkPattern}"),
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

    private static int DecodeBlkCbphp(ref BitReader reader, MbCbphpState state)
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
