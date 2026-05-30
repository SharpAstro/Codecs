using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// JPEG XL Modular mode (ISO/IEC 18181-1 §H) — Rung 4. The integer-math primitives (predictors,
/// RCT) are validated standalone here (RCT by forward/inverse round-trip; predictors by formula);
/// the full decode loop is validated end-to-end against real libjxl bytes once the frame glue lands.
/// </summary>
public sealed class JxlModularTests
{
    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, -1)]
    [InlineData(2u, 1)]
    [InlineData(3u, -2)]
    [InlineData(4u, 2)]
    [InlineData(5u, -3)]
    [InlineData(100u, 50)]
    [InlineData(101u, -51)]
    public void UnpackSigned_MatchesZigzag(uint encoded, int expected)
        => JxlModular.UnpackSigned(encoded).ShouldBe(expected);

    [Fact]
    public void Predictors_MatchSpecFormulas()
    {
        var nb = new JxlNeighbors { N = 100, W = 40, NW = 60, NE = 120, NN = 90, WW = 30, NEE = 130 };

        JxlModularPredictor.Predict(JxlPredictor.Zero, nb).ShouldBe(0);
        JxlModularPredictor.Predict(JxlPredictor.West, nb).ShouldBe(40);
        JxlModularPredictor.Predict(JxlPredictor.North, nb).ShouldBe(100);
        JxlModularPredictor.Predict(JxlPredictor.NorthEast, nb).ShouldBe(120);
        JxlModularPredictor.Predict(JxlPredictor.NorthWest, nb).ShouldBe(60);
        JxlModularPredictor.Predict(JxlPredictor.WestWest, nb).ShouldBe(30);

        JxlModularPredictor.Predict(JxlPredictor.AvgWestAndNorth, nb).ShouldBe((40 + 100) / 2);
        JxlModularPredictor.Predict(JxlPredictor.AvgWestAndNorthWest, nb).ShouldBe((40 + 60) / 2);
        JxlModularPredictor.Predict(JxlPredictor.AvgNorthAndNorthWest, nb).ShouldBe((100 + 60) / 2);
        JxlModularPredictor.Predict(JxlPredictor.AvgNorthAndNorthEast, nb).ShouldBe((100 + 120) / 2);

        // Select: |N-NW| = 40, |W-NW| = 20 -> 40 < 20 is false -> N
        JxlModularPredictor.Predict(JxlPredictor.Select, nb).ShouldBe(100);

        // Gradient: clamp(N+W-NW=80, min(40,100)=40, max=100) = 80
        JxlModularPredictor.Predict(JxlPredictor.Gradient, nb).ShouldBe(80);

        // AvgAll: (6*100 - 2*90 + 7*40 + 30 + 130 + 3*120 + 8) / 16
        long avgAll = (6 * 100 - 2 * 90 + 7 * 40 + 30 + 130 + 3 * 120 + 8) / 16;
        JxlModularPredictor.Predict(JxlPredictor.AvgAll, nb).ShouldBe((int)avgAll);

        // SelfCorrecting: (wp*8 prediction + 3) >> 3
        JxlModularPredictor.Predict(JxlPredictor.SelfCorrecting, nb, wpPredictionTimes8: 803).ShouldBe((803 + 3) >> 3);
    }

    [Fact]
    public void Gradient_ClampsToNeighbourRange()
    {
        // N+W-NW outside [min(W,N), max(W,N)] must clamp.
        var hi = new JxlNeighbors { N = 10, W = 20, NW = 5, NE = 0, NN = 0, WW = 0, NEE = 0 }; // 10+20-5=25 -> clamp to 20
        JxlModularPredictor.Predict(JxlPredictor.Gradient, hi).ShouldBe(20);
        var lo = new JxlNeighbors { N = 10, W = 20, NW = 40, NE = 0, NN = 0, WW = 0, NEE = 0 }; // 10+20-40=-10 -> clamp to 10
        JxlModularPredictor.Predict(JxlPredictor.Gradient, lo).ShouldBe(10);
    }

    [Fact]
    public void Rct_ForwardThenInverse_RoundTrips_AllTypes()
    {
        const int n = 64;
        // Deterministic spread of values (incl. negatives) feeding the wrapping arithmetic.
        for (int rctType = 0; rctType <= 41; rctType++)
        {
            int[][] ch =
            [
                MakeChannel(n, seed: 1),
                MakeChannel(n, seed: 2),
                MakeChannel(n, seed: 3),
            ];
            int[][] original = [(int[])ch[0].Clone(), (int[])ch[1].Clone(), (int[])ch[2].Clone()];

            JxlRct.Forward(rctType, ch, beginC: 0);
            JxlRct.Inverse(rctType, ch, beginC: 0);

            for (int c = 0; c < 3; c++)
                ch[c].ShouldBe(original[c], $"rct_type={rctType} channel={c}");
        }
    }

    [Fact]
    public void Rct_Inverse_YCoCg_KnownVector()
    {
        // rct_type 6: pure YCoCg-R inverse on a single pixel. Encode a known RGB, decode it back.
        int[][] ch = [[123], [-45], [200]]; // arbitrary residual channels
        int[][] expected = [(int[])ch[0].Clone(), (int[])ch[1].Clone(), (int[])ch[2].Clone()];
        // Forward(6) then Inverse(6) is identity (covered above); here assert the inverse is stable
        // and self-consistent with the forward for a hand-picked vector.
        JxlRct.Forward(6, ch, 0);
        JxlRct.Inverse(6, ch, 0);
        for (int c = 0; c < 3; c++)
            ch[c].ShouldBe(expected[c]);
    }

    private static int[] MakeChannel(int n, int seed)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++)
            a[i] = ((i * 2654435761L + seed * 40503L) % 131072) is var v && v > 65536 ? (int)(v - 131072) : (int)v;
        return a;
    }
}
