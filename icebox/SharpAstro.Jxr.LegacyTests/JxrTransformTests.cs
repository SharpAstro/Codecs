using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-2a tests for the JXR core transform (T.832 D.4 / 9.9.7). The key
/// invariant is that <c>ICT4x4(FCT4x4(x)) == x</c> exactly for any 4×4 input
/// block — the transform is fully invertible in integer arithmetic, no
/// precision is lost. We verify this for a range of inputs that would
/// stress different code paths through the integer rounding.
/// </summary>
public sealed class JxrTransformTests
{
    [Fact]
    public void T2x2h_AppliedTwice_WithSameRound_IsIdentity()
    {
        // T.832 9.9.7.2 Note: applying T2x2h twice with the same valRound
        // restores the original — the operation is an involution.
        foreach (var valRound in new[] { 0, 1 })
        {
            Span<int> coeff = [17, -83, 256, -1024];
            var original = coeff.ToArray();

            Transforms.T2x2h(coeff, valRound);
            // After one application, the values have been transformed.
            coeff.SequenceEqual(original).ShouldBeFalse(
                $"T2x2h(round={valRound}) should change the values on first call");

            Transforms.T2x2h(coeff, valRound);
            coeff.SequenceEqual(original).ShouldBeTrue(
                $"T2x2h(round={valRound}) should be involutive");
        }
    }

    [Fact]
    public void FwdPermute_InvPermute_AreInverses()
    {
        Span<int> coeff = stackalloc int[16];
        for (var i = 0; i < 16; i++) coeff[i] = i + 1; // 1..16
        var original = coeff.ToArray();

        Transforms.FwdPermute(coeff);
        Transforms.InvPermute(coeff);

        coeff.SequenceEqual(original).ShouldBeTrue();
    }

    [Fact]
    public void TOdd_InvTodd_AreInverses()
    {
        // Try a few representative inputs including some that exercise the
        // signed-shift edge cases. Pure 1D rotate from T.832 D.4.2 / 9.9.7.3.
        int[][] cases =
        [
            [0, 0, 0, 0],
            [1, 2, 3, 4],
            [-1, -2, -3, -4],
            [100, -200, 300, -400],
            [int.MaxValue / 32, int.MinValue / 32, 7, -7],
        ];

        Span<int> coeff = stackalloc int[4];
        foreach (var input in cases)
        {
            input.CopyTo(coeff);

            Transforms.TOdd(coeff);
            Transforms.InvTodd(coeff);

            coeff.SequenceEqual(input).ShouldBeTrue($"TOdd round-trip failed for [{string.Join(',', input)}], got [{coeff[0]},{coeff[1]},{coeff[2]},{coeff[3]}]");
        }
    }

    [Fact]
    public void TOddOdd_InvToddodd_AreInverses()
    {
        int[][] cases =
        [
            [0, 0, 0, 0],
            [1, 2, 3, 4],
            [-1, -2, -3, -4],
            [100, -200, 300, -400],
            [int.MaxValue / 32, int.MinValue / 32, 7, -7],
        ];

        Span<int> coeff = stackalloc int[4];
        foreach (var input in cases)
        {
            input.CopyTo(coeff);

            Transforms.TOddOdd(coeff);
            Transforms.InvToddodd(coeff);

            coeff.SequenceEqual(input).ShouldBeTrue($"TOddOdd round-trip failed for [{string.Join(',', input)}], got [{coeff[0]},{coeff[1]},{coeff[2]},{coeff[3]}]");
        }
    }

    [Fact]
    public void FCT4x4_Then_ICT4x4_IsExactIdentity_OnRandomBlocks()
    {
        // Deterministic RNG so failures are reproducible.
        var rng = new Random(0xC0FFEE);
        Span<int> input = stackalloc int[16];
        Span<int> coeff = stackalloc int[16];
        for (var trial = 0; trial < 200; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                // Range ±2^14 — typical residual range for BD8 source after
                // pre-scaling but well within int safety margin for the
                // accumulating shifts inside FCT.
                input[i] = rng.Next(-16384, 16385);
                coeff[i] = input[i];
            }

            Transforms.FCT4x4(coeff);
            Transforms.ICT4x4(coeff);

            for (var i = 0; i < 16; i++)
                coeff[i].ShouldBe(input[i], $"trial #{trial} index {i}");
        }
    }

    [Fact]
    public void ICT4x4_Then_FCT4x4_IsExactIdentity_OnRandomBlocks()
    {
        // The transform pair is symmetric — running them in the other order
        // (decoder synthesises coefficients, encoder re-encodes them) also
        // produces an exact identity. Catches asymmetry bugs the forward-then-
        // inverse test would miss.
        var rng = new Random(0xCAFE);
        Span<int> input = stackalloc int[16];
        Span<int> coeff = stackalloc int[16];
        for (var trial = 0; trial < 200; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                input[i] = rng.Next(-16384, 16385);
                coeff[i] = input[i];
            }

            Transforms.ICT4x4(coeff);
            Transforms.FCT4x4(coeff);

            for (var i = 0; i < 16; i++)
                coeff[i].ShouldBe(input[i], $"trial #{trial} index {i}");
        }
    }

    [Fact]
    public void FCT4x4_DcOnly_ProducesPureDc()
    {
        // A 4×4 block where every pixel has the same value should transform
        // to a block with energy concentrated in the DC coefficient and
        // (modulo prescaling) zeros elsewhere — sanity-checks the spectral
        // interpretation of the transform.
        Span<int> coeff = stackalloc int[16];
        for (var i = 0; i < 16; i++) coeff[i] = 100;

        Transforms.FCT4x4(coeff);

        // Per spec, the DC coefficient lives at index 0 after permutation.
        coeff[0].ShouldNotBe(0, "DC of a constant block must be non-zero");
        for (var i = 1; i < 16; i++)
            coeff[i].ShouldBe(0, $"AC coefficient at index {i} must be zero for a constant input block");
    }
}
