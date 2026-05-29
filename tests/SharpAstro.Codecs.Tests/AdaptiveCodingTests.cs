using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the two adaptive mechanisms that were the prime suspects for the
/// original "garbage after the first block": the adaptive coefficient scan
/// (bubble-swap) and the adaptive-Huffman table-index state machine
/// (<c>AdaptDiscriminant</c>). The state machine is checked against vectors from
/// the real jxrlib C (<c>Oracle/probe/adapthuff_probe.c</c>); the scan against
/// its unambiguous reset ramp + adjacent-bubble rule.
/// </summary>
public sealed class AdaptiveCodingTests
{
    // ---- adaptive scan ----

    [Fact]
    public void AdaptiveScan_Reset_ProducesTheJxrlibWeightRamp()
    {
        var initial = new int[16];
        for (var i = 0; i < 16; i++) initial[i] = i;
        var scan = new AdaptiveScan(initial);

        scan.Total.ShouldBe(new[] { 32767, 32, 30, 28, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4 });
        scan.Scan.ShouldBe(initial); // scan order unchanged by reset
    }

    [Fact]
    public void AdaptiveScan_Visit_BubblesForwardOnlyWhenItOvertakes()
    {
        var initial = new int[16];
        for (var i = 0; i < 16; i++) initial[i] = 100 + i;
        var scan = new AdaptiveScan(initial);

        // Slot 5 starts at total 24, slot 4 at 26. Two visits -> 26 (== 26, no swap).
        scan.Visit(5);
        scan.Visit(5);
        scan.Scan[5].ShouldBe(105);
        scan.Total[5].ShouldBe(26);

        // Third visit -> 27 > 26: swap slots 4 and 5.
        scan.Visit(5);
        scan.Scan[4].ShouldBe(105);
        scan.Scan[5].ShouldBe(104);
        scan.Total[4].ShouldBe(27);
        scan.Total[5].ShouldBe(26);
    }

    [Fact]
    public void AdaptiveScan_Slot0_IsPinnedAtFront()
    {
        var initial = new int[16];
        for (var i = 0; i < 16; i++) initial[i] = i;
        var scan = new AdaptiveScan(initial);

        // Hammer slot 1; it can never overtake the pinned MAXTOTAL at slot 0.
        for (var i = 0; i < 1000; i++) scan.Visit(1);
        scan.Scan[0].ShouldBe(0);
        scan.Total[0].ShouldBe(AdaptiveScan.MaxTotal);
    }

    // ---- adaptive Huffman table-index state machine (golden vs jxrlib) ----

    // (inDisc, inDisc1) -> (table, disc, disc1, lo, hi)
    private static void Step(AdaptiveHuffman h, int disc, int disc1,
        int t, int outDisc, int outDisc1, int lo, int hi, string tag)
    {
        h.Discriminant = disc;
        h.Discriminant1 = disc1;
        h.AdaptDiscriminant();
        (h.TableIndex, h.Discriminant, h.Discriminant1, h.LowerBound, h.UpperBound)
            .ShouldBe((t, outDisc, outDisc1, lo, hi), tag);
    }

    [Fact]
    public void AdaptDiscriminant_Sym12_DualDiscriminant_MatchesJxrlib()
    {
        const int Min = -2147483648, Max = 1073741824;
        var h = new AdaptiveHuffman(12);
        Step(h, 0, 0, 1, 0, 0, -8, 8, "S12_0");
        Step(h, 0, 100, 2, 0, 0, -8, 8, "S12_1");
        Step(h, 0, 100, 3, 0, 0, -8, 8, "S12_2");
        Step(h, 0, 100, 4, 0, 0, -8, Max, "S12_3");
        Step(h, 0, 100, 4, 0, 64, -8, Max, "S12_4");
        Step(h, 100, 5, 4, 64, 5, -8, Max, "S12_5");
        Step(h, -100, 0, 3, 0, 0, -8, 8, "S12_6");
        Step(h, -100, 0, 2, 0, 0, -8, 8, "S12_7");
        Step(h, -100, 0, 1, 0, 0, -8, 8, "S12_8");
        Step(h, -100, 0, 0, 0, 0, Min, 8, "S12_9");
        Step(h, -100, 0, 0, -64, 0, Min, 8, "S12_10");
    }

    [Fact]
    public void AdaptDiscriminant_Sym5_SingleDiscriminant_MatchesJxrlib()
    {
        const int Min = -2147483648, Max = 1073741824;
        var h = new AdaptiveHuffman(5);
        Step(h, 0, 0, 0, 0, 0, Min, 8, "S5_0");
        Step(h, 100, 0, 1, 0, 0, -8, Max, "S5_1");
        Step(h, 100, 0, 1, 64, 0, -8, Max, "S5_2");
        Step(h, -100, 0, 0, 0, 0, Min, 8, "S5_3");
        Step(h, -100, 0, 0, -64, 0, Min, 8, "S5_4");
    }

    [Fact]
    public void AdaptiveHuffman_TableCount_MatchesAlphabet()
    {
        new AdaptiveHuffman(4).TableCount.ShouldBe(1);
        new AdaptiveHuffman(5).TableCount.ShouldBe(2);
        new AdaptiveHuffman(6).TableCount.ShouldBe(4);
        new AdaptiveHuffman(12).TableCount.ShouldBe(5);
    }
}
