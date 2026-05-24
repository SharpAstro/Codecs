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
        // Encoder emits INDEX_TABLE_TILES when IndexTablePresentFlag is set.
        // Decoder reads it. Verify full round-trip including the table.
        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 31,
                HeightMinus1 = 31,
                TilingFlag = true,
                NumVerTilesMinus1 = 1,
                NumHorTilesMinus1 = 1,
                TileWidthInMb = [1],
                TileHeightInMb = [1],
                IndexTablePresentFlag = true, // ← the new bit
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.DcOnly,
                NumComponents = 1,
                DcQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks =
            [
                new Macroblock { Dc = [10] },
                new Macroblock { Dc = [20] },
                new Macroblock { Dc = [30] },
                new Macroblock { Dc = [40] },
            ],
        };

        var bytes = img.Encode();
        var decoded = CodedImage.Decode(bytes);

        decoded.ImageHeader.IndexTablePresentFlag.ShouldBeTrue();
        decoded.ImageHeader.TilingFlag.ShouldBeTrue();
        decoded.Macroblocks.Length.ShouldBe(4);
        decoded.Macroblocks[0].Dc[0].ShouldBe(10);
        decoded.Macroblocks[1].Dc[0].ShouldBe(20);
        decoded.Macroblocks[2].Dc[0].ShouldBe(30);
        decoded.Macroblocks[3].Dc[0].ShouldBe(40);
    }

    [Fact]
    public void IndexTable_SingleTile_RoundTrips()
    {
        // Microsoft's WIC WMPhoto encoder writes IndexTable=true even for
        // single-tile images (and Windows Photo / WIC requires it to expose
        // a frame). Encoder now supports the combination; the table carries
        // one entry per band (1 for spatial DcOnly).
        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 15,
                HeightMinus1 = 15,
                IndexTablePresentFlag = true,
                // No TilingFlag = single tile.
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.DcOnly,
                NumComponents = 1,
                DcQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [42] }],
        };

        var decoded = CodedImage.Decode(img.Encode());
        decoded.ImageHeader.IndexTablePresentFlag.ShouldBeTrue();
        decoded.TileOffsets.ShouldNotBeNull();
        decoded.TileOffsets!.Length.ShouldBe(1, "single tile + spatial mode = 1 index entry");
        decoded.Macroblocks[0].Dc[0].ShouldBe(42);
    }
}
