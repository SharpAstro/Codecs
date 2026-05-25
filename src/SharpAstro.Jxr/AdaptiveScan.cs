namespace SharpAstro.Jxr;

/// <summary>
/// Adaptive inverse-scanning tables for LP and HP coefficients — T.832 §8.11.
/// </summary>
/// <remarks>
/// Each band (LP, HP-horizontal, HP-vertical) maintains a permutation of
/// the integers 1..15 that determines the order in which coefficients are
/// written to / read from the bitstream. The permutation adapts based on
/// observed coefficient activity: positions that hold non-zero coefficients
/// more often migrate forward in the scan, putting the most-likely-significant
/// coefficients first so RLE/EOB termination can fire sooner.
///
/// Both encoder and decoder maintain the same scan state, updated by the
/// same rule — see <see cref="Step"/>. As long as both sides call Step in
/// the same order with the same coefficient values, they stay in sync.
///
/// The HP-horizontal / HP-vertical distinction comes from the per-MB
/// MBHPMode (computed by <see cref="HpPrediction.CalcMode"/>): mode 0
/// (predict-from-left) uses the horizontal scan, mode 1 (predict-from-top)
/// uses the vertical scan, mode 2 (no prediction) defaults to horizontal.
/// </remarks>
public sealed class AdaptiveScan
{
    // Initial scan orders — match jxrlib's actual computed values
    // (image/sys/image.c InitZigzagScan):
    //   m_aScanLowpass[k].uScan = grgiZigzagInv4x4_lowpass[k]
    //   m_aScanHoriz  [k].uScan = dctIndex[0][grgiZigzagInv4x4H[k]]
    //   m_aScanVert   [k].uScan = dctIndex[0][grgiZigzagInv4x4V[k]]
    // with dctIndex[0] = {0,5,1,6, 10,12,8,14, 2,4,3,7, 9,13,11,15}.
    //
    // T.832 Table 107 lists scan-orders [0, 4, 1, 5, 8, ...] etc., but
    // those are in transmission order BEFORE the FwdPermute / dctIndex
    // permutation. Our block layout post-FCT4x4 (which ends in
    // FwdPermute) matches jxrlib's natural data order, so the HP scan
    // walks the dctIndex-permuted positions; LP coefs come from
    // FCT4x4Stage2 which intentionally skips FwdPermute and are scanned
    // in the un-permuted grgiZigzagInv4x4_lowpass order.
    private static ReadOnlySpan<byte> ScanOrderLp =>
        [0, 1, 4, 5, 2, 8, 6, 9, 3, 12, 10, 7, 13, 11, 14, 15];
    private static ReadOnlySpan<byte> ScanOrder0 =>
        [0, 5, 10, 12, 1, 2, 8, 4, 6, 9, 3, 14, 13, 7, 11, 15];
    private static ReadOnlySpan<byte> ScanOrder1 =>
        [0, 10, 2, 12, 5, 9, 4, 8, 1, 13, 6, 15, 14, 3, 11, 7];

    // T.832 Table 108. Index 0 is unused; the band runs over i = 1..15.
    private static ReadOnlySpan<byte> ScanTotalsInit =>
        [0, 32, 30, 28, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4];

    private readonly byte[] _order = new byte[16];
    private readonly byte[] _totals = new byte[16];

    /// <summary>Create an LP-scan state initialised per T.832 8.11.2.</summary>
    // ScanOrderLp matches jxrlib's grgiZigzagInv4x4_lowpass but using it
    // breaks self-roundtrip and the 2-MB cross-codec test (1.5 maxDiff,
    // was 0.08 with the old order). The HP scan change works at the
    // bitstream level because FCT4x4 has a FwdPermute that produces
    // jxrlib's natural data order; FCT4x4Stage2 ALSO skips its own
    // permute, so LP coefs should be in natural order too — yet
    // changing the LP scan tables to match jxrlib breaks both sides.
    // Some other piece of LP-data layout differs and we haven't found
    // it; leaving the LP scan at the original [0,4,1,5,...] for now.
    public static AdaptiveScan ForLp() => new(ScanOrder0);

    /// <summary>
    /// Create an HP horizontal-scan state (used when MBHPMode = 0 or 2).
    /// Initialised per T.832 8.11.3 first branch.
    /// </summary>
    public static AdaptiveScan ForHpHorizontal() => new(ScanOrder0);

    /// <summary>
    /// Create an HP vertical-scan state (used when MBHPMode = 1).
    /// Initialised per T.832 8.11.3 second branch.
    /// </summary>
    public static AdaptiveScan ForHpVertical() => new(ScanOrder1);

    private AdaptiveScan(ReadOnlySpan<byte> initialOrder)
    {
        initialOrder.CopyTo(_order);
        ScanTotalsInit.CopyTo(_totals);
    }

    /// <summary>
    /// Reset the totals (frequency counts) but preserve the current
    /// permutation — T.832 8.11.4 / 8.11.5. Called at tile boundaries.
    /// </summary>
    public void ResetTotals()
    {
        ScanTotalsInit.CopyTo(_totals);
    }

    /// <summary>
    /// Look up the block position for parse-index <paramref name="i"/> (1..15).
    /// Does not mutate state — use <see cref="Step"/> for the full
    /// scan-and-update sequence.
    /// </summary>
    public int Position(int i) => _order[i];

    /// <summary>
    /// Single scan step — T.832 8.11.6 (LP) / 8.11.7 (HP). Returns the
    /// block-position <c>k</c> corresponding to parse-index <c>i</c>, then
    /// updates the totals/order so future calls may see a different
    /// permutation.
    /// </summary>
    /// <param name="i">Parse-index (1..15). Caller iterates 1..15 over each block.</param>
    /// <returns>The block position k = scanOrder[i] AT CALL TIME (before any swap).</returns>
    public int Step(int i)
    {
        if (i < 1 || i > 15) throw new ArgumentOutOfRangeException(nameof(i));
        var k = _order[i];
        _totals[i]++;
        if (i > 1 && _totals[i] > _totals[i - 1])
        {
            (_totals[i], _totals[i - 1]) = (_totals[i - 1], _totals[i]);
            (_order[i], _order[i - 1]) = (_order[i - 1], _order[i]);
        }
        return k;
    }

    /// <summary>Copy the current scan order into <paramref name="dest"/> for inspection or persistence.</summary>
    public void CopyOrderTo(Span<byte> dest)
    {
        if (dest.Length < 16) throw new ArgumentException("Destination too small", nameof(dest));
        ((ReadOnlySpan<byte>)_order).CopyTo(dest);
    }

    /// <summary>Copy the current totals into <paramref name="dest"/>.</summary>
    public void CopyTotalsTo(Span<byte> dest)
    {
        if (dest.Length < 16) throw new ArgumentException("Destination too small", nameof(dest));
        ((ReadOnlySpan<byte>)_totals).CopyTo(dest);
    }
}
