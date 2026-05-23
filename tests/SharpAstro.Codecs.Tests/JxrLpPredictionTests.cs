using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-3b tests for LP coefficient prediction (T.832 9.6.2). LP prediction
/// only fires when (a) DC was predicted from a specific direction, and (b)
/// the neighbour MB shares the same LP-QP index. The decoder/encoder must
/// agree on which positions get touched and produce bit-exact round-trips.
/// </summary>
public sealed class JxrLpPredictionTests
{
    private static int[,,,] RandomDcLp(int mbW, int mbH, int components, Random rng, int range = 2048)
    {
        var g = new int[mbW, mbH, components, 16];
        for (var y = 0; y < mbH; y++)
            for (var x = 0; x < mbW; x++)
                for (var c = 0; c < components; c++)
                    for (var p = 0; p < 16; p++)
                        g[x, y, c, p] = rng.Next(-range, range);
        return g;
    }

    private static int[,,,] Clone(int[,,,] src)
    {
        var w = src.GetLength(0); var h = src.GetLength(1);
        var c = src.GetLength(2); var p = src.GetLength(3);
        var dst = new int[w, h, c, p];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    private static int[,] RandomDcModes(int mbW, int mbH, Random rng)
    {
        // DC modes per MB. Top-left is always 3 (no prediction); left column
        // is 1 (top only — but for top-left MB this doesn't apply); top row
        // is 0 (left only). Interior is anything in 0..2. We mirror what
        // CalcDCPredMode in DcPrediction would emit.
        var modes = new int[mbW, mbH];
        for (var y = 0; y < mbH; y++)
            for (var x = 0; x < mbW; x++)
            {
                if (x == 0 && y == 0) modes[x, y] = 3;
                else if (x == 0) modes[x, y] = 1;
                else if (y == 0) modes[x, y] = 0;
                else modes[x, y] = rng.Next(0, 3); // 0, 1, or 2
            }
        return modes;
    }

    private static void AssertEqual(int[,,,] a, int[,,,] b)
    {
        var w = a.GetLength(0); var h = a.GetLength(1);
        var c = a.GetLength(2); var p = a.GetLength(3);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var k = 0; k < c; k++)
                    for (var j = 0; j < p; j++)
                        a[x, y, k, j].ShouldBe(b[x, y, k, j], $"mismatch at ({x},{y},{k}) pos {j}");
    }

    [Theory]
    [InlineData(JxrInternalColorFormat.YOnly, 1)]
    [InlineData(JxrInternalColorFormat.YUV444, 3)]
    [InlineData(JxrInternalColorFormat.YUV422, 3)]
    [InlineData(JxrInternalColorFormat.YUV420, 3)]
    [InlineData(JxrInternalColorFormat.NComponent, 4)]
    [InlineData(JxrInternalColorFormat.Rgb, 3)]
    public void LpPrediction_RoundTrips_OnRandomMbGrid(JxrInternalColorFormat fmt, int numComponents)
    {
        var rng = new Random(0xBEEF);
        var original = RandomDcLp(mbW: 6, mbH: 5, components: numComponents, rng);
        var residual = Clone(original);
        var dcModes = RandomDcModes(6, 5, rng);

        var pred = new int[6, 5, numComponents, 16];
        LpPrediction.Encode(residual, pred, dcModes, fmt);

        var pred2 = new int[6, 5, numComponents, 16];
        LpPrediction.Decode(residual, pred2, dcModes, fmt);

        AssertEqual(residual, original);
    }

    [Fact]
    public void LpPrediction_RespectsQpIndexEquality()
    {
        // When neighbour MBs have different LP QP indices, prediction must not
        // fire even if DC mode would normally allow it.
        const int W = 4;
        const int H = 3;
        const int C = 1;
        var grid = new int[W, H, C, 16];
        var rng = new Random(7);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                for (var p = 0; p < 16; p++)
                    grid[x, y, 0, p] = rng.Next(-1000, 1000);

        // Force "predict from left" DC mode everywhere except column 0.
        var dcModes = new int[W, H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                dcModes[x, y] = x == 0 ? (y == 0 ? 3 : 1) : 0;

        // Every other column gets a different QP index — neighbour mismatch breaks prediction.
        var qpIndex = new int[W, H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                qpIndex[x, y] = x % 2;

        var original = Clone(grid);
        var pred = new int[W, H, C, 16];
        LpPrediction.Encode(grid, pred, dcModes, JxrInternalColorFormat.YOnly, qpIndex);

        var pred2 = new int[W, H, C, 16];
        LpPrediction.Decode(grid, pred2, dcModes, JxrInternalColorFormat.YOnly, qpIndex);

        AssertEqual(grid, original);
    }

    [Fact]
    public void LpPrediction_ConstantGrid_ProducesZeroResiduals_ForPredictedPositions()
    {
        // Constant LP values everywhere → prediction from neighbour should give
        // exact match, so residuals at the predicted positions are zero (except
        // the first row / column where the predicting neighbour doesn't exist).
        const int W = 4;
        const int H = 3;
        var grid = new int[W, H, 1, 16];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                for (var p = 0; p < 16; p++)
                    grid[x, y, 0, p] = 777;

        var dcModes = new int[W, H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                dcModes[x, y] = x == 0 ? (y == 0 ? 3 : 1) : 0;

        var pred = new int[W, H, 1, 16];
        LpPrediction.Encode(grid, pred, dcModes, JxrInternalColorFormat.YOnly);

        // For MB (1,0): DC mode 0 → LP mode 0 (left). Positions {4, 8, 12} get
        // predicted from left, so they should be zero residual; the rest are
        // unchanged at 777.
        grid[1, 0, 0, 4].ShouldBe(0);
        grid[1, 0, 0, 8].ShouldBe(0);
        grid[1, 0, 0, 12].ShouldBe(0);
        grid[1, 0, 0, 1].ShouldBe(777, "position 1 is not predicted by mode 0");
        grid[1, 0, 0, 5].ShouldBe(777, "position 5 is not predicted by mode 0");

        // For MB (0,1): DC mode 1 → LP mode 1 (top). Positions {1, 2, 3} get
        // predicted from top, so they should be zero residual.
        grid[0, 1, 0, 1].ShouldBe(0);
        grid[0, 1, 0, 2].ShouldBe(0);
        grid[0, 1, 0, 3].ShouldBe(0);
        grid[0, 1, 0, 4].ShouldBe(777, "position 4 is not predicted by mode 1");
    }
}
