using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// VarDCT 5m (payload) — the full-image DCT8 pixel pipeline JxlVarDctImage: sRGB → XYB → per-block
/// DCT → quantize (LF DC + HF AC) → chroma-from-luma, and the exact inverse. This is the lossy
/// analogue of the lossless Modular round-trip and the multi-block extension of the single-block
/// math capstone; it validates that the LF/HF split + CfL + dequant matrices compose over a whole
/// image at low RMSE before the data is wrapped in the LfGroup/PassGroup bitstream.
/// </summary>
public sealed class JxlVarDctImageTests
{
    private static float[][] Gradient(int width, int height)
    {
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
            srgb[c] = new float[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                srgb[0][i] = Math.Clamp((30 + x * 3 + y * 2) / 255f, 0f, 1f);
                srgb[1][i] = Math.Clamp((80 + x * 2 + y * 3) / 255f, 0f, 1f);
                srgb[2][i] = Math.Clamp((160 - x + y * 2) / 255f, 0f, 1f);
            }
        return srgb;
    }

    private static (double Rmse, float Max) Error(float[][] a, float[][] b, int n)
    {
        double sumSq = 0;
        float max = 0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < n; i++)
            {
                float e = Math.Abs(a[c][i] - b[c][i]);
                max = Math.Max(max, e);
                sumSq += e * (double)e;
            }
        return (Math.Sqrt(sumSq / (3 * n)), max);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 24)]
    [InlineData(8, 8)]
    public void Image_Gradient_RoundTripsWithinTolerance(int width, int height)
    {
        float[][] srgb = Gradient(width, height);

        JxlVarDctImage.Encoded enc = JxlVarDctImage.Encode(
            srgb, width, height, globalScale: 4096, quantLf: 32, hfMul: 32, extraPrecision: 0);
        float[][] recon = JxlVarDctImage.Decode(enc);

        (double rmse, float max) = Error(srgb, recon, width * height);
        rmse.ShouldBeLessThan(0.02);
        max.ShouldBeLessThan(0.1f);
    }

    [Fact]
    public void Image_FlatSolid_NearLossless()
    {
        // A flat colour has only DC energy; the AC coefficients are ~0 and the DC quantizes tightly,
        // so the reconstruction should be very close.
        const int w = 24, h = 16;
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            srgb[c] = new float[w * h];
            float v = c switch { 0 => 0.45f, 1 => 0.60f, _ => 0.30f };
            Array.Fill(srgb[c], v);
        }

        JxlVarDctImage.Encoded enc = JxlVarDctImage.Encode(srgb, w, h, 4096, 32, 32, 0);
        float[][] recon = JxlVarDctImage.Decode(enc);

        (double rmse, float max) = Error(srgb, recon, w * h);
        rmse.ShouldBeLessThan(0.01);
        max.ShouldBeLessThan(0.03f);
    }

    [Fact]
    public void Image_LumaDc_IsConstantForFlatSolid()
    {
        // Sanity on the data the bitstream will carry: a flat solid yields identical per-block luma DC
        // and (after CfL with kx=0, kb=1) no AC energy.
        const int w = 16, h = 16;
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++) { srgb[c] = new float[w * h]; Array.Fill(srgb[c], 0.5f); }

        JxlVarDctImage.Encoded enc = JxlVarDctImage.Encode(srgb, w, h, 4096, 32, 32, 0);

        for (int c = 0; c < 3; c++)
        {
            int[] lf = enc.LfQuant[c];
            lf.ShouldAllBe(v => v == lf[0]); // every block's DC is equal
            enc.HfQuant[c].ShouldAllBe(v => v == 0); // no AC
        }
    }
}
