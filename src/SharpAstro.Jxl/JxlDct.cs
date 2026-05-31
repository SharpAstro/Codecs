namespace SharpAstro.Jxl;

internal enum JxlDctDirection
{
    /// <summary>Pixels → coefficients (analysis DCT-II, JPEG XL normalisation).</summary>
    Forward,

    /// <summary>Coefficients → pixels (synthesis DCT-III).</summary>
    Inverse,
}

/// <summary>
/// The separable floating-point DCT used by JPEG XL VarDCT (ISO/IEC 18181-1 §K), ported faithfully
/// from jxl-oxide's <c>jxl-render/src/vardct/generic/dct.rs</c>. This is the recursively-factored
/// DCT-II / DCT-III over power-of-two lengths (1, 2, 4, 8, 16, 32, …) with the exact JPEG XL
/// normalisation, which the reference pins down as:
///
/// <para>
/// Forward (analysis): <c>out[k] = (1/s)·Σₙ in[n]·cos(k(2n+1)π/2s)</c>, then <c>out[k] *= √2</c> for
/// <c>k ≠ 0</c>. Inverse (synthesis): <c>out[k] = in[0] + Σₙ₌₁ in[n]·cos(n(2k+1)π/2s)·√2</c>.
/// </para>
///
/// The 2-D transform is the separable product: a 1-D DCT along every row followed by a 1-D DCT down
/// every column. (jxl-oxide does the column pass via square-block transposition for cache locality;
/// the arithmetic per 1-D line is identical, so the straightforward separable form below is
/// value-equivalent — VarDCT validation is RMSE-level, not bit-exact against the decoder reference.)
/// </summary>
internal static class JxlDct
{
    private const float Sqrt2 = 1.41421356237309504880f;

    // sec_half(n)[k] = 1 / (2·cos((2k+1)π / 2n)), k = 0 … n/2-1 — the "secant-half" twiddle table
    // that drives the recursive split. Computed once per size and cached.
    private static readonly Dictionary<int, float[]> SecHalfCache = new();

    private static float[] SecHalf(int n)
    {
        lock (SecHalfCache)
        {
            if (SecHalfCache.TryGetValue(n, out float[]? cached))
                return cached;

            var table = new float[n / 2];
            for (int k = 0; k < table.Length; k++)
                table[k] = (float)(1.0 / Math.Cos((2 * k + 1) * Math.PI / (2 * n)) / 2.0);
            SecHalfCache[n] = table;
            return table;
        }
    }

    /// <summary>
    /// In-place 2-D DCT over a row-major <paramref name="data"/> grid of the given dimensions.
    /// Both dimensions must be 1 or a power of two. Equivalent to a 1-D <see cref="Dct1d"/> on every
    /// row, then on every column.
    /// </summary>
    public static void Dct2d(float[] data, int width, int height, JxlDctDirection direction)
    {
        var scratch = new float[Math.Max(width, height)];

        // Horizontal pass: a 1-D DCT along each row.
        for (int y = 0; y < height; y++)
            Dct1d(data.AsSpan(y * width, width), scratch.AsSpan(0, width), direction);

        // Vertical pass: a 1-D DCT down each column (gathered into a contiguous buffer first).
        if (height > 1)
        {
            var col = new float[height];
            var colScratch = new float[height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                    col[y] = data[y * width + x];
                Dct1d(col, colScratch, direction);
                for (int y = 0; y < height; y++)
                    data[y * width + x] = col[y];
            }
        }
    }

    /// <summary>
    /// In-place 1-D DCT of length <c>io.Length</c> (1 or a power of two). <paramref name="scratch"/>
    /// must be the same length as <paramref name="io"/>.
    /// </summary>
    public static void Dct1d(Span<float> io, Span<float> scratch, JxlDctDirection direction)
    {
        int n = io.Length;
        if (n <= 1)
            return;

        if (n == 2)
        {
            float t0 = io[0] + io[1];
            float t1 = io[0] - io[1];
            if (direction == JxlDctDirection.Forward)
            {
                io[0] = t0 / 2f;
                io[1] = t1 / 2f;
            }
            else
            {
                io[0] = t0;
                io[1] = t1;
            }
            return;
        }

        if (n == 4)
        {
            Dct4(io, direction);
            return;
        }

        if (n == 8)
        {
            Dct8(io, direction);
            return;
        }

        // General power-of-two recursion. The two halves of `scratch` hold the even/odd split;
        // the two halves of `io` serve as the recursion's scratch (roles swap each level).
        int h = n / 2;
        Span<float> in0 = scratch[..h];
        Span<float> in1 = scratch[h..];
        Span<float> out0 = io[..h];
        Span<float> out1 = io[h..];
        float[] sec = SecHalf(n);

        if (direction == JxlDctDirection.Forward)
        {
            for (int i = 0; i < h; i++)
            {
                in0[i] = (io[i] + io[n - i - 1]) / 2f;
                in1[i] = (io[i] - io[n - i - 1]) / 2f;
            }
            for (int i = 0; i < h; i++)
                in1[i] *= sec[i];

            Dct1d(in0, out0, JxlDctDirection.Forward);
            Dct1d(in1, out1, JxlDctDirection.Forward);

            in1[0] *= Sqrt2;
            for (int i = 0; i < h - 1; i++)
                in1[i] += in1[i + 1];

            for (int i = 0; i < h; i++)
            {
                io[i * 2] = in0[i];
                io[i * 2 + 1] = in1[i];
            }
        }
        else
        {
            for (int i = 0; i < h; i++)
            {
                in0[i] = io[i * 2];
                in1[i] = io[i * 2 + 1];
            }
            for (int i = 1; i < h; i++)
                in1[h - i] += in1[h - i - 1];
            in1[0] *= Sqrt2;

            Dct1d(in0, out0, JxlDctDirection.Inverse);
            Dct1d(in1, out1, JxlDctDirection.Inverse);

            for (int i = 0; i < h; i++)
                in1[i] *= sec[i];

            for (int i = 0; i < h; i++)
            {
                out0[i] = scratch[i] + scratch[i + h];
                out1[h - i - 1] = scratch[i] - scratch[i + h];
            }
        }
    }

    private static void Dct4(Span<float> io, JxlDctDirection direction)
    {
        const float sec0 = 0.5411961f;
        const float sec1 = 1.306563f;

        if (direction == JxlDctDirection.Forward)
        {
            float sum03 = io[0] + io[3];
            float sum12 = io[1] + io[2];
            float tmp0 = (io[0] - io[3]) * sec0;
            float tmp1 = (io[1] - io[2]) * sec1;
            float o0 = (tmp0 + tmp1) / 4f;
            float o1 = (tmp0 - tmp1) / 4f;

            io[0] = (sum03 + sum12) / 4f;
            io[1] = o0 * Sqrt2 + o1;
            io[2] = (sum03 - sum12) / 4f;
            io[3] = o1;
        }
        else
        {
            float tmp0 = io[1] * Sqrt2;
            float tmp1 = io[1] + io[3];
            float o0 = (tmp0 + tmp1) * sec0;
            float o1 = (tmp0 - tmp1) * sec1;
            float sum02 = io[0] + io[2];
            float sub02 = io[0] - io[2];

            io[0] = sum02 + o0;
            io[1] = sub02 + o1;
            io[2] = sub02 - o1;
            io[3] = sum02 - o0;
        }
    }

    private static void Dct8(Span<float> io, JxlDctDirection direction)
    {
        float[] sec = SecHalf(8);

        if (direction == JxlDctDirection.Forward)
        {
            Span<float> input0 = stackalloc float[4]
            {
                (io[0] + io[7]) / 2f,
                (io[1] + io[6]) / 2f,
                (io[2] + io[5]) / 2f,
                (io[3] + io[4]) / 2f,
            };
            Span<float> input1 = stackalloc float[4]
            {
                (io[0] - io[7]) * sec[0] / 2f,
                (io[1] - io[6]) * sec[1] / 2f,
                (io[2] - io[5]) * sec[2] / 2f,
                (io[3] - io[4]) * sec[3] / 2f,
            };
            Dct4(input0, JxlDctDirection.Forward);
            for (int i = 0; i < 4; i++)
                io[i * 2] = input0[i];

            Dct4(input1, JxlDctDirection.Forward);
            input1[0] *= Sqrt2;
            for (int i = 0; i < 3; i++)
                io[i * 2 + 1] = input1[i] + input1[i + 1];
            io[7] = input1[3];
        }
        else
        {
            Span<float> input0 = stackalloc float[4] { io[0], io[2], io[4], io[6] };
            Span<float> input1 = stackalloc float[4]
            {
                io[1] * Sqrt2,
                io[3] + io[1],
                io[5] + io[3],
                io[7] + io[5],
            };
            Dct4(input0, JxlDctDirection.Inverse);
            Dct4(input1, JxlDctDirection.Inverse);
            for (int i = 0; i < 4; i++)
            {
                float r = input1[i] * sec[i];
                io[i] = input0[i] + r;
                io[7 - i] = input0[i] - r;
            }
        }
    }
}
