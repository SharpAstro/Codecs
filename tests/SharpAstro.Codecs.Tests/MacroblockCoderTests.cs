using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 6 — the first full-macroblock integration. Assembles the DC + LP + HP band
/// coders (with CBP prediction, model bits, adaptive scan, and the shared
/// <see cref="CodingContext"/> pool) into <see cref="MacroblockCoder"/> and
/// round-trips synthetic YUV444 residual coefficient planes our-encode ↔ our-decode,
/// no oracle. The HP quantizer is the identity (hpQp = 1) so the highpass plane
/// reconstructs bit-for-bit; DC/LP carry their values losslessly by construction.
/// Encode and decode each drive their own coding context, which evolve in lock-step.
/// </summary>
public sealed class MacroblockCoderTests
{
    private const int HpQp = 1; // identity highpass quantizer => plane round-trips exactly

    // The block-relative coefficient positions the highpass scans cover (a permutation
    // of 1..15); index 0 is the block DC and is never highpass-coded.
    private static (Macroblock mb, int orientation) GenMacroblock(Random rng)
    {
        var mb = new Macroblock(3) { Orientation = rng.Next(0, 2) };
        Span<int> pos = stackalloc int[15]; // distinct AC positions scratch, reused per block

        for (var ch = 0; ch < 3; ch++)
        {
            // DC + 15 lowpass AD coefficients.
            mb.BlockDc[ch][0] = RandLevel(rng, 3000);
            for (var k = 1; k < 16; k++)
                mb.BlockDc[ch][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;

            // 16 highpass blocks, each maybe carrying a few sparse AC coefficients.
            for (var blk = 0; blk < 16; blk++)
            {
                int off = MacroblockLayout.BlkOffset[blk];
                if (rng.Next(0, 2) == 0) continue; // inactive block

                int nnz = rng.Next(1, 7);
                for (var i = 0; i < 15; i++) pos[i] = i + 1; // positions 1..15
                for (var i = 0; i < nnz; i++)
                {
                    int j = rng.Next(i, 15);
                    (pos[i], pos[j]) = (pos[j], pos[i]);
                    mb.Plane[ch][off + pos[i]] = RandLevel(rng, 1000, nonZero: true);
                }
            }
        }

        return (mb, mb.Orientation);
    }

    private static int RandLevel(Random rng, int max, bool nonZero = false)
    {
        int mag = rng.Next(0, 4) == 0 ? 1 : rng.Next(2, max);
        if (nonZero && mag == 0) mag = 1;
        if (!nonZero && rng.Next(0, 3) == 0) return 0;
        return rng.Next(0, 2) == 0 ? mag : -mag;
    }

    private static void RoundTrip(Macroblock src, int orientation)
    {
        var encCtx = new CodingContext(ColorFormat.Yuv444, 3);
        var dc = new BitWriter();
        var lp = new BitWriter();
        var ac = new BitWriter();
        MacroblockCoder.Encode(encCtx, src, dc, lp, ac);
        dc.WriteBits(0, 24); lp.WriteBits(0, 24); ac.WriteBits(0, 24); // pad for 5-bit root peeks

        var decCtx = new CodingContext(ColorFormat.Yuv444, 3);
        var dst = new Macroblock(3) { Orientation = orientation };
        var rdc = new BitReader(dc.AsSpan());
        var rlp = new BitReader(lp.AsSpan());
        var rac = new BitReader(ac.AsSpan());
        MacroblockCoder.Decode(decCtx, dst, ref rdc, ref rlp, ref rac, HpQp);

        for (var ch = 0; ch < 3; ch++)
        {
            for (var k = 0; k < 16; k++)
                dst.BlockDc[ch][k].ShouldBe(src.BlockDc[ch][k], $"BlockDc[{ch}][{k}]");
            dst.Cbp[ch].ShouldBe(src.Cbp[ch], $"Cbp[{ch}]");
            for (var p = 0; p < MacroblockLayout.PlaneSize; p++)
                dst.Plane[ch][p].ShouldBe(src.Plane[ch][p], $"Plane[{ch}][{p}]");
        }
    }

    [Fact]
    public void RandomMacroblocks_RoundTrip()
    {
        var rng = new Random(0x444);
        for (var t = 0; t < 3000; t++)
        {
            var (mb, orient) = GenMacroblock(rng);
            RoundTrip(mb, orient);
        }
    }

    [Fact]
    public void AllZero_RoundTrip()
    {
        // Empty MB: no DC, no LP, no HP — exercises the all-zero CBP / significance paths.
        RoundTrip(new Macroblock(3), 0);
    }

    [Fact]
    public void DenseHighpass_AllBlocksActive_RoundTrip()
    {
        // Every highpass block significant with a full run of AC coefficients — exercises
        // the CBP=0xffff path and the end-of-block index cases at scan positions 15/16.
        var mb = new Macroblock(3) { Orientation = 0 };
        for (var ch = 0; ch < 3; ch++)
        {
            mb.BlockDc[ch][0] = 100 * (ch + 1);
            for (var k = 1; k < 16; k++) mb.BlockDc[ch][k] = (k % 2 == 0) ? 7 : -3;
            for (var blk = 0; blk < 16; blk++)
            {
                int off = MacroblockLayout.BlkOffset[blk];
                for (var p = 1; p < 16; p++) mb.Plane[ch][off + p] = ((p + blk) % 3 == 0) ? 5 : ((p % 2 == 0) ? 1 : -2);
            }
        }
        RoundTrip(mb, 0);
    }

    [Fact]
    public void OnlyDc_RoundTrip()
    {
        var mb = new Macroblock(3) { Orientation = 1 };
        mb.BlockDc[0][0] = 1234;
        mb.BlockDc[1][0] = -56;
        mb.BlockDc[2][0] = 7890;
        RoundTrip(mb, 1);
    }
}
