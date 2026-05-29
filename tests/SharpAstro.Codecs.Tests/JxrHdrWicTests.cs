using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Windows-Photo / WIC open tests for the HDR formats. WIC
/// (<c>System.Windows.Media.Imaging.BitmapDecoder</c>) is what Windows Photo and the
/// file-preview pipeline use, and it is stricter than jxrlib's <c>JxrDecApp</c>. The
/// open question this answers empirically: does our BD16F-RGB / BD32F file (written with
/// <c>OutputClrFmt=Rgb</c> / <c>YOnly</c>) actually open in WIC, or does float-RGB need
/// <c>OutputClrFmt=NComponent</c> as the tianwen consumer believed? Windows-only; no-ops
/// elsewhere and when WIC is unavailable.
/// </summary>
public sealed class JxrHdrWicTests
{
    private readonly ITestOutputHelper _out;
    public JxrHdrWicTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(48, 32)]
    [InlineData(64, 48)]
    public void EncodeRgbF16_OpensInWic(int w, int h)
    {
        Probe(w, h, JxrImageCodec.EncodeRgbF16(JxrBd16FTests.RgbPattern(w, h, "hdr"), w, h), "BD16F-RGB");
    }

    [Theory]
    [InlineData(48, 32)]
    [InlineData(64, 48)]
    public void EncodeGrayF32_OpensInWic(int w, int h)
    {
        Probe(w, h, JxrImageCodec.EncodeGrayF32(JxrBd32FTests.Pattern(w, h, "hdr"), w, h), "BD32F-mono");
    }

    [Theory]
    [InlineData(48, 32)]
    public void EncodeGrayF16_OpensInWic(int w, int h)
    {
        Probe(w, h, JxrImageCodec.EncodeGrayF16(JxrBd16FTests.GrayPattern(w, h, "hdr"), w, h), "BD16F-gray");
    }

    [Theory]
    [InlineData(48, 32)]
    public void EncodeRgb48_OpensInWic(int w, int h)
    {
        var (r, g, b) = (JxrBd16Tests.Pattern(w, h, "gradient", 1), JxrBd16Tests.Pattern(w, h, "gradient", 2), JxrBd16Tests.Pattern(w, h, "gradient", 3));
        Probe(w, h, JxrImageCodec.EncodeRgb48(r, g, b, w, h), "BD16-RGB");
    }

    [Theory]
    [InlineData(48, 32)]
    public void EncodeGray16_OpensInWic(int w, int h)
    {
        Probe(w, h, JxrImageCodec.EncodeGray16(JxrBd16Tests.Pattern(w, h, "gradient", 7), w, h), "BD16-gray");
    }

    private void Probe(int w, int h, byte[] jxr, string label)
    {
        if (!OperatingSystem.IsWindows()) { _out.WriteLine("Not Windows — skipping WIC test."); return; }

        var jxrPath = Path.Combine(Path.GetTempPath(), $"jxrwic_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var wic = WicOracle.Probe(jxrPath);
            if (!wic.Available) { _out.WriteLine($"WIC unavailable — skipping. {wic.RawOutput}"); return; }
            _out.WriteLine($"{label} {w}x{h}:\n{wic.RawOutput}");

            wic.IsValidImage.ShouldBeTrue($"WIC must accept our {label} .jxr ({w}x{h}); error: {wic.Error}");
            wic.Width.ShouldBe(w);
            wic.Height.ShouldBe(h);
            wic.HasNonZeroPixels.ShouldBeTrue($"WIC must decode {label} to non-zero pixels (not a silent all-zero decode)");
        }
        finally
        {
            File.Delete(jxrPath);
        }
    }
}
