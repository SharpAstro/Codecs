using System.Diagnostics;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// <c>--heif-report</c>: where a Canon R7 decode's time goes, by phase (HEVC decode of the tiles, the
/// YCbCr conversion, the whole public decode), as medians over several runs after a warm-up. Reads the
/// third-party Canon files from the test project's <c>Fixtures/heif-external</c>
/// (<c>tests/SharpAstro.Codecs.Tests/fetch-heif-external.sh</c>); they are not committed.
/// </summary>
internal static class HeifDecodeReport
{
    public static int Run(string[] args)
    {
        var runs = 5;
        foreach (var a in args)
            if (a.StartsWith("--runs=", StringComparison.Ordinal)) runs = int.Parse(a["--runs=".Length..]);

        var path = FindExternal("canon-r7-hdrpq.hif");
        if (path is null)
        {
            Console.Error.WriteLine("canon-r7-hdrpq.hif not found; run tests/SharpAstro.Codecs.Tests/fetch-heif-external.sh");
            return 1;
        }
        var file = File.ReadAllBytes(path);
        var heif = HeifReader.Read(file);
        var image = heif.DescribePrimary(file);
        var grid = image.Grid!;
        var tiles = grid.TileItemIds.Select(id => heif.GetItem(id)!).ToArray();
        var codings = tiles.Select(t => heif.Describe(file, t).Coding).ToArray();
        var data = tiles.Select(t => heif.ReadItemData(file, t)).ToArray();
        if (args.Contains("--profile"))
        {
            // A single-threaded loop over the tiles' HEVC decode, for a CPU sampler (dotnet-trace).
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < 20)
                for (var t = 0; t < tiles.Length; t++) HevcDecoder.Decode(codings[t], data[t]);
            return 0;
        }

        Console.WriteLine($"R7 {image.Width}x{image.Height}, {tiles.Length} tiles of {image.TileWidth}x{image.TileHeight}, {Environment.ProcessorCount} logical processors");

        // Warm-up (JIT, tiering).
        for (var i = 0; i < 2; i++) HeifDecoder.Decode(file);

        var hevc = new List<double>();
        var convert = new List<double>();
        var whole = new List<double>();
        var serial = new List<double>();
        var floats = new List<double>();
        var floatsSerial = new List<double>();
        for (var r = 0; r < runs; r++)
        {
            var pictures = new HevcPicture[tiles.Length];
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < tiles.Length; t++) pictures[t] = HevcDecoder.Decode(codings[t], data[t]);
            hevc.Add(sw.Elapsed.TotalMilliseconds);

            var colour = HeifDecoder.Colour.For(image, pictures[0]);
            var width = (int)image.Width;
            var height = (int)image.Height;
            var canvas = new ushort[(long)width * height * 3];
            sw.Restart();
            for (var t = 0; t < tiles.Length; t++)
                HeifDecoder.Convert(pictures[t], colour, (int)image.TileWidth, (int)image.TileHeight, canvas, width, height,
                    t % grid.Columns * (int)image.TileWidth, t / grid.Columns * (int)image.TileHeight);
            convert.Add(sw.Elapsed.TotalMilliseconds);

            var gen2 = GC.CollectionCount(2);
            var gen0 = GC.CollectionCount(0);
            var pause = GC.GetTotalPauseDuration();
            sw.Restart();
            var rgb = HeifDecoder.Decode(file);
            whole.Add(sw.Elapsed.TotalMilliseconds);
            if (r == 0)
                Console.WriteLine($"one parallel decode: {GC.CollectionCount(0) - gen0} gen0 and {GC.CollectionCount(2) - gen2} gen2 collections, {(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:F0} ms paused");
            foreach (var degree in r == 0 ? new[] { 2, 4, 8 } : [])
            {
                var t0 = Stopwatch.StartNew();
                HeifDecoder.Decode(file, maxDegreeOfParallelism: degree);
                Console.WriteLine($"  {degree} at once: {t0.Elapsed.TotalMilliseconds:F0} ms");
            }

            sw.Restart();
            HeifDecoder.Decode(file, maxDegreeOfParallelism: 1);
            serial.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart();
            rgb.ToLinearRgbaFloat(out _);
            floats.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart();
            rgb.ToLinearRgbaFloat(out _, maxDegreeOfParallelism: 1);
            floatsSerial.Add(sw.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"HEVC decode, 20 tiles in turn: {Median(hevc),8:F1} ms");
        Console.WriteLine($"YCbCr to R'G'B':                {Median(convert),8:F1} ms");
        Console.WriteLine($"HeifDecoder.Decode, whole:      {Median(whole),8:F1} ms");
        Console.WriteLine($"HeifDecoder.Decode, one thread: {Median(serial),8:F1} ms");
        Console.WriteLine($"ToLinearRgbaFloat:              {Median(floats),8:F1} ms");
        Console.WriteLine($"ToLinearRgbaFloat, one thread:  {Median(floatsSerial),8:F1} ms");
        Console.WriteLine($"(medians of {runs}; all runs: decode {string.Join(", ", whole.Select(v => v.ToString("F0")))})");
        return 0;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static string? FindExternal(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "SharpAstro.Codecs.Tests", "Fixtures", "heif-external", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
