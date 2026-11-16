using System.Diagnostics;
using System.Text;
using ImageMagick;
using SharpAstro.Svg;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The scene against a browser. Chromium is the renderer most SVG is written for, so it is the stronger of the two
/// pixel oracles: it reads what ImageMagick's renderer does not (<c>hsl()</c>, a visible child of a hidden group), and it
/// is what the later rungs (gradients, <c>use</c>, clipping, text) will have to agree with.
/// </summary>
/// <remarks>
/// <para>Every document is put on ONE page, as an <c>img</c> with a data URI so that it is rendered as a standalone SVG
/// document, and the page is captured in a single headless launch (about a second and a half, however many documents).
/// The capture is cut into one tile a document. Device scale 1 and the sRGB profile are forced, so a tile is the
/// document's pixels.</para>
/// <para>An external program, so it goes through <see cref="OracleGate"/>: skipped where no browser is found, a failure
/// under <c>REQUIRE_ORACLES</c>. ImageMagick (<see cref="SvgRenderOracleTests"/>) stays as the check that needs nothing.
/// Still a tolerance and still fills only, for the same reasons as there.</para>
/// <para><b>The browser's curves are not exact, and that was measured, not assumed.</b> With a plain per-pixel threshold
/// two documents failed on a handful of pixels along tight curves (7 of 14,400 on the rotated arc, 4 of 7,200 on a
/// stretched circle). At each of those pixels the coverage of the true ellipse was computed independently: ours was within
/// 6 levels of it, the browser lighter by 70 to 100. So here a pixel only counts as wrong when it is also outside what
/// the browser has within one pixel of it: an edge may sit a pixel away, a shape in the wrong place still fails.</para>
/// </remarks>
public sealed class SvgBrowserOracleTests
{
    /// <summary>The ImageMagick documents, and those taken out of them because ImageMagick draws them wrong.</summary>
    public static TheoryData<string, string, string> Documents
    {
        get
        {
            var documents = new TheoryData<string, string, string>();
            foreach (var row in SvgRenderOracleTests.Documents) documents.Add(row.Data.Item1, row.Data.Item2, row.Data.Item3);
            documents.Add("hsl", "width=\"90\" height=\"40\"",
                "<rect x=\"5\" y=\"5\" width=\"20\" height=\"30\" fill=\"hsl(300, 60%, 40%)\"/>" +
                "<rect x=\"35\" y=\"5\" width=\"20\" height=\"30\" fill=\"hsl(30deg 100% 50%)\"/>" +
                "<rect x=\"65\" y=\"5\" width=\"20\" height=\"30\" fill=\"hsla(200, 50%, 50%, 0.5)\"/>");
            documents.Add("visible-in-hidden", "width=\"90\" height=\"40\"",
                "<g visibility=\"hidden\"><rect x=\"5\" y=\"5\" width=\"80\" height=\"30\" fill=\"#c00\"/>" +
                "<rect x=\"30\" y=\"12\" width=\"30\" height=\"16\" visibility=\"visible\" fill=\"#06c\"/></g>");
            documents.Add("colour-syntax", "width=\"120\" height=\"40\"",
                "<rect x=\"4\" y=\"4\" width=\"24\" height=\"32\" fill=\"#f80c\"/>" +
                "<rect x=\"32\" y=\"4\" width=\"24\" height=\"32\" fill=\"#0088ff80\"/>" +
                "<rect x=\"60\" y=\"4\" width=\"24\" height=\"32\" fill=\"rgb(0 128 64 / 50%)\"/>" +
                "<rect x=\"88\" y=\"4\" width=\"24\" height=\"32\" fill=\"rebeccapurple\" stroke=\"none\"/>");
            documents.Add("root-defaults", "viewBox=\"0 0 60 30\" width=\"120\"",
                "<rect width=\"60\" height=\"30\" fill=\"#ddd\"/><circle cx=\"30\" cy=\"15\" r=\"12\" fill=\"gray\"/>");
            return documents;
        }
    }

    /// <summary>The page, rendered once for the whole class.</summary>
    private static readonly Lazy<Dictionary<string, (int Width, int Height, byte[] Rgb)>> Rendered = new(RenderAll);

    [Theory]
    [MemberData(nameof(Documents))]
    public void OurScene_DrawnPlainly_MatchesTheBrowsersRenderingOfTheDocument(string name, string root, string body)
    {
        SvgBrowser.Require();
        SvgDocument document = SvgReader.Parse(Wrap(body, root));
        document.Issues.ShouldBeEmpty(name);

        (int width, int height, byte[] rgb) = Rendered.Value[name];
        ((int)MathF.Round(document.Width), (int)MathF.Round(document.Height)).ShouldBe((width, height), name);
        SvgRenderOracleTests.Compare(name, "browser", document.Scene, width, height, rgb, MaxMeanDifference, edgeSlack: true);
    }

    /// <summary>
    /// Of 255, over all pixels and channels. Measured with Edge 142 on Windows: 0.00 to 0.80 over these documents. The
    /// largest is the group-opacity document, where a half-transparent layer over a flat colour comes out one level
    /// apart over a large area (the browser composites in 8 bits).
    /// </summary>
    private const double MaxMeanDifference = 1.2;

    private static Dictionary<string, (int Width, int Height, byte[] Rgb)> RenderAll()
    {
        // The tiles are stacked down the page, each at a known place. Sizes come from our reading of the root; the test
        // then checks the browser drew the same size by comparing every pixel of the tile, the white around it included.
        var tiles = new List<(string Name, int Y, int Width, int Height)>();
        var html = new StringBuilder("<!doctype html><html><body style=\"margin:0;background:#fff\">");
        int y = 0, pageWidth = 1;
        foreach (var row in Documents)
        {
            (string name, string root, string body) = row.Data;
            string svg = Wrap(body, root);
            SvgDocument document = SvgReader.Parse(svg);
            int width = (int)MathF.Round(document.Width), height = (int)MathF.Round(document.Height);
            html.Append($"<img style=\"position:absolute;left:0;top:{y}px\" src=\"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}\">");
            tiles.Add((name, y, width, height));
            y += height + Gap;
            pageWidth = Math.Max(pageWidth, width + Gap);
        }
        html.Append("</body></html>");

        byte[] page = SvgBrowser.Capture(html.ToString(), pageWidth, y, out int pageHeight);
        pageHeight.ShouldBeGreaterThanOrEqualTo(y);

        var rendered = new Dictionary<string, (int, int, byte[])>();
        foreach ((string name, int top, int width, int height) in tiles)
        {
            // The tile, and a check that the margin right of and below it is untouched white: an image drawn larger
            // than we think the document is would spill into it.
            byte[] rgb = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
            {
                page.AsSpan(((top + row) * pageWidth) * 3, width * 3).CopyTo(rgb.AsSpan(row * width * 3));
            }
            for (int row = 0; row < height + Gap; row++)
            {
                int from = row < height ? width : 0;
                ReadOnlySpan<byte> margin = page.AsSpan(((top + row) * pageWidth + from) * 3, (width + Gap - from) * 3);
                margin.IndexOfAnyExcept((byte)255).ShouldBe(-1, $"{name}: the browser drew outside {width} x {height}");
            }
            rendered[name] = (width, height, rgb);
        }
        return rendered;
    }

    /// <summary>White pixels kept right of and below every tile.</summary>
    private const int Gap = 4;
}

/// <summary>Finds a Chromium-family browser and has it capture a page, headless.</summary>
internal static class SvgBrowser
{
    /// <summary>The variable that names the browser's executable, ahead of the usual places.</summary>
    public const string Variable = "SVG_BROWSER";

    private static readonly Lazy<string?> Path_ = new(Resolve);

    public static void Require() => OracleGate.RequireOrSkip(Path_.Value is not null, "A Chromium-family browser (Chrome or Edge)",
        $"Set {Variable} to its executable, or install Google Chrome or Microsoft Edge.");

    /// <summary>The page as RGB bytes, <paramref name="width"/> wide; the height the browser gave is returned.</summary>
    public static byte[] Capture(string html, int width, int height, out int capturedHeight)
    {
        string browser = Path_.Value ?? throw new InvalidOperationException("No browser.");
        string work = Path.Combine(Path.GetTempPath(), "sharpastro-svg-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string page = Path.Combine(work, "page.html"), shot = Path.Combine(work, "page.png");
            File.WriteAllText(page, html);
            var start = new ProcessStartInfo(browser)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in (string[])
            [
                "--headless=new", "--disable-gpu", "--no-sandbox", "--hide-scrollbars", "--no-first-run",
                "--disable-extensions", "--disable-background-networking",
                "--force-device-scale-factor=1", "--force-color-profile=srgb",
                // A profile of its own, so that a running browser is neither reused nor disturbed.
                $"--user-data-dir={Path.Combine(work, "profile")}",
                $"--window-size={width},{height}",
                $"--screenshot={shot}",
                new Uri(page).AbsoluteUri,
            ])
            {
                start.ArgumentList.Add(argument);
            }

            // The capture is done when the browser says so on stderr ("N bytes written to file ..."), not when it exits:
            // measured here with Edge 157, about one launch in eight writes the whole screenshot and then never exits.
            // So wait for the message or the exit, whichever is first, give a browser that has finished a moment to
            // leave on its own, and end it if it does not. Everything waits on an event or a timer.
            var log = new StringBuilder();
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (log) log.AppendLine(e.Data);
                if (e.Data.Contains("written to file", StringComparison.Ordinal)) written.TrySetResult();
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                Task exited = process.WaitForExitAsync();
                Task.WhenAny(exited, written.Task, Task.Delay(CaptureTimeout)).GetAwaiter().GetResult();
                if (written.Task.IsCompleted) exited.Wait(ExitGrace);
                bool finished = exited.IsCompleted || written.Task.IsCompleted;
                if (!finished || !File.Exists(shot))
                {
                    string said;
                    lock (log) said = log.ToString();
                    throw new InvalidOperationException(
                        $"{browser} made no screenshot ({(exited.IsCompleted ? $"exit code {process.ExitCode}" : $"still running after {CaptureTimeout.TotalSeconds:0} s")}). {said}");
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }

            using var image = new MagickImage(shot);
            ((int)image.Width).ShouldBe(width, "the browser's capture is not as wide as the window that was asked for");
            capturedHeight = (int)image.Height;
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            using IPixelCollection<float> pixels = image.GetPixels();
            return pixels.ToByteArray(PixelMapping.RGB)!;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>How long a capture may take before the browser is given up on.</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a browser that has written its screenshot gets to exit by itself.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);

    private static string? Resolve()
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named)
        {
            // Named explicitly: use it or have nothing, so that a wrong path shows as a missing oracle.
            return File.Exists(named) ? named : null;
        }

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (string? root in (string?[])
                [Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LocalAppData")])
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
        }
        else
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string name in (string[])["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge"])
                {
                    candidates.Add(Path.Combine(directory, name));
                }
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
