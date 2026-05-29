using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The first multi-component integration: wires the VLC symbol codec, run/level
/// syntax, and joint FIRST_INDEX/INDEX packing into <see cref="BlockCoder"/> and
/// round-trips real (run, level) coefficient blocks encode→decode to identity —
/// our-encoder ↔ our-decoder, no oracle. Exercises ±1 (SL=0) and larger (SL=1,
/// abs-level) magnitudes, all run lengths, and the end-of-block index special
/// cases at scan positions 15/16.
/// </summary>
public sealed class BlockCoderTests
{
    // Generate a valid run/level block starting at iLocation: positions stay within [iLocation, 15].
    private static (int[] coef, int n) GenBlock(Random rng, int iLocation)
    {
        var coef = new List<int>();
        int loc = iLocation;
        int n = 0;
        while (loc <= 15 && n < 16)
        {
            int maxRun = 15 - loc;
            int run = rng.Next(0, maxRun + 1);
            loc += run;                       // coefficient position
            int mag = rng.Next(0, 4) == 0 ? 1 : rng.Next(2, 64);
            int level = rng.Next(0, 2) == 0 ? mag : -mag;
            coef.Add(run);
            coef.Add(level);
            n++;
            loc += 1;
            if (rng.Next(0, 3) == 0) break;   // sometimes end early
        }
        if (n == 0) { coef.Add(0); coef.Add(1); n = 1; }
        return (coef.ToArray(), n);
    }

    private static void RoundTrip(int[] coef, int n, int iLocation)
    {
        var w = new BitWriter();
        BlockCoder.Encode(w, coef, n, iLocation, new BlockContext());
        w.WriteBits(0, 24); // pad for the 5-bit root peeks

        var r = new BitReader(w.AsSpan());
        var dec = new int[64];
        int dn = BlockCoder.Decode(ref r, dec, iLocation, new BlockContext());

        dn.ShouldBe(n, "nonzero count");
        for (var i = 0; i < n * 2; i++)
            dec[i].ShouldBe(coef[i], $"coef[{i}] (loc={iLocation}, n={n})");
    }

    // iLocation >= 1 is the jxrlib contract (LP/HP block coding starts at position 1;
    // only 4:2:0 / 4:2:2 chroma start at 10 / 2). maxRun = 15 - iLocation is then <= 14.
    [Theory]
    [InlineData(1)]   // typical HP / full-res LP start
    [InlineData(2)]   // 4:2:2 chroma start
    [InlineData(8)]
    [InlineData(10)]  // 4:2:0 chroma start
    [InlineData(14)]  // forces the position-15 / 16 index special cases
    public void RandomBlocks_RoundTrip(int iLocation)
    {
        var rng = new Random(0xB10C + iLocation);
        for (var t = 0; t < 4000; t++)
        {
            var (coef, n) = GenBlock(rng, iLocation);
            RoundTrip(coef, n, iLocation);
        }
    }

    [Fact]
    public void SingleNonzero_AllPositions_RoundTrip()
    {
        // One nonzero at each block position (run = pos-1 from iLocation 1) with ±1
        // and larger magnitudes (incl. the abs-level escape).
        for (var pos = 1; pos <= 15; pos++)
        foreach (var level in new[] { 1, -1, 2, -2, 37, -37, 5000 })
        {
            var coef = new[] { pos - 1, level };
            RoundTrip(coef, 1, 1);
        }
    }

    [Fact]
    public void AdjacentRun_NoGaps_RoundTrip()
    {
        // All 15 block positions (1..15) nonzero, run 0 everywhere — exercises SRn=1
        // adjacency and the end-of-block index special cases.
        var coef = new int[30];
        for (var k = 0; k < 15; k++) { coef[k * 2] = 0; coef[k * 2 + 1] = (k % 2 == 0) ? 1 : -3; }
        RoundTrip(coef, 15, 1);
    }
}
