using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c tests for the block-level head/body INDEX syntax elements:
/// FIRST_INDEX (T.832 §8.7.18.8, alphabet 0..11, 5 adaptive tables) and
/// INDEX_A / INDEX_B / INDEX_C_FLAG (T.832 §8.7.18.7, iLocation-dispatched).
/// </summary>
public sealed class JxrFirstIndexAndIndexCodingTests
{
    // -----------------------------------------------------------------------
    // FIRST_INDEX
    // -----------------------------------------------------------------------

    [Fact]
    public void FirstIndex_AllValues_AllAdaptiveTables_RoundTrip()
    {
        for (var tableIndex = 0; tableIndex < 5; tableIndex++)
        {
            for (var v = 0; v <= 11; v++)
            {
                var enc = AdaptiveVlc.InitializeTable2();
                enc.TableIndex = tableIndex;
                var w = new BitWriter();
                FirstIndexCoding.Encode(w, ref enc, v);

                var dec = AdaptiveVlc.InitializeTable2();
                dec.TableIndex = tableIndex;
                var r = new BitReader(w.AsSpan());
                FirstIndexCoding.Decode(ref r, ref dec).ShouldBe(v, $"table {tableIndex} value {v}");

                // Encoder + decoder must agree on both discriminants.
                dec.DiscrimVal1.ShouldBe(enc.DiscrimVal1);
                dec.DiscrimVal2.ShouldBe(enc.DiscrimVal2);
            }
        }
    }

    [Fact]
    public void FirstIndex_OutOfRange_Throws()
    {
        var s = AdaptiveVlc.InitializeTable2();
        var w = new BitWriter();
        Should.Throw<ArgumentOutOfRangeException>(() => FirstIndexCoding.Encode(w, ref s, 12));
        Should.Throw<ArgumentOutOfRangeException>(() => FirstIndexCoding.Encode(w, ref s, -1));
    }

    // -----------------------------------------------------------------------
    // INDEX (positional dispatch)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(14)]    // boundary — still INDEX_A
    public void Index_IndexAPath_AllValues_AllAdaptiveTables_RoundTrip(int iLocation)
    {
        for (var tableIndex = 0; tableIndex < 4; tableIndex++)
        {
            for (var v = 0; v <= 5; v++)
            {
                var enc = AdaptiveVlc.InitializeTable2();
                enc.TableIndex = tableIndex;
                var w = new BitWriter();
                IndexCoding.Encode(w, iLocation, ref enc, v);

                var dec = AdaptiveVlc.InitializeTable2();
                dec.TableIndex = tableIndex;
                var r = new BitReader(w.AsSpan());
                IndexCoding.Decode(ref r, iLocation, ref dec).ShouldBe(v, $"iLocation={iLocation} table {tableIndex} v {v}");
                dec.DiscrimVal1.ShouldBe(enc.DiscrimVal1);
                dec.DiscrimVal2.ShouldBe(enc.DiscrimVal2);
            }
        }
    }

    [Fact]
    public void Index_IndexBPath_AllValues_RoundTrip()
    {
        // iLocation == 15 → INDEX_B, fixed table, no adaptive state.
        for (var v = 0; v <= 3; v++)
        {
            var s = AdaptiveVlc.InitializeTable2();
            var w = new BitWriter();
            IndexCoding.Encode(w, iLocation: 15, ref s, v);
            var r = new BitReader(w.AsSpan());
            var s2 = AdaptiveVlc.InitializeTable2();
            IndexCoding.Decode(ref r, iLocation: 15, ref s2).ShouldBe(v);
        }
    }

    [Fact]
    public void Index_IndexCFlagPath_RoundTrip()
    {
        // iLocation > 15 → INDEX_C_FLAG, 1-bit FLC.
        foreach (var v in new[] { 0, 1 })
        {
            var s = AdaptiveVlc.InitializeTable2();
            var w = new BitWriter();
            IndexCoding.Encode(w, iLocation: 16, ref s, v);
            w.BitPosition.ShouldBe(1, "INDEX_C_FLAG emits exactly 1 bit");

            var r = new BitReader(w.AsSpan());
            var s2 = AdaptiveVlc.InitializeTable2();
            IndexCoding.Decode(ref r, iLocation: 16, ref s2).ShouldBe(v);
        }
    }

    [Fact]
    public void Index_OutOfRange_Throws()
    {
        var s = AdaptiveVlc.InitializeTable2();
        var w = new BitWriter();
        Should.Throw<ArgumentOutOfRangeException>(() => IndexCoding.Encode(w, iLocation: 0, ref s, 6));   // INDEX_A max is 5
        Should.Throw<ArgumentOutOfRangeException>(() => IndexCoding.Encode(w, iLocation: 15, ref s, 4));  // INDEX_B max is 3
    }
}
