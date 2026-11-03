using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Png;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// The synthetic PNGs both PNG-HDR benchmark classes decode. Smooth gradients plus noise, like the JPEG
/// benchmark's input and for the same reason: flat bars would make every sample hit the same few
/// transfer-curve table entries and hide the real cost of a lookup that spans the table.
/// </summary>
internal static class PngHdrInputs
{
    public const int Width = 1920;
    public const int Height = 1080;

    public static ushort[] Rgb16(int w, int h)
    {
        var samples = new ushort[w * h * 3];
        var rnd = new Random(7);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                samples[i + 0] = (ushort)Math.Clamp(x * 65535L / (w - 1) + rnd.Next(-300, 301), 0, 65535);
                samples[i + 1] = (ushort)Math.Clamp(y * 65535L / (h - 1) + rnd.Next(-300, 301), 0, 65535);
                samples[i + 2] = (ushort)Math.Clamp(32768 + 24000 * Math.Sin(x * 0.01) * Math.Cos(y * 0.013) + rnd.Next(-300, 301), 0, 65535);
            }
        }
        return samples;
    }

    public static byte[] Rgba8(int w, int h)
    {
        var samples = new byte[w * h * 4];
        var rnd = new Random(11);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                samples[i + 0] = (byte)Math.Clamp(x * 255 / (w - 1) + rnd.Next(-8, 9), 0, 255);
                samples[i + 1] = (byte)Math.Clamp(y * 255 / (h - 1) + rnd.Next(-8, 9), 0, 255);
                samples[i + 2] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.01) * Math.Cos(y * 0.013) + rnd.Next(-8, 9), 0, 255);
                samples[i + 3] = (byte)Math.Clamp(255 - x * 100 / (w - 1) + rnd.Next(-4, 5), 0, 255);
            }
        }
        return samples;
    }

    public static byte[] PqRgb16(int w, int h, bool fullRange = true) =>
        PngWriter.EncodeRgb16(Rgb16(w, h), w, h, new PngWriteOptions { Cicp = fullRange ? CicpChunk.Hdr10Pq : CicpChunk.Hdr10Pq with { VideoFullRangeFlag = false } });

    public static byte[] HlgRgb16(int w, int h) =>
        PngWriter.EncodeRgb16(Rgb16(w, h), w, h, new PngWriteOptions { Cicp = CicpChunk.Bt2020Hlg });

    public static byte[] SrgbRgba8(int w, int h) => PngWriter.Encode(Rgba8(w, h), w, h);

    // PQ with a 16-bit alpha channel: the one case that divides per sample (no 65536-entry alpha table).
    public static byte[] PqRgba16(int w, int h)
    {
        var rgb = Rgb16(w, h);
        var rgba = new ushort[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            rgba[i * 4] = rgb[i * 3];
            rgba[i * 4 + 1] = rgb[i * 3 + 1];
            rgba[i * 4 + 2] = rgb[i * 3 + 2];
            rgba[i * 4 + 3] = (ushort)(65535 - i % 60000);
        }
        return PngWriter.EncodeRgba16(rgba, w, h, new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq });
    }
}

/// <summary>
/// <see cref="PngImageDecoder.TryDecodeRgbaFloat(ReadOnlySpan{byte}, out float[], out int, out int, out ColorEncoding)"/>
/// as a caller calls it: a whole PNG file in, a fresh linear-light float raster out, at 1080p.
/// <para>
/// <c>PngReader.TryDecode alone</c> is the floor: it is the same inflate and unfilter the projection starts from, and
/// not something the float projection added, so what each projection costs above it is the number that matters.
/// </para>
/// <para>
/// <b>Reading the Allocated column.</b> A 1920x1080 float raster is 33 177 600 bytes, so the projection must allocate
/// that plus whatever the reader allocated for the decoded samples (12 441 600 bytes for 16-bit RGB) and nothing
/// that grows with the image beyond it.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 6)]
public class PngHdrFloatBenchmarks
{
    private byte[] _pq16 = [];
    private byte[] _hlg16 = [];
    private byte[] _srgb8 = [];

    [GlobalSetup]
    public void Setup()
    {
        _pq16 = PngHdrInputs.PqRgb16(PngHdrInputs.Width, PngHdrInputs.Height);
        _hlg16 = PngHdrInputs.HlgRgb16(PngHdrInputs.Width, PngHdrInputs.Height);
        _srgb8 = PngHdrInputs.SrgbRgba8(PngHdrInputs.Width, PngHdrInputs.Height);
    }

    [Benchmark(Baseline = true, Description = "PngReader.TryDecode only, RGB16 (floor)")]
    public PngImage? DecodeOnly16()
    {
        PngReader.TryDecode(_pq16, out var png);
        return png;
    }

    [Benchmark(Description = "TryDecodeRgbaFloat PQ RGB16")]
    public float[]? Pq16()
    {
        PngImageDecoder.TryDecodeRgbaFloat(_pq16, out var rgba, out _, out _, out _);
        return rgba;
    }

    [Benchmark(Description = "TryDecodeRgbaFloat HLG RGB16")]
    public float[]? Hlg16()
    {
        PngImageDecoder.TryDecodeRgbaFloat(_hlg16, out var rgba, out _, out _, out _);
        return rgba;
    }

    [Benchmark(Description = "TryDecodeRgbaFloat sRGB RGBA8")]
    public float[]? Srgb8()
    {
        PngImageDecoder.TryDecodeRgbaFloat(_srgb8, out var rgba, out _, out _, out _);
        return rgba;
    }
}

/// <summary>
/// The same projection at 4K UHD (3840x2160, a 132.7 MB float raster) for PQ only: the size where the result lands on
/// the large-object heap and the allocation, not the arithmetic, starts to matter.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 1, iterationCount: 4)]
public class PngHdrFloat4kBenchmarks
{
    private byte[] _pq16 = [];

    [GlobalSetup]
    public void Setup() => _pq16 = PngHdrInputs.PqRgb16(3840, 2160);

    [Benchmark(Baseline = true, Description = "PngReader.TryDecode only, RGB16 (floor)")]
    public PngImage? DecodeOnly16()
    {
        PngReader.TryDecode(_pq16, out var png);
        return png;
    }

    [Benchmark(Description = "TryDecodeRgbaFloat PQ RGB16")]
    public float[]? Pq16()
    {
        PngImageDecoder.TryDecodeRgbaFloat(_pq16, out var rgba, out _, out _, out _);
        return rgba;
    }
}

/// <summary>
/// What the flight added, isolated from inflate: the pixel loops alone over an already-decoded 1080p image into a
/// preallocated destination, scalar reference against the shipped path. These are the numbers that decide a kernel;
/// the whole-decode classes above are the ones that say whether the decision moved the real thing (a kernel timed in
/// a loop over-predicts, see the repo's CLAUDE.md).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 6)]
public class PngHdrFillBenchmarks
{
    private PngImage _pq16 = null!, _pq16Narrow = null!, _hlg16 = null!, _srgb8 = null!, _pqA16 = null!;
    private ColorEncoding _pq16Enc = null!, _pq16NarrowEnc = null!, _hlg16Enc = null!, _srgb8Enc = null!, _pqA16Enc = null!;
    private float[] _dst = [];

    [GlobalSetup]
    public void Setup()
    {
        (_pq16, _pq16Enc) = Load(PngHdrInputs.PqRgb16(PngHdrInputs.Width, PngHdrInputs.Height));
        (_pq16Narrow, _pq16NarrowEnc) = Load(PngHdrInputs.PqRgb16(PngHdrInputs.Width, PngHdrInputs.Height, fullRange: false));
        (_hlg16, _hlg16Enc) = Load(PngHdrInputs.HlgRgb16(PngHdrInputs.Width, PngHdrInputs.Height));
        (_srgb8, _srgb8Enc) = Load(PngHdrInputs.SrgbRgba8(PngHdrInputs.Width, PngHdrInputs.Height));
        (_pqA16, _pqA16Enc) = Load(PngHdrInputs.PqRgba16(PngHdrInputs.Width, PngHdrInputs.Height));
        _dst = new float[PngHdrInputs.Width * PngHdrInputs.Height * 4];
    }

    private static (PngImage, ColorEncoding) Load(byte[] file)
    {
        var png = PngReader.Decode(file);
        return (png, PngImageDecoder.MapColorEncoding(png));
    }

    [Benchmark(Baseline = true, Description = "PQ RGB16 reference")]
    public float[] Pq16Reference() { PngFloatFill.FillReference(_pq16, _pq16Enc, _dst); return _dst; }

    [Benchmark(Description = "PQ RGB16 shipped")]
    public float[] Pq16Shipped() { PngFloatFill.Fill(_pq16, _pq16Enc, _dst); return _dst; }

    [Benchmark(Description = "PQ RGB16 narrow-range reference")]
    public float[] Pq16NarrowReference() { PngFloatFill.FillReference(_pq16Narrow, _pq16NarrowEnc, _dst); return _dst; }

    [Benchmark(Description = "PQ RGB16 narrow-range shipped")]
    public float[] Pq16NarrowShipped() { PngFloatFill.Fill(_pq16Narrow, _pq16NarrowEnc, _dst); return _dst; }

    [Benchmark(Description = "HLG RGB16 reference")]
    public float[] Hlg16Reference() { PngFloatFill.FillReference(_hlg16, _hlg16Enc, _dst); return _dst; }

    [Benchmark(Description = "HLG RGB16 shipped")]
    public float[] Hlg16Shipped() { PngFloatFill.Fill(_hlg16, _hlg16Enc, _dst); return _dst; }

    [Benchmark(Description = "sRGB RGBA8 reference")]
    public float[] Srgb8Reference() { PngFloatFill.FillReference(_srgb8, _srgb8Enc, _dst); return _dst; }

    [Benchmark(Description = "PQ RGBA16 reference")]
    public float[] PqA16Reference() { PngFloatFill.FillReference(_pqA16, _pqA16Enc, _dst); return _dst; }

    [Benchmark(Description = "PQ RGBA16 shipped")]
    public float[] PqA16Shipped() { PngFloatFill.Fill(_pqA16, _pqA16Enc, _dst); return _dst; }

    [Benchmark(Description = "sRGB RGBA8 shipped")]
    public float[] Srgb8Shipped() { PngFloatFill.Fill(_srgb8, _srgb8Enc, _dst); return _dst; }
}
