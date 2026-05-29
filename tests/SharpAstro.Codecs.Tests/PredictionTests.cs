using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the prediction port. <see cref="Prediction.GetAcPredMode"/> is
/// checked against vectors from the real jxrlib C
/// (<c>Oracle/probe/predict_probe.c</c>). The DC/AD and AC prediction apply
/// functions are checked by encode∘decode round-trip identity — the property
/// the codec relies on — over random data and every mode.
/// </summary>
public sealed class PredictionTests
{
    private static int[][] Blocks(int[] y, int[] u, int[] v) => new[] { y, u, v };

    private static int[] DcBlock(int r1, int r2, int r3, int c1, int c2, int c3)
    {
        var b = new int[16];
        b[1] = r1; b[2] = r2; b[3] = r3; b[4] = c1; b[8] = c2; b[12] = c3;
        return b;
    }

    [Fact]
    public void GetAcPredMode_MatchesJxrlib()
    {
        var hY = DcBlock(30, 20, 10, 1, 1, 1);   // strong row
        var vY = DcBlock(1, 1, 1, 30, 20, 10);   // strong column
        var bY = DcBlock(10, 10, 10, 10, 10, 10); // balanced
        var z = new int[16];
        var u1 = new int[16]; u1[1] = 5; u1[4] = 20;
        var v1 = new int[16]; v1[1] = 5; v1[4] = 20;

        Prediction.GetAcPredMode(Blocks(hY, z, z), ColorFormat.YOnly).ShouldBe(0);
        Prediction.GetAcPredMode(Blocks(vY, z, z), ColorFormat.YOnly).ShouldBe(1);
        Prediction.GetAcPredMode(Blocks(bY, z, z), ColorFormat.YOnly).ShouldBe(2);
        Prediction.GetAcPredMode(Blocks(hY, u1, v1), ColorFormat.Yuv444).ShouldBe(2);
        Prediction.GetAcPredMode(Blocks(vY, u1, v1), ColorFormat.Yuv444).ShouldBe(1);
    }

    [Fact]
    public void DcAdPredict_EncThenDec_IsIdentity()
    {
        var rng = new Random(0x9A1);
        for (var dcMode = 0; dcMode <= 3; dcMode++)
        for (var adMode = 0; adMode <= 2; adMode++)
        for (var t = 0; t < 50; t++)
        {
            var original = new int[16];
            for (var i = 0; i < 16; i++) original[i] = rng.Next(-4096, 4096);
            var block = (int[])original.Clone();

            var left = NewPredInfo(rng);
            var top = NewPredInfo(rng);

            Prediction.DcAdPredictEnc(block, dcMode, adMode, left, top);
            Prediction.DcAdPredictDec(block, dcMode, adMode, left, top);

            block.ShouldBe(original, $"dcMode={dcMode} adMode={adMode} t={t}");
        }
    }

    [Theory]
    [InlineData(0)] // from left
    [InlineData(1)] // from top
    [InlineData(2)] // none
    public void AcPredict_EncThenDec_IsIdentity(int acPredMode)
    {
        var rng = new Random(0xACED + acPredMode);
        for (var t = 0; t < 100; t++)
        {
            var original = new int[256];
            for (var i = 0; i < 256; i++) original[i] = rng.Next(-4096, 4096);
            var plane = (int[])original.Clone();

            Prediction.AcPredictEnc(plane, acPredMode);
            Prediction.AcPredictDec(plane, acPredMode);

            plane.ShouldBe(original, $"acPredMode={acPredMode} t={t}");
        }
    }

    private static PredInfo NewPredInfo(Random rng)
    {
        var p = new PredInfo { Dc = rng.Next(-4096, 4096), QpIndex = rng.Next(0, 16) };
        for (var i = 0; i < 6; i++) p.Ad[i] = rng.Next(-4096, 4096);
        return p;
    }
}
