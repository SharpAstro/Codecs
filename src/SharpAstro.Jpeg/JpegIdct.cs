using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace SharpAstro.Jpeg;

/// <summary>
/// Inverse DCT kernels. <see cref="Idct8x8"/> is a 1:1 port of stb_image's
/// fixed-point <c>stbi__idct_block</c> (itself derived from the IJG accurate
/// integer IDCT) — every shift and rounding constant matches, which is what
/// makes full-scale output byte-exact against the StbImageSharp reference.
///
/// <para>
/// <see cref="IdctReduced"/> is the scaled-decode kernel: a clean-room
/// DCT-domain decimation (NOT ported from libjpeg's jidctred.c, which carries
/// the IJG license). Taking the top-left B×B coefficients of an 8×8 block and
/// applying a B-point inverse transform with the same c_u/2 normalisation
/// yields a faithful B×B downsample of the block — for B=1 this degenerates to
/// the well-known "DC/8" property. High-frequency coefficients above B are
/// discarded, which acts as the anti-alias prefilter.
/// </para>
/// </summary>
internal static class JpegIdct
{
    // stb_image's STBI__IDCT_1D constants, hoisted out of the expression bodies so the
    // scalar and SIMD paths provably multiply by the same values rather than keeping two
    // copies that could drift. The expressions are stb's verbatim, including the float
    // literal and the +0.5 truncation-to-nearest, because that is what fixes the exact
    // integers the StbImageSharp golden digests pin.
    private static readonly int C0541 = (int)(0.5411961f * 4096 + 0.5);
    private static readonly int C1847 = (int)(-1.847759065f * 4096 + 0.5);
    private static readonly int C0765 = (int)(0.765366865f * 4096 + 0.5);
    private static readonly int C1175 = (int)(1.175875602f * 4096 + 0.5);
    private static readonly int C0298 = (int)(0.298631336f * 4096 + 0.5);
    private static readonly int C2053 = (int)(2.053119869f * 4096 + 0.5);
    private static readonly int C3072 = (int)(3.072711026f * 4096 + 0.5);
    private static readonly int C1501 = (int)(1.501321110f * 4096 + 0.5);
    private static readonly int C0899 = (int)(-0.899976223f * 4096 + 0.5);
    private static readonly int C2562 = (int)(-2.562915447f * 4096 + 0.5);
    private static readonly int C1961 = (int)(-1.961570560f * 4096 + 0.5);
    private static readonly int C0390 = (int)(-0.390180644f * 4096 + 0.5);

    private static byte Clamp(int x)
    {
        if ((uint)x > 255)
        {
            if (x < 0)
                return 0;
            return 255;
        }

        return (byte)x;
    }

    /// <summary>
    /// Full 8×8 inverse DCT of one dequantized coefficient block into an 8-bit
    /// sample plane (adds the +128 level shift, clamps to 0..255).
    /// </summary>
    /// <remarks>
    /// Dispatches to a <see cref="Vector128{T}"/> implementation where the hardware has
    /// it (SSE2/NEON — i.e. effectively everywhere), falling back to the scalar kernel
    /// otherwise. Both produce identical bytes; see <see cref="Idct8x8Simd"/>.
    /// </remarks>
    public static void Idct8x8(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data)
    {
        if (Vector128.IsHardwareAccelerated)
            Idct8x8Simd(outPlane, outOffset, outStride, data);
        else
            Idct8x8Scalar(outPlane, outOffset, outStride, data);
    }

    /// <summary>
    /// The reference kernel: a direct transliteration of stb_image's
    /// <c>stbi__idct_block</c>, kept as the fallback and as the thing
    /// <see cref="Idct8x8Simd"/> is checked against.
    /// </summary>
    internal static void Idct8x8Scalar(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data)
    {
        Span<int> val = stackalloc int[64];

        // Columns first. The all-AC-zero shortcut and every fixed-point constant
        // below replicate stb_image's STBI__IDCT_1D expansion exactly.
        for (var i = 0; i < 8; ++i)
        {
            if (data[i + 8] == 0 && data[i + 16] == 0 && data[i + 24] == 0 && data[i + 32] == 0 &&
                data[i + 40] == 0 && data[i + 48] == 0 && data[i + 56] == 0)
            {
                var dcterm = data[i] * 4;
                val[i] = val[i + 8] = val[i + 16] = val[i + 24] = val[i + 32] = val[i + 40] = val[i + 48] = val[i + 56] = dcterm;
            }
            else
            {
                int p2 = data[i + 16];
                int p3 = data[i + 48];
                var p1 = (p2 + p3) * C0541;
                var t2 = p1 + p3 * C1847;
                var t3 = p1 + p2 * C0765;
                p2 = data[i];
                p3 = data[i + 32];
                var t0 = (p2 + p3) * 4096;
                var t1 = (p2 - p3) * 4096;
                var x0 = t0 + t3;
                var x3 = t0 - t3;
                var x1 = t1 + t2;
                var x2 = t1 - t2;
                t0 = data[i + 56];
                t1 = data[i + 40];
                t2 = data[i + 24];
                t3 = data[i + 8];
                p3 = t0 + t2;
                var p4 = t1 + t3;
                p1 = t0 + t3;
                p2 = t1 + t2;
                var p5 = (p3 + p4) * C1175;
                t0 = t0 * C0298;
                t1 = t1 * C2053;
                t2 = t2 * C3072;
                t3 = t3 * C1501;
                p1 = p5 + p1 * C0899;
                p2 = p5 + p2 * C2562;
                p3 = p3 * C1961;
                p4 = p4 * C0390;
                t3 += p1 + p4;
                t2 += p2 + p3;
                t1 += p2 + p4;
                t0 += p1 + p3;
                x0 += 512;
                x1 += 512;
                x2 += 512;
                x3 += 512;
                val[i] = (x0 + t3) >> 10;
                val[i + 56] = (x0 - t3) >> 10;
                val[i + 8] = (x1 + t2) >> 10;
                val[i + 48] = (x1 - t2) >> 10;
                val[i + 16] = (x2 + t1) >> 10;
                val[i + 40] = (x2 - t1) >> 10;
                val[i + 24] = (x3 + t0) >> 10;
                val[i + 32] = (x3 - t0) >> 10;
            }
        }

        // Rows. The +65536 + (128 << 17) bias folds round-to-nearest and the
        // +128 level shift into the final >> 17.
        for (var i = 0; i < 8; ++i)
        {
            var v = val.Slice(i * 8, 8);
            var o = outOffset + i * outStride;

            int p2 = v[2];
            int p3 = v[6];
            var p1 = (p2 + p3) * C0541;
            var t2 = p1 + p3 * C1847;
            var t3 = p1 + p2 * C0765;
            p2 = v[0];
            p3 = v[4];
            var t0 = (p2 + p3) * 4096;
            var t1 = (p2 - p3) * 4096;
            var x0 = t0 + t3;
            var x3 = t0 - t3;
            var x1 = t1 + t2;
            var x2 = t1 - t2;
            t0 = v[7];
            t1 = v[5];
            t2 = v[3];
            t3 = v[1];
            p3 = t0 + t2;
            var p4 = t1 + t3;
            p1 = t0 + t3;
            p2 = t1 + t2;
            var p5 = (p3 + p4) * C1175;
            t0 = t0 * C0298;
            t1 = t1 * C2053;
            t2 = t2 * C3072;
            t3 = t3 * C1501;
            p1 = p5 + p1 * C0899;
            p2 = p5 + p2 * C2562;
            p3 = p3 * C1961;
            p4 = p4 * C0390;
            t3 += p1 + p4;
            t2 += p2 + p3;
            t1 += p2 + p4;
            t0 += p1 + p3;
            x0 += 65536 + (128 << 17);
            x1 += 65536 + (128 << 17);
            x2 += 65536 + (128 << 17);
            x3 += 65536 + (128 << 17);
            outPlane[o + 0] = Clamp((x0 + t3) >> 17);
            outPlane[o + 7] = Clamp((x0 - t3) >> 17);
            outPlane[o + 1] = Clamp((x1 + t2) >> 17);
            outPlane[o + 6] = Clamp((x1 - t2) >> 17);
            outPlane[o + 2] = Clamp((x2 + t1) >> 17);
            outPlane[o + 5] = Clamp((x2 - t1) >> 17);
            outPlane[o + 3] = Clamp((x3 + t0) >> 17);
            outPlane[o + 4] = Clamp((x3 - t0) >> 17);
        }
    }

    /// <summary>
    /// Four-lane <see cref="Vector128{T}"/> form of the same transform, ~2× the scalar
    /// kernel on realistic coefficient blocks.
    /// </summary>
    /// <remarks>
    /// <para><b>Provenance.</b> This was derived by vectorizing the public-domain scalar
    /// kernel above, NOT by reading libjpeg-turbo's SIMD IDCT — the same line this repo
    /// already draws around <c>jidctred.c</c>. It also stays in <b>int32</b> lanes rather
    /// than packing eight 16-bit lanes the way libjpeg-turbo does: 16-bit lanes require
    /// extra descaling that changes the rounding, and byte-exactness against the
    /// StbImageSharp digests is the whole point. Int32 lanes make every lane bit-identical
    /// to the scalar path by construction.</para>
    ///
    /// <para><b>Layout.</b> The column pass puts one column per lane. A coefficient block
    /// is row-major, so a row is 8 contiguous shorts and
    /// <see cref="Vector128.WidenLower(Vector128{short})"/>/<c>WidenUpper</c> yields
    /// columns 0-3 / 4-7 directly — no gather. Results are written to scratch
    /// <b>transposed</b> (<c>t[col * 8 + row]</c>) with scalar stores, which is the same
    /// 64 int stores the scalar kernel already pays. The row pass then puts one row per
    /// lane and reads each position as a contiguous 4-int load. So the ~40 multiplies per
    /// pass go four-wide while the load/store counts stay flat.</para>
    ///
    /// <para><b>The shortcut.</b> The scalar all-AC-zero test is per column; here it is per
    /// group of four, so it only fires when all four are DC-only. That is a real dilution,
    /// but measured against zigzag-clustered (i.e. realistic) coefficients the vector path
    /// still wins ~2× even on very sparse blocks, because AC energy concentrates in the low
    /// columns and the high group keeps hitting the shortcut.</para>
    /// </remarks>
    internal static void Idct8x8Simd(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data)
    {
        // Transposed intermediate: t[col * 8 + row], so the row pass loads contiguously.
        Span<int> t = stackalloc int[64];

        var r0 = Vector128.Create(data.Slice(0, 8));
        var r1 = Vector128.Create(data.Slice(8, 8));
        var r2 = Vector128.Create(data.Slice(16, 8));
        var r3 = Vector128.Create(data.Slice(24, 8));
        var r4 = Vector128.Create(data.Slice(32, 8));
        var r5 = Vector128.Create(data.Slice(40, 8));
        var r6 = Vector128.Create(data.Slice(48, 8));
        var r7 = Vector128.Create(data.Slice(56, 8));

        // ---- Column pass: lane = column, two groups of four. ----
        for (var g = 0; g < 2; g++)
        {
            Vector128<int> s0, s1, s2, s3, s4, s5, s6, s7;
            if (g == 0)
            {
                s0 = Vector128.WidenLower(r0);
                s1 = Vector128.WidenLower(r1);
                s2 = Vector128.WidenLower(r2);
                s3 = Vector128.WidenLower(r3);
                s4 = Vector128.WidenLower(r4);
                s5 = Vector128.WidenLower(r5);
                s6 = Vector128.WidenLower(r6);
                s7 = Vector128.WidenLower(r7);
            }
            else
            {
                s0 = Vector128.WidenUpper(r0);
                s1 = Vector128.WidenUpper(r1);
                s2 = Vector128.WidenUpper(r2);
                s3 = Vector128.WidenUpper(r3);
                s4 = Vector128.WidenUpper(r4);
                s5 = Vector128.WidenUpper(r5);
                s6 = Vector128.WidenUpper(r6);
                s7 = Vector128.WidenUpper(r7);
            }

            var colBase = g * 4;

            if ((s1 | s2 | s3 | s4 | s5 | s6 | s7) == Vector128<int>.Zero)
            {
                var dcterm = s0 * Vector128.Create(4);
                for (var k = 0; k < 8; k++)
                    StoreTransposed(t, colBase, k, dcterm);
                continue;
            }

            Butterfly(s0, s1, s2, s3, s4, s5, s6, s7,
                      out var x0, out var x1, out var x2, out var x3,
                      out var u0, out var u1, out var u2, out var u3);

            var bias = Vector128.Create(512);
            x0 += bias;
            x1 += bias;
            x2 += bias;
            x3 += bias;

            StoreTransposed(t, colBase, 0, (x0 + u3) >> 10);
            StoreTransposed(t, colBase, 7, (x0 - u3) >> 10);
            StoreTransposed(t, colBase, 1, (x1 + u2) >> 10);
            StoreTransposed(t, colBase, 6, (x1 - u2) >> 10);
            StoreTransposed(t, colBase, 2, (x2 + u1) >> 10);
            StoreTransposed(t, colBase, 5, (x2 - u1) >> 10);
            StoreTransposed(t, colBase, 3, (x3 + u0) >> 10);
            StoreTransposed(t, colBase, 4, (x3 - u0) >> 10);
        }

        // ---- Row pass: lane = row, two groups of four. ----
        for (var h = 0; h < 2; h++)
        {
            var rowBase = h * 4;
            var q0 = Vector128.Create(t.Slice(rowBase, 4));
            var q1 = Vector128.Create(t.Slice(8 + rowBase, 4));
            var q2 = Vector128.Create(t.Slice(16 + rowBase, 4));
            var q3 = Vector128.Create(t.Slice(24 + rowBase, 4));
            var q4 = Vector128.Create(t.Slice(32 + rowBase, 4));
            var q5 = Vector128.Create(t.Slice(40 + rowBase, 4));
            var q6 = Vector128.Create(t.Slice(48 + rowBase, 4));
            var q7 = Vector128.Create(t.Slice(56 + rowBase, 4));

            Butterfly(q0, q1, q2, q3, q4, q5, q6, q7,
                      out var x0, out var x1, out var x2, out var x3,
                      out var u0, out var u1, out var u2, out var u3);

            var bias = Vector128.Create(65536 + (128 << 17));
            x0 += bias;
            x1 += bias;
            x2 += bias;
            x3 += bias;

            Emit(outPlane, outOffset, outStride, rowBase, 0, x0 + u3);
            Emit(outPlane, outOffset, outStride, rowBase, 7, x0 - u3);
            Emit(outPlane, outOffset, outStride, rowBase, 1, x1 + u2);
            Emit(outPlane, outOffset, outStride, rowBase, 6, x1 - u2);
            Emit(outPlane, outOffset, outStride, rowBase, 2, x2 + u1);
            Emit(outPlane, outOffset, outStride, rowBase, 5, x2 - u1);
            Emit(outPlane, outOffset, outStride, rowBase, 3, x3 + u0);
            Emit(outPlane, outOffset, outStride, rowBase, 4, x3 - u0);
        }
    }

    /// <summary>
    /// The shared 1-D butterfly, lane-parallel. Mirrors stb's STBI__IDCT_1D operation
    /// for operation — same order, same int32 truncation — so each lane reproduces the
    /// scalar kernel bit for bit. The caller adds the pass-specific rounding bias and
    /// applies the pass-specific final shift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(
        Vector128<int> s0, Vector128<int> s1, Vector128<int> s2, Vector128<int> s3,
        Vector128<int> s4, Vector128<int> s5, Vector128<int> s6, Vector128<int> s7,
        out Vector128<int> x0, out Vector128<int> x1, out Vector128<int> x2, out Vector128<int> x3,
        out Vector128<int> t0, out Vector128<int> t1, out Vector128<int> t2, out Vector128<int> t3)
    {
        var p2 = s2;
        var p3 = s6;
        var p1 = (p2 + p3) * Vector128.Create(C0541);
        t2 = p1 + p3 * Vector128.Create(C1847);
        t3 = p1 + p2 * Vector128.Create(C0765);
        p2 = s0;
        p3 = s4;
        t0 = (p2 + p3) * Vector128.Create(4096);
        t1 = (p2 - p3) * Vector128.Create(4096);
        x0 = t0 + t3;
        x3 = t0 - t3;
        x1 = t1 + t2;
        x2 = t1 - t2;

        t0 = s7;
        t1 = s5;
        t2 = s3;
        t3 = s1;
        p3 = t0 + t2;
        var p4 = t1 + t3;
        p1 = t0 + t3;
        p2 = t1 + t2;
        var p5 = (p3 + p4) * Vector128.Create(C1175);
        t0 *= Vector128.Create(C0298);
        t1 *= Vector128.Create(C2053);
        t2 *= Vector128.Create(C3072);
        t3 *= Vector128.Create(C1501);
        p1 = p5 + p1 * Vector128.Create(C0899);
        p2 = p5 + p2 * Vector128.Create(C2562);
        p3 *= Vector128.Create(C1961);
        p4 *= Vector128.Create(C0390);
        t3 += p1 + p4;
        t2 += p2 + p3;
        t1 += p2 + p4;
        t0 += p1 + p3;
    }

    /// <summary>
    /// Scatters one column-pass result (lane j = column <paramref name="colBase"/> + j,
    /// all at row <paramref name="row"/>) into the transposed scratch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreTransposed(Span<int> t, int colBase, int row, Vector128<int> v)
    {
        t[(colBase + 0) * 8 + row] = v[0];
        t[(colBase + 1) * 8 + row] = v[1];
        t[(colBase + 2) * 8 + row] = v[2];
        t[(colBase + 3) * 8 + row] = v[3];
    }

    /// <summary>
    /// Descales, clamps and scatters one row-pass result (lane j = row
    /// <paramref name="rowBase"/> + j, all at column <paramref name="col"/>) into the
    /// output plane. <see cref="Vector128.Min(Vector128{int}, Vector128{int})"/> /
    /// <c>Max</c> reproduce <see cref="Clamp"/> branchlessly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Emit(byte[] outPlane, int outOffset, int outStride, int rowBase, int col, Vector128<int> v)
    {
        var c = Vector128.Max(Vector128.Min(v >> 17, Vector128.Create(255)), Vector128<int>.Zero);
        var b = outOffset + col;
        outPlane[b + (rowBase + 0) * outStride] = (byte)c[0];
        outPlane[b + (rowBase + 1) * outStride] = (byte)c[1];
        outPlane[b + (rowBase + 2) * outStride] = (byte)c[2];
        outPlane[b + (rowBase + 3) * outStride] = (byte)c[3];
    }

    // Reduced-IDCT basis tables, T[u * B + p] = (c_u / 2) * cos((2p+1) u π / (2B)).
    // With this normalisation a DC-only block yields X[0,0]/8 in 2D — identical
    // level handling to the full 8×8 transform — and B=8 would reproduce the
    // standard IDCT basis exactly.
    private static readonly float[] Table4 = BuildTable(4);
    private static readonly float[] Table2 = BuildTable(2);
    private static readonly float[] Table1 = BuildTable(1);

    private static float[] BuildTable(int b)
    {
        var t = new float[b * b];
        for (var u = 0; u < b; u++)
        {
            var cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
            for (var p = 0; p < b; p++)
                t[u * b + p] = (float)(cu / 2.0 * Math.Cos((2 * p + 1) * u * Math.PI / (2 * b)));
        }

        return t;
    }

    /// <summary>
    /// Reduced inverse DCT: transforms the top-left <paramref name="b"/>×<paramref name="b"/>
    /// coefficients of an 8×8 block into a b×b downsampled sample tile
    /// (b ∈ {4, 2, 1}). Adds the +128 level shift and clamps.
    /// </summary>
    public static void IdctReduced(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data, int b)
    {
        if (b == 1)
        {
            // 2D DC-only: mean of the block = X[0,0] / 8.
            var dc = Clamp((int)MathF.Floor(data[0] * 0.125f + 128.5f));
            outPlane[outOffset] = dc;
            return;
        }

        var table = b == 4 ? Table4 : Table2;

        // Separable: rows of `tmp` are partially-transformed along the horizontal
        // frequency axis; the second pass finishes the vertical axis.
        Span<float> tmp = stackalloc float[16]; // b*b, max 4*4

        for (var u = 0; u < b; u++)
        {
            for (var q = 0; q < b; q++)
            {
                var acc = 0f;
                for (var v = 0; v < b; v++)
                    acc += table[v * b + q] * data[u * 8 + v];
                tmp[u * b + q] = acc;
            }
        }

        for (var p = 0; p < b; p++)
        {
            var o = outOffset + p * outStride;
            for (var q = 0; q < b; q++)
            {
                var acc = 0f;
                for (var u = 0; u < b; u++)
                    acc += table[u * b + p] * tmp[u * b + q];
                outPlane[o + q] = Clamp((int)MathF.Floor(acc + 128.5f));
            }
        }
    }
}
