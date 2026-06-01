using System.Buffers.Binary;
using System.Diagnostics;
using SharpAstro.Jxr;
using SharpAstro.Tiff;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Signed-integer grayscale conformance — BD16S (16bppGrayFixedPoint, BITPIX 16) and BD32S
/// (32bppGrayFixedPoint, BITPIX 32), the native FITS signed formats. The signed paths use no
/// level bias (samples are centred at 0) and a signed output clip. Validated two ways: lossless
/// self round-trip (our encode → our decode is exact), and oracle round-trip — JxrDecApp's decode
/// of our file must equal our own decode (proving the bitstream is conformant and our signed
/// store matches jxrlib). The codestream byte-match direction needs a strict signed-TIFF writer to
/// feed JxrEncApp, so it is out of scope here (same tier as BD16 / BD16F / BD32F).
/// Tests no-op (pass) when the oracle binary isn't present.
/// </summary>
public sealed class JxrSignedOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrSignedOracleTests(ITestOutputHelper output) => _out = output;

    // ---- self round-trip (lossless): our encode → our decode is identity ------------------------

    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(48, 32, "random", 2)]
    [InlineData(33, 40, "random", 0)] // non-16-aligned
    public void Gray16S_SelfRoundTrip_Lossless(int w, int h, string kind, int overlap)
    {
        var y = SignedPattern(w, h, kind, 16);
        var jxr = JxrImageCodec.EncodeGray16S(y, w, h, overlap: overlap);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray16S(jxr);
        (dw, dh).ShouldBe((w, h));
        for (var i = 0; i < w * h; i++)
            dy[i].ShouldBe(y[i], $"Y[{i}] (bd16s self OL{overlap} {kind} {w}x{h})");
    }

    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(48, 32, "random", 2)]
    [InlineData(33, 40, "random", 0)] // non-16-aligned
    public void Gray32S_SelfRoundTrip_Lossless(int w, int h, string kind, int overlap)
    {
        var y = SignedPattern(w, h, kind, 24); // BITPIX 32, values within int24 (the FITS-realistic range)
        var jxr = JxrImageCodec.EncodeGray32S(y, w, h, overlap: overlap);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray32S(jxr);
        (dw, dh).ShouldBe((w, h));
        for (var i = 0; i < w * h; i++)
            dy[i].ShouldBe(y[i], $"Y[{i}] (bd32s self OL{overlap} {kind} {w}x{h})");
    }

    // ---- oracle round-trip: JxrDecApp's decode of our file == our decode ------------------------

    [Theory]
    [InlineData(16, 16, "gradient", 0, 0)]
    [InlineData(64, 48, "random", 0, 0)]
    [InlineData(80, 80, "gradient", 0, 1)]
    [InlineData(48, 32, "random", 0, 2)]
    [InlineData(33, 40, "random", 0, 0)] // non-16-aligned
    [InlineData(64, 48, "random", 16, 0)] // lossy QP
    [InlineData(80, 80, "random", 16, 1)]
    public void Gray16S_DecodesLikeJxrDecApp(int w, int h, string kind, int qp, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping."); return; }

        var y = SignedPattern(w, h, kind, 16);
        var jxr = JxrImageCodec.EncodeGray16S(y, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray16S(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 4"); // 16bppGrayFixedPoint
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our BD16S file");

            var (rw, rh, samples) = ReadSignedTiff(tif);
            (dw, dh).ShouldBe((w, h));
            samples.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(samples[i], $"Y[{i}] (bd16s QP{qp} OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    [Theory]
    [InlineData(16, 16, "gradient", 0, 0)]
    [InlineData(64, 48, "random", 0, 0)]
    [InlineData(80, 80, "gradient", 0, 1)]
    [InlineData(48, 32, "random", 0, 2)]
    [InlineData(33, 40, "random", 0, 0)] // non-16-aligned
    [InlineData(64, 48, "random", 16, 0)] // lossy QP (BD32S stays non-scaled)
    public void Gray32S_DecodesLikeJxrDecApp(int w, int h, string kind, int qp, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping."); return; }

        var y = SignedPattern(w, h, kind, 24);
        var jxr = JxrImageCodec.EncodeGray32S(y, w, h, qpDc: qp, qpLp: qp, qpHp: qp, overlap: overlap);
        var (dw, dh, dy) = JxrImageCodec.DecodeGray32S(jxr);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 7"); // 32bppGrayFixedPoint
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our BD32S file");

            var (rw, rh, samples) = ReadSignedTiff(tif);
            (dw, dh).ShouldBe((w, h));
            samples.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
                dy[i].ShouldBe(samples[i], $"Y[{i}] (bd32s QP{qp} OL{overlap} {kind} {w}x{h})");
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Signed test pattern with values in roughly [-2^(bits-1), 2^(bits-1)).</summary>
    private static int[] SignedPattern(int w, int h, string kind, int bits)
    {
        int amp = 1 << (bits - 1);
        var y = new int[w * h];
        if (kind == "random")
        {
            uint s = 0x1234567u ^ (uint)(w * 7 + h * 13 + bits);
            for (var i = 0; i < y.Length; i++)
            {
                s = s * 1664525u + 1013904223u;
                y[i] = (int)(s % (uint)(2 * amp)) - amp; // [-amp, amp)
            }
        }
        else // gradient (signed ramp, mixes positive and negative)
        {
            for (var py = 0; py < h; py++)
                for (var px = 0; px < w; px++)
                    y[py * w + px] = (int)(((long)(px + py) * (2 * amp) / (w + h)) - amp);
        }
        return y;
    }

    /// <summary>Read a signed 16/32-bit TIFF and return its samples as ints (sign-extended).</summary>
    private static (int w, int h, int[] samples) ReadSignedTiff(string path)
    {
        var page = TiffReader.Read(File.ReadAllBytes(path)).Pages[0];
        var px = page.Pixels.AsSpan();
        int bytesPer = page.BitsPerSample / 8;
        int n = px.Length / bytesPer;
        var samples = new int[n];
        for (var i = 0; i < n; i++)
            samples[i] = page.BitsPerSample == 16
                ? (page.FileIsLittleEndian
                    ? BinaryPrimitives.ReadInt16LittleEndian(px.Slice(i * 2, 2))
                    : BinaryPrimitives.ReadInt16BigEndian(px.Slice(i * 2, 2)))
                : (page.FileIsLittleEndian
                    ? BinaryPrimitives.ReadInt32LittleEndian(px.Slice(i * 4, 4))
                    : BinaryPrimitives.ReadInt32BigEndian(px.Slice(i * 4, 4)));
        return (page.Width, page.Height, samples);
    }

    private static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), $"jxrS_{Guid.NewGuid():N}{ext}");
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
