using System.Diagnostics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Planar-alpha (32bppBGRA, jxrlib <c>-a 2</c>) conformance. Planar alpha is the default jxrlib alpha
/// mode: the colour is one codestream (YCoCg-R + YUV444) and the alpha is a second, self-contained
/// Y-only codestream stored alongside it in the container. Validated four ways: lossless self
/// round-trip; the colour AND alpha codestreams each byte-for-byte identical to what JxrEncApp emits;
/// our decode of JxrEncApp's RGBA file; and JxrDecApp's decode of ours. Tests no-op when the oracle
/// binaries aren't present.
/// </summary>
public sealed class JxrAlphaOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrAlphaOracleTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(48, 32, 0)]
    [InlineData(64, 48, 0)]
    [InlineData(80, 80, 1)]
    [InlineData(33, 40, 0)] // non-16-aligned
    public void Rgba32_SelfRoundTrip_Lossless(int w, int h, int overlap)
    {
        var (r, g, b, a) = RgbaPattern(w, h);
        var jxr = JxrImageCodec.EncodeRgba32(r, g, b, a, w, h, overlap: overlap);
        var (dw, dh, dr, dg, db, da) = JxrImageCodec.DecodeRgba32(jxr);
        (dw, dh).ShouldBe((w, h));
        for (var i = 0; i < w * h; i++)
        {
            dr[i].ShouldBe(r[i], $"R[{i}]"); dg[i].ShouldBe(g[i], $"G[{i}]");
            db[i].ShouldBe(b[i], $"B[{i}]"); da[i].ShouldBe(a[i], $"A[{i}] (alpha)");
        }
    }

    // The colour AND alpha codestreams must each be byte-for-byte identical to JxrEncApp's (-c 22 BGRA,
    // -a 2 planar). The container bytes differ (our IFD layout), so we compare the two extracted
    // codestreams, not the whole file.
    [Theory]
    [InlineData(48, 32, 0)]
    [InlineData(64, 48, 0)]
    [InlineData(80, 80, 1)]
    [InlineData(64, 48, 2)]
    [InlineData(33, 40, 0)]
    public void OurEncodeRgba32_CodestreamsMatchJxrlib(int w, int h, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping."); return; }

        var (r, g, b, a) = RgbaPattern(w, h);
        var ours = JxrImageCodec.EncodeRgba32(r, g, b, a, w, h, overlap: overlap);
        var ourFile = JxrContainer.Read(ours);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxra_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp32(bmpPath, w, h, r, g, b, a);
        try
        {
            var (exit, so, se) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 22 -a 2 -q 1 -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrEncApp must encode the BGRA BMP");

            var theirFile = JxrContainer.Read(File.ReadAllBytes(jxrPath));
            _out.WriteLine($"colour ours={ourFile.Codestream.Length} theirs={theirFile.Codestream.Length}; " +
                           $"alpha ours={ourFile.AlphaCodestream?.Length} theirs={theirFile.AlphaCodestream?.Length}");

            theirFile.AlphaCodestream.ShouldNotBeNull("JxrEncApp must emit a planar alpha codestream");
            ourFile.AlphaCodestream.ShouldNotBeNull();

            ourFile.Codestream.Length.ShouldBe(theirFile.Codestream.Length, $"colour codestream length (OL{overlap} {w}x{h})");
            for (var i = 0; i < ourFile.Codestream.Length; i++)
                ourFile.Codestream[i].ShouldBe(theirFile.Codestream[i], $"colour byte {i} (0x{i:X}) (OL{overlap} {w}x{h})");

            ourFile.AlphaCodestream!.Length.ShouldBe(theirFile.AlphaCodestream!.Length, $"alpha codestream length (OL{overlap} {w}x{h})");
            for (var i = 0; i < ourFile.AlphaCodestream.Length; i++)
                ourFile.AlphaCodestream[i].ShouldBe(theirFile.AlphaCodestream[i], $"alpha byte {i} (0x{i:X}) (OL{overlap} {w}x{h})");
        }
        finally { Cleanup(bmpPath, jxrPath); }
    }

    // Our planar-alpha file, decoded by the reference JxrDecApp (-c 22), returns the RGBA verbatim.
    [Theory]
    [InlineData(64, 48, 0)]
    [InlineData(80, 80, 1)]
    [InlineData(33, 40, 0)]
    public void OurEncodeRgba32_DecodedByJxrDecApp_IsLossless(int w, int h, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping."); return; }

        var (r, g, b, a) = RgbaPattern(w, h);
        var jxr = JxrImageCodec.EncodeRgba32(r, g, b, a, w, h, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxradec_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var bmpPath = tmp + ".bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\" -c 22");
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our planar-alpha file");

            var (dw, dh, dr, dg, db, da) = ReadBmp32(bmpPath);
            (dw, dh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}]"); dg[i].ShouldBe(g[i], $"G[{i}]");
                db[i].ShouldBe(b[i], $"B[{i}]"); da[i].ShouldBe(a[i], $"A[{i}] (alpha)");
            }
        }
        finally { Cleanup(jxrPath, bmpPath); }
    }

    // A planar-alpha file from the reference JxrEncApp must decode losslessly through our codec.
    [Theory]
    [InlineData(64, 48, 0)]
    [InlineData(80, 80, 1)]
    [InlineData(33, 40, 0)]
    public void JxrlibEncodeRgba32_DecodedByUs_IsLossless(int w, int h, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping."); return; }

        var (r, g, b, a) = RgbaPattern(w, h);
        var tmp = Path.Combine(Path.GetTempPath(), $"jxradecu_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp32(bmpPath, w, h, r, g, b, a);
        try
        {
            var (exit, so, se) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 22 -a 2 -q 1 -l {overlap} -f");
            exit.ShouldBe(0, "JxrEncApp must encode the BGRA BMP");

            var (dw, dh, dr, dg, db, da) = JxrImageCodec.DecodeRgba32(File.ReadAllBytes(jxrPath));
            (dw, dh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}]"); dg[i].ShouldBe(g[i], $"G[{i}]");
                db[i].ShouldBe(b[i], $"B[{i}]"); da[i].ShouldBe(a[i], $"A[{i}] (alpha)");
            }
        }
        finally { Cleanup(bmpPath, jxrPath); }
    }

    // ----------------------------------------------------------------- helpers

    private static (int[] r, int[] g, int[] b, int[] a) RgbaPattern(int w, int h)
    {
        var (r, g, b, a) = (new int[w * h], new int[w * h], new int[w * h], new int[w * h]);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = (x * 3 + y * 2) & 0xff;
                g[i] = (128 + x - y) & 0xff;
                b[i] = (x + y * 3) & 0xff;
                a[i] = (x * 2 + y * 5 + 17) & 0xff; // a non-trivial, non-opaque alpha ramp
            }
        return (r, g, b, a);
    }

    /// <summary>Write a 32bpp BGRA (B,G,R,A per pixel) bottom-up BMP — what <c>JxrEncApp -c 22</c> reads.</summary>
    private static void WriteBmp32(string path, int w, int h, int[] r, int[] g, int[] b, int[] a)
    {
        const int dataOffset = 14 + 40;
        int stride = w * 4; // 32bpp is always 4-aligned
        int dataSize = stride * h;
        var bytes = new byte[dataOffset + dataSize];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(dataOffset + dataSize).CopyTo(bytes, 2);
        BitConverter.GetBytes(dataOffset).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(w).CopyTo(bytes, 18);
        BitConverter.GetBytes(h).CopyTo(bytes, 22);   // positive ⇒ bottom-up
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)32).CopyTo(bytes, 28);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 34);
        for (var row = 0; row < h; row++)
        {
            int srcRow = h - 1 - row;
            int dst = dataOffset + row * stride;
            for (var x = 0; x < w; x++)
            {
                int i = srcRow * w + x, p = dst + x * 4;
                bytes[p + 0] = (byte)b[i]; bytes[p + 1] = (byte)g[i]; bytes[p + 2] = (byte)r[i]; bytes[p + 3] = (byte)a[i];
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Parse a 32bpp BGRA BMP (what <c>JxrDecApp -c 22</c> writes).</summary>
    private static (int w, int h, int[] r, int[] g, int[] b, int[] a) ReadBmp32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M') throw new InvalidDataException("Not a BMP");
        int dataOffset = BitConverter.ToInt32(bytes, 10);
        int width = BitConverter.ToInt32(bytes, 18);
        int height = BitConverter.ToInt32(bytes, 22);
        int bpp = BitConverter.ToInt16(bytes, 28);
        if (bpp != 32) throw new InvalidDataException($"Expected 32bpp BMP, got {bpp}");
        bool topDown = height < 0;
        int hh = Math.Abs(height);
        int stride = width * 4;
        var (r, g, b, a) = (new int[width * hh], new int[width * hh], new int[width * hh], new int[width * hh]);
        for (var row = 0; row < hh; row++)
        {
            int srcRow = topDown ? row : (hh - 1 - row);
            int src = dataOffset + srcRow * stride;
            for (var x = 0; x < width; x++)
            {
                int i = row * width + x, p = src + x * 4;
                b[i] = bytes[p + 0]; g[i] = bytes[p + 1]; r[i] = bytes[p + 2]; a[i] = bytes[p + 3];
            }
        }
        return (width, hh, r, g, b, a);
    }

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
