using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Tile-grid round-trip for YUV420/422 — the chroma analogue of <see cref="TileCoderTests"/>.
/// Adds the reduced-chroma DC/AD/AC prediction layer (the <c>...Chroma</c> predictors + the
/// reduced copyAC neighbour buffers) on top of the chroma entropy, across real left/top
/// neighbours via <see cref="TileCoder"/>. Encode mutates the MB in place (prediction subtract),
/// so we clone originals before encoding and compare the decoded grid back to the originals.
/// Self-consistency (enc↔dec); byte-exactness vs jxrlib is the C5 oracle check.
/// </summary>
public sealed class JxrChromaTileCoderTests
{
    private const int HpQp = 1;

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 4)]
    [InlineData(4, 2)]
    [InlineData(1, 6)]
    [InlineData(6, 1)]
    [InlineData(5, 5)]
    public void Grid420_RoundTrip(int rows, int cols)
    {
        for (var seed = 0; seed < 40; seed++)
            RoundTripGrid(ColorFormat.Yuv420, rows, cols, 0x420000 + seed * 131 + rows * 17 + cols);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 4)]
    [InlineData(4, 2)]
    [InlineData(1, 6)]
    [InlineData(6, 1)]
    [InlineData(5, 5)]
    public void Grid422_RoundTrip(int rows, int cols)
    {
        for (var seed = 0; seed < 40; seed++)
            RoundTripGrid(ColorFormat.Yuv422, rows, cols, 0x422000 + seed * 131 + rows * 17 + cols);
    }

    [Fact]
    public void SingleMb_420_MatchesIsolated()
    {
        for (var seed = 0; seed < 200; seed++) RoundTripGrid(ColorFormat.Yuv420, 1, 1, 0x515 + seed);
    }

    [Fact]
    public void SingleMb_422_MatchesIsolated()
    {
        for (var seed = 0; seed < 200; seed++) RoundTripGrid(ColorFormat.Yuv422, 1, 1, 0x616 + seed);
    }

    private static void RoundTripGrid(ColorFormat cf, int rows, int cols, int seed)
    {
        int chromaBlocks = MacroblockLayout.ChromaBlocks(cf);
        var rng = new Random(seed);
        var original = new Macroblock[rows, cols];
        var encoded = new Macroblock[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                original[r, c] = Gen(cf, chromaBlocks, rng);
                encoded[r, c] = Clone(original[r, c], chromaBlocks);
            }

        var encCtx = new CodingContext(cf, 3);
        var encTile = new TileCoder(cols, 3, cf);
        var dc = new BitWriter();
        var lp = new BitWriter();
        var ac = new BitWriter();
        var fl = new BitWriter();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
                encTile.EncodeMacroblock(encCtx, encoded[r, c], c, r, dc, lp, ac, fl);
            encTile.AdvanceRow();
        }
        dc.WriteBits(0, 24); lp.WriteBits(0, 24); ac.WriteBits(0, 24); fl.WriteBits(0, 24);

        var decCtx = new CodingContext(cf, 3);
        var decTile = new TileCoder(cols, 3, cf);
        var rdc = new BitReader(dc.AsSpan());
        var rlp = new BitReader(lp.AsSpan());
        var rac = new BitReader(ac.AsSpan());
        var rfl = new BitReader(fl.AsSpan());
        var decoded = new Macroblock[rows, cols];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                decoded[r, c] = new Macroblock(3, chromaBlocks);
                decTile.DecodeMacroblock(decCtx, decoded[r, c], c, r, ref rdc, ref rlp, ref rac, ref rfl, HpQp);
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
                    for (var k = 0; k < d.BlockDc[ch].Length; k++)
                        d.BlockDc[ch][k].ShouldBe(o.BlockDc[ch][k], $"{cf} mb({r},{c}) BlockDc[{ch}][{k}]");
                    for (var p = 0; p < d.Plane[ch].Length; p++)
                        d.Plane[ch][p].ShouldBe(o.Plane[ch][p], $"{cf} mb({r},{c}) Plane[{ch}][{p}]");
                    d.Cbp[ch].ShouldBe(e.Cbp[ch], $"{cf} mb({r},{c}) Cbp[{ch}]");
                }
            }
    }

    private static Macroblock Gen(ColorFormat cf, int chromaBlocks, Random rng)
    {
        int lpCount = cf == ColorFormat.Yuv420 ? 4 : 8;
        var mb = new Macroblock(3, chromaBlocks);
        Span<int> pos = stackalloc int[15];

        mb.BlockDc[0][0] = RandLevel(rng, 3000);
        for (var k = 1; k < 16; k++) mb.BlockDc[0][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;
        FillHp(mb.Plane[0], MacroblockLayout.BlkOffset, 16, rng, pos);

        var coff = MacroblockLayout.ChromaBlkOffset(cf);
        for (var ch = 1; ch <= 2; ch++)
        {
            mb.BlockDc[ch][0] = RandLevel(rng, 3000);
            for (var k = 1; k < lpCount; k++) mb.BlockDc[ch][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;
            FillHp(mb.Plane[ch], coff, chromaBlocks, rng, pos);
        }
        return mb;
    }

    private static void FillHp(int[] plane, int[] offsets, int blocks, Random rng, Span<int> pos)
    {
        for (var b = 0; b < blocks; b++)
        {
            int off = offsets[b];
            if (rng.Next(0, 2) == 0) continue;
            int nnz = rng.Next(1, 7);
            for (var i = 0; i < 15; i++) pos[i] = i + 1;
            for (var i = 0; i < nnz; i++)
            {
                int j = rng.Next(i, 15);
                (pos[i], pos[j]) = (pos[j], pos[i]);
                plane[off + pos[i]] = RandLevel(rng, 1000, nonZero: true);
            }
        }
    }

    private static Macroblock Clone(Macroblock src, int chromaBlocks)
    {
        var d = new Macroblock(3, chromaBlocks) { Orientation = src.Orientation };
        for (var ch = 0; ch < 3; ch++)
        {
            Array.Copy(src.BlockDc[ch], d.BlockDc[ch], d.BlockDc[ch].Length);
            Array.Copy(src.Plane[ch], d.Plane[ch], d.Plane[ch].Length);
        }
        return d;
    }

    private static int RandLevel(Random rng, int max, bool nonZero = false)
    {
        int mag = rng.Next(0, 4) == 0 ? 1 : rng.Next(2, max);
        if (nonZero && mag == 0) mag = 1;
        if (!nonZero && rng.Next(0, 3) == 0) return 0;
        return rng.Next(0, 2) == 0 ? mag : -mag;
    }
}
