using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Random-access decode tests — <see cref="CodedImage.DecodeTile"/> uses
/// INDEX_TABLE_TILES offsets to seek directly to one tile and decode it
/// in isolation, without walking through any of the other tiles.
/// </summary>
public sealed class JxrRandomAccessTests
{
    private static CodedImage BuildTiledImage(int gridCols, int gridRows, Func<int, int, int> dcByTileCoord)
    {
        // gridCols × gridRows tiles of 1×1 MB each. Each tile carries a single
        // distinct DC value chosen via the dcByTileCoord callback so we can
        // identify which tile was actually decoded.
        var tilesCount = gridCols * gridRows;
        var widthInMb = gridCols;
        var heightInMb = gridRows;
        var mbs = new Macroblock[tilesCount];
        for (var ty = 0; ty < gridRows; ty++)
        for (var tx = 0; tx < gridCols; tx++)
            mbs[ty * gridCols + tx] = new Macroblock { Dc = [dcByTileCoord(tx, ty)] };

        return new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = (uint)(widthInMb * 16 - 1),
                HeightMinus1 = (uint)(heightInMb * 16 - 1),
                TilingFlag = true,
                IndexTablePresentFlag = true,
                NumVerTilesMinus1 = gridCols - 1,
                NumHorTilesMinus1 = gridRows - 1,
                TileWidthInMb = Enumerable.Repeat(1, gridCols - 1).ToArray(),
                TileHeightInMb = Enumerable.Repeat(1, gridRows - 1).ToArray(),
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.DcOnly,
                NumComponents = 1,
                DcQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs,
        };
    }

    [Fact]
    public void DecodeTile_RandomOrder_RecoversCorrectTile()
    {
        // 3×3 grid; each tile's DC = unique signature.
        var img = BuildTiledImage(3, 3, (tx, ty) => 100 + tx * 10 + ty);
        var bytes = img.Encode();

        // Decode tiles in REVERSE order — exercises the random-access path
        // (sequential decode would visit them in 0,1,2,...,8 order).
        for (var ty = 2; ty >= 0; ty--)
        for (var tx = 2; tx >= 0; tx--)
        {
            var tile = CodedImage.DecodeTile(bytes, tx, ty);
            tile.Length.ShouldBe(1, $"tile ({tx},{ty}) is 1×1 MB");
            tile[0].Dc[0].ShouldBe(100 + tx * 10 + ty, $"tile ({tx},{ty})");
        }
    }

    [Fact]
    public void DecodeTile_MatchesFullDecode()
    {
        // Build, full-decode, then for each tile compare random-access decode
        // against the corresponding slice of the full decode.
        var img = BuildTiledImage(2, 2, (tx, ty) => 50 + tx * 7 + ty * 13);
        var bytes = img.Encode();
        var full = CodedImage.Decode(bytes);

        for (var ty = 0; ty < 2; ty++)
        for (var tx = 0; tx < 2; tx++)
        {
            var tile = CodedImage.DecodeTile(bytes, tx, ty);
            var expectedMbIdx = ty * 2 + tx;
            tile[0].Dc[0].ShouldBe(full.Macroblocks[expectedMbIdx].Dc[0], $"tile ({tx},{ty})");
        }
    }

    [Fact]
    public void DecodeTile_NonSquareGrid()
    {
        // 4 cols × 2 rows.
        var img = BuildTiledImage(4, 2, (tx, ty) => 200 + tx + ty * 100);
        var bytes = img.Encode();

        var corner = CodedImage.DecodeTile(bytes, 3, 1);
        corner[0].Dc[0].ShouldBe(303); // 200 + 3 + 1*100

        var firstRowMiddle = CodedImage.DecodeTile(bytes, 2, 0);
        firstRowMiddle[0].Dc[0].ShouldBe(202); // 200 + 2 + 0*100
    }

    [Fact]
    public void DecodeTile_TileOffsetsExposedAfterFullDecode()
    {
        var img = BuildTiledImage(3, 3, (tx, ty) => tx * 10 + ty);
        var bytes = img.Encode();
        var decoded = CodedImage.Decode(bytes);

        decoded.TileOffsets.ShouldNotBeNull();
        decoded.TileOffsets!.Length.ShouldBe(9);
        decoded.TileOffsets[0].ShouldBe(0L, "first tile always starts at offset 0");
        // Strictly increasing — each tile is non-empty.
        for (var i = 1; i < decoded.TileOffsets.Length; i++)
            decoded.TileOffsets[i].ShouldBeGreaterThan(decoded.TileOffsets[i - 1], $"offset {i}");
    }

    [Fact]
    public void DecodeTile_OutOfRangeCoordinate_Throws()
    {
        var img = BuildTiledImage(2, 2, (_, _) => 0);
        var bytes = img.Encode();

        Should.Throw<ArgumentOutOfRangeException>(() => CodedImage.DecodeTile(bytes, 2, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => CodedImage.DecodeTile(bytes, 0, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => CodedImage.DecodeTile(bytes, -1, 0));
    }

    [Fact]
    public void DecodeTile_RequiresTilingFlag()
    {
        // Non-tiled codestream should reject DecodeTile.
        var single = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 15,
                HeightMinus1 = 15,
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
        var bytes = single.Encode();
        Should.Throw<InvalidOperationException>(() => CodedImage.DecodeTile(bytes, 0, 0));
    }

    [Fact]
    public void DecodeTile_RequiresIndexTable()
    {
        // Tiled but no index table — DecodeTile should reject.
        var noIndex = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 31,
                HeightMinus1 = 31,
                TilingFlag = true,
                IndexTablePresentFlag = false,    // ← the test's distinguishing feature
                NumVerTilesMinus1 = 1,
                NumHorTilesMinus1 = 1,
                TileWidthInMb = [1],
                TileHeightInMb = [1],
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.DcOnly,
                NumComponents = 1,
                DcQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = Enumerable.Range(0, 4).Select(i => new Macroblock { Dc = [i] }).ToArray(),
        };
        var bytes = noIndex.Encode();
        Should.Throw<InvalidOperationException>(() => CodedImage.DecodeTile(bytes, 0, 0));
    }

    [Fact]
    public void DecodeTile_FrequencyMode_NotYetSupported()
    {
        // Frequency-mode random access needs per-band seeking — explicitly
        // rejected for now. Sequential decode still works.
        var freq = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 31,
                HeightMinus1 = 31,
                TilingFlag = true,
                FrequencyModeCodestreamFlag = true,
                IndexTablePresentFlag = true,
                NumVerTilesMinus1 = 1,
                NumHorTilesMinus1 = 1,
                TileWidthInMb = [1],
                TileHeightInMb = [1],
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.DcOnly,
                NumComponents = 1,
                DcQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = Enumerable.Range(0, 4).Select(i => new Macroblock { Dc = [i] }).ToArray(),
        };
        var bytes = freq.Encode();
        Should.Throw<NotSupportedException>(() => CodedImage.DecodeTile(bytes, 0, 0));

        // Sequential decode must still work end-to-end.
        var full = CodedImage.Decode(bytes);
        for (var i = 0; i < 4; i++) full.Macroblocks[i].Dc[0].ShouldBe(i);
    }
}
