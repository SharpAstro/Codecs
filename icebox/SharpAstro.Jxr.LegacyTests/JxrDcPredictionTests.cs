using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-3a tests for DC coefficient prediction (T.832 9.6.1). Each test
/// verifies that <c>Decode(Encode(x)) == x</c> on a random MB grid for the
/// internal colour format. The forward and inverse paths share the
/// <c>CalcMode</c> routine, so a mismatch would manifest as a non-zero
/// residual after the round-trip.
/// </summary>
public sealed class JxrDcPredictionTests
{
    private static int[,,] RandomMbGrid(int mbW, int mbH, int components, Random rng, int range = 4096)
    {
        var g = new int[mbW, mbH, components];
        for (var y = 0; y < mbH; y++)
            for (var x = 0; x < mbW; x++)
                for (var c = 0; c < components; c++)
                    g[x, y, c] = rng.Next(-range, range);
        return g;
    }

    private static int[,,] Clone(int[,,] src)
    {
        var w = src.GetLength(0); var h = src.GetLength(1); var c = src.GetLength(2);
        var dst = new int[w, h, c];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    private static void AssertEqual(int[,,] a, int[,,] b)
    {
        var w = a.GetLength(0); var h = a.GetLength(1); var c = a.GetLength(2);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var k = 0; k < c; k++)
                    a[x, y, k].ShouldBe(b[x, y, k], $"mismatch at ({x},{y},{k})");
    }

    [Theory]
    [InlineData(JxrInternalColorFormat.YOnly, 1)]
    [InlineData(JxrInternalColorFormat.YUV444, 3)]
    [InlineData(JxrInternalColorFormat.YUV422, 3)]
    [InlineData(JxrInternalColorFormat.YUV420, 3)]
    [InlineData(JxrInternalColorFormat.NComponent, 4)]
    [InlineData(JxrInternalColorFormat.Rgb, 3)]
    public void DcPrediction_RoundTrips_OnRandomMbGrid(JxrInternalColorFormat fmt, int numComponents)
    {
        var rng = new Random(0x1234);
        var original = RandomMbGrid(mbW: 8, mbH: 6, components: numComponents, rng);
        var residual = Clone(original);

        var predScratch = new int[8, 6, numComponents];
        DcPrediction.Encode(residual, predScratch, fmt);

        // After encoding, the values should differ from the original (except the top-left MB
        // which has mode=3 = no prediction).
        residual[0, 0, 0].ShouldBe(original[0, 0, 0], "top-left MB has no prediction (mode 3)");
        // Some MB elsewhere must have changed — sanity check that prediction actually fired.
        var anyChanged = false;
        for (var y = 0; y < 6 && !anyChanged; y++)
            for (var x = 0; x < 8 && !anyChanged; x++)
                if (residual[x, y, 0] != original[x, y, 0])
                    anyChanged = true;
        anyChanged.ShouldBeTrue("DC prediction should change some residual values");

        // Now decode and assert exact recovery.
        var predScratch2 = new int[8, 6, numComponents];
        DcPrediction.Decode(residual, predScratch2, fmt);
        AssertEqual(residual, original);
    }

    [Fact]
    public void DcPrediction_ConstantGrid_ProducesZeroResidualsExceptCorner()
    {
        // If every MB has the same DC, prediction (from left or top) should be exact —
        // the residuals everywhere except the no-prediction corner should be zero.
        var components = 3;
        var grid = new int[5, 5, components];
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 5; x++)
                for (var c = 0; c < components; c++)
                    grid[x, y, c] = 500;

        var predScratch = new int[5, 5, components];
        DcPrediction.Encode(grid, predScratch, JxrInternalColorFormat.YUV444);

        grid[0, 0, 0].ShouldBe(500);   // No prediction — original value retained
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 5; x++)
                for (var c = 0; c < components; c++)
                    if (!(x == 0 && y == 0))
                        grid[x, y, c].ShouldBe(0, $"residual at ({x},{y},{c}) for constant grid should be 0");
    }

    [Fact]
    public void DcPrediction_TopLeftMb_HasNoPrediction()
    {
        // mode == 3 means MbDC[0,0,*] should equal its original after encode.
        var grid = new int[3, 3, 1];
        grid[0, 0, 0] = 12345;
        grid[1, 0, 0] = 12345;
        grid[0, 1, 0] = 12345;

        var pred = new int[3, 3, 1];
        DcPrediction.Encode(grid, pred, JxrInternalColorFormat.YOnly);

        grid[0, 0, 0].ShouldBe(12345);  // unchanged
        grid[1, 0, 0].ShouldBe(0);      // predicted from left (= 12345 left) → 0 residual
        grid[0, 1, 0].ShouldBe(0);      // predicted from top → 0 residual
    }

    [Fact]
    public void DcPrediction_RespectsTileEdges()
    {
        // When an MB is marked as a tile-left edge, DC prediction must NOT
        // reach into the left neighbour from the previous tile. Set up two
        // tiles side-by-side and verify that the first MB of tile 2 still
        // has no prediction from tile 1.
        var grid = new int[4, 2, 1];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
                grid[x, y, 0] = 100;

        // Two tiles: cols 0-1 = tile 0, cols 2-3 = tile 1.
        var leftMask = new bool[4, 2];
        leftMask[0, 0] = true; leftMask[2, 0] = true;
        leftMask[0, 1] = true; leftMask[2, 1] = true;
        var topMask = new bool[4, 2];
        topMask[0, 0] = true; topMask[1, 0] = true;
        topMask[2, 0] = true; topMask[3, 0] = true;

        var pred = new int[4, 2, 1];
        DcPrediction.Encode(grid, pred, JxrInternalColorFormat.YOnly, leftMask, topMask);

        // Tile-corner MBs (mode 3): unchanged.
        grid[0, 0, 0].ShouldBe(100);
        grid[2, 0, 0].ShouldBe(100, "MB (2,0) is the top-left of the second tile — must have no prediction");
    }
}
