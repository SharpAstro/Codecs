using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c MB-level tests: T.832 §8.7.18.2 MB_HP for the luma-only case.
/// Verifies that encoding a 16-block MB then decoding (with the same CBPHP
/// signal) reproduces all 16 block coefficient buffers bit-exact, AND
/// that the per-MB AdaptiveVLC state + scan state + Model state stay in
/// lock-step so multi-MB sequences also round-trip.
/// </summary>
public sealed class JxrMbHpTests
{
    private static int[] RandomMb(Random rng, int density = 4)
    {
        // 16 blocks of 16 ints. Position 0 of each block is DC — we leave it 0.
        // For positions 1..15, ~1/density of them are non-zero.
        var mb = new int[256];
        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                if (rng.Next(0, density) == 0)
                    mb[b * 16 + p] = rng.Next(-100, 101);
        return mb;
    }

    private static void RoundTripMb(int[] mb, int mbhpMode)
    {
        var encState = new MbHpState();
        var w = new BitWriter();
        var cbphp = MbHp.EncodeLumaMb(w, encState, mbhpMode, mb);

        var decState = new MbHpState();
        var r = new BitReader(w.AsSpan());
        var decoded = new int[256];
        MbHp.DecodeLumaMb(ref r, decState, mbhpMode, cbphp, decoded);

        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                decoded[b * 16 + p].ShouldBe(mb[b * 16 + p], $"block {b} pos {p}");
    }

    [Fact]
    public void EmptyMb_NoBitsEmitted_CbphpZero()
    {
        var state = new MbHpState();
        var w = new BitWriter();
        var cbphp = MbHp.EncodeLumaMb(w, state, mbhpMode: 0, new int[256]);
        cbphp.ShouldBe(0);
        w.BitPosition.ShouldBe(0, "empty MB emits no HP bits — caller signals via CBPHP");
    }

    [Theory]
    [InlineData(0)] // horizontal scan
    [InlineData(1)] // vertical scan
    [InlineData(2)] // no prediction (horizontal scan)
    public void SingleNonZeroBlock_AtVariousPositions_RoundTrips(int mbhpMode)
    {
        // Try having only one block non-zero, at each of several positions.
        foreach (var blockPos in new[] { 0, 5, 15 })
        {
            var mb = new int[256];
            mb[blockPos * 16 + 3] = 42;
            mb[blockPos * 16 + 7] = -13;
            RoundTripMb(mb, mbhpMode);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DenseMb_AllBlocksNonZero_RoundTrips(int mbhpMode)
    {
        var rng = new Random(0xDEAD);
        var mb = new int[256];
        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                mb[b * 16 + p] = rng.Next(-50, 51);
        RoundTripMb(mb, mbhpMode);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 4)]  // ~25% density
    [InlineData(1, 4)]
    [InlineData(0, 10)] // very sparse
    public void RandomMb_RoundTrips_FullSweep(int mbhpMode, int density)
    {
        var rng = new Random(unchecked((int)0xBADC0DE) + mbhpMode);
        for (var trial = 0; trial < 50; trial++)
        {
            var mb = RandomMb(rng, density);
            RoundTripMb(mb, mbhpMode);
        }
    }

    [Fact]
    public void SequentialMbs_StateEvolvesIdentically()
    {
        // Encode 4 MBs through one shared state, then decode through another.
        // If MbHpState's snapshot/restore is wrong (e.g. AbsLevel sharing
        // broken), the 2nd+ MB decodes with different state and fails.
        var rng = new Random(0x12345);
        var mbs = new int[4][];
        for (var i = 0; i < 4; i++) mbs[i] = RandomMb(rng, 4);

        var encState = new MbHpState();
        var w = new BitWriter();
        var cbphps = new int[4];
        for (var i = 0; i < 4; i++)
            cbphps[i] = MbHp.EncodeLumaMb(w, encState, mbhpMode: 0, mbs[i]);

        var decState = new MbHpState();
        var r = new BitReader(w.AsSpan());
        for (var i = 0; i < 4; i++)
        {
            var decoded = new int[256];
            MbHp.DecodeLumaMb(ref r, decState, mbhpMode: 0, cbphps[i], decoded);
            for (var b = 0; b < 16; b++)
                for (var p = 1; p < 16; p++)
                    decoded[b * 16 + p].ShouldBe(mbs[i][b * 16 + p], $"MB {i} block {b} pos {p}");
        }
    }

    [Fact]
    public void CbphpBitOrdering_MatchesHierScanIterationOrder()
    {
        // Set block at MB-position iHierScanOrder[5] = 3 non-zero, all others zero.
        // CBPHP bit 5 should be set (iteration 5).
        var mb = new int[256];
        var iterIndex = 5;
        var blockPos = (int)MbHp.HierScanOrder[iterIndex];
        mb[blockPos * 16 + 1] = 7;

        var state = new MbHpState();
        var w = new BitWriter();
        var cbphp = MbHp.EncodeLumaMb(w, state, mbhpMode: 0, mb);
        cbphp.ShouldBe(1 << iterIndex, $"CBPHP bit {iterIndex} expected (iteration index for MB-position {blockPos})");
    }
}
