using BenchmarkDotNet.Attributes;
using ImageMagick;
using SharpAstro.Jpeg;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// Full-frame JPEG decode through the public API. Deliberately measures the whole
/// pipeline (Huffman → dequantize → IDCT → upsample → colour convert) rather than
/// individual internal kernels: a kernel that gets faster in isolation but does not
/// move this number has not actually made decoding faster, which is a mistake this
/// repo has already made once.
///
/// <para>The subsampling axis matters because it changes the block count per output
/// pixel: 4:2:0 codes 1.5 blocks per 8×8 of output, 4:4:4 codes 3. So the pair
/// brackets how much of decode is per-block work (IDCT, entropy) versus per-pixel
/// work (upsampling, colour convert).</para>
/// </summary>
[MemoryDiagnoser]
public class JpegDecodeBenchmarks
{
    private byte[] _jpeg420 = [];
    private byte[] _jpeg444 = [];
    private byte[] _rgba = [];
    private byte[] _half = [];
    private byte[] _thumb = [];
    private byte[] _crop = [];

    /// <summary>The middle band of a 2048×1536 frame, an MCU-row-aligned third of its height.</summary>
    private static readonly JpegRegion Band = new(256, 512, 1536, 512);

    /// <summary>
    /// 2048×1536. Content is smooth gradients plus low-amplitude noise: a flat
    /// synthetic image would leave nearly every block DC-only and let the IDCT's
    /// all-AC-zero shortcut hide the real cost of the transform.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        const int w = 2048;
        const int h = 1536;
        var rgb = new byte[w * h * 3];
        var rnd = new Random(7);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                rgb[i + 0] = (byte)Math.Clamp(x * 255 / (w - 1) + rnd.Next(-8, 9), 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(y * 255 / (h - 1) + rnd.Next(-8, 9), 0, 255);
                rgb[i + 2] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.01) * Math.Cos(y * 0.013) + rnd.Next(-8, 9), 0, 255);
            }
        }

        _jpeg420 = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 90, Subsampling = JpegSubsampling.Chroma420 });
        _jpeg444 = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 95, Subsampling = JpegSubsampling.Chroma444 });
        _rgba = new byte[w * h * 4];
        _half = new byte[1024 * 768 * 4];
        _thumb = new byte[256 * 192 * 4];
        _crop = new byte[Band.Width * Band.Height * 4];
    }

    [Benchmark(Baseline = true, Description = "4:2:0 full scale")]
    public (int, int) Decode420() => JpegDecoder.DecodeTo(_jpeg420, _rgba);

    [Benchmark(Description = "4:4:4 full scale")]
    public (int, int) Decode444() => JpegDecoder.DecodeTo(_jpeg444, _rgba);

    /// <summary>
    /// Scaled decode runs the reduced IDCT instead of the full 8×8 one and produces
    /// 1/64th the pixels, so it isolates how much of full-scale decode is the
    /// transform plus the per-pixel output path.
    /// </summary>
    [Benchmark(Description = "4:2:0 eighth scale")]
    public (int, int) Decode420Eighth() => JpegDecoder.DecodeTo(_jpeg420, _rgba, JpegScale.Eighth);

    /// <summary>
    /// Region and fitted decode on the small 4:2:0 frame, where the chroma upsampler (the Hv2 kernel) is on the path
    /// the 4:4:4 scan never takes. The whole-plane cases are the pre-strip engine, kept as the reference
    /// (<c>forceWholePlanes</c>), so each pair is the strip pipeline against what it replaced.
    /// </summary>
    [Benchmark(Description = "4:2:0 region, strips")]
    public (int, int) Region420() => JpegDecoder.DecodeRegionTo(_jpeg420, Band, _crop);

    [Benchmark(Description = "4:2:0 region, whole planes")]
    public long Region420WholePlanes() => JpegDecoder.DecodeRegionToWithStats(_jpeg420, Band, _crop, JpegScale.Full, forceWholePlanes: true).PeakPlaneBytes;

    [Benchmark(Description = "4:2:0 fitted to 1024x768, strips")]
    public (int, int) Fitted420Half() => JpegDecoder.DecodeFittedTo(_jpeg420, 1024, 768, _half);

    [Benchmark(Description = "4:2:0 fitted to 1024x768, whole planes")]
    public long Fitted420HalfWholePlanes() => JpegDecoder.DecodeFittedToWithStats(_jpeg420, 1024, 768, _half, forceWholePlanes: true).PeakPlaneBytes;

    /// <summary>An 8× shrink at full scale: boxes 8 pixels wide, the shape of a thumbnail, where the filter's per-pixel adds dominate.</summary>
    [Benchmark(Description = "4:2:0 fitted to 256x192 (8x boxes)")]
    public (int, int) Fitted420Thumb() => JpegDecoder.DecodeFittedTo(_jpeg420, 256, 192, _thumb);

    [Benchmark(Description = "4:4:4 fitted to 1024x768")]
    public (int, int) Fitted444Half() => JpegDecoder.DecodeFittedTo(_jpeg444, 1024, 768, _half);
}

/// <summary>
/// The shape of the scans in issue #28, which the cases above are far too small to say anything about: 5100×6600,
/// baseline 4:4:4, a restart marker after every MCU row (DRI = 638). It decodes the whole image against a band one
/// third of its height from the middle and against fits to 3165×4096, so the issue's cost claims are measured on its
/// own input rather than inferred: a region of a row-restart scan should cost about a third of a full decode, and a
/// fitted decode should not cost the full-resolution RGBA it replaces.
///
/// <para>A class of its own, and not more cases on <see cref="JpegDecodeBenchmarks"/>, because the input is a
/// 33.7-megapixel JPEG: building it is seconds of setup and ~0.5 GB of transient memory, which the small cases'
/// runs should not pay. It still matches <c>--filter '*JpegDecode*'</c>.</para>
///
/// <para><b>MemoryDiagnoser under-reports the strips.</b> They are rented from <c>ArrayPool</c>, so after the first
/// iteration a decode allocates (almost) nothing however many bytes it holds, and the "Allocated" column below is the
/// steady state, not the footprint. <c>--jpeg-scan-report</c> prints what the decoder held (strip bytes, MCU rows
/// entropy-decoded and transformed) beside it.</para>
/// </summary>
[MemoryDiagnoser]
public class JpegDecodeScanBenchmarks
{
    private byte[] _jpeg = [];
    private byte[] _full = [];
    private byte[] _band = [];
    private byte[] _fitted = [];
    private byte[] _bandFitted = [];
    private byte[] _thumb = [];

    /// <summary>A tenth of the scan's size (510×660): boxes about ten pixels wide, which is what a thumbnail asks of the filter.</summary>
    private const int ThumbWidth = 510;
    private const int ThumbHeight = 660;

    [GlobalSetup]
    public void Setup()
    {
        _jpeg = JpegIssueScan.Build();
        _thumb = new byte[ThumbWidth * ThumbHeight * 4];
        _full = new byte[JpegIssueScan.Width * JpegIssueScan.Height * 4];
        _band = new byte[JpegIssueScan.Band.Width * JpegIssueScan.Band.Height * 4];
        _fitted = new byte[JpegIssueScan.FitWidth * JpegIssueScan.FitHeight * 4];
        _bandFitted = new byte[JpegIssueScan.FitWidth * JpegIssueScan.BandFitHeight * 4];
    }

    [Benchmark(Baseline = true, Description = "scan: DecodeTo, whole image")]
    public (int, int) Full() => JpegDecoder.DecodeTo(_jpeg, _full);

    [Benchmark(Description = "scan: DecodeRegionTo, middle third of the height")]
    public (int, int) Region() => JpegDecoder.DecodeRegionTo(_jpeg, JpegIssueScan.Band, _band);

    [Benchmark(Description = "scan: DecodeFittedTo, whole image to 3165x4096")]
    public (int, int) Fitted() => JpegDecoder.DecodeFittedTo(_jpeg, JpegIssueScan.FitWidth, JpegIssueScan.FitHeight, _fitted);

    [Benchmark(Description = "scan: DecodeFittedTo, middle third to 3165x1365")]
    public (int, int) RegionFitted() => JpegDecoder.DecodeFittedTo(
        _jpeg, JpegIssueScan.FitWidth, JpegIssueScan.BandFitHeight, _bandFitted, JpegScale.Full, JpegIssueScan.Band);

    [Benchmark(Description = "scan: DecodeFittedTo, whole image to 510x660 (10x boxes)")]
    public (int, int) FittedThumb() => JpegDecoder.DecodeFittedTo(_jpeg, ThumbWidth, ThumbHeight, _thumb);

    /// <summary>
    /// The fitted sink's 64-bit path (what it was before 3.25's vector sums, kept as the reference and as the fallback
    /// for a box over 2^24 pixels), measured beside the 32-bit lane path it is paired with above, in the same run.
    /// </summary>
    [Benchmark(Description = "scan: fitted to 3165x4096, 64-bit sums (old sink)")]
    public long FittedWide() => JpegDecoder.DecodeFittedToWithStats(
        _jpeg, JpegIssueScan.FitWidth, JpegIssueScan.FitHeight, _fitted, forceWideSums: true).PeakPlaneBytes;

    [Benchmark(Description = "scan: fitted middle third to 3165x1365, 64-bit sums (old sink)")]
    public long RegionFittedWide() => JpegDecoder.DecodeFittedToWithStats(
        _jpeg, JpegIssueScan.FitWidth, JpegIssueScan.BandFitHeight, _bandFitted, JpegScale.Full, JpegIssueScan.Band, forceWideSums: true).PeakPlaneBytes;

    [Benchmark(Description = "scan: fitted to 510x660 (10x boxes), 64-bit sums (old sink)")]
    public long FittedThumbWide() => JpegDecoder.DecodeFittedToWithStats(
        _jpeg, ThumbWidth, ThumbHeight, _thumb, forceWideSums: true).PeakPlaneBytes;

    /// <summary>
    /// The strip pipeline against the engine it replaced: <c>forceWholePlanes</c> runs the pre-strip path (three
    /// 33.7 MB planes per call, a fresh <c>new byte[]</c> each, so Gen2/LOH traffic that the pooled strips do not have).
    /// The destination is the same, so a pair differs only in the engine.
    /// </summary>
    [Benchmark(Description = "scan: whole image, whole planes (pre-strip engine)")]
    public long FullWholePlanes() => JpegDecoder.DecodeRegionToWithStats(
        _jpeg, new JpegRegion(0, 0, JpegIssueScan.Width, JpegIssueScan.Height), _full, JpegScale.Full, forceWholePlanes: true).PeakPlaneBytes;

    [Benchmark(Description = "scan: middle third, whole planes (pre-strip engine)")]
    public long RegionWholePlanes() => JpegDecoder.DecodeRegionToWithStats(
        _jpeg, JpegIssueScan.Band, _band, JpegScale.Full, forceWholePlanes: true).PeakPlaneBytes;

    [Benchmark(Description = "scan: fitted to 3165x4096, whole planes (pre-strip engine)")]
    public long FittedWholePlanes() => JpegDecoder.DecodeFittedToWithStats(
        _jpeg, JpegIssueScan.FitWidth, JpegIssueScan.FitHeight, _fitted, forceWholePlanes: true).PeakPlaneBytes;

    /// <summary>
    /// The path <c>DecodeFittedTo</c> replaces, measured in the same run so the two are an A/B and not two numbers from
    /// different sittings: <c>Decode</c> (a fresh full RGBA, 134.6 MB of large-object allocation per call) and then the
    /// box filter, here <see cref="BoxShrinkReference"/>. That is a transcription of the viewer's
    /// <c>RgbaBoxDownsample.Shrink</c> and not the viewer's own code, which may be faster, so this is a bound on the
    /// viewer's cost rather than its measured cost. It is byte-identical to what <c>DecodeFittedTo</c> returns
    /// (<c>--jpeg-scan-report</c> checks that).
    /// </summary>
    [Benchmark(Description = "scan: Decode + box filter to 3165x4096 (the path DecodeFittedTo replaces)")]
    public byte[] DecodeThenShrink()
    {
        var image = JpegDecoder.Decode(_jpeg);
        return BoxShrinkReference.Shrink(image.Pixels, image.Width, image.Height, JpegIssueScan.FitWidth, JpegIssueScan.FitHeight);
    }
}

/// <summary>
/// <c>RgbaBoxDownsample.Shrink</c> from the Drawboard PDF viewer, transcribed line for line in <c>int</c> arithmetic:
/// the same function <c>JpegFittedDecodeTests.ReferenceShrink</c> pins the decoder against. The target size is a
/// parameter (the viewer derives it from a maximum display dimension) and a source row is sliced out of the packed
/// array instead of coming from the viewer's row interface; neither changes a result or the work done per pixel.
/// </summary>
internal static class BoxShrinkReference
{
    public static byte[] Shrink(ReadOnlySpan<byte> pixels, int w, int h, int newW, int newH)
    {
        var sx0 = new int[newW];
        var sx1 = new int[newW];
        for (var dx = 0; dx < newW; dx++)
        {
            sx0[dx] = dx * w / newW;
            sx1[dx] = Math.Min(w, Math.Max(sx0[dx] + 1, (dx + 1) * w / newW));
        }

        var dst = new byte[newW * newH * 4];
        var acc = new int[newW * 4];
        for (var dy = 0; dy < newH; dy++)
        {
            var sy0 = dy * h / newH;
            var sy1 = Math.Min(h, Math.Max(sy0 + 1, (dy + 1) * h / newH));
            Array.Clear(acc);
            for (var sy = sy0; sy < sy1; sy++)
            {
                var row = pixels.Slice(sy * w * 4, w * 4);
                for (var dx = 0; dx < newW; dx++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (var i = sx0[dx] * 4; i < sx1[dx] * 4; i += 4)
                    {
                        r += row[i];
                        g += row[i + 1];
                        b += row[i + 2];
                        a += row[i + 3];
                    }

                    var ai = dx * 4;
                    acc[ai] += r;
                    acc[ai + 1] += g;
                    acc[ai + 2] += b;
                    acc[ai + 3] += a;
                }
            }

            var rowsInBox = sy1 - sy0;
            var di = dy * newW * 4;
            for (var dx = 0; dx < newW; dx++)
            {
                var n = rowsInBox * (sx1[dx] - sx0[dx]);
                var ai = dx * 4;
                dst[di + ai] = (byte)((acc[ai] + n / 2) / n);
                dst[di + ai + 1] = (byte)((acc[ai + 1] + n / 2) / n);
                dst[di + ai + 2] = (byte)((acc[ai + 2] + n / 2) / n);
                dst[di + ai + 3] = (byte)((acc[ai + 3] + n / 2) / n);
            }
        }

        return dst;
    }
}

/// <summary>The input for <see cref="JpegDecodeScanBenchmarks"/> and <see cref="JpegScanReport"/>, built in memory: no binary is committed.</summary>
internal static class JpegIssueScan
{
    public const int Width = 5100;
    public const int Height = 6600;

    /// <summary>MCUs in a row of a 4:4:4 scan: one marker after every MCU row. 5100 / 8 = 637.5, rounded up.</summary>
    public const int RestartInterval = (Width + 7) / 8;

    /// <summary>The band: a third of the image's height, from the middle third's first row (2200, which is an MCU row boundary).</summary>
    public static readonly JpegRegion Band = new(0, Height / 3, Width, Height / 3);

    /// <summary>The issue's fit (3165×4096), the same ratio in both axes (0.6206) ...</summary>
    public const int FitWidth = 3165;
    public const int FitHeight = 4096;

    /// <summary>... and the band at that ratio: 2200 × 4096 / 6600 = 1365.3.</summary>
    public const int BandFitHeight = 1365;

    /// <summary>
    /// Smooth gradients plus low-amplitude noise, as <see cref="JpegDecodeBenchmarks"/> uses: a flat image would leave
    /// nearly every block DC-only and let the IDCT's all-AC-zero shortcut hide what a real scan costs. Encoded by
    /// libjpeg through Magick.NET because <c>JpegEncoder</c> writes no restart markers (a shipped-encoder feature is
    /// not this benchmark's to add); the stream is then checked to be the shape the benchmark names.
    /// </summary>
    public static byte[] Build()
    {
        var rgb = new byte[Width * Height * 3];
        var rnd = new Random(7);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 3;
                rgb[i + 0] = (byte)Math.Clamp(x * 255 / (Width - 1) + rnd.Next(-8, 9), 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(y * 255 / (Height - 1) + rnd.Next(-8, 9), 0, 255);
                rgb[i + 2] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.01) * Math.Cos(y * 0.013) + rnd.Next(-8, 9), 0, 255);
            }
        }

        using var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings((uint)Width, (uint)Height, StorageType.Char, "RGB"));
        image.Quality = 90;
        image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "1x1,1x1,1x1");
        image.Settings.SetDefine(MagickFormat.Jpeg, "restart-interval", RestartInterval.ToString());
        var jpeg = image.ToByteArray(MagickFormat.Jpeg);
        Verify(jpeg);
        return jpeg;
    }

    /// <summary>
    /// The restart-interval define is the only thing that makes a DRI, and a different native library that stopped
    /// honouring it would turn every case here into a plain-baseline one that still runs and still prints a number.
    /// </summary>
    private static void Verify(byte[] jpeg)
    {
        var restart = 0;
        var baseline = false;
        var sampling = "";
        foreach (var segment in JpegSegmentScanner.Scan(jpeg))
        {
            var p = segment.Payload(jpeg);
            if (segment.Marker == 0xDD)
            {
                restart = (p[0] << 8) | p[1];
            }
            else if (segment.Marker == 0xC0)
            {
                baseline = true;
                for (var c = 0; c < p[5]; c++)
                    sampling += $"{p[7 + 3 * c] >> 4}x{p[7 + 3 * c] & 15} ";
            }
        }

        if (!baseline || sampling != "1x1 1x1 1x1 " || restart != RestartInterval)
        {
            throw new InvalidOperationException(
                $"Expected baseline 4:4:4 with DRI={RestartInterval}, got baseline={baseline} sampling='{sampling.Trim()}' DRI={restart}.");
        }
    }
}

/// <summary>
/// <c>--jpeg-scan-report</c>: for each case of <see cref="JpegDecodeScanBenchmarks"/>, what the decoder <i>held</i>.
/// BenchmarkDotNet gives time and managed allocation, and the strips are pooled, so the strip bytes and the number of
/// MCU rows entropy-decoded and transformed (the region's cost, as a count) come from the decoder's own stats.
/// The time here is the median of a few runs after a warm-up, as a cross-check, not a replacement for BenchmarkDotNet.
/// </summary>
internal static class JpegScanReport
{
    public static void Run()
    {
        var jpeg = JpegIssueScan.Build();
        var info = JpegDecoder.ReadInfo(jpeg);
        Console.WriteLine($"scan: {info.Width}x{info.Height}, {jpeg.Length / 1048576.0:F1} MiB of JPEG, DRI={JpegIssueScan.RestartInterval}");
        Console.WriteLine($"{"case",-40} {"median ms",10} {"allocated",12} {"strips",10} {"dest",10} {"entropy",8} {"IDCT",6}");

        var full = new byte[JpegIssueScan.Width * JpegIssueScan.Height * 4];
        var band = new byte[JpegIssueScan.Band.Width * JpegIssueScan.Band.Height * 4];
        var fitted = new byte[JpegIssueScan.FitWidth * JpegIssueScan.FitHeight * 4];
        var bandFitted = new byte[JpegIssueScan.FitWidth * JpegIssueScan.BandFitHeight * 4];

        Measure("DecodeTo, whole image", full.Length,
            () => JpegDecoder.DecodeTo(jpeg, full),
            () => JpegDecoder.DecodeWithStats(jpeg, JpegScale.Full, out _));
        Measure("DecodeRegionTo, middle third", band.Length,
            () => JpegDecoder.DecodeRegionTo(jpeg, JpegIssueScan.Band, band),
            () => JpegDecoder.DecodeRegionToWithStats(jpeg, JpegIssueScan.Band, band));
        Measure("DecodeFittedTo, whole -> 3165x4096", fitted.Length,
            () => JpegDecoder.DecodeFittedTo(jpeg, JpegIssueScan.FitWidth, JpegIssueScan.FitHeight, fitted),
            () => JpegDecoder.DecodeFittedToWithStats(jpeg, JpegIssueScan.FitWidth, JpegIssueScan.FitHeight, fitted));
        Measure("DecodeFittedTo, middle third -> 3165x1365", bandFitted.Length,
            () => JpegDecoder.DecodeFittedTo(jpeg, JpegIssueScan.FitWidth, JpegIssueScan.BandFitHeight, bandFitted, JpegScale.Full, JpegIssueScan.Band),
            () => JpegDecoder.DecodeFittedToWithStats(jpeg, JpegIssueScan.FitWidth, JpegIssueScan.BandFitHeight, bandFitted, JpegScale.Full, JpegIssueScan.Band));

        // The path the fitted decode replaces: a fresh full RGBA, then the box filter. Its "dest" is that RGBA plus the
        // result, both allocated per call (and so also what "allocated" shows, unlike the pooled strips).
        byte[] viewerPath = [];
        Measure("Decode + box filter (replaced path)", full.Length + fitted.Length,
            () => viewerPath = Viewer(jpeg),
            () => JpegDecoder.DecodeWithStats(jpeg, JpegScale.Full, out _));

        Console.WriteLine();
        Console.WriteLine($"DecodeFittedTo == Decode + box filter, byte for byte: {fitted.AsSpan().SequenceEqual(viewerPath)}");

        static byte[] Viewer(byte[] jpeg)
        {
            var image = JpegDecoder.Decode(jpeg);
            return BoxShrinkReference.Shrink(image.Pixels, image.Width, image.Height, JpegIssueScan.FitWidth, JpegIssueScan.FitHeight);
        }
    }

    private static void Measure(string name, int destinationBytes, Action run, Func<JpegDecodeStats> stats)
    {
        run(); // JIT, and the first rent of every strip: what follows is the steady state

        var before = GC.GetAllocatedBytesForCurrentThread();
        run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var ms = new double[5];
        for (var i = 0; i < ms.Length; i++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            run();
            ms[i] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(ms);
        var s = stats();
        Console.WriteLine(
            $"{name,-40} {ms[ms.Length / 2],10:F0} {allocated / 1048576.0,9:F2} MiB {s.PeakPlaneBytes / 1048576.0,7:F2} MiB {destinationBytes / 1048576.0,7:F1} MiB {s.EntropyUnits,8} {s.TransformedUnits,6}");
    }
}
