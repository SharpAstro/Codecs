using SharpAstro.Jpeg;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The JPEG decoder assembles its output a few MCU rows at a time through per-component strips
/// (<c>JpegCore</c>'s strip pipeline) instead of whole-image planes. These tests pin the two claims that
/// restructure makes:
///
/// <list type="number">
/// <item><b>The bytes did not change.</b> <c>JpegDecoder.DecodeWithStats(forceWholePlanes: true)</c> runs the
/// whole-plane engine, which is stb's pipeline as it was before strips; the strip engine must reproduce it
/// byte for byte on every sampling layout, odd size, scale and salvage path below. (The committed golden
/// digests in <see cref="JpegDecoderOracleTests"/> cover real files; this covers the paths those do not.)</item>
/// <item><b>The memory is bounded by the MCU row, not the image.</b> The sample memory a decode holds is read
/// through <c>JpegDecodeStats.PeakPlaneBytes</c> and must not depend on the image's height.</item>
/// </list>
///
/// <para>The restart-interval, truncation and marker cases build or damage streams by hand because that is the
/// only way to reach <c>JpegCore</c>'s salvage code: an encoder never produces them.</para>
/// </summary>
public sealed class JpegStripDecodeTests
{
    private static readonly JpegScale[] Scales = [JpegScale.Full, JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth];

    // ------------------------------------------------------------ inputs

    internal static byte[] Pattern(int w, int h)
    {
        // Gradients, a checker and LCG noise: dense AC, sharp chroma edges, deterministic.
        var rgb = new byte[w * h * 3];
        var seed = 0x2545F491u;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                seed = seed * 1664525u + 1013904223u;
                var noise = (int)(seed >> 28);
                rgb[i + 0] = (byte)Math.Clamp(x * 255 / Math.Max(1, w - 1) + noise - 8, 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(y * 255 / Math.Max(1, h - 1) + (((x >> 2) ^ (y >> 2)) & 1) * 70, 0, 255);
                rgb[i + 2] = (byte)(128 + 100 * Math.Sin(x * 0.4 + y * 0.2));
            }
        }

        return rgb;
    }

    private static byte[] Encode(int w, int h, string? sampling, bool progressive = false, bool gray = false, bool cmyk = false) =>
        JpegDecoderOracleTests.EncodeJpeg(Pattern(w, h), w, h, 88, sampling, progressive, gray, cmyk);

    private static readonly (int W, int H)[] OddSizes =
        [(1, 1), (8, 8), (9, 17), (17, 9), (37, 23), (67, 45), (130, 7), (7, 130)];

    // ------------------------------------------------------------ results

    private readonly record struct Outcome(JpegDecodeStats Stats, int Width, int Height, byte[]? Pixels, string? Error);

    private static Outcome Run(byte[] jpeg, JpegScale scale, bool wholePlanes)
    {
        try
        {
            var stats = JpegDecoder.DecodeWithStats(jpeg, scale, out var image, wholePlanes);
            return new Outcome(stats, image.Width, image.Height, image.Pixels, null);
        }
        catch (InvalidDataException e)
        {
            return new Outcome(default, 0, 0, null, e.Message);
        }
    }

    private static void ShouldMatch(Outcome strips, Outcome planes, string label)
    {
        strips.Error.ShouldBe(planes.Error, label);
        if (planes.Pixels is null)
            return;

        strips.Width.ShouldBe(planes.Width, label);
        strips.Height.ShouldBe(planes.Height, label);
        var a = strips.Pixels!;
        var b = planes.Pixels;
        a.Length.ShouldBe(b.Length, label);
        var mismatch = a.AsSpan().CommonPrefixLength(b.AsSpan());
        if (mismatch != a.Length)
        {
            var px = mismatch / 4;
            Assert.Fail($"{label}: strips differ from whole planes at pixel ({px % planes.Width}, {px / planes.Width}) channel {mismatch % 4}: {a[mismatch]} vs {b[mismatch]}");
        }
    }

    /// <summary>The strip engine against the whole-plane engine, at every scale. Returns the full-scale strip outcome.</summary>
    private static Outcome ShouldAgreeAtEveryScale(byte[] jpeg, string label)
    {
        Outcome full = default;
        foreach (var scale in Scales)
        {
            var strips = Run(jpeg, scale, wholePlanes: false);
            var planes = Run(jpeg, scale, wholePlanes: true);
            ShouldMatch(strips, planes, $"{label} @{scale}");
            if (scale == JpegScale.Full)
                full = strips;
        }

        return full;
    }

    // ------------------------------------------------------------ stream anatomy

    private readonly record struct Frame(bool Progressive, int Width, int Height, (int Id, int H, int V)[] Components);

    /// <summary>Reads the SOF, so a test can see what the encoder really wrote rather than what it was asked for.</summary>
    private static Frame ReadFrame(byte[] j)
    {
        var pos = 2;
        while (pos + 4 <= j.Length)
        {
            var m = j[pos + 1];
            var len = (j[pos + 2] << 8) | j[pos + 3];
            if (m is 0xC0 or 0xC1 or 0xC2)
            {
                var h = (j[pos + 5] << 8) | j[pos + 6];
                var w = (j[pos + 7] << 8) | j[pos + 8];
                var n = j[pos + 9];
                var comps = new (int, int, int)[n];
                for (var i = 0; i < n; i++)
                    comps[i] = (j[pos + 10 + 3 * i], j[pos + 11 + 3 * i] >> 4, j[pos + 11 + 3 * i] & 15);
                return new Frame(m == 0xC2, w, h, comps);
            }

            pos += 2 + len;
        }

        throw new InvalidOperationException("no SOF");
    }

    /// <summary>Offset of the first SOS marker, found by walking the header segments.</summary>
    internal static int FirstSos(byte[] j)
    {
        var pos = 2;
        while (pos + 4 <= j.Length)
        {
            if (j[pos] != 0xFF)
                throw new InvalidOperationException("not at a marker");
            if (j[pos + 1] == 0xDA)
                return pos;
            pos += 2 + ((j[pos + 2] << 8) | j[pos + 3]);
        }

        throw new InvalidOperationException("no SOS");
    }

    private static int EoiOffset(byte[] j)
    {
        (j[^2], j[^1]).ShouldBe(((byte)0xFF, (byte)0xD9), "fixture should end in EOI");
        return j.Length - 2;
    }

    internal static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    internal static byte[] Slice(byte[] j, int from, int to) => j[from..to];

    /// <summary>The header with every APP0 (JFIF) segment removed, so colour is decided by the Adobe marker alone.</summary>
    private static byte[] HeaderWithoutJfif(byte[] j)
    {
        var sos = FirstSos(j);
        var kept = new List<byte> { 0xFF, 0xD8 };
        var pos = 2;
        while (pos < sos)
        {
            var len = (j[pos + 2] << 8) | j[pos + 3];
            if (j[pos + 1] != 0xE0)
                kept.AddRange(j.AsSpan(pos, 2 + len).ToArray());
            pos += 2 + len;
        }

        return kept.ToArray();
    }

    /// <summary>An APP14 "Adobe" segment: version 100, flags 0, and the colour-transform byte.</summary>
    private static byte[] AdobeSegment(byte transform) =>
    [
        0xFF, 0xEE, 0x00, 0x0E,
        (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e',
        0x00, 0x64, 0x00, 0x00, 0x00, 0x00, transform,
    ];

    // ============================================================ 1. bytes: strips == whole planes

    [Theory]
    [InlineData("1x1,1x1,1x1", false)] // 4:4:4
    [InlineData("1x1,1x1,1x1", true)]
    [InlineData("2x2,1x1,1x1", false)] // 4:2:0: the Hv2 kernel, far row across units
    [InlineData("2x2,1x1,1x1", true)]
    [InlineData("2x1,1x1,1x1", false)] // 4:2:2: H2, no far row
    [InlineData("2x1,1x1,1x1", true)]
    [InlineData("1x2,1x1,1x1", false)] // 4:4:0: V2
    [InlineData("1x2,1x1,1x1", true)]
    [InlineData("4x1,1x1,1x1", false)] // 4:1:1: horizontal factor 4, the generic kernel
    [InlineData("4x1,1x1,1x1", true)]
    [InlineData("4x2,1x1,1x1", false)] // horizontal 4 over vertical 2: still the generic kernel (libjpeg caps an MCU at 10 blocks)
    [InlineData("4x2,1x1,1x1", true)]
    [InlineData("2x4,1x1,1x1", false)] // vertical factor 4: generic, and the longest vertical window
    [InlineData("2x4,1x1,1x1", true)]
    [InlineData("2x2,1x2,2x1", false)] // three different kernels in one frame
    [InlineData("2x2,1x2,2x1", true)]
    [InlineData("1x4,1x2,1x1", false)] // vertical 1, 2 and 4: the worst strip retention
    [InlineData("1x4,1x2,1x1", true)]
    public void Colour_StripsMatchWholePlanes_OnOddSizes(string sampling, bool progressive)
    {
        foreach (var (w, h) in OddSizes)
        {
            var jpeg = Encode(w, h, sampling, progressive);

            // Guard against asserting nothing: the encoder must really have written the layout under test.
            var frame = ReadFrame(jpeg);
            frame.Progressive.ShouldBe(progressive);
            string.Join(",", frame.Components.Select(c => $"{c.H}x{c.V}")).ShouldBe(sampling, $"{w}x{h}");

            var full = ShouldAgreeAtEveryScale(jpeg, $"{sampling} {(progressive ? "progressive " : "")}{w}x{h}");
            full.Error.ShouldBeNull($"{sampling} {w}x{h} is a valid stream");
            full.Stats.WholePlanes.ShouldBeFalse($"{w}x{h} should stream through strips");
            full.Stats.Restarted.ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Grey_StripsMatchWholePlanes_OnOddSizes(bool progressive)
    {
        foreach (var (w, h) in OddSizes)
        {
            var jpeg = Encode(w, h, null, progressive, gray: true);
            ReadFrame(jpeg).Components.Length.ShouldBe(1);

            var full = ShouldAgreeAtEveryScale(jpeg, $"grey {(progressive ? "progressive " : "")}{w}x{h}");
            full.Error.ShouldBeNull($"grey {w}x{h} is a valid stream");
            full.Stats.WholePlanes.ShouldBeFalse($"{w}x{h} should stream through strips");
        }
    }

    [Fact]
    public void Cmyk_StripsMatchWholePlanes()
    {
        foreach (var (w, h) in new[] { (1, 1), (17, 9), (37, 23) })
        {
            var jpeg = Encode(w, h, null, cmyk: true);
            ReadFrame(jpeg).Components.Length.ShouldBe(4);
            var full = ShouldAgreeAtEveryScale(jpeg, $"cmyk {w}x{h}");
            full.Error.ShouldBeNull();
            full.Stats.WholePlanes.ShouldBeFalse();
        }
    }

    [Fact]
    public void RgbMarkedComponentIds_StripsMatchWholePlanes()
    {
        // Component ids 'R','G','B' make stb treat the samples as RGB, not YCbCr. Magick writes ids 1,2,3,
        // so patch both the SOF and the SOS.
        var jpeg = Encode(37, 23, "1x1,1x1,1x1");
        var pos = 2;
        ReadOnlySpan<byte> rgb = "RGB"u8;
        while (jpeg[pos + 1] != 0xDA)
        {
            if (jpeg[pos + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                for (var i = 0; i < 3; i++)
                    jpeg[pos + 10 + 3 * i] = rgb[i];
            }

            pos += 2 + ((jpeg[pos + 2] << 8) | jpeg[pos + 3]);
        }

        for (var i = 0; i < 3; i++)
            jpeg[pos + 5 + 2 * i] = rgb[i];

        var strips = Run(jpeg, JpegScale.Full, wholePlanes: false);
        strips.Error.ShouldBeNull();
        ShouldAgreeAtEveryScale(jpeg, "RGB ids");

        // ... and that it really took the RGB path: the samples are copied through, not converted.
        var asYcc = Run(Encode(37, 23, "1x1,1x1,1x1"), JpegScale.Full, wholePlanes: false);
        strips.Pixels.ShouldNotBe(asYcc.Pixels);
    }

    // ============================================================ 2. bytes: DecodeTo == Decode

    [Theory]
    [InlineData("1x1,1x1,1x1", false, false)]
    [InlineData("2x2,1x1,1x1", false, false)]
    [InlineData("2x1,1x1,1x1", false, false)]
    [InlineData("2x2,1x1,1x1", true, false)]
    [InlineData("1x1,1x1,1x1", true, false)]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    public void DecodeTo_EqualsDecode_AtEveryScale(string? sampling, bool progressive, bool gray)
    {
        foreach (var (w, h) in new[] { (37, 23), (1, 1), (17, 9) })
        {
            var jpeg = Encode(w, h, sampling, progressive, gray);
            foreach (var scale in Scales)
            {
                var label = $"{sampling ?? "grey"} {(progressive ? "progressive " : "")}{w}x{h} @{scale}";
                var expected = JpegDecoder.Decode(jpeg, scale);

                // Oversized destination, filled with a sentinel: DecodeTo must write exactly the image.
                var length = expected.Width * expected.Height * 4;
                var dest = new byte[length + 64];
                Array.Fill(dest, (byte)0xAB);
                var (dw, dh) = JpegDecoder.DecodeTo(jpeg, dest, scale);

                (dw, dh).ShouldBe((expected.Width, expected.Height), label);
                dest.AsSpan(0, length).SequenceEqual(expected.Pixels).ShouldBeTrue($"{label}: DecodeTo pixels differ from Decode");
                dest.AsSpan(length).ToArray().ShouldAllBe(b => b == 0xAB, $"{label}: DecodeTo wrote past the image");
            }
        }
    }

    [Fact]
    public void DecodeTo_TooSmallDestination_ThrowsBeforeWritingAnything()
    {
        // The size is checked against the frame header, before any row is decoded.
        var jpeg = Encode(64, 64, "2x2,1x1,1x1");
        var dest = new byte[64 * 64 * 4 - 1];
        Array.Fill(dest, (byte)0xAB);

        Should.Throw<ArgumentException>(() => JpegDecoder.DecodeTo(jpeg, dest));
        dest.ShouldAllBe(b => b == 0xAB);
    }

    // ============================================================ 3. memory: bounded by the MCU row

    /// <summary>
    /// The strips a decode of this frame should hold: for each component one unit of plane rows plus the
    /// retention margin, times the plane stride. This is the bound the design states, computed from the
    /// stream's own SOF rather than from the decoder's numbers.
    /// </summary>
    private static long ExpectedStripBytes(Frame f, JpegScale scale)
    {
        var b = 8 / (int)scale;
        var hMax = f.Components.Max(c => c.H);
        var mcuX = (f.Width + 8 * hMax - 1) / (8 * hMax);
        long total = 0;
        foreach (var (_, h, v) in f.Components)
        {
            var unitBlockRows = f.Components.Length == 1 ? 1 : v;
            total += (long)(unitBlockRows * b + 2) * (mcuX * h * b);
        }

        return total;
    }

    private static long WholePlaneBytes(Frame f, JpegScale scale)
    {
        var b = 8 / (int)scale;
        var hMax = f.Components.Max(c => c.H);
        var vMax = f.Components.Max(c => c.V);
        var mcuX = (f.Width + 8 * hMax - 1) / (8 * hMax);
        var mcuY = (f.Height + 8 * vMax - 1) / (8 * vMax);
        return f.Components.Sum(c => (long)(mcuX * c.H * b) * (mcuY * c.V * b));
    }

    [Theory]
    [InlineData("1x1,1x1,1x1", false)] // baseline 4:4:4
    [InlineData("2x2,1x1,1x1", false)] // baseline 4:2:0
    [InlineData("2x1,1x1,1x1", false)]
    [InlineData("1x1,1x1,1x1", true)]  // progressive: the coefficients stay whole, the samples must not
    [InlineData("2x2,1x1,1x1", true)]
    [InlineData(null, false)]          // grey, baseline
    [InlineData(null, true)]           // grey, progressive
    public void SampleMemory_DoesNotGrowWithImageHeight(string? sampling, bool progressive)
    {
        const int width = 600;
        var gray = sampling is null;
        var shortJpeg = Encode(width, 64, sampling, progressive, gray);
        var tallJpeg = Encode(width, 1280, sampling, progressive, gray);
        var tallFrame = ReadFrame(tallJpeg);

        foreach (var scale in Scales)
        {
            var label = $"{sampling ?? "grey"} {(progressive ? "progressive " : "")}@{scale}";
            var shortStats = JpegDecoder.DecodeWithStats(shortJpeg, scale, out _);
            var tallStats = JpegDecoder.DecodeWithStats(tallJpeg, scale, out _);

            shortStats.WholePlanes.ShouldBeFalse(label);
            tallStats.WholePlanes.ShouldBeFalse(label);
            tallStats.Restarted.ShouldBeFalse(label);

            // The claim: a 20x taller image holds exactly the same sample memory.
            tallStats.PeakPlaneBytes.ShouldBe(shortStats.PeakPlaneBytes, label);

            // And it is the few-MCU-rows figure the design states: at least the strips, at most twice
            // that (ArrayPool rounds a rent up to a power of two).
            var strips = ExpectedStripBytes(tallFrame, scale);
            tallStats.PeakPlaneBytes.ShouldBeGreaterThanOrEqualTo(strips, label);
            tallStats.PeakPlaneBytes.ShouldBeLessThanOrEqualTo(2 * strips, label);

            // ... which is a small fraction of what whole planes would hold.
            var whole = WholePlaneBytes(tallFrame, scale);
            (tallStats.PeakPlaneBytes * 8).ShouldBeLessThan(whole, $"{label}: strips {tallStats.PeakPlaneBytes} B vs whole planes {whole} B");
        }
    }

    [Fact]
    public void ScanAtTheIssueWidth_NeedsStripsNotThreeSixtyFourMebibytePlanes()
    {
        // Issue #28: a 5100×6600 4:4:4 scan rented three 64 MiB planes (33,660,000 bytes each, just over 2^25).
        // Strip memory is a function of the width and the sampling only, so a scan of that width, two MCU rows
        // tall, holds what the full-height one does, without encoding 33 megapixels in a test.
        const int width = 5100;
        var rgb = Pattern(width, 16);
        var jpeg = JpegEncoder.Encode(rgb, width, 16, 3, new JpegEncodeOptions { Subsampling = JpegSubsampling.Chroma444, Quality = 80 });
        ReadFrame(jpeg).Components.ShouldAllBe(c => c.H == 1 && c.V == 1);

        var stats = JpegDecoder.DecodeWithStats(jpeg, JpegScale.Full, out var image);
        image.Width.ShouldBe(width);
        stats.WholePlanes.ShouldBeFalse();

        const long issuePlaneBytes = 5100L * 6600;
        const long issueRentedBytes = 3 * 64L * 1024 * 1024;
        stats.PeakPlaneBytes.ShouldBeLessThan(issuePlaneBytes / 50);
        stats.PeakPlaneBytes.ShouldBeLessThan(issueRentedBytes / 500);
    }

    // ============================================================ 4. salvage paths: what stb produces today

    private static readonly (int H, int V)[][] RestartLayouts =
    [
        [(1, 1), (1, 1), (1, 1)], // 4:4:4
        [(2, 2), (1, 1), (1, 1)], // 4:2:0
        [(2, 1), (1, 1), (1, 1)], // 4:2:2
        [(1, 1)],                 // grey, restarts counted in blocks
    ];

    private static string Name((int H, int V)[] layout) => string.Join(",", layout.Select(c => $"{c.H}x{c.V}"));

    // Odd in both axes, and wide and tall enough for 4:2:0 to have several MCU rows and restart markers.
    private const int RestartW = 163, RestartH = 125;

    [Fact]
    public void RestartIntervals_StripsMatchWholePlanes()
    {
        // Interval = one MCU row, as the issue's scans have, and one that does not divide a row.
        foreach (var layout in RestartLayouts)
        {
            var hMax = layout.Max(c => c.H);
            var mcuX = (RestartW + 8 * hMax - 1) / (8 * hMax);
            foreach (var interval in new[] { mcuX, 3 })
            {
                var jpeg = BuildDcOnlyJpeg(RestartW, RestartH, layout, restartInterval: interval);
                var label = $"{Name(layout)} DRI {interval}";
                var full = ShouldAgreeAtEveryScale(jpeg, label);
                full.Error.ShouldBeNull(label);
                full.Stats.WholePlanes.ShouldBeFalse(label);
            }
        }
    }

    /// <summary>Offsets of every RSTn marker in a stream built by <see cref="BuildDcOnlyJpeg"/>, whose entropy data stuffs every 0xFF.</summary>
    internal static List<int> RstOffsets(byte[] j)
    {
        var offsets = new List<int>();
        for (var i = FirstSos(j); i + 1 < j.Length; i++)
        {
            if (j[i] == 0xFF && j[i + 1] is >= 0xD0 and <= 0xD7)
                offsets.Add(i);
        }

        return offsets;
    }

    [Fact]
    public void RestartIntervalRunsOut_RestOfImageIsZero_AsBefore()
    {
        // Where the scan ends early: the restart marker that should follow an interval is another marker,
        // is missing, or the stream ends. stb leaves the rest of the planes zero, and so must the strips,
        // including the part of the unit the scan stopped in, and blended with the rows above.
        foreach (var layout in RestartLayouts)
        {
            var hMax = layout.Max(c => c.H);
            var mcuX = (RestartW + 8 * hMax - 1) / (8 * hMax);
            foreach (var interval in new[] { mcuX, 3 })
            {
                var pristine = BuildDcOnlyJpeg(RestartW, RestartH, layout, restartInterval: interval);
                var rsts = RstOffsets(pristine);
                rsts.Count.ShouldBeGreaterThan(3);

                foreach (var which in new[] { 0, 1, rsts.Count / 2, rsts.Count - 1 })
                {
                    var at = rsts[which];

                    // The marker becomes EOI: the stream ends there.
                    var eoi = (byte[])pristine.Clone();
                    eoi[at + 1] = 0xD9;
                    // ... or an unknown marker: ProcessMarker gives up and the decode is salvaged.
                    var unknown = (byte[])pristine.Clone();
                    unknown[at + 1] = 0x01;
                    // ... or the marker is simply not there (and the entropy data runs on).
                    var missing = Concat(Slice(pristine, 0, at), Slice(pristine, at + 2, pristine.Length));

                    foreach (var (kind, jpeg) in new[] { ("EOI", eoi), ("unknown marker", unknown), ("missing", missing) })
                    {
                        var label = $"{Name(layout)} DRI {interval}, RST #{which} -> {kind}";
                        ShouldAgreeAtEveryScale(jpeg, label).Stats.WholePlanes.ShouldBeFalse(label);
                    }
                }
            }
        }
    }

    [Fact]
    public void TruncatedStreams_StripsMatchWholePlanes()
    {
        // Early EOF: the entropy decoder is fed zero bits to the end of the scan, or throws, and a progressive
        // stream never reaches its IDCT. Whichever it is, it must be the same under both engines.
        var sources = new Dictionary<string, byte[]>
        {
            ["baseline 4:2:0"] = Encode(61, 47, "2x2,1x1,1x1"),
            ["baseline 4:4:4"] = Encode(61, 47, "1x1,1x1,1x1"),
            ["baseline grey"] = Encode(61, 47, null, gray: true),
            ["progressive 4:2:0"] = Encode(61, 47, "2x2,1x1,1x1", progressive: true),
            ["progressive grey"] = Encode(61, 47, null, progressive: true, gray: true),
            ["restart 4:2:0"] = BuildDcOnlyJpeg(83, 61, [(2, 2), (1, 1), (1, 1)], restartInterval: 3),
        };

        foreach (var (name, jpeg) in sources)
        {
            for (var tenth = 1; tenth <= 9; tenth++)
            {
                var cut = Slice(jpeg, 0, jpeg.Length * tenth / 10);
                ShouldAgreeAtEveryScale(cut, $"{name} cut at {tenth * 10}%");
            }

            // Cut inside the header (before any scan) too.
            ShouldAgreeAtEveryScale(Slice(jpeg, 0, FirstSos(jpeg) + 3), $"{name} cut inside SOS");
        }
    }

    [Fact]
    public void NoScan_DecodesZeroPlanes_AsBefore()
    {
        // Header and EOI, no SOS: today's planes are zero and are converted anyway.
        foreach (var jpeg in new[] { Encode(37, 23, "2x2,1x1,1x1"), Encode(37, 23, null, gray: true), Encode(37, 23, "1x1,1x1,1x1", progressive: true) })
        {
            var headerOnly = Concat(Slice(jpeg, 0, FirstSos(jpeg)), [0xFF, 0xD9]);
            var full = ShouldAgreeAtEveryScale(headerOnly, "no scan");
            full.Error.ShouldBeNull();
            full.Pixels.ShouldNotBeNull();
        }
    }

    [Fact]
    public void ProgressiveStreamThatNeverFinishes_StaysZero_AsBefore()
    {
        // A bad marker after the first scan makes the decoder salvage and return without Finish():
        // the planes of a progressive stream are zero, whatever scans were read.
        var jpeg = Encode(61, 47, "2x2,1x1,1x1", progressive: true);
        var firstSos = FirstSos(jpeg);
        var secondSos = FindMarker(jpeg, 0xDA, firstSos + 4);
        var broken = Concat(Slice(jpeg, 0, secondSos), [0xFF, 0x02]);

        var full = ShouldAgreeAtEveryScale(broken, "progressive, bad marker");
        full.Error.ShouldBeNull();
        full.Pixels!.Where((_, i) => i % 4 != 3).Distinct().Count().ShouldBeLessThan(4, "a zero plane converts to a constant colour");
    }

    private static int FindMarker(byte[] j, byte marker, int from)
    {
        for (var i = from; i + 1 < j.Length; i++)
        {
            if (j[i] == 0xFF && j[i + 1] == marker)
                return i;
        }

        throw new InvalidOperationException($"no marker 0x{marker:X2}");
    }

    // ============================================================ 5. streams that cannot stream

    private static readonly (int H, int V)[][] SeparateScanLayouts =
    [
        [(1, 1), (1, 1), (1, 1)],
        [(2, 2), (1, 1), (1, 1)],
        [(2, 1), (1, 1), (1, 1)],
        [(1, 2), (1, 1), (1, 1)],
    ];

    [Fact]
    public void SeparateBaselineScans_KeepWholePlanes_AndDecodeLikeTheInterleavedStream()
    {
        // Baseline with one scan per component cannot stream. Same blocks coded interleaved (streaming) and
        // as three scans (whole planes) must decode identically: the strips against the old engine on a stream
        // whose block order is entirely different.
        foreach (var layout in SeparateScanLayouts)
        {
            foreach (var interval in new[] { 0, 4 })
            {
                var label = $"{Name(layout)} DRI {interval}";
                var interleaved = BuildDcOnlyJpeg(RestartW, RestartH, layout, restartInterval: interval);
                var separate = BuildDcOnlyJpeg(RestartW, RestartH, layout, separateScans: true, restartInterval: interval);

                foreach (var scale in Scales)
                {
                    var streamed = Run(interleaved, scale, wholePlanes: false);
                    var planed = Run(separate, scale, wholePlanes: false);
                    streamed.Error.ShouldBeNull(label);
                    planed.Error.ShouldBeNull(label);
                    streamed.Stats.WholePlanes.ShouldBeFalse(label);
                    planed.Stats.WholePlanes.ShouldBeTrue($"{label}: one scan per component must keep whole planes");
                    planed.Stats.Restarted.ShouldBeFalse($"{label}: the first scan already says so, no restart needed");

                    ShouldMatch(streamed, planed, $"{label} @{scale}");
                }
            }
        }
    }

    [Fact]
    public void WholePlanes_AreAllocatedAtTheirExactSize()
    {
        // The fallback allocates planes exactly instead of renting them (a rent rounds up to a power of two).
        var jpeg = BuildDcOnlyJpeg(83, 61, [(1, 1), (1, 1), (1, 1)], separateScans: true);
        var frame = ReadFrame(jpeg);
        var stats = JpegDecoder.DecodeWithStats(jpeg, JpegScale.Full, out _);

        stats.WholePlanes.ShouldBeTrue();
        stats.PeakPlaneBytes.ShouldBe(WholePlaneBytes(frame, JpegScale.Full));
    }

    [Fact]
    public void ASecondScanAfterTheStreamedOne_RestartsWithWholePlanes()
    {
        // A scan after the one that streamed overwrites its blocks in today's decoder; rows already delivered
        // cannot be taken back, so the decode starts over with whole planes. Not a valid file, but it must
        // not be decoded differently than before. Here the second scan is the first again.
        foreach (var jpeg in new[] { Encode(61, 47, "2x2,1x1,1x1"), Encode(61, 47, null, gray: true), BuildDcOnlyJpeg(83, 61, [(2, 2), (1, 1), (1, 1)], restartInterval: 3) })
        {
            var sos = FirstSos(jpeg);
            var scan = Slice(jpeg, sos, EoiOffset(jpeg));
            var twice = Concat(Slice(jpeg, 0, sos), scan, scan, [0xFF, 0xD9]);

            var single = Run(jpeg, JpegScale.Full, wholePlanes: false);
            var strips = Run(twice, JpegScale.Full, wholePlanes: false);
            strips.Error.ShouldBeNull();
            strips.Stats.Restarted.ShouldBeTrue("the second scan cannot be streamed after the first");
            strips.Stats.WholePlanes.ShouldBeTrue();
            strips.Pixels.ShouldBe(single.Pixels, "the same scan twice decodes to the same image");

            ShouldAgreeAtEveryScale(twice, "scan twice");
        }
    }

    [Fact]
    public void ASecondScanThatDiffers_OverwritesTheFirst_AsBefore()
    {
        // The second scan here is a different image's, so what it overwrites is observable: the result is
        // the second image where it covers (whatever the first scan's tables make of it), not the first's.
        var a = Encode(61, 47, "1x1,1x1,1x1");
        var inverted = Pattern(61, 47);
        for (var i = 0; i < inverted.Length; i++)
            inverted[i] = (byte)(255 - inverted[i]);
        var b = JpegDecoderOracleTests.EncodeJpeg(inverted, 61, 47, 88, "1x1,1x1,1x1");
        var sosA = FirstSos(a);
        var sosB = FirstSos(b);
        var mixed = Concat(Slice(a, 0, sosA), Slice(a, sosA, EoiOffset(a)), Slice(b, sosB, EoiOffset(b)), [0xFF, 0xD9]);

        // B's scan was coded with B's own Huffman tables, so under A's tables it may decode to anything or
        // fail; either way both engines must do the same, and the second scan does overwrite the first.
        ShouldAgreeAtEveryScale(mixed, "differing second scan");

        var strips = Run(mixed, JpegScale.Full, wholePlanes: false);
        if (strips.Error is null)
        {
            strips.Stats.Restarted.ShouldBeTrue();
            strips.Pixels.ShouldNotBe(Run(a, JpegScale.Full, wholePlanes: false).Pixels, "the second scan overwrote the first");
        }
    }

    [Fact]
    public void AdobeMarkerAfterTheScan_ChangesTheColourModel_AsBefore()
    {
        // Without JFIF, an Adobe transform of 0 means the three components are RGB. stb converts after the
        // whole stream is read, so a marker AFTER the scan counts, though the streamed rows were assembled
        // before it was seen: the decode starts over with whole planes. A marker BEFORE the scan streams.
        var source = Encode(37, 23, "1x1,1x1,1x1");
        var noJfif = HeaderWithoutJfif(source);
        var sos = FirstSos(source);
        var scan = Slice(source, sos, EoiOffset(source));
        var eoi = new byte[] { 0xFF, 0xD9 };

        var markerBefore = Concat(noJfif, AdobeSegment(0), scan, eoi);
        var markerAfter = Concat(noJfif, scan, AdobeSegment(0), eoi);
        var noMarker = Concat(noJfif, scan, eoi);

        var before = ShouldAgreeAtEveryScale(markerBefore, "Adobe before scan");
        before.Stats.WholePlanes.ShouldBeFalse("a marker before the scan is known when the scan starts");
        before.Stats.Restarted.ShouldBeFalse();

        var after = ShouldAgreeAtEveryScale(markerAfter, "Adobe after scan");
        after.Stats.Restarted.ShouldBeTrue("the colour model changed after rows were streamed");
        after.Pixels.ShouldBe(before.Pixels, "the same final state, the same colours");

        var none = ShouldAgreeAtEveryScale(noMarker, "no Adobe marker");
        none.Stats.Restarted.ShouldBeFalse();
        none.Pixels.ShouldNotBe(before.Pixels, "RGB-marked and YCbCr must differ, or this test proves nothing");
    }

    [Fact]
    public void DnlSegment_IsValidatedAfterTheScan_AsBefore()
    {
        var jpeg = Encode(37, 23, "2x2,1x1,1x1");
        var sos = FirstSos(jpeg);
        var scan = Slice(jpeg, sos, EoiOffset(jpeg));
        byte[] Dnl(int height) => [0xFF, 0xDC, 0x00, 0x04, (byte)(height >> 8), (byte)height];

        var matching = Concat(Slice(jpeg, 0, sos), scan, Dnl(23), [0xFF, 0xD9]);
        var plain = Run(jpeg, JpegScale.Full, wholePlanes: false);
        var withDnl = ShouldAgreeAtEveryScale(matching, "DNL matching");
        withDnl.Error.ShouldBeNull();
        withDnl.Pixels.ShouldBe(plain.Pixels);

        var wrong = Concat(Slice(jpeg, 0, sos), scan, Dnl(24), [0xFF, 0xD9]);
        var strips = Run(wrong, JpegScale.Full, wholePlanes: false);
        var planes = Run(wrong, JpegScale.Full, wholePlanes: true);
        strips.Error.ShouldNotBeNull();
        strips.Error.ShouldBe(planes.Error);
    }

    // ============================================================ hand-built baseline streams

    /// <summary>
    /// A baseline JPEG whose blocks are DC-only: no encoder, so any layout, restart interval and scan structure
    /// can be written, and the same block levels can be coded interleaved or one scan per component. Levels sweep
    /// both signs and differ per component and block, so a neighbouring block, row or component read in place of
    /// the right one changes pixels. Quantisation is 4 (every block is flat at <c>128 + level/2</c>).
    /// </summary>
    internal static byte[] BuildDcOnlyJpeg(int width, int height, (int H, int V)[] comps, bool separateScans = false, int restartInterval = 0)
    {
        var o = new List<byte>();

        void Marker(byte m, params byte[] payload)
        {
            o.Add(0xFF);
            o.Add(m);
            if (m is 0xD8 or 0xD9)
                return;
            var len = payload.Length + 2;
            o.Add((byte)(len >> 8));
            o.Add((byte)len);
            o.AddRange(payload);
        }

        var hMax = comps.Max(c => c.H);
        var vMax = comps.Max(c => c.V);
        var mcuX = (width + 8 * hMax - 1) / (8 * hMax);
        var mcuY = (height + 8 * vMax - 1) / (8 * vMax);

        Marker(0xD8);

        var dqt = new byte[65];
        Array.Fill(dqt, (byte)4, 1, 64);
        Marker(0xDB, dqt);

        var sof = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)comps.Length };
        for (var c = 0; c < comps.Length; c++)
        {
            sof.Add((byte)(c + 1));
            sof.Add((byte)((comps[c].H << 4) | comps[c].V));
            sof.Add(0);
        }

        Marker(0xC0, sof.ToArray());

        // DC: the twelve categories as 4-bit codes 0..11. AC: one 1-bit code, for EOB.
        var dc = new byte[1 + 16 + 12];
        dc[1 + 3] = 12;
        for (var i = 0; i < 12; i++)
            dc[1 + 16 + i] = (byte)i;
        Marker(0xC4, dc);

        var ac = new byte[1 + 16 + 1];
        ac[0] = 0x10;
        ac[1] = 1;
        Marker(0xC4, ac);

        if (restartInterval > 0)
            Marker(0xDD, (byte)(restartInterval >> 8), (byte)restartInterval);

        var acc = 0;
        var nbits = 0;

        void Put(int value, int count)
        {
            acc = (acc << count) | (value & ((1 << count) - 1));
            nbits += count;
            while (nbits >= 8)
            {
                var b = (byte)(acc >> (nbits - 8));
                o.Add(b);
                if (b == 0xFF)
                    o.Add(0x00);
                nbits -= 8;
                acc &= (1 << nbits) - 1;
            }
        }

        void Align()
        {
            if (nbits > 0)
                Put((1 << (8 - nbits)) - 1, 8 - nbits);
        }

        var pred = new int[comps.Length];
        var rst = 0;

        void Restart()
        {
            Align();
            o.Add(0xFF);
            o.Add((byte)(0xD0 + (rst++ & 7)));
            Array.Clear(pred);
        }

        void Block(int c, int bx, int by, bool real)
        {
            var level = real ? ((bx * 37 + by * 91 + c * 53 + (bx ^ by) * 7) % 401) - 200 : 0;
            var diff = level - pred[c];
            pred[c] = level;
            var cat = 0;
            for (var a = Math.Abs(diff); a > 0; a >>= 1)
                cat++;
            Put(cat, 4);
            if (cat > 0)
                Put(diff >= 0 ? diff : diff + (1 << cat) - 1, cat);
            Put(0, 1);
        }

        int CompW(int c) => (width * comps[c].H + hMax - 1) / hMax;
        int CompH(int c) => (height * comps[c].V + vMax - 1) / vMax;

        if (!separateScans)
        {
            var sos = new List<byte> { (byte)comps.Length };
            for (var c = 0; c < comps.Length; c++)
            {
                sos.Add((byte)(c + 1));
                sos.Add(0x00);
            }

            sos.AddRange([0, 63, 0]);
            Marker(0xDA, sos.ToArray());

            var mcus = 0;
            for (var my = 0; my < mcuY; my++)
            {
                for (var mx = 0; mx < mcuX; mx++)
                {
                    if (restartInterval > 0 && mcus > 0 && mcus % restartInterval == 0)
                        Restart();
                    mcus++;

                    // A single-component frame is scanned on its own block grid, one block per "MCU".
                    for (var c = 0; c < comps.Length; c++)
                    {
                        for (var y = 0; y < comps[c].V; y++)
                        {
                            for (var x = 0; x < comps[c].H; x++)
                            {
                                var bx = mx * comps[c].H + x;
                                var by = my * comps[c].V + y;
                                Block(c, bx, by, bx < (CompW(c) + 7) / 8 && by < (CompH(c) + 7) / 8);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            for (var c = 0; c < comps.Length; c++)
            {
                Marker(0xDA, (byte)1, (byte)(c + 1), 0x00, 0, 63, 0);
                Array.Clear(pred);
                var blocks = 0;
                for (var by = 0; by < (CompH(c) + 7) / 8; by++)
                {
                    for (var bx = 0; bx < (CompW(c) + 7) / 8; bx++)
                    {
                        if (restartInterval > 0 && blocks > 0 && blocks % restartInterval == 0)
                            Restart();
                        blocks++;
                        Block(c, bx, by, true);
                    }
                }

                Align();
            }
        }

        Align();
        Marker(0xD9);
        return o.ToArray();
    }
}
