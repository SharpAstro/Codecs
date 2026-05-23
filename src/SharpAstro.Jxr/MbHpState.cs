namespace SharpAstro.Jxr;

/// <summary>
/// Aggregated AdaptiveVLC + scan + Model state for the HP band of a tile —
/// T.832 §8.8.3.3 (InitializeHPVLC), §8.11.3 (InitializeAdaptiveScanHP),
/// and §8.12.1 (InitializeModelMB with iBand = 2).
/// </summary>
/// <remarks>
/// Per the spec, HP has 8 AdaptiveVlcStates:
/// <list type="bullet">
///   <item><see cref="FirstIndexLum"/>, <see cref="FirstIndexChr"/> — separate FIRST_INDEX per Luma/Chroma channel split (multi-table)</item>
///   <item><see cref="IndexLum0"/> / <see cref="IndexLum1"/> / <see cref="IndexChr0"/> / <see cref="IndexChr1"/> — INDEX with context-0/1 split per channel</item>
///   <item><see cref="AbsLevel0"/> / <see cref="AbsLevel1"/> — ABS_LEVEL context-0/1, <b>shared</b> between Luma and Chroma per spec</item>
/// </list>
/// Plus two scan orders (horizontal and vertical, dispatched by MBHPMode)
/// and one CoefficientModel state for the band.
///
/// This is a class rather than a struct because the AdaptiveScan instances
/// it holds are themselves reference types (they own internal byte arrays)
/// and because MB-level callers want shared mutable identity — the same
/// AbsLevel state must accumulate deltaDisc across Luma and Chroma block
/// emissions within an MB.
/// </remarks>
public sealed class MbHpState
{
    public AdaptiveVlcState FirstIndexLum;
    public AdaptiveVlcState FirstIndexChr;
    public AdaptiveVlcState IndexLum0;
    public AdaptiveVlcState IndexLum1;
    public AdaptiveVlcState IndexChr0;
    public AdaptiveVlcState IndexChr1;
    public AdaptiveVlcState AbsLevel0;
    public AdaptiveVlcState AbsLevel1;

    public readonly AdaptiveScan ScanHorizontal;
    public readonly AdaptiveScan ScanVertical;

    public CoefficientModelState Model;

    public MbHpState()
    {
        FirstIndexLum = AdaptiveVlc.InitializeTable2();
        FirstIndexChr = AdaptiveVlc.InitializeTable2();
        IndexLum0 = AdaptiveVlc.InitializeTable2();
        IndexLum1 = AdaptiveVlc.InitializeTable2();
        IndexChr0 = AdaptiveVlc.InitializeTable2();
        IndexChr1 = AdaptiveVlc.InitializeTable2();
        AbsLevel0 = AdaptiveVlc.InitializeTable1();
        AbsLevel1 = AdaptiveVlc.InitializeTable1();

        ScanHorizontal = AdaptiveScan.ForHpHorizontal();
        ScanVertical = AdaptiveScan.ForHpVertical();

        Model = CoefficientModel.Initialize(CoefficientModel.Band.Hp);
    }
}
