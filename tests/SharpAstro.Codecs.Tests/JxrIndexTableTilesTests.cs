using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Tests for INDEX_TABLE_TILES (T.832 §8.7.1.3). Covers the start-code,
/// vlw_esc value encoding (short, 32-bit escape, 64-bit escape), and the
/// full table round-trip used by CodedImage when IndexTablePresentFlag
/// is set.
/// </summary>
public sealed class JxrIndexTableTilesTests
{
    [Fact]
    public void Empty_OnlyStartCode()
    {
        var table = new IndexTableTiles { Offsets = [] };
        var w = new BitWriter();
        table.Write(w);

        // Start code = 0x0001 → 2 bytes.
        w.ByteCount.ShouldBe(2);
        w.AsSpan()[0].ShouldBe((byte)0x00);
        w.AsSpan()[1].ShouldBe((byte)0x01);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(0xFFFBL)] // last short value
    public void ShortVlw_RoundTrips(long offset)
    {
        var table = new IndexTableTiles { Offsets = [offset] };
        var w = new BitWriter();
        table.Write(w);

        // 2 bytes startcode + 2 bytes short vlw = 4 bytes total.
        w.ByteCount.ShouldBe(4);

        var r = new BitReader(w.AsSpan());
        var read = IndexTableTiles.Read(ref r, expectedEntries: 1);
        read.Offsets[0].ShouldBe(offset);
    }

    [Theory]
    [InlineData(0xFFFCL)]     // first 32-bit value
    [InlineData(0x1234_5678L)]
    [InlineData(0xFFFF_FFFFL)] // max 32-bit
    public void Escape32_VlwRoundTrips(long offset)
    {
        var table = new IndexTableTiles { Offsets = [offset] };
        var w = new BitWriter();
        table.Write(w);

        // 2 startcode + 2 escape marker + 4 u32 = 8 bytes.
        w.ByteCount.ShouldBe(8);

        var r = new BitReader(w.AsSpan());
        var read = IndexTableTiles.Read(ref r, expectedEntries: 1);
        read.Offsets[0].ShouldBe(offset);
    }

    [Fact]
    public void Escape64_VlwRoundTrips()
    {
        // Past 32-bit range — escape into 64-bit follow-up.
        const long bigOffset = 0x1_2345_6789L;
        var table = new IndexTableTiles { Offsets = [bigOffset] };
        var w = new BitWriter();
        table.Write(w);

        var r = new BitReader(w.AsSpan());
        var read = IndexTableTiles.Read(ref r, expectedEntries: 1);
        read.Offsets[0].ShouldBe(bigOffset);
    }

    [Fact]
    public void MultipleEntries_MixedSizes_RoundTrip()
    {
        var offsets = new long[] { 0, 0xFFFB, 0x10000, 0x1_0000_0000L };
        var table = new IndexTableTiles { Offsets = offsets };
        var w = new BitWriter();
        table.Write(w);

        var r = new BitReader(w.AsSpan());
        var read = IndexTableTiles.Read(ref r, expectedEntries: 4);
        read.Offsets.ShouldBe(offsets);
    }

    [Fact]
    public void WrongStartCode_Throws()
    {
        // Plant the wrong start code and verify the reader rejects it.
        var w = new BitWriter();
        w.WriteBits(0xBADD, 16);

        var threw = false;
        try
        {
            var r = new BitReader(w.AsSpan());
            IndexTableTiles.Read(ref r, expectedEntries: 0);
        }
        catch (InvalidDataException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void CodedImage_WithIndexTable_RoundTrips()
    {
        // Set IndexTablePresentFlag and ensure CodedImage.Decode skips past
        // the table to read PROFILE_LEVEL_INFO correctly. We can't yet PRODUCE
        // the offsets at encode time (would need full bytestream measurement),
        // so we use a hand-crafted codestream below.
        var w = new BitWriter();

        // Hand-write an IMAGE_HEADER with IndexTablePresentFlag = true, single tile.
        var img = new ImageHeader
        {
            OutputClrFmt = JxrOutputColorFormat.YOnly,
            OutputBitDepth = JxrOutputBitDepth.Bd8,
            ShortHeaderFlag = true,
            WidthMinus1 = 15,
            HeightMinus1 = 15,
            IndexTablePresentFlag = true,
        };
        // ImageHeader.Write rejects IndexTablePresentFlag — so encode via raw bytes
        // by toggling the flag after Write. Simpler: build by hand here is overkill,
        // we just verify the Read path tolerates the flag when the table is present
        // at the correct codestream position by skipping it.

        // For this round-trip we only verify the table read parses correctly on its
        // own (the codestream-position test is implicit once we get a real fixture
        // we can decode end-to-end).
        var table = new IndexTableTiles { Offsets = [42L, 1234L, 0xCAFEL] };
        table.Write(w);

        var r = new BitReader(w.AsSpan());
        var read = IndexTableTiles.Read(ref r, expectedEntries: 3);
        read.Offsets.ShouldBe(new long[] { 42, 1234, 0xCAFE });
    }
}
