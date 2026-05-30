using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the model-bits adaptation against vectors dumped from the real,
/// linkable jxrlib <c>UpdateModelMB</c> (<c>Oracle/probe/model_probe.c</c>),
/// across DC/LP/AC bands, YUV444 and Y_ONLY, exercising the FLC-width up/down
/// drift and the floor at 0. Also checks the jxrlib reset/init widths.
/// </summary>
public sealed class ModelBitsTests
{
    private static void Step(AdaptiveModel m, ColorFormat cf, int ch, int lm0, int lm1,
                             int bits0, int bits1, int st0, int st1, string tag)
    {
        var lapMean = new[] { lm0, lm1 };
        ModelBits.UpdateMb(cf, ch, lapMean, m);
        m.FlcBits[0].ShouldBe(bits0, $"{tag} FlcBits0");
        m.FlcBits[1].ShouldBe(bits1, $"{tag} FlcBits1");
        m.FlcState[0].ShouldBe(st0, $"{tag} FlcState0");
        m.FlcState[1].ShouldBe(st1, $"{tag} FlcState1");
    }

    [Fact]
    public void InitWidths_MatchJxrlibReset()
    {
        new AdaptiveModel(Band.Dc).FlcBits.ShouldBe(new[] { 8, 8 });
        new AdaptiveModel(Band.Lp).FlcBits.ShouldBe(new[] { 4, 4 });
        new AdaptiveModel(Band.Ac).FlcBits.ShouldBe(new[] { 0, 0 });
    }

    [Fact]
    public void Dc_Yuv444_DriftsUpAndDown_MatchesJxrlib()
    {
        var m = new AdaptiveModel(Band.Dc);
        Step(m, ColorFormat.Yuv444, 3, 1, 1, 9, 8, 0, 8, "DC_up1");
        Step(m, ColorFormat.Yuv444, 3, 1, 1, 10, 9, 0, 0, "DC_up2");
        Step(m, ColorFormat.Yuv444, 3, 1, 1, 11, 9, 0, 8, "DC_up3");
        Step(m, ColorFormat.Yuv444, 3, 0, 0, 10, 9, 0, -6, "DC_dn1");
        Step(m, ColorFormat.Yuv444, 3, 0, 0, 9, 8, 0, 0, "DC_dn2");
        Step(m, ColorFormat.Yuv444, 3, 0, 0, 8, 7, 0, 0, "DC_dn3");
        Step(m, ColorFormat.Yuv444, 3, 20, 20, 9, 8, 0, 0, "DC_mid");
    }

    [Fact]
    public void Lp_Yuv444_MatchesJxrlib()
    {
        var m = new AdaptiveModel(Band.Lp);
        Step(m, ColorFormat.Yuv444, 3, 30, 30, 5, 5, 0, 0, "LP_a");
        Step(m, ColorFormat.Yuv444, 3, 30, 30, 6, 6, 0, 0, "LP_b");
        Step(m, ColorFormat.Yuv444, 3, 0, 0, 5, 5, 0, 0, "LP_c");
        Step(m, ColorFormat.Yuv444, 3, 200, 200, 6, 6, 0, 0, "LP_d");
    }

    [Fact]
    public void Ac_Yuv444_FloorsAtZero_MatchesJxrlib()
    {
        var m = new AdaptiveModel(Band.Ac);
        Step(m, ColorFormat.Yuv444, 3, 100, 100, 0, 0, 0, 0, "AC_a");
        Step(m, ColorFormat.Yuv444, 3, 100, 100, 0, 0, 0, 0, "AC_b");
        Step(m, ColorFormat.Yuv444, 3, 0, 0, 0, 0, -8, -8, "AC_c");
    }

    [Fact]
    public void Dc_YOnly_OnlyUpdatesLuma_MatchesJxrlib()
    {
        var m = new AdaptiveModel(Band.Dc);
        Step(m, ColorFormat.YOnly, 1, 1, 999, 9, 8, 0, 0, "YDC_a");
        Step(m, ColorFormat.YOnly, 1, 0, 999, 8, 8, 0, 0, "YDC_b");
    }
}
