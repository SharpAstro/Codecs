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
    public static void Idct8x8(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data)
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
                var p1 = (p2 + p3) * (int)(0.5411961f * 4096 + 0.5);
                var t2 = p1 + p3 * (int)(-1.847759065f * 4096 + 0.5);
                var t3 = p1 + p2 * (int)(0.765366865f * 4096 + 0.5);
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
                var p5 = (p3 + p4) * (int)(1.175875602f * 4096 + 0.5);
                t0 = t0 * (int)(0.298631336f * 4096 + 0.5);
                t1 = t1 * (int)(2.053119869f * 4096 + 0.5);
                t2 = t2 * (int)(3.072711026f * 4096 + 0.5);
                t3 = t3 * (int)(1.501321110f * 4096 + 0.5);
                p1 = p5 + p1 * (int)(-0.899976223f * 4096 + 0.5);
                p2 = p5 + p2 * (int)(-2.562915447f * 4096 + 0.5);
                p3 = p3 * (int)(-1.961570560f * 4096 + 0.5);
                p4 = p4 * (int)(-0.390180644f * 4096 + 0.5);
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
            var p1 = (p2 + p3) * (int)(0.5411961f * 4096 + 0.5);
            var t2 = p1 + p3 * (int)(-1.847759065f * 4096 + 0.5);
            var t3 = p1 + p2 * (int)(0.765366865f * 4096 + 0.5);
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
            var p5 = (p3 + p4) * (int)(1.175875602f * 4096 + 0.5);
            t0 = t0 * (int)(0.298631336f * 4096 + 0.5);
            t1 = t1 * (int)(2.053119869f * 4096 + 0.5);
            t2 = t2 * (int)(3.072711026f * 4096 + 0.5);
            t3 = t3 * (int)(1.501321110f * 4096 + 0.5);
            p1 = p5 + p1 * (int)(-0.899976223f * 4096 + 0.5);
            p2 = p5 + p2 * (int)(-2.562915447f * 4096 + 0.5);
            p3 = p3 * (int)(-1.961570560f * 4096 + 0.5);
            p4 = p4 * (int)(-0.390180644f * 4096 + 0.5);
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
