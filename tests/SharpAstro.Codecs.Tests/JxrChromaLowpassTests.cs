using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Component round-trip for the YUV420/422 chroma DC + LP bands (jxrlib
/// EncodeMacroblockLowpass / DecodeMacroblockLowpass chroma branch). Built
/// encoder+decoder together: the joint U+V LP interleave (LpChromaRemap, iLocation
/// 10/2), the 2-channel LP-CBP, and the interleaved-sign refinement must round-trip
/// exactly through our-encode ↔ our-decode. DC uses the joint (Y,U,V) 3-symbol path,
/// identical to 444. (Self-consistency; byte-exactness vs jxrlib is the C5 oracle check.)
/// </summary>
public sealed class JxrChromaLowpassTests
{
    [Fact]
    public void Chroma420_DcLp_RoundTrips() => RoundTripMany(ColorFormat.Yuv420);

    [Fact]
    public void Chroma422_DcLp_RoundTrips() => RoundTripMany(ColorFormat.Yuv422);

    private static void RoundTripMany(ColorFormat cf)
    {
        var rng = new Random(0x420 ^ (int)cf);
        for (var t = 0; t < 3000; t++) RoundTrip(cf, rng);
    }

    private static void RoundTrip(ColorFormat cf, Random rng)
    {
        int chromaBlocks = MacroblockLayout.ChromaBlocks(cf);
        int lpCount = cf == ColorFormat.Yuv420 ? 4 : 8; // chroma LP carries positions 1..3 / 1..7

        var src = new Macroblock(3, chromaBlocks);
        // luma DC + 15 LP coefficients
        src.BlockDc[0][0] = RandLevel(rng, 3000);
        for (var k = 1; k < 16; k++) src.BlockDc[0][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;
        // chroma DC + the carried LP positions (others stay 0 — outside the band)
        src.BlockDc[1][0] = RandLevel(rng, 3000);
        src.BlockDc[2][0] = RandLevel(rng, 3000);
        for (var k = 1; k < lpCount; k++)
        {
            src.BlockDc[1][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;
            src.BlockDc[2][k] = rng.Next(0, 3) == 0 ? RandLevel(rng, 800) : 0;
        }

        var encCtx = new CodingContext(cf, 3);
        var dc = new BitWriter();
        var lp = new BitWriter();
        MacroblockCoder.EncodeDc(encCtx, src, dc);
        MacroblockCoder.EncodeLowpass(encCtx, src, lp, resetContext: true, resetTotals: true);
        dc.WriteBits(0, 24); lp.WriteBits(0, 24); // pad for the 5-bit root peeks / refinement peeks

        var decCtx = new CodingContext(cf, 3);
        var dst = new Macroblock(3, chromaBlocks);
        var rdc = new BitReader(dc.AsSpan());
        var rlp = new BitReader(lp.AsSpan());
        MacroblockCoder.DecodeDc(decCtx, dst, ref rdc);
        MacroblockCoder.DecodeLowpass(decCtx, dst, ref rlp, resetContext: true, resetTotals: true);

        for (var ch = 0; ch < 3; ch++)
            for (var k = 0; k < dst.BlockDc[ch].Length; k++)
                dst.BlockDc[ch][k].ShouldBe(src.BlockDc[ch][k], $"BlockDc[{ch}][{k}] cf={cf}");
    }

    private static int RandLevel(Random rng, int max)
    {
        int mag = rng.Next(0, 4) == 0 ? 1 : rng.Next(2, max);
        if (rng.Next(0, 3) == 0) return 0;
        return rng.Next(0, 2) == 0 ? mag : -mag;
    }
}
