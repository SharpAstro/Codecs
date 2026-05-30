using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Self-tests for the <see cref="VisualJudge"/> harness — proves the instrument
/// reads zero on identical rasters and nonzero on a perturbation, and that it
/// drops a diff artifact, independently of the JXR codec it will later judge.
/// </summary>
public sealed class VisualJudgeTests
{
    private readonly ITestOutputHelper _out;
    public VisualJudgeTests(ITestOutputHelper output) => _out = output;

    private static byte[] Gradient24(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 3;
            px[i + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
            px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
            px[i + 2] = (byte)((x + y) * 255 / Math.Max(1, w + h - 2));
        }
        return px;
    }

    [Fact]
    public void IdenticalRgb_DistortionIsZero()
    {
        const int w = 64, h = 48;
        var img = Gradient24(w, h);

        var r = VisualJudge.CompareRgb24(img, img, w, h, nameof(IdenticalRgb_DistortionIsZero));
        _out.WriteLine(r.ToString());

        r.Distortion.ShouldBe(0.0);
        r.DiffImagePath.ShouldBeNull(); // nothing written when identical
    }

    [Fact]
    public void PerturbedRgb_DistortionIsPositive_AndWritesDiffArtifact()
    {
        const int w = 64, h = 48;
        var a = Gradient24(w, h);
        var b = (byte[])a.Clone();

        // Corrupt a 8x8 block in the middle — the kind of "garbage after the
        // first block" signature this judge exists to catch.
        for (var y = 20; y < 28; y++)
        for (var x = 28; x < 36; x++)
        {
            var i = (y * w + x) * 3;
            b[i + 0] = 255; b[i + 1] = 0; b[i + 2] = 0;
        }

        var r = VisualJudge.CompareRgb24(a, b, w, h, nameof(PerturbedRgb_DistortionIsPositive_AndWritesDiffArtifact));
        _out.WriteLine(r.ToString());

        r.Distortion.ShouldBeGreaterThan(0.0);
        r.DiffImagePath.ShouldNotBeNull();
        File.Exists(r.DiffImagePath!).ShouldBeTrue("a red-highlighted diff PNG should be written on mismatch");
    }

    [Fact]
    public void Gray16_RoundTripsThroughJudge()
    {
        const int w = 32, h = 32;
        var a = new ushort[w * h];
        for (var i = 0; i < a.Length; i++) a[i] = (ushort)(i * 37 % 65536);
        var b = (ushort[])a.Clone();
        b[w * h / 2] ^= 0xFFFF; // flip one pixel

        VisualJudge.CompareGray16(a, a, w, h, "gray16-identical").Distortion.ShouldBe(0.0);
        VisualJudge.CompareGray16(a, b, w, h, "gray16-onepix").Distortion.ShouldBeGreaterThan(0.0);
    }
}
