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

    /// <summary>
    /// CBPHP prediction state (T.832 §8.10) — adapts CountOnes / CountZeroes
    /// per component-class (luma vs chroma) after each MB's iCBPHP is
    /// reconstructed. Drives the PredCBPHP444 transform variant
    /// (state 0 = neighbour-XOR cascade, 1 = pass-through, 2 = invert).
    /// </summary>
    public readonly CbphpPredictionState Predictor = new();

    /// <summary>
    /// Per-MB previously-reconstructed MBCBPHP, indexed as
    /// <c>[component, mbY * widthInMb + mbX]</c>. Used by
    /// <see cref="CbphpPrediction"/> to look up the left and top neighbours.
    /// Allocated lazily on first <see cref="StoreMbCbphp"/> call.
    /// </summary>
    private int[]? _mbCbphpGrid;
    private int _numComponents;
    private int _widthInMb;

    public MbCbphpState()
    {
        NumCbphp = AdaptiveVlc.InitializeTable1();
        NumBlkCbphp = AdaptiveVlc.InitializeTable1();
    }

    /// <summary>
    /// Configure neighbour-MBCBPHP storage. Called once per tile by the
    /// tile-level orchestrator; size is fixed for the lifetime of the state.
    /// </summary>
    public void InitMbCbphpGrid(int widthInMb, int heightInMb, int numComponents)
    {
        _widthInMb = widthInMb;
        _numComponents = numComponents;
        _mbCbphpGrid = new int[numComponents * widthInMb * heightInMb];
    }

    /// <summary>Record the just-reconstructed iCBPHP for MB at (mbX, mbY).</summary>
    public void StoreMbCbphp(int mbX, int mbY, int component, int iCbphp)
    {
        if (_mbCbphpGrid is null) return;
        _mbCbphpGrid[(component * _widthInMb + mbX) + mbY * _widthInMb * _numComponents] = iCbphp;
    }

    /// <summary>Recall a neighbour MBCBPHP; returns 0 when out of bounds.</summary>
    public int LoadMbCbphp(int mbX, int mbY, int component)
    {
        if (_mbCbphpGrid is null || mbX < 0 || mbY < 0 || mbX >= _widthInMb) return 0;
        return _mbCbphpGrid[(component * _widthInMb + mbX) + mbY * _widthInMb * _numComponents];
    }

    /// <summary>
    /// T.832 §8.8.4.3 AdaptHP( ): the NUM_CBPHP / NUM_BLKCBPHP VLC tables are
    /// adapted as part of the HP-band adapt step. Called at MB boundaries
    /// alongside <see cref="MbHpState.Adapt"/>.
    /// </summary>
    public void Adapt()
    {
        AdaptiveVlc.AdaptTable1(ref NumCbphp);
        AdaptiveVlc.AdaptTable1(ref NumBlkCbphp);
    }
}
