using System.Diagnostics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Encode a known pixel pattern via JxrEncoder, decode via jxrlib's
/// JxrDecApp, then compare. Probes whether our encoder's spec compliance
/// is sufficient for the reference decoder, separately from whether our
/// decoder correctly interprets WIC's output.
/// </summary>
public sealed class JxrEncoderOracleProbe
{
    private readonly ITestOutputHelper _out;
    public JxrEncoderOracleProbe(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData((byte)100, (byte)150, (byte)200)]   // user's original probe — would fail with the broken YCoCg-R
    [InlineData((byte)47, (byte)20, (byte)49)]      // seagull (0,0) pattern
    [InlineData((byte)200, (byte)100, (byte)50)]    // warm tones
    [InlineData((byte)128, (byte)128, (byte)128)]   // neutral gray
    public void OurEncoder_DecodedByJxrDecApp_MatchesInput(byte r, byte g, byte b)
    {
        var jxrDecApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrDecApp.exe");
        if (!File.Exists(jxrDecApp))
        {
            _out.WriteLine($"SKIP — {jxrDecApp} not built. Run tests/SharpAstro.Codecs.Tests/Oracle/build.sh");
            return;
        }

        // Solid-colour 32×32 RGB. Lossless quant + YUV444 internal — the
        // reference decoder must reconstruct the exact source colour. This
        // is the spec-compliance guard for our encoder + the YCoCg colour
        // conversion: any drift here means files we produce won't decode
        // through WIC / jxrlib.
        const int w = 32, h = 32;
        var pixels = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 3 + 0] = r;
            pixels[i * 3 + 1] = g;
            pixels[i * 3 + 2] = b;
        }

        var jxr = JxrFileFormatter.SaveBd8RgbNoFlexbits(pixels, w, h, useYUV444: true);
        var jxrPath = Path.Combine(Path.GetTempPath(), $"oracle_probe_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(jxrPath, jxr);
        var bmpPath = jxrPath.Replace(".jxr", ".bmp");

        try
        {
            var psi = new ProcessStartInfo(jxrDecApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(30_000).ShouldBeTrue("JxrDecApp hung past 30s");
            proc.ExitCode.ShouldBe(0);

            File.Exists(bmpPath).ShouldBeTrue("JxrDecApp did not produce output");
            var bmp = File.ReadAllBytes(bmpPath);
            bmp[0].ShouldBe((byte)'B'); bmp[1].ShouldBe((byte)'M');
            var pxOff = BitConverter.ToInt32(bmp, 10);
            var stride = (w * 3 + 3) & ~3;

            // BMP is bottom-up BGR. Sample the center pixel.
            var centerInBmp = pxOff + (h - 1 - h / 2) * stride + (w / 2) * 3;
            var actualB = bmp[centerInBmp];
            var actualG = bmp[centerInBmp + 1];
            var actualR = bmp[centerInBmp + 2];
            _out.WriteLine($"Source RGB=({r},{g},{b}) → JxrDecApp BGR=({actualB},{actualG},{actualR})");

            // Lossless path: bit-exact. Any drift here is a bug, not rounding.
            actualR.ShouldBe(r, $"R channel diverges — encoder/spec mismatch");
            actualG.ShouldBe(g, $"G channel diverges — encoder/spec mismatch");
            actualB.ShouldBe(b, $"B channel diverges — encoder/spec mismatch");
        }
        finally
        {
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
        }
    }
}
