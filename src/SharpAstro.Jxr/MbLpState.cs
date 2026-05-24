namespace SharpAstro.Jxr;

/// <summary>
/// Per-tile adaptive state for the LP band — T.832 §8.8.3.2
/// (InitializeLPVLC), §8.11.2 (InitializeAdaptiveScanLP), and §8.12.1
/// with iBand = 1.
/// </summary>
/// <remarks>
/// LP-band shape mirrors HP: 8 AdaptiveVlcStates (FirstIndex Lum/Chr,
/// Index Lum0/Lum1/Chr0/Chr1, AbsLevel 0/1 shared) plus a single
/// adaptive scan (LP has only one scan direction — no horizontal vs
/// vertical split like HP does) and the LP-band CoefficientModelState
/// (initial MBits = 4).
/// </remarks>
public sealed class MbLpState
{
    public AdaptiveVlcState FirstIndexLum;
    public AdaptiveVlcState FirstIndexChr;
    public AdaptiveVlcState IndexLum0;
    public AdaptiveVlcState IndexLum1;
    public AdaptiveVlcState IndexChr0;
    public AdaptiveVlcState IndexChr1;
    public AdaptiveVlcState AbsLevel0;
    public AdaptiveVlcState AbsLevel1;

    public readonly AdaptiveScan Scan;
    public CoefficientModelState Model;

    /// <summary>
    /// CountZeroCBPLP — T.832 Table 103. Tracks how often recent
    /// macroblocks had iCBPLP == 0; when it drops to ≤ 0 the joint-VLC
    /// path (CBPLP_YUV1) is favoured because zeros are common enough that
    /// the 1-bit code pays off. Initialized to 1 at the top-left MB of
    /// each tile (which corresponds to a fresh <see cref="MbLpState"/>).
    /// </summary>
    public int CountZeroCBPLP = 1;

    /// <summary>
    /// CountMaxCBPLP — T.832 Table 103. Mirror of <see cref="CountZeroCBPLP"/>
    /// for the "all-bits-set" extreme; when it drops below 0 the VLC
    /// path's inversion trick is picked.
    /// </summary>
    public int CountMaxCBPLP = 1;

    public MbLpState()
    {
        FirstIndexLum = AdaptiveVlc.InitializeTable2();
        FirstIndexChr = AdaptiveVlc.InitializeTable2();
        IndexLum0 = AdaptiveVlc.InitializeTable2();
        IndexLum1 = AdaptiveVlc.InitializeTable2();
        IndexChr0 = AdaptiveVlc.InitializeTable2();
        IndexChr1 = AdaptiveVlc.InitializeTable2();
        AbsLevel0 = AdaptiveVlc.InitializeTable1();
        AbsLevel1 = AdaptiveVlc.InitializeTable1();

        Scan = AdaptiveScan.ForLp();
        Model = CoefficientModel.Initialize(CoefficientModel.Band.Lp);
    }
}
