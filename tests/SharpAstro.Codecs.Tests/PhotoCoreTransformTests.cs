using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the Photo Core Transform port two ways:
/// <list type="number">
/// <item><b>Known-answer</b> — forward outputs match vectors dumped from the real
/// jxrlib C (<c>Oracle/probe/transform_probe.c</c>), so the port is bit-faithful
/// to the reference, not merely self-consistent.</item>
/// <item><b>Round-trip</b> — forward∘inverse is the identity on random data, the
/// reversible-integer property the whole codec depends on.</item>
/// </list>
/// </summary>
public sealed class PhotoCoreTransformTests
{
    private const int Stride16Count = 16;
    private static readonly int[] Stride16 =
        { 0, 16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240 };

    // ---- Known-answer (golden vectors from jxrlib) ----

    [Fact]
    public void ForwardStage1_Ramp_MatchesJxrlib()
    {
        var p = new int[16];
        for (var i = 0; i < 16; i++) p[i] = i;
        PhotoCoreTransform.ForwardStage1(p);
        p.ShouldBe(new[] { 30, -4, -2, 0, 0, -14, 6, 0, 1, 2, -8, -1, 0, 0, 0, 0 });
    }

    [Fact]
    public void ForwardStage1_FlatDc_MatchesJxrlib()
    {
        var p = new int[16];
        Array.Fill(p, 100);
        PhotoCoreTransform.ForwardStage1(p);
        // Flat input → all energy in DC (index 0), everything else zero.
        p.ShouldBe(new[] { 400, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    }

    [Fact]
    public void ForwardStage1_Mixed_MatchesJxrlib()
    {
        var p = new[] { 5, -3, 7, 2, -8, 4, 0, 11, 6, -1, 9, -4, 3, 8, -2, 1 };
        PhotoCoreTransform.ForwardStage1(p);
        p.ShouldBe(new[] { 9, -2, 1, -1, -5, -3, -8, 5, -1, 16, 7, 0, 4, 1, 4, -1 });
    }

    [Fact]
    public void ForwardStage2_Mixed_MatchesJxrlib()
    {
        var p = new int[256];
        for (var k = 0; k < Stride16Count; k++) p[Stride16[k]] = (k + 1) * 7 - 50;
        PhotoCoreTransform.ForwardStage2(p);

        var got = new int[16];
        for (var k = 0; k < Stride16Count; k++) got[k] = p[Stride16[k]];
        got.ShouldBe(new[] { 38, 0, -118, 0, 0, 0, -1, -14, -30, -1, 0, 0, 0, -4, 0, 0 });
    }

    // ---- Round-trip (reversible-integer property) ----

    [Fact]
    public void Stage1_ForwardThenInverse_IsIdentity()
    {
        var rng = new Random(0x5EED);
        for (var trial = 0; trial < 200; trial++)
        {
            var original = new int[16];
            for (var i = 0; i < 16; i++) original[i] = rng.Next(-4096, 4096);
            var p = (int[])original.Clone();

            PhotoCoreTransform.ForwardStage1(p);
            PhotoCoreTransform.InverseStage1(p);

            p.ShouldBe(original, $"trial {trial}");
        }
    }

    [Fact]
    public void Stage2_ForwardThenInverse_IsIdentity()
    {
        var rng = new Random(0xC0FFEE);
        for (var trial = 0; trial < 100; trial++)
        {
            var original = new int[256];
            for (var k = 0; k < Stride16Count; k++) original[Stride16[k]] = rng.Next(-4096, 4096);
            var p = (int[])original.Clone();

            PhotoCoreTransform.ForwardStage2(p);
            PhotoCoreTransform.InverseStage2(p);

            p.ShouldBe(original, $"trial {trial}");
        }
    }

    [Fact]
    public void TwoStage_FullMacroblock_RoundTrips()
    {
        // Exercise the realistic two-stage flow on a full 16x16 macroblock:
        // stage 1 per 4x4 block, then stage 2 over the 16 block-DCs (stride 16),
        // then invert in reverse order. Must reconstruct exactly.
        var rng = new Random(0x1234ABCD);
        var original = new int[256];
        for (var i = 0; i < 256; i++) original[i] = rng.Next(-2048, 2048);
        var p = (int[])original.Clone();

        // forward: stage 1 on each contiguous 4x4 block
        for (var blk = 0; blk < 16; blk++)
            PhotoCoreTransform.ForwardStage1(p.AsSpan(blk * 16, 16));
        // forward: stage 2 over the super-DC grid
        PhotoCoreTransform.ForwardStage2(p);

        // inverse: stage 2 first, then stage 1 per block
        PhotoCoreTransform.InverseStage2(p);
        for (var blk = 0; blk < 16; blk++)
            PhotoCoreTransform.InverseStage1(p.AsSpan(blk * 16, 16));

        p.ShouldBe(original);
    }
}
