using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Sweep tests for every VLC code table ported from T.832 §8.7. Each table
/// is exercised by encoding every defined value, decoding the result, and
/// asserting the round-trip value matches. This catches transcription
/// errors (wrong bit pattern, wrong length, swapped value-to-code mapping)
/// because any of those break the prefix property and cause Decode to
/// either fail or return a different value.
/// </summary>
public sealed class JxrVlcTablesTests
{
    // -----------------------------------------------------------------------
    // Spec-pinned encode samples — make sure individual entries that I
    // transcribed by hand match the spec page exactly. Round-trip alone could
    // pass with all bits accidentally inverted; these lock down direction.
    // -----------------------------------------------------------------------

    [Fact]
    public void Table55_CbpLpYuv444_KnownEntries()
    {
        VlcTables.CbpLpYuv444.Encode(0).ShouldBe(((uint)0b0,    1));
        VlcTables.CbpLpYuv444.Encode(1).ShouldBe(((uint)0b100,  3));
        VlcTables.CbpLpYuv444.Encode(7).ShouldBe(((uint)0b1111, 4));
    }

    [Fact]
    public void Table56_CbpLpYuv420Or422_KnownEntries()
    {
        VlcTables.CbpLpYuv420Or422.Encode(0).ShouldBe(((uint)0b0,   1));
        VlcTables.CbpLpYuv420Or422.Encode(3).ShouldBe(((uint)0b111, 3));
    }

    [Fact]
    public void Table62_ChrCbphp_KnownEntries()
    {
        VlcTables.ChrCbphp.Encode(0).ShouldBe(((uint)0b1,  1));
        VlcTables.ChrCbphp.Encode(2).ShouldBe(((uint)0b00, 2));
    }

    [Fact]
    public void Table64_RefCbphp1_SparseValues()
    {
        // REF_CBPHP1 has sparse values 3, 5, 6, 9, 10, 12 (not contiguous).
        VlcTables.RefCbphp1.Encode(3).ShouldBe(((uint)0b00,  2));
        VlcTables.RefCbphp1.Encode(12).ShouldBe(((uint)0b111, 3));
    }

    [Fact]
    public void Table79_RunIndex_KnownEntries()
    {
        VlcTables.RunIndex.Encode(0).ShouldBe(((uint)0b1,    1));
        VlcTables.RunIndex.Encode(4).ShouldBe(((uint)0b0001, 4));
    }

    [Fact]
    public void Table82_FirstIndex_Code0_KnownEntries()
    {
        // Code 0 column from Table 82.
        VlcTables.FirstIndex[0].Encode(7).ShouldBe(((uint)0b1,        1));
        VlcTables.FirstIndex[0].Encode(0).ShouldBe(((uint)0b00001,    5));
        VlcTables.FirstIndex[0].Encode(2).ShouldBe(((uint)0b0000000,  7));
    }

    // -----------------------------------------------------------------------
    // Round-trip sweeps over every defined value of every table.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RunValue_AllMaxRunVariants_RoundTrip(int iMaxRun)
    {
        var table = VlcTables.RunValue(iMaxRun);
        for (var v = 1; v <= iMaxRun; v++)
        {
            var w = new BitWriter();
            table.Encode(w, v);
            var r = new BitReader(w.AsSpan());
            table.Decode(ref r).ShouldBe(v);
        }
    }

    [Fact]
    public void NumCbphp_BothAdaptiveTables_RoundTripAllValues()
    {
        foreach (var tableIndex in new[] { 0, 1 })
        {
            var table = VlcTables.NumCbphp[tableIndex];
            for (var v = 0; v <= 4; v++)
                RoundTrip(table, v).ShouldBe(v, $"NumCbphp table {tableIndex} value {v}");
        }
    }

    [Fact]
    public void NumBlkCbphpColour_BothAdaptiveTables_RoundTripAllValues()
    {
        foreach (var tableIndex in new[] { 0, 1 })
        {
            var table = VlcTables.NumBlkCbphpColour[tableIndex];
            for (var v = 0; v <= 8; v++)
                RoundTrip(table, v).ShouldBe(v, $"NumBlkCbphpColour table {tableIndex} value {v}");
        }
    }

    [Fact]
    public void IndexA_AllFourAdaptiveTables_RoundTripAllValues()
    {
        for (var tableIndex = 0; tableIndex < 4; tableIndex++)
        {
            var table = VlcTables.IndexA[tableIndex];
            for (var v = 0; v <= 5; v++)
                RoundTrip(table, v).ShouldBe(v, $"IndexA table {tableIndex} value {v}");
        }
    }

    [Fact]
    public void IndexB_RoundTripAllValues()
    {
        for (var v = 0; v <= 3; v++)
            RoundTrip(VlcTables.IndexB, v).ShouldBe(v);
    }

    [Fact]
    public void FirstIndex_AllFiveAdaptiveTables_RoundTripAllValues()
    {
        for (var tableIndex = 0; tableIndex < 5; tableIndex++)
        {
            var table = VlcTables.FirstIndex[tableIndex];
            for (var v = 0; v <= 11; v++)
                RoundTrip(table, v).ShouldBe(v, $"FirstIndex table {tableIndex} value {v}");
        }
    }

    [Theory]
    [InlineData(nameof(VlcTables.CbpLpYuv444), 0, 7)]
    [InlineData(nameof(VlcTables.CbpLpYuv420Or422), 0, 3)]
    [InlineData(nameof(VlcTables.ChrCbphp), 0, 2)]
    [InlineData(nameof(VlcTables.NumChBlk), 0, 3)]
    [InlineData(nameof(VlcTables.RunIndex), 0, 4)]
    public void SingleTables_RoundTripAllValues(string tableName, int minValue, int maxValue)
    {
        var table = (VlcCodeTable)typeof(VlcTables).GetField(tableName)!.GetValue(null)!;
        for (var v = minValue; v <= maxValue; v++)
            RoundTrip(table, v).ShouldBe(v, $"{tableName} value {v}");
    }

    [Fact]
    public void RefCbphp1_RoundTripsSparseValues()
    {
        // REF_CBPHP1 uses sparse value indices 3, 5, 6, 9, 10, 12 — skip the others.
        foreach (var v in new[] { 3, 5, 6, 9, 10, 12 })
            RoundTrip(VlcTables.RefCbphp1, v).ShouldBe(v);
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static int RoundTrip(VlcCodeTable table, int value)
    {
        var w = new BitWriter();
        table.Encode(w, value);
        var r = new BitReader(w.AsSpan());
        return table.Decode(ref r);
    }
}
