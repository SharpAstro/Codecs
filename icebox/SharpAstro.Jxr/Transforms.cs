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
public static class Transforms
{
    /// <summary>
    /// Forward Core Transform on a 4×4 sub-block — ported from jxrlib's
    /// strDCT4x4Stage1 (strFwdTransform.c). Use for the FIRST stage of the
    /// cascaded PCT (per sub-block). For the super-block stage that
    /// transforms the 4×4 grid of sub-block DC values, use
    /// <see cref="FCT4x4Stage2"/> instead — jxrlib's second-stage algorithm
    /// uses different position orderings than the first stage.
    /// Task #12.
    /// </summary>
    public static void FCT4x4(Span<int> c)
    {
        StrDCT2x2dn(c, 0, 4, 8, 12);
        StrDCT2x2dn(c, 1, 5, 9, 13);
        StrDCT2x2dn(c, 2, 6, 10, 14);
        StrDCT2x2dn(c, 3, 7, 11, 15);
        StrDCT2x2up(c, 0, 1, 2, 3);
        FwdOddOdd(c, 15, 14, 13, 12);
        FwdOdd(c, 5, 4, 7, 6);
        FwdOdd(c, 10, 8, 11, 9);
        FwdPermute(c);
    }

    public static void ICT4x4(Span<int> c)
    {
        InvPermute(c);
        StrDCT2x2up(c, 0, 1, 2, 3);
        InvOdd(c, 5, 4, 7, 6);
        InvOdd(c, 10, 8, 11, 9);
        InvOddOdd(c, 15, 14, 13, 12);
        StrDCT2x2dn(c, 0, 4, 8, 12);
        StrDCT2x2dn(c, 1, 5, 9, 13);
        StrDCT2x2dn(c, 2, 6, 10, 14);
        StrDCT2x2dn(c, 3, 7, 11, 15);
    }

    /// <summary>
    /// Forward super-block PCT — ported from jxrlib's strDCT4x4SecondStage.
    /// Same primitives as <see cref="FCT4x4"/> but with different index
    /// orderings (the "corner" arrangement instead of "column" arrangement).
    /// Both decompositions implement a 4×4 PCT but produce different
    /// intermediate coefficients; only this one matches jxrlib's super-block
    /// output. Task #12.
    /// </summary>
    public static void FCT4x4Stage2(Span<int> c)
    {
        // FOURBUTTERFLY(0,192,48,240, 64,128,112,176, 16,208,32,224, 80,144,96,160)
        // translated to sequential 16-element indices (offset / 16):
        StrDCT2x2dn(c, 0, 12, 3, 15);
        StrDCT2x2dn(c, 4, 8, 7, 11);
        StrDCT2x2dn(c, 1, 13, 2, 14);
        StrDCT2x2dn(c, 5, 9, 6, 10);
        StrDCT2x2up(c, 0, 4, 1, 5);
        FwdOddOdd(c, 10, 14, 11, 15);
        FwdOdd(c, 8, 12, 9, 13);
        FwdOdd(c, 2, 3, 6, 7);
        // No FwdPermute — natural data order matches what jxrlib's super-block
        // decoder expects.
    }

    /// <summary>Inverse super-block PCT — jxrlib's strIDCT4x4Stage2.</summary>
    public static void ICT4x4Stage2(Span<int> c)
    {
        InvOdd(c, 2, 3, 6, 7);
        InvOdd(c, 8, 12, 9, 13);
        InvOddOdd(c, 10, 14, 11, 15);
        StrDCT2x2up(c, 0, 4, 1, 5);
        StrDCT2x2dn(c, 0, 12, 3, 15);
        StrDCT2x2dn(c, 4, 8, 7, 11);
        StrDCT2x2dn(c, 1, 13, 2, 14);
        StrDCT2x2dn(c, 5, 9, 6, 10);
    }

    // ------------------------------------------------------------------
    // jxrlib's 2x2 / odd primitives, ported verbatim from strTransform.c
    // and strFwdTransform.c / strInvTransform.c (4creators/jxrlib).
    // ------------------------------------------------------------------

    /// <summary>strDCT2x2dn — 2×2 butterfly (round-toward-zero variant).</summary>
    private static void StrDCT2x2dn(Span<int> p, int ia, int ib, int ic, int id)
    {
        int a = p[ia], b = p[ib], C = p[ic], d = p[id], t;
        a += d;
        b -= C;
        t = (a - b) >> 1;
        var c = t - d;
        d = t - C;
        a -= d;
        b += c;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }

    /// <summary>strDCT2x2up — same as dn but uses round-half-up via +1 in t.</summary>
    private static void StrDCT2x2up(Span<int> p, int ia, int ib, int ic, int id)
    {
        int a = p[ia], b = p[ib], C = p[ic], d = p[id], t;
        a += d;
        b -= C;
        t = (a - b + 1) >> 1;
        var c = t - d;
        d = t - C;
        a -= d;
        b += c;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }

    /// <summary>fwdOdd — Kron(Rotate(pi/8), [1 1; 1 -1]/sqrt(2)). [a b c d] => [D C A B].</summary>
    private static void FwdOdd(Span<int> p, int ia, int ib, int ic, int id)
    {
        int a = p[ia], b = p[ib], c = p[ic], d = p[id];
        // butterflies
        b -= c;
        a += d;
        c += (b + 1) >> 1;
        d = ((a + 1) >> 1) - d;
        // rotate pi/8 — ROTATE2 macro applied twice
        b -= (a * 3 + 4) >> 3;  a += (b * 3 + 4) >> 3;
        d -= (c * 3 + 4) >> 3;  c += (d * 3 + 4) >> 3;
        // butterflies
        d += b >> 1;
        c -= (a + 1) >> 1;
        b -= d;
        a += c;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }

    /// <summary>fwdOddOdd — Kron(Rotate(pi/8), Rotate(pi/8)) variant. Sign-flips b and c on entry.</summary>
    private static void FwdOddOdd(Span<int> p, int ia, int ib, int ic, int id)
    {
        int a = p[ia], b = -p[ib], c = -p[ic], d = p[id], t1, t2;
        // butterflies
        d += a;
        c -= b;
        a -= (t1 = d >> 1);
        b += (t2 = c >> 1);
        // rotate pi/4
        a += (b * 3 + 4) >> 3;
        b -= (a * 3 + 3) >> 2;
        a += (b * 3 + 3) >> 3;
        // butterflies
        b -= t2;
        a += t1;
        c += b;
        d -= a;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }

    /// <summary>invOdd — inverse of fwdOdd (rotate -pi/8).</summary>
    private static void InvOdd(Span<int> p, int ia, int ib, int ic, int id)
    {
        int a = p[ia], b = p[ib], c = p[ic], d = p[id];
        // butterflies
        b += d;
        a -= c;
        d -= b >> 1;
        c += (a + 1) >> 1;
        // rotate -pi/8 — IROTATE2: a -= (b*3+4)>>3; b += (a*3+4)>>3;
        a -= (b * 3 + 4) >> 3;  b += (a * 3 + 4) >> 3;
        c -= (d * 3 + 4) >> 3;  d += (c * 3 + 4) >> 3;
        // butterflies
        c -= (b + 1) >> 1;
        d = ((a + 1) >> 1) - d;
        b += c;
        a -= d;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }

    /// <summary>invOddOdd — inverse of fwdOddOdd. Sign-flips b and c on output.</summary>
    private static void InvOddOdd(Span<int> p, int ia, int ib, int ic, int id)
    {
        int a = p[ia], b = p[ib], c = p[ic], d = p[id], t1, t2;
        // butterflies
        d += a;
        c -= b;
        a -= (t1 = d >> 1);
        b += (t2 = c >> 1);
        // rotate -pi/4
        a -= (b * 3 + 3) >> 3;
        b += (a * 3 + 3) >> 2;
        a -= (b * 3 + 4) >> 3;
        // butterflies
        b -= t2;
        a += t1;
        c += b;
        d -= a;
        // sign flips
        p[ia] = a; p[ib] = -b; p[ic] = -c; p[id] = d;
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

    // -----------------------------------------------------------------------
    // int64 variants — exact duals of the int32 path above, only the working
    // type widens. Used for BD32F where the FCT cascade can amplify a 31-bit
    // input by ~6–8 bits, putting intermediate values past int32 range. The
    // integer FCT/ICT are bit-exact inverses in long arithmetic just as they
    // are in int — we just need wider headroom.
    // -----------------------------------------------------------------------

    public static void FCT4x4(Span<long> c)
    {
        // Mirror of the int FCT4x4 — jxrlib's primitives in long arithmetic.
        StrDCT2x2dn(c, 0, 4, 8, 12);
        StrDCT2x2dn(c, 1, 5, 9, 13);
        StrDCT2x2dn(c, 2, 6, 10, 14);
        StrDCT2x2dn(c, 3, 7, 11, 15);
        StrDCT2x2up(c, 0, 1, 2, 3);
        FwdOddOdd(c, 15, 14, 13, 12);
        FwdOdd(c, 5, 4, 7, 6);
        FwdOdd(c, 10, 8, 11, 9);
    }

    public static void ICT4x4(Span<long> c)
    {
        StrDCT2x2up(c, 0, 1, 2, 3);
        InvOdd(c, 5, 4, 7, 6);
        InvOdd(c, 10, 8, 11, 9);
        InvOddOdd(c, 15, 14, 13, 12);
        StrDCT2x2dn(c, 0, 4, 8, 12);
        StrDCT2x2dn(c, 1, 5, 9, 13);
        StrDCT2x2dn(c, 2, 6, 10, 14);
        StrDCT2x2dn(c, 3, 7, 11, 15);
    }

    private static void StrDCT2x2dn(Span<long> p, int ia, int ib, int ic, int id)
    {
        long a = p[ia], b = p[ib], C = p[ic], d = p[id], t;
        a += d; b -= C;
        t = (a - b) >> 1;
        var c = t - d; d = t - C; a -= d; b += c;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }
    private static void StrDCT2x2up(Span<long> p, int ia, int ib, int ic, int id)
    {
        long a = p[ia], b = p[ib], C = p[ic], d = p[id], t;
        a += d; b -= C;
        t = (a - b + 1) >> 1;
        var c = t - d; d = t - C; a -= d; b += c;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }
    private static void FwdOdd(Span<long> p, int ia, int ib, int ic, int id)
    {
        long a = p[ia], b = p[ib], c = p[ic], d = p[id];
        b -= c; a += d; c += (b + 1) >> 1; d = ((a + 1) >> 1) - d;
        b -= (a * 3 + 4) >> 3;  a += (b * 3 + 4) >> 3;
        d -= (c * 3 + 4) >> 3;  c += (d * 3 + 4) >> 3;
        d += b >> 1; c -= (a + 1) >> 1; b -= d; a += c;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }
    private static void FwdOddOdd(Span<long> p, int ia, int ib, int ic, int id)
    {
        long a = p[ia], b = -p[ib], c = -p[ic], d = p[id], t1, t2;
        d += a; c -= b; a -= (t1 = d >> 1); b += (t2 = c >> 1);
        a += (b * 3 + 4) >> 3; b -= (a * 3 + 3) >> 2; a += (b * 3 + 3) >> 3;
        b -= t2; a += t1; c += b; d -= a;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }
    private static void InvOdd(Span<long> p, int ia, int ib, int ic, int id)
    {
        long a = p[ia], b = p[ib], c = p[ic], d = p[id];
        b += d; a -= c; d -= b >> 1; c += (a + 1) >> 1;
        a -= (b * 3 + 4) >> 3;  b += (a * 3 + 4) >> 3;
        c -= (d * 3 + 4) >> 3;  d += (c * 3 + 4) >> 3;
        c -= (b + 1) >> 1; d = ((a + 1) >> 1) - d; b += c; a -= d;
        p[ia] = a; p[ib] = b; p[ic] = c; p[id] = d;
    }
    private static void InvOddOdd(Span<long> p, int ia, int ib, int ic, int id)
    {
        long a = p[ia], b = p[ib], c = p[ic], d = p[id], t1, t2;
        d += a; c -= b; a -= (t1 = d >> 1); b += (t2 = c >> 1);
        a -= (b * 3 + 3) >> 3; b += (a * 3 + 3) >> 2; a -= (b * 3 + 4) >> 3;
        b -= t2; a += t1; c += b; d -= a;
        p[ia] = a; p[ib] = -b; p[ic] = -c; p[id] = d;
    }

    private static void Stage_T2x2h(Span<long> c, int a, int b, int d, int e, int valRound)
    {
        Span<long> local = [c[a], c[b], c[d], c[e]];
        T2x2h(local, valRound);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_TOdd(Span<long> c, int a, int b, int d, int e)
    {
        Span<long> local = [c[a], c[b], c[d], c[e]];
        TOdd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_InvTodd(Span<long> c, int a, int b, int d, int e)
    {
        Span<long> local = [c[a], c[b], c[d], c[e]];
        InvTodd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_TOddOdd(Span<long> c, int a, int b, int d, int e)
    {
        Span<long> local = [c[a], c[b], c[d], c[e]];
        TOddOdd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    private static void Stage_InvToddodd(Span<long> c, int a, int b, int d, int e)
    {
        Span<long> local = [c[a], c[b], c[d], c[e]];
        InvToddodd(local);
        c[a] = local[0]; c[b] = local[1]; c[d] = local[2]; c[e] = local[3];
    }

    public static void T2x2h(Span<long> ic, int valRound)
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

    public static void TOdd(Span<long> ic)
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

    public static void InvTodd(Span<long> ic)
    {
        ic[1] += ic[3];
        ic[0] -= ic[2];
        ic[3] -= ic[1] >> 1;
        ic[2] += (ic[0] + 1) >> 1;
        ic[0] -= (3 * ic[1] + 4) >> 3;
        ic[1] += (3 * ic[0] + 4) >> 3;
        ic[2] -= (3 * ic[3] + 4) >> 3;
        ic[3] += (3 * ic[2] + 4) >> 3;
        ic[2] -= (ic[1] + 1) >> 1;
        ic[3] = ((ic[0] + 1) >> 1) - ic[3];
        ic[1] += ic[2];
        ic[0] -= ic[3];
    }

    public static void TOddOdd(Span<long> ic)
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

    public static void InvToddodd(Span<long> ic)
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

    public static void FwdPermute(Span<long> a)
    {
        Span<long> tmp = stackalloc long[16];
        for (var i = 0; i < 16; i++) tmp[FwdPermArr[i]] = a[i];
        tmp.CopyTo(a);
    }

    public static void InvPermute(Span<long> a)
    {
        Span<long> tmp = stackalloc long[16];
        for (var i = 0; i < 16; i++) tmp[InvPermArr[i]] = a[i];
        tmp.CopyTo(a);
    }
}
