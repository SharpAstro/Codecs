using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-2b tests for the JXR overlap pre/post filters (T.832 D.5 / 9.9.8).
/// Each forward pre-filter has a matching inverse post-filter; together they
/// must round-trip bit-exact in integer arithmetic.
/// </summary>
public sealed class JxrOverlapFilterTests
{
    // -----------------------------------------------------------------------
    // 2-point and 4-point elementary helpers
    // -----------------------------------------------------------------------

    [Fact]
    public void FwdRotate_InvRotate_AreInverses()
    {
        int[][] cases = [[0, 0], [1, 2], [-100, 100], [12345, -67890]];
        foreach (var input in cases)
        {
            int a = input[0], b = input[1];
            OverlapFilters.FwdRotate(ref a, ref b);
            OverlapFilters.InvRotate(ref a, ref b);
            a.ShouldBe(input[0]);
            b.ShouldBe(input[1]);
        }
    }

    [Fact]
    public void FwdScale_InvScale_AreInverses()
    {
        int[][] cases = [[0, 0], [1, 2], [-100, 100], [12345, -67890], [int.MaxValue / 64, int.MinValue / 64]];
        foreach (var input in cases)
        {
            int a = input[0], b = input[1];
            OverlapFilters.FwdScale(ref a, ref b);
            OverlapFilters.InvScale(ref a, ref b);
            a.ShouldBe(input[0]);
            b.ShouldBe(input[1]);
        }
    }

    [Fact]
    public void T2x2hEnc_T2x2hPOST_AreInverses()
    {
        // T2x2hEnc applied then T2x2hPOST must reproduce the input. Distinct
        // from T2x2h(round=0) twice (which is also identity but a different operation).
        int[][] cases =
        [
            [0, 0, 0, 0], [1, 2, 3, 4], [-1, -2, -3, -4],
            [100, -200, 300, -400], [int.MaxValue / 64, int.MinValue / 64, 7, -7]
        ];
        Span<int> coeff = stackalloc int[4];
        foreach (var input in cases)
        {
            input.CopyTo(coeff);
            OverlapFilters.T2x2hEnc(coeff);
            OverlapFilters.T2x2hPOST(coeff);
            coeff.SequenceEqual(input).ShouldBeTrue($"T2x2hEnc/POST round-trip failed for [{string.Join(',', input)}], got [{coeff[0]},{coeff[1]},{coeff[2]},{coeff[3]}]");
        }
    }

    [Fact]
    public void FwdTOddOdd_InvToddoddPOST_AreInverses()
    {
        int[][] cases =
        [
            [0, 0, 0, 0], [1, 2, 3, 4], [-1, -2, -3, -4],
            [100, -200, 300, -400], [int.MaxValue / 64, int.MinValue / 64, 7, -7]
        ];
        Span<int> coeff = stackalloc int[4];
        foreach (var input in cases)
        {
            input.CopyTo(coeff);
            OverlapFilters.FwdTOddOdd(coeff);
            OverlapFilters.InvToddoddPOST(coeff);
            coeff.SequenceEqual(input).ShouldBeTrue($"FwdTOddOdd/InvToddoddPOST round-trip failed for [{string.Join(',', input)}], got [{coeff[0]},{coeff[1]},{coeff[2]},{coeff[3]}]");
        }
    }

    // -----------------------------------------------------------------------
    // Boundary 2-point overlap filter
    // -----------------------------------------------------------------------

    [Fact]
    public void OverlapPreFilter2_Post_AreInverses()
    {
        // 2-point edge filter — used at boundary 2×1 / 1×2 areas.
        var rng = new Random(0x2222);
        Span<int> coeff = stackalloc int[2];
        Span<int> original = stackalloc int[2];
        for (var trial = 0; trial < 200; trial++)
        {
            original[0] = rng.Next(-8192, 8193);
            original[1] = rng.Next(-8192, 8193);
            original.CopyTo(coeff);

            OverlapFilters.OverlapPreFilter2(coeff);
            OverlapFilters.OverlapPostFilter2(coeff);

            coeff.SequenceEqual(original).ShouldBeTrue($"trial {trial}: input [{original[0]},{original[1]}], got [{coeff[0]},{coeff[1]}]");
        }
    }

    // -----------------------------------------------------------------------
    // Boundary 2×2 and 4-point chroma overlap filters
    // -----------------------------------------------------------------------

    [Fact]
    public void OverlapPreFilter2x2_Post_AreInverses()
    {
        var rng = new Random(0x2202);
        Span<int> coeff = stackalloc int[4];
        Span<int> original = stackalloc int[4];
        for (var trial = 0; trial < 200; trial++)
        {
            for (var i = 0; i < 4; i++) original[i] = rng.Next(-8192, 8193);
            original.CopyTo(coeff);

            OverlapFilters.OverlapPreFilter2x2(coeff);
            OverlapFilters.OverlapPostFilter2x2(coeff);

            coeff.SequenceEqual(original).ShouldBeTrue($"trial {trial}");
        }
    }

    [Fact]
    public void OverlapPreFilter4_Post_AreInverses()
    {
        var rng = new Random(0x4444);
        Span<int> coeff = stackalloc int[4];
        Span<int> original = stackalloc int[4];
        for (var trial = 0; trial < 200; trial++)
        {
            for (var i = 0; i < 4; i++) original[i] = rng.Next(-8192, 8193);
            original.CopyTo(coeff);

            OverlapFilters.OverlapPreFilter4(coeff);
            OverlapFilters.OverlapPostFilter4(coeff);

            coeff.SequenceEqual(original).ShouldBeTrue($"trial {trial}");
        }
    }

    // -----------------------------------------------------------------------
    // 4×4 block-junction overlap filter — the main path
    // -----------------------------------------------------------------------

    [Fact]
    public void OverlapPreFilter4x4_Post_AreInverses_OnRandomBlocks()
    {
        var rng = new Random(unchecked((int)0xFEEDFACE));
        Span<int> coeff = stackalloc int[16];
        Span<int> original = stackalloc int[16];
        for (var trial = 0; trial < 200; trial++)
        {
            for (var i = 0; i < 16; i++)
                original[i] = rng.Next(-8192, 8193);
            original.CopyTo(coeff);

            OverlapFilters.OverlapPreFilter4x4(coeff);
            OverlapFilters.OverlapPostFilter4x4(coeff);

            for (var i = 0; i < 16; i++)
                coeff[i].ShouldBe(original[i], $"trial {trial} index {i}");
        }
    }

    [Fact]
    public void OverlapPreFilter4x4_Then_FCT4x4_Then_ICT4x4_Then_Post_IsIdentity()
    {
        // Full lapped transform path: POT → FCT → ICT → inverse POT.
        // This is the actual encode-then-decode path the codec will use, so
        // verifying bit-exact round-trip here exercises everything end-to-end.
        var rng = new Random(0xABCDEF);
        Span<int> coeff = stackalloc int[16];
        Span<int> original = stackalloc int[16];
        for (var trial = 0; trial < 50; trial++)
        {
            for (var i = 0; i < 16; i++)
                original[i] = rng.Next(-4096, 4097);
            original.CopyTo(coeff);

            OverlapFilters.OverlapPreFilter4x4(coeff);
            Transforms.FCT4x4(coeff);
            Transforms.ICT4x4(coeff);
            OverlapFilters.OverlapPostFilter4x4(coeff);

            for (var i = 0; i < 16; i++)
                coeff[i].ShouldBe(original[i], $"trial {trial} index {i}");
        }
    }
}
