using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Tests for the CBPHP prediction layer — T.832 §8.7.17.5 (PredCBPHP444)
/// and §8.10 (adaptive state machine). Verifies that Decode and Encode
/// are exact inverses across all three state variants AND across the
/// state-machine evolution that fires after each MB.
/// </summary>
public sealed class JxrCbphpPredictionTests
{
    [Fact]
    public void InitialState_MatchesSpec()
    {
        var s = new CbphpPredictionState();
        for (var i = 0; i < 2; i++)
        {
            s.CbphpState[i].ShouldBe(0);
            s.CountOnes[i].ShouldBe(-4);
            s.CountZeroes[i].ShouldBe(4);
        }
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0001)]
    [InlineData(0x1234)]
    [InlineData(0xABCD)]
    [InlineData(0xFFFF)]
    public void State0_TopLeftCorner_RoundTrips(int iCbphp)
    {
        // Top-left of tile, state 0 → neighbour = 1 constant. Forward and
        // inverse of the XOR propagation cascade must round-trip.
        var encState = new CbphpPredictionState();
        var decState = new CbphpPredictionState();

        var diff = CbphpPrediction.Encode(iCbphp, encState, componentIndex: 0,
            isLeftEdge: true, isTopEdge: true, topNeighbourCbphp: 0, leftNeighbourCbphp: 0);

        var reconstructed = CbphpPrediction.Decode(diff, decState, componentIndex: 0,
            isLeftEdge: true, isTopEdge: true, topNeighbourCbphp: 0, leftNeighbourCbphp: 0);

        reconstructed.ShouldBe(iCbphp);

        // Both sides must have evolved to the same state for the next MB.
        encState.CbphpState[0].ShouldBe(decState.CbphpState[0]);
        encState.CountOnes[0].ShouldBe(decState.CountOnes[0]);
        encState.CountZeroes[0].ShouldBe(decState.CountZeroes[0]);
    }

    [Theory]
    [InlineData(0x1234, 0x5678)]
    [InlineData(0xABCD, 0xFFFF)]
    [InlineData(0xFFFF, 0x0000)]
    public void State0_WithLeftNeighbour_RoundTrips(int iCbphp, int leftNeighbour)
    {
        // Interior MB (no edge flags) — neighbour comes from left MB's bit 5.
        var encState = new CbphpPredictionState();
        var decState = new CbphpPredictionState();

        var diff = CbphpPrediction.Encode(iCbphp, encState, componentIndex: 0,
            isLeftEdge: false, isTopEdge: false,
            topNeighbourCbphp: 0xFAFA, leftNeighbourCbphp: leftNeighbour);

        var reconstructed = CbphpPrediction.Decode(diff, decState, componentIndex: 0,
            isLeftEdge: false, isTopEdge: false,
            topNeighbourCbphp: 0xFAFA, leftNeighbourCbphp: leftNeighbour);

        reconstructed.ShouldBe(iCbphp);
    }

    [Fact]
    public void State1_PassThrough_NoTransform()
    {
        // Force state into 1 by feeding many low-popcount MBs.
        var state = new CbphpPredictionState();
        // Update with iNOrig = 0 repeatedly — CountOnes drops fast (each step
        // subtracts 3), CountZeroes climbs to 15, so CountOnes < 0 and
        // CountOnes < CountZeroes → state = 1.
        for (var i = 0; i < 5; i++) state.Update(0, iNOrig: 0);
        state.CbphpState[0].ShouldBe(1, $"expected state 1, got {state.CbphpState[0]} CountOnes={state.CountOnes[0]} CountZeroes={state.CountZeroes[0]}");

        var encState = new CbphpPredictionState();
        var decState = new CbphpPredictionState();
        for (var i = 0; i < 5; i++) encState.Update(0, iNOrig: 0);
        for (var i = 0; i < 5; i++) decState.Update(0, iNOrig: 0);

        var iCbphp = 0x1234;
        var diff = CbphpPrediction.Encode(iCbphp, encState, 0, false, false, 0, 0xFFFF);
        diff.ShouldBe(iCbphp, "state 1 is pass-through — diff == cbphp");

        var reconstructed = CbphpPrediction.Decode(diff, decState, 0, false, false, 0, 0xFFFF);
        reconstructed.ShouldBe(iCbphp);
    }

    [Fact]
    public void State2_BitInvert_RoundTrips()
    {
        // Force state into 2 by feeding many high-popcount MBs.
        var state = new CbphpPredictionState();
        for (var i = 0; i < 5; i++) state.Update(0, iNOrig: 16); // all blocks set
        state.CbphpState[0].ShouldBe(2, $"expected state 2, got {state.CbphpState[0]}");

        var encState = new CbphpPredictionState();
        var decState = new CbphpPredictionState();
        for (var i = 0; i < 5; i++) encState.Update(0, iNOrig: 16);
        for (var i = 0; i < 5; i++) decState.Update(0, iNOrig: 16);

        var iCbphp = 0x1234;
        var diff = CbphpPrediction.Encode(iCbphp, encState, 0, false, false, 0, 0);
        diff.ShouldBe(iCbphp ^ 0xFFFF);
        var reconstructed = CbphpPrediction.Decode(diff, decState, 0, false, false, 0, 0);
        reconstructed.ShouldBe(iCbphp);
    }

    [Fact]
    public void SequentialMbs_StateStaysInSync()
    {
        // Encode many MBs with varying patterns; decoder must reconstruct
        // every iCBPHP correctly even as the predictor state evolves.
        var rng = new Random(0xCBABBA);
        var encState = new CbphpPredictionState();
        var decState = new CbphpPredictionState();
        var prevEncCbphp = 0;
        var prevDecCbphp = 0;

        for (var mb = 0; mb < 50; mb++)
        {
            var iCbphp = rng.Next(0, 65536);
            // Pretend this MB is to the right of the previous one — interior MB.
            var diff = CbphpPrediction.Encode(iCbphp, encState, 0,
                isLeftEdge: false, isTopEdge: false,
                topNeighbourCbphp: 0, leftNeighbourCbphp: prevEncCbphp);
            var reconstructed = CbphpPrediction.Decode(diff, decState, 0,
                isLeftEdge: false, isTopEdge: false,
                topNeighbourCbphp: 0, leftNeighbourCbphp: prevDecCbphp);
            reconstructed.ShouldBe(iCbphp, $"MB {mb}");

            encState.CbphpState[0].ShouldBe(decState.CbphpState[0], $"MB {mb} state");
            encState.CountOnes[0].ShouldBe(decState.CountOnes[0]);
            encState.CountZeroes[0].ShouldBe(decState.CountZeroes[0]);

            prevEncCbphp = iCbphp;
            prevDecCbphp = reconstructed;
        }
    }

    [Fact]
    public void RandomBitmapSweep_AllStateConfigurations()
    {
        // For each initial state, encode and decode a series of patterns.
        // Even though the state will evolve during the sweep, encoder and
        // decoder both start at the same place and apply the same updates.
        var rng = new Random(0x6262);
        var encState = new CbphpPredictionState();
        var decState = new CbphpPredictionState();
        for (var trial = 0; trial < 200; trial++)
        {
            var iCbphp = rng.Next(0, 65536);
            var neighbour = rng.Next(0, 65536);
            var diff = CbphpPrediction.Encode(iCbphp, encState, 0, false, false, 0, neighbour);
            var rec = CbphpPrediction.Decode(diff, decState, 0, false, false, 0, neighbour);
            rec.ShouldBe(iCbphp);
        }
    }
}
