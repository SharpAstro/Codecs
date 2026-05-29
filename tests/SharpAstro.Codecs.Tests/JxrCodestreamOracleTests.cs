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
