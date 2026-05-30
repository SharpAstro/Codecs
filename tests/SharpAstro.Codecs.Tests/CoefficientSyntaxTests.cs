using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the leaf coefficient-syntax coders (run length, absolute level) by
/// encode→decode round-trip — including the abs-level escape paths for large
/// magnitudes (which exercise the multi-stage iFixed encoding). The VLC parts go
/// through the Rung-1 <see cref="AdaptiveHuffman"/> (run = alphabet 5, level =
/// alphabet 7), both pinned at table 0 as jxrlib does for these symbols.
/// </summary>
public sealed class CoefficientSyntaxTests
{
    private static AdaptiveHuffman Table0(int nSym)
    {
        var h = new AdaptiveHuffman(nSym);
        h.AdaptDiscriminant(); // init -> table gSecondDisc[nSym] (0 for 5 and 7)
        return h;
    }

    [Fact]
    public void Run_RoundTrips_AcrossAllMaxRunAndRun()
    {
        var enc = Table0(5);
        var dec = Table0(5);
        for (var maxRun = 1; maxRun <= 14; maxRun++)
        for (var run = 1; run <= maxRun; run++)
        {
            var w = new BitWriter();
            CoefficientSyntax.EncodeRun(w, enc, run, maxRun);
            w.WriteBits(0, 24); // pad for the 5-bit root peek

            var r = new BitReader(w.AsSpan());
            int got = CoefficientSyntax.DecodeRun(ref r, dec, maxRun);
            got.ShouldBe(run, $"maxRun={maxRun} run={run}");
        }
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(9)]
    [InlineData(16)] [InlineData(17)]                 // last non-escape (a = level-2 = 15)
    [InlineData(18)] [InlineData(50)] [InlineData(100)] [InlineData(1000)]  // escape, small iFixed
    [InlineData(100000)] [InlineData(1048576)]        // escape, iFixed in 18..19 range
    [InlineData(8388608)]                             // escape, iFixed > 21 path
    public void AbsLevel_RoundTrips(int absLevel)
    {
        var enc = Table0(7);
        var dec = Table0(7);

        var w = new BitWriter();
        CoefficientSyntax.EncodeAbsLevel(w, enc, absLevel);
        w.WriteBits(0, 16); // pad for root peek + escape FLC

        var r = new BitReader(w.AsSpan());
        int got = CoefficientSyntax.DecodeAbsLevel(ref r, dec);
        got.ShouldBe(absLevel);
    }

    [Fact]
    public void AbsLevel_RoundTrips_DenseSmallRange()
    {
        var enc = Table0(7);
        var dec = Table0(7);
        for (var v = 2; v <= 2000; v++)
        {
            var w = new BitWriter();
            CoefficientSyntax.EncodeAbsLevel(w, enc, v);
            w.WriteBits(0, 16);
            var r = new BitReader(w.AsSpan());
            CoefficientSyntax.DecodeAbsLevel(ref r, dec).ShouldBe(v, $"v={v}");
        }
    }
}
