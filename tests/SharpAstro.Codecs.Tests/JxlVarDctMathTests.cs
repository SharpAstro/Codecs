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
}
