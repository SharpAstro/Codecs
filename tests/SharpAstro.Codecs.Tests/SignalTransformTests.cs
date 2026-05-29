using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 7b — the signal path. Round-trips a 16×16 BD8 RGB macroblock through
/// <see cref="SignalTransform"/> (color transform → block-layout load → 2-stage
/// Photo Core Transform → quantize → DC-block extract, and the inverse), OL_NONE.
/// At QP index 0 (lossless) the whole chain is an exact inverse, so the output
/// pixels must equal the input bit-for-bit — validating the pixel↔plane layout
/// (idxCC), the 2-stage transform orchestration, and the DC-block extract/restore.
/// </summary>
public sealed class SignalTransformTests
{
    private static readonly JxrQuantizer Lossless = Quantization.Resolve(0); // Qp = 1

    private static void RoundTrip(int[] r, int[] g, int[] b)
    {
        var mb = new Macroblock(3);
        SignalTransform.Forward(r, g, b, mb, Lossless, Lossless, Lossless);
        SignalTransform.DequantizeHighpass(mb, Lossless.Qp);

        var r2 = new int[256];
        var g2 = new int[256];
        var b2 = new int[256];
        SignalTransform.Inverse(mb, r2, g2, b2, Lossless.Qp, Lossless.Qp);

        for (var i = 0; i < 256; i++)
        {
            r2[i].ShouldBe(r[i], $"R[{i}]");
            g2[i].ShouldBe(g[i], $"G[{i}]");
            b2[i].ShouldBe(b[i], $"B[{i}]");
        }
    }

    [Fact]
    public void RandomPixels_RoundTrip()
    {
        var rng = new Random(0x5167);
        var r = new int[256];
        var g = new int[256];
        var b = new int[256];
        for (var t = 0; t < 4000; t++)
        {
            for (var i = 0; i < 256; i++) { r[i] = rng.Next(256); g[i] = rng.Next(256); b[i] = rng.Next(256); }
            RoundTrip(r, g, b);
        }
    }

    [Fact]
    public void ConstantColor_RoundTrip()
    {
        foreach (var (cr, cg, cb) in new[] { (0, 0, 0), (255, 255, 255), (128, 64, 200), (17, 240, 3) })
        {
            var r = new int[256];
            var g = new int[256];
            var b = new int[256];
            Array.Fill(r, cr); Array.Fill(g, cg); Array.Fill(b, cb);
            RoundTrip(r, g, b);
        }
    }

    [Fact]
    public void Gradients_RoundTrip()
    {
        var r = new int[256];
        var g = new int[256];
        var b = new int[256];
        for (var row = 0; row < 16; row++)
            for (var col = 0; col < 16; col++)
            {
                int i = row * 16 + col;
                r[i] = row * 16 + col;          // diagonal-ish ramp
                g[i] = col * 16;                // horizontal ramp
                b[i] = 255 - row * 16;          // vertical ramp
            }
        RoundTrip(r, g, b);
    }
}
