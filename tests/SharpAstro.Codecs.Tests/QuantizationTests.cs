using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the quantization port against vectors dumped from the real jxrlib C
/// (<c>Oracle/probe/quant_probe.c</c>): <c>remapQP</c> in both default and scaled
/// modes, the forward <c>QUANT</c> / <c>QUANT_Mulless</c> path, dequantization,
/// and exact lossless round-trip.
/// </summary>
public sealed class QuantizationTests
{
    // idx -> (Qp, Man, Exp, Offset), non-scaled (bScaledArith = false).
    private static readonly (int Idx, int Qp, uint Man, int Exp, int Off)[] NonScaled =
    {
        (0, 1, 0, 0, 0), (1, 1, 0, 0, 0), (2, 1, 0, 0, 0), (3, 1, 0, 0, 0), (4, 1, 0, 0, 0),
        (5, 2, 0, 1, 0), (8, 2, 0, 1, 0), (15, 4, 0, 2, 1), (16, 4, 0, 2, 1),
        (17, 5, 3435973837, 2, 2), (31, 8, 0, 3, 3), (32, 8, 0, 3, 3), (33, 9, 3817748708, 3, 3),
        (47, 16, 0, 4, 6), (48, 16, 0, 4, 6), (49, 17, 4042322161, 4, 6),
        (64, 32, 0, 5, 12), (80, 64, 0, 6, 24), (127, 496, 2216757315, 8, 186),
        (200, 12288, 2863311531, 13, 4608), (255, 126976, 2216757315, 16, 47616),
    };

    // idx -> (Qp, Man, Exp, Offset), scaled (bScaledArith = true, shift = SHIFTZERO = 1).
    private static readonly (int Idx, int Qp, uint Man, int Exp, int Off)[] Scaled =
    {
        (0, 1, 0, 0, 0), (1, 2, 0, 1, 0), (2, 4, 0, 2, 1), (3, 6, 2863311531, 2, 2),
        (4, 8, 0, 3, 3), (5, 10, 3435973837, 3, 3), (8, 16, 0, 4, 6), (15, 30, 2290649225, 4, 11),
        (16, 32, 0, 5, 12), (17, 34, 4042322161, 5, 12), (31, 62, 2216757315, 5, 23),
        (32, 64, 0, 6, 24), (33, 68, 4042322161, 6, 25), (47, 124, 2216757315, 6, 46),
        (48, 128, 0, 7, 48), (49, 136, 4042322161, 7, 51), (64, 256, 0, 8, 96),
        (80, 512, 0, 9, 192), (127, 3968, 2216757315, 11, 1488),
        (200, 98304, 2863311531, 16, 36864), (255, 1015808, 2216757315, 19, 380928),
    };

    // (idx, v, quantized, dequantized) non-scaled. Covers the mulless (power-of-two)
    // path and the reciprocal-multiply path (idx 33 -> Qp 9, non-power-of-two).
    private static readonly (int Idx, int V, int Q, int Deq)[] QuantSamples =
    {
        (1, 12345, 12345, 12345), (1, -12345, -12345, -12345),
        (8, 5, 2, 4), (8, -5, -2, -4), (8, 100, 50, 100), (8, 12345, 6172, 12344), (8, -12345, -6172, -12344),
        (16, 5, 1, 4), (16, 100, 25, 100), (16, 1000, 250, 1000), (16, 12345, 3086, 12344),
        (33, 5, 0, 0), (33, 100, 11, 99), (33, 1000, 111, 999), (33, 12345, 1372, 12348), (33, -12345, -1372, -12348),
        (64, 100, 3, 96), (64, 1000, 31, 992), (64, 12345, 386, 12352), (64, -12345, -386, -12352),
    };

    [Fact]
    public void RemapQp_NonScaled_MatchesJxrlib()
    {
        foreach (var (idx, qp, man, exp, off) in NonScaled)
        {
            var q = Quantization.Resolve(idx, scaledArith: false);
            (q.Qp, q.Man, q.Exp, q.Offset).ShouldBe((qp, man, exp, off), $"idx {idx}");
        }
    }

    [Fact]
    public void RemapQp_Scaled_MatchesJxrlib()
    {
        foreach (var (idx, qp, man, exp, off) in Scaled)
        {
            var q = Quantization.Resolve(idx, scaledArith: true, shift: 1);
            (q.Qp, q.Man, q.Exp, q.Offset).ShouldBe((qp, man, exp, off), $"idx {idx}");
        }
    }

    [Fact]
    public void Quantize_And_Dequantize_MatchJxrlib()
    {
        foreach (var (idx, v, expectedQ, expectedDeq) in QuantSamples)
        {
            var q = Quantization.Resolve(idx, scaledArith: false);
            int quantized = Quantization.Quantize(v, q);
            quantized.ShouldBe(expectedQ, $"quantize idx={idx} v={v}");
            Quantization.Dequantize(quantized, q.Qp).ShouldBe(expectedDeq, $"dequantize idx={idx} v={v}");
        }
    }

    [Fact]
    public void LosslessIndices_RoundTripExactly()
    {
        // Indices 0..4 all resolve to Qp == 1 in non-scaled mode -> identity.
        var rng = new Random(0xDEC0DE);
        for (var idx = 0; idx <= 4; idx++)
        {
            var q = Quantization.Resolve(idx, scaledArith: false);
            q.Qp.ShouldBe(1, $"idx {idx} should be lossless");
            for (var t = 0; t < 200; t++)
            {
                int v = rng.Next(-1_000_000, 1_000_000);
                Quantization.Dequantize(Quantization.Quantize(v, q), q.Qp).ShouldBe(v);
            }
        }
    }
}
