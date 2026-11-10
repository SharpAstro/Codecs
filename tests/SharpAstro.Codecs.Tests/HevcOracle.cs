using System.Diagnostics;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// ffmpeg as the black-box oracle for <c>SharpAstro.Heif</c>'s HEVC decode: the item becomes an Annex B
/// stream (parameter sets from <c>hvcC</c>, then the item's NAL units, each behind a start code), ffmpeg's
/// native <c>hevc</c> decoder turns it into raw planar samples, and the test compares every plane EXACTLY.
/// HEVC decoding is bit-exact by specification, so there is no tolerance to choose.
/// <para>
/// <b>Binary only.</b> ffmpeg is LGPL/GPL and its source is never read; running it is fine, the line drawn
/// around jbig2dec, OpenJPEG and libwebp. Resolved from <c>FFMPEG</c>, then
/// <c>Oracle/heif/dist/bin</c> (<c>Oracle/heif/fetch.sh</c>), then <c>PATH</c> (CI: <c>apt-get install ffmpeg</c>).
/// </para>
/// </summary>
internal static class HevcOracle
{
    private const string HowToGet =
        "Install ffmpeg (apt-get install ffmpeg), run `bash tests/SharpAstro.Codecs.Tests/Oracle/heif/fetch.sh`, " +
        "or set FFMPEG to an ffmpeg executable.";

    private static readonly Lazy<(string? Path, string Reason)> Tool = new(Resolve);

    public static string RequireFfmpeg()
    {
        OracleGate.RequireOrSkip(Tool.Value.Path is not null, "ffmpeg", Tool.Value.Reason);
        return Tool.Value.Path!;
    }

    /// <summary>The item as an Annex B byte stream: hvcC's NAL units, then the item's, each after 00 00 00 01.</summary>
    public static byte[] AnnexB(HevcDecoderConfiguration configuration, ReadOnlySpan<byte> itemData)
    {
        var stream = new MemoryStream();
        byte[] startCode = [0, 0, 0, 1];
        foreach (var array in configuration.NalUnitArrays)
            foreach (var nal in array.NalUnits)
            {
                stream.Write(startCode);
                stream.Write(nal);
            }
        foreach (var nal in HevcNalUnit.SplitLengthPrefixed(itemData, configuration.LengthSize))
        {
            stream.Write(startCode);
            stream.Write(nal);
        }
        return stream.ToArray();
    }

    /// <summary>ffmpeg's decode of <paramref name="annexB"/> as planes, cropped by the conformance window as ffmpeg crops.</summary>
    public static (ushort[] Luma, ushort[] Cb, ushort[] Cr) Decode(byte[] annexB, HevcPicture shape) =>
        DecodeFrames(annexB, shape, 1)[0];

    /// <summary>
    /// ffmpeg's decode of an Annex B stream of <paramref name="frames"/> consecutive pictures that all have
    /// the shape of <paramref name="shape"/> (the pictures of a grid's tiles, each an IDR with its own copy of
    /// the parameter sets), one run for all of them: a 200 MP phone grid has 768 tiles and a process per tile
    /// would cost more than the decode. Timestamps are ignored (passthrough), so no frame is dropped or doubled.
    /// </summary>
    public static List<(ushort[] Luma, ushort[] Cb, ushort[] Cr)> DecodeFrames(byte[] annexB, HevcPicture shape, int frames)
    {
        var ffmpeg = RequireFfmpeg();
        var format = (shape.ChromaFormatIdc switch { 0 => "gray", 1 => "yuv420p", 2 => "yuv422p", _ => "yuv444p" })
                     + (shape.BitDepthLuma == 8 ? "" : $"{shape.BitDepthLuma}le");
        var bytesPerSample = shape.BitDepthLuma == 8 ? 1 : 2;

        var directory = Directory.CreateTempSubdirectory("hevc-oracle-");
        try
        {
            var input = Path.Combine(directory.FullName, "in.hevc");
            var output = Path.Combine(directory.FullName, "out.yuv");
            File.WriteAllBytes(input, annexB);
            var start = new ProcessStartInfo(ffmpeg)
            {
                ArgumentList = { "-hide_banner", "-v", "error", "-f", "hevc", "-i", input, "-fps_mode", "passthrough", "-frames:v", frames.ToString(), "-f", "rawvideo", "-pix_fmt", format, "-y", output },
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("ffmpeg did not finish decoding within two minutes.");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {errors.Result}");

            var raw = File.ReadAllBytes(output);
            var window = shape.ConformanceWindow;
            var width = shape.Width - window.Left - window.Right;
            var height = shape.Height - window.Top - window.Bottom;
            var (subW, subH) = shape.ChromaFormatIdc switch { 1 => (2, 2), 2 => (2, 1), _ => (1, 1) };
            var chromaSamples = shape.ChromaFormatIdc == 0 ? 0 : width / subW * (height / subH);
            var frameSamples = width * height + 2 * chromaSamples;
            var expected = (long)frames * frameSamples * bytesPerSample;
            if (raw.Length != expected)
                throw new InvalidOperationException($"ffmpeg wrote {raw.Length} bytes as {format}; {frames} frame(s) of {width}x{height} need {expected}.");

            var result = new List<(ushort[], ushort[], ushort[])>(frames);
            var samples = new ushort[frameSamples];
            for (var f = 0; f < frames; f++)
            {
                var offset = (long)f * frameSamples * bytesPerSample;
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = bytesPerSample == 1 ? raw[offset + i] : (ushort)(raw[offset + 2 * i] | raw[offset + 2 * i + 1] << 8);
                result.Add((samples[..(width * height)], samples[(width * height)..(width * height + chromaSamples)], samples[(width * height + chromaSamples)..]));
            }
            return result;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static (string? Path, string Reason) Resolve()
    {
        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var configured = Environment.GetEnvironmentVariable("FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured);
            return File.Exists(path) ? (path, "") : (null, $"FFMPEG is set to '{configured}', which does not exist. {HowToGet}");
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("Oracle", "heif", "dist", "bin"),
                         Path.Combine("tests", "SharpAstro.Codecs.Tests", "Oracle", "heif", "dist", "bin"),
                     })
            {
                var candidate = Path.Combine(directory.FullName, relative, executable);
                if (File.Exists(candidate)) return (candidate, "");
            }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry, executable);
            if (File.Exists(candidate)) return (candidate, "");
        }

        return (null, $"ffmpeg was not found via FFMPEG, Oracle/heif/dist/bin or PATH. {HowToGet}");
    }
}
