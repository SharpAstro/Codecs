using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the internal color transform against jxrlib's exact <c>_CC</c> /
/// <c>_CC_CMYK</c> macros (<c>Oracle/probe/ycocg_probe.c</c> run through clang)
/// and confirms forward∘inverse is bit-exact identity. This is the RGB→YUV444
/// lift that Windows Photo / WIC requires for RGB JXR.
/// </summary>
public sealed class ColorTransformTests
{
    [Theory]
    [InlineData(100, 150, 200, 0, 150, 100)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(255, 0, 128, 192, 96, -127)]
    [InlineData(47, 20, 49, 28, 34, 2)]
    [InlineData(200, 100, 50, 25, 112, -150)]
    public void ForwardRgb_MatchesJxrlib(int r, int g, int b, int er, int eg, int eb)
    {
        int rr = r, gg = g, bb = b;
        ColorTransform.ForwardRgb(ref rr, ref gg, ref bb);
        (rr, gg, bb).ShouldBe((er, eg, eb));
    }

    [Fact]
    public void ForwardThenInverseRgb_IsIdentity()
    {
        var rng = new Random(0xC0107);
        for (var t = 0; t < 2000; t++)
        {
            int r = rng.Next(-1000, 1000), g = rng.Next(-1000, 1000), b = rng.Next(-1000, 1000);
            int rr = r, gg = g, bb = b;
            ColorTransform.ForwardRgb(ref rr, ref gg, ref bb);
            ColorTransform.InverseRgb(ref rr, ref gg, ref bb);
            (rr, gg, bb).ShouldBe((r, g, b), $"t={t}");
        }
    }

    [Fact]
    public void ForwardCmyk_MatchesJxrlib()
    {
        int c = 200, m = 50, y = 180, k = 30;
        ColorTransform.ForwardCmyk(ref c, ref m, ref y, ref k);
        (c, m, y, k).ShouldBe((140, 90, -20, 75));
    }

    [Fact]
    public void ForwardThenInverseCmyk_IsIdentity()
    {
        var rng = new Random(0xC0108);
        for (var t = 0; t < 2000; t++)
        {
            int c = rng.Next(-1000, 1000), m = rng.Next(-1000, 1000), y = rng.Next(-1000, 1000), k = rng.Next(-1000, 1000);
            int cc = c, mm = m, yy = y, kk = k;
            ColorTransform.ForwardCmyk(ref cc, ref mm, ref yy, ref kk);
            ColorTransform.InverseCmyk(ref cc, ref mm, ref yy, ref kk);
            (cc, mm, yy, kk).ShouldBe((c, m, y, k), $"t={t}");
        }
    }
}
