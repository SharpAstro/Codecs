using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4b tests for the bit I/O primitives (T.832 §5.3.1 MSB-first) and
/// the VLC code-table abstraction. These foundations underpin every
/// codestream syntax element that lands in Phase 4c.
/// </summary>
public sealed class JxrBitIoTests
{
    [Fact]
    public void BitWriter_SingleByte_PacksMsbFirst()
    {
        // Writing bits 1, 0, 1, 1, 0, 0, 0, 1 should produce byte 0b10110001 = 0xB1.
        var w = new BitWriter();
        foreach (var b in new[] { true, false, true, true, false, false, false, true })
            w.WriteBit(b);
        w.ToArray().ShouldBe([0xB1]);
    }

    [Fact]
    public void BitWriter_PartialByte_PadsLowBitsWithZero()
    {
        // Writing only 3 bits "101" should produce one byte with the bits in
        // the high positions and the low 5 bits zero: 0b10100000 = 0xA0.
        var w = new BitWriter();
        w.WriteBit(true); w.WriteBit(false); w.WriteBit(true);
        w.ToArray().ShouldBe([0xA0]);
        w.BitPosition.ShouldBe(3);
        w.ByteCount.ShouldBe(1);
    }

    [Fact]
    public void BitWriter_MultiBitWrites_StraddleByteBoundary()
    {
        // 12 bits 0b101100110001: should span two bytes as 0xB3 0x10 (with the
        // low nibble of the second byte zero-padded).
        var w = new BitWriter();
        w.WriteBits(0b101100110001, 12);
        w.ToArray().ShouldBe([0xB3, 0x10]);
        w.BitPosition.ShouldBe(12);
    }

    [Fact]
    public void BitWriter_LongValue_StraddlesManyBytes()
    {
        // 24-bit value 0xABCDEF spans 3 bytes exactly.
        var w = new BitWriter();
        w.WriteBits(0xABCDEF, 24);
        w.ToArray().ShouldBe([0xAB, 0xCD, 0xEF]);
    }

    [Fact]
    public void BitWriter_GeometricGrowth_HandlesLargePayloads()
    {
        // Force the buffer to grow several times by writing a kilobit.
        var w = new BitWriter(initialCapacity: 4);
        for (var i = 0; i < 1024; i++) w.WriteBit((i & 1) == 0);
        w.ByteCount.ShouldBe(128);
    }

    [Fact]
    public void BitReader_ReadsBackWrittenBits_RoundTrip()
    {
        var rng = new Random(0x42);
        var w = new BitWriter();
        var pattern = new (uint value, int length)[64];
        for (var i = 0; i < pattern.Length; i++)
        {
            var len = rng.Next(1, 17);
            var mask = len == 32 ? uint.MaxValue : (1u << len) - 1;
            pattern[i] = ((uint)rng.Next() & mask, len);
            w.WriteBits(pattern[i].value, len);
        }

        var reader = new BitReader(w.AsSpan());
        for (var i = 0; i < pattern.Length; i++)
            reader.ReadBits(pattern[i].length).ShouldBe(pattern[i].value, $"entry {i}");
    }

    [Fact]
    public void BitReader_SignedMagnitude_RoundTrips()
    {
        var w = new BitWriter();
        w.WriteSignedMagnitude(-1234, absLength: 11);
        w.WriteSignedMagnitude(1234, absLength: 11);
        w.WriteSignedMagnitude(0, absLength: 1);

        var r = new BitReader(w.AsSpan());
        r.ReadSignedMagnitude(11).ShouldBe(-1234);
        r.ReadSignedMagnitude(11).ShouldBe(1234);
        r.ReadSignedMagnitude(1).ShouldBe(0);
    }

    [Fact]
    public void BitReader_PeekBits_DoesNotAdvancePosition()
    {
        var w = new BitWriter();
        w.WriteBits(0b11001010, 8);
        var r = new BitReader(w.AsSpan());

        r.PeekBits(4).ShouldBe(0b1100u);
        r.BitPosition.ShouldBe(0, "peek must not advance");
        r.PeekBits(8).ShouldBe(0b11001010u);
        r.BitPosition.ShouldBe(0);
        r.ReadBits(8).ShouldBe(0b11001010u);
        r.BitPosition.ShouldBe(8);
    }

    [Fact]
    public void BitReader_OverRun_Throws()
    {
        // ref struct can't escape into a lambda, so use a flag rather than
        // Should.Throw to catch the expected EndOfStreamException.
        var r = new BitReader([0b10000000]);
        for (var i = 0; i < 8; i++) r.ReadBit();

        var threw = false;
        try { r.ReadBit(); }
        catch (EndOfStreamException) { threw = true; }
        threw.ShouldBeTrue("reading past the end should throw EndOfStreamException");
    }

    // -----------------------------------------------------------------------
    // VlcCodeTable
    // -----------------------------------------------------------------------

    [Fact]
    public void VlcCodeTable_ValDcYuv_EncodeMatchesSpec()
    {
        // Table 51 from T.832 8.7.14.2.
        VlcTables.ValDcYuv.Encode(0).ShouldBe(((uint)0b10, 2));
        VlcTables.ValDcYuv.Encode(2).ShouldBe(((uint)0b00001, 5));
        VlcTables.ValDcYuv.Encode(6).ShouldBe(((uint)0b00000, 5));
        VlcTables.ValDcYuv.Encode(7).ShouldBe(((uint)0b011, 3));
    }

    [Fact]
    public void VlcCodeTable_ValDcYuv_RoundTripsAllValues()
    {
        for (var v = 0; v <= 7; v++)
        {
            var w = new BitWriter();
            VlcTables.ValDcYuv.Encode(w, v);
            var r = new BitReader(w.AsSpan());
            VlcTables.ValDcYuv.Decode(ref r).ShouldBe(v);
        }
    }

    [Fact]
    public void VlcCodeTable_AbsLevelIndex_BothTables_RoundTripAllValues()
    {
        // Both AbsLevelIndex tables encode values 0..6; the bit-codes differ
        // per table (Table 52 in the spec has two columns).
        foreach (var tableIndex in new[] { 0, 1 })
        {
            var table = VlcTables.AbsLevelIndex[tableIndex];
            for (var v = 0; v <= 6; v++)
            {
                var w = new BitWriter();
                table.Encode(w, v);
                var r = new BitReader(w.AsSpan());
                table.Decode(ref r).ShouldBe(v, $"table {tableIndex}, value {v}");
            }
        }
    }

    [Fact]
    public void VlcCodeTable_OutOfRangeValue_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => VlcTables.ValDcYuv.Encode(8));
    }

    [Fact]
    public void VlcCodeTable_DuplicateValueInConstruction_Throws()
    {
        Should.Throw<ArgumentException>(() => new VlcCodeTable(
        [
            new(Value: 0, Code: 0b1, Length: 1),
            new(Value: 0, Code: 0b01, Length: 2), // duplicate
        ]));
    }
}
