namespace SharpAstro.Jxr;

/// <summary>
/// Adaptive CBPHP prediction state — T.832 §8.10. Tracks observed
/// per-component-class CBPHP statistics (luma vs chroma) and selects
/// one of three transform variants for the prediction step:
/// <list type="bullet">
///   <item>State 0: XOR-propagation cascade with neighbour-derived seed</item>
///   <item>State 1: pass-through (iDiffCBPHP == iCBPHP)</item>
///   <item>State 2: bit-wise inversion (iCBPHP ^= 0xFFFF)</item>
/// </list>
/// State evolves after each MB based on the popcount of the actual
/// iCBPHP, with separate accumulators for "ones" vs "zeroes" balance
/// (clipped to ±16).
/// </summary>
public sealed class CbphpPredictionState
{
    /// <summary>0, 1, or 2 — index into transform variants. One per component class (0=luma, 1=chroma).</summary>
    public readonly int[] CbphpState = new int[2];

    /// <summary>Balance counters used by <see cref="Update"/> to select the next state.</summary>
    public readonly int[] CountOnes = new int[2];
    public readonly int[] CountZeroes = new int[2];

    public CbphpPredictionState()
    {
        Reset();
    }

    /// <summary>Reset to spec-defined initial values (T.832 §8.10.1).</summary>
    public void Reset()
    {
        for (var i = 0; i < 2; i++)
        {
            CbphpState[i] = 0;
            CountOnes[i] = -4;
            CountZeroes[i] = 4;
        }
    }

    /// <summary>
    /// T.832 §8.10.2 — accumulate <paramref name="iNOrig"/> (popcount of
    /// the just-processed iCBPHP) into the per-class counters, then
    /// recompute the transform state from the balance.
    /// </summary>
    public void Update(int componentClass, int iNOrig)
    {
        const int iNDiff = 3;
        CountOnes[componentClass]   = Clip(CountOnes[componentClass]   + iNOrig - iNDiff, -16, 15);
        CountZeroes[componentClass] = Clip(CountZeroes[componentClass] + (16 - iNOrig) - iNDiff, -16, 15);

        if (CountOnes[componentClass] < 0)
            CbphpState[componentClass] =
                CountOnes[componentClass] < CountZeroes[componentClass] ? 1 : 2;
        else if (CountZeroes[componentClass] < 0)
            CbphpState[componentClass] = 2;
        else
            CbphpState[componentClass] = 0;
    }

    private static int Clip(int x, int lo, int hi) => x < lo ? lo : x > hi ? hi : x;
}

/// <summary>
/// CBPHP prediction transforms — T.832 §8.7.17.5 (PredCBPHP444). Maps
/// between iDiffCBPHP (the residual that gets entropy-coded into the
/// bitstream) and iCBPHP (the actual 16-bit per-component bitmap).
/// </summary>
/// <remarks>
/// The class encapsulates just the 4:4:4 path; YUV422 / YUV420 chroma
/// prediction (PredCBPHP422 / PredCBPHP420 from §8.7.17.5.3 / .5.4)
/// has a different XOR pattern that pairs with the reduced
/// per-component block counts and is not yet implemented.
/// </remarks>
public static class CbphpPrediction
{
    /// <summary>
    /// Decoder direction (T.832 PredCBPHP444): given the
    /// entropy-decoded <paramref name="iDiffCbphp"/>, apply the
    /// state-dependent transform and return the actual iCBPHP.
    /// Also calls <see cref="CbphpPredictionState.Update"/> after the
    /// transform per spec ordering.
    /// </summary>
    /// <param name="iDiffCbphp">Residual bitmap from MbCbphp.DecodeMb.</param>
    /// <param name="state">Mutable predictor state — updated by this call.</param>
    /// <param name="componentIndex">Component index (0=luma, ≥1=chroma).</param>
    /// <param name="isLeftEdge">True iff this MB is at the left edge of its tile.</param>
    /// <param name="isTopEdge">True iff this MB is at the top edge of its tile.</param>
    /// <param name="topNeighbourCbphp">MBCBPHP of the MB above (any 16-bit value; ignored when <paramref name="isTopEdge"/>).</param>
    /// <param name="leftNeighbourCbphp">MBCBPHP of the MB to the left (ignored when <paramref name="isLeftEdge"/>).</param>
    /// <returns>The reconstructed actual iCBPHP.</returns>
    public static int Decode(
        int iDiffCbphp,
        CbphpPredictionState state,
        int componentIndex,
        bool isLeftEdge, bool isTopEdge,
        int topNeighbourCbphp, int leftNeighbourCbphp)
    {
        var c1 = componentIndex > 0 ? 1 : 0;
        var x = iDiffCbphp;
        var stateVal = state.CbphpState[c1];

        if (stateVal == 0)
        {
            var neighbour = ResolveNeighbour(isLeftEdge, isTopEdge, topNeighbourCbphp, leftNeighbourCbphp);
            x = ForwardXorPropagation444(x, neighbour);
        }
        else if (stateVal == 2)
        {
            x ^= 0xFFFF;
        }
        // stateVal == 1 → pass-through

        var iNOrig = PopCount16(x);
        state.Update(c1, iNOrig);
        return x;
    }

    /// <summary>
    /// Encoder direction (inverse of <see cref="Decode"/>): given the
    /// actual <paramref name="iCbphp"/> we want to signal, compute the
    /// iDiffCBPHP that the entropy coder should emit. Updates predictor
    /// state in the same way as the decoder so both stay in lock-step.
    /// </summary>
    public static int Encode(
        int iCbphp,
        CbphpPredictionState state,
        int componentIndex,
        bool isLeftEdge, bool isTopEdge,
        int topNeighbourCbphp, int leftNeighbourCbphp)
    {
        var c1 = componentIndex > 0 ? 1 : 0;
        var stateVal = state.CbphpState[c1];

        int iDiff;
        if (stateVal == 0)
        {
            var neighbour = ResolveNeighbour(isLeftEdge, isTopEdge, topNeighbourCbphp, leftNeighbourCbphp);
            iDiff = InverseXorPropagation444(iCbphp, neighbour);
        }
        else if (stateVal == 2)
        {
            iDiff = iCbphp ^ 0xFFFF;
        }
        else
        {
            iDiff = iCbphp; // state 1 — pass through
        }

        var iNOrig = PopCount16(iCbphp);
        state.Update(c1, iNOrig);
        return iDiff;
    }

    private static int ResolveNeighbour(bool isLeftEdge, bool isTopEdge, int topCbphp, int leftCbphp)
    {
        if (isLeftEdge)
            return isTopEdge ? 1 : (topCbphp >> 10) & 1;
        return (leftCbphp >> 5) & 1;
    }

    /// <summary>
    /// Forward XOR-propagation cascade per T.832 §8.7.17.5.2 state-0 branch.
    /// Used by the <em>decoder</em> direction.
    /// </summary>
    private static int ForwardXorPropagation444(int x, int neighbour)
    {
        x ^= neighbour;
        x ^= 0x02 & (x << 1);
        x ^= 0x10 & (x << 3);
        x ^= 0x20 & (x << 1);
        x ^= (x & 0x33) << 2;
        x ^= (x & 0x00CC) << 6;
        x ^= (x & 0x3300) << 2;
        return x & 0xFFFF;
    }

    /// <summary>
    /// Inverse XOR-propagation — applies the same elementary operations
    /// in REVERSE order. Each <c>x ^= (mask &amp; (x &lt;&lt; shift))</c>
    /// is self-inverse when re-applied to its own output (the mask hits
    /// bits whose shifted contribution lands outside the mask range),
    /// so reversing the sequence undoes the forward cascade exactly.
    /// </summary>
    private static int InverseXorPropagation444(int x, int neighbour)
    {
        x ^= (x & 0x3300) << 2;
        x ^= (x & 0x00CC) << 6;
        x ^= (x & 0x33) << 2;
        x ^= 0x20 & (x << 1);
        x ^= 0x10 & (x << 3);
        x ^= 0x02 & (x << 1);
        x ^= neighbour;
        return x & 0xFFFF;
    }

    private static int PopCount16(int x)
    {
        var c = 0;
        for (var i = 0; i < 16; i++)
            if ((x & (1 << i)) != 0) c++;
        return c;
    }
}
