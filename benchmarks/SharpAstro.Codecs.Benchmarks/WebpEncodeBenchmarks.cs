using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpAstro.Webp;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// Encode time per <see cref="WebpEncodeOptions.Effort"/> level for the lossless VP8L encoder, on
/// 1080p sources: the committed <c>webp-inputs</c> decoded to RGBA in <c>GlobalSetup</c> and a
/// generated photo / screenshot pair. One encode is far longer than BenchmarkDotNet's invocation
/// target at the higher efforts, so every case runs one encode per iteration
/// (<see cref="RunStrategy.Monitoring"/>); the size comes from <c>--webp-encode-report</c>, which
/// also sets it against <c>cwebp -lossless</c>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class WebpEncodeBenchmarks
{
    [Params(
        WebpEncodeInputs.Photo1080p,
        WebpEncodeInputs.Screenshot1080p,
        WebpEncodeInputs.Lossless1080p)]
    public string Image { get; set; } = string.Empty;

    [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9)]
    public int Effort { get; set; }

    private WebpEncodeInputs.Source _source = null!;
    private WebpEncodeOptions _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = WebpEncodeInputs.Benchmark1080p().Single(s => s.Name == Image);
        _options = new WebpEncodeOptions { Effort = Effort };
    }

    [Benchmark(Description = "WebpEncoder.EncodeLossless")]
    public int Encode() => WebpEncoder.EncodeLossless(_source.Rgba, _source.Width, _source.Height, 0, _options).Length;
}

/// <summary>The images the encode benchmark and report run on.</summary>
internal static class WebpEncodeInputs
{
    public const string Photo1080p = "photo-1080p";
    public const string Screenshot1080p = "screenshot-1080p";
    public const string Lossless1080p = "lossless-1080p";
    public const string LossyOpaque1080p = "lossy-opaque-1080p";
    public const string LossyAlpha1080p = "lossy-alpha-1080p";

    public sealed record Source(string Name, int Width, int Height, byte[] Rgba);

    // Still fixtures the decoder tests already commit: small, but real, and between them a lossy
    // photo, a test pattern, palette images, noise, a gradient and transparency.
    private static readonly string[] CorpusFiles =
    [
        "astronaut-192-q20-normal.webp",
        "testsrc2-320x240-q75.webp",
        "lossless-near60.webp",
        "lossless-palette-200.webp",
        "lossless-palette-16.webp",
        "lossless-exact-alpha.webp",
        "lossy-alpha-q50.webp",
        "noise-64x48-vp8-partitions2.webp",
        "gradient-16x16.webp",
        "odd-33x19-libvpx.webp",
    ];

    /// <summary>The small committed fixtures (the test project's <c>Fixtures/webp</c>), decoded to RGBA.</summary>
    public static IEnumerable<Source> Corpus()
    {
        string? directory = FindFixtureDirectory();
        if (directory is null)
        {
            Console.WriteLine("note: tests/SharpAstro.Codecs.Tests/Fixtures/webp was not found; the corpus is skipped.");
            yield break;
        }

        foreach (string file in CorpusFiles)
        {
            yield return Decode(Path.GetFileNameWithoutExtension(file), File.ReadAllBytes(Path.Combine(directory, file)));
        }
    }

    /// <summary>The three committed 1080p still inputs (decoded) and the generated photo / screenshot.</summary>
    public static IEnumerable<Source> Benchmark1080p()
    {
        yield return new(Photo1080p, 1920, 1080, GeneratePhoto(1920, 1080));
        yield return new(Screenshot1080p, 1920, 1080, GenerateScreenshot(1920, 1080));
        foreach (string name in new[] { Lossless1080p, LossyOpaque1080p, LossyAlpha1080p })
        {
            yield return Decode(name, WebpBenchmarkInputs.Read(name + ".webp"));
        }
    }

    private static Source Decode(string name, byte[] webp)
    {
        WebpImage image = WebpDecoder.Decode(webp);
        return new(name, image.Width, image.Height, image.Rgba);
    }

    private static string? FindFixtureDirectory()
    {
        string relative = Path.Combine("tests", "SharpAstro.Codecs.Tests", "Fixtures", "webp");
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, relative);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>
    /// A photo-like image: low-frequency colour fields (sums of sines, so every channel varies
    /// smoothly and the predictor has something to predict) with sensor-style noise of about
    /// +-3 levels on top, so it does not collapse to a few colours.
    /// </summary>
    public static byte[] GeneratePhoto(int width, int height)
    {
        byte[] rgba = new byte[checked(width * height * 4)];
        uint noise = 0x2545f491;
        for (int y = 0; y < height; y++)
        {
            double v = y / (double)height;
            for (int x = 0; x < width; x++)
            {
                double u = x / (double)width;
                double sky = 1 - v;
                double r = 110 + 70 * Math.Sin(u * 5.1 + v * 2.3) + 40 * Math.Sin(u * 17 - v * 9) * v + 30 * sky;
                double g = 120 + 60 * Math.Sin(u * 3.7 - v * 4.1) + 35 * Math.Cos(u * 23 + v * 13) * v + 20 * sky;
                double b = 130 + 50 * Math.Cos(u * 4.3 + v * 6.7) + 25 * Math.Sin(u * 31 - v * 21) * v + 60 * sky;
                uint n = Next(ref noise);
                int shared = (int)(n & 7) - 3;
                int o = (y * width + x) * 4;
                rgba[o] = Clamp(r + shared + (int)((n >> 3) & 3) - 1);
                rgba[o + 1] = Clamp(g + shared + (int)((n >> 5) & 3) - 1);
                rgba[o + 2] = Clamp(b + shared + (int)((n >> 7) & 3) - 1);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    /// <summary>
    /// A screenshot-like image: flat panels, a title bar, a sidebar, lines of anti-aliased
    /// "text" built from a few dozen repeated glyph bitmaps (so a long run of repeats at a few
    /// distances), a gradient banner (over 256 colours, so no palette) and a thin grid.
    /// </summary>
    public static byte[] GenerateScreenshot(int width, int height)
    {
        byte[] rgba = new byte[checked(width * height * 4)];
        void Fill(int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(height, y1); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
                {
                    int o = (y * width + x) * 4;
                    rgba[o] = r;
                    rgba[o + 1] = g;
                    rgba[o + 2] = b;
                    rgba[o + 3] = 255;
                }
            }
        }

        Fill(0, 0, width, height, 250, 250, 252);
        Fill(0, 0, width, 40, 32, 36, 48);
        Fill(0, 40, 280, height, 238, 240, 244);

        // The glyph bitmaps: 7 x 12 cells of four grey levels, a quarter of them ink.
        uint seed = 0x1234567;
        byte[][] glyphs = new byte[48][];
        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphs[i] = new byte[7 * 12];
            for (int k = 0; k < glyphs[i].Length; k++)
            {
                uint n = Next(ref seed) % 8;
                glyphs[i][k] = n < 4 ? (byte)255 : (byte)((n - 3) * 50);
            }
        }

        void Text(int x0, int y0, int x1, int line)
        {
            uint s = (uint)(line * 7919 + 17);
            for (int x = x0; x + 8 <= x1; x += 8)
            {
                if (Next(ref s) % 6 == 0)
                {
                    continue; // a space
                }

                byte[] glyph = glyphs[Next(ref s) % (uint)glyphs.Length];
                for (int gy = 0; gy < 12; gy++)
                {
                    for (int gx = 0; gx < 7; gx++)
                    {
                        byte cover = glyph[gy * 7 + gx];
                        if (cover == 255 || y0 + gy >= height)
                        {
                            continue;
                        }

                        int o = ((y0 + gy) * width + x + gx) * 4;
                        byte ink = (byte)(cover * 2 / 3);
                        rgba[o] = Math.Min(rgba[o], ink);
                        rgba[o + 1] = Math.Min(rgba[o + 1], ink);
                        rgba[o + 2] = Math.Min(rgba[o + 2], (byte)(ink + 30));
                    }
                }
            }
        }

        for (int line = 0; 60 + line * 18 + 12 < height; line++)
        {
            Text(300, 60 + line * 18, width - 40, line);
            Text(20, 60 + line * 18, 260, line + 1000);
        }

        // A gradient banner and a grid of cells.
        for (int y = 44; y < 100; y++)
        {
            for (int x = 600; x < width - 60; x++)
            {
                int o = (y * width + x) * 4;
                double t = (x - 600) / (double)(width - 660);
                rgba[o] = Clamp(20 + 200 * t);
                rgba[o + 1] = Clamp(90 + 100 * Math.Sin(t * 3) + (y - 44));
                rgba[o + 2] = Clamp(220 - 150 * t);
            }
        }

        for (int y = 400; y < height; y += 64)
        {
            Fill(300, y, width - 40, y + 1, 200, 204, 212);
        }

        return rgba;
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
