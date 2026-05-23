using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-3c tests for the adaptive inverse-scanning state (T.832 §8.11).
/// The scan tables are deterministic given the sequence of <c>Step</c> calls
/// — both encoder and decoder must drive the state identically to stay in
/// sync. These tests pin down the initial conditions and the swap rule.
/// </summary>
public sealed class JxrAdaptiveScanTests
{
    [Fact]
    public void InitialOrder_LP_MatchesScanOrder0()
    {
        var scan = AdaptiveScan.ForLp();
        Span<byte> order = stackalloc byte[16];
        scan.CopyOrderTo(order);

        // T.832 Table 107 ScanOrder0 (index 0 unused).
        byte[] expected = [0, 4, 1, 5, 8, 2, 9, 6, 12, 3, 10, 13, 7, 14, 11, 15];
        for (var i = 0; i < 16; i++) order[i].ShouldBe(expected[i]);
    }

    [Fact]
    public void InitialOrder_HpVertical_MatchesScanOrder1()
    {
        var scan = AdaptiveScan.ForHpVertical();
        Span<byte> order = stackalloc byte[16];
        scan.CopyOrderTo(order);

        byte[] expected = [0, 1, 2, 5, 4, 3, 6, 9, 8, 7, 12, 15, 13, 10, 11, 14];
        for (var i = 0; i < 16; i++) order[i].ShouldBe(expected[i]);
    }

    [Fact]
    public void InitialTotals_MatchScanTotals()
    {
        var scan = AdaptiveScan.ForLp();
        Span<byte> totals = stackalloc byte[16];
        scan.CopyTotalsTo(totals);

        byte[] expected = [0, 32, 30, 28, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4];
        for (var i = 0; i < 16; i++) totals[i].ShouldBe(expected[i]);
    }

    [Fact]
    public void Step_BumpsTotalsAndSwapsWhenAhead()
    {
        // Initial totals[14] = 6, totals[15] = 4. After enough Step(15) calls,
        // totals[15] will exceed totals[14] and trigger a swap of indices 14 and 15.
        var scan = AdaptiveScan.ForLp();
        Span<byte> orderBefore = stackalloc byte[16];
        scan.CopyOrderTo(orderBefore);
        var positionAt14_before = orderBefore[14];
        var positionAt15_before = orderBefore[15];

        // Step(15) three times: totals[15] goes 4 -> 5 -> 6 -> 7. The third call
        // pushes totals[15] = 7 > totals[14] = 6 and triggers the swap.
        scan.Step(15);
        scan.Step(15);
        scan.Step(15);

        Span<byte> orderAfter = stackalloc byte[16];
        scan.CopyOrderTo(orderAfter);
        orderAfter[14].ShouldBe(positionAt15_before, "index 14 now holds what was at 15");
        orderAfter[15].ShouldBe(positionAt14_before, "index 15 now holds what was at 14");
    }

    [Fact]
    public void Step_AtIndex1_NeverSwaps()
    {
        // The spec swap rule is "if i > 1 and totals[i] > totals[i-1]" — index 1
        // has no left neighbour to compare against, so it should never swap.
        var scan = AdaptiveScan.ForLp();
        Span<byte> orderBefore = stackalloc byte[16];
        scan.CopyOrderTo(orderBefore);
        var pos1Before = orderBefore[1];

        for (var i = 0; i < 100; i++) scan.Step(1);

        Span<byte> orderAfter = stackalloc byte[16];
        scan.CopyOrderTo(orderAfter);
        orderAfter[1].ShouldBe(pos1Before);
    }

    [Fact]
    public void ResetTotals_PreservesOrder()
    {
        // ResetTotals zeroes the counts but keeps the permutation — used at tile
        // boundaries where statistics restart but the previously-learned order
        // remains a reasonable guess.
        var scan = AdaptiveScan.ForLp();

        // Drive enough swaps to perturb the order.
        for (var i = 0; i < 10; i++) scan.Step(15);
        Span<byte> perturbed = stackalloc byte[16];
        scan.CopyOrderTo(perturbed);

        scan.ResetTotals();
        Span<byte> afterReset = stackalloc byte[16];
        scan.CopyOrderTo(afterReset);

        for (var i = 0; i < 16; i++)
            afterReset[i].ShouldBe(perturbed[i], $"reset must not change order[{i}]");

        Span<byte> totals = stackalloc byte[16];
        scan.CopyTotalsTo(totals);
        // Totals should be back to initial values.
        byte[] expected = [0, 32, 30, 28, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4];
        for (var i = 0; i < 16; i++) totals[i].ShouldBe(expected[i]);
    }

    [Fact]
    public void Step_ReturnsPositionBeforeSwap()
    {
        // Step returns the block-position k for parse-index i AT CALL TIME —
        // i.e., before any swap. The swap (if any) affects future calls.
        var scan = AdaptiveScan.ForLp();
        Span<byte> initial = stackalloc byte[16];
        scan.CopyOrderTo(initial);

        // Force a swap on index 15: get there with three calls.
        scan.Step(15);
        scan.Step(15);
        var k = scan.Step(15);

        k.ShouldBe((int)initial[15], "third Step(15) returns the position that was at index 15 BEFORE the swap");
    }
}
