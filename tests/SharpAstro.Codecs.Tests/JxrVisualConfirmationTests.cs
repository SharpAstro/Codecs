using ImageMagick;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Eyeball / WIC confirmation that the codec produces a real, viewable <c>.jxr</c>
/// from a real photo. Loads a JPEG, crops to a 16-multiple, encodes BD8 RGB at the
/// default overlap (OL_ONE, what jxrlib emits), and writes three artifacts to a
/// discoverable folder so a human can open them:
/// <list type="bullet">
///   <item><c>dockpanes-source.png</c> — the exact pixels fed to the encoder,</item>
///   <item><c>dockpanes-ours-ol1.jxr</c> — our encode (open in Windows Photo),</item>
///   <item><c>dockpanes-roundtrip.png</c> — our decode of that .jxr.</item>
/// </list>
/// It also asserts the round-trip is lossless (RMSE 0) and that WIC accepts the
/// file, so the visual check is backed by hard numbers. The artifact path is
/// written to the test output.
/// </summary>
public sealed class JxrVisualConfirmationTests
{
    private readonly ITestOutputHelper _out;
    public JxrVisualConfirmationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RealPhoto_Encode_Decode_Artifacts()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DockPanes.jpg");
        if (!File.Exists(src)) { _out.WriteLine("DockPanes.jpg not found — skipping visual confirmation."); return; }

        // Load the photo and crop to a multiple of 16 (the codec's MB grid).
        using var img = new MagickImage(src);
        img.ColorSpace = ColorSpace.sRGB;
        int w = (int)img.Width & ~15;
        int h = (int)img.Height & ~15;
        img.Crop(new MagickGeometry(0, 0, (uint)w, (uint)h));
        img.ResetPage();

        using var px = img.GetPixels();
        byte[] rgb = px.ToByteArray(PixelMapping.RGB)!; // packed 8-bit RGB, row-major
        rgb.Length.ShouldBe(w * h * 3);

        var r = new int[w * h];
        var g = new int[w * h];
        var b = new int[w * h];
        for (var i = 0; i < w * h; i++)
        {
            r[i] = rgb[i * 3 + 0];
            g[i] = rgb[i * 3 + 1];
            b[i] = rgb[i * 3 + 2];
        }

        // Encode at the default overlap (OL_ONE), lossless.
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h, overlap: 1);

        // Decode back and pack for comparison.
        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr);
        dw.ShouldBe(w);
        dh.ShouldBe(h);
        var rt = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rt[i * 3 + 0] = (byte)dr[i];
            rt[i * 3 + 1] = (byte)dg[i];
            rt[i * 3 + 2] = (byte)db[i];
        }

        // Write the artifacts for a human to open.
        var dir = Path.Combine(FindRepoRoot() ?? Path.GetTempPath(), "artifacts", "jxr-visual");
        Directory.CreateDirectory(dir);
        var srcPng = Path.Combine(dir, "dockpanes-source.png");
        var oursJxr = Path.Combine(dir, "dockpanes-ours-ol1.jxr");
        var rtPng = Path.Combine(dir, "dockpanes-roundtrip.png");
        WritePng(srcPng, rgb, w, h);
        File.WriteAllBytes(oursJxr, jxr);
        WritePng(rtPng, rt, w, h);

        _out.WriteLine($"Artifacts ({w}x{h}, OL_ONE lossless):");
        _out.WriteLine($"  source    : {srcPng}");
        _out.WriteLine($"  our .jxr  : {oursJxr}   <-- open in Windows Photo");
        _out.WriteLine($"  roundtrip : {rtPng}");

        // Back the eyeball check with numbers: lossless round-trip + WIC accepts it.
        var diff = VisualJudge.CompareRgb24(rgb, rt, w, h, "jxr-visual-roundtrip");
        _out.WriteLine($"  round-trip {diff}");
        diff.Distortion.ShouldBe(0.0, "OL_ONE lossless round-trip must be pixel-identical");

        if (OperatingSystem.IsWindows())
        {
            var wic = WicOracle.Probe(oursJxr);
            if (wic.Available)
            {
                _out.WriteLine($"  WIC       : {wic.RawOutput}");
                wic.IsValidImage.ShouldBeTrue("WIC must accept our .jxr");
                wic.Width.ShouldBe(w);
                wic.Height.ShouldBe(h);
                wic.HasNonZeroPixels.ShouldBeTrue();
            }
        }
    }

    // ----------------------------------------------------------------- helpers

    private static void WritePng(string path, byte[] rgb, int w, int h)
    {
        var settings = new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "RGB");
        using var img = new MagickImage();
        img.ReadPixels(rgb, settings);
        img.Write(path, MagickFormat.Png);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return null;
    }
}
