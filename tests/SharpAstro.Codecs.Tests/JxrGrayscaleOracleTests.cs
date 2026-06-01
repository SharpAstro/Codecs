using System.Diagnostics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8a — the bit-exact oracle for BD8 grayscale (Y-only). The gold standard is
/// the codestream byte-match: our encoder's output must be byte-for-byte identical to
/// what Microsoft jxrlib's reference <c>JxrEncApp</c> emits for the same 8bpp-gray
/// image and settings (<c>-c 2</c> forces CF_YONLY). We also check both decode
/// directions (our-encode → JxrDecApp pixels; JxrEncApp → our-decode pixels).
/// Tests no-op (pass) when the oracle binaries aren't present.
/// </summary>
public sealed class JxrGrayscaleOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrGrayscaleOracleTests(ITestOutputHelper output) => _out = output;

    // The strongest check: our entire Y-only codestream must equal JxrEncApp's byte-for-byte.
    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(32, 16, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 0)]
    [InlineData(272, 16, "gradient", 0)]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(80, 80, "gradient", 2)]
    // sub-MB / non-16-aligned dimensions: partial right/bottom macroblocks edge-replicated.
    [InlineData(17, 13, "gradient", 0)]
    [InlineData(33, 40, "random", 0)]
    [InlineData(100, 60, "gradient", 0)]
    [InlineData(17, 13, "gradient", 1)]
    [InlineData(33, 40, "random", 1)]
    [InlineData(100, 60, "gradient", 2)]
    [InlineData(1, 1, "flat", 0)]
    [InlineData(7, 1, "gradient", 0)]
    public void OurEncodeGray_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var y = JxrGrayscaleTests.Pattern(w, h, kind);
        var ours = JxrCodestream.EncodeGray(y, w, h, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxrg_cmp{overlap}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp8Gray(bmpPath, w, h, y);
        try
        {
            // -c 2 = 8bppGray input (forces CF_YONLY), -q 1 lossless, -l N overlap, -f spatial.
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 2 -q 1 -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the gray BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (gray OL{overlap} {kind} {w}x{h})");
            for (var i = 0; i < ours.Length; i++)
                ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (gray OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Grayscale lossy QP (BD8 Y-only, uniform index N): JxrEncApp -c 2 -q N (N ≥ 2) ⇒ scaled-arith
    // CF_YONLY at uniform QP index N. Exercises the scaled gray path (Y input <<3 + the DC iQP>>1
    // deadzone). Our codestream must be byte-for-byte identical to JxrEncApp.
    [Theory]
    [InlineData(16, 16, "gradient", 2, 0)]
    [InlineData(32, 32, "gradient", 5, 0)]
    [InlineData(48, 32, "random", 8, 0)]
    [InlineData(64, 48, "random", 16, 0)]
    [InlineData(80, 80, "random", 32, 0)]
    [InlineData(64, 48, "random", 48, 0)]
    [InlineData(48, 32, "random", 64, 0)]
    [InlineData(64, 48, "random", 5, 1)]   // OL_ONE
    [InlineData(80, 80, "random", 16, 1)]
    [InlineData(64, 48, "random", 8, 2)]   // OL_TWO
    [InlineData(48, 32, "random", 32, 2)]
    [InlineData(17, 13, "random", 5, 0)]   // non-16-aligned
    [InlineData(33, 40, "random", 16, 1)]
    public void OurEncodeGray_LossyQp_CodestreamMatchesJxrlib(int w, int h, string kind, int qp, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var y = JxrGrayscaleTests.Pattern(w, h, kind);
        var ours = JxrCodestream.EncodeGray(y, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxrg_q{qp}_{overlap}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp8Gray(bmpPath, w, h, y);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 2 -q {qp} -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the gray BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (gray QP{qp} OL{overlap} {kind} {w}x{h})");
            for (var i = 0; i < ours.Length; i++)
                ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (gray QP{qp} OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Grayscale lossy QP decode: our decode of our BD8 Y-only lossy file must agree with JxrDecApp's.
    [Theory]
    [InlineData(64, 48, "random", 8, 0)]
    [InlineData(80, 80, "random", 16, 1)]
    [InlineData(48, 32, "random", 32, 2)]
    [InlineData(33, 40, "random", 5, 0)]
    public void OurEncodeGray_LossyQp_DecodedByJxrDecApp(int w, int h, string kind, int qp, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrGrayscaleTests.Pattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGray8(y, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray8(jxr); // our decode of our lossy file

        var tmp = Path.Combine(Path.GetTempPath(), $"jxrg_qdec{qp}_{overlap}_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var bmpPath = tmp + ".bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, stdout, stderr) = Run(decApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\" -c 2");
            _out.WriteLine($"JxrDecApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrDecApp must decode our lossy gray file");

            var (rw, rh, ry) = ReadBmp8Gray(bmpPath);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(ry[i], $"Y[{i}] (gray QP{qp} OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
        }
    }

    // Multi-tile grayscale (SOFT tiles): our Y-only multi-tile codestream must be byte-for-byte
    // identical to JxrEncApp's (-c 2 = 8bppGray, -U rows cols). Proves the generalized multi-tile core
    // drives the single-channel path correctly (tiling header, INDEX_TABLE_TILES, per-tile entropy).
    [Theory]
    [InlineData(64, 32, "gradient", 0, 2, 1)]
    [InlineData(64, 64, "gradient", 0, 2, 2)]
    [InlineData(128, 64, "random", 0, 4, 2)]
    [InlineData(544, 16, "gradient", 0, 2, 1)]  // spans the 16-MB group boundary
    [InlineData(64, 32, "gradient", 1, 2, 1)]   // OL_ONE
    [InlineData(64, 64, "random", 1, 2, 2)]
    [InlineData(128, 64, "gradient", 2, 4, 2)]  // OL_TWO
    public void OurEncodeGray_Tiled_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap, int cols, int rows)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var y = JxrGrayscaleTests.Pattern(w, h, kind);
        int mbW = (w + 15) / 16, mbH = (h + 15) / 16;
        var layout = JxrTileLayout.Uniform(mbW, mbH, cols, rows);
        var ours = JxrImageCodec.EncodeGray8(y, w, h, overlap: overlap, tiles: layout);
        var oursCs = JxrContainer.Read(ours).Codestream;

        var tmp = Path.Combine(Path.GetTempPath(), $"jxrg_tile_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp8Gray(bmpPath, w, h, y);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 2 -q 1 -l {overlap} -U {rows} {cols} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the tiled gray BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={oursCs.Length} theirs={theirs.Length}");
            oursCs.Length.ShouldBe(theirs.Length, $"codestream length (gray tiled {cols}x{rows} OL{overlap} {kind} {w}x{h})");
            for (var i = 0; i < oursCs.Length; i++)
                oursCs[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (gray tiled {cols}x{rows} OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Multi-tile gray decode: a tiled gray .jxr from JxrEncApp must decode losslessly through our codec.
    [Theory]
    [InlineData(64, 64, "gradient", 0, 2, 2)]
    [InlineData(128, 64, "random", 0, 4, 2)]
    [InlineData(64, 64, "random", 1, 2, 2)]
    [InlineData(128, 64, "gradient", 2, 4, 2)]
    public void JxrlibEncodeGray_Tiled_DecodedByUs_IsLossless(int w, int h, string kind, int overlap, int cols, int rows)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var y = JxrGrayscaleTests.Pattern(w, h, kind);
        var tmp = Path.Combine(Path.GetTempPath(), $"jxrg_tdec_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp8Gray(bmpPath, w, h, y);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 2 -q 1 -l {overlap} -U {rows} {cols} -f");
            exit.ShouldBe(0, "JxrEncApp must encode the tiled gray BMP");

            var (dw, dh, dy) = JxrImageCodec.DecodeGray8(File.ReadAllBytes(jxrPath));
            (dw, dh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(y[i], $"Y[{i}] (gray tiled-decode {cols}x{rows} OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Decoder conformance: a lossless Y-only file from JxrEncApp must decode losslessly
    // through our container reader + DecodeGray (also proves we parse real jxrlib gray files).
    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(48, 32, "gradient", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "random", 0)]
    public void JxrlibEncodeGray_DecodedByUs_IsLossless(int w, int h, string kind, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var y = JxrGrayscaleTests.Pattern(w, h, kind);
        var tmp = Path.Combine(Path.GetTempPath(), $"jxrg_dec{overlap}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp8Gray(bmpPath, w, h, y);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 2 -q 1 -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the gray BMP");

            var (dw, dh, dy) = JxrImageCodec.DecodeGray8(File.ReadAllBytes(jxrPath));
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(y[i], $"Y[{i}] (gray OL{overlap} decode {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Encoder conformance: our Y-only file, decoded by the reference JxrDecApp, returns
    // pixel-identical 8bpp-gray output.
    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 1)]
    public void OurEncodeGray_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrGrayscaleTests.Pattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGray8(y, w, h, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxrg_oracle{overlap}_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var bmpPath = tmp + ".bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            // -c 2 = 8bppGray output BMP.
            var (exit, stdout, stderr) = Run(decApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\" -c 2");
            _out.WriteLine($"JxrDecApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrDecApp must decode our gray file");

            var (dw, dh, dy) = ReadBmp8Gray(bmpPath);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(y[i], $"Y[{i}] (gray OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            File.Delete(jxrPath);
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
        }
    }

    // ----------------------------------------------------------------- BMP 8bpp gray

    private const int Bmp8DataOffset = 14 + 40 + 256 * 4; // file header + info header + grayscale palette

    /// <summary>Write an 8bpp grayscale BMP with the identity gray palette jxrlib requires
    /// (<c>palette[i] == i|(i&lt;&lt;8)|(i&lt;&lt;16)</c>), bottom-up.</summary>
    private static void WriteBmp8Gray(string path, int w, int h, int[] y)
    {
        int stride = (w + 3) & ~3;
        int dataSize = stride * h;
        int fileSize = Bmp8DataOffset + dataSize;
        var bytes = new byte[fileSize];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bytes, 2);
        BitConverter.GetBytes(Bmp8DataOffset).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);     // BITMAPINFOHEADER size
        BitConverter.GetBytes(w).CopyTo(bytes, 18);
        BitConverter.GetBytes(h).CopyTo(bytes, 22);      // positive ⇒ bottom-up
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)8).CopyTo(bytes, 28);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 34);
        BitConverter.GetBytes(256).CopyTo(bytes, 46);    // biClrUsed
        for (var i = 0; i < 256; i++)
        {
            int p = 54 + i * 4;
            bytes[p + 0] = (byte)i; // B
            bytes[p + 1] = (byte)i; // G
            bytes[p + 2] = (byte)i; // R
            bytes[p + 3] = 0;       // reserved
        }
        for (var row = 0; row < h; row++)
        {
            int srcRow = h - 1 - row; // BMP rows run bottom-to-top
            int dst = Bmp8DataOffset + row * stride;
            for (var x = 0; x < w; x++)
                bytes[dst + x] = (byte)y[srcRow * w + x];
        }
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Parse an 8bpp grayscale BMP (what <c>JxrDecApp -c 2</c> writes).</summary>
    private static (int w, int h, int[] y) ReadBmp8Gray(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
            throw new InvalidDataException("Not a BMP");
        int dataOffset = BitConverter.ToInt32(bytes, 10);
        int width = BitConverter.ToInt32(bytes, 18);
        int height = BitConverter.ToInt32(bytes, 22);
        int bpp = BitConverter.ToInt16(bytes, 28);
        if (bpp != 8) throw new InvalidDataException($"Expected 8bpp BMP, got {bpp}");
        bool topDown = height < 0;
        int h = Math.Abs(height);
        int stride = (width + 3) & ~3;

        var y = new int[width * h];
        for (var row = 0; row < h; row++)
        {
            int srcRow = topDown ? row : (h - 1 - row);
            int src = dataOffset + srcRow * stride;
            for (var x = 0; x < width; x++)
                y[row * width + x] = bytes[src + x];
        }
        return (width, h, y);
    }

    // ----------------------------------------------------------------- process helpers

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
