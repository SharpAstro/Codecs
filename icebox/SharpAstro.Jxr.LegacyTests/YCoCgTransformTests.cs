using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

public sealed class YCoCgTransformTests
{
    [Fact]
    public void Forward_Inverse_RoundTrips_OverRandomPixels()
    {
        // Lifting is reversible by construction — verify on a few thousand
        // random RGB triples spanning the full signed-int range you'd see
        // post-bias in BD8/BD16/BD32F pipelines.
        var rng = new Random(unchecked((int)0xC0C6_C0C6));
        for (var iter = 0; iter < 5000; iter++)
        {
            // Mix ranges: tight 8-bit, wide 16-bit, extreme 24-bit.
            int range = iter % 3 == 0 ? 256 : iter % 3 == 1 ? 65536 : 16_777_216;
            var rgb = new[]
            {
                rng.Next(-range, range),
                rng.Next(-range, range),
                rng.Next(-range, range),
            };
            var work = (int[])rgb.Clone();
            YCoCgTransform.ForwardInPlace(work);
            YCoCgTransform.InverseInPlace(work);
            work[0].ShouldBe(rgb[0], $"R iter={iter}");
            work[1].ShouldBe(rgb[1], $"G iter={iter}");
            work[2].ShouldBe(rgb[2], $"B iter={iter}");
        }
    }

    [Fact]
    public void Forward_GraysMapToZeroChroma()
    {
        // A neutral gray pixel (R=G=B=v) must produce Co=Cg=0 — luminance
        // carries all the signal. This is the standard YCoCg test case.
        for (var v = -1000; v <= 1000; v += 137)
        {
            var work = new int[] { v, v, v };
            YCoCgTransform.ForwardInPlace(work);
            work[1].ShouldBe(0, $"Co at v={v}");
            work[2].ShouldBe(0, $"Cg at v={v}");
            // Y for R=G=B=v: Co=0, t=v+0=v, Cg=v-v=0, Y=v+0 = v.
            work[0].ShouldBe(v, $"Y at v={v}");
        }
    }

    [Fact]
    public void Forward_Inverse_RejectsOddLengthBuffer()
    {
        Should.Throw<ArgumentException>(() => YCoCgTransform.ForwardInPlace(new int[5]));
        Should.Throw<ArgumentException>(() => YCoCgTransform.InverseInPlace(new int[5]));
    }

    [Fact]
    public void Forward_MultipleConsecutivePixels_AreIndependent()
    {
        // Channel layout sanity: triples don't bleed into each other.
        var work = new int[] { 100, 150, 50, 200, 100, 50 };
        YCoCgTransform.ForwardInPlace(work);
        YCoCgTransform.InverseInPlace(work);
        work.ShouldBe(new[] { 100, 150, 50, 200, 100, 50 });
    }
}
