using System.Buffers.Binary;
using System.Diagnostics;
using SharpAstro.Jxr;
using SharpAstro.Tiff;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8b — oracle conformance for BD16 integer. Encoder conformance: our BD16 file,
/// decoded by the reference <c>JxrDecApp</c> to a 16-bit TIFF, must come back sample-
/// identical. This is the conformance proof that self round-trip can't give for BD16 —
/// a wrong luma bias or SHIFT_BITS field still round-trips symmetrically through our own
/// codec, but only a value that matches jxrlib survives a decode by the reference. (The
/// codestream byte-match + decode-of-jxrlib-files directions need a strict 16-bit TIFF
/// *writer* to feed JxrEncApp — that's the HDR test-harness task.)
/// Tests no-op (pass) when the oracle binary isn't present.
/// </summary>
public sealed class JxrBd16OracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrBd16OracleTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(64, 48, "random", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "random", 0)]
    public void OurEncodeGray16_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrBd16Tests.Pattern(w, h, kind, 7);
        var jxr = JxrImageCodec.EncodeGray16(y, w, h, overlap: overlap);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 3"); // 16bppGray
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our BD16 gray file");

            var (dw, dh, samples) = ReadTiffSamples(tif);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            samples.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
                samples[i].ShouldBe(y[i], $"Y[{i}] (gray16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(64, 48, "random", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "random", 0)]
    public void OurEncodeRgb48_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var r = JxrBd16Tests.Pattern(w, h, kind, 1);
        var g = JxrBd16Tests.Pattern(w, h, kind, 2);
        var b = JxrBd16Tests.Pattern(w, h, kind, 3);
        var jxr = JxrImageCodec.EncodeRgb48(r, g, b, w, h, overlap: overlap);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 10"); // 48bppRGB
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our BD16 RGB file");

            var (dw, dh, samples) = ReadTiffSamples(tif);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            samples.Length.ShouldBe(w * h * 3);
            for (var i = 0; i < w * h; i++)
            {
                samples[i * 3 + 0].ShouldBe(r[i], $"R[{i}] (rgb48 OL{overlap} {kind} {w}x{h})");
                samples[i * 3 + 1].ShouldBe(g[i], $"G[{i}] (rgb48 OL{overlap} {kind} {w}x{h})");
                samples[i * 3 + 2].ShouldBe(b[i], $"B[{i}] (rgb48 OL{overlap} {kind} {w}x{h})");
            }
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // BD16-integer lossy QP: scaled-arith uses a distinct store rounding ((1<<(s-1)), no -1) and the
    // <<nLen post-shift (nLen=0 here). Lossy but deterministic — our decode of our file must agree with
    // JxrDecApp's decode of it. (Encode-direction byte-match needs a strict 16-bit TIFF writer to feed
    // JxrEncApp; the quantize/entropy path is already byte-validated via BD8 444.)
    [Theory]
    [InlineData(48, 32, "gradient", 8, 0)]
    [InlineData(64, 48, "random", 16, 0)]
    [InlineData(80, 80, "random", 32, 1)]   // OL_ONE
    [InlineData(64, 48, "random", 16, 2)]   // OL_TWO
    [InlineData(33, 40, "random", 8, 0)]    // non-16-aligned
    public void OurEncodeGray16_LossyQp_DecodesLikeJxrDecApp(int w, int h, string kind, int qp, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrBd16Tests.Pattern(w, h, kind, 7);
        var jxr = JxrImageCodec.EncodeGray16(y, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray16(jxr); // our decode of our lossy file

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 3"); // 16bppGray
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our lossy BD16 gray file");

            var (rw, rh, samples) = ReadTiffSamples(tif);
            (dw, dh).ShouldBe((w, h));
            samples.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(samples[i], $"Y[{i}] (lossy QP{qp} gray16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    [Theory]
    [InlineData(48, 32, "gradient", 8, 0)]
    [InlineData(64, 48, "random", 16, 0)]
    [InlineData(80, 80, "random", 32, 1)]   // OL_ONE
    [InlineData(64, 48, "random", 16, 2)]   // OL_TWO
    [InlineData(33, 40, "random", 8, 0)]    // non-16-aligned
    public void OurEncodeRgb48_LossyQp_DecodesLikeJxrDecApp(int w, int h, string kind, int qp, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var r = JxrBd16Tests.Pattern(w, h, kind, 1);
        var g = JxrBd16Tests.Pattern(w, h, kind, 2);
        var b = JxrBd16Tests.Pattern(w, h, kind, 3);
        var jxr = JxrImageCodec.EncodeRgb48(r, g, b, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);
        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb48(jxr); // our decode of our lossy file

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 10"); // 48bppRGB
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our lossy BD16 RGB file");

            var (rw, rh, samples) = ReadTiffSamples(tif);
            (dw, dh).ShouldBe((w, h));
            samples.Length.ShouldBe(w * h * 3);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(samples[i * 3 + 0], $"R[{i}] (lossy QP{qp} rgb48 OL{overlap} {kind} {w}x{h})");
                dg[i].ShouldBe(samples[i * 3 + 1], $"G[{i}] (lossy QP{qp} rgb48 OL{overlap} {kind} {w}x{h})");
                db[i].ShouldBe(samples[i * 3 + 2], $"B[{i}] (lossy QP{qp} rgb48 OL{overlap} {kind} {w}x{h})");
            }
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // BD16-integer NO_FLEXBITS (the flexbits plane omitted ⇒ forces scaled-arith): exercises the same
    // BD16 scaled store as lossy QP, but at QP 0. Our decode of our file must agree with JxrDecApp's.
    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "random", 1)]   // OL_ONE
    [InlineData(64, 48, "random", 2)]   // OL_TWO
    [InlineData(33, 40, "random", 0)]   // non-16-aligned
    public void OurEncodeGray16_NoFlexBits_DecodesLikeJxrDecApp(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrBd16Tests.Pattern(w, h, kind, 7);
        var jxr = JxrImageCodec.EncodeGray16(y, w, h, overlap: overlap, noFlexBits: true);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray16(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 3"); // 16bppGray
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our NO_FLEXBITS BD16 gray file");

            var (rw, rh, samples) = ReadTiffSamples(tif);
            (dw, dh).ShouldBe((w, h));
            samples.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(samples[i], $"Y[{i}] (NoFlexBits gray16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    [Theory]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "random", 1)]   // OL_ONE
    [InlineData(64, 48, "random", 2)]   // OL_TWO
    [InlineData(33, 40, "random", 0)]   // non-16-aligned
    public void OurEncodeRgb48_NoFlexBits_DecodesLikeJxrDecApp(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var r = JxrBd16Tests.Pattern(w, h, kind, 1);
        var g = JxrBd16Tests.Pattern(w, h, kind, 2);
        var b = JxrBd16Tests.Pattern(w, h, kind, 3);
        var jxr = JxrImageCodec.EncodeRgb48(r, g, b, w, h, overlap: overlap, noFlexBits: true);
        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb48(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 10"); // 48bppRGB
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our NO_FLEXBITS BD16 RGB file");

            var (rw, rh, samples) = ReadTiffSamples(tif);
            (dw, dh).ShouldBe((w, h));
            samples.Length.ShouldBe(w * h * 3);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(samples[i * 3 + 0], $"R[{i}] (NoFlexBits rgb48 OL{overlap} {kind} {w}x{h})");
                dg[i].ShouldBe(samples[i * 3 + 1], $"G[{i}] (NoFlexBits rgb48 OL{overlap} {kind} {w}x{h})");
                db[i].ShouldBe(samples[i * 3 + 2], $"B[{i}] (NoFlexBits rgb48 OL{overlap} {kind} {w}x{h})");
            }
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // BD16-integer multi-tile (gray + RGB): our tiled file decoded by JxrDecApp must match our decode.
    [Theory]
    [InlineData(64, 64, "gradient", 0, 2, 2)]
    [InlineData(128, 64, "random", 0, 4, 2)]
    [InlineData(64, 64, "random", 1, 2, 2)]
    [InlineData(128, 64, "gradient", 2, 4, 2)]
    public void OurEncodeGray16_Tiled_DecodesLikeJxrDecApp(int w, int h, string kind, int overlap, int cols, int rows)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrBd16Tests.Pattern(w, h, kind, 7);
        int mbW = (w + 15) / 16, mbH = (h + 15) / 16;
        var layout = JxrTileLayout.Uniform(mbW, mbH, cols, rows);
        var jxr = JxrImageCodec.EncodeGray16(y, w, h, overlap: overlap, tiles: layout);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray16(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 3");
            exit.ShouldBe(0, "JxrDecApp must decode our tiled BD16 gray file");
            var (rw, rh, samples) = ReadTiffSamples(tif);
            (dw, dh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(samples[i], $"Y[{i}] (tiled {cols}x{rows} gray16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    [Theory]
    [InlineData(64, 64, "gradient", 0, 2, 2)]
    [InlineData(128, 64, "random", 0, 4, 2)]
    [InlineData(64, 64, "random", 1, 2, 2)]
    [InlineData(128, 64, "gradient", 2, 4, 2)]
    public void OurEncodeRgb48_Tiled_DecodesLikeJxrDecApp(int w, int h, string kind, int overlap, int cols, int rows)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var r = JxrBd16Tests.Pattern(w, h, kind, 1);
        var g = JxrBd16Tests.Pattern(w, h, kind, 2);
        var b = JxrBd16Tests.Pattern(w, h, kind, 3);
        int mbW = (w + 15) / 16, mbH = (h + 15) / 16;
        var layout = JxrTileLayout.Uniform(mbW, mbH, cols, rows);
        var jxr = JxrImageCodec.EncodeRgb48(r, g, b, w, h, overlap: overlap, tiles: layout);
        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb48(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 10");
            exit.ShouldBe(0, "JxrDecApp must decode our tiled BD16 RGB file");
            var (rw, rh, samples) = ReadTiffSamples(tif);
            (dw, dh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(samples[i * 3 + 0], $"R[{i}] (tiled {cols}x{rows} rgb48 OL{overlap} {kind} {w}x{h})");
                dg[i].ShouldBe(samples[i * 3 + 1], $"G[{i}]");
                db[i].ShouldBe(samples[i * 3 + 2], $"B[{i}]");
            }
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Read a 16-bit TIFF via SharpAstro.Tiff and return its samples as ints,
    /// honoring the file's byte order (Page.Pixels preserves on-disk sample bytes).</summary>
    private static (int w, int h, int[] samples) ReadTiffSamples(string path)
    {
        var page = TiffReader.Read(File.ReadAllBytes(path)).Pages[0];
        page.BitsPerSample.ShouldBe(16);
        var px = page.Pixels.AsSpan();
        int n = px.Length / 2;
        var samples = new int[n];
        for (var i = 0; i < n; i++)
            samples[i] = page.FileIsLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(i * 2, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(px.Slice(i * 2, 2));
        return (page.Width, page.Height, samples);
    }

    private static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), $"jxr16_{Guid.NewGuid():N}{ext}");

    private static void Cleanup(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) File.Delete(p);
    }

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
