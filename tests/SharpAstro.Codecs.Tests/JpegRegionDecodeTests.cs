using System.Buffers.Binary;
using ImageMagick;
using SharpAstro.Jpeg;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <c>JpegDecoder.DecodeRegionTo</c> decodes one rectangle of the output, and its contract is exactness: the bytes
/// are the same crop of <c>Decode</c> at the same scale. Everything here asserts that, from different angles:
///
/// <list type="number">
/// <item><b>The sweep</b> (<see cref="Region_EqualsCropOfFullDecode"/>): every sampling layout, at every scale, over
/// baseline without restarts, baseline with restart intervals of one MCU row, of three MCUs (which does not divide a
/// row) and of one, progressive, and the whole-plane fallback; on small odd-sized images, with region origins and
/// sizes chosen to put edges on, just inside and just outside every MCU, block and chroma-neighbourhood boundary.
/// A small image is also swept exhaustively, every origin and size.</item>
/// <item><b>The damaged streams</b>: restart markers that are missing, wrong or out of sequence, and truncations. A
/// skip-ahead that trusted its markers would decode wrong pixels from them; it must fall back to what a full decode
/// does, and then agree with it.</item>
/// <item><b>The cost</b>, through the internal seam (<c>JpegDecodeStats</c>): how many MCU rows were entropy-decoded
/// and transformed, and how much strip memory was held, on an image with row restarts.</item>
/// </list>
///
/// <para>The expected side is always <c>Decode</c> itself, not a second implementation: whatever the full decode does
/// (stb's rounding, its edge rules, its salvage), the region must do too.</para>
/// </summary>
public sealed class JpegRegionDecodeTests
{
    private static readonly JpegScale[] Scales = [JpegScale.Full, JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth];

    // ------------------------------------------------------------ fixtures

    private enum Kind
    {
        Baseline,        // no restart markers: entropy decoding has to run from the top
        RestartRow,      // DRI = one MCU row, as the issue's scans have
        RestartOdd,      // DRI = 3 MCUs: does not divide a row, so intervals start mid-row
        RestartOne,      // DRI = 1: a marker after every MCU
        Progressive,     // every scan decoded, the IDCT and assembly narrowed
        ProgressiveRestart,
        WholePlanes,     // baseline, forced onto the whole-plane engine: only the assembly is cropped
    }

    private static readonly string?[] Layouts =
    [
        "1x1,1x1,1x1", // 4:4:4
        "2x1,1x1,1x1", // 4:2:2: H2, no far row
        "2x2,1x1,1x1", // 4:2:0: Hv2, far row across units
        "1x2,1x1,1x1", // 4:4:0: V2
        "4x1,1x1,1x1", // 4:1:1: the generic kernel
        "2x4,1x1,1x1", // vertical factor 4: generic, the longest vertical window
        "1x4,1x2,1x1", // vertical 1, 2 and 4 in one frame
        "2x2,1x2,2x1", // three different kernels in one frame
        null,          // grey, restarts counted in blocks
    ];

    public static IEnumerable<object[]> Matrix() =>
        from layout in Layouts
        from kind in Enum.GetValues<Kind>()
        select new object[] { layout ?? "grey", kind };

    private static string? LayoutOf(string name) => name == "grey" ? null : name;

    private static readonly (int W, int H)[] Sizes = [(37, 23), (67, 70), (130, 7), (7, 130), (33, 33), (1, 1), (16, 16)];

    private static byte[] Magick(int w, int h, string? sampling, bool progressive, int? restartInterval)
    {
        var settings = new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "RGB");
        using var img = new MagickImage();
        img.ReadPixels(JpegStripDecodeTests.Pattern(w, h), settings);
        img.Quality = 88;
        if (sampling != null)
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", sampling);
        if (progressive)
            img.Settings.Interlace = Interlace.Jpeg;
        if (sampling == null)
            img.ColorSpace = ColorSpace.Gray;
        if (restartInterval is { } r)
            img.Settings.SetDefine(MagickFormat.Jpeg, "restart-interval", r.ToString());
        return img.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>The SOF as the encoder really wrote it, and the DRI as the stream really carries it (0 if none).</summary>
    private readonly record struct Anatomy(bool Progressive, int Width, int Height, (int H, int V)[] Components, int RestartInterval)
    {
        public int HMax => Components.Max(c => c.H);

        /// <summary>MCUs (blocks, for one component) in a row of the scan: what a restart interval of "one row" is.</summary>
        public int McusPerRow => Components.Length == 1 ? (Width + 7) / 8 : (Width + 8 * HMax - 1) / (8 * HMax);
    }

    private static Anatomy Read(byte[] jpeg)
    {
        var restart = 0;
        Anatomy? frame = null;
        foreach (var seg in JpegSegmentScanner.Scan(jpeg))
        {
            var p = seg.Payload(jpeg);
            if (seg.Marker == 0xDD)
                restart = BinaryPrimitives.ReadUInt16BigEndian(p);
            else if (seg.Marker is 0xC0 or 0xC1 or 0xC2)
            {
                var comps = new (int, int)[p[5]];
                for (var i = 0; i < comps.Length; i++)
                    comps[i] = (p[7 + 3 * i] >> 4, p[7 + 3 * i] & 15);
                frame = new Anatomy(seg.Marker == 0xC2, BinaryPrimitives.ReadUInt16BigEndian(p[3..]), BinaryPrimitives.ReadUInt16BigEndian(p[1..]), comps, 0);
            }
        }

        return frame!.Value with { RestartInterval = restart };
    }

    /// <summary>
    /// Builds a stream of the given kind, and checks that what was built is what was asked for: the encoder's
    /// restart-interval define is the only thing that makes a DRI, and a regeneration (a different native library) that
    /// silently stopped honouring it would turn every restart case into a plain-baseline one that still passes.
    /// </summary>
    private static byte[] Build(int w, int h, string? sampling, Kind kind)
    {
        var progressive = kind is Kind.Progressive or Kind.ProgressiveRestart;

        // The first pass finds the MCU row width the encoder chose, to size "one MCU row" from the stream itself.
        var perRow = kind is Kind.RestartRow or Kind.ProgressiveRestart ? Read(Magick(w, h, sampling, progressive, null)).McusPerRow : 0;
        int? interval = kind switch
        {
            Kind.RestartRow or Kind.ProgressiveRestart => perRow,
            Kind.RestartOdd => 3,
            Kind.RestartOne => 1,
            _ => null,
        };

        var jpeg = interval is null ? Magick(w, h, sampling, progressive, null) : Magick(w, h, sampling, progressive, interval);
        var a = Read(jpeg);
        a.Progressive.ShouldBe(progressive, $"{sampling} {kind} {w}x{h}");
        a.RestartInterval.ShouldBe(interval ?? 0, $"{sampling} {kind} {w}x{h}: the stream's DRI");
        string.Join(",", a.Components.Select(c => $"{c.H}x{c.V}")).ShouldBe(sampling ?? "1x1", $"{kind} {w}x{h}");
        return jpeg;
    }

    // ------------------------------------------------------------ the comparison

    /// <summary>Region origins and sizes along one axis: edges on, just inside and just outside every boundary that matters.</summary>
    private static List<(int Start, int Len)> Axis(int n, bool exhaustive)
    {
        var result = new List<(int, int)>();
        if (exhaustive)
        {
            for (var s = 0; s < n; s++)
                for (var l = 1; l <= n - s; l++)
                    result.Add((s, l));
            return result;
        }

        // 1, 2, 3 reach the margin of a horizontal upsampler; 7/8/9, 15/16/17, 31/32/33 straddle blocks and MCUs.
        var starts = new SortedSet<int> { 0, 1, 2, 3, 7, 8, 9, 15, 16, 17, 31, 32, 33, n / 2, n - 1, n - 2, n - 9, n - 17 };
        foreach (var s in starts.Where(s => s >= 0 && s < n))
        {
            var lens = new SortedSet<int> { 1, 2, 3, 8, 9, 16, 17, 33, n - s, n - s - 1, n - s - 8, n - s - 9 };
            foreach (var l in lens.Where(l => l >= 1 && l <= n - s))
                result.Add((s, l));
        }

        return result;
    }

    /// <summary>
    /// A deterministic selection from the product of the two axes: the whole image, a spread of full-width and
    /// full-height bands, plus <paramref name="budget"/> others. Every rectangle when there are at most
    /// <paramref name="exhaustiveUpTo"/> of them, which is always the case for the smallest images and scales.
    /// </summary>
    private static IEnumerable<JpegRegion> Regions(int w, int h, int budget, int exhaustiveUpTo = 400)
    {
        var small = (long)w * (w + 1) / 2 * h * (h + 1) / 2 <= exhaustiveUpTo;
        var xs = Axis(w, small);
        var ys = Axis(h, small);
        if (small)
        {
            foreach (var (x, cw) in xs)
                foreach (var (y, ch) in ys)
                    yield return new JpegRegion(x, y, cw, ch);
            yield break;
        }

        yield return new JpegRegion(0, 0, w, h);
        for (var i = 0; i < ys.Count; i++)
        {
            if (i % 4 == 0 || ys[i].Start + ys[i].Len == h)
                yield return new JpegRegion(0, ys[i].Start, w, ys[i].Len);
        }

        for (var i = 0; i < xs.Count; i++)
        {
            if (i % 4 == 0 || xs[i].Start + xs[i].Len == w)
                yield return new JpegRegion(xs[i].Start, 0, xs[i].Len, h);
        }

        var seed = 0x9E3779B9u;
        for (var i = 0; i < budget; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            var (x, cw) = xs[(int)((seed >> 8) % (uint)xs.Count)];
            seed = seed * 1664525u + 1013904223u;
            var (y, ch) = ys[(int)((seed >> 8) % (uint)ys.Count)];
            yield return new JpegRegion(x, y, cw, ch);
        }
    }

    private static void ShouldBeCropOf(JpegImage full, JpegRegion r, byte[] actual, string label)
    {
        for (var row = 0; row < r.Height; row++)
        {
            var expected = full.Pixels.AsSpan(((r.Y + row) * full.Width + r.X) * 4, r.Width * 4);
            var got = actual.AsSpan(row * r.Width * 4, r.Width * 4);
            var at = expected.CommonPrefixLength(got);
            if (at != expected.Length)
            {
                Assert.Fail($"{label} region {r}: differs from the crop of the full decode at pixel " +
                            $"({r.X + at / 4}, {r.Y + row}) channel {at % 4}: got {got[at]}, expected {expected[at]}");
            }
        }
    }

    /// <summary>Decodes <paramref name="r"/> into a sentinel-filled buffer: every byte of the region is written and equal, nothing past it is touched.</summary>
    private static void AssertRegionMatches(byte[] jpeg, JpegImage full, JpegScale scale, JpegRegion r, string label, bool wholePlanes = false)
    {
        const int pad = 32;
        var length = r.Width * r.Height * 4;
        var dest = new byte[length + pad];
        Array.Fill(dest, (byte)0xAB);

        if (wholePlanes)
            JpegDecoder.DecodeRegionToWithStats(jpeg, r, dest, scale, forceWholePlanes: true);
        else
            JpegDecoder.DecodeRegionTo(jpeg, r, dest, scale).ShouldBe((r.Width, r.Height), label);

        dest.AsSpan(length).ToArray().ShouldAllBe(b => b == 0xAB, $"{label} region {r}: wrote past the region");
        ShouldBeCropOf(full, r, dest, label);
    }

    // ============================================================ 1. the sweep

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Region_EqualsCropOfFullDecode(string layoutName, object kindObject)
    {
        var kind = (Kind)kindObject;
        var sampling = LayoutOf(layoutName);
        foreach (var (w, h) in Sizes)
        {
            var jpeg = Build(w, h, sampling, kind);
            foreach (var scale in Scales)
            {
                var label = $"{layoutName} {kind} {w}x{h} @{scale}";
                var full = JpegDecoder.Decode(jpeg, scale);
                foreach (var region in Regions(full.Width, full.Height, budget: 160))
                    AssertRegionMatches(jpeg, full, scale, region, label, wholePlanes: kind == Kind.WholePlanes);
            }
        }
    }

    [Theory]
    [InlineData(false, 3)] // JpegEncoder: 4:4:4 and 4:2:0 baseline, grey (one channel), no restart markers
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    [InlineData(true, 1)]
    public void Region_EqualsCropOfFullDecode_OnOurOwnEncoder(bool chroma420, int channels)
    {
        foreach (var (w, h) in new[] { (37, 23), (67, 70), (130, 7), (33, 33) })
        {
            var pixels = new byte[w * h * channels];
            var rgb = JpegStripDecodeTests.Pattern(w, h);
            for (var i = 0; i < w * h; i++)
                for (var c = 0; c < channels; c++)
                    pixels[i * channels + c] = rgb[i * 3 + c];

            var jpeg = JpegEncoder.Encode(pixels, w, h, channels,
                new JpegEncodeOptions { Quality = 85, Subsampling = chroma420 ? JpegSubsampling.Chroma420 : JpegSubsampling.Chroma444 });
            Read(jpeg).RestartInterval.ShouldBe(0);

            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                foreach (var region in Regions(full.Width, full.Height, budget: 200))
                    AssertRegionMatches(jpeg, full, scale, region, $"encoder 420={chroma420} ch={channels} {w}x{h} @{scale}");
            }
        }
    }

    [Fact]
    public void EveryRegionOfASmallImage_EqualsCropOfFullDecode()
    {
        // Exhaustive: every origin and size, 4:2:0 (the layout with a far row and a horizontal pair), every scale, one
        // MCU row per restart interval, a non-dividing interval, none, and progressive. 19x11 is two MCU columns and
        // rows plus a partial one; Regions() enumerates all of its ~11,000 rectangles.
        foreach (var kind in new[] { Kind.Baseline, Kind.RestartRow, Kind.RestartOdd, Kind.Progressive })
        {
            var jpeg = Build(19, 11, "2x2,1x1,1x1", kind);
            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                var count = 0;
                foreach (var region in Regions(full.Width, full.Height, budget: 0, exhaustiveUpTo: 20_000))
                {
                    AssertRegionMatches(jpeg, full, scale, region, $"4:2:0 {kind} 19x11 @{scale}");
                    count++;
                }

                var w = full.Width;
                var h = full.Height;
                count.ShouldBe(w * (w + 1) / 2 * (h * (h + 1) / 2), $"{kind} @{scale}: the sweep must cover every rectangle");
            }
        }
    }

    [Fact]
    public void ARegionTheSizeOfTheImage_IsTheDecodedImage()
    {
        foreach (var kind in Enum.GetValues<Kind>())
        {
            var jpeg = Build(67, 45, "2x2,1x1,1x1", kind);
            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                var dest = new byte[full.Pixels.Length];
                JpegDecoder.DecodeRegionTo(jpeg, new JpegRegion(0, 0, full.Width, full.Height), dest, scale)
                    .ShouldBe((full.Width, full.Height));
                dest.ShouldBe(full.Pixels, $"{kind} @{scale}");
            }
        }
    }

    [Fact]
    public void CmykAndRgbMarked_RegionsMatchToo()
    {
        // The colour paths other than plain YCbCr and grey: Adobe CMYK (YCCK-less) and 'R','G','B' component ids.
        var cmyk = JpegDecoderOracleTests.EncodeJpeg(JpegStripDecodeTests.Pattern(37, 23), 37, 23, 88, null, false, false, cmyk: true);
        var rgbIds = Build(37, 23, "1x1,1x1,1x1", Kind.Baseline);
        var pos = 2;
        ReadOnlySpan<byte> ids = "RGB"u8;
        while (rgbIds[pos + 1] != 0xDA)
        {
            if (rgbIds[pos + 1] is 0xC0 or 0xC1 or 0xC2)
                for (var i = 0; i < 3; i++)
                    rgbIds[pos + 10 + 3 * i] = ids[i];
            pos += 2 + ((rgbIds[pos + 2] << 8) | rgbIds[pos + 3]);
        }

        for (var i = 0; i < 3; i++)
            rgbIds[pos + 5 + 2 * i] = ids[i];

        foreach (var (name, jpeg) in new[] { ("cmyk", cmyk), ("rgb ids", rgbIds) })
        {
            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                foreach (var region in Regions(full.Width, full.Height, budget: 120))
                    AssertRegionMatches(jpeg, full, scale, region, $"{name} @{scale}");
            }
        }
    }

    // ============================================================ 2. damaged streams

    private static readonly (int H, int V)[][] DcLayouts =
    [
        [(1, 1), (1, 1), (1, 1)],
        [(2, 2), (1, 1), (1, 1)],
        [(2, 1), (1, 1), (1, 1)],
        [(1, 1)],
    ];

    /// <summary>
    /// Compares every region of the (possibly damaged) stream with the same crop of the full decode, at every scale.
    /// Where the full decode throws there is no crop to compare with: what the region does there is not specified
    /// (it may not reach the damage at all).
    /// </summary>
    private static int ShouldMatchWhereFullDecodeSucceeds(byte[] jpeg, string label, int budget = 100)
    {
        var compared = 0;
        foreach (var scale in Scales)
        {
            JpegImage full;
            try
            {
                full = JpegDecoder.Decode(jpeg, scale);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            foreach (var region in Regions(full.Width, full.Height, budget))
            {
                AssertRegionMatches(jpeg, full, scale, region, $"{label} @{scale}");
                compared++;
            }
        }

        return compared;
    }

    [Fact]
    public void DamagedRestartMarkers_FallBackToWhatAFullDecodeDoes()
    {
        // A stream whose restart markers do not check out cannot be skipped through: the interval that a count of
        // markers lands on is not the interval the full decode would be in. The marker becomes EOI (the stream ends
        // there), an unknown marker (the decode is salvaged), or goes missing (everything after it is one marker out
        // of sequence). Each case is compared at damage points before, inside and after the region.
        var compared = 0;
        foreach (var layout in DcLayouts)
        {
            var hMax = layout.Max(c => c.H);
            var perRow = layout.Length == 1 ? (163 + 7) / 8 : (163 + 8 * hMax - 1) / (8 * hMax);
            foreach (var interval in new[] { perRow, 3 })
            {
                var pristine = JpegStripDecodeTests.BuildDcOnlyJpeg(163, 125, layout, restartInterval: interval);
                Read(pristine).RestartInterval.ShouldBe(interval);
                var rsts = JpegStripDecodeTests.RstOffsets(pristine);
                rsts.Count.ShouldBeGreaterThan(3);

                foreach (var which in new[] { 0, 1, rsts.Count / 2, rsts.Count - 1 })
                {
                    var at = rsts[which];
                    var eoi = (byte[])pristine.Clone();
                    eoi[at + 1] = 0xD9;
                    var unknown = (byte[])pristine.Clone();
                    unknown[at + 1] = 0x01;
                    var missing = JpegStripDecodeTests.Concat(pristine[..at], pristine[(at + 2)..]);

                    foreach (var (kind, jpeg) in new[] { ("EOI", eoi), ("unknown marker", unknown), ("missing", missing) })
                    {
                        var label = $"{string.Join(",", layout.Select(c => $"{c.H}x{c.V}"))} DRI {interval}, RST #{which} -> {kind}";
                        compared += ShouldMatchWhereFullDecodeSucceeds(jpeg, label, budget: 40);
                    }
                }
            }
        }

        compared.ShouldBeGreaterThan(0, "the full decode of a damaged stream must succeed somewhere, or this compares nothing");
    }

    [Fact]
    public void RestartMarkersOutOfSequence_AreNotSkippedThrough()
    {
        // RST numbers cycle 0..7. Swapping two markers' numbers leaves the count right and the sequence wrong; a full
        // decode takes any RSTn as a restart and does not notice, so the region must produce the same pixels. Interval 3
        // over 4:2:0 gives well over eight markers, so the cycle wraps.
        var pristine = JpegStripDecodeTests.BuildDcOnlyJpeg(163, 125, [(2, 2), (1, 1), (1, 1)], restartInterval: 3);
        var rsts = JpegStripDecodeTests.RstOffsets(pristine);
        rsts.Count.ShouldBeGreaterThan(10);

        foreach (var which in new[] { 2, 9 })
        {
            var swapped = (byte[])pristine.Clone();
            (swapped[rsts[which] + 1], swapped[rsts[which + 1] + 1]) = (swapped[rsts[which + 1] + 1], swapped[rsts[which] + 1]);
            ShouldMatchWhereFullDecodeSucceeds(swapped, $"markers {which} and {which + 1} swapped", budget: 60).ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void TruncatedStreams_RegionsMatchTheFullDecodeOfTheTruncation()
    {
        // Early EOF: the entropy decoder is fed zero bits to the end of the scan, or throws, and a progressive stream
        // never reaches its IDCT. A region must see the same bytes the full decode does wherever the full decode
        // produces an image at all.
        var sources = new Dictionary<string, byte[]>
        {
            ["baseline 4:2:0"] = Build(61, 47, "2x2,1x1,1x1", Kind.Baseline),
            ["restart 4:2:0"] = Build(61, 47, "2x2,1x1,1x1", Kind.RestartRow),
            ["restart odd 4:4:4"] = Build(61, 47, "1x1,1x1,1x1", Kind.RestartOdd),
            ["baseline grey"] = Build(61, 47, null, Kind.Baseline),
            ["progressive 4:2:0"] = Build(61, 47, "2x2,1x1,1x1", Kind.Progressive),
            ["hand-built restart 4:2:0"] = JpegStripDecodeTests.BuildDcOnlyJpeg(83, 61, [(2, 2), (1, 1), (1, 1)], restartInterval: 3),
        };

        foreach (var (name, jpeg) in sources)
        {
            var compared = 0;
            for (var tenth = 1; tenth <= 9; tenth++)
                compared += ShouldMatchWhereFullDecodeSucceeds(jpeg[..(jpeg.Length * tenth / 10)], $"{name} cut at {tenth * 10}%", budget: 40);

            compared.ShouldBeGreaterThan(0, $"{name}: some truncation must still decode, or this proves nothing");
        }
    }

    [Fact]
    public void WholePlaneStreams_AreCropped()
    {
        // One scan per component cannot stream: whole planes, then the crop. Interleaved blocks and separate scans
        // carry the same levels, so they decode to the same image, and a region of either is that image's crop.
        foreach (var layout in DcLayouts)
        {
            foreach (var interval in new[] { 0, 4 })
            {
                var separate = JpegStripDecodeTests.BuildDcOnlyJpeg(83, 61, layout, separateScans: true, restartInterval: interval);
                JpegDecoder.DecodeWithStats(separate, JpegScale.Full, out _).WholePlanes.ShouldBeTrue();
                ShouldMatchWhereFullDecodeSucceeds(separate, $"separate scans DRI {interval}").ShouldBeGreaterThan(0);
            }
        }
    }

    [Fact]
    public void ASecondScan_IsSeenWhenTheRegionReachesTheEndOfTheScan()
    {
        // A scan after the streamed one overwrites its blocks in the full decode, which therefore decodes again
        // with whole planes. A region that stops before the end of the first scan never reads that far (documented on
        // DecodeRegionTo); one that reaches the end does, and must agree with the full decode.
        var jpeg = Build(61, 47, "2x2,1x1,1x1", Kind.RestartRow);
        var sos = JpegStripDecodeTests.FirstSos(jpeg);
        var scan = jpeg[sos..^2];
        var twice = JpegStripDecodeTests.Concat(jpeg[..sos], scan, scan, [0xFF, 0xD9]);

        var full = JpegDecoder.Decode(twice);
        JpegDecoder.DecodeWithStats(twice, JpegScale.Full, out _).Restarted.ShouldBeTrue();
        foreach (var region in new[] { new JpegRegion(0, 40, 61, 7), new JpegRegion(10, 30, 20, 17), new JpegRegion(0, 0, 61, 47) })
            AssertRegionMatches(twice, full, JpegScale.Full, region, "second scan");
    }

    // ============================================================ 3. the arguments

    [Fact]
    public void BadRegionsAndDestinations_ThrowArgumentException_WithoutWritingAnything()
    {
        var jpeg = Build(67, 45, "2x2,1x1,1x1", Kind.RestartRow);
        foreach (var scale in Scales)
        {
            var (w, h) = JpegDecoder.ReadInfo(jpeg).ScaledSize(scale);
            var bad = new[]
            {
                new JpegRegion(0, 0, 0, 1),            // empty
                new JpegRegion(0, 0, 1, 0),
                new JpegRegion(0, 0, 0, 0),
                new JpegRegion(0, 0, -1, 1),
                new JpegRegion(0, 0, 1, -1),
                new JpegRegion(-1, 0, 1, 1),           // negative origin
                new JpegRegion(0, -1, 1, 1),
                new JpegRegion(0, 0, w + 1, 1),        // too wide
                new JpegRegion(0, 0, 1, h + 1),        // too tall
                new JpegRegion(w, 0, 1, 1),            // origin outside
                new JpegRegion(0, h, 1, 1),
                new JpegRegion(1, 0, w, 1),            // starts inside, ends outside
                new JpegRegion(0, 1, 1, h),
                new JpegRegion(int.MaxValue, 0, 1, 1), // would overflow a naive x + width
                new JpegRegion(1, 1, int.MaxValue, 1),
                new JpegRegion(0, 0, int.MaxValue, int.MaxValue),
            };

            foreach (var region in bad)
            {
                var dest = new byte[w * h * 4 + 64];
                Array.Fill(dest, (byte)0xAB);
                Should.Throw<ArgumentException>(() => JpegDecoder.DecodeRegionTo(jpeg, region, dest, scale), $"{region} @{scale}");
                dest.ShouldAllBe(b => b == 0xAB, $"{region} @{scale}: nothing is written for a region that is refused");
            }

            // The largest valid region needs exactly w*h*4 bytes; one byte short is refused before any row is decoded.
            var whole = new JpegRegion(0, 0, w, h);
            var exact = new byte[w * h * 4];
            JpegDecoder.DecodeRegionTo(jpeg, whole, exact, scale).ShouldBe((w, h));
            var shortBy1 = new byte[w * h * 4 - 1];
            Array.Fill(shortBy1, (byte)0xAB);
            Should.Throw<ArgumentException>(() => JpegDecoder.DecodeRegionTo(jpeg, whole, shortBy1, scale)).ParamName.ShouldBe("rgbaDestination");
            shortBy1.ShouldAllBe(b => b == 0xAB);

            // ... and a small region needs only its own size, whatever the image.
            JpegDecoder.DecodeRegionTo(jpeg, new JpegRegion(w - 1, h - 1, 1, 1), new byte[4], scale).ShouldBe((1, 1));
            Should.Throw<ArgumentException>(() => JpegDecoder.DecodeRegionTo(jpeg, new JpegRegion(w - 1, h - 1, 1, 1), new byte[3], scale));
        }
    }

    [Fact]
    public void RegionBounds_AreAtTheDecodeScale()
    {
        // The same pixels are in range at Full and out of range at Eighth: the region is in output pixels at the scale.
        var jpeg = Build(67, 45, "2x2,1x1,1x1", Kind.Baseline);
        var region = new JpegRegion(60, 40, 7, 5);
        JpegDecoder.DecodeRegionTo(jpeg, region, new byte[7 * 5 * 4]).ShouldBe((7, 5));
        Should.Throw<ArgumentException>(() => JpegDecoder.DecodeRegionTo(jpeg, region, new byte[7 * 5 * 4], JpegScale.Eighth));
        Should.Throw<ArgumentOutOfRangeException>(() => JpegDecoder.DecodeRegionTo(jpeg, region, new byte[7 * 5 * 4], (JpegScale)3));
    }

    // ============================================================ 4. the cost

    private const int CostW = 1600, CostH = 1200; // 4:2:0: 100 x 75 MCUs of 16x16

    private static (byte[] Jpeg, int McuRows) CostImage(Kind kind)
    {
        var jpeg = Build(CostW, CostH, "2x2,1x1,1x1", kind);
        return (jpeg, (CostH + 15) / 16);
    }

    [Fact]
    public void WithRowRestarts_ARegionAThirdHighEntropyDecodesAboutAThirdOfTheMcuRows()
    {
        var (jpeg, mcuRows) = CostImage(Kind.RestartRow);
        mcuRows.ShouldBe(75);
        var full = JpegDecoder.DecodeWithStats(jpeg, JpegScale.Full, out _);
        full.EntropyUnits.ShouldBe(mcuRows);
        full.TransformedUnits.ShouldBe(mcuRows);
        var reference = JpegDecoder.Decode(jpeg);

        // The middle third by rows, an MCU-aligned third (25 rows of MCUs = 400 pixels) and an unaligned one.
        foreach (var (y, height) in new[] { (400, 400), (413, 400), (800, 400) })
        {
            var region = new JpegRegion(0, y, CostW, height);
            var dest = new byte[CostW * height * 4];
            var stats = JpegDecoder.DecodeRegionToWithStats(jpeg, region, dest);

            // The MCU rows the region covers, and one more at either end for the rows the chroma upsampler blends in
            // from the unit above or below an interior edge: 25 + at most 2, plus one when it is not MCU-aligned.
            var covered = (y + height - 1) / 16 - y / 16 + 1;
            stats.EntropyUnits.ShouldBeGreaterThanOrEqualTo(covered, $"y={y}");
            stats.EntropyUnits.ShouldBeLessThanOrEqualTo(covered + 2, $"y={y}: only the region's MCU rows, plus the margin");
            stats.EntropyUnits.ShouldBeLessThan(mcuRows / 3 + 4, $"y={y}: about a third of {mcuRows}");
            stats.TransformedUnits.ShouldBe(stats.EntropyUnits, $"y={y}: with restarts nothing is decoded that is not transformed");
            ShouldBeCropOf(reference, region, dest, $"cost y={y}");
        }
    }

    [Fact]
    public void WithRowRestarts_StripsAreSizedToTheRegionNotTheImage()
    {
        var (jpeg, _) = CostImage(Kind.RestartRow);
        var full = JpegDecoder.DecodeWithStats(jpeg, JpegScale.Full, out _);

        // A quarter as wide: the strips (a unit of rows, by the block columns the region holds) are about a quarter.
        var region = new JpegRegion(600, 400, CostW / 4, CostH / 3);
        var stats = JpegDecoder.DecodeRegionToWithStats(jpeg, region, new byte[region.Width * region.Height * 4]);
        stats.WholePlanes.ShouldBeFalse();
        stats.PeakPlaneBytes.ShouldBeLessThan(full.PeakPlaneBytes / 2, $"region {stats.PeakPlaneBytes} B vs full width {full.PeakPlaneBytes} B");

        // The bound itself, from the geometry: for each component a unit of rows plus the two retained, by the
        // block columns holding the region and one block of horizontal margin each side; at most twice that rented.
        var ceiling = 2L * ((16 + 2) * (region.Width + 16 + 16) + 2 * (8 + 2) * (region.Width / 2 + 16 + 16));
        stats.PeakPlaneBytes.ShouldBeLessThanOrEqualTo(ceiling);

        // And a region as wide as the image holds what the full decode does.
        var band = new JpegRegion(0, 400, CostW, 64);
        JpegDecoder.DecodeRegionToWithStats(jpeg, band, new byte[CostW * 64 * 4]).PeakPlaneBytes.ShouldBe(full.PeakPlaneBytes);
    }

    [Fact]
    public void WithoutRestarts_EntropyDecodingRunsToTheRegionsLastRow_ButTransformsOnlyTheRegion()
    {
        var (jpeg, mcuRows) = CostImage(Kind.Baseline);
        var region = new JpegRegion(0, 400, CostW, 400); // rows 400..799: MCU rows 25..49
        var stats = JpegDecoder.DecodeRegionToWithStats(jpeg, region, new byte[CostW * 400 * 4]);

        stats.EntropyUnits.ShouldBeGreaterThanOrEqualTo(50, "from the top of the scan through the region's last MCU row");
        stats.EntropyUnits.ShouldBeLessThanOrEqualTo(51);
        stats.EntropyUnits.ShouldBeLessThan(mcuRows, "and then it stops");
        stats.TransformedUnits.ShouldBeLessThanOrEqualTo(27, "the region's 25 MCU rows and the margin: nothing above it is IDCT'd or assembled");
        stats.TransformedUnits.ShouldBeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public void Progressive_DecodesEveryScan_ButTransformsOnlyTheRegion()
    {
        var (jpeg, mcuRows) = CostImage(Kind.Progressive);
        var region = new JpegRegion(200, 400, 400, 400);
        var dest = new byte[400 * 400 * 4];
        var stats = JpegDecoder.DecodeRegionToWithStats(jpeg, region, dest);

        stats.WholePlanes.ShouldBeFalse();
        stats.EntropyUnits.ShouldBe(mcuRows, "every scan is needed");
        stats.TransformedUnits.ShouldBeLessThanOrEqualTo(27);
        stats.TransformedUnits.ShouldBeGreaterThanOrEqualTo(25);
        ShouldBeCropOf(JpegDecoder.Decode(jpeg), region, dest, "progressive cost");
    }

    [Fact]
    public void ARegionThatDoesNotTouchTheTop_DoesNotReadTheStreamsFirstIntervals()
    {
        // Direct proof that the intervals above the region are not entropy-decoded: wreck them. The first 19 MCU
        // rows of entropy data are zeroed (markers and stuffing left alone, so the intervals still count), which
        // changes what they decode to. A region below them still decodes to what the undamaged stream gives, because
        // it never reads them; one inside them does not.
        var (jpeg, _) = CostImage(Kind.RestartRow);
        var reference = JpegDecoder.Decode(jpeg);
        var sos = JpegStripDecodeTests.FirstSos(jpeg);
        var rsts = JpegStripDecodeTests.RstOffsets(jpeg);

        var wrecked = (byte[])jpeg.Clone();
        var begin = sos + 2 + ((jpeg[sos + 2] << 8) | jpeg[sos + 3]); // start of the entropy data
        for (var i = begin; i < rsts[19]; i++)
        {
            if (wrecked[i] == 0xFF || (i > 0 && wrecked[i - 1] == 0xFF))
                continue;
            wrecked[i] = 0x00;
        }

        var region = new JpegRegion(100, 600, 300, 300); // MCU rows 37..55, well below row 20
        var dest = new byte[300 * 300 * 4];
        JpegDecoder.DecodeRegionTo(wrecked, region, dest);
        ShouldBeCropOf(reference, region, dest, "wrecked top");

        // A region in the wrecked part reads its (garbage) intervals and cannot match; that it differs shows the
        // wrecking took effect, so the equal result above is not an accident of the damage being invisible.
        var top = new JpegRegion(0, 0, 100, 100);
        var topDest = new byte[100 * 100 * 4];
        try
        {
            JpegDecoder.DecodeRegionTo(wrecked, top, topDest);
            var expectedTop = new byte[100 * 100 * 4];
            for (var row = 0; row < 100; row++)
                reference.Pixels.AsSpan(row * CostW * 4, 100 * 4).CopyTo(expectedTop.AsSpan(row * 400, 400));
            topDest.ShouldNotBe(expectedTop, "the damage must be visible where it is read");
        }
        catch (InvalidDataException)
        {
            // Also fine: the damage was read, and refused.
        }
    }

    [Fact]
    public void ARegionHasNoLargeAllocation()
    {
        // The decode itself allocates the engine's small tables and rents its strips from the pool. Nothing is the
        // size of the image or of the region: the destination is the caller's.
        var (jpeg, _) = CostImage(Kind.RestartRow);
        var region = new JpegRegion(0, 400, CostW, 400);
        var dest = new byte[CostW * 400 * 4];
        JpegDecoder.DecodeRegionTo(jpeg, region, dest); // warm the pool and the JIT

        var before = GC.GetAllocatedBytesForCurrentThread();
        JpegDecoder.DecodeRegionTo(jpeg, region, dest);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThan(dest.Length / 16, $"allocated {allocated} B decoding a {dest.Length} B region");
    }
}
