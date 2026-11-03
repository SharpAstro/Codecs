using System.Diagnostics;
using System.Text;
using SharpAstro.Webp;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// <c>--webp-encode-report</c>: for each corpus image and each 1080p benchmark input, our file size
/// and encode time at every <see cref="WebpEncodeOptions.Effort"/>, against <c>cwebp -lossless</c>
/// at its default (<c>-z 6</c>) and at <c>-z 9</c> (a black-box process, found through
/// <c>CWEBP</c> or <c>Oracle/webp/bin</c> or PATH), with its wall time. Every output, ours and
/// cwebp's, is decoded by <see cref="WebpDecoder"/> and by <c>dwebp</c> and must equal the source
/// RGBA exactly, or the report says so and exits non-zero.
/// <para>
/// <c>cwebp</c> is given <c>-exact</c>: ours keeps the colour under fully transparent pixels, so
/// without it cwebp would be allowed to throw that colour away and the sizes would not be of the
/// same thing. <c>--efforts=0,3,6,9</c> restricts the efforts; <c>--only=name[,name]</c> restricts
/// the images; <c>--no-corpus</c> and <c>--no-1080p</c> drop a group; <c>--out=path</c> also writes the two tables to a file.
/// </para>
/// <para>
/// <c>cwebp</c> and <c>dwebp</c> are local executables; temporary files go to the system
/// temp directory and are removed.
/// </para>
/// </summary>
internal static class WebpEncodeReport
{
    private sealed record Cwebp(string Label, long Size, double Milliseconds, bool Exact);

    public static int Run(string[] args)
    {
        int[] efforts = Enumerable.Range(0, 10).ToArray();
        string[]? only = null;
        bool corpus = true, big = true;
        string? outPath = null;
        foreach (string arg in args.Skip(1))
        {
            if (arg.StartsWith("--efforts=", StringComparison.Ordinal))
            {
                efforts = arg["--efforts=".Length..].Split(',').Select(int.Parse).ToArray();
            }
            else if (arg.StartsWith("--only=", StringComparison.Ordinal))
            {
                only = arg["--only=".Length..].Split(',');
            }
            else if (arg.StartsWith("--out=", StringComparison.Ordinal))
            {
                outPath = arg["--out=".Length..];
            }
            else if (arg == "--no-corpus")
            {
                corpus = false;
            }
            else if (arg == "--no-1080p")
            {
                big = false;
            }
            else
            {
                Console.Error.WriteLine($"unknown option {arg}");
                return 2;
            }
        }

        string? cwebp = ResolveTool("CWEBP", "cwebp");
        string? dwebp = ResolveTool("DWEBP", "dwebp");
        if (cwebp is null)
        {
            Console.Error.WriteLine("cwebp not found: set CWEBP, put it in tests/SharpAstro.Codecs.Tests/Oracle/webp/bin (Oracle/webp/fetch.sh) or on PATH.");
            return 2;
        }

        Console.WriteLine($"cwebp: {cwebp} ({Run(cwebp, "-version").Output.Split('\n')[0].Trim()})");
        Console.WriteLine($"dwebp: {dwebp ?? "(not found: only our own decoder checks the outputs)"}");
        Console.WriteLine($"machine: {Environment.ProcessorCount} logical cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        string scratch = Path.Combine(Path.GetTempPath(), "webp-encode-report-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        int failures = 0;
        var sizeTable = new List<string[]>();
        var timeTable = new List<string[]>();
        try
        {
            var sources = new List<WebpEncodeInputs.Source>();
            if (corpus)
            {
                sources.AddRange(WebpEncodeInputs.Corpus());
            }

            if (big)
            {
                sources.AddRange(WebpEncodeInputs.Benchmark1080p());
            }

            foreach (var source in sources.Where(s => only is null || only.Contains(s.Name)))
            {
                Console.Error.WriteLine($"-- {source.Name} {source.Width}x{source.Height}");
                string png = Path.Combine(scratch, source.Name + ".png");
                WritePng(png, source);

                var ours = new List<(int Effort, long Size, double Ms)>();
                foreach (int effort in efforts)
                {
                    var sw = Stopwatch.StartNew();
                    byte[] file = WebpEncoder.EncodeLossless(source.Rgba, source.Width, source.Height, 0, new WebpEncodeOptions { Effort = effort });
                    sw.Stop();
                    ours.Add((effort, file.Length, sw.Elapsed.TotalMilliseconds));
                    failures += Verify(source, file, $"ours e{effort}", dwebp, scratch);
                }

                var theirs = new List<Cwebp>();
                foreach ((string label, string z) in new[] { ("cwebp default", ""), ("cwebp -z 9", "-z 9") })
                {
                    string output = Path.Combine(scratch, source.Name + (z.Length == 0 ? ".z6.webp" : ".z9.webp"));
                    File.Delete(output);
                    var sw = Stopwatch.StartNew();
                    var result = Run(cwebp, $"-quiet -lossless -exact {z} \"{png}\" -o \"{output}\"");
                    sw.Stop();
                    if (result.ExitCode != 0 || !File.Exists(output))
                    {
                        Console.WriteLine($"FAIL {label} on {source.Name}: exit {result.ExitCode} {result.Output}");
                        failures++;
                        continue;
                    }

                    byte[] file = File.ReadAllBytes(output);
                    int bad = Verify(source, file, label, dwebp, scratch);
                    failures += bad;
                    theirs.Add(new Cwebp(label, file.Length, sw.Elapsed.TotalMilliseconds, bad == 0));
                }

                long def = theirs.FirstOrDefault(t => t.Label == "cwebp default")?.Size ?? 0;
                long z9 = theirs.FirstOrDefault(t => t.Label == "cwebp -z 9")?.Size ?? 0;
                sizeTable.Add([source.Name, $"{source.Width}x{source.Height}", .. ours.Select(o => Size(o.Size, def)), Size(def, def), Size(z9, def)]);
                timeTable.Add([source.Name, .. ours.Select(o => Time(o.Ms)), Time(theirs.FirstOrDefault(t => t.Label == "cwebp default")?.Milliseconds), Time(theirs.FirstOrDefault(t => t.Label == "cwebp -z 9")?.Milliseconds)]);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        string[] effortHeaders = efforts.Select(e => $"e{e}").ToArray();
        Console.WriteLine("### File size in bytes (ratio to `cwebp -lossless` at its default)");
        Console.WriteLine();
        Print(["image", "size", .. effortHeaders, "cwebp default", "cwebp -z 9"], sizeTable);
        Console.WriteLine();
        Console.WriteLine("### Encode wall time in ms (one run each, one sitting)");
        Console.WriteLine();
        Print(["image", .. effortHeaders, "cwebp default", "cwebp -z 9"], timeTable);
        Console.WriteLine();
        if (outPath is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### File size in bytes (ratio to `cwebp -lossless` at its default)").AppendLine();
            sb.Append(Table(["image", "size", .. effortHeaders, "cwebp default", "cwebp -z 9"], sizeTable)).AppendLine();
            sb.AppendLine("### Encode wall time in ms (one run each, one sitting)").AppendLine();
            sb.Append(Table(["image", .. effortHeaders, "cwebp default", "cwebp -z 9"], timeTable));
            sb.AppendLine().AppendLine(failures == 0 ? "every output decodes exactly" : $"{failures} CHECK(S) FAILED");
            File.WriteAllText(outPath, sb.ToString());
        }

        Console.WriteLine(failures == 0
            ? "every output decodes exactly (our decoder" + (dwebp is null ? "" : " and dwebp") + ")"
            : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static string Size(long size, long reference) =>
        reference > 0 ? $"{size:N0} ({size / (double)reference:0.00})" : $"{size:N0}";

    private static string Time(double? ms) => ms is null ? "-" : ms.Value >= 1000 ? $"{ms.Value / 1000:0.0} s" : $"{ms.Value:0} ms";

    private static void Print(string[] header, List<string[]> rows) => Console.Write(Table(header, rows));

    private static string Table(string[] header, List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", header) + " |");
        sb.AppendLine("|" + string.Concat(header.Select((_, i) => i == 0 ? "---|" : "---:|")));
        foreach (string[] row in rows)
        {
            sb.AppendLine("| " + string.Join(" | ", row) + " |");
        }

        return sb.ToString();
    }

    // The decode check: our decoder, and dwebp (as PAM, so alpha is compared too), must both give
    // the source back exactly. Returns the number of failures.
    private static int Verify(WebpEncodeInputs.Source source, byte[] file, string what, string? dwebp, string scratch)
    {
        int failures = 0;
        try
        {
            WebpImage decoded = WebpDecoder.Decode(file);
            if (decoded.Width != source.Width || decoded.Height != source.Height || !decoded.Rgba.AsSpan().SequenceEqual(source.Rgba))
            {
                Console.WriteLine($"FAIL {what} on {source.Name}: our decoder does not return the source");
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {what} on {source.Name}: our decoder threw {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        if (dwebp is not null)
        {
            string input = Path.Combine(scratch, "check.webp");
            string output = Path.Combine(scratch, "check.pam");
            File.WriteAllBytes(input, file);
            File.Delete(output);
            var result = Run(dwebp, $"-quiet -pam \"{input}\" -o \"{output}\"");
            if (result.ExitCode != 0 || !File.Exists(output))
            {
                Console.WriteLine($"FAIL {what} on {source.Name}: dwebp exit {result.ExitCode} {result.Output}");
                failures++;
            }
            else
            {
                byte[] pam = File.ReadAllBytes(output);
                int data = IndexOfHeaderEnd(pam);
                if (data < 0 || !pam.AsSpan(data).SequenceEqual(source.Rgba))
                {
                    Console.WriteLine($"FAIL {what} on {source.Name}: dwebp does not return the source");
                    failures++;
                }
            }
        }

        return failures;
    }

    private static int IndexOfHeaderEnd(byte[] pam)
    {
        ReadOnlySpan<byte> marker = "ENDHDR\n"u8;
        int i = pam.AsSpan(0, Math.Min(pam.Length, 256)).IndexOf(marker);
        return i < 0 ? -1 : i + marker.Length;
    }

    // cwebp is a Windows release build that reads PNG (through WIC) but not PAM.
    private static void WritePng(string path, WebpEncodeInputs.Source source) =>
        File.WriteAllBytes(path, SharpAstro.Png.PngWriter.Encode(source.Rgba, source.Width, source.Height));

    private static (int ExitCode, string Output) Run(string executable, string arguments)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
        string stderr = process.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
        process.WaitForExit();
        return (process.ExitCode, (stdout + stderr).Trim());
    }

    // The lookup WebpOracle (in the test project) does for dwebp: an explicit variable, then
    // Oracle/webp/bin up the directory tree, then PATH. The test project has no cwebp lookup.
    private static string? ResolveTool(string variable, string tool)
    {
        string executable = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = Path.GetFullPath(configured);
            return File.Exists(path) ? path : null;
        }

        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                foreach (string relative in new[]
                         {
                             Path.Combine("Oracle", "webp", "bin"),
                             Path.Combine("tests", "SharpAstro.Codecs.Tests", "Oracle", "webp", "bin"),
                         })
                {
                    string candidate = Path.Combine(directory.FullName, relative, executable);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(entry, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
