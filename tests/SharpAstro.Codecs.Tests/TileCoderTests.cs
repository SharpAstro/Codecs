using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 7a — the frequency-domain tile assembly. Wires per-MB DC/AD/AC + CBP
/// prediction (across real left/top neighbors, via the <see cref="PredInfo"/> row
/// buffers) into the Rung-6 band coders through <see cref="TileCoder"/>, and
/// round-trips a grid of synthetic quantized YUV444 macroblocks our-encode ↔
/// our-decode (hpQp = 1, no oracle, no signal path). Exercises the corner
/// (no-prediction), top-row (predict-from-left), left-column (predict-from-top),
/// and interior (orientation-from-neighbors) prediction paths.
/// </summary>
public sealed class TileCoderTests
{
    private const int HpQp = 1;

    private static Macroblock GenMacroblock(Random rng)
    {
        var mb = new Macroblock(3);
        Span<int> pos = stackalloc int[15];
        for (var ch = 0; ch < 3; ch++)
        {
            mb.BlockDc[ch][0] = RandLevel(rng, 3000);
            for (var k = 1; k < 16; k++)
                mb.BlockDc[ch][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;

            for (var blk = 0; blk < 16; blk++)
            {
                int off = MacroblockLayout.BlkOffset[blk];
                if (rng.Next(0, 2) == 0) continue;
                int nnz = rng.Next(1, 7);
                for (var i = 0; i < 15; i++) pos[i] = i + 1;
                for (var i = 0; i < nnz; i++)
                {
                    int j = rng.Next(i, 15);
                    (pos[i], pos[j]) = (pos[j], pos[i]);
                    mb.Plane[ch][off + pos[i]] = RandLevel(rng, 1000, nonZero: true);
                }
            }
        }
        return mb;
    }

    private static int RandLevel(Random rng, int max, bool nonZero = false)
    {
        int mag = rng.Next(0, 4) == 0 ? 1 : rng.Next(2, max);
        if (nonZero && mag == 0) mag = 1;
        if (!nonZero && rng.Next(0, 3) == 0) return 0;
        return rng.Next(0, 2) == 0 ? mag : -mag;
    }

    private static Macroblock Clone(Macroblock src)
    {
        var d = new Macroblock(3) { Orientation = src.Orientation };
        for (var ch = 0; ch < 3; ch++)
        {
            Array.Copy(src.BlockDc[ch], d.BlockDc[ch], 16);
            Array.Copy(src.Plane[ch], d.Plane[ch], MacroblockLayout.PlaneSize);
        }
        return d;
    }

    private static void RoundTripGrid(int rows, int cols, int seed)
    {
        var rng = new Random(seed);
        var original = new Macroblock[rows, cols];
        var encoded = new Macroblock[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                original[r, c] = GenMacroblock(rng);
                encoded[r, c] = Clone(original[r, c]);
            }

        var encCtx = new CodingContext(ColorFormat.Yuv444, 3);
        var encTile = new TileCoder(cols);
        var dc = new BitWriter();
        var lp = new BitWriter();
        var ac = new BitWriter();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
                encTile.EncodeMacroblock(encCtx, encoded[r, c], c, r, dc, lp, ac);
            encTile.AdvanceRow();
        }
        dc.WriteBits(0, 24); lp.WriteBits(0, 24); ac.WriteBits(0, 24);

        var decCtx = new CodingContext(ColorFormat.Yuv444, 3);
        var decTile = new TileCoder(cols);
        var rdc = new BitReader(dc.AsSpan());
        var rlp = new BitReader(lp.AsSpan());
        var rac = new BitReader(ac.AsSpan());
        var decoded = new Macroblock[rows, cols];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                decoded[r, c] = new Macroblock(3);
                decTile.DecodeMacroblock(decCtx, decoded[r, c], c, r, ref rdc, ref rlp, ref rac, HpQp);
            }
            decTile.AdvanceRow();
        }

        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var o = original[r, c];
                var d = decoded[r, c];
                var e = encoded[r, c]; // holds the computed CBP
                for (var ch = 0; ch < 3; ch++)
                {
                    for (var k = 0; k < 16; k++)
                        d.BlockDc[ch][k].ShouldBe(o.BlockDc[ch][k], $"mb({r},{c}) BlockDc[{ch}][{k}]");
                    for (var p = 0; p < MacroblockLayout.PlaneSize; p++)
                        d.Plane[ch][p].ShouldBe(o.Plane[ch][p], $"mb({r},{c}) Plane[{ch}][{p}]");
                    d.Cbp[ch].ShouldBe(e.Cbp[ch], $"mb({r},{c}) Cbp[{ch}]");
                }
            }
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 4)]
    [InlineData(4, 2)]
    [InlineData(1, 6)]
    [InlineData(6, 1)]
    [InlineData(5, 5)]
    public void Grid_RoundTrip(int rows, int cols)
    {
        for (var seed = 0; seed < 80; seed++)
            RoundTripGrid(rows, cols, 0x7A000 + seed * 131 + rows * 17 + cols);
    }

    [Fact]
    public void SingleMb_MatchesIsolatedCoder()
    {
        // A 1x1 tile must behave like the isolated Rung-6 coder (corner: no prediction).
        for (var seed = 0; seed < 200; seed++)
            RoundTripGrid(1, 1, 0x515 + seed);
    }
}
