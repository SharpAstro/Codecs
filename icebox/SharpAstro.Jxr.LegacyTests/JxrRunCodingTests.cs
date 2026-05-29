using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c tests for DECODE_RUN (T.832 §8.7.18.6). Each (iMaxRun, iRun)
/// pair encodes the count of zero coefficients between two non-zero ones
/// in a block scan. Different iMaxRun values pick different code tables.
/// </summary>
public sealed class JxrRunCodingTests
{
    [Fact]
    public void IMaxRun1_EmitsNoBits()
    {
        var w = new BitWriter();
        RunCoding.Encode(w, iMaxRun: 1, iRun: 1);
        w.BitPosition.ShouldBe(0, "iMaxRun=1 means run is always 1, no syntax needed");

        var r = new BitReader(w.AsSpan());
        RunCoding.Decode(ref r, iMaxRun: 1).ShouldBe(1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ShortIMaxRun_AllRuns_RoundTrip(int iMaxRun)
    {
        for (var iRun = 1; iRun <= iMaxRun; iRun++)
        {
            var w = new BitWriter();
            RunCoding.Encode(w, iMaxRun, iRun);
            var r = new BitReader(w.AsSpan());
            RunCoding.Decode(ref r, iMaxRun).ShouldBe(iRun, $"iMaxRun={iMaxRun} iRun={iRun}");
        }
    }

    [Theory]
    // Each iMaxRun value goes through one of three iRunBin partitions; test
    // representative members of each.
    [InlineData(5)]   // iRunBin=2 → runs 1..6
    [InlineData(7)]   // iRunBin=2 boundary
    [InlineData(8)]   // iRunBin=1 → runs 1..10
    [InlineData(11)]  // iRunBin=1 boundary
    [InlineData(12)]  // iRunBin=0 → runs 1..14
    [InlineData(14)]  // iRunBin=0 boundary, max possible iMaxRun
    public void LongIMaxRun_AllRuns_RoundTrip(int iMaxRun)
    {
        // For each iMaxRun, iterate all valid iRun values 1..iMaxRun and
        // verify round-trip.
        for (var iRun = 1; iRun <= iMaxRun; iRun++)
        {
            var w = new BitWriter();
            RunCoding.Encode(w, iMaxRun, iRun);
            var r = new BitReader(w.AsSpan());
            RunCoding.Decode(ref r, iMaxRun).ShouldBe(iRun, $"iMaxRun={iMaxRun} iRun={iRun}");
        }
    }

    [Fact]
    public void Encode_RunExceedsMaxRun_Throws()
    {
        var w = new BitWriter();
        Should.Throw<ArgumentOutOfRangeException>(() => RunCoding.Encode(w, iMaxRun: 3, iRun: 4));
    }

    [Fact]
    public void Encode_RunZero_Throws()
    {
        var w = new BitWriter();
        Should.Throw<ArgumentOutOfRangeException>(() => RunCoding.Encode(w, iMaxRun: 5, iRun: 0));
    }

    [Fact]
    public void Encode_IMaxRunOutOfRange_Throws()
    {
        var w = new BitWriter();
        Should.Throw<ArgumentOutOfRangeException>(() => RunCoding.Encode(w, iMaxRun: 0, iRun: 1));
        Should.Throw<ArgumentOutOfRangeException>(() => RunCoding.Encode(w, iMaxRun: 15, iRun: 1));
    }
}
