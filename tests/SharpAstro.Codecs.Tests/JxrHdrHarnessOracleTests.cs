using System.Diagnostics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// HDR harness oracle — the directions that need jxrlib to <i>encode</i> from a strict
/// uncompressed TIFF we write (<see cref="HdrTiff"/>): the codestream <b>byte-for-byte</b>
/// match vs <c>JxrEncApp</c> (the gold standard, previously only BD8 had it) and the
/// <c>JxrEncApp</c>→our-decode direction (decoder conformance on real jxrlib HDR files).
/// Complements the per-rung our-encode→JxrDecApp oracles. No-ops when binaries are absent.
/// </summary>
public sealed class JxrHdrHarnessOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrHdrHarnessOracleTests(ITestOutputHelper output) => _out = output;

    // ---------------------------------------------------------------- BD16 integer

    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 1)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "random", 0)]
    [InlineData(17, 13, "gradient", 1)]
    public void Gray16_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var enc = FindOracle("JxrEncApp.exe"); if (enc is null) { Skip(); return; }
        var y = JxrBd16Tests.Pattern(w, h, kind, 7);
        var ours = JxrCodestream.EncodeGray(y, w, h, overlap: overlap, bd: JxrOutputBitDepth.Bd16);
        var theirs = Codestream(JxrlibEncode(enc, HdrTiff.Uint16Gray(w, h, y), $"-c 3 -q 1 -l {overlap} -f"));
        AssertBytesEqual(ours, theirs, $"gray16 OL{overlap} {kind} {w}x{h}");
    }

    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 1)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "random", 0)]
    public void Gray16_JxrlibEncode_DecodedByUs_IsLossless(int w, int h, string kind, int overlap)
    {
        var enc = FindOracle("JxrEncApp.exe"); if (enc is null) { Skip(); return; }
        var y = JxrBd16Tests.Pattern(w, h, kind, 7);
        var jxr = JxrlibEncode(enc, HdrTiff.Uint16Gray(w, h, y), $"-c 3 -q 1 -l {overlap} -f");
        var (_, _, dy) = JxrImageCodec.DecodeGray16(jxr);
        for (var i = 0; i < w * h; i++) dy[i].ShouldBe(y[i], $"Y[{i}]");
    }

    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 1)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "random", 0)]
    [InlineData(33, 40, "gradient", 1)]
    public void Rgb48_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var enc = FindOracle("JxrEncApp.exe"); if (enc is null) { Skip(); return; }
        var (r, g, b) = (JxrBd16Tests.Pattern(w, h, kind, 1), JxrBd16Tests.Pattern(w, h, kind, 2), JxrBd16Tests.Pattern(w, h, kind, 3));
        var ours = JxrCodestream.Encode(r, g, b, w, h, overlap: overlap, bd: JxrOutputBitDepth.Bd16);
        var theirs = Codestream(JxrlibEncode(enc, HdrTiff.Uint16Rgb(w, h, r, g, b), $"-c 10 -d 3 -q 1 -l {overlap} -f"));
        AssertBytesEqual(ours, theirs, $"rgb48 OL{overlap} {kind} {w}x{h}");
    }

    // ---------------------------------------------------------------- BD32F float

    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "hdr", 1)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient", 0)]
    [InlineData(33, 40, "hdr", 1)]
    public void GrayF32_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var enc = FindOracle("JxrEncApp.exe"); if (enc is null) { Skip(); return; }
        var y = JxrBd32FTests.Pattern(w, h, kind);
        // jxrlib BD32F defaults: lenMantissa 13, expBias 4 (strenc.c:1316; no -S/-C).
        var ours = JxrCodestream.EncodeGrayF32(y, w, h, 13, 4, overlap: overlap);
        var theirs = Codestream(JxrlibEncode(enc, HdrTiff.Float32Gray(w, h, y), $"-c 8 -q 1 -l {overlap} -f"));
        AssertBytesEqual(ours, theirs, $"f32 OL{overlap} {kind} {w}x{h}");
    }

    // ---------------------------------------------------------------- BD16F half

    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "hdr", 1)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient", 0)]
    [InlineData(33, 40, "hdr", 1)]
    public void GrayF16_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var enc = FindOracle("JxrEncApp.exe"); if (enc is null) { Skip(); return; }
        var y = JxrBd16FTests.GrayPattern(w, h, kind);
        var ours = JxrCodestream.EncodeGrayHalf(y, w, h, overlap: overlap);
        var theirs = Codestream(JxrlibEncode(enc, HdrTiff.HalfGray(w, h, y), $"-c 5 -q 1 -l {overlap} -f"));
        AssertBytesEqual(ours, theirs, $"f16 OL{overlap} {kind} {w}x{h}");
    }

    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "hdr", 1)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient", 0)]
    [InlineData(33, 40, "hdr", 1)]
    public void RgbF16_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var enc = FindOracle("JxrEncApp.exe"); if (enc is null) { Skip(); return; }
        var rgb = JxrBd16FTests.RgbPattern(w, h, kind);
        var (r, g, b) = Deinterleave(rgb, w * h);
        var ours = JxrCodestream.EncodeRgbHalf(r, g, b, w, h, overlap: overlap);
        var theirs = Codestream(JxrlibEncode(enc, HdrTiff.HalfRgb(w, h, rgb), $"-c 12 -d 3 -q 1 -l {overlap} -f"));
        AssertBytesEqual(ours, theirs, $"rgbf16 OL{overlap} {kind} {w}x{h}");
    }

    // ----------------------------------------------------------------- helpers

    private void Skip() => _out.WriteLine("JxrEncApp.exe not found — skipping oracle test.");

    /// <summary>Run JxrEncApp on a TIFF we wrote and return the full <c>.jxr</c> bytes.</summary>
    private byte[] JxrlibEncode(string encApp, byte[] tiff, string args)
    {
        var (tif, jxr) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(tif, tiff);
        try
        {
            var (exit, so, se) = Run(encApp, $"-i \"{tif}\" -o \"{jxr}\" {args}");
            _out.WriteLine($"JxrEncApp {args} exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrEncApp must encode the TIFF");
            return File.ReadAllBytes(jxr);
        }
        finally { Cleanup(tif, jxr); }
    }

    private static byte[] Codestream(byte[] jxr) => JxrContainer.Read(jxr).Codestream;

    private void AssertBytesEqual(byte[] ours, byte[] theirs, string ctx)
    {
        _out.WriteLine($"ours={ours.Length} theirs={theirs.Length} ({ctx})");
        int firstDiff = -1, lim = Math.Min(ours.Length, theirs.Length);
        for (var i = 0; i < lim; i++) if (ours[i] != theirs[i]) { firstDiff = i; break; }
        _out.WriteLine($"  ours [0..32]: {Convert.ToHexString(ours.AsSpan(0, Math.Min(32, ours.Length)))}");
        _out.WriteLine($"theirs [0..32]: {Convert.ToHexString(theirs.AsSpan(0, Math.Min(32, theirs.Length)))}");
        _out.WriteLine($"  first byte diff at: {firstDiff}");
        ours.Length.ShouldBe(theirs.Length, $"codestream length ({ctx})");
        for (var i = 0; i < ours.Length; i++)
            ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) ({ctx})");
    }

    private static (Half[] r, Half[] g, Half[] b) Deinterleave(Half[] rgb, int n)
    {
        var (r, g, b) = (new Half[n], new Half[n], new Half[n]);
        for (var i = 0; i < n; i++) { r[i] = rgb[i * 3]; g[i] = rgb[i * 3 + 1]; b[i] = rgb[i * 3 + 2]; }
        return (r, g, b);
    }

    private static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), $"jxrhdr_{Guid.NewGuid():N}{ext}");
    private static void Cleanup(params string[] paths) { foreach (var p in paths) if (File.Exists(p)) File.Delete(p); }

    private static (int exit, string stdout, string stderr) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, so, se);
    }

    private static string? FindOracle(string exe)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "Oracle", "bin", exe);
            if (File.Exists(direct)) return direct;
            var nested = Path.Combine(dir.FullName, "tests", "SharpAstro.Codecs.Tests", "Oracle", "bin", exe);
            if (File.Exists(nested)) return nested;
        }
        return null;
    }
}
