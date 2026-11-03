using SharpAstro.Jpeg;
using Shouldly;
using Kind = SharpAstro.Codecs.Tests.JpegRegionDecodeTests.Kind;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <c>JpegDecoder.DecodeFittedTo</c> decodes at a scale (of a region, if asked) and box-filters to a target size inside
/// the decoder's row loop. Its contract is that the result is <b>byte for byte</b> what <c>Decode</c> followed by the
/// Drawboard viewer's <c>RgbaBoxDownsample.Shrink</c> gives, so the expected side here is always <c>Decode</c> (or its
/// crop, for a region) put through <see cref="ReferenceShrink"/>, a transcription of Shrink. Nothing is "within 1".
///
/// <list type="number">
/// <item><b>The sweep</b>: every sampling layout at every scale, over baseline without restarts, with restart intervals
/// of a row, of three MCUs and of one, progressive, and the whole-plane engine, at targets from 1×1 through every
/// awkward ratio to the identity; and the same with regions. It is the whole of the decoder.</item>
/// <item><b>The sink on its own</b>: <see cref="JpegFittedSink"/> exhaustively over small geometries with no JPEG in
/// sight, so a wrong box edge cannot hide behind a decoder that happens to produce smooth images. Also the restart: a
/// sink that is handed the rows again from the first must not keep what the first attempt left in its sums.</item>
/// <item><b>The scale of it</b>: the issue's 5100×6600 → 3165×4096 in miniature, the overflow the viewer's own
/// <c>int</c> sums have and these do not, the arguments, and what a decode allocates and holds.</item>
/// </list>
/// </summary>
public sealed class JpegFittedDecodeTests
{
    private static readonly JpegScale[] Scales = [JpegScale.Full, JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth];

    private const byte Sentinel = 0xAB;

    // ------------------------------------------------------------ the reference

    /// <summary>
    /// <b>Transcribed, not referenced</b>: <c>RgbaBoxDownsample.Shrink</c> from the Drawboard PDF viewer
    /// (repository <c>pdf-viewer</c>, <c>src/PdfViewer.Abstractions/RgbaBoxDownsample.cs</c>, namespace
    /// <c>PdfViewer.Pdf</c>), so that the filter the decoder claims to match is pinned here in plain
    /// <c>int</c> arithmetic, line for line. Two things differ from the original and neither changes a result: the
    /// target size is a parameter (the viewer derives it from a maximum display dimension; <see cref="ViewerSize"/>
    /// is that derivation), and a source row is sliced out of the packed array instead of coming from an
    /// <c>IRgbaRows</c>. The <c>int</c> accumulators are kept on purpose: for a box of more than about 8.4 million
    /// pixels they wrap, which is why the overflow test does not call this.
    /// </summary>
    internal static byte[] ReferenceShrink(ReadOnlySpan<byte> pixels, int w, int h, int newW, int newH)
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

    /// <summary>The viewer's own target sizing (the first lines of <c>Shrink</c>): the larger side becomes <paramref name="maxDisplayDim"/>.</summary>
    private static (int W, int H) ViewerSize(int w, int h, int maxDisplayDim)
    {
        var scale = (float)maxDisplayDim / Math.Max(w, h);
        return (Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
    }

    private static byte[] Crop(JpegImage image, JpegRegion r)
    {
        var crop = new byte[r.Width * r.Height * 4];
        for (var row = 0; row < r.Height; row++)
            image.Pixels.AsSpan(((r.Y + row) * image.Width + r.X) * 4, r.Width * 4).CopyTo(crop.AsSpan(row * r.Width * 4, r.Width * 4));
        return crop;
    }

    // ------------------------------------------------------------ the comparison

    /// <summary>
    /// Fits <paramref name="jpeg"/> (or <paramref name="region"/> of it) to <paramref name="tw"/>×<paramref name="th"/> into a
    /// sentinel-padded buffer and requires every byte of the target to equal the reference filter of the full decode,
    /// and nothing past it to have been touched.
    /// </summary>
    private static void AssertFitted(byte[] jpeg, JpegScale scale, JpegImage full, JpegRegion? region, int tw, int th, string label, bool wholePlanes = false)
    {
        var (sw, sh, source) = region is { } r ? (r.Width, r.Height, Crop(full, r)) : (full.Width, full.Height, full.Pixels);
        var expected = ReferenceShrink(source, sw, sh, tw, th);

        const int pad = 32;
        var length = tw * th * 4;
        var dest = new byte[length + pad];
        Array.Fill(dest, Sentinel);

        if (wholePlanes)
            JpegDecoder.DecodeFittedToWithStats(jpeg, tw, th, dest, scale, region, forceWholePlanes: true);
        else
            JpegDecoder.DecodeFittedTo(jpeg, tw, th, dest, scale, region).ShouldBe((tw, th), label);

        dest.AsSpan(length).ToArray().ShouldAllBe(b => b == Sentinel, $"{label}: wrote past the target");

        var got = dest.AsSpan(0, length);
        var at = got.CommonPrefixLength(expected);
        if (at != length)
        {
            Assert.Fail($"{label} {sw}×{sh} → {tw}×{th}{(region is { } rr ? $" of {rr}" : "")}: differs from decode + reference filter at target pixel " +
                        $"({at / 4 % tw}, {at / 4 / tw}) channel {at % 4}: got {got[at]}, expected {expected[at]}");
        }
    }

    /// <summary>The target sizes worth trying for a source: the edges of the range, the nearly-1:1 ones where only a single box is wider than a pixel, and awkward ratios.</summary>
    private static List<(int W, int H)> Targets(int sw, int sh)
    {
        var set = new HashSet<(int, int)>();
        void Add(int w, int h) => set.Add((Math.Clamp(w, 1, sw), Math.Clamp(h, 1, sh)));

        Add(sw, sh);                              // 1:1
        Add(1, 1);                                // the whole image in one box
        Add(1, sh);                               // one column, every row
        Add(sw, 1);
        Add(sw - 1, sh - 1);                      // exactly one wider box on each axis
        Add(sw - 1, sh);
        Add(sw, sh - 1);
        Add((sw * 3 + 4) / 5, (sh * 3 + 4) / 5);  // 0.6: boxes alternate between two sizes
        Add(sw / 2, sh / 2);
        Add(sw / 3, sh * 2 / 3);                  // different ratios on the two axes
        Add(sw * 2 / 3, sh / 3);
        foreach (var max in new[] { 3, 5, 17, 41 })
        {
            var (w, h) = ViewerSize(sw, sh, max);
            Add(w, h);
        }

        return [.. set.OrderBy(t => t.Item1).ThenBy(t => t.Item2)];
    }

    /// <summary>A few targets for a region: the sweep over regions is large, so each gets the four that matter most.</summary>
    private static IEnumerable<(int W, int H)> RegionTargets(int rw, int rh)
    {
        var set = new HashSet<(int, int)>
        {
            (rw, rh),
            (1, 1),
            (Math.Max(1, (rw * 3 + 4) / 5), Math.Max(1, (rh * 3 + 4) / 5)),
            (Math.Max(1, rw / 3), Math.Max(1, rh * 2 / 3)),
        };
        return set;
    }

    private static readonly (int W, int H)[] Sizes = [(37, 23), (67, 70), (51, 66), (130, 7), (7, 130), (1, 1)];

    public static IEnumerable<object[]> Matrix() =>
        from layout in JpegRegionDecodeTests.Layouts
        from kind in Enum.GetValues<Kind>()
        select new object[] { layout ?? "grey", kind };

    // ============================================================ 1. the sweep

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Fitted_EqualsDecodeThenReferenceFilter(string layoutName, object kindObject)
    {
        var kind = (Kind)kindObject;
        var sampling = JpegRegionDecodeTests.LayoutOf(layoutName);
        foreach (var (w, h) in Sizes)
        {
            var jpeg = JpegRegionDecodeTests.Build(w, h, sampling, kind);
            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                foreach (var (tw, th) in Targets(full.Width, full.Height))
                    AssertFitted(jpeg, scale, full, null, tw, th, $"{layoutName} {kind} {w}x{h} @{scale}", wholePlanes: kind == Kind.WholePlanes);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void FittedRegion_EqualsCropOfDecodeThenReferenceFilter(string layoutName, object kindObject)
    {
        var kind = (Kind)kindObject;
        var sampling = JpegRegionDecodeTests.LayoutOf(layoutName);
        foreach (var (w, h) in new[] { (37, 23), (67, 70), (130, 7) })
        {
            var jpeg = JpegRegionDecodeTests.Build(w, h, sampling, kind);
            foreach (var scale in new[] { JpegScale.Full, JpegScale.Half, JpegScale.Eighth })
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                foreach (var region in JpegRegionDecodeTests.Regions(full.Width, full.Height, budget: 12))
                {
                    foreach (var (tw, th) in RegionTargets(region.Width, region.Height))
                        AssertFitted(jpeg, scale, full, region, tw, th, $"{layoutName} {kind} {w}x{h} @{scale}", wholePlanes: kind == Kind.WholePlanes);
                }
            }
        }
    }

    [Theory]
    [InlineData(false, 3)] // our own encoder: 4:4:4 and 4:2:0 baseline, grey; no restart markers
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    [InlineData(true, 1)]
    public void Fitted_EqualsDecodeThenReferenceFilter_OnOurOwnEncoder(bool chroma420, int channels)
    {
        foreach (var (w, h) in new[] { (37, 23), (67, 70), (33, 33) })
        {
            var pixels = new byte[w * h * channels];
            var rgb = JpegStripDecodeTests.Pattern(w, h);
            for (var i = 0; i < w * h; i++)
                for (var c = 0; c < channels; c++)
                    pixels[i * channels + c] = rgb[i * 3 + c];

            var jpeg = JpegEncoder.Encode(pixels, w, h, channels,
                new JpegEncodeOptions { Quality = 85, Subsampling = chroma420 ? JpegSubsampling.Chroma420 : JpegSubsampling.Chroma444 });

            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                foreach (var (tw, th) in Targets(full.Width, full.Height))
                    AssertFitted(jpeg, scale, full, null, tw, th, $"encoder 420={chroma420} ch={channels} {w}x{h} @{scale}");
            }
        }
    }

    [Fact]
    public void ATargetTheSizeOfTheSource_IsTheDecodedImage()
    {
        // Independent of the reference filter: with a box of one pixel the filter must be the identity.
        foreach (var kind in Enum.GetValues<Kind>())
        {
            var jpeg = JpegRegionDecodeTests.Build(67, 45, "2x2,1x1,1x1", kind);
            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                var dest = new byte[full.Pixels.Length];
                JpegDecoder.DecodeFittedTo(jpeg, full.Width, full.Height, dest, scale).ShouldBe((full.Width, full.Height));
                dest.ShouldBe(full.Pixels, $"{kind} @{scale}");

                var region = new JpegRegion(full.Width / 3, full.Height / 4, full.Width - full.Width / 3, full.Height - full.Height / 4);
                var part = new byte[region.Width * region.Height * 4];
                JpegDecoder.DecodeFittedTo(jpeg, region.Width, region.Height, part, scale, region);
                part.ShouldBe(Crop(full, region), $"{kind} @{scale} {region}");
            }
        }
    }

    [Fact]
    public void CmykAndRgbMarked_FitToo()
    {
        // The colour paths other than plain YCbCr and grey: Adobe CMYK and 'R','G','B' component ids.
        var cmyk = JpegDecoderOracleTests.EncodeJpeg(JpegStripDecodeTests.Pattern(37, 23), 37, 23, 88, null, false, false, cmyk: true);
        var rgbIds = JpegRegionDecodeTests.Build(37, 23, "1x1,1x1,1x1", Kind.Baseline);
        var pos = 2;
        ReadOnlySpan<byte> ids = "RGB"u8;
        while (rgbIds[pos + 1] != 0xDA)
        {
            if (rgbIds[pos + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                for (var i = 0; i < 3; i++)
                    rgbIds[pos + 10 + 3 * i] = ids[i];
            }

            pos += 2 + ((rgbIds[pos + 2] << 8) | rgbIds[pos + 3]);
        }

        for (var i = 0; i < 3; i++)
            rgbIds[pos + 5 + 2 * i] = ids[i]; // the scan header names the components too

        foreach (var (jpeg, name) in new[] { (cmyk, "cmyk"), (rgbIds, "rgb ids") })
        {
            foreach (var scale in Scales)
            {
                var full = JpegDecoder.Decode(jpeg, scale);
                foreach (var (tw, th) in Targets(full.Width, full.Height))
                    AssertFitted(jpeg, scale, full, null, tw, th, $"{name} @{scale}");
            }
        }
    }

    // ============================================================ 2. the issue's ratios, in miniature

    [Fact]
    public void ViewerSizing_OfTheIssuesScan_IsTheIssuesTarget()
    {
        // The arithmetic the viewer uses to choose the target, so that the miniature below really is "5100x6600 → 3165x4096".
        ViewerSize(5100, 6600, 4096).ShouldBe((3165, 4096));
    }

    [Theory]
    [InlineData("1x1,1x1,1x1", nameof(Kind.RestartRow))] // the issue's scans: 4:4:4, DRI = one MCU row
    [InlineData("2x2,1x1,1x1", nameof(Kind.RestartRow))]
    [InlineData("1x1,1x1,1x1", nameof(Kind.Baseline))]
    [InlineData("2x2,1x1,1x1", nameof(Kind.Progressive))]
    public void TheIssuesRatio_InMiniature(string sampling, string kindName)
    {
        var kind = Enum.Parse<Kind>(kindName);
        // 510×660 has the 17:22 shape of 5100×6600; the viewer's sizing gives a non-integer ratio on both axes, as
        // 5100×6600 → 3165×4096 does. The same ratios go through every scale, and through a region a third of the image high.
        var jpeg = JpegRegionDecodeTests.Build(510, 660, sampling, kind);
        foreach (var scale in new[] { JpegScale.Full, JpegScale.Half })
        {
            var full = JpegDecoder.Decode(jpeg, scale);
            var third = new JpegRegion(0, full.Height / 3, full.Width, full.Height / 3);
            var inset = new JpegRegion(full.Width / 5, full.Height / 4 + 1, full.Width / 2 + 1, full.Height / 3 - 1);
            var counted = 0;
            foreach (var max in new[] { 410, 256, 100, 31 })
            {
                // The viewer shrinks only an image larger than its maximum, and never enlarges; a source already
                // inside the maximum is not a case it asks for.
                foreach (var (name, region, sw, sh) in new (string, JpegRegion?, int, int)[]
                         {
                             ("whole", null, full.Width, full.Height),
                             ("third", third, third.Width, third.Height),
                             ("inset", inset, inset.Width, inset.Height),
                         })
                {
                    if (Math.Max(sw, sh) <= max)
                        continue;

                    var (tw, th) = ViewerSize(sw, sh, max);
                    AssertFitted(jpeg, scale, full, region, tw, th, $"{sampling} {kind} @{scale} max {max} {name}");
                    counted++;
                }
            }

            counted.ShouldBeGreaterThanOrEqualTo(6, $"@{scale}: the sweep must reach non-integer ratios on both axes");
        }
    }

    [Fact]
    public void TheMiniature_HasTheIssuesKindOfRatio()
    {
        // What "in miniature" has to preserve: neither axis shrinks by a whole number, so the boxes are of two different
        // sizes along both, as for the issue's 5100×6600 → 3165×4096.
        foreach (var (sw, sh, max) in new[] { (5100, 6600, 4096), (510, 660, 410) })
        {
            var (tw, th) = ViewerSize(sw, sh, max);
            (sw % tw).ShouldNotBe(0, $"{sw}×{sh} → {tw}×{th}");
            (sh % th).ShouldNotBe(0, $"{sw}×{sh} → {tw}×{th}");
        }
    }

    // ============================================================ 3. the sink on its own

    /// <summary>Hands <paramref name="source"/> (<paramref name="sw"/>×<paramref name="sh"/>) to a fresh sink the way the engine does, with an optional abandoned first attempt.</summary>
    private static byte[] RunSink(byte[] source, int sw, int sh, int tw, int th, int firstRow = 0, int abandonedRows = 0, bool wide = false)
    {
        var dest = new byte[tw * th * 4];
        // A region's rows are numbered as the image's, and the engine's Begin is told the image's size, not the region's.
        JpegRegion? region = firstRow == 0 ? null : new JpegRegion(2, firstRow, sw, sh);
        var sink = new JpegFittedSink(dest, tw, th, region, forceWideSums: wide);
        try
        {
            if (region is null)
                sink.Begin(sw, sh);
            else
                sink.Begin(sw + 5, firstRow + sh + 3);

            // An attempt that delivers some rows of other pixels and is then abandoned: the engine's restart.
            for (var row = 0; row < abandonedRows; row++)
            {
                var span = sink.RowBuffer(firstRow + row);
                span.Length.ShouldBe(sw * 4);
                span.Fill((byte)(255 - row));
                sink.RowDone(firstRow + row);
            }

            for (var row = 0; row < sh; row++)
            {
                var span = sink.RowBuffer(firstRow + row);
                span.Length.ShouldBe(sw * 4);
                source.AsSpan(row * sw * 4, sw * 4).CopyTo(span);
                sink.RowDone(firstRow + row);
            }
        }
        finally
        {
            sink.Release();
        }

        return dest;
    }

    private static byte[] Noise(int w, int h, uint seed)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            // Extremes and mid-values alike: sums that land exactly on a rounding half are the ones that expose a wrong n/2.
            pixels[i] = (seed >> 29) switch { 0 => 0, 1 => 255, 2 => 1, 3 => 254, _ => (byte)(seed >> 16) };
        }

        return pixels;
    }

    [Fact]
    public void TheSink_MatchesTheReferenceForEveryBoxGeometry()
    {
        // No JPEG: every source and target size up to 14×14, so every way the boxes can fall, including each place a
        // box is wider than its neighbours, is compared with the transcription. 14^4/4 ≈ 10,000 geometries.
        var count = 0;
        for (var sw = 1; sw <= 14; sw++)
        {
            for (var sh = 1; sh <= 14; sh++)
            {
                var source = Noise(sw, sh, (uint)(sw * 31 + sh));
                for (var tw = 1; tw <= sw; tw++)
                {
                    for (var th = 1; th <= sh; th++)
                    {
                        RunSink(source, sw, sh, tw, th).ShouldBe(ReferenceShrink(source, sw, sh, tw, th), $"{sw}×{sh} → {tw}×{th}");
                        count++;
                    }
                }
            }
        }

        count.ShouldBe(Enumerable.Range(1, 14).Sum() * Enumerable.Range(1, 14).Sum());
    }

    [Fact]
    public void TheSink_MatchesTheReferenceForWideAndTallShapes()
    {
        foreach (var (sw, sh, tw, th) in new[] { (300, 5, 7, 3), (5, 300, 3, 7), (257, 129, 31, 17), (1000, 1, 999, 1), (1, 1000, 1, 999), (640, 480, 213, 160) })
        {
            var source = Noise(sw, sh, 77);
            RunSink(source, sw, sh, tw, th).ShouldBe(ReferenceShrink(source, sw, sh, tw, th), $"{sw}×{sh} → {tw}×{th}");
        }
    }

    [Fact]
    public void TheWideAndTheNarrowPath_AgreeWithTheReferenceAndEachOther()
    {
        // The sink has two paths: 32-bit column sums with a multiply-high divide (the one every ordinary fit takes) and
        // the 64-bit sums with a real divide (a box over ~16.7 million pixels, and the fallback). A given machine only
        // ever takes one for a given size, so the other would rot unseen: this drives both over the same geometries,
        // the identity (a divisor of 1, which the reciprocal cannot represent) and the one-pixel box included.
        var count = 0;
        for (var sw = 1; sw <= 11; sw++)
        {
            for (var sh = 1; sh <= 11; sh++)
            {
                var source = Noise(sw, sh, (uint)(sw * 17 + sh * 3));
                for (var tw = 1; tw <= sw; tw++)
                {
                    for (var th = 1; th <= sh; th++)
                    {
                        var expected = ReferenceShrink(source, sw, sh, tw, th);
                        RunSink(source, sw, sh, tw, th, wide: false).ShouldBe(expected, $"narrow {sw}×{sh} → {tw}×{th}");
                        RunSink(source, sw, sh, tw, th, wide: true).ShouldBe(expected, $"wide {sw}×{sh} → {tw}×{th}");
                        count++;
                    }
                }
            }
        }

        count.ShouldBe(Enumerable.Range(1, 11).Sum() * Enumerable.Range(1, 11).Sum());

        // Widths that are not a multiple of the 16 bytes (4 pixels) one vector add takes, and not a multiple of its tail.
        foreach (var (sw, sh, tw, th) in new[] { (4, 3, 2, 2), (5, 4, 3, 3), (7, 5, 2, 4), (33, 9, 10, 4), (35, 8, 35, 8), (61, 47, 13, 7) })
        {
            var source = Noise(sw, sh, 123);
            var expected = ReferenceShrink(source, sw, sh, tw, th);
            RunSink(source, sw, sh, tw, th, wide: false).ShouldBe(expected, $"narrow {sw}×{sh} → {tw}×{th}");
            RunSink(source, sw, sh, tw, th, wide: true).ShouldBe(expected, $"wide {sw}×{sh} → {tw}×{th}");
        }
    }

    [Fact]
    public void ABoxJustInsideTheNarrowPathsRange_IsTheMean()
    {
        // 4095×4095 = 16,769,025 pixels in one box, of 255 in every channel: the sums are 4.28e9, a hair under 2^32, which is
        // the largest the 32-bit sums are allowed to reach. A sum that wrapped, or a rounding half added in the wrong place,
        // would land on something other than 255. Rows are fed to the sink directly (a source this size is 67 MB).
        const int side = 4095;
        var dest = new byte[4];
        var sink = new JpegFittedSink(dest, 1, 1, null);
        try
        {
            sink.Begin(side, side);
            for (var row = 0; row < side; row++)
            {
                sink.RowBuffer(row).Fill(255);
                sink.RowDone(row);
            }
        }
        finally
        {
            sink.Release();
        }

        dest.ShouldBe(new byte[] { 255, 255, 255, 255 });

        // And over varied rows, against the wide path and against a sum made here in long.
        var pixel = new byte[4];
        foreach (var wide in new[] { false, true })
        {
            var result = new byte[4];
            long[] sums = new long[4];
            var s = new JpegFittedSink(result, 1, 1, null, forceWideSums: wide);
            try
            {
                s.Begin(side, side);
                for (var row = 0; row < side; row++)
                {
                    var span = s.RowBuffer(row);
                    for (var i = 0; i < span.Length; i++)
                        span[i] = (byte)(255 - ((row * 7 + i * 13) % 5));
                    for (var i = 0; i < span.Length; i++)
                        sums[i & 3] += span[i];
                    s.RowDone(row);
                }
            }
            finally
            {
                s.Release();
            }

            var n = (long)side * side;
            for (var c = 0; c < 4; c++)
                pixel[c] = (byte)((sums[c] + n / 2) / n);
            result.ShouldBe(pixel, $"wide: {wide}");
        }
    }

    [Fact]
    public void TheSink_BeginsOverWhenTheRowsComeAgainFromTheFirst()
    {
        // The engine's restart delivers every row again from the first, after some of them. Whatever the first attempt
        // left in the sums, including a target row part-accumulated, must not reach the result.
        foreach (var firstRow in new[] { 0, 3 }) // a region starts at its own first row, not at 0
        {
            foreach (var (sw, sh, tw, th) in new[] { (11, 13, 4, 5), (9, 9, 9, 3), (12, 7, 1, 1), (10, 10, 10, 10) })
            {
                var source = Noise(sw, sh, 5);
                var expected = ReferenceShrink(source, sw, sh, tw, th);
                for (var abandoned = 1; abandoned < sh; abandoned++)
                {
                    foreach (var wide in new[] { false, true })
                        RunSink(source, sw, sh, tw, th, firstRow, abandoned, wide).ShouldBe(expected, $"row {firstRow} {sw}×{sh} → {tw}×{th} after {abandoned} rows, wide: {wide}");
                }
            }
        }
    }

    [Fact]
    public void TheSink_WithARegion_NumbersItsRowsAsTheImages()
    {
        var source = Noise(20, 16, 9);
        var expected = ReferenceShrink(source, 20, 16, 7, 5);
        foreach (var firstRow in new[] { 1, 5, 40 })
            RunSink(source, 20, 16, 7, 5, firstRow).ShouldBe(expected, $"first row {firstRow}");
    }

    // ============================================================ 4. a restart, through the decoder

    [Fact]
    public void ASecondScan_RestartsTheDecode_AndTheFittedResultStillMatches()
    {
        // A scan after the streamed one overwrites its blocks in the full decode, which therefore decodes again with
        // whole planes and delivers the rows again. The sink sees both attempts and must end with the second's.
        // This pins the end to end result and that the region's first row (not 0) is what the second attempt starts
        // from. It cannot see a sink that forgets to start its sums over: here the first attempt delivers every row
        // before the restart, so the sums are empty anyway. TheSink_BeginsOverWhenTheRowsComeAgainFromTheFirst
        // abandons an attempt part-way and is the one that does (mutation-checked: it alone fails without the reset).
        var jpeg = JpegRegionDecodeTests.Build(61, 47, "2x2,1x1,1x1", Kind.RestartRow);
        var sos = JpegStripDecodeTests.FirstSos(jpeg);
        var scan = jpeg[sos..^2];
        var twice = JpegStripDecodeTests.Concat(jpeg[..sos], scan, scan, [0xFF, 0xD9]);

        var full = JpegDecoder.Decode(twice);
        foreach (var region in new JpegRegion?[] { null, new JpegRegion(0, 0, 61, 47), new JpegRegion(10, 30, 20, 17), new JpegRegion(0, 40, 61, 7) })
        {
            var (rw, rh) = region is { } r ? (r.Width, r.Height) : (61, 47);
            foreach (var (tw, th) in RegionTargets(rw, rh))
            {
                var dest = new byte[tw * th * 4];
                var stats = JpegDecoder.DecodeFittedToWithStats(twice, tw, th, dest, JpegScale.Full, region);
                stats.Restarted.ShouldBeTrue($"{region}: the second scan must restart the decode");
                var source = region is { } rg ? Crop(full, rg) : full.Pixels;
                dest.ShouldBe(ReferenceShrink(source, rw, rh, tw, th), $"{region} → {tw}×{th}");
            }
        }
    }

    // ============================================================ 5. beyond the viewer's filter

    [Fact]
    public void ABoxOfMoreThanEightMillionPixels_IsTheMean_NotAWrappedSum()
    {
        // The viewer's filter sums in int, so a box over 2^31/255 ≈ 8.4 million pixels wraps: a 3000×3000 image fitted to
        // 1×1 is 9 million. The sums here are long and give the mean, which is computed independently, here, in long.
        const int side = 3000;
        var pixels = new byte[side * side];
        Array.Fill(pixels, (byte)255);
        var jpeg = JpegEncoder.Encode(pixels, side, side, 1, new JpegEncodeOptions { Quality = 100 });
        var full = JpegDecoder.Decode(jpeg);

        long sum = 0;
        for (var i = 0; i < full.Pixels.Length; i += 4)
            sum += full.Pixels[i];
        sum.ShouldBeGreaterThan(int.MaxValue, "the test must reach the range where an int sum wraps");

        var n = (long)side * side;
        var expected = (byte)((sum + n / 2) / n);
        var dest = new byte[4];
        JpegDecoder.DecodeFittedTo(jpeg, 1, 1, dest);
        dest.ShouldBe(new byte[] { expected, expected, expected, 255 });

        // Fitted to 2×1 each box holds 4.5 million pixels: below the wrap, so the reference filter applies and must agree.
        var two = new byte[8];
        JpegDecoder.DecodeFittedTo(jpeg, 2, 1, two);
        two.ShouldBe(ReferenceShrink(full.Pixels, side, side, 2, 1));
    }

    // ============================================================ 6. the arguments

    [Fact]
    public void BadTargetsRegionsAndDestinations_ThrowArgumentException_WithoutWritingAnything()
    {
        var jpeg = JpegRegionDecodeTests.Build(67, 45, "2x2,1x1,1x1", Kind.RestartRow);
        foreach (var scale in Scales)
        {
            var (w, h) = JpegDecoder.ReadInfo(jpeg).ScaledSize(scale);

            void Refused(int tw, int th, int destBytes, JpegRegion? region, string expectedParam)
            {
                var dest = new byte[destBytes];
                Array.Fill(dest, Sentinel);
                var ex = Should.Throw<ArgumentException>(() => JpegDecoder.DecodeFittedTo(jpeg, tw, th, dest, scale, region), $"{tw}×{th} {region} @{scale}");
                ex.ParamName.ShouldBe(expectedParam, $"{tw}×{th} {region} @{scale}");
                dest.ShouldAllBe(b => b == Sentinel, $"{tw}×{th} {region} @{scale}: nothing is written for a call that is refused");
            }

            var big = w * h * 4 + 64;
            Refused(0, 1, big, null, "targetWidth");
            Refused(1, 0, big, null, "targetHeight");
            Refused(-1, 1, big, null, "targetWidth");
            Refused(1, -1, big, null, "targetHeight");
            Refused(0, 0, big, null, "targetWidth");
            Refused(int.MinValue, 1, big, null, "targetWidth");
            Refused(w + 1, 1, big, null, "targetWidth");   // larger than the source, by one
            Refused(1, h + 1, big, null, "targetHeight");
            Refused(int.MaxValue, 1, big, null, "targetWidth");
            Refused(1, int.MaxValue, big, null, "targetHeight");

            // Against the region, not the image: a target that fits the image does not fit a region inside it.
            if (w > 1 && h > 1)
            {
                var small = new JpegRegion(0, 0, w - 1, h - 1);
                Refused(w, 1, big, small, "targetWidth");
                Refused(1, h, big, small, "targetHeight");
            }

            // A region that is no good is refused as it is for DecodeRegionTo.
            Refused(1, 1, big, new JpegRegion(0, 0, w + 1, 1), "region");
            Refused(1, 1, big, new JpegRegion(0, 0, 0, 1), "region");

            // The destination: exactly the target's size is enough, one byte less is refused before any row is decoded.
            var (tw, th) = (Math.Max(1, w / 2), Math.Max(1, h / 2));
            Refused(tw, th, tw * th * 4 - 1, null, "rgbaDestination");
            Refused(1, 1, 3, null, "rgbaDestination");
            Refused(tw, th, 0, null, "rgbaDestination");
            JpegDecoder.DecodeFittedTo(jpeg, tw, th, new byte[tw * th * 4], scale).ShouldBe((tw, th));

            // ... whatever the size of the source: a 1×1 target needs four bytes of any image.
            JpegDecoder.DecodeFittedTo(jpeg, 1, 1, new byte[4], scale).ShouldBe((1, 1));
        }

        Should.Throw<ArgumentOutOfRangeException>(() => JpegDecoder.DecodeFittedTo(jpeg, 1, 1, new byte[4], (JpegScale)3));
    }

    [Fact]
    public void ATargetThatIsRefused_LeavesNoPooledBufferUnreturned()
    {
        // The refusal happens in Begin, after the sink has rented nothing; and one that is raised from the engine
        // (a region) before Begin has no sink buffers at all. Neither may throw from the cleanup or leave the sink dirty.
        var jpeg = JpegRegionDecodeTests.Build(67, 45, "2x2,1x1,1x1", Kind.Baseline);
        for (var i = 0; i < 20; i++)
        {
            Should.Throw<ArgumentException>(() => JpegDecoder.DecodeFittedTo(jpeg, 1000, 1000, new byte[4_000_000]));
            Should.Throw<ArgumentException>(() => JpegDecoder.DecodeFittedTo(jpeg, 1, 1, new byte[4], JpegScale.Full, new JpegRegion(0, 0, 1000, 1000)));
        }

        var dest = new byte[4];
        JpegDecoder.DecodeFittedTo(jpeg, 1, 1, dest).ShouldBe((1, 1));
    }

    // ============================================================ 7. what a decode costs

    private static byte[] Large(Kind kind, int height = 2600) => JpegRegionDecodeTests.Build(2000, height, "1x1,1x1,1x1", kind);

    [Fact]
    public void AFittedDecode_AllocatesNextToNothing_WhateverTheSourceSize()
    {
        // The issue's image shape, smaller: 4:4:4 with a restart marker after every MCU row. One plane of it is 5.2 MB
        // and the RGBA 20.8 MB, either of which is what a decode that builds the image allocates. A fitted decode
        // into a caller's destination rents its strips and one source row from the pool and allocates only the
        // engine's small bookkeeping, so after one warm-up it must stay far below even a single plane.
        var jpeg = Large(Kind.RestartRow);
        var dest = new byte[1000 * 1300 * 4];
        JpegDecoder.DecodeFittedTo(jpeg, 1000, 1300, dest); // warms the pool and the JIT

        var before = GC.GetAllocatedBytesForCurrentThread();
        JpegDecoder.DecodeFittedTo(jpeg, 1000, 1300, dest).ShouldBe((1000, 1300));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        const int plane = 2000 * 2600;
        allocated.ShouldBeLessThan(plane / 20, $"allocated {allocated} B fitting a {plane} B-per-plane image");

        // And the same through a region, and to a destination far smaller than the source.
        var region = new JpegRegion(0, 800, 2000, 900);
        var small = new byte[500 * 450 * 4];
        JpegDecoder.DecodeFittedTo(jpeg, 500, 450, small, JpegScale.Full, region);
        before = GC.GetAllocatedBytesForCurrentThread();
        JpegDecoder.DecodeFittedTo(jpeg, 500, 450, small, JpegScale.Full, region);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBeLessThan(plane / 20, $"allocated {allocated} B fitting a region");
    }

    [Fact]
    public void WhatAFittedDecodeAllocates_DoesNotGrowWithTheImagesHeight()
    {
        // The strip is as wide as the image and a few MCU rows high, so doubling the height changes nothing that is held
        // or allocated: any height-proportional intermediate would add megabytes here.
        var shorter = Large(Kind.RestartRow, 1300);
        var tall = Large(Kind.RestartRow, 2600);
        var dest = new byte[1000 * 650 * 4];

        long Measure(byte[] jpeg)
        {
            JpegDecoder.DecodeFittedTo(jpeg, 1000, 650, dest);
            var before = GC.GetAllocatedBytesForCurrentThread();
            JpegDecoder.DecodeFittedTo(jpeg, 1000, 650, dest);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var a = Measure(shorter);
        var b = Measure(tall);
        b.ShouldBeLessThan(a + 16 * 1024, $"1300 rows allocated {a} B, 2600 rows {b} B");

        // The sample memory held, from the engine itself: a unit of rows by the width, nothing of the height.
        var statsShort = JpegDecoder.DecodeFittedToWithStats(shorter, 1000, 650, dest);
        var statsTall = JpegDecoder.DecodeFittedToWithStats(tall, 1000, 650, dest);
        statsTall.WholePlanes.ShouldBeFalse();
        statsTall.PeakPlaneBytes.ShouldBe(statsShort.PeakPlaneBytes);
        statsTall.PeakPlaneBytes.ShouldBeLessThan(2000L * 2600 / 8, $"{statsTall.PeakPlaneBytes} B of strips for three 2000×2600 planes");
    }

    [Fact]
    public void AFittedDecodeOfARegion_CostsWhatTheRegionCosts()
    {
        // The fitted sink changes what happens to the rows, not which ones are decoded: with row restarts only the region's
        // MCU rows (and the margin) are entropy-decoded and transformed, exactly as DecodeRegionTo.
        var jpeg = JpegRegionDecodeTests.Build(1600, 1200, "2x2,1x1,1x1", Kind.RestartRow);
        var region = new JpegRegion(0, 400, 1600, 400);
        var stats = JpegDecoder.DecodeFittedToWithStats(jpeg, 400, 100, new byte[400 * 100 * 4], JpegScale.Full, region);

        stats.EntropyUnits.ShouldBeGreaterThanOrEqualTo(25);
        stats.EntropyUnits.ShouldBeLessThanOrEqualTo(27, "a third of the 75 MCU rows, plus the margin: not the image");
        stats.TransformedUnits.ShouldBe(stats.EntropyUnits);
        stats.WholePlanes.ShouldBeFalse();

        var withoutRegion = JpegDecoder.DecodeFittedToWithStats(jpeg, 400, 300, new byte[400 * 300 * 4]);
        withoutRegion.EntropyUnits.ShouldBe(75);
    }
}
