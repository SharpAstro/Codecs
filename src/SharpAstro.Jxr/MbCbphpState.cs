namespace SharpAstro.Jxr;

/// <summary>
/// Per-tile adaptive state for CBPHP signalling — T.832 §8.8.3.4
/// (InitializeCBPHPVLC). Two AdaptiveVlcStates: one for the NUM_CBPHP
/// VLC (Table 59, 2 adaptive code tables) and one for NUM_BLKCBPHP
/// (Table 60 / 61 depending on colour format).
/// </summary>
/// <remarks>
/// CBPHP signalling separately needs CBPHP prediction state (T.832
/// §8.7.17.5 / §8.10) which adapts on observed coefficient counts and
/// XORs the residual against a neighbour-derived pattern. The
/// prediction layer is not yet implemented; callers using this state
/// produce a bitstream where iDiffCBPHP == iCBPHP (no prediction
/// applied) — usable for our own round-trip tests but not yet a
/// spec-compliant interoperable codestream.
/// </remarks>
public sealed class MbCbphpState
{
    public AdaptiveVlcState NumCbphp;
    public AdaptiveVlcState NumBlkCbphp;

    public MbCbphpState()
    {
        NumCbphp = AdaptiveVlc.InitializeTable1();
        NumBlkCbphp = AdaptiveVlc.InitializeTable1();
    }
}
