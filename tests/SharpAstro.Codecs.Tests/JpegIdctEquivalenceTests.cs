using SharpAstro.Jpeg;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="JpegIdct.Idct8x8"/> dispatches to a <c>Vector128</c> kernel wherever the
/// hardware supports it, which is everywhere this ships in practice — so the
/// StbImageSharp golden digests (<c>jpeg-oracle-golden.tsv</c>) only ever exercise
/// <em>one</em> of the two paths on any given machine, and the scalar fallback could rot
/// undetected (or, worse, the digests could be pinning a SIMD path that silently diverged
/// from the reference kernel it claims to reproduce).
///
/// <para>These tests drive both kernels directly and demand byte-for-byte equality. The
/// SIMD path stays in int32 lanes precisely so this can be exact rather than tolerant:
/// there is no "close enough" acceptable here.</para>
/// </summary>
public sealed class JpegIdctEquivalenceTests
{
    private const int Stride = 8;

    private static void BothAgree(short[] block, string because)
    {
        block.Length.ShouldBe(64);
        var scalar = new byte[64];
        var simd = new byte[64];

        JpegIdct.Idct8x8Scalar(scalar, 0, Stride, block);
        JpegIdct.Idct8x8Simd(simd, 0, Stride, block);

        for (var i = 0; i < 64; i++)
        {
            simd[i].ShouldBe(scalar[i],
                $"{because}: mismatch at sample {i} (row {i / 8}, col {i % 8}) — scalar={scalar[i]}, simd={simd[i]}");
        }
    }

    [Fact]
    public void DcOnly_TakesTheShortcutInBothPaths()
    {
        // Fires the all-AC-zero path in the scalar kernel (per column) and in the SIMD
        // kernel (per group of four) simultaneously — the one case where the two
        // shortcut granularities coincide.
        foreach (var dc in new short[] { 0, 1, -1, 255, -255, 1024, -1024, short.MaxValue, short.MinValue })
        {
            var block = new short[64];
            block[0] = dc;
            BothAgree(block, $"DC-only block, dc={dc}");
        }
    }

    [Fact]
    public void SingleCoefficient_AtEveryPosition()
    {
        // Isolates each basis function, and (because only one column is non-zero) puts
        // the two kernels' shortcut granularities maximally out of step: the scalar
        // takes the shortcut for 7 of 8 columns, the SIMD path for at most one group.
        for (var pos = 0; pos < 64; pos++)
        {
            foreach (var v in new short[] { 1, -1, 512, -512 })
            {
                var block = new short[64];
                block[0] = 64;
                block[pos] = v;
                BothAgree(block, $"single coefficient {v} at position {pos}");
            }
        }
    }

    [Fact]
    public void ClampBoundaries_AtBothEnds()
    {
        // Large low-frequency coefficients drive samples past 0 and 255 so the branchy
        // scalar Clamp and the branchless Vector128.Min/Max have to agree at the rails.
        foreach (var scale in new short[] { 4000, -4000, 16000, -16000 })
        {
            var block = new short[64];
            block[0] = scale;
            block[1] = (short)(-scale / 2);
            block[8] = (short)(scale / 3);
            block[9] = (short)(-scale / 5);
            BothAgree(block, $"clamp boundary sweep, scale={scale}");
        }
    }

    [Fact]
    public void ExtremeCoefficients_OverflowIdenticallyInBothPaths()
    {
        // short.MinValue/MaxValue coefficients can overflow the int32 intermediates. That
        // is fine and deliberately untested for "correctness" — what matters is that both
        // kernels overflow the SAME way, which they do because the SIMD path keeps int32
        // lanes and the identical operation order rather than packing 16-bit lanes.
        var rnd = new Random(4242);
        for (var iter = 0; iter < 200; iter++)
        {
            var block = new short[64];
            for (var i = 0; i < 64; i++)
                block[i] = (short)(rnd.Next(2) == 0 ? short.MinValue + rnd.Next(64) : short.MaxValue - rnd.Next(64));
            BothAgree(block, $"extreme-coefficient block, iteration {iter}");
        }
    }

    [Fact]
    public void RandomBlocks_AcrossRealisticSparsity()
    {
        // Zigzag-clustered AC, which is what real quantized JPEG blocks look like: the
        // sparsity level decides how often each kernel's all-zero shortcut fires, so
        // sweeping it covers the interesting divergence between the two granularities.
        int[] zigzag =
        [
             0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        ];

        var rnd = new Random(1337);
        foreach (var lastNonZero in new[] { 1, 3, 6, 12, 20, 32, 45, 63 })
        {
            for (var iter = 0; iter < 60; iter++)
            {
                var block = new short[64];
                block[0] = (short)rnd.Next(-2048, 2048);
                for (var k = 1; k <= lastNonZero; k++)
                {
                    if (rnd.Next(100) < 70)
                        block[zigzag[k]] = (short)rnd.Next(-400, 400);
                }

                BothAgree(block, $"random block, lastNonZero={lastNonZero}, iteration={iter}");
            }
        }
    }

    [Fact]
    public void WritesRespectOffsetAndStride()
    {
        // The SIMD row pass scatters by stride rather than writing contiguous rows, so a
        // non-trivial offset/stride is where an indexing slip would show up — and it must
        // not touch anything outside its 8x8 window.
        var rnd = new Random(5150);
        var block = new short[64];
        for (var i = 0; i < 16; i++)
            block[i] = (short)rnd.Next(-500, 500);

        const int stride = 37;
        const int offset = 11;
        var scalar = new byte[stride * 8 + offset];
        var simd = new byte[stride * 8 + offset];
        Array.Fill(scalar, (byte)0xAB);
        Array.Fill(simd, (byte)0xAB);

        JpegIdct.Idct8x8Scalar(scalar, offset, stride, block);
        JpegIdct.Idct8x8Simd(simd, offset, stride, block);

        simd.ShouldBe(scalar);

        // The untouched guard bytes must survive in both.
        for (var row = 0; row < 8; row++)
        {
            for (var k = 8; k < stride && offset + row * stride + k < simd.Length; k++)
                simd[offset + row * stride + k].ShouldBe((byte)0xAB, $"row {row} column {k} should be untouched");
        }
    }
}
