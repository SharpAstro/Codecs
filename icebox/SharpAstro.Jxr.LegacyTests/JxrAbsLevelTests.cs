using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c tests for DECODE_ABS_LEVEL (T.832 §8.7.13) and its forward
/// counterpart. The full domain of interest is absLevel in [2, ~2^29];
/// we round-trip the small-range values exhaustively and sample the
/// escape-mode range densely enough to hit the FIXED_NUM_EXT branches.
/// </summary>
public sealed class JxrAbsLevelTests
{
    [Theory]
    [InlineData(2)]    // index 0, no LEVEL_REF
    [InlineData(3)]    // index 1, no LEVEL_REF
    [InlineData(4)][InlineData(5)]                          // index 2 range
    [InlineData(6)][InlineData(7)][InlineData(8)][InlineData(9)]    // index 3 range
    [InlineData(10)][InlineData(11)][InlineData(12)][InlineData(13)] // index 4 range
    [InlineData(14)][InlineData(15)][InlineData(16)][InlineData(17)] // index 5 range
    public void Encode_Decode_ShortRange_RoundTrip(int absLevel)
    {
        foreach (var tableIndex in new[] { 0, 1 })
        {
            var encState = AdaptiveVlc.InitializeTable1();
            encState.TableIndex = tableIndex;
            var w = new BitWriter();
            AbsLevel.Encode(w, ref encState, absLevel);

            var decState = AdaptiveVlc.InitializeTable1();
            decState.TableIndex = tableIndex;
            var r = new BitReader(w.AsSpan());
            AbsLevel.Decode(ref r, ref decState).ShouldBe(absLevel, $"tableIndex={tableIndex} absLevel={absLevel}");

            // Both sides agree on the DiscrimVal1 update — the deltaDisc
            // accumulation must be identical between encoder and decoder.
            decState.DiscrimVal1.ShouldBe(encState.DiscrimVal1);
        }
    }

    [Theory]
    [InlineData(18)]    // first escape level (iFixed=4, FIXED_NUM=0)
    [InlineData(19)]
    [InlineData(33)]    // boundary: iFixed=4, FIXED_NUM=0, LEVEL_REF=15
    [InlineData(34)]    // iFixed=5
    [InlineData(65)]    // iFixed=5 max
    [InlineData(66)]    // iFixed=6
    [InlineData(257)]   // iFixed=8
    [InlineData(65538)] // iFixed=16 — getting into the wide escape codes
    public void Encode_Decode_EscapeRange_RoundTrip(int absLevel)
    {
        var encState = AdaptiveVlc.InitializeTable1();
        var w = new BitWriter();
        AbsLevel.Encode(w, ref encState, absLevel);

        var r = new BitReader(w.AsSpan());
        var decState = AdaptiveVlc.InitializeTable1();
        AbsLevel.Decode(ref r, ref decState).ShouldBe(absLevel);
        decState.DiscrimVal1.ShouldBe(encState.DiscrimVal1);
    }

    [Fact]
    public void Encode_Decode_FixedNumExtBranch_iFixed19plus()
    {
        // iFixed=19 needs FIXED_NUM=15 + FIXED_NUM_EXT(u(2))=0. Level = 2 + 2^19 + 0
        // = 524290.
        var encState = AdaptiveVlc.InitializeTable1();
        var w = new BitWriter();
        AbsLevel.Encode(w, ref encState, 524290);
        var r = new BitReader(w.AsSpan());
        var decState = AdaptiveVlc.InitializeTable1();
        AbsLevel.Decode(ref r, ref decState).ShouldBe(524290);
    }

    [Fact]
    public void Encode_Decode_FixedNumExt2Branch_iFixed22plus()
    {
        // iFixed=22 needs FIXED_NUM=15 + FIXED_NUM_EXT=3 + FIXED_NUM_EXT2(u(3))=0.
        // Level = 2 + 2^22 + 0 = 4194306.
        var encState = AdaptiveVlc.InitializeTable1();
        var w = new BitWriter();
        AbsLevel.Encode(w, ref encState, 4194306);
        var r = new BitReader(w.AsSpan());
        var decState = AdaptiveVlc.InitializeTable1();
        AbsLevel.Decode(ref r, ref decState).ShouldBe(4194306);
    }

    [Fact]
    public void Encode_Decode_MaxIFixed29_RoundTrip()
    {
        // iFixed=29 is the maximum (FIXED_NUM_EXT2 = 7). Level = 2 + 2^29.
        const int max = 2 + (1 << 29);
        var encState = AdaptiveVlc.InitializeTable1();
        var w = new BitWriter();
        AbsLevel.Encode(w, ref encState, max);
        var r = new BitReader(w.AsSpan());
        var decState = AdaptiveVlc.InitializeTable1();
        AbsLevel.Decode(ref r, ref decState).ShouldBe(max);
    }

    [Fact]
    public void Encode_LevelBelow2_Throws()
    {
        var state = AdaptiveVlc.InitializeTable1();
        var w = new BitWriter();
        Should.Throw<ArgumentOutOfRangeException>(() => AbsLevel.Encode(w, ref state, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => AbsLevel.Encode(w, ref state, 0));
    }

    [Fact]
    public void DeltaDisc_AbsLevelIndex_ValuesMatchSpec()
    {
        // Table 86 verbatim: 1, 0, -1, -1, -1, -1, -1.
        DeltaDiscTables.AbsLevelIndex[0].ShouldBe([1, 0, -1, -1, -1, -1, -1]);
    }

    [Fact]
    public void DeltaDisc_AccumulateUpdatesDiscrimVal1()
    {
        var s = AdaptiveVlc.InitializeTable1();
        DeltaDiscTables.AccumulateTable1(ref s, DeltaDiscTables.AbsLevelIndex, iVal: 0);
        s.DiscrimVal1.ShouldBe(1);
        DeltaDiscTables.AccumulateTable1(ref s, DeltaDiscTables.AbsLevelIndex, iVal: 2);
        s.DiscrimVal1.ShouldBe(0); // 1 + (-1)
    }

    [Fact]
    public void Sweep_AllLevelsUpTo_1024_RoundTrip()
    {
        // Dense round-trip sweep for the practically-encountered range.
        for (var level = 2; level <= 1024; level++)
        {
            var encState = AdaptiveVlc.InitializeTable1();
            var w = new BitWriter();
            AbsLevel.Encode(w, ref encState, level);
            var r = new BitReader(w.AsSpan());
            var decState = AdaptiveVlc.InitializeTable1();
            AbsLevel.Decode(ref r, ref decState).ShouldBe(level, $"level {level}");
        }
    }
}
