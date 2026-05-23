namespace SharpAstro.Jxr;

/// <summary>
/// JPEG XR overlap pre/post filters — the lapped portion of the lapped
/// biorthogonal transform. These run across block boundaries so that the
/// 4×4 FCT can be applied to non-overlapping blocks while still synthesising
/// a smooth reconstruction without classic block-boundary artifacts.
/// </summary>
/// <remarks>
/// Pseudocode source: ITU-T T.832 (06/2019)
/// <list type="bullet">
///   <item>Encoder pre-filters: clause D.5 — OverlapPreFilter4x4/4/2x2/2 plus
///         helpers T2x2hEnc, FwdRotate, FwdScale, FwdTOddOdd (Tables D.14–D.21).</item>
///   <item>Decoder post-filters: clause 9.9.8 — OverlapPostFilter4x4/4/2x2/2 plus
///         helpers InvRotate, InvScale, T2x2hPOST, InvToddoddPOST (Tables 168–175).</item>
/// </list>
/// All operations are in-place on integer arrays. Each pre-filter has a
/// matching post-filter that fully inverts it bit-exact — verified by the
/// JxrOverlapFilterTests suite for random inputs.
/// </remarks>
internal static class OverlapFilters
{
    // -----------------------------------------------------------------------
    // 2-point elementary operations (T.832 D.5.1.2 / D.5.1.3 / 9.9.8.5 / 9.9.8.6)
    // -----------------------------------------------------------------------

    /// <summary>Forward 2-point rotate. T.832 D.5.1.2 / Table D.15.</summary>
    public static void FwdRotate(ref int a, ref int b)
    {
        b -= (a + 1) >> 1;
        a += (b + 1) >> 1;
    }

    /// <summary>Inverse 2-point rotate. T.832 9.9.8.5 / Table 172.</summary>
    public static void InvRotate(ref int a, ref int b)
    {
        a -= (b + 1) >> 1;
        b += (a + 1) >> 1;
    }

    /// <summary>Forward 2-point scale. T.832 D.5.1.3 / Table D.16.</summary>
    public static void FwdScale(ref int a, ref int b)
    {
        b -= (a * 3 + 0) >> 4;
        b -= a >> 7;
        b += a >> 10;
        a -= (b * 3 + 0) >> 3;
        b = (a >> 1) - b;
        a -= b;
    }

    /// <summary>Inverse 2-point scale. T.832 9.9.8.6 / Table 173.</summary>
    public static void InvScale(ref int a, ref int b)
    {
        a += b;
        b = (a >> 1) - b;
        a += (b * 3 + 0) >> 3;
        b += (a * 3 + 0) >> 4;
        b += a >> 7;
        b -= a >> 10;
    }

    // -----------------------------------------------------------------------
    // 4-point elementary operations (T.832 D.5.1.1 / D.5.1.4 / 9.9.8.7 / 9.9.8.8)
    // -----------------------------------------------------------------------

    /// <summary>Forward 2×2 Hadamard-with-bias. T.832 D.5.1.1 / Table D.14.</summary>
    public static void T2x2hEnc(Span<int> ic)
    {
        ic[0] += ic[3];
        ic[1] -= ic[2];
        var valT1 = ic[3];
        var valT2 = ic[2];
        ic[2] = ((ic[0] - ic[1]) >> 1) - valT1;
        ic[3] = valT2 + (ic[1] >> 1);
        ic[1] += ic[2];
        ic[0] -= (ic[3] * 3 + 4) >> 3;
    }

    /// <summary>Inverse 2×2 Hadamard-with-bias. T.832 9.9.8.7 / Table 174.</summary>
    public static void T2x2hPOST(Span<int> ic)
    {
        ic[1] -= ic[2];
        ic[0] += (ic[3] * 3 + 4) >> 3;
        ic[3] -= ic[1] >> 1;
        ic[2] = ((ic[0] - ic[1]) >> 1) - ic[2];
        // Spec swaps positions [2] and [3] here via an explicit valT1 temporary.
        (ic[2], ic[3]) = (ic[3], ic[2]);
        ic[0] -= ic[3];
        ic[1] += ic[2];
    }

    /// <summary>Forward 4-point 2D rotate used by the overlap filter. T.832 D.5.1.4 / Table D.17.</summary>
    public static void FwdTOddOdd(Span<int> ic)
    {
        ic[3] += ic[0];
        ic[2] -= ic[1];
        var valT1 = ic[3] >> 1;
        var valT2 = ic[2] >> 1;
        ic[0] -= valT1;
        ic[1] += valT2;
        ic[0] += (ic[1] * 3 + 4) >> 3;
        ic[1] -= (ic[0] * 3 + 2) >> 2;
        ic[0] += (ic[1] * 3 + 6) >> 3;
        ic[1] -= valT2;
        ic[0] += valT1;
        ic[2] += ic[1];
        ic[3] -= ic[0];
    }

    /// <summary>Inverse 4-point 2D rotate used by the overlap filter. T.832 9.9.8.8 / Table 175.</summary>
    public static void InvToddoddPOST(Span<int> ic)
    {
        ic[3] += ic[0];
        ic[2] -= ic[1];
        var valT1 = ic[3] >> 1;
        var valT2 = ic[2] >> 1;
        ic[0] -= valT1;
        ic[1] += valT2;
        ic[0] -= (ic[1] * 3 + 6) >> 3;
        ic[1] += (ic[0] * 3 + 2) >> 2;
        ic[0] -= (ic[1] * 3 + 4) >> 3;
        ic[1] -= valT2;
        ic[0] += valT1;
        ic[2] += ic[1];
        ic[3] -= ic[0];
    }

    // -----------------------------------------------------------------------
    // 2-point overlap pre/post (image-edge 2×1 / 1×2 boundary)
    // T.832 D.5.4 / 9.9.8.4
    // -----------------------------------------------------------------------

    /// <summary>Forward 2-point edge overlap pre-filter. T.832 D.5.4 / Table D.21.</summary>
    public static void OverlapPreFilter2(Span<int> ic)
    {
        ic[1] -= (ic[0] + 2) >> 2;
        ic[0] -= ic[1] >> 13;
        ic[0] -= ic[1] >> 9;
        ic[0] -= ic[1] >> 5;
        ic[0] -= (ic[1] + 1) >> 1;
        ic[1] -= (ic[0] + 2) >> 2;
    }

    /// <summary>Inverse 2-point edge overlap post-filter. T.832 9.9.8.4 / Table 171.</summary>
    public static void OverlapPostFilter2(Span<int> ic)
    {
        ic[1] += (ic[0] + 2) >> 2;
        ic[0] += (ic[1] + 1) >> 1;
        ic[0] += ic[1] >> 5;
        ic[0] += ic[1] >> 9;
        ic[0] += ic[1] >> 13;
        ic[1] += (ic[0] + 2) >> 2;
    }

    // -----------------------------------------------------------------------
    // 2×2 overlap pre/post (chroma DC-LP block junctions for YUV420/YUV422)
    // T.832 D.5.3 / 9.9.8.3
    // -----------------------------------------------------------------------

    /// <summary>Forward 2×2 overlap pre-filter. T.832 D.5.3 / Table D.20.</summary>
    public static void OverlapPreFilter2x2(Span<int> ic)
    {
        ic[0] += ic[3];
        ic[1] += ic[2];
        ic[3] -= (ic[0] + 1) >> 1;
        ic[2] -= (ic[1] + 1) >> 1;
        ic[1] -= (ic[0] + 2) >> 2;
        ic[0] -= ic[1] >> 5;
        ic[0] -= ic[1] >> 9;
        ic[0] -= ic[1] >> 13;
        ic[0] -= (ic[1] + 1) >> 1;
        ic[1] -= (ic[0] + 2) >> 2;
        ic[3] += (ic[0] + 1) >> 1;
        ic[2] += (ic[1] + 1) >> 1;
        ic[0] -= ic[3];
        ic[1] -= ic[2];
    }

    /// <summary>Inverse 2×2 overlap post-filter. T.832 9.9.8.3 / Table 170.</summary>
    public static void OverlapPostFilter2x2(Span<int> ic)
    {
        ic[0] += ic[3];
        ic[1] += ic[2];
        ic[3] -= (ic[0] + 1) >> 1;
        ic[2] -= (ic[1] + 1) >> 1;
        ic[1] += (ic[0] + 2) >> 2;
        ic[0] += (ic[1] + 1) >> 1;
        ic[0] += ic[1] >> 5;
        ic[0] += ic[1] >> 9;
        ic[0] += ic[1] >> 13;
        ic[1] += (ic[0] + 2) >> 2;
        ic[3] += (ic[0] + 1) >> 1;
        ic[2] += (ic[1] + 1) >> 1;
        ic[0] -= ic[3];
        ic[1] -= ic[2];
    }

    // -----------------------------------------------------------------------
    // 4-point overlap pre/post (image-edge 2×4 / 4×2 boundary)
    // T.832 D.5.2 / 9.9.8.2
    // -----------------------------------------------------------------------

    /// <summary>Forward 4-point edge overlap pre-filter. T.832 D.5.2 / Table D.19.</summary>
    public static void OverlapPreFilter4(Span<int> ic)
    {
        ic[0] += ic[3];
        ic[1] += ic[2];
        ic[3] -= (ic[0] + 1) >> 1;
        ic[2] -= (ic[1] + 1) >> 1;
        FwdRotate(ref ic[2], ref ic[3]);
        ic[3] = -ic[3];
        ic[2] = -ic[2];
        ic[0] -= ic[3];
        ic[1] -= ic[2];
        ic[3] += ic[0] >> 1;
        ic[2] += ic[1] >> 1;
        ic[0] -= (ic[3] * 3 + 4) >> 3;
        ic[1] -= (ic[2] * 3 + 4) >> 3;
        FwdScale(ref ic[0], ref ic[3]);
        FwdScale(ref ic[1], ref ic[2]);
        ic[3] += (ic[0] + 1) >> 1;
        ic[2] += (ic[1] + 1) >> 1;
        ic[0] -= ic[3];
        ic[1] -= ic[2];
    }

    /// <summary>Inverse 4-point edge overlap post-filter. T.832 9.9.8.2 / Table 169.</summary>
    public static void OverlapPostFilter4(Span<int> ic)
    {
        ic[0] += ic[3];
        ic[1] += ic[2];
        ic[3] -= (ic[0] + 1) >> 1;
        ic[2] -= (ic[1] + 1) >> 1;
        InvScale(ref ic[0], ref ic[3]);
        InvScale(ref ic[1], ref ic[2]);
        ic[0] += (ic[3] * 3 + 4) >> 3;
        ic[1] += (ic[2] * 3 + 4) >> 3;
        ic[3] -= ic[0] >> 1;
        ic[2] -= ic[1] >> 1;
        ic[0] += ic[3];
        ic[1] += ic[2];
        ic[3] = -ic[3];
        ic[2] = -ic[2];
        InvRotate(ref ic[2], ref ic[3]);
        ic[3] += (ic[0] + 1) >> 1;
        ic[2] += (ic[1] + 1) >> 1;
        ic[0] -= ic[3];
        ic[1] -= ic[2];
    }

    // -----------------------------------------------------------------------
    // 4×4 overlap pre/post (block junctions inside the image)
    // T.832 D.5.1.5 / 9.9.8.1
    //
    // The 4×4 filter operates on the 16 samples that straddle the corners of
    // four adjacent 4×4 blocks. The forward pipeline runs (in order):
    //   1. T2x2hEnc on the four 2×2 corner sub-patterns
    //   2. FwdScale on the four diagonal pairs
    //   3. FwdRotate on the four cross pairs
    //   4. FwdTOddOdd on the high-frequency corner
    //   5. T2x2h(round=0) on the four 2×2 corner sub-patterns (different ordering)
    // The post pipeline applies the exact inverses in reverse order.
    // -----------------------------------------------------------------------

    /// <summary>Forward 4×4 block-junction overlap pre-filter. T.832 D.5.1.5 / Table D.18.</summary>
    public static void OverlapPreFilter4x4(Span<int> c)
    {
        // Stage 1: T2x2hEnc on four 2×2 corner sub-patterns.
        Stage4_T2x2hEnc(c, 0, 3, 12, 15);
        Stage4_T2x2hEnc(c, 1, 2, 13, 14);
        Stage4_T2x2hEnc(c, 4, 7, 8, 11);
        Stage4_T2x2hEnc(c, 5, 6, 9, 10);

        // Stage 2: FwdScale on the four diagonal pairs.
        FwdScale(ref c[0], ref c[15]);
        FwdScale(ref c[1], ref c[14]);
        FwdScale(ref c[4], ref c[11]);
        FwdScale(ref c[5], ref c[10]);

        // Stage 3: FwdRotate on the four cross pairs.
        FwdRotate(ref c[13], ref c[12]);
        FwdRotate(ref c[9], ref c[8]);
        FwdRotate(ref c[7], ref c[3]);
        FwdRotate(ref c[6], ref c[2]);

        // Stage 4: FwdTOddOdd on the high-frequency corner.
        Stage4_FwdTOddOdd(c, 10, 11, 14, 15);

        // Stage 5: T2x2h(round=0) on four 2×2 patterns. NOTE the first group is
        // packed as (0,12,3,15), with the middle two positions intentionally
        // swapped vs Stage 1 — this is what makes the matching Post stage 1
        // (packed (0,3,12,15)) the correct inverse via T2x2h's Hadamard symmetry.
        Stage4_T2x2h(c, 0, 12, 3, 15, 0);
        Stage4_T2x2h(c, 1, 2, 13, 14, 0);
        Stage4_T2x2h(c, 4, 7, 8, 11, 0);
        Stage4_T2x2h(c, 5, 6, 9, 10, 0);
    }

    /// <summary>Inverse 4×4 block-junction overlap post-filter. T.832 9.9.8.1 / Table 168.</summary>
    public static void OverlapPostFilter4x4(Span<int> c)
    {
        // Stage 1 (undoes Pre stage 5): T2x2h(round=0) on four 2×2 patterns.
        // First group packed (0,3,12,15) — middle two swapped vs the Pre call
        // by design (see comment in OverlapPreFilter4x4).
        Stage4_T2x2h(c, 0, 3, 12, 15, 0);
        Stage4_T2x2h(c, 1, 2, 13, 14, 0);
        Stage4_T2x2h(c, 4, 7, 8, 11, 0);
        Stage4_T2x2h(c, 5, 6, 9, 10, 0);

        // Stage 2 (undoes Pre stage 3): InvRotate.
        InvRotate(ref c[13], ref c[12]);
        InvRotate(ref c[9], ref c[8]);
        InvRotate(ref c[7], ref c[3]);
        InvRotate(ref c[6], ref c[2]);

        // Stage 3 (undoes Pre stage 4): InvToddoddPOST.
        Stage4_InvToddoddPOST(c, 10, 11, 14, 15);

        // Stage 4 (undoes Pre stage 2): InvScale.
        InvScale(ref c[0], ref c[15]);
        InvScale(ref c[1], ref c[14]);
        InvScale(ref c[4], ref c[11]);
        InvScale(ref c[5], ref c[10]);

        // Stage 5 (undoes Pre stage 1): T2x2hPOST.
        Stage4_T2x2hPOST(c, 0, 3, 12, 15);
        Stage4_T2x2hPOST(c, 1, 2, 13, 14);
        Stage4_T2x2hPOST(c, 4, 7, 8, 11);
        Stage4_T2x2hPOST(c, 5, 6, 9, 10);
    }

    // -----------------------------------------------------------------------
    // Stage adapters (pack/call/unpack idiom from the spec)
    // -----------------------------------------------------------------------

    private static void Stage4_T2x2hEnc(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        T2x2hEnc(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage4_T2x2hPOST(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        T2x2hPOST(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage4_FwdTOddOdd(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        FwdTOddOdd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage4_InvToddoddPOST(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        InvToddoddPOST(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage4_T2x2h(Span<int> c, int a, int b, int d, int e, int valRound)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        Transforms.T2x2h(local, valRound);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }
}
