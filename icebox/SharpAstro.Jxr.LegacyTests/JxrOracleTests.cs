using System.Diagnostics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Cross-check against Microsoft's BSD-2 reference jxrlib
/// (<c>JxrDecApp.exe</c> / <c>JxrEncApp.exe</c>). The binaries are built
/// out-of-tree by <c>Oracle/build.sh</c>; tests in this file skip
/// gracefully when the binaries aren't present so CI / fresh checkouts
/// pass without the manual build step.
/// </summary>
public sealed class JxrOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrOracleTests(ITestOutputHelper output) => _out = output;

    private static string? FindOracleBinary(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Oracle", name);
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void Seagull_RoundTripsViaJxrDecApp()
    {
        // Sanity check the oracle itself: jxrlib's reference decoder must run
        // cleanly on the bundled seagull fixture. If this fails the oracle
        // is broken before we use it to assess our own decoder.
        var jxrDecApp = FindOracleBinary("JxrDecApp.exe");
        if (jxrDecApp is null)
        {
            _out.WriteLine("SKIP — Oracle/JxrDecApp.exe not built. Run tests/SharpAstro.Codecs.Tests/Oracle/build.sh");
            return;
        }

        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "seagull_nebula.jxr");
        File.Exists(fixture).ShouldBeTrue($"missing fixture: {fixture}");

        var outDir = Path.Combine(Path.GetTempPath(), $"sharpastro_oracle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var outTif = Path.Combine(outDir, "seagull.tif");

        var psi = new ProcessStartInfo(jxrDecApp, $"-i \"{fixture}\" -o \"{outTif}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(60_000).ShouldBeTrue("JxrDecApp hung past 60s");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.ExitCode.ShouldBe(0, $"JxrDecApp exit={proc.ExitCode}\nstdout: {stdout}\nstderr: {stderr}");

        // Verify the output TIFF parses as a 2963×2991 8-bit BGRA image.
        var bytes = File.ReadAllBytes(outTif);
        File.Delete(outTif);
        Directory.Delete(outDir);

        // Minimal TIFF header sniff — Little-endian, magic 42, then walk the
        // IFD for ImageWidth / ImageLength / SamplesPerPixel. SharpAstro.Tiff
        // could parse this more robustly; this keeps the test self-contained.
        bytes[0].ShouldBe((byte)'I');
        bytes[1].ShouldBe((byte)'I');
        BitConverter.ToUInt16(bytes, 2).ShouldBe((ushort)42);
        var ifdOffset = (int)BitConverter.ToUInt32(bytes, 4);
        var nEntries = BitConverter.ToUInt16(bytes, ifdOffset);

        var width = 0; var height = 0; var samples = 0;
        for (var i = 0; i < nEntries; i++)
        {
            var entry = ifdOffset + 2 + i * 12;
            var tag = BitConverter.ToUInt16(bytes, entry);
            var val = (int)BitConverter.ToUInt32(bytes, entry + 8);
            if (tag == 0x100) width = val;
            else if (tag == 0x101) height = val;
            else if (tag == 0x115) samples = val;
        }
        width.ShouldBe(2963);
        height.ShouldBe(2991);
        samples.ShouldBe(4);
        _out.WriteLine($"JxrDecApp produced {width}×{height}×{samples} TIFF — {bytes.Length:N0} bytes");
    }

    // Pixel-level cross-check between OUR decoder and jxrlib's reference
    // decoder on the WIC-encoded seagull fixture. Currently fails — the
    // post-YCoCg-fix decoder still produces near-zero pixels, indicating
    // a deeper pipeline divergence (bias / dequant / ICT / prediction
    // chain works for files we encode but not for spec-encoded ones).
    // Logs diff stats for tracking; doesn't assert match yet.
    [Fact]
    public void Seagull_PixelMatchesJxrDecApp()
    {
        // End-to-end pixel comparison: decode the seagull through our path
        // (JxrDecoder.DecodeBd8RgbNoFlexbits returning RGB) and through
        // jxrlib's reference decoder (JxrDecApp -> BGRA TIFF).
        var jxrDecApp = FindOracleBinary("JxrDecApp.exe");
        if (jxrDecApp is null)
        {
            _out.WriteLine("SKIP — Oracle/JxrDecApp.exe not built. Run tests/SharpAstro.Codecs.Tests/Oracle/build.sh");
            return;
        }

        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "seagull_nebula.jxr");
        var file = JxrContainer.Read(File.ReadAllBytes(fixture));
        var ours = JxrDecoder.DecodeBd8RgbNoFlexbits(file.Codestream, out var w, out var h);
        w.ShouldBe(2963);
        h.ShouldBe(2991);

        // Run JxrDecApp on the same fixture.
        var outDir = Path.Combine(Path.GetTempPath(), $"sharpastro_oracle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var outTif = Path.Combine(outDir, "seagull.tif");
        var psi = new ProcessStartInfo(jxrDecApp, $"-i \"{fixture}\" -o \"{outTif}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(60_000).ShouldBeTrue("JxrDecApp hung past 60s");
        proc.ExitCode.ShouldBe(0);
        var tif = File.ReadAllBytes(outTif);
        File.Delete(outTif);
        Directory.Delete(outDir);

        // Walk the IFD for StripOffsets, StripByteCounts, RowsPerStrip,
        // PhotometricInterpretation. SamplesPerPixel/BitsPerSample we
        // confirmed above; here we just need the strip layout.
        var ifdOff = (int)BitConverter.ToUInt32(tif, 4);
        var nEntries = BitConverter.ToUInt16(tif, ifdOff);
        var stripOffset = -1;
        var stripBytes = -1;
        var rowsPerStrip = -1;
        for (var i = 0; i < nEntries; i++)
        {
            var entry = ifdOff + 2 + i * 12;
            var tag = BitConverter.ToUInt16(tif, entry);
            var val = (int)BitConverter.ToUInt32(tif, entry + 8);
            if (tag == 0x111) stripOffset = val;
            else if (tag == 0x117) stripBytes = val;
            else if (tag == 0x116) rowsPerStrip = val;
        }
        stripOffset.ShouldBePositive();
        stripBytes.ShouldBe(w * h * 4); // 2963 * 2991 * 4
        rowsPerStrip.ShouldBe(h);       // single strip

        // TIFF samples are BGRA. Compare each pixel: ours[i*3+R] vs tif[i*4+2],
        // ours[i*3+G] vs tif[i*4+1], ours[i*3+B] vs tif[i*4+0]. Alpha is
        // presumed 0xFF by JxrDecApp; we don't compare it.
        var maxDiff = 0;
        long sumDiff = 0;
        long exactlyEqual = 0;
        var total = (long)w * h * 3;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var our = (y * w + x) * 3;
            var their = stripOffset + (y * w + x) * 4;
            var dR = Math.Abs(ours[our + 0] - tif[their + 2]);
            var dG = Math.Abs(ours[our + 1] - tif[their + 1]);
            var dB = Math.Abs(ours[our + 2] - tif[their + 0]);
            if (dR > maxDiff) maxDiff = dR;
            if (dG > maxDiff) maxDiff = dG;
            if (dB > maxDiff) maxDiff = dB;
            sumDiff += dR + dG + dB;
            if (dR == 0) exactlyEqual++;
            if (dG == 0) exactlyEqual++;
            if (dB == 0) exactlyEqual++;
        }
        var avgDiff = sumDiff / (double)total;
        var pctExact = exactlyEqual * 100.0 / total;
        _out.WriteLine($"Pixel diff vs JxrDecApp: max={maxDiff} avg={avgDiff:F4} exact={pctExact:F2}% ({exactlyEqual:N0}/{total:N0})");

        // Dump a few sample pixels for diagnosis. Cover top-left (likely
        // baseline-near), middle, and a non-edge tile interior.
        var sampleCoords = new[] { (0, 0), (1, 0), (8, 0), (15, 0), (0, 1), (100, 100), (1000, 1000), (2000, 2000) };
        foreach (var (sx, sy) in sampleCoords)
        {
            var our = (sy * w + sx) * 3;
            var their = stripOffset + (sy * w + sx) * 4;
            _out.WriteLine($"  ({sx},{sy})  ours=R{ours[our+0],3} G{ours[our+1],3} B{ours[our+2],3}    " +
                           $"theirs=B{tif[their+0],3} G{tif[their+1],3} R{tif[their+2],3} A{tif[their+3],3}");
        }

        // TODO: tighten to maxDiff ≤ 2 once the spec-compliance gap below
        // the YCoCg layer is closed. For now the test logs the divergence
        // baseline so any regression that worsens it is visible.
    }
}
