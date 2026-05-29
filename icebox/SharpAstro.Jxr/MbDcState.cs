namespace SharpAstro.Jxr;

/// <summary>
/// Per-tile adaptive state for the DC band — T.832 §8.8.3.1 + §8.12.1
/// with iBand = 0.
/// </summary>
/// <remarks>
/// Smaller than the HP/LP states: DC has only 2 AdaptiveVlcStates
/// (AbsLevelIndDCLum / AbsLevelIndDCChr) — there is no FIRST_INDEX or
/// INDEX adaptation at DC because each MB has exactly one super-DC per
/// component (no scan, no run-length). The CoefficientModelState
/// initialises with MBits = 8 (DC = highest-energy band, widest FLC).
/// </remarks>
public sealed class MbDcState
{
    public AdaptiveVlcState AbsLevelLum;
    public AdaptiveVlcState AbsLevelChr;
    public CoefficientModelState Model;

    public MbDcState()
    {
        AbsLevelLum = AdaptiveVlc.InitializeTable1();
        AbsLevelChr = AdaptiveVlc.InitializeTable1();
        Model = CoefficientModel.Initialize(CoefficientModel.Band.Dc);
    }

    /// <summary>
    /// T.832 §8.8.4.1 AdaptDC( ): adapt the DC band's AbsLevel VLC tables.
    /// Called at MB boundaries where bResetContext fires (last MB column in
    /// tile, or column at a 16-MB stride start).
    /// </summary>
    public void Adapt()
    {
        AdaptiveVlc.AdaptTable1(ref AbsLevelLum);
        AdaptiveVlc.AdaptTable1(ref AbsLevelChr);
    }
}
