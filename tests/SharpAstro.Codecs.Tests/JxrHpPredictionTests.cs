using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-3c tests for HP coefficient prediction (T.832 9.6.3). HP prediction
/// is intra-MB only — no cross-MB references — so each MB encodes/decodes
/// independently given its mode. The encoder reverses the spec's iteration
/// order so that subtraction sees the still-unmodified reference block.
/// </summary>
public sealed class JxrHpPredictionTests
{
    private static int[,,,,] RandomHp(int mbW, int mbH, int components, Random rng, int range = 1024)
    {
        var g = new int[mbW, mbH, components, 16, 16];
        for (var y = 0; y < mbH; y++)
            for (var x = 0; x < mbW; x++)
                for (var c = 0; c < components; c++)
                    for (var b = 0; b < 16; b++)
                        for (var p = 0; p < 16; p++)
                            g[x, y, c, b, p] = rng.Next(-range, range);
        return g;
    }

    private static int[,,,,] Clone(int[,,,,] src)
    {
        var d0 = src.GetLength(0); var d1 = src.GetLength(1); var d2 = src.GetLength(2);
        var d3 = src.GetLength(3); var d4 = src.GetLength(4);
        var dst = new int[d0, d1, d2, d3, d4];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    private static void AssertEqual(int[,,,,] a, int[,,,,] expected)
    {
        var d0 = a.GetLength(0); var d1 = a.GetLength(1); var d2 = a.GetLength(2);
        var d3 = a.GetLength(3); var d4 = a.GetLength(4);
        for (var y = 0; y < d1; y++)
            for (var x = 0; x < d0; x++)
                for (var c = 0; c < d2; c++)
                    for (var blk = 0; blk < d3; blk++)
                        for (var p = 0; p < d4; p++)
                            a[x, y, c, blk, p].ShouldBe(expected[x, y, c, blk, p], $"mismatch at ({x},{y},{c}) block {blk} pos {p}");
    }

    [Theory]
    [InlineData(JxrInternalColorFormat.YOnly, 1)]
    [InlineData(JxrInternalColorFormat.YUV444, 3)]
    [InlineData(JxrInternalColorFormat.YUV422, 3)]
    [InlineData(JxrInternalColorFormat.YUV420, 3)]
    [InlineData(JxrInternalColorFormat.NComponent, 4)]
    [InlineData(JxrInternalColorFormat.Rgb, 3)]
    public void HpPrediction_RoundTrips_AcrossAllModes(JxrInternalColorFormat fmt, int numComponents)
    {
        // Try every mode 0/1/2 on every MB to exercise both prediction paths
        // (left and top) plus the no-prediction skip path.
        var rng = new Random(0xC0DE);
        var original = RandomHp(mbW: 4, mbH: 3, components: numComponents, rng);

        var mbModes = new int[4, 3];
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 4; x++)
                mbModes[x, y] = rng.Next(0, 3);

        var residual = Clone(original);
        HpPrediction.Encode(residual, mbModes, fmt);
        HpPrediction.Decode(residual, mbModes, fmt);

        AssertEqual(residual, original);
    }

    [Fact]
    public void HpPrediction_Mode2_LeavesEverythingUntouched()
    {
        var grid = RandomHp(2, 2, 1, new Random(1));
        var copy = Clone(grid);
        var modes = new int[2, 2];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
                modes[x, y] = 2;

        HpPrediction.Encode(grid, modes, JxrInternalColorFormat.YOnly);
        AssertEqual(grid, copy);
    }

    [Fact]
    public void HpPrediction_ConstantBlocks_ProduceZeroResiduals_ForPredictedPositions()
    {
        // Every block has the same value 100 at every position. With mode 0
        // (predict from left), positions {1, 5, 6} of every block except
        // column 0 should subtract 100 and become 0 (matches jxrlib's AC-from-LEFT
        // convention — verified via instrumented stderr trace). Column-0 blocks
        // (indices 0, 4, 8, 12) stay at 100.
        const int N = 100;
        var grid = new int[1, 1, 1, 16, 16];
        for (var b = 0; b < 16; b++)
            for (var p = 0; p < 16; p++)
                grid[0, 0, 0, b, p] = N;

        var modes = new int[1, 1];
        modes[0, 0] = 0; // left
        HpPrediction.Encode(grid, modes, JxrInternalColorFormat.YOnly);

        for (var b = 0; b < 16; b++)
        {
            var col = b % 4;
            foreach (var p in new[] { 1, 5, 6 })
            {
                var expected = col == 0 ? N : 0;
                grid[0, 0, 0, b, p].ShouldBe(expected, $"block {b} (col {col}) position {p}");
            }
            // Position 0 (DC slot in the block — not touched by HP prediction): unchanged.
            grid[0, 0, 0, b, 0].ShouldBe(N);
        }
    }

    [Fact]
    public void CalcMode_GradientDirection_ChoosesPrediction()
    {
        // LP positions {1,2,3} capture horizontal frequencies — strong energy
        // there means the block varies along rows, so the TOP neighbour block
        // (same column, row above) has similar pattern. Predict from top -> 1.
        var dcLpTop = new int[1, 1, 1, 16];
        dcLpTop[0, 0, 0, 1] = 1000;
        dcLpTop[0, 0, 0, 2] = 1000;
        dcLpTop[0, 0, 0, 3] = 1000;
        var modeTop = HpPrediction.CalcMode(dcLpTop, 0, 0, JxrInternalColorFormat.YOnly, 1);
        modeTop.ShouldBe(1, "strong {1,2,3} LP energy -> predict from top");

        // LP positions {4,8,12} capture vertical frequencies — strong energy
        // there means the block varies along columns, so the LEFT neighbour
        // (same row, column to the left) has similar pattern. Predict from left -> 0.
        var dcLpLeft = new int[1, 1, 1, 16];
        dcLpLeft[0, 0, 0, 4] = 1000;
        dcLpLeft[0, 0, 0, 8] = 1000;
        dcLpLeft[0, 0, 0, 12] = 1000;
        var modeLeft = HpPrediction.CalcMode(dcLpLeft, 0, 0, JxrInternalColorFormat.YOnly, 1);
        modeLeft.ShouldBe(0, "strong {4,8,12} LP energy -> predict from left");

        // Balanced -> mode 2 (no prediction).
        var dcLpBal = new int[1, 1, 1, 16];
        for (var p = 1; p <= 12; p++) dcLpBal[0, 0, 0, p] = 500;
        var modeBal = HpPrediction.CalcMode(dcLpBal, 0, 0, JxrInternalColorFormat.YOnly, 1);
        modeBal.ShouldBe(2);
    }
}
