using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using SharpAstro.Png;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// Whole APNG decodes through the public API: <see cref="PngReader.EnumerateFrames"/> (one working canvas) against
/// <see cref="PngReader.DecodeFrames(ReadOnlySpan{byte})"/> (every canvas materialised). Allocated bytes and GC counts
/// are the half of the result that matters here: a frame's compositing should allocate next to nothing, and streaming
/// should hold the canvas and no more.
/// </summary>
/// <remarks>
/// The inputs are built in <see cref="ApngBenchmarkInputs"/> from <see cref="PngWriter"/>'s own frames, so no fixture
/// is committed. They are meant to be realistic rather than adversarial: photo-like (smooth plus noise) colour so the
/// deflate and the filters have real work, and an alpha mix that real animations show (mostly opaque, a band of
/// partial alpha at the edges of a sprite, some fully transparent). <c>Over</c> with a lot of partial alpha is the
/// slow case for the blend and is kept as <c>LargeOverPartial</c>.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 6)]
public class ApngCompositeBenchmarks
{
    [Params(
        ApngBenchmarkInputs.ManySmall,
        ApngBenchmarkInputs.LargeOverMixed,
        ApngBenchmarkInputs.LargeOverPartial,
        ApngBenchmarkInputs.LargeSource,
        ApngBenchmarkInputs.LargeRgb8)]
    public string Fixture { get; set; } = string.Empty;

    private byte[] _png = [];
    private int _canvasBytes;

    [GlobalSetup]
    public void Setup()
    {
        _png = ApngBenchmarkInputs.Build(Fixture);
        var image = PngReader.Decode(_png);
        _canvasBytes = image.Width * image.Height * 4;
        if (!image.IsAnimated)
            throw new InvalidOperationException($"{Fixture} is not animated");
    }

    [Benchmark(Baseline = true, Description = "EnumerateFrames (stream)")]
    public long Stream()
    {
        long sum = 0;
        foreach (var frame in PngReader.EnumerateFrames(_png))
            sum += frame.Rgba[^1] + frame.Rgba[frame.Rgba.Length / 2];
        return sum;
    }

    [Benchmark(Description = "DecodeFrames (materialise)")]
    public int Materialise()
    {
        var frames = PngReader.DecodeFrames(_png, long.MaxValue);
        return frames.Frames.Count;
    }
}

/// <summary>
/// The vector OVER against the scalar one, whole streaming decodes of the same file (<c>PngCompositor.ForceScalar</c> is
/// the switch), so the number is the decode's and not a kernel's in isolation. Each parameter combination runs in its own
/// process, so the static flag cannot leak between rows.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 8)]
public class ApngOverKernelBenchmarks
{
    [Params(ApngBenchmarkInputs.LargeOverMixed, ApngBenchmarkInputs.LargeOverPartial)]
    public string Fixture { get; set; } = string.Empty;

    [Params(true, false)]
    public bool ForceScalar { get; set; }

    private byte[] _png = [];

    [GlobalSetup]
    public void Setup()
    {
        _png = ApngBenchmarkInputs.Build(Fixture);
        PngCompositor.ForceScalar = ForceScalar;
    }

    [Benchmark(Description = "EnumerateFrames (stream)")]
    public long Stream()
    {
        long sum = 0;
        foreach (var frame in PngReader.EnumerateFrames(_png))
            sum += frame.Rgba[^1] + frame.Rgba[frame.Rgba.Length / 2];
        return sum;
    }
}

/// <summary>Builds animated PNGs out of <see cref="PngWriter"/>'s frames: each frame's IDAT payload becomes an <c>fdAT</c>.</summary>
internal static class ApngBenchmarkInputs
{
    public const string ManySmall = "many-small-640x360x60";
    public const string LargeOverMixed = "large-1080p-x8-over-mixed";
    public const string LargeOverPartial = "large-1080p-x8-over-partial";
    public const string LargeSource = "large-1080p-x8-source";
    public const string LargeRgb8 = "large-1080p-x8-rgb8-source";

    public static byte[] Build(string name) => name switch
    {
        // 60 frames of a moving 320x180 sprite on a 640x360 canvas; dispose cycles none / previous / background.
        ManySmall => Build(640, 360, 60, 320, 180, PngBlendOp.Over, Alpha.Sprite, cycleDispose: true),
        // 8 full-canvas 1080p frames: mostly opaque with a soft edge and holes, OVER, dispose none.
        LargeOverMixed => Build(1920, 1080, 8, 1920, 1080, PngBlendOp.Over, Alpha.Sprite, cycleDispose: false),
        // the same, but half of every frame is partial alpha: every one of those pixels takes the divisions.
        LargeOverPartial => Build(1920, 1080, 8, 1920, 1080, PngBlendOp.Over, Alpha.Partial, cycleDispose: false),
        LargeSource => Build(1920, 1080, 8, 1920, 1080, PngBlendOp.Source, Alpha.Sprite, cycleDispose: false),
        // opaque colour type 2: every frame is expanded from RGB to RGBA8 before it is drawn.
        LargeRgb8 => Build(1920, 1080, 8, 1920, 1080, PngBlendOp.Source, Alpha.Sprite, cycleDispose: false, rgb: true),
        _ => throw new ArgumentException(name),
    };

    private enum Alpha { Sprite, Partial }

    private static byte[] Build(int canvasW, int canvasH, int frames, int frameW, int frameH, PngBlendOp blend, Alpha alpha, bool cycleDispose, bool rgb = false)
    {
        var chunks = new List<(string Type, byte[] Data)>();
        uint sequence = 0;
        for (var i = 0; i < frames; i++)
        {
            var w = i == 0 ? canvasW : frameW;
            var h = i == 0 ? canvasH : frameH;
            var rgba = Frame(w, h, i, alpha, firstFrame: i == 0);
            var parts = Split(rgb ? PngWriter.EncodeRgb8(DropAlpha(rgba), w, h) : PngWriter.Encode(rgba, w, h));
            if (i == 0)
            {
                chunks.Add(parts.First(c => c.Type == "IHDR"));
                var actl = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames);
                chunks.Add(("acTL", actl));
            }

            var x = i == 0 ? 0 : (i * 37) % (canvasW - frameW + 1);
            var y = i == 0 ? 0 : (i * 23) % (canvasH - frameH + 1);
            var dispose = !cycleDispose ? PngDisposeOp.None : (PngDisposeOp)(i % 3);
            var control = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(control, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8), (uint)h);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(12), (uint)x);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(16), (uint)y);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22), 10);
            control[24] = (byte)dispose;
            control[25] = (byte)(i == 0 ? PngBlendOp.Source : blend);
            chunks.Add(("fcTL", control));

            foreach (var idat in parts.Where(c => c.Type == "IDAT"))
            {
                if (i == 0)
                    chunks.Add(idat);
                else
                {
                    var fdat = new byte[4 + idat.Data.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(fdat, sequence++);
                    idat.Data.CopyTo(fdat, 4);
                    chunks.Add(("fdAT", fdat));
                }
            }
        }
        chunks.Add(("IEND", []));

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> word = stackalloc byte[4];
        foreach (var (type, data) in chunks)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
            ms.Write(word);
            ms.Write(typeBytes);
            ms.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typeBytes, data));
            ms.Write(word);
        }
        return ms.ToArray();
    }

    /// <summary>Smooth gradient plus deterministic noise; alpha per <paramref name="alpha"/>.</summary>
    private static byte[] Frame(int w, int h, int index, Alpha alpha, bool firstFrame)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(1000 + index);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / w + rng.Next(8) + index * 5);
                rgba[o + 1] = (byte)(y * 255 / h + rng.Next(8));
                rgba[o + 2] = (byte)((x + y) * 255 / (w + h) + rng.Next(8));
                byte a;
                if (firstFrame)
                    a = 255;
                else if (alpha == Alpha.Partial)
                    a = (x / 16 + y / 16) % 2 == 0 ? (byte)(64 + rng.Next(128)) : (byte)255;
                else
                {
                    // inside: opaque; a soft band at the edges of 8-pixel tiles' borders; some holes
                    var tile = (x / 64 + y / 64) % 5;
                    a = tile switch { 0 => 0, 1 => (byte)(32 + (x % 64) * 3), _ => 255 };
                }
                rgba[o + 3] = a;
            }
        return rgba;
    }

    private static byte[] DropAlpha(byte[] rgba)
    {
        var rgb = new byte[rgba.Length / 4 * 3];
        for (int i = 0, o = 0; i < rgba.Length; i += 4, o += 3)
        {
            rgb[o] = rgba[i];
            rgb[o + 1] = rgba[i + 1];
            rgb[o + 2] = rgba[i + 2];
        }
        return rgb;
    }

    private static List<(string Type, byte[] Data)> Split(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        for (var pos = 8; pos < png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            chunks.Add((Encoding.ASCII.GetString(png, pos + 4, 4), png.AsSpan(pos + 8, length).ToArray()));
            pos += 12 + length;
        }
        return chunks;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
