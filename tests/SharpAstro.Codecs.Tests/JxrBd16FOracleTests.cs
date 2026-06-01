using System.Buffers.Binary;
using System.Diagnostics;
using SharpAstro.Jxr;
using SharpAstro.Tiff;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8d — oracle conformance for BD16F half. Encoder conformance: our BD16F file,
/// decoded by the reference <c>JxrDecApp</c> to a half TIFF (16bppGrayHalf / 48bppRGBHalf),
/// must reproduce the half bit patterns exactly. Because BD16F keeps the raw half bits and
/// the codec is lossless on them, jxrlib's output must equal the requantized input
/// (identity but for −0 → +0). Tests no-op (pass) when the oracle binary isn't present.
/// </summary>
public sealed class JxrBd16FOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrBd16FOracleTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "hdr", 0)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(64, 48, "hdr", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "hdr", 0)]
    public void OurEncodeGrayF16_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrBd16FTests.GrayPattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGrayF16(y, w, h, overlap: overlap);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 5"); // 16bppGrayHalf
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our BD16F gray file");

            var (dw, dh, bits) = ReadHalfTiff(tif);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            bits.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
                bits[i].ShouldBe(HBits(JxrBd16FTests.Expected(y[i])), $"Y[{i}] (f16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "hdr", 0)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(64, 48, "hdr", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "hdr", 0)]
    public void OurEncodeRgbF16_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var rgb = JxrBd16FTests.RgbPattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeRgbF16(rgb, w, h, overlap: overlap);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 12"); // 48bppRGBHalf
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our BD16F RGB file");

            var (dw, dh, bits) = ReadHalfTiff(tif);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            bits.Length.ShouldBe(w * h * 3);
            for (var i = 0; i < w * h * 3; i++)
                bits[i].ShouldBe(HBits(JxrBd16FTests.Expected(rgb[i])), $"RGB[{i}] (f16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // BD16F RGB with NO_FLEXBITS — the consumer's Save…NoFlexbits HDR-master mode for colour. Since
    // NO_FLEXBITS forces scaled-arithmetic for BD16F, this exercises the scaled-444 path on the half
    // pixel mapping. Lossy but deterministic: our decode of our file must agree bit-for-bit with the
    // reference JxrDecApp's decode of the same file.
    [Theory]
    [InlineData(64, 48, "hdr", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(80, 80, "hdr", 1)]   // OL_ONE
    [InlineData(64, 48, "hdr", 2)]   // OL_TWO
    [InlineData(33, 40, "hdr", 1)]   // non-16-aligned
    public void OurEncodeRgbF16_NoFlexBits_DecodesLikeJxrDecApp(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var rgb = JxrBd16FTests.RgbPattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeRgbF16(rgb, w, h, overlap: overlap, noFlexBits: true);
        var (dw, dh, ours) = JxrImageCodec.DecodeRgbF16(jxr); // our decode of our NO_FLEXBITS file

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 12"); // 48bppRGBHalf
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our NO_FLEXBITS BD16F RGB file");

            var (rw, rh, bits) = ReadHalfTiff(tif);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            ours.Length.ShouldBe(w * h * 3);
            bits.Length.ShouldBe(w * h * 3);
            for (var i = 0; i < w * h * 3; i++)
                HBits(ours[i]).ShouldBe(bits[i], $"RGB[{i}] (NoFlexBits f16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // BD16F gray + RGB with lossy QP: NO_FLEXBITS proved the scaled-444 half path; this adds the
    // reciprocal-multiply quantizer (incl. the half-step chroma DC/LP + DC iQP>>1 deadzone) on the
    // half pixel mapping. Lossy but deterministic — our decode must match JxrDecApp's, bit-for-bit.
    [Theory]
    [InlineData(64, 48, "hdr", 8, 0)]
    [InlineData(48, 32, "gradient", 16, 0)]
    [InlineData(80, 80, "hdr", 32, 1)]   // OL_ONE
    [InlineData(64, 48, "hdr", 16, 2)]   // OL_TWO
    [InlineData(33, 40, "hdr", 8, 1)]    // non-16-aligned
    public void OurEncodeGrayF16_LossyQp_DecodesLikeJxrDecApp(int w, int h, string kind, int qp, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var y = JxrBd16FTests.GrayPattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGrayF16(y, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);
        var (dw, dh, ours) = JxrImageCodec.DecodeGrayF16(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 5"); // 16bppGrayHalf
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our lossy BD16F gray file");

            var (rw, rh, bits) = ReadHalfTiff(tif);
            (dw, dh).ShouldBe((w, h));
            ours.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
                HBits(ours[i]).ShouldBe(bits[i], $"Y[{i}] (lossy QP{qp} f16 gray OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    [Theory]
    [InlineData(64, 48, "hdr", 8, 0)]
    [InlineData(48, 32, "gradient", 16, 0)]
    [InlineData(80, 80, "hdr", 32, 1)]   // OL_ONE
    [InlineData(64, 48, "hdr", 16, 2)]   // OL_TWO
    [InlineData(33, 40, "hdr", 8, 1)]    // non-16-aligned
    public void OurEncodeRgbF16_LossyQp_DecodesLikeJxrDecApp(int w, int h, string kind, int qp, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var rgb = JxrBd16FTests.RgbPattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeRgbF16(rgb, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);
        var (dw, dh, ours) = JxrImageCodec.DecodeRgbF16(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 12"); // 48bppRGBHalf
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our lossy BD16F RGB file");

            var (rw, rh, bits) = ReadHalfTiff(tif);
            (dw, dh).ShouldBe((w, h));
            ours.Length.ShouldBe(w * h * 3);
            bits.Length.ShouldBe(w * h * 3);
            for (var i = 0; i < w * h * 3; i++)
                HBits(ours[i]).ShouldBe(bits[i], $"RGB[{i}] (lossy QP{qp} f16 OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Read a 16-bit-sample TIFF and return its samples as raw 16-bit (half) bit patterns.</summary>
    private static (int w, int h, short[] bits) ReadHalfTiff(string path)
    {
        var page = TiffReader.Read(File.ReadAllBytes(path)).Pages[0];
        page.BitsPerSample.ShouldBe(16);
        var px = page.Pixels.AsSpan();
        int n = px.Length / 2;
        var bits = new short[n];
        for (var i = 0; i < n; i++)
            bits[i] = page.FileIsLittleEndian
                ? BinaryPrimitives.ReadInt16LittleEndian(px.Slice(i * 2, 2))
                : BinaryPrimitives.ReadInt16BigEndian(px.Slice(i * 2, 2));
        return (page.Width, page.Height, bits);
    }

    private static short HBits(Half h) => BitConverter.HalfToInt16Bits(h);
    private static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), $"jxrf16_{Guid.NewGuid():N}{ext}");
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
