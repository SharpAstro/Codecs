using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the Photo Overlap Transform port against vectors dumped from the
/// real jxrlib C (<c>Oracle/probe/overlap_probe.c</c>). The default decode
/// operators (<c>strPost*</c>, <c>HstDec1</c>) are the ones standard codestreams
/// use (jxrlib selects them when <c>cSubVersion == CODEC_SUBVERSION</c>), so
/// matching them is what makes our output interoperate with JxrEncApp/JxrDecApp.
/// The static HST / oddOddPre primitives are covered transitively through the
/// linkable wrappers. The three <c>*Alt</c> pairs are additionally checked for
/// exact round-trip, since those are true structural inverses.
/// </summary>
public sealed class PhotoOverlapTransformTests
{
    // Same deterministic buffer fill as the probe.
    private static int Fill(int i) => (i * 37 + 11) % 211 - 105;

    private static readonly int[] Stage1Cells =
        { 12, 13, 14, 15, 20, 21, 22, 23, 72, 73, 74, 75, 80, 81, 82, 83 };
    private static readonly int[] Stage2Cells =
        { 32, 48, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240, 256, 272, 320, 336 };

    private static int[] Gather(int[] buf, int[] idx)
    {
        var o = new int[idx.Length];
        for (var k = 0; k < idx.Length; k++) o[k] = buf[idx[k]];
        return o;
    }

    private static int[] Filled(int n)
    {
        var b = new int[n];
        for (var i = 0; i < n; i++) b[i] = Fill(i);
        return b;
    }

    // ---- scalar primitives (known-answer vs jxrlib) ----

    [Fact]
    public void Pre2_MatchesJxrlib()
    {
        int a = 40, b = -13;
        PhotoOverlapTransform.Pre2(ref a, ref b);
        (a, b).ShouldBe((54, -37));
    }

    [Fact]
    public void Post2_And_Post2Alt_MatchJxrlib()
    {
        int a = 40, b = -13;
        PhotoOverlapTransform.Post2(ref a, ref b);
        (a, b).ShouldBe((38, -3));

        int a2 = 40, b2 = -13;
        PhotoOverlapTransform.Post2Alt(ref a2, ref b2);
        (a2, b2).ShouldBe((36, 6));
    }

    [Fact]
    public void Pre2x2_Post2x2_MatchJxrlib()
    {
        int a = 50, b = -20, c = 33, d = -7;
        PhotoOverlapTransform.Pre2x2(ref a, ref b, ref c, ref d);
        (a, b, c, d).ShouldBe((50, -31, 22, -8));

        int a2 = 50, b2 = -20, c2 = 33, d2 = -7;
        PhotoOverlapTransform.Post2x2(ref a2, ref b2, ref c2, ref d2);
        (a2, b2, c2, d2).ShouldBe((56, -7, 45, -1));

        int a3 = 50, b3 = -20, c3 = 33, d3 = -7;
        PhotoOverlapTransform.Post2x2Alt(ref a3, ref b3, ref c3, ref d3);
        (a3, b3, c3, d3).ShouldBe((56, -7, 45, -1));
    }

    [Fact]
    public void Pre4_Post4_MatchJxrlib()
    {
        int a = 50, b = -20, c = 33, d = -7;
        PhotoOverlapTransform.Pre4(ref a, ref b, ref c, ref d);
        (a, b, c, d).ShouldBe((76, -3, 12, -46));

        int a2 = 50, b2 = -20, c2 = 33, d2 = -7;
        PhotoOverlapTransform.Post4(ref a2, ref b2, ref c2, ref d2);
        (a2, b2, c2, d2).ShouldBe((33, -26, 41, 19));

        int a3 = 50, b3 = -20, c3 = 33, d3 = -7;
        PhotoOverlapTransform.Post4Alt(ref a3, ref b3, ref c3, ref d3);
        (a3, b3, c3, d3).ShouldBe((36, -21, 38, 26));
    }

    // ---- block-level Split functions (known-answer vs jxrlib) ----

    [Fact]
    public void PreStage1_MatchesJxrlib()
    {
        var buf = Filled(256);
        PhotoOverlapTransform.PreStage1(buf, 0, 0);
        Gather(buf, Stage1Cells).ShouldBe(
            new[] { -115, 9, 53, 43, 72, 1, 12, -127, 51, 29, -156, -61, -82, -13, 75, 89 });
    }

    [Fact]
    public void PostStage1_NoCompensation_MatchesJxrlib()
    {
        var buf = Filled(256);
        PhotoOverlapTransform.PostStage1(buf, 0, 0, iHpQp: 0, hpAbsent: false);
        Gather(buf, Stage1Cells).ShouldBe(
            new[] { -24, -42, -6, 19, -57, 65, 92, -35, 8, 81, -65, -57, -84, -46, -56, -54 });
    }

    [Fact]
    public void PostStage1_HpAbsentCompensation_MatchesJxrlib()
    {
        var buf = Filled(256);
        PhotoOverlapTransform.PostStage1(buf, 0, 0, iHpQp: 0, hpAbsent: true);
        Gather(buf, Stage1Cells).ShouldBe(
            new[] { -24, -42, -6, 19, -57, 65, 92, -35, 8, 81, -65, -57, -84, -46, -56, -54 });
    }

    [Fact]
    public void PreStage2Split_MatchesJxrlib()
    {
        var buf = Filled(512);
        PhotoOverlapTransform.PreStage2Split(buf, 128, 256);
        Gather(buf, Stage2Cells).ShouldBe(
            new[] { 6, 29, 105, 73, 12, -50, -111, 2, 21, -58, -12, -100, 91, 105, -45, 97 });
    }

    [Fact]
    public void PostStage2Split_MatchesJxrlib()
    {
        var buf = Filled(512);
        PhotoOverlapTransform.PostStage2Split(buf, 128, 256);
        Gather(buf, Stage2Cells).ShouldBe(
            new[] { 53, -55, 64, 55, -27, -8, -47, 150, 59, 54, -35, -47, 128, 13, -94, 75 });
    }

    // ---- exact-inverse round-trips for the *Alt boundary operators ----

    [Fact]
    public void Post2Alt_ExactlyInverts_Pre2()
    {
        var rng = new Random(0xA11);
        for (var t = 0; t < 500; t++)
        {
            int a = rng.Next(-5000, 5000), b = rng.Next(-5000, 5000);
            int a0 = a, b0 = b;
            PhotoOverlapTransform.Pre2(ref a, ref b);
            PhotoOverlapTransform.Post2Alt(ref a, ref b);
            (a, b).ShouldBe((a0, b0), $"trial {t}");
        }
    }

    [Fact]
    public void Post2x2Alt_ExactlyInverts_Pre2x2()
    {
        var rng = new Random(0xB22);
        for (var t = 0; t < 500; t++)
        {
            int a = rng.Next(-5000, 5000), b = rng.Next(-5000, 5000), c = rng.Next(-5000, 5000), d = rng.Next(-5000, 5000);
            int a0 = a, b0 = b, c0 = c, d0 = d;
            PhotoOverlapTransform.Pre2x2(ref a, ref b, ref c, ref d);
            PhotoOverlapTransform.Post2x2Alt(ref a, ref b, ref c, ref d);
            (a, b, c, d).ShouldBe((a0, b0, c0, d0), $"trial {t}");
        }
    }

    [Fact]
    public void Post4Alt_ExactlyInverts_Pre4()
    {
        var rng = new Random(0xC33);
        for (var t = 0; t < 500; t++)
        {
            int a = rng.Next(-5000, 5000), b = rng.Next(-5000, 5000), c = rng.Next(-5000, 5000), d = rng.Next(-5000, 5000);
            int a0 = a, b0 = b, c0 = c, d0 = d;
            PhotoOverlapTransform.Pre4(ref a, ref b, ref c, ref d);
            PhotoOverlapTransform.Post4Alt(ref a, ref b, ref c, ref d);
            (a, b, c, d).ShouldBe((a0, b0, c0, d0), $"trial {t}");
        }
    }
}
