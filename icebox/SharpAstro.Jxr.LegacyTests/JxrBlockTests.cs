using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c capstone: full block-level codec tests (T.832 §8.7.18.5
/// DECODE_BLOCK + its forward dual). Each test encodes a known
/// <c>(run, level)*</c> pattern, decodes it back, and verifies
/// bit-exact recovery of the coefficient array.
/// </summary>
public sealed class JxrBlockTests
{
    private static BlockCodingContext FreshContext()
    {
        return new BlockCodingContext
        {
            FirstIndex = AdaptiveVlc.InitializeTable2(),
            Index0     = AdaptiveVlc.InitializeTable2(),
            Index1     = AdaptiveVlc.InitializeTable2(),
            AbsLevel0  = AdaptiveVlc.InitializeTable1(),
            AbsLevel1  = AdaptiveVlc.InitializeTable1(),
        };
    }

    private static int RoundTrip(int[] coeff, int iNumNonZero, int iLocation = 1)
    {
        var encCtx = FreshContext();
        var w = new BitWriter();
        Block.Encode(w, ref encCtx, coeff.AsSpan(0, iNumNonZero * 2), iNumNonZero, iLocation);

        var decCtx = FreshContext();
        var r = new BitReader(w.AsSpan());
        var decoded = new int[32];
        var decoded_n = Block.Decode(ref r, ref decCtx, decoded, iLocation);

        decoded_n.ShouldBe(iNumNonZero);
        for (var i = 0; i < iNumNonZero * 2; i++)
            decoded[i].ShouldBe(coeff[i], $"index {i}");
        return decoded_n;
    }

    [Fact]
    public void SingleCoefficient_AtPosition1_Magnitude1_RoundTrips()
    {
        // iCoeff = (run=0, level=1). Simplest possible block.
        RoundTrip([0, 1], iNumNonZero: 1);
    }

    [Fact]
    public void SingleCoefficient_AtPosition1_NegativeMagnitude1_RoundTrips()
    {
        RoundTrip([0, -1], iNumNonZero: 1);
    }

    [Fact]
    public void SingleCoefficient_AtPosition1_LargeMagnitude_RoundTrips()
    {
        // Triggers ABS_LEVEL emission.
        RoundTrip([0, 42], iNumNonZero: 1);
        RoundTrip([0, -1000], iNumNonZero: 1);
    }

    [Fact]
    public void SingleCoefficient_AtLastPosition_RoundTrips()
    {
        // Run of 14, coefficient at scan step 15 (start iLocation=1 → end 15).
        RoundTrip([14, 7], iNumNonZero: 1);
    }

    [Fact]
    public void TwoCoefficients_AdjacentNonZero_RoundTrips()
    {
        // (run=0, level=3) then (run=0, level=-5). iSRn=1 (next-run-zero) for first.
        RoundTrip([0, 3, 0, -5], iNumNonZero: 2);
    }

    [Fact]
    public void TwoCoefficients_WithGap_RoundTrips()
    {
        // (run=0, level=2) then (run=3, level=4). iSRn=2 (next-run-nonzero) for first.
        RoundTrip([0, 2, 3, 4], iNumNonZero: 2);
    }

    [Fact]
    public void TwoCoefficients_LeadingRun_RoundTrips()
    {
        // (run=5, level=-1) then (run=0, level=2). Leading run non-zero.
        RoundTrip([5, -1, 0, 2], iNumNonZero: 2);
    }

    [Fact]
    public void ThreeCoefficients_MixedRuns_RoundTrips()
    {
        // Mix of leading run, gap, and adjacent.
        RoundTrip([2, 7, 0, -3, 1, 1], iNumNonZero: 3);
    }

    [Fact]
    public void FullDenseBlock_15Coefficients_AllOnes_RoundTrips()
    {
        // 15 coefficients, all run=0, all level=1. Maximum density.
        var coeff = new int[30];
        for (var k = 0; k < 15; k++)
        {
            coeff[k * 2]     = 0;
            coeff[k * 2 + 1] = (k & 1) == 0 ? 1 : -1; // alternate signs
        }
        RoundTrip(coeff, iNumNonZero: 15);
    }

    [Fact]
    public void FullDenseBlock_15Coefficients_LargeMagnitudes_RoundTrips()
    {
        // Stress the ABS_LEVEL path on every coefficient.
        var coeff = new int[30];
        for (var k = 0; k < 15; k++)
        {
            coeff[k * 2]     = 0;
            coeff[k * 2 + 1] = (k + 2) * ((k & 1) == 0 ? 1 : -1); // levels 2, -3, 4, -5, ...
        }
        RoundTrip(coeff, iNumNonZero: 15);
    }

    [Fact]
    public void SparseBlock_PositionedCoefficients_RoundTrips()
    {
        // Coefficients at scan positions 3, 7, 13 (runs of 2, 3, 5).
        // iLocation=1 → +2+1=4 (pos 3 in 0-indexed terms, 4 in 1-indexed)
        //   wait — iLocation is 1-indexed. Position of first non-zero
        //   when run=2 is iLocation + run = 1 + 2 = 3? Or iLocation + run + 1 = 4?
        // Let's just round-trip — DECODE_BLOCK_ADAPTIVE uses iCoeff[kk * 2] as
        // the run, so the absolute position is iLocation + sum of (run + 1).
        RoundTrip([2, 100, 3, -200, 5, 50], iNumNonZero: 3);
    }

    [Fact]
    public void RandomBlocks_FullSweep()
    {
        var rng = new Random(0xB10C);
        for (var trial = 0; trial < 200; trial++)
        {
            // Generate a random block with valid layout: iNumNonZero in 1..15,
            // run distribution sums to <= 14 (since first run + N-1 inter-runs
            // and 15 positions = 15 - iNumNonZero zeros total available).
            var iNumNZ = rng.Next(1, 16);
            var totalZeros = 15 - iNumNZ;
            var coeff = new int[iNumNZ * 2];
            // Distribute the zeros randomly across N+1 gap slots (but the last
            // gap is implicit — we don't emit it). So N runs sum to totalZeros.
            var runsLeft = totalZeros;
            for (var k = 0; k < iNumNZ; k++)
            {
                var maxThis = k == iNumNZ - 1 ? runsLeft : rng.Next(0, runsLeft + 1);
                coeff[k * 2] = maxThis;
                runsLeft -= maxThis;
                // Non-zero level in a reasonable range.
                coeff[k * 2 + 1] = rng.Next(-128, 129);
                if (coeff[k * 2 + 1] == 0) coeff[k * 2 + 1] = 1;
            }
            RoundTrip(coeff, iNumNZ);
        }
    }

    [Fact]
    public void Encode_ZeroLevel_Throws()
    {
        var ctx = FreshContext();
        var w = new BitWriter();
        Should.Throw<ArgumentException>(() =>
            Block.Encode(w, ref ctx, new int[] { 0, 0 }, iNumNonZero: 1, iLocation: 1));
    }

    [Fact]
    public void Encode_EmptyBlock_Throws()
    {
        var ctx = FreshContext();
        var w = new BitWriter();
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Block.Encode(w, ref ctx, Array.Empty<int>(), iNumNonZero: 0, iLocation: 1));
    }
}
