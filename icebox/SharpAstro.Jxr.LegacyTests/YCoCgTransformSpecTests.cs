using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Pin <see cref="YCoCgTransform"/> to the T.832 wire format. These are
/// regression tests against the spec-divergence bug where the original
/// implementation used the standard YCoCg-R lifting (<c>Co = R-B</c>) —
/// it round-tripped with itself but produced files that WIC / jxrlib
/// reference decoders reconstructed as channel-scrambled garbage.
/// </summary>
/// <remarks>
/// Reference: T.832 D.3.2 / Table D.6 (forward, RGB→YUV) and 9.10.4.3 /
/// Table 185 (inverse, YUV→RGB):
/// <code>
/// Forward:
///   V = B - R
///   t = R - G + Ceiling(V/2)
///   Y = G + Floor(t/2)
///   U = -t
///
/// Inverse:
///   t = -U
///   G = Y - Floor(t/2)
///   R = t + G - Ceiling(V/2)
///   B = V + R
/// </code>
/// Any reordering of operations or sign flips silently breaks WIC interop
/// without breaking our self-round-trip — so we verify both directions
/// independently against hand-traced spec values.
/// </remarks>
public sealed class YCoCgTransformSpecTests
{
    [Theory]
    // Spec-traced (R, G, B) → (Y, U, V) per D.3.2 / Table D.6:
    //   V = B - R
    //   t = R - G + Ceiling(V/2)
    //   Y = G + Floor(t/2)
    //   U = -t
    [InlineData(0, 0, 0, 0, 0, 0)]               // black
    [InlineData(100, 100, 100, 100, 0, 0)]        // gray
    [InlineData(100, 150, 200, 150, 0, 100)]      // V=100, t=0, Y=150, U=0
    [InlineData(47, 20, 49, 34, -28, 2)]         // seagull pixel (0,0): V=2, t=28, Y=34, U=-28
    [InlineData(-81, -108, -79, -94, -28, 2)]    // same but Bd8Bias-shifted (subtract 128 from each input)
    [InlineData(255, 0, 0, 64, -128, -255)]      // pure red: V=-255, Ceil(V/2)=-127, t=128, Y=64, U=-128
    [InlineData(0, 255, 0, 127, 255, 0)]         // pure green: V=0, t=-255, Y=255+Floor(-128)=127, U=255
    [InlineData(0, 0, 255, 64, -128, 255)]       // pure blue: V=255, Ceil(V/2)=128, t=128, Y=64, U=-128
    public void Forward_MatchesSpec(int r, int g, int b, int expectedY, int expectedU, int expectedV)
    {
        Span<int> buf = [r, g, b];
        YCoCgTransform.ForwardInPlace(buf);
        buf[0].ShouldBe(expectedY, "Y");
        buf[1].ShouldBe(expectedU, "U");
        buf[2].ShouldBe(expectedV, "V");
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(100, 0, 0, 100, 100, 100)]        // gray inverse: V=0, t=0, G=100, R=100, B=100
    [InlineData(34, -28, 2, 47, 20, 49)]         // inverse of seagull sample
    [InlineData(-94, -28, 2, -81, -108, -79)]    // biased variant
    [InlineData(64, -128, -255, 255, 0, 0)]      // inverse of pure red
    [InlineData(127, 255, 0, 0, 255, 0)]         // inverse of pure green
    public void Inverse_MatchesSpec(int y, int u, int v, int expectedR, int expectedG, int expectedB)
    {
        Span<int> buf = [y, u, v];
        YCoCgTransform.InverseInPlace(buf);
        buf[0].ShouldBe(expectedR, "R");
        buf[1].ShouldBe(expectedG, "G");
        buf[2].ShouldBe(expectedB, "B");
    }

    [Fact]
    public void Forward_ThenInverse_RoundTrips()
    {
        // Self-inverse property across the full RGB cube range. This is
        // necessary but NOT sufficient — the old broken implementation
        // also round-tripped with itself.
        var rng = new Random(0x10C0C9);
        for (var trial = 0; trial < 1000; trial++)
        {
            int r = rng.Next(-256, 256);
            int g = rng.Next(-256, 256);
            int b = rng.Next(-256, 256);
            Span<int> buf = [r, g, b];
            YCoCgTransform.ForwardInPlace(buf);
            YCoCgTransform.InverseInPlace(buf);
            buf[0].ShouldBe(r);
            buf[1].ShouldBe(g);
            buf[2].ShouldBe(b);
        }
    }
}
