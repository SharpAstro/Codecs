using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 5 decodes a grid's tiles in parallel and fills the float projection in parallel bands. Neither may
/// change a sample, and a damaged file must fail the same way however the work was scheduled. These run on a
/// grid built from committed x265 fixtures, so they need nothing fetched; the Canon R7 grid is checked against
/// libheif in <see cref="HeifDecodeTests"/> through the parallel path, which is the default.
/// </summary>
public sealed class HeifParallelDecodeTests
{
    private const string Fixture = "x265-420p8-window"; // 230x166, a conformance window on every picture

    [Fact]
    public void Grid_InParallel_EqualsOneThread()
    {
        // 2 x 3 tiles of 230x166, the output 680x320 so the last column and row are clipped.
        var file = Grid(680, 320, Enumerable.Repeat(Tile(), 6).ToArray());
        var serial = HeifDecoder.Decode(file, maxDegreeOfParallelism: 1);
        serial.Width.ShouldBe(680);
        serial.Height.ShouldBe(320);
        foreach (var degree in new[] { -1, 2, 3 })
            HeifDecoder.Decode(file, maxDegreeOfParallelism: degree).Samples.ShouldBe(serial.Samples, $"{degree} at once");
    }

    [Fact]
    public void Grid_TilesLandWhereTheLayoutPutsThem()
    {
        // Each tile alone decodes to the picture's own samples; in the grid they sit in row-major order.
        var (configuration, item) = HevcFixtureTests.Load(Fixture);
        var single = HeifDecoder.Decode(HeifBuilder.WrapHevcPicture(HvcC(configuration), item, 230, 166, null, null));
        var grid = HeifDecoder.Decode(Grid(460, 332, Enumerable.Repeat(Tile(), 4).ToArray(), columns: 2, rows: 2));
        for (var tile = 0; tile < 4; tile++)
        {
            int x0 = tile % 2 * 230, y0 = tile / 2 * 166;
            for (var y = 0; y < 166; y += 13)
                for (var x = 0; x < 230; x += 7)
                    for (var c = 0; c < 3; c++)
                        grid.Samples[((y0 + y) * 460 + x0 + x) * 3 + c].ShouldBe(single.Samples[(y * 230 + x) * 3 + c]);
        }
    }

    [Fact]
    public void Grid_WithTwoDamagedTiles_FailsWithTheFirstOnes_HoweverScheduled()
    {
        // Two different errors, and the earlier tile fails SLOWLY: tile 1's slice is cut in half (its length
        // rewritten to match), so it decodes half a picture before running out; tile 4 holds one SEI NAL unit
        // and no slice, so it fails at once. Run in parallel, tile 4's error usually exists first in time, and
        // tile 1's must still be the one thrown. (An instant failure of tile 1 would stop the loop before tile 4
        // starts, and could not tell "the first tile's error" from "the last one's".)
        var good = Tile();
        var tiles = Enumerable.Repeat(good, 6).ToArray();
        tiles[1] = HalveLastNal(good);
        tiles[4] = [0, 0, 0, 3, 39 << 1, 1, 0x80]; // a 4-byte length, then a prefix SEI with nothing in it
        var file = Grid(690, 332, tiles);
        var expected = Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(file, maxDegreeOfParallelism: 1)).Message;
        expected.ShouldContain("ended before its last coding tree unit");
        for (var attempt = 0; attempt < 20; attempt++)
            Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(file)).Message.ShouldBe(expected);

        tiles[1] = good; // with tile 1 mended, tile 4's error is the one
        var other = Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(Grid(690, 332, tiles))).Message;
        other.ShouldNotBe(expected);
    }

    /// <summary>The item's last NAL unit cut to half its length, with its 4-byte length prefix rewritten to match.</summary>
    private static byte[] HalveLastNal(byte[] item)
    {
        var start = 0;
        var last = 0;
        while (start < item.Length)
        {
            last = start;
            start += 4 + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(item.AsSpan(start));
        }
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(item.AsSpan(last)) / 2;
        var cut = item[..(last + 4 + length)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(cut.AsSpan(last), length);
        return cut;
    }

    [Fact]
    public void ParallelismOfZero_IsRefused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => HeifDecoder.Decode(Grid(460, 166, [Tile(), Tile()], columns: 2, rows: 1), maxDegreeOfParallelism: 0));

    [Theory]
    [InlineData(13)] // sRGB, the table path
    [InlineData(16)] // PQ, the table path at HDR scale
    [InlineData(18)] // HLG, the per-pixel OOTF path
    public void FloatProjection_InParallelBands_EqualsThePerPixelReference(int transfer)
    {
        // Large enough for several bands: a 3 x 4 grid is 920x498, about 1.75 bands of 2^18 pixels. The
        // reference is the per-pixel loop written out here, not the same banded code run on one thread (which
        // would share any banding bug), and every alpha must be 1, so a pixel no band wrote is seen.
        var file = Grid(920, 498, Enumerable.Repeat(Tile(), 12).ToArray(), columns: 4, rows: 3,
            nclx: HeifBuilder.Nclx(9, transfer, 9, true));
        var rgb = HeifDecoder.Decode(file);
        var expected = Reference(rgb);
        foreach (var degree in new[] { -1, 1, 3 })
            rgb.ToLinearRgbaFloat(out _, maxDegreeOfParallelism: degree).ShouldBe(expected, $"{degree} at once");
    }

    private static float[] Reference(HeifRgbImage rgb)
    {
        var pixels = rgb.Width * rgb.Height;
        var output = new float[pixels * 4];
        var transfer = rgb.ColorEncoding.Transfer;
        if (transfer == TransferFunction.Hlg)
        {
            var scene = new float[1 << rgb.BitDepth];
            HdrTransfer.BuildHlgSceneLightTable(rgb.BitDepth, fullRange: true, scene);
            var weights = LumaWeights.For(rgb.ColorEncoding.Primaries);
            for (var p = 0; p < pixels; p++)
                HdrTransfer.HlgSceneToLinear(scene[rgb.Samples[3 * p]], scene[rgb.Samples[3 * p + 1]], scene[rgb.Samples[3 * p + 2]], weights,
                    out output[4 * p], out output[4 * p + 1], out output[4 * p + 2]);
        }
        else
        {
            var table = new float[1 << rgb.BitDepth];
            HdrTransfer.BuildLinearTable(transfer, rgb.ColorEncoding.Primaries, rgb.BitDepth, fullRange: true, table);
            for (var p = 0; p < pixels; p++)
                for (var c = 0; c < 3; c++)
                    output[4 * p + c] = table[rgb.Samples[3 * p + c]];
        }
        for (var p = 0; p < pixels; p++) output[4 * p + 3] = 1f;
        return output;
    }

    private static byte[] Tile() => HevcFixtureTests.Load(Fixture).Item;

    private static byte[] Grid(uint width, uint height, byte[][] tiles, int columns = 3, int rows = 2, byte[]? nclx = null)
    {
        var (configuration, _) = HevcFixtureTests.Load(Fixture);
        var builder = new HeifBuilder();
        builder.AddItem(new HeifBuilder.Item(1, "grid", HeifBuilder.GridLayout(rows, columns, width, height)));
        var ids = Enumerable.Range(10, tiles.Length).Select(i => (uint)i).ToArray();
        for (var t = 0; t < tiles.Length; t++) builder.AddItem(new HeifBuilder.Item(ids[t], "hvc1", tiles[t], Hidden: true));
        builder.Reference("dimg", 1, ids);
        var hvcc = builder.Property("hvcC", HvcC(configuration));
        var size = builder.Property("ispe", HeifBuilder.Ispe(230, 166));
        foreach (var id in ids)
        {
            builder.Associate(id, hvcc, essential: true);
            builder.Associate(id, size);
        }
        builder.Associate(1, "ispe", HeifBuilder.Ispe(width, height));
        if (nclx is not null) builder.Associate(1, "colr", nclx);
        return builder.Build();
    }

    private static byte[] HvcC(HevcDecoderConfiguration c) =>
        HeifBuilder.HvcC((int)c.ChromaFormat, c.BitDepthLuma, c.BitDepthChroma, c.LengthSize, c.ProfileIdc, c.LevelIdc,
            [.. c.NalUnitArrays.Select(a => (a.NalUnitType, a.NalUnits.ToArray()))]);
}
