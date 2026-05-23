namespace SharpAstro.Jxr;

/// <summary>
/// JPEG XR core transform operations — the integer-arithmetic 4×4 lapped
/// biorthogonal transform that sits at the heart of the codec.
/// </summary>
/// <remarks>
/// Pseudocode source: ITU-T T.832 (06/2019)
/// <list type="bullet">
///   <item>Forward (encoder, FCT): clause D.4 — TOdd, TOddOdd, FwdPermute, FCT4x4.</item>
///   <item>Inverse (decoder, ICT): clause 9.9.7 — InvTodd, InvToddodd, InvPermute, ICT4x4.</item>
///   <item>Shared building block: T2x2h (clause 9.9.7.2) — self-inverse under two
///     successive applications with the same <c>valRound</c>.</item>
/// </list>
/// All operations are in-place on a length-16 row-major <c>Span&lt;int&gt;</c>
/// representing a 4×4 block in raster order (index = row*4 + col). Integer
/// shifts are arithmetic (signed) which matches the spec's assumption that
/// <c>iCoeff[]</c> values are signed integers.
///
/// Per spec Note 1 of D.4: <c>ICT(FCT(x)) == x</c> exactly, and the unit
/// tests in <c>JxrTransformTests</c> verify this for a range of random and
/// edge-case inputs.
/// </remarks>
internal static class Transforms
{
    /// <summary>
    /// Forward Core Transform on a 4×4 block. T.832 D.4.5.1 / Table D.13.
    /// </summary>
    public static void FCT4x4(Span<int> c)
    {
        // First stage: T2x2h on the four "corner" 2×2 sub-patterns of a 4×4
        // block, in the (0,3,12,15) / (5,6,9,10) / (1,2,13,14) / (4,7,8,11)
        // arrangement specified by Table D.13.
        Stage_T2x2h(c, 0, 3, 12, 15, 0);
        Stage_T2x2h(c, 5, 6, 9, 10, 0);
        Stage_T2x2h(c, 1, 2, 13, 14, 0);
        Stage_T2x2h(c, 4, 7, 8, 11, 0);

        // Second stage: one T2x2h(round=1) for the low-frequency 2×2 corner,
        // two TOdd (1D rotates) for the row/column odd bands, one TOddOdd
        // (2D rotate) for the high-frequency corner.
        Stage_T2x2h(c, 0, 1, 4, 5, 1);
        Stage_TOdd(c, 2, 3, 6, 7);
        Stage_TOdd(c, 8, 12, 9, 13);
        Stage_TOddOdd(c, 10, 11, 14, 15);

        // Coefficient permutation — interleaves DC, LP and HP into the
        // standard 4×4 DPCM-friendly order expected downstream.
        FwdPermute(c);
    }

    /// <summary>
    /// Inverse Core Transform — exact dual of <see cref="FCT4x4"/>.
    /// T.832 9.9.7.1 / Table 160.
    /// </summary>
    public static void ICT4x4(Span<int> c)
    {
        InvPermute(c);

        // ICT first stage matches FCT second stage in reverse: T2x2h(round=1)
        // is its own inverse with the same valRound, while InvTodd / InvToddodd
        // are the explicit inverses of TOdd / TOddOdd.
        Stage_T2x2h(c, 0, 1, 4, 5, 1);
        Stage_InvTodd(c, 2, 3, 6, 7);
        Stage_InvTodd(c, 8, 12, 9, 13);
        Stage_InvToddodd(c, 10, 11, 14, 15);

        // ICT second stage matches FCT first stage in reverse.
        Stage_T2x2h(c, 0, 3, 12, 15, 0);
        Stage_T2x2h(c, 5, 6, 9, 10, 0);
        Stage_T2x2h(c, 1, 2, 13, 14, 0);
        Stage_T2x2h(c, 4, 7, 8, 11, 0);
    }

    // -----------------------------------------------------------------------
    // Stage adapters: copy 4 picked indices into a length-4 local array,
    // run the elementary 2×2 transform on it, write the result back. This
    // matches the spec's "arrayLocal[] = { iCoeff[a], iCoeff[b], … }" idiom.
    // -----------------------------------------------------------------------

    private static void Stage_T2x2h(Span<int> c, int a, int b, int d, int e, int valRound)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        T2x2h(local, valRound);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_TOdd(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        TOdd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_InvTodd(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        InvTodd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_TOddOdd(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        TOddOdd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_InvToddodd(Span<int> c, int a, int b, int d, int e)
    {
        Span<int> local = [c[a], c[b], c[d], c[e]];
        InvToddodd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    // -----------------------------------------------------------------------
    // Elementary 2×2 operations (length-4 in-place)
    // -----------------------------------------------------------------------

    /// <summary>
    /// 2×2 Hadamard-like transform. T.832 9.9.7.2 / Table 161.
    /// Self-inverse under two successive applications with the same
    /// <paramref name="valRound"/> — applied once in the FCT pipeline and
    /// once in the ICT pipeline (with matching <paramref name="valRound"/>)
    /// produces the identity.
    /// </summary>
    public static void T2x2h(Span<int> ic, int valRound)
    {
        ic[0] += ic[3];
        ic[1] -= ic[2];
        var valT1 = (ic[0] - ic[1] + valRound) >> 1;
        var valT2 = ic[2];
        ic[2] = valT1 - ic[3];
        ic[3] = valT1 - valT2;
        ic[0] -= ic[3];
        ic[1] += ic[2];
    }

    /// <summary>Forward 1D rotate. T.832 D.4.2 / Table D.9.</summary>
    public static void TOdd(Span<int> ic)
    {
        ic[1] -= ic[2];
        ic[0] += ic[3];
        ic[2] += (ic[1] + 1) >> 1;
        ic[3] = ((ic[0] + 1) >> 1) - ic[3];
        ic[1] -= (3 * ic[0] + 4) >> 3;
        ic[0] += (3 * ic[1] + 4) >> 3;
        ic[3] -= (3 * ic[2] + 4) >> 3;
        ic[2] += (3 * ic[3] + 4) >> 3;
        ic[3] += ic[1] >> 1;
        ic[2] -= (ic[0] + 1) >> 1;
        ic[1] -= ic[3];
        ic[0] += ic[2];
    }

    /// <summary>Inverse 1D rotate. T.832 9.9.7.3 / Table 162.</summary>
    public static void InvTodd(Span<int> ic)
    {
        ic[1] += ic[3];
        ic[0] -= ic[2];
        ic[3] -= ic[1] >> 1;
        ic[2] += (ic[0] + 1) >> 1;
        ic[0] -= (3 * ic[1] + 4) >> 3;
        ic[1] += (3 * ic[0] + 4) >> 3;
        ic[2] -= (3 * ic[3] + 4) >> 3;
        // Spec line 9570 has a typo (missing close paren before newline). Reading the
        // matching forward operation in TOdd at the symmetric position, the intent is:
        //     iCoeff[3] += ((3 * iCoeff[2] + 4) >> 3)
        ic[3] += (3 * ic[2] + 4) >> 3;
        ic[2] -= (ic[1] + 1) >> 1;
        ic[3] = ((ic[0] + 1) >> 1) - ic[3];
        ic[1] += ic[2];
        ic[0] -= ic[3];
    }

    /// <summary>Forward 2D rotate. T.832 D.4.3 / Table D.10.</summary>
    public static void TOddOdd(Span<int> ic)
    {
        ic[1] = -ic[1];
        ic[2] = -ic[2];
        ic[3] += ic[0];
        ic[2] -= ic[1];
        var valT1 = ic[3] >> 1;
        ic[0] -= valT1;
        var valT2 = ic[2] >> 1;
        ic[1] += valT2;
        ic[0] += (ic[1] * 3 + 4) >> 3;
        ic[1] -= (ic[0] * 3 + 3) >> 2;
        ic[0] += (ic[1] * 3 + 3) >> 3;
        ic[1] -= valT2;
        ic[0] += valT1;
        ic[2] += ic[1];
        ic[3] -= ic[0];
    }

    /// <summary>Inverse 2D rotate. T.832 9.9.7.4 / Table 163.</summary>
    public static void InvToddodd(Span<int> ic)
    {
        ic[3] += ic[0];
        ic[2] -= ic[1];
        var valT1 = ic[3] >> 1;
        var valT2 = ic[2] >> 1;
        ic[0] -= valT1;
        ic[1] += valT2;
        ic[0] -= (ic[1] * 3 + 3) >> 3;
        ic[1] += (ic[0] * 3 + 3) >> 2;
        ic[0] -= (ic[1] * 3 + 4) >> 3;
        ic[1] -= valT2;
        ic[0] += valT1;
        ic[2] += ic[1];
        ic[3] -= ic[0];
        ic[1] = -ic[1];
        ic[2] = -ic[2];
    }

    // -----------------------------------------------------------------------
    // 16-element permutations (Tables D.11 / 164)
    // -----------------------------------------------------------------------

    private static ReadOnlySpan<byte> FwdPermArr =>
        [0, 8, 4, 6, 2, 10, 14, 12, 1, 11, 15, 13, 9, 3, 7, 5];

    private static ReadOnlySpan<byte> InvPermArr =>
        [0, 8, 4, 13, 2, 15, 3, 14, 1, 12, 5, 9, 7, 11, 6, 10];

    public static void FwdPermute(Span<int> a)
    {
        Span<int> tmp = stackalloc int[16];
        for (var i = 0; i < 16; i++) tmp[FwdPermArr[i]] = a[i];
        tmp.CopyTo(a);
    }

    public static void InvPermute(Span<int> a)
    {
        Span<int> tmp = stackalloc int[16];
        for (var i = 0; i < 16; i++) tmp[InvPermArr[i]] = a[i];
        tmp.CopyTo(a);
    }
}
