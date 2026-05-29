using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c CBPHP signalling tests — T.832 §8.7.17. Round-trips a
/// per-component 16-bit CBPHP residual (iDiffCBPHP) through encode and
/// decode. Prediction layer (PredCBPHP444/422/420) is not yet wired,
/// so what's encoded is the residual itself.
/// </summary>
public sealed class JxrMbCbphpTests
{
    private static void RoundTrip(int[] cbphp)
    {
        var fmt = JxrInternalColorFormat.NComponent;
        var encState = new MbCbphpState();
        encState.InitMbCbphpGrid(1, 1, cbphp.Length);
        var w = new BitWriter();
        MbCbphp.EncodeMb(w, encState, fmt, cbphp.Length,
            mbX: 0, mbY: 0, isLeftEdge: true, isTopEdge: true, cbphp);

        var decState = new MbCbphpState();
        decState.InitMbCbphpGrid(1, 1, cbphp.Length);
        var r = new BitReader(w.AsSpan());
        var decoded = new int[cbphp.Length];
        MbCbphp.DecodeMb(ref r, decState, fmt, cbphp.Length,
            mbX: 0, mbY: 0, isLeftEdge: true, isTopEdge: true, decoded);

        for (var c = 0; c < cbphp.Length; c++)
            decoded[c].ShouldBe(cbphp[c], $"component {c}");
    }

    [Fact]
    public void AllZero_NoBlocks_RoundTrips()
    {
        RoundTrip([0]);
    }

    [Fact]
    public void SingleBlockNonZero_RoundTrips()
    {
        // Single block set at various positions.
        for (var bit = 0; bit < 16; bit++)
            RoundTrip([1 << bit]);
    }

    [Fact]
    public void SingleGroupPatterns_RoundTrip()
    {
        // Group 0 patterns (low 4 bits set).
        foreach (var pattern in new[] { 0x0001, 0x0003, 0x0005, 0x0007, 0x000F })
            RoundTrip([pattern]);
    }

    [Fact]
    public void TwoGroupsAdjacent_RoundTrip()
    {
        // Two adjacent groups set: e.g. bits 0..7 → groups 0 and 1.
        RoundTrip([0x00FF]);
    }

    [Fact]
    public void TwoGroupsDiagonal_RoundTrip()
    {
        // Diagonal: groups 0 and 2 (NUM_CBPHP=2, REF_CBPHP1 value 5).
        RoundTrip([0x0F0F]);
    }

    [Fact]
    public void ThreeGroups_RoundTrip()
    {
        // 3 groups set: bits 0..3, 4..7, 8..11 → groups 0,1,2 (omit group 3).
        RoundTrip([0x0FFF]);
    }

    [Fact]
    public void AllBlocksSet_RoundTrips()
    {
        RoundTrip([0xFFFF]);
    }

    [Fact]
    public void Multicomponent_RoundTrip()
    {
        // 3 components, each with different CBPHP patterns.
        RoundTrip([0x1234, 0xABCD, 0x5500]);
    }

    [Fact]
    public void RandomBitmaps_FullSweep()
    {
        var rng = new Random(0xCBCBCB);
        for (var trial = 0; trial < 200; trial++)
        {
            var nc = 1 + rng.Next(4);
            var cbphp = new int[nc];
            for (var c = 0; c < nc; c++)
                cbphp[c] = rng.Next(0, 65536); // any 16-bit pattern
            RoundTrip(cbphp);
        }
    }

    [Fact]
    public void SequentialMbs_StateEvolvesIdentically()
    {
        // Run 6 MBs through one shared state, decode through another.
        var rng = new Random(0x4242);
        var mbs = new int[6][];
        for (var i = 0; i < 6; i++)
        {
            mbs[i] = new int[3];
            for (var c = 0; c < 3; c++) mbs[i][c] = rng.Next(0, 65536);
        }

        var fmt = JxrInternalColorFormat.NComponent;
        var encState = new MbCbphpState();
        encState.InitMbCbphpGrid(6, 1, 3);
        var w = new BitWriter();
        for (var i = 0; i < mbs.Length; i++)
            MbCbphp.EncodeMb(w, encState, fmt, 3,
                mbX: i, mbY: 0, isLeftEdge: i == 0, isTopEdge: true, mbs[i]);

        var decState = new MbCbphpState();
        decState.InitMbCbphpGrid(6, 1, 3);
        var r = new BitReader(w.AsSpan());
        for (var i = 0; i < 6; i++)
        {
            var decoded = new int[3];
            MbCbphp.DecodeMb(ref r, decState, fmt, 3,
                mbX: i, mbY: 0, isLeftEdge: i == 0, isTopEdge: true, decoded);
            for (var c = 0; c < 3; c++)
                decoded[c].ShouldBe(mbs[i][c], $"MB {i} c={c}");
        }
    }
}
