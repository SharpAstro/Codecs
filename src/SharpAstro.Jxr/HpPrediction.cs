namespace SharpAstro.Jxr;

/// <summary>
/// High-pass (HP) coefficient prediction within macroblocks — T.832 §9.6.3.
/// </summary>
/// <remarks>
/// HP prediction is fundamentally simpler than DC/LP prediction: it is
/// <b>intra-macroblock only</b>. Each MB contains a 4×4 grid of 4×4 blocks
/// (16 blocks of 16 transform coefficients), and HP prediction transfers
/// energy from one block to an adjacent block within the same MB. There is
/// no cross-MB referencing here — that distinction is what lets the encoder
/// process each MB independently in this phase.
///
/// Three HP modes:
/// <list type="bullet">
///   <item>0 — predict from left: each block (except col 0) subtracts the
///     {4,8,12} coefficients of its left-neighbour block.</item>
///   <item>1 — predict from top: each block (except row 0) subtracts the
///     {1,2,3} coefficients of its top-neighbour block.</item>
///   <item>2 — no prediction.</item>
/// </list>
/// The decoder applies the prediction cumulatively along the scan direction
/// (each block reads the already-modified previous block). To invert this,
/// the encoder runs the blkId loop in <em>reverse order</em>, subtracting
/// against the still-original value of the previous block.
/// </remarks>
public static class HpPrediction
{
    /// <summary>
    /// T.832 9.6.3.2 / Table 135 — compute HP mode from LP coefficients of the
    /// current MB (positions 1..12 of MbDcLp). Pure function — no neighbour MB
    /// references, no QP dependency.
    /// </summary>
    public static int CalcMode(
        int[,,,] mbDcLp,
        int mbx, int mby,
        JxrInternalColorFormat format,
        int numComponents)
    {
        var strHor = Abs(mbDcLp[mbx, mby, 0, 1]) + Abs(mbDcLp[mbx, mby, 0, 2]) + Abs(mbDcLp[mbx, mby, 0, 3]);
        var strVer = Abs(mbDcLp[mbx, mby, 0, 4]) + Abs(mbDcLp[mbx, mby, 0, 8]) + Abs(mbDcLp[mbx, mby, 0, 12]);

        if (format != JxrInternalColorFormat.YOnly && format != JxrInternalColorFormat.NComponent && numComponents >= 3)
        {
            for (var c = 1; c <= 2; c++)
            {
                strHor += Abs(mbDcLp[mbx, mby, c, 1]);
                if (format == JxrInternalColorFormat.YUV420)
                {
                    strVer += Abs(mbDcLp[mbx, mby, c, 2]);
                }
                else if (format == JxrInternalColorFormat.YUV422)
                {
                    strVer += Abs(mbDcLp[mbx, mby, c, 2]) + Abs(mbDcLp[mbx, mby, c, 6]);
                    strHor += Abs(mbDcLp[mbx, mby, c, 5]);
                }
                else
                {
                    strVer += Abs(mbDcLp[mbx, mby, c, 4]);
                }
            }
        }

        const int iOrWt = 4;
        if (strHor * iOrWt < strVer) return 0; // Predict from left (vertical gradient)
        if (strVer * iOrWt < strHor) return 1; // Predict from top  (horizontal gradient)
        return 2;                              // No prediction
    }

    /// <summary>
    /// Forward (encoder) HP prediction. Subtracts the intra-MB prediction from
    /// each affected block coefficient. <paramref name="mbHp"/> is a 5D buffer
    /// indexed <c>[mbx, mby, component, blockIdx 0..15, position 0..15]</c>;
    /// position 0 is DC (not touched here — it's already in MbDcLp), positions
    /// 1..15 are the AC coefficients of that 4×4 block.
    /// </summary>
    public static void Encode(
        int[,,,,] mbHp,
        int[,] mbHpMode,
        JxrInternalColorFormat format)
    {
        Process(mbHp, mbHpMode, format, addNotSubtract: false);
    }

    /// <summary>
    /// Inverse (decoder) HP prediction — T.832 9.6.3.3 / Table 136. Adds back
    /// the intra-MB prediction.
    /// </summary>
    public static void Decode(
        int[,,,,] mbHp,
        int[,] mbHpMode,
        JxrInternalColorFormat format)
    {
        Process(mbHp, mbHpMode, format, addNotSubtract: true);
    }

    private static void Process(int[,,,,] mbHp, int[,] mbHpMode, JxrInternalColorFormat format, bool addNotSubtract)
    {
        var mbWidth = mbHp.GetLength(0);
        var mbHeight = mbHp.GetLength(1);
        var numComponents = mbHp.GetLength(2);

        for (var mby = 0; mby < mbHeight; mby++)
        {
            for (var mbx = 0; mbx < mbWidth; mbx++)
            {
                var mode = mbHpMode[mbx, mby];
                if (mode == 2) continue; // No prediction

                // For YUV420 / YUV422, only the luma plane participates in the main
                // intra-MB prediction loop. Chroma planes have their own (smaller)
                // block lists handled below.
                var lumaComponents = (format == JxrInternalColorFormat.YUV420 || format == JxrInternalColorFormat.YUV422)
                    ? 1
                    : numComponents;

                for (var c = 0; c < lumaComponents; c++)
                    PredictBlocks(mbHp, mbx, mby, c, mode, BlocksFull(mode), DeltaFull(mode), PositionsFull(mode), addNotSubtract);

                if (format == JxrInternalColorFormat.YUV420)
                {
                    // Chroma 2x2 col-major. LEFT (col 1) delta 2; TOP (row 1) delta 1.
                    for (var c = 1; c < numComponents; c++)
                        PredictBlocks(mbHp, mbx, mby, c, mode,
                            mode == 0 ? Yuv420LeftBlocks : Yuv420TopBlocks,
                            mode == 0 ? 2 : 1,
                            mode == 0 ? LeftPositions : TopPositions,
                            addNotSubtract);
                }
                else if (format == JxrInternalColorFormat.YUV422)
                {
                    // Chroma 2x4 col-major. LEFT (col 1) delta 4; TOP (rows 1..3) delta 1.
                    for (var c = 1; c < numComponents; c++)
                        PredictBlocks(mbHp, mbx, mby, c, mode,
                            mode == 0 ? Yuv422LeftBlocks : Yuv422TopBlocks,
                            mode == 0 ? 4 : 1,
                            mode == 0 ? LeftPositions : TopPositions,
                            addNotSubtract);
                }
            }
        }
    }

    private static void PredictBlocks(
        int[,,,,] mbHp,
        int mbx, int mby, int c,
        int mode,
        int[] blkIds, int delta, int[] positions,
        bool addNotSubtract)
    {
        // Decoder applies prediction cumulatively in forward order; encoder
        // must process in REVERSE so the subtraction sees still-actual data
        // in the reference block.
        if (addNotSubtract)
        {
            for (var j = 0; j < blkIds.Length; j++)
                ApplyBlock(mbHp, mbx, mby, c, blkIds[j], delta, positions, addNotSubtract: true);
        }
        else
        {
            for (var j = blkIds.Length - 1; j >= 0; j--)
                ApplyBlock(mbHp, mbx, mby, c, blkIds[j], delta, positions, addNotSubtract: false);
        }
    }

    private static void ApplyBlock(
        int[,,,,] mbHp,
        int mbx, int mby, int c,
        int blkId, int delta, int[] positions,
        bool addNotSubtract)
    {
        var refBlk = blkId - delta;
        foreach (var pos in positions)
        {
            if (addNotSubtract)
                mbHp[mbx, mby, c, blkId, pos] += mbHp[mbx, mby, c, refBlk, pos];
            else
                mbHp[mbx, mby, c, blkId, pos] -= mbHp[mbx, mby, c, refBlk, pos];
        }
    }

    // Block-index lists. Column-major (blkIdx = col*4 + row), matching jxrlib's
    // blkOffset[] storage convention. LEFT prediction touches blocks in
    // cols 1..3 (= blkIdx 4..15), TOP prediction touches blocks in rows
    // 1..3 of each column (= blkIdx {1,2,3, 5,6,7, 9,10,11, 13,14,15}).
    private static readonly int[] LumaLeftBlocks  = [4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
    private static readonly int[] LumaTopBlocks   = [1, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15];
    // YUV420 chroma 2x2 col-major: blocks 0=(0,0), 1=(0,1), 2=(1,0), 3=(1,1).
    // LEFT (col 1) {2,3} delta 2; TOP (row 1) {1,3} delta 1.
    private static readonly int[] Yuv420LeftBlocks = [2, 3];
    private static readonly int[] Yuv420TopBlocks  = [1, 3];
    // YUV422 chroma 2x4 col-major. LEFT (col 1) {4..7} delta 4;
    // TOP (rows 1..3) {1,2,3, 5,6,7} delta 1.
    private static readonly int[] Yuv422LeftBlocks = [4, 5, 6, 7];
    private static readonly int[] Yuv422TopBlocks  = [1, 2, 3, 5, 6, 7];

    // From jxrlib's reference encoder (predMacroblockEnc, AC prediction loops):
    // predict-from-LEFT modifies positions {1, 5, 6}, predict-from-TOP modifies
    // {2, 9, 10}. Verified via instrumented stderr trace. These are the
    // HF-coefficient positions that adjoin the chosen neighbour-block edge
    // after the PCT permutation.
    private static readonly int[] LeftPositions = [1, 5, 6];
    private static readonly int[] TopPositions  = [2, 9, 10];

    private static int[] BlocksFull(int mode) => mode == 0 ? LumaLeftBlocks : LumaTopBlocks;
    // Column-major deltas: mode 0 (LEFT) steps by 4 (one column),
    // mode 1 (TOP) steps by 1 (one row).
    private static int DeltaFull(int mode) => mode == 0 ? 4 : 1;
    private static int[] PositionsFull(int mode) => mode == 0 ? LeftPositions : TopPositions;

    private static int Abs(int x) => x < 0 ? -x : x;
}
