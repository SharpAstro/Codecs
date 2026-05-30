using System.Buffers.Binary;
using System.Diagnostics;
using SharpAstro.Jxr;
using SharpAstro.Tiff;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 8c — oracle conformance for BD32F float (the HDR headline). Encoder conformance:
/// our BD32F file, decoded by the reference <c>JxrDecApp</c> to a 32-bit float TIFF, must
/// reproduce the requantized floats bit-exact. Because the codec is lossless on the float-
/// pixel representation and jxrlib applies the same pixel→float mapping (LEN_MANTISSA /
/// EXP_BIAS read from our plane header), the reference output must equal
/// <see cref="FloatPixel.Requantize"/>(x) exactly — proving our float mapping + headers are
/// conformant (the codestream byte-match direction needs a strict float-TIFF writer to feed
/// JxrEncApp — that's the HDR test-harness task).
/// Tests no-op (pass) when the oracle binary isn't present.
/// </summary>
public sealed class JxrBd32FOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrBd32FOracleTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(16, 16, "gradient", 8, 0)]
    [InlineData(48, 32, "gradient", 8, 0)]
    [InlineData(64, 48, "hdr", 8, 0)]
    [InlineData(80, 80, "gradient", 13, 0)]
    [InlineData(64, 48, "hdr", 13, 0)]
    [InlineData(64, 48, "hdr", 8, 1)]       // overlap OL_ONE
    [InlineData(48, 32, "hdr", 10, 2)]      // overlap OL_TWO
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(33, 40, "hdr", 8, 0)]
    public void OurEncodeGrayF32_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int lenMantissa, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        const int expBias = 0;
        var y = JxrBd32FTests.Pattern(w, h, kind);
        var jxr = JxrImageCodec.EncodeGrayF32(y, w, h, lenMantissa, expBias, overlap: overlap);

        var (tif, jxrPath) = (TempPath(".tif"), TempPath(".jxr"));
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{tif}\" -c 8"); // 32bppGrayFloat
            _out.WriteLine($"JxrDecApp exit={exit}\n{so}\n{se}");
            exit.ShouldBe(0, "JxrDecApp must decode our BD32F file");

            var (dw, dh, floats) = ReadFloatTiff(tif);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            floats.Length.ShouldBe(w * h);
            for (var i = 0; i < w * h; i++)
            {
                float expected = FloatPixel.Requantize(y[i], expBias, lenMantissa);
                BitsOf(floats[i]).ShouldBe(BitsOf(expected), $"Y[{i}] (f32 OL{overlap} {kind} {w}x{h} lm{lenMantissa})");
            }
        }
        finally { Cleanup(jxrPath, tif); }
    }

    // ----------------------------------------------------------------- helpers

    private static (int w, int h, float[] floats) ReadFloatTiff(string path)
    {
        var page = TiffReader.Read(File.ReadAllBytes(path)).Pages[0];
        page.BitsPerSample.ShouldBe(32);
        page.SampleFormat.ShouldBe(TiffSampleFormat.IeeeFloat);
        var px = page.Pixels.AsSpan();
        int n = px.Length / 4;
        var floats = new float[n];
        for (var i = 0; i < n; i++)
        {
            int bits = page.FileIsLittleEndian
                ? BinaryPrimitives.ReadInt32LittleEndian(px.Slice(i * 4, 4))
                : BinaryPrimitives.ReadInt32BigEndian(px.Slice(i * 4, 4));
            floats[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return (page.Width, page.Height, floats);
    }

    private static int BitsOf(float f) => BitConverter.SingleToInt32Bits(f);
    private static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), $"jxrf32_{Guid.NewGuid():N}{ext}");
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
