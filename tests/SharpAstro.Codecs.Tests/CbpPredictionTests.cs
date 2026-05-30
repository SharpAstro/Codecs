using System.Numerics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates CBP prediction + the <see cref="CbpModel"/> state machine.
/// <see cref="CbpPrediction.NumOnes"/> is cross-checked against an independent
/// popcount; the prediction is validated by encode→decode round-trip identity
/// (which exercises the spatial bit-propagation inverse and keeps the encoder and
/// decoder models in lock-step) across all three model states and neighbor
/// contexts.
/// </summary>
public sealed class CbpPredictionTests
{
    [Fact]
    public void NumOnes_MatchesPopCount()
    {
        for (var i = 0; i <= 0xffff; i++)
            CbpPrediction.NumOnes(i).ShouldBe(BitOperations.PopCount((uint)i));
        // High bits above 16 are ignored (i & 0xffff).
        CbpPrediction.NumOnes(unchecked((int)0xFFFF_0000)).ShouldBe(0);
        CbpPrediction.NumOnes(unchecked((int)0xFFFF_FFFF)).ShouldBe(16);
    }

    private static void AssertModelsInSync(CbpModel a, CbpModel b, string ctx)
    {
        a.State[0].ShouldBe(b.State[0], $"State[0] {ctx}");
        a.Count0[0].ShouldBe(b.Count0[0], $"Count0[0] {ctx}");
        a.Count1[0].ShouldBe(b.Count1[0], $"Count1[0] {ctx}");
    }

    [Fact]
    public void RoundTrip_Interior_RandomSequence()
    {
        var rng = new Random(0xCB9);
        var me = new CbpModel();
        var md = new CbpModel();
        int leftCbp = 0, topCbp = 0;

        for (var t = 0; t < 5000; t++)
        {
            int cbp = rng.Next(0, 0x10000);
            int sent = CbpPrediction.PredictEnc(cbp, ctxLeft: false, ctxTop: false, topCbp, leftCbp, 0, me);
            int got = CbpPrediction.PredictDec(sent, ctxLeft: false, ctxTop: false, topCbp, leftCbp, 0, md);

            got.ShouldBe(cbp, $"t={t}");
            AssertModelsInSync(me, md, $"t={t}");
            leftCbp = cbp; // neighbor is the reconstructed (== original) CBP
        }
    }

    [Fact]
    public void RoundTrip_DrivesAllThreeStates()
    {
        var me = new CbpModel();
        var md = new CbpModel();
        bool seen0 = false, seen1 = false, seen2 = false;

        // Long stretches of all-zero (-> state 1), all-one (-> state 2) and
        // moderate (-> state 0) CBPs, so every state path is exercised.
        int[] phases = { 0x0000, 0xffff, 0x0007, 0xffff, 0x0000, 0x00ff };
        foreach (var cbp in phases)
        for (var rep = 0; rep < 30; rep++)
        {
            int stateBefore = me.State[0];
            seen0 |= stateBefore == 0; seen1 |= stateBefore == 1; seen2 |= stateBefore == 2;

            int sent = CbpPrediction.PredictEnc(cbp, false, false, 0, 0, 0, me);
            int got = CbpPrediction.PredictDec(sent, false, false, 0, 0, 0, md);

            got.ShouldBe(cbp);
            AssertModelsInSync(me, md, $"cbp={cbp:X} rep={rep}");
        }

        seen0.ShouldBeTrue("state 0 exercised");
        seen1.ShouldBeTrue("state 1 exercised");
        seen2.ShouldBeTrue("state 2 exercised");
    }

    [Theory]
    [InlineData(true, true)]   // top-left corner (pred seed bit = 1)
    [InlineData(true, false)]  // left column (seed from top neighbor bit 10)
    [InlineData(false, true)]  // top row (seed from left neighbor bit 5)
    [InlineData(false, false)] // interior
    public void RoundTrip_NeighborContexts(bool ctxLeft, bool ctxTop)
    {
        var rng = new Random(0x5EED + (ctxLeft ? 2 : 0) + (ctxTop ? 1 : 0));
        var me = new CbpModel();
        var md = new CbpModel();

        for (var t = 0; t < 2000; t++)
        {
            int cbp = rng.Next(0, 0x10000);
            int topCbp = rng.Next(0, 0x10000);
            int leftCbp = rng.Next(0, 0x10000);
            int sent = CbpPrediction.PredictEnc(cbp, ctxLeft, ctxTop, topCbp, leftCbp, 0, me);
            int got = CbpPrediction.PredictDec(sent, ctxLeft, ctxTop, topCbp, leftCbp, 0, md);
            got.ShouldBe(cbp, $"t={t}");
        }
    }
}
