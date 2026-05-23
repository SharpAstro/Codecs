namespace SharpAstro.Jxr;

/// <summary>
/// Scan-order layer wrapping <see cref="Block"/> — T.832 §8.7.18.4
/// (DECODE_BLOCK_ADAPTIVE) plus its forward dual.
/// </summary>
/// <remarks>
/// Inside <see cref="Block"/> a block is represented as a flat
/// <c>(run, level)*</c> sequence in scan order. Real callers want a
/// 16-position raster-order block buffer where each slot maps to a
/// specific position in the 4×4 transform output. The adaptive scan
/// (T.832 §8.11, see <see cref="AdaptiveScan"/>) is the bijection
/// between the two: scan-index <c>i ∈ 1..15</c> → block-position
/// <c>scanOrder[i] ∈ 1..15</c>. Position 0 is reserved for DC and is
/// not touched by this code.
///
/// The scan adapts based on observed non-zero coefficient frequencies:
/// only non-zero coefficients call <see cref="AdaptiveScan.Step"/>, so
/// positions that frequently hold non-zeros migrate to lower
/// scan-indices, putting them earlier in the bitstream (where the
/// Block-level encoding is more compact).
/// </remarks>
public static class BlockAdaptive
{
    /// <summary>
    /// Decode the next block's coefficients into <paramref name="blockCoeffs"/>
    /// at raster positions 1..15. Position 0 is untouched. Returns the
    /// number of non-zero coefficients decoded.
    /// </summary>
    /// <param name="reader">Bit-stream reader.</param>
    /// <param name="blockCtx">5-state AdaptiveVlc context for this band/chroma.</param>
    /// <param name="scan">Adaptive scan state for this band+direction (HP horizontal vs vertical, or LP).</param>
    /// <param name="blockCoeffs">16-int raster-order buffer. Positions 1..15 are zeroed then populated.</param>
    public static int Decode(
        ref BitReader reader,
        ref BlockCodingContext blockCtx,
        AdaptiveScan scan,
        Span<int> blockCoeffs)
    {
        if (blockCoeffs.Length < 16)
            throw new ArgumentException("blockCoeffs must be at least 16 ints", nameof(blockCoeffs));

        // Zero positions 1..15. Position 0 (DC) is the caller's responsibility.
        for (var i = 1; i < 16; i++) blockCoeffs[i] = 0;

        var localBuf = new int[32];
        var iNumNonZero = Block.Decode(ref reader, ref blockCtx, localBuf, iLocation: 1);

        // Walk the (run, level) pairs in scan order, resolving each parse-index
        // to a block position via the scan and updating the scan state per
        // T.832 8.11.7 AdaptiveHPScan.
        var k = 1; // running parse-index, starts at iLocation = 1
        for (var kk = 0; kk < iNumNonZero; kk++)
        {
            k += localBuf[kk * 2];               // add the run
            var blockPos = scan.Step(k);         // resolve + adapt scan
            blockCoeffs[blockPos] = localBuf[kk * 2 + 1];
            k++;
        }

        return iNumNonZero;
    }

    /// <summary>
    /// Encode one block's coefficients. Reads positions 1..15 of
    /// <paramref name="blockCoeffs"/>; position 0 is ignored.
    /// </summary>
    public static int Encode(
        BitWriter writer,
        ref BlockCodingContext blockCtx,
        AdaptiveScan scan,
        ReadOnlySpan<int> blockCoeffs)
    {
        if (blockCoeffs.Length < 16)
            throw new ArgumentException("blockCoeffs must be at least 16 ints", nameof(blockCoeffs));

        // Collect (run, level) pairs in scan order, matching the decoder's
        // adapt-on-non-zero semantics (only Step the scan for non-zero hits).
        Span<int> pairs = stackalloc int[32];
        var iNumNonZero = 0;
        var pendingRun = 0;
        for (var i = 1; i <= 15; i++)
        {
            var blockPos = scan.Position(i);     // peek without adapting
            var value = blockCoeffs[blockPos];
            if (value != 0)
            {
                pairs[iNumNonZero * 2] = pendingRun;
                pairs[iNumNonZero * 2 + 1] = value;
                iNumNonZero++;
                scan.Step(i);                    // commit: resolve + adapt
                pendingRun = 0;
            }
            else
            {
                pendingRun++;
            }
        }

        if (iNumNonZero == 0)
            return 0; // No bits emitted; caller must signal "skip" via CBPHP

        Block.Encode(writer, ref blockCtx, pairs[..(iNumNonZero * 2)], iNumNonZero, iLocation: 1);
        return iNumNonZero;
    }
}
