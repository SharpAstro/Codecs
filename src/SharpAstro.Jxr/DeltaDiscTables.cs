namespace SharpAstro.Jxr;

/// <summary>
/// deltaDisc tables from T.832 §8.8.2 (Tables 86–91). Each table is
/// indexed by <c>[m][n]</c> where <c>m</c> is the adaptive
/// <see cref="AdaptiveVlcState.DeltaTableIndex"/> or
/// <see cref="AdaptiveVlcState.Delta2TableIndex"/> (the deltaDisc table
/// to use, since adapting between code tables i and i+1 uses different
/// deltaDisc weights), and <c>n</c> is the just-encoded value of the
/// VLC syntax element.
/// </summary>
/// <remarks>
/// After encoding or decoding a value <c>v</c> of an adaptive syntax
/// element, the caller increments the discriminant accumulators by the
/// corresponding deltaDisc entries. The encoder/decoder then invokes
/// <see cref="AdaptiveVlc.AdaptTable1"/> / <see cref="AdaptiveVlc.AdaptTable2"/>
/// at band boundaries to actually switch code tables when the
/// discriminant has crossed a threshold (±8).
/// </remarks>
public static class DeltaDiscTables
{
    // Table 86 — AbslevelIndexDelta. 1 deltaDisc table (2 code tables for
    // ABS_LEVEL_INDEX → 1 delta), 7 values.
    public static readonly int[][] AbsLevelIndex =
    [
        [1, 0, -1, -1, -1, -1, -1],
    ];

    // Table 87 — FirstIndexDelta. 4 deltaDisc tables (5 code tables for
    // FIRST_INDEX → 4 deltas), 12 values each.
    public static readonly int[][] FirstIndex =
    [
        [1, 1, 1, 1, 1, 0, 0, -1, 2, 1, 0, 0],
        [2, 2, -1, -1, -1, 0, -2, -1, 0, 0, -2, -1],
        [-1, 1, 0, 2, 0, 0, 0, 0, -2, 0, 1, 1],
        [0, 1, 0, 1, -2, 0, -1, -1, -2, -1, -2, -2],
    ];

    // Table 88 — Index1Delta (for INDEX_A). 3 deltaDisc tables, 6 values each.
    public static readonly int[][] IndexA =
    [
        [-1, 1, 1, 1, 0, 1],
        [-2, 0, 0, 2, 0, 0],
        [-1, -1, 0, 1, -2, 0],
    ];

    // Table 89 — NumCBPHPDelta. 1 deltaDisc table, 5 values.
    public static readonly int[][] NumCbphp =
    [
        [0, -1, 0, 1, 1],
    ];

    // Table 90 — NumBlkCBPHPDelta for YONLY / NCOMPONENT / YUVK. 1 deltaDisc
    // table, 5 values.
    public static readonly int[][] NumBlkCbphpYuvk =
    [
        [0, -1, 0, 1, 1],
    ];

    // Table 91 — NumBlkCBPHPDelta for INTERNAL_CLR_FMT NOT in {YONLY,
    // NCOMPONENT, YUVK}. 1 deltaDisc table, 9 values.
    public static readonly int[][] NumBlkCbphpColour =
    [
        [2, 2, 1, 1, -1, -2, -2, -2, -3],
    ];

    /// <summary>
    /// Common helper — after coding value <paramref name="iVal"/> of a
    /// syntax element, accumulate the deltaDisc into both discriminants.
    /// Use <c>deltaTable</c> for the relevant deltaDisc table (one of the
    /// static arrays above). Two-table syntax elements (only DiscrimVal1)
    /// pass the same <paramref name="state"/>.Delta index and ignore Disc2.
    /// </summary>
    public static void AccumulateTable1(ref AdaptiveVlcState state, int[][] deltaTable, int iVal)
    {
        state.DiscrimVal1 += deltaTable[state.DeltaTableIndex][iVal];
    }

    /// <summary>
    /// Multi-table variant: both discriminants get hit. T.832 8.8.2 spec text.
    /// </summary>
    public static void AccumulateTable2(ref AdaptiveVlcState state, int[][] deltaTable, int iVal)
    {
        state.DiscrimVal1 += deltaTable[state.DeltaTableIndex][iVal];
        state.DiscrimVal2 += deltaTable[state.Delta2TableIndex][iVal];
    }
}
