using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Full-macroblock integration round-trip for YUV420/422 — the chroma analogue of
/// <see cref="MacroblockCoderTests"/>. Assembles DC + LP + CBP + HP (with the chroma
/// CBP packing/predictors and the reduced HP block layout) through
/// <see cref="MacroblockCoder.Encode"/>/<c>Decode</c> and round-trips synthetic
/// reduced-chroma residual planes our-encode ↔ our-decode (hpQp = 1, so HP reconstructs
/// bit-exact). Self-consistency; byte-exactness vs jxrlib is the C5 oracle check.
/// </summary>
public sealed class JxrChromaMacroblockTests
{
    private const int HpQp = 1;

    [Fact]
    public void Chroma420_Macroblock_RoundTrips() => RoundTripMany(ColorFormat.Yuv420);

    [Fact]
    public void Chroma422_Macroblock_RoundTrips() => RoundTripMany(ColorFormat.Yuv422);

    private static void RoundTripMany(ColorFormat cf)
    {
        var rng = new Random(0x3b ^ (int)cf);
        for (var t = 0; t < 3000; t++) RoundTrip(cf, Gen(cf, rng));
    }

    private static Macroblock Gen(ColorFormat cf, Random rng)
    {
        int chromaBlocks = MacroblockLayout.ChromaBlocks(cf);
        int lpCount = cf == ColorFormat.Yuv420 ? 4 : 8; // chroma LP carries positions 1..3 / 1..7
        var mb = new Macroblock(3, chromaBlocks) { Orientation = rng.Next(0, 2) };
        Span<int> pos = stackalloc int[15];

        // luma: DC + 15 LP + 16 HP blocks
        mb.BlockDc[0][0] = RandLevel(rng, 3000);
        for (var k = 1; k < 16; k++) mb.BlockDc[0][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;
        FillHp(mb.Plane[0], MacroblockLayout.BlkOffset, 16, rng, pos);

        // chroma: DC + carried LP + reduced HP blocks
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
            if (rng.Next(0, 2) == 0) continue; // inactive block
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

    private static void RoundTrip(ColorFormat cf, Macroblock src)
    {
        int chromaBlocks = MacroblockLayout.ChromaBlocks(cf);
        var encCtx = new CodingContext(cf, 3);
        var dc = new BitWriter();
        var lp = new BitWriter();
        var ac = new BitWriter();
        var fl = new BitWriter();
        MacroblockCoder.Encode(encCtx, src, dc, lp, ac, fl);
        dc.WriteBits(0, 24); lp.WriteBits(0, 24); ac.WriteBits(0, 24); fl.WriteBits(0, 24);

        var decCtx = new CodingContext(cf, 3);
        var dst = new Macroblock(3, chromaBlocks) { Orientation = src.Orientation };
        var rdc = new BitReader(dc.AsSpan());
        var rlp = new BitReader(lp.AsSpan());
        var rac = new BitReader(ac.AsSpan());
        var rfl = new BitReader(fl.AsSpan());
        MacroblockCoder.Decode(decCtx, dst, ref rdc, ref rlp, ref rac, ref rfl, HpQp);

        for (var ch = 0; ch < 3; ch++)
        {
            for (var k = 0; k < dst.BlockDc[ch].Length; k++)
                dst.BlockDc[ch][k].ShouldBe(src.BlockDc[ch][k], $"BlockDc[{ch}][{k}] cf={cf}");
            dst.Cbp[ch].ShouldBe(src.Cbp[ch], $"Cbp[{ch}] cf={cf}");
            for (var p = 0; p < dst.Plane[ch].Length; p++)
                dst.Plane[ch][p].ShouldBe(src.Plane[ch][p], $"Plane[{ch}][{p}] cf={cf}");
        }
    }

    private static int RandLevel(Random rng, int max, bool nonZero = false)
    {
        int mag = rng.Next(0, 4) == 0 ? 1 : rng.Next(2, max);
        if (nonZero && mag == 0) mag = 1;
        if (!nonZero && rng.Next(0, 3) == 0) return 0;
        return rng.Next(0, 2) == 0 ? mag : -mag;
    }
}
