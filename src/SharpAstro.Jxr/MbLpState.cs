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
