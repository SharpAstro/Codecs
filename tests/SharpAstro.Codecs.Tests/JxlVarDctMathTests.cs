using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// VarDCT (Rung 5) math-foundation tests: the separable DCT-II/III (<see cref="JxlDct"/>) and the
/// XYB opsin colour transform (<see cref="JxlXyb"/>). These are validated in isolation, before any
/// bitstream glue — the DCT against the naïve mathematical definition the JPEG XL spec pins down
/// (matching jxl-oxide's own reference tests), the colour transform by round-trip and by the
/// achromatic/black anchors the opsin model guarantees.
/// </summary>
public sealed class JxlVarDctMathTests
{
    private const double Sqrt2 = 1.4142135623730951;

    // Naïve ground-truth 1-D forward DCT (JPEG XL normalisation), computed in double precision.
    private static double[] NaiveForward(double[] input)
    {
        int s = input.Length;
        var outp = new double[s];
        for (int k = 0; k < s; k++)
        {
            double v = 0;
            for (int n = 0; n < s; n++)
                v += input[n] * Math.Cos(k * (2 * n + 1) * Math.PI / (2 * s));
            v /= s;
            if (k != 0) v *= Sqrt2;
            outp[k] = v;
        }
        return outp;
    }

    // Naïve ground-truth 1-D inverse DCT (synthesis), the transpose of NaiveForward.
    private static double[] NaiveInverse(double[] input)
    {
        int s = input.Length;
        var outp = new double[s];
        for (int k = 0; k < s; k++)
        {
            double v = input[0];
            for (int n = 1; n < s; n++)
                v += input[n] * Math.Cos(n * (2 * k + 1) * Math.PI / (2 * s)) * Sqrt2;
            outp[k] = v;
        }
        return outp;
    }

    private static float[] Random(int count, int seed)
    {
        var a = new float[count];
        uint state = (uint)seed | 1u;
        for (int i = 0; i < count; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            a[i] = (state / (float)uint.MaxValue) * 4f - 2f; // [-2, 2)
        }
        return a;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Dct1d_Forward_MatchesNaiveDefinition(int n)
    {
        float[] input = Random(n, n * 7 + 1);
        var io = (float[])input.Clone();
        JxlDct.Dct1d(io, new float[n], JxlDctDirection.Forward);

        double[] expected = NaiveForward(Array.ConvertAll(input, x => (double)x));
        for (int k = 0; k < n; k++)
            io[k].ShouldBe((float)expected[k], tolerance: 1e-4f);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Dct1d_Inverse_MatchesNaiveDefinition(int n)
    {
        float[] input = Random(n, n * 13 + 3);
        var io = (float[])input.Clone();
        JxlDct.Dct1d(io, new float[n], JxlDctDirection.Inverse);

        double[] expected = NaiveInverse(Array.ConvertAll(input, x => (double)x));
        for (int k = 0; k < n; k++)
            io[k].ShouldBe((float)expected[k], tolerance: 1e-4f);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Dct1d_RoundTrip_IsIdentity(int n)
    {
        float[] input = Random(n, n * 31 + 5);
        var io = (float[])input.Clone();
        JxlDct.Dct1d(io, new float[n], JxlDctDirection.Forward);
        JxlDct.Dct1d(io, new float[n], JxlDctDirection.Inverse);
        for (int i = 0; i < n; i++)
            io[i].ShouldBe(input[i], tolerance: 1e-4f);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(8, 16)]
    [InlineData(16, 8)]
    [InlineData(4, 4)]
    [InlineData(2, 32)]
    [InlineData(32, 2)]
    public void Dct2d_RoundTrip_IsIdentity(int w, int h)
    {
        float[] input = Random(w * h, w * 101 + h);
        var io = (float[])input.Clone();
        JxlDct.Dct2d(io, w, h, JxlDctDirection.Forward);
        JxlDct.Dct2d(io, w, h, JxlDctDirection.Inverse);
        for (int i = 0; i < w * h; i++)
            io[i].ShouldBe(input[i], tolerance: 2e-4f);
    }

    [Fact]
    public void Dct2d_ConstantBlock_HasOnlyDc()
    {
        const int w = 8, h = 8;
        const float value = 1.75f;
        var io = new float[w * h];
        Array.Fill(io, value);

        JxlDct.Dct2d(io, w, h, JxlDctDirection.Forward);

        io[0].ShouldBe(value, tolerance: 1e-5f); // DC = the block mean
        for (int i = 1; i < w * h; i++)
            io[i].ShouldBe(0f, tolerance: 1e-5f);
    }

    [Fact]
    public void Xyb_RoundTrip_LinearRgb_IsNearIdentity()
    {
        float[] rgb = Random(300, 9001);
        for (int i = 0; i < rgb.Length; i++)
            rgb[i] = Math.Abs(rgb[i]) / 2f; // [0, 1)

        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            (float x, float y, float b) = JxlXyb.LinearToXyb(rgb[i], rgb[i + 1], rgb[i + 2]);
            (float r, float g, float bb) = JxlXyb.XybToLinear(x, y, b);
            // The default opsin inverse matrix is F16-rounded, so the inverse of the published
            // forward matrix is only approximate — a few thousandths of error is expected.
            r.ShouldBe(rgb[i], tolerance: 3e-3f);
            g.ShouldBe(rgb[i + 1], tolerance: 3e-3f);
            bb.ShouldBe(rgb[i + 2], tolerance: 3e-3f);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Xyb_Achromatic_HasZeroX(float v)
    {
        // Equal RGB is grey: the L and M cone mixes are equal (both absorbance rows sum to 1),
        // so the red-green opponent channel X must vanish.
        (float x, _, _) = JxlXyb.LinearToXyb(v, v, v);
        x.ShouldBe(0f, tolerance: 1e-6f);
    }

    [Fact]
    public void Xyb_Black_IsOrigin()
    {
        (float x, float y, float b) = JxlXyb.LinearToXyb(0f, 0f, 0f);
        x.ShouldBe(0f, tolerance: 1e-6f);
        y.ShouldBe(0f, tolerance: 1e-6f);
        b.ShouldBe(0f, tolerance: 1e-6f);
    }

    [Fact]
    public void SrgbTransfer_RoundTrips()
    {
        for (int i = 0; i <= 255; i++)
        {
            float c = i / 255f;
            JxlXyb.LinearToSrgb(JxlXyb.SrgbToLinear(c)).ShouldBe(c, tolerance: 1e-5f);
        }
    }

    [Fact]
    public void DequantMatrices_BuildDefault_AllPositiveAndFinite()
    {
        var dq = JxlDequantMatrices.BuildDefault();
        foreach (JxlVarDctTransform t in Enum.GetValues<JxlVarDctTransform>())
            for (int c = 0; c < 3; c++)
            {
                float[] m = dq.Get(c, t);
                (int w, int h) = t.DequantMatrixSize();
                m.Length.ShouldBe(w * h);
                foreach (float v in m)
                {
                    v.ShouldBeGreaterThan(0f);
                    v.ShouldBeLessThan(1e8f);
                }
            }
    }

    [Fact]
    public void DequantMatrices_Dct8Luma_DcWeight_IsReciprocalOfFirstBand()
    {
        var dq = JxlDequantMatrices.BuildDefault();
        // The DC (radial distance 0) interpolates to the first band 3150; reciprocated on build.
        dq.Get(0, JxlVarDctTransform.Dct8)[0].ShouldBe(1f / 3150f, tolerance: 1e-9f);
    }

    [Fact]
    public void DequantMatrices_Hornuss_DcWeight_IsOne()
    {
        var dq = JxlDequantMatrices.BuildDefault();
        dq.Get(0, JxlVarDctTransform.Hornuss)[0].ShouldBe(1f, tolerance: 1e-6f);
    }

    [Fact]
    public void DequantMatrices_Transpose_IsValuePermutation()
    {
        var dq = JxlDequantMatrices.BuildDefault();
        foreach (JxlVarDctTransform t in new[]
                 { JxlVarDctTransform.Dct8, JxlVarDctTransform.Dct8x16, JxlVarDctTransform.Dct16x32 })
            for (int c = 0; c < 3; c++)
            {
                var raster = (float[])dq.Get(c, t).Clone();
                var transposed = (float[])dq.GetTransposed(c, t).Clone();
                Array.Sort(raster);
                Array.Sort(transposed);
                transposed.ShouldBe(raster); // transpose preserves the value multiset
            }
    }

    [Fact]
    public void Quantizer_Hf_RoundTrips_WithinOneStep()
    {
        const float weight = 1f / 560f; // a representative luma DCT weight
        const float scale = 0.5f;
        float step = weight * scale;
        for (float c = -2f; c <= 2f; c += 0.013f)
        {
            int q = JxlQuantizer.QuantizeHf(c, weight, scale);
            float d = JxlQuantizer.DequantizeHf(q, weight, scale, channel: 1);
            Math.Abs(d - c).ShouldBeLessThan(step + 1e-6f);
        }
    }

    [Fact]
    public void VarDct_Dct8Block_FullPipeline_RoundTripsWithinTolerance()
    {
        // An 8x8 RGB block taken all the way through the VarDCT math stack and back:
        //   sRGB → linear → XYB → DCT8 → quantize → dequantize → IDCT8 → XYB → linear → sRGB.
        // This is the lossy analogue of the lossless Modular round-trip — it proves the colour
        // transform, separable DCT, default dequant matrix and quantizer all compose correctly.
        const int n = 8;
        var dq = JxlDequantMatrices.BuildDefault();
        var quant = new JxlQuantizer(globalScale: 4096, quantLf: 32);
        float scale = quant.HfScale(hfMul: 32, qmScale: 1f); // 65536 / (4096*32) = 0.5

        // Smooth gradient content (the kind of low-frequency energy real images carry).
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++) srgb[c] = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                srgb[0][i] = Math.Clamp((40 + x * 18 + y * 6) / 255f, 0f, 1f);
                srgb[1][i] = Math.Clamp((90 + x * 8 + y * 14) / 255f, 0f, 1f);
                srgb[2][i] = Math.Clamp((150 - x * 5 + y * 9) / 255f, 0f, 1f);
            }

        // sRGB → linear → XYB planes (X, Y, B).
        var plane = new float[3][];
        for (int c = 0; c < 3; c++) plane[c] = new float[n * n];
        for (int i = 0; i < n * n; i++)
        {
            float lr = JxlXyb.SrgbToLinear(srgb[0][i]);
            float lg = JxlXyb.SrgbToLinear(srgb[1][i]);
            float lb = JxlXyb.SrgbToLinear(srgb[2][i]);
            (float xx, float yy, float bb) = JxlXyb.LinearToXyb(lr, lg, lb);
            plane[0][i] = xx; plane[1][i] = yy; plane[2][i] = bb;
        }

        // Forward DCT, quantize + dequantize each coefficient, inverse DCT.
        for (int c = 0; c < 3; c++)
        {
            JxlDct.Dct2d(plane[c], n, n, JxlDctDirection.Forward);
            float[] w = dq.Get(c, JxlVarDctTransform.Dct8);
            for (int i = 0; i < n * n; i++)
            {
                int q = JxlQuantizer.QuantizeHf(plane[c][i], w[i], scale);
                plane[c][i] = JxlQuantizer.DequantizeHf(q, w[i], scale, c);
            }
            JxlDct.Dct2d(plane[c], n, n, JxlDctDirection.Inverse);
        }

        // XYB → linear → sRGB and measure the reconstruction error.
        double sumSq = 0;
        float maxErr = 0;
        for (int i = 0; i < n * n; i++)
        {
            (float r, float g, float b) = JxlXyb.XybToLinear(plane[0][i], plane[1][i], plane[2][i]);
            float[] outSrgb =
            {
                Math.Clamp(JxlXyb.LinearToSrgb(r), 0f, 1f),
                Math.Clamp(JxlXyb.LinearToSrgb(g), 0f, 1f),
                Math.Clamp(JxlXyb.LinearToSrgb(b), 0f, 1f),
            };
            for (int c = 0; c < 3; c++)
            {
                float e = Math.Abs(outSrgb[c] - srgb[c][i]);
                maxErr = Math.Max(maxErr, e);
                sumSq += e * (double)e;
            }
        }
        double rmse = Math.Sqrt(sumSq / (3 * n * n));

        rmse.ShouldBeLessThan(0.02);
        maxErr.ShouldBeLessThan(0.08f);
    }

    [Theory]
    [InlineData(1, 1)]  // Dct8
    [InlineData(2, 2)]  // Dct16
    [InlineData(4, 4)]  // Dct32
    [InlineData(2, 1)]  // Dct8x16
    [InlineData(1, 2)]  // Dct16x8
    [InlineData(4, 1)]  // Dct8x32
    [InlineData(1, 4)]  // Dct32x8
    [InlineData(4, 2)]  // Dct16x32
    [InlineData(2, 4)]  // Dct32x16
    [InlineData(8, 8)]  // Dct64
    public void VarDctBlock_DctFamily_ForwardThenInverse_IsIdentity(int bw, int bh)
    {
        int width = bw * 8, height = bh * 8;
        float[] pixels = Random(width * height, bw * 911 + bh * 17 + 1);

        var block = (float[])pixels.Clone();
        float[] lf = JxlVarDctBlock.ForwardTransform(block, bw, bh); // block = full coeffs, lf = DC

        // Transmit HF (the block, with its LLF top-left) + the separate LF DC values, then rebuild:
        // overwrite the top-left bw×bh with the LF DC values and run the inverse.
        var recon = (float[])block.Clone();
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                recon[y * width + x] = lf[y * bw + x];
        JxlVarDctBlock.InverseTransform(recon, bw, bh);

        for (int i = 0; i < width * height; i++)
            recon[i].ShouldBe(pixels[i], tolerance: 3e-4f);
    }

    [Theory]
    [InlineData(0, 8, 8)]
    [InlineData(1, 8, 8)]
    [InlineData(2, 16, 16)]
    [InlineData(3, 32, 32)]
    [InlineData(4, 16, 8)]
    [InlineData(5, 32, 8)]
    [InlineData(6, 32, 16)]
    [InlineData(7, 64, 64)]
    [InlineData(8, 64, 32)]
    [InlineData(9, 128, 128)]
    [InlineData(10, 128, 64)]
    [InlineData(11, 256, 256)]
    [InlineData(12, 256, 128)]
    public void CoeffOrder_NaturalOrder_IsBijectionOntoGrid(int orderId, int w, int h)
    {
        (int X, int Y)[] order = JxlCoeffOrder.NaturalOrder(orderId);
        order.Length.ShouldBe(w * h);

        var seen = new HashSet<int>(w * h);
        foreach ((int x, int y) in order)
        {
            x.ShouldBeInRange(0, w - 1);
            y.ShouldBeInRange(0, h - 1);
            seen.Add(y * w + x).ShouldBeTrue(); // each position exactly once
        }
        seen.Count.ShouldBe(w * h);
    }

    [Fact]
    public void CoeffOrder_Dct8_StartsAtDc()
    {
        JxlCoeffOrder.NaturalOrder(0)[0].ShouldBe((0, 0)); // the DC coefficient leads the scan
    }

    [Fact]
    public void ChromaFromLuma_DefaultFactors_AreZeroAndOne()
    {
        (float kx, float kb) = JxlChromaFromLuma.LfFactors(
            JxlChromaFromLuma.DefaultFactorLf, JxlChromaFromLuma.DefaultFactorLf,
            JxlChromaFromLuma.DefaultColourFactor,
            JxlChromaFromLuma.DefaultBaseCorrelationX, JxlChromaFromLuma.DefaultBaseCorrelationB);
        kx.ShouldBe(0f);
        kb.ShouldBe(1f);
    }

    [Theory]
    [InlineData(128, 128)] // default
    [InlineData(100, 160)]
    [InlineData(200, 64)]
    public void ChromaFromLuma_DecorrelateThenRestore_IsIdentity(int xFactor, int bFactor)
    {
        float[] x = Random(64, xFactor * 3 + 1);
        float[] y = Random(64, bFactor * 5 + 7);
        float[] b = Random(64, xFactor + bFactor + 11);
        var x0 = (float[])x.Clone();
        var b0 = (float[])b.Clone();

        (float kx, float kb) = JxlChromaFromLuma.LfFactors(xFactor, bFactor, 84, 0f, 1f);
        JxlChromaFromLuma.Decorrelate(x, y, b, kx, kb);
        JxlChromaFromLuma.Restore(x, y, b, kx, kb);

        for (int i = 0; i < 64; i++)
        {
            x[i].ShouldBe(x0[i], tolerance: 1e-5f);
            b[i].ShouldBe(b0[i], tolerance: 1e-5f);
        }
    }
}
