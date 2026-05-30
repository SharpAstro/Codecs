using System.Diagnostics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 7e (part 2) — the bit-exact oracle. Encodes an image with our codec,
/// writes a real <c>.jxr</c> file, and decodes it with Microsoft jxrlib's
/// reference <c>JxrDecApp.exe</c>. A lossless round-trip that comes back
/// pixel-identical proves our entire encode pipeline (container + codestream
/// headers + per-MB band entropy coding) is conformant with the reference
/// decoder — the milestone the whole re-port has been building toward.
/// Tests no-op (pass) when the oracle binary isn't present.
/// </summary>
public sealed class JxrCodestreamOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrCodestreamOracleTests(ITestOutputHelper output) => _out = output;

    // Encodes with our codec and decodes with jxrlib's reference JxrDecApp; a
    // lossless round-trip that returns pixel-identical proves the whole encode
    // pipeline — container + codestream headers + per-MB band entropy coding +
    // cross-MB DC/AD/CBP prediction + the m_bResetContext / m_bResetRGITotals
    // adaptive-state timing + signal transform + colour — is bit-exact conformant.
    [Theory]
    [InlineData(16, 16, "flat")]
    [InlineData(16, 16, "gradient")]
    [InlineData(32, 16, "gradient")]
    [InlineData(48, 32, "gradient")]
    [InlineData(64, 48, "gradient")]
    [InlineData(64, 48, "random")]
    [InlineData(96, 64, "random")]
    [InlineData(80, 80, "gradient")]
    [InlineData(272, 16, "gradient")] // spans a 16-MB group boundary (mbX wraps 0..16,17)
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient")]
    [InlineData(33, 40, "random")]
    [InlineData(100, 60, "gradient")]
    public void OurEncode_DecodedByJxrDecApp_IsLossless(int w, int h, string kind)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "flat" => Flat(w, h, 100, 150, 200),
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_oracle_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var bmpPath = tmp + ".bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, stdout, stderr) = Run(decApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrDecApp must decode our file");

            var (dw, dh, dr, dg, db) = ReadBmp24(bmpPath);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}] ({kind} {w}x{h})");
                dg[i].ShouldBe(g[i], $"G[{i}] ({kind} {w}x{h})");
                db[i].ShouldBe(b[i], $"B[{i}] ({kind} {w}x{h})");
            }
        }
        finally
        {
            File.Delete(jxrPath);
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
        }
    }

    // Rung 7f.2/7f.3 — Photo Overlap encoder conformance: our overlap-on encode (OL_ONE
    // = jxrlib's default, OL_TWO), decoded by the reference JxrDecApp, must come back lossless.
    [Theory]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(32, 16, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(272, 16, "gradient", 1)]
    [InlineData(16, 16, "gradient", 2)]
    [InlineData(32, 16, "gradient", 2)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(64, 48, "gradient", 2)]
    [InlineData(64, 48, "random", 2)]
    [InlineData(80, 80, "gradient", 2)]
    [InlineData(272, 16, "gradient", 2)]
    public void OurEncode_Overlap_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_ol{overlap}_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var bmpPath = tmp + ".bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, stdout, stderr) = Run(decApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrDecApp must decode our OL_ONE file");

            var (dw, dh, dr, dg, db) = ReadBmp24(bmpPath);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}] (OL{overlap} {kind} {w}x{h})");
                dg[i].ShouldBe(g[i], $"G[{i}] (OL{overlap} {kind} {w}x{h})");
                db[i].ShouldBe(b[i], $"B[{i}] (OL{overlap} {kind} {w}x{h})");
            }
        }
        finally
        {
            File.Delete(jxrPath);
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
        }
    }

    // Rung 7f.2/7f.3 — Photo Overlap decoder conformance: a lossless spatial OL_ONE/OL_TWO
    // YUV444 file produced by the reference JxrEncApp must decode losslessly through our
    // container reader + codestream decoder (the inverse overlap _alternate operators).
    [Theory]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(32, 16, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(16, 16, "gradient", 2)]
    [InlineData(32, 16, "gradient", 2)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(64, 48, "gradient", 2)]
    [InlineData(64, 48, "random", 2)]
    [InlineData(80, 80, "gradient", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient", 1)]
    public void JxrlibEncode_Overlap_DecodedByUs_IsLossless(int w, int h, string kind, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_dec{overlap}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            // -f spatial, -l N overlap level, -d 3 YUV444, -q 1 lossless, -c 0 24bppBGR.
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the BMP");

            var jxr = File.ReadAllBytes(jxrPath);
            var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}] (OL{overlap} decode {kind} {w}x{h})");
                dg[i].ShouldBe(g[i], $"G[{i}] (OL{overlap} decode {kind} {w}x{h})");
                db[i].ShouldBe(b[i], $"B[{i}] (OL{overlap} decode {kind} {w}x{h})");
            }
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Rung 7f.2/7f.3 — the strongest overlap check: our entire codestream must be byte-for-byte
    // identical to what the reference JxrEncApp emits for the same image and settings.
    [Theory]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(32, 16, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(16, 16, "gradient", 2)]
    [InlineData(32, 16, "gradient", 2)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(64, 48, "random", 2)]
    [InlineData(80, 80, "gradient", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient", 1)]
    [InlineData(33, 40, "random", 1)]
    [InlineData(100, 60, "gradient", 2)]
    public void OurEncode_Overlap_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };

        var ours = JxrCodestream.Encode(r, g, b, w, h, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_cmp{overlap}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (OL{overlap} {kind} {w}x{h})");
            for (var i = 0; i < ours.Length; i++)
                ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    /// <summary>
    /// Rung 7e.4 — the Windows-Photo milestone: our encoded <c>.jxr</c> must open in
    /// WIC (<c>System.Windows.Media.Imaging.BitmapDecoder</c>, what Windows Photo /
    /// Microsoft Photos use), instantiate a frame at the right dimensions, and decode
    /// to non-zero pixels (WIC silently yields all-zero pixels on some malformed
    /// codestreams, so Frames>0 alone isn't enough). Windows-only; no-ops elsewhere.
    /// </summary>
    [Theory]
    [InlineData(16, 16)]
    [InlineData(48, 32)]
    [InlineData(64, 48)]
    public void OurEncode_OpensInWic(int w, int h)
    {
        if (!OperatingSystem.IsWindows()) { _out.WriteLine("Not Windows — skipping WIC test."); return; }

        var (r, g, b) = Gradient(w, h);
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h);
        var jxrPath = Path.Combine(Path.GetTempPath(), $"jxr_wic_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var wic = WicOracle.Probe(jxrPath);
            if (!wic.Available) { _out.WriteLine($"WIC unavailable — skipping. {wic.RawOutput}"); return; }
            _out.WriteLine(wic.RawOutput);

            wic.IsValidImage.ShouldBeTrue($"WIC must accept our .jxr ({w}x{h}); error: {wic.Error}");
            wic.Width.ShouldBe(w);
            wic.Height.ShouldBe(h);
            wic.HasNonZeroPixels.ShouldBeTrue("WIC must decode to non-zero pixels (not a silent all-zero decode)");
        }
        finally
        {
            File.Delete(jxrPath);
        }
    }

    // ----------------------------------------------------------------- helpers

    private static (int[] r, int[] g, int[] b) Random(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var r = new int[w * h]; var g = new int[w * h]; var b = new int[w * h];
        for (var i = 0; i < w * h; i++) { r[i] = rng.Next(256); g[i] = rng.Next(256); b[i] = rng.Next(256); }
        return (r, g, b);
    }

    private static (int[] r, int[] g, int[] b) Flat(int w, int h, int r, int g, int b)
    {
        var ra = new int[w * h]; var ga = new int[w * h]; var ba = new int[w * h];
        Array.Fill(ra, r); Array.Fill(ga, g); Array.Fill(ba, b);
        return (ra, ga, ba);
    }

    private static (int[] r, int[] g, int[] b) Gradient(int w, int h)
    {
        var r = new int[w * h]; var g = new int[w * h]; var b = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = (x * 3 + y * 2) & 0xff;
                g[i] = (128 + x - y) & 0xff;
                b[i] = (x + y * 3) & 0xff;
            }
        return (r, g, b);
    }

    /// <summary>Write a 24bpp BGR bottom-up BMP (what <c>JxrEncApp -c 0</c> reads).</summary>
    private static void WriteBmp24(string path, int w, int h, int[] r, int[] g, int[] b)
    {
        int stride = (w * 3 + 3) & ~3;
        int dataSize = stride * h;
        int fileSize = 54 + dataSize;
        var bytes = new byte[fileSize];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);     // pixel-data offset
        BitConverter.GetBytes(40).CopyTo(bytes, 14);     // BITMAPINFOHEADER size
        BitConverter.GetBytes(w).CopyTo(bytes, 18);
        BitConverter.GetBytes(h).CopyTo(bytes, 22);      // positive ⇒ bottom-up
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 34);
        for (var row = 0; row < h; row++)
        {
            int srcRow = h - 1 - row; // BMP rows run bottom-to-top
            int dst = 54 + row * stride;
            for (var x = 0; x < w; x++)
            {
                int i = srcRow * w + x;
                bytes[dst + x * 3 + 0] = (byte)b[i];
                bytes[dst + x * 3 + 1] = (byte)g[i];
                bytes[dst + x * 3 + 2] = (byte)r[i];
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Parse a 24bpp BGR bottom-up BMP (what <c>JxrDecApp -c 0</c> writes).</summary>
    private static (int w, int h, int[] r, int[] g, int[] b) ReadBmp24(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
            throw new InvalidDataException("Not a BMP");
        int dataOffset = BitConverter.ToInt32(bytes, 10);
        int width = BitConverter.ToInt32(bytes, 18);
        int height = BitConverter.ToInt32(bytes, 22);
        int bpp = BitConverter.ToInt16(bytes, 28);
        if (bpp != 24) throw new InvalidDataException($"Expected 24bpp BMP, got {bpp}");
        bool topDown = height < 0;
        int h = Math.Abs(height);
        int stride = (width * 3 + 3) & ~3;

        var r = new int[width * h]; var g = new int[width * h]; var b = new int[width * h];
        for (var row = 0; row < h; row++)
        {
            int srcRow = topDown ? row : (h - 1 - row);
            int src = dataOffset + srcRow * stride;
            for (var x = 0; x < width; x++)
            {
                int p = src + x * 3;
                int i = row * width + x;
                b[i] = bytes[p];
                g[i] = bytes[p + 1];
                r[i] = bytes[p + 2];
            }
        }
        return (width, h, r, g, b);
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

    /// <summary>Walk up from the test output directory to find Oracle/bin/&lt;exe&gt;.</summary>
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
