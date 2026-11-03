using ImageMagick;
using SharpAstro.Gif;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// GIF animations against Magick.NET. ImageMagick writes the files (every disposal method, sub-rectangle frames, a
/// distinct palette per frame so local colour tables appear, transparency, interlace, odd sizes) and reads them back
/// through <c>Coalesce()</c>; every frame the decoder returns must equal Magick's coalesced frame byte for byte,
/// alpha-0 pixels' RGB included. There is no install and no skip. Each fixture is parsed first and asserted to carry what its
/// name says, so a Magick release that quietly stops writing a disposal method or a local table fails here instead of
/// turning the case into a plain one.
/// <para>
/// Disposal values ImageMagick will not write (the reserved 4-7), chains of disposal 2 and 3 over differently placed frames,
/// a frame 0 smaller than the screen, bombs and truncation come from <see cref="GifBuilder"/>, with Magick still
/// supplying the expected pixels wherever it reads the file.
/// </para>
/// </summary>
public sealed class GifAnimationTests
{
    // ---- the oracle -------------------------------------------------------------------------------------------

    private static byte[][] MagickFrames(byte[] gif, out int width, out int height)
    {
        using var collection = new MagickImageCollection(gif);
        collection.Coalesce();
        width = (int)collection[0].Width;
        height = (int)collection[0].Height;
        return collection.Select(f => f.GetPixels().ToByteArray(PixelMapping.RGBA)!).ToArray();
    }

    private static void ShouldMatchMagick(byte[] gif)
    {
        var expected = MagickFrames(gif, out int w, out int h);
        var ours = GifDecoder.DecodeAnimation(gif);

        ours.Width.ShouldBe(w);
        ours.Height.ShouldBe(h);
        ours.IsTruncated.ShouldBeFalse();
        ours.Frames.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            ours.Frames[i].Index.ShouldBe(i);
            ours.Frames[i].Rgba.Length.ShouldBe(expected[i].Length);
            ours.Frames[i].Rgba.AsSpan().SequenceEqual(expected[i]).ShouldBeTrue($"frame {i}: " + FirstDifference(ours.Frames[i].Rgba, expected[i], w));
        }
    }

    private static string FirstDifference(byte[] ours, byte[] expected, int width)
    {
        for (int i = 0; i < ours.Length; i++)
        {
            if (ours[i] != expected[i])
            {
                int p = i / 4;
                return $"first difference at pixel ({p % width},{p / width}) channel {i % 4}: ours {ours[i]}, Magick {expected[i]}";
            }
        }
        return "identical";
    }

    // ---- fixtures written by ImageMagick ------------------------------------------------------------------------

    private static (byte R, byte G, byte B, byte A) Colour(int[][] palette, int k) =>
        ((byte)palette[k][0], (byte)palette[k][1], (byte)palette[k][2], 255);

    // Two palettes with nothing in common, so a frame drawn from the second needs a local table.
    private static readonly int[][] WarmColours =
        [[255, 0, 0], [255, 128, 0], [255, 255, 0], [200, 60, 60], [128, 0, 0], [255, 200, 150], [90, 30, 0], [250, 250, 200]];

    private static readonly int[][] CoolColours =
        [[0, 0, 255], [0, 128, 255], [0, 255, 255], [60, 60, 200], [0, 0, 128], [150, 200, 255], [0, 30, 90], [200, 250, 250]];

    // A cheap deterministic noise so the LZW table really fills and no region is a flat colour.
    private static int Noise(int x, int y, int n, int seed = 0)
    {
        uint s = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791) ^ 0x9E3779B9u;
        s ^= s >> 13;
        s *= 0x85EBCA6Bu;
        s ^= s >> 16;
        return (int)(s % (uint)n);
    }

    private static MagickImage Frame(int screenW, int screenH, int x, int y, int w, int h, int[][] palette, int seed,
        GifDisposeMethod dispose, int delay, int holes = 0, bool interlace = false)
    {
        var raw = new byte[w * h * 4];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                var (r, g, b, a) = Colour(palette, Noise(i, j, palette.Length, seed));
                if (holes > 0 && Noise(i, j, holes, seed + 100) == 0)
                {
                    (r, g, b, a) = (0, 0, 0, 0);
                }
                int o = (j * w + i) * 4;
                raw[o] = r;
                raw[o + 1] = g;
                raw[o + 2] = b;
                raw[o + 3] = a;
            }
        }

        var image = new MagickImage(raw, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));
        image.Format = MagickFormat.Gif;
        image.Page = new MagickGeometry(x, y, (uint)screenW, (uint)screenH);
        image.GifDisposeMethod = dispose;
        image.AnimationDelay = (uint)delay;
        if (interlace)
        {
            image.Settings.Interlace = Interlace.Gif;
        }
        return image;
    }

    private static byte[] Write(int loops, params MagickImage[] frames)
    {
        using var collection = new MagickImageCollection();
        foreach (var f in frames)
        {
            collection.Add(f);
        }
        collection[0].AnimationIterations = (uint)loops;
        using var stream = new MemoryStream();
        collection.Write(stream, MagickFormat.Gif);
        return stream.ToArray();
    }

    private const int Sw = 40, Sh = 30;

    private static readonly Dictionary<string, (Func<byte[]> Build, Action<GifInfo> Check)> Fixtures = new()
    {
        ["disposal-none-overlays-with-local-palettes"] = (
            () => Write(0,
                Frame(Sw, Sh, 0, 0, Sw, Sh, WarmColours, 1, GifDisposeMethod.None, 5),
                Frame(Sw, Sh, 5, 3, 12, 9, CoolColours, 2, GifDisposeMethod.None, 7),
                Frame(Sw, Sh, 20, 15, 10, 10, WarmColours, 3, GifDisposeMethod.None, 9, holes: 3)),
            i =>
            {
                i.FrameCount.ShouldBe(3);
                i.Frames.ShouldAllBe(f => f.DisposalMethod == 1);
                i.Frames[1].HasLocalColorTable.ShouldBeTrue("the second palette shares nothing with the first");
                i.Frames[2].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
                i.Frames[1].X.ShouldBe(5);
                i.Frames[1].Width.ShouldBe(12);
            }),
        ["disposal-background-clears-each-rectangle"] = (
            () => Write(2,
                Frame(Sw, Sh, 0, 0, Sw, Sh, WarmColours, 4, GifDisposeMethod.Background, 4),
                Frame(Sw, Sh, 3, 4, 20, 14, CoolColours, 5, GifDisposeMethod.Background, 4, holes: 4),
                Frame(Sw, Sh, 10, 10, 14, 12, WarmColours, 6, GifDisposeMethod.Background, 4),
                Frame(Sw, Sh, 0, 0, Sw, Sh, CoolColours, 7, GifDisposeMethod.None, 4, holes: 3)),
            i =>
            {
                i.Frames.Take(3).ShouldAllBe(f => f.DisposalMethod == 2);
                i.Frames[1].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
                i.Frames[3].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
            }),
        ["disposal-previous-restores-what-was-under-the-frame"] = (
            () => Write(0,
                Frame(Sw, Sh, 0, 0, Sw, Sh, WarmColours, 8, GifDisposeMethod.None, 3),
                Frame(Sw, Sh, 4, 4, 18, 14, CoolColours, 9, GifDisposeMethod.Previous, 3),
                Frame(Sw, Sh, 10, 8, 18, 14, WarmColours, 10, GifDisposeMethod.Previous, 3, holes: 3),
                Frame(Sw, Sh, 2, 2, 10, 10, CoolColours, 11, GifDisposeMethod.None, 3)),
            i =>
            {
                i.Frames[1].DisposalMethod.ShouldBe(3);
                i.Frames[2].DisposalMethod.ShouldBe(3);
                i.Frames[2].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
            }),
        ["transparent-first-frame-smaller-than-the-screen"] = (
            () => Write(0,
                Frame(Sw, Sh, 6, 5, 20, 12, WarmColours, 12, GifDisposeMethod.Background, 6, holes: 4),
                Frame(Sw, Sh, 12, 9, 20, 12, CoolColours, 13, GifDisposeMethod.None, 6, holes: 4),
                Frame(Sw, Sh, 0, 0, Sw, Sh, WarmColours, 14, GifDisposeMethod.Previous, 6, holes: 2),
                Frame(Sw, Sh, 20, 20, 8, 6, CoolColours, 15, GifDisposeMethod.None, 6)),
            i =>
            {
                i.Frames[0].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
                i.Frames[0].X.ShouldBe(6);
                i.Frames[0].Width.ShouldBeLessThan(i.Width);
            }),
        ["interlaced-frames"] = (
            () => Write(0,
                Frame(Sw, Sh, 0, 0, Sw, Sh, WarmColours, 16, GifDisposeMethod.None, 2, interlace: true),
                Frame(Sw, Sh, 7, 3, 21, 17, CoolColours, 17, GifDisposeMethod.Background, 2, holes: 3, interlace: true),
                Frame(Sw, Sh, 2, 11, 19, 13, WarmColours, 18, GifDisposeMethod.None, 2, holes: 3, interlace: true)),
            i => i.Frames.ShouldAllBe(f => f.Interlaced)),
        ["odd-sizes"] = (
            () => Write(0,
                Frame(7, 5, 0, 0, 7, 5, WarmColours, 19, GifDisposeMethod.None, 1),
                Frame(7, 5, 1, 1, 3, 2, CoolColours, 20, GifDisposeMethod.Background, 1),
                Frame(7, 5, 6, 4, 1, 1, WarmColours, 21, GifDisposeMethod.Previous, 1),
                Frame(7, 5, 0, 0, 7, 5, CoolColours, 22, GifDisposeMethod.None, 1, holes: 2)),
            i =>
            {
                i.Width.ShouldBe(7);
                i.Height.ShouldBe(5);
                i.Frames[2].Width.ShouldBe(1);
                i.Frames[2].Height.ShouldBe(1);
            }),
        ["odd-1x1-screen"] = (
            () => Write(0,
                Frame(1, 1, 0, 0, 1, 1, WarmColours, 23, GifDisposeMethod.Background, 1),
                Frame(1, 1, 0, 0, 1, 1, CoolColours, 24, GifDisposeMethod.None, 1)),
            i =>
            {
                i.Width.ShouldBe(1);
                i.FrameCount.ShouldBe(2);
            }),
        ["many-frames-on-a-wide-screen"] = (
            () => Write(0, Enumerable.Range(0, 12).Select(k =>
                Frame(333, 17, (k * 23) % 300, (k * 5) % 12, 31, 5, k % 2 == 0 ? WarmColours : CoolColours, 30 + k,
                    k % 4 == 3 ? GifDisposeMethod.Previous : k % 4 == 1 ? GifDisposeMethod.Background : GifDisposeMethod.None, 3,
                    holes: k % 3)).ToArray()),
            i => i.FrameCount.ShouldBe(12)),
    };

    public static TheoryData<string> FixtureNames() => new(Fixtures.Keys);

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void MagickWrittenAnimation_IsWhatItsNameSays_AndEveryFrameMatchesCoalesce(string name)
    {
        var (build, check) = Fixtures[name];
        var gif = build();

        var info = GifDecoder.ReadInfo(gif);
        info.HasTrailer.ShouldBeTrue();
        info.UnparsedByteCount.ShouldBe(0);
        check(info);

        ShouldMatchMagick(gif);
    }

    [Fact]
    public void MagickWrittenDelaysAndLoopCount_AreReportedRaw()
    {
        var gif = Write(3,
            Frame(Sw, Sh, 0, 0, Sw, Sh, WarmColours, 1, GifDisposeMethod.None, 7),
            Frame(Sw, Sh, 0, 0, Sw, Sh, CoolColours, 2, GifDisposeMethod.None, 0),
            Frame(Sw, Sh, 0, 0, Sw, Sh, WarmColours, 3, GifDisposeMethod.None, 250));

        var animation = GifDecoder.DecodeAnimation(gif);
        // The expected count is read from the bytes Magick wrote, not assumed from AnimationIterations: observed, Magick
        // writes iterations 3 as a NETSCAPE2.0 loop count of 2 (it counts plays, the extension counts repeats).
        int app = gif.AsSpan().IndexOf("NETSCAPE2.0"u8);
        app.ShouldBeGreaterThan(0);
        int written = gif[app + 11 + 2] | (gif[app + 11 + 3] << 8);
        written.ShouldBeGreaterThan(0);
        animation.LoopCount.ShouldBe(written);
        animation.Frames.Select(f => f.DelayCentiseconds).ShouldBe([7, 0, 250]);
        animation.Frames.Select(f => f.DisposalMethod).ShouldBe([1, 1, 1]);
        animation.Info.LoopCount.ShouldBe(written);
    }

    // ---- hand-built files, expected pixels still from Magick -------------------------------------------------------

    private static readonly byte[] FourColours = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];

    // 6x4 screen with background index 2 (blue). Frame 0 is a full screen or a 3x2 rectangle at (1,1); frame 1 is 2x2 at (2,0)
    // overlapping it; frame 2 is 3x2 at (0,2) with index 3 transparent, so it shows what disposal left under it.
    private static byte[] ThreeFrames(int disposal0, int disposal1, bool partialFirst, int transparent0)
    {
        var b = new GifBuilder().Header(6, 4, FourColours, background: 2).Netscape(0);
        b.GraphicControl(delay: 5, disposal: disposal0, transparentIndex: transparent0);
        var first = Enumerable.Range(0, 24).Select(i => (byte)(i % 3)).ToArray();
        if (partialFirst)
        {
            b.Image(1, 1, 3, 2, first[..6], 2);
        }
        else
        {
            b.Image(0, 0, 6, 4, first, 2);
        }
        b.GraphicControl(delay: 6, disposal: disposal1);
        b.Image(2, 0, 2, 2, [1, 1, 0, 0], 2);
        b.GraphicControl(delay: 7, transparentIndex: 3);
        b.Image(0, 2, 3, 2, [3, 2, 3, 2, 3, 2], 2);
        return b.Trailer().ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryDisposalMethodOfTheFirstFrame_MatchesCoalesce_WithAndWithoutTransparencyAndCoverage(int disposal)
    {
        // The reserved values 4-7 behave as "do not dispose" in Magick's Coalesce (observed), and so here.
        foreach (var partial in new[] { false, true })
        {
            foreach (var transparent in new[] { -1, 3 })
            {
                var gif = ThreeFrames(disposal, 0, partial, transparent);
                GifDecoder.ReadInfo(gif).Frames[0].DisposalMethod.ShouldBe(disposal);
                ShouldMatchMagick(gif);
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(1, 6)]
    [InlineData(7, 3)]
    public void ChainsOfDisposalOverDifferentlyPlacedFrames_MatchCoalesce(int disposal0, int disposal1)
    {
        foreach (var partial in new[] { false, true })
        {
            ShouldMatchMagick(ThreeFrames(disposal0, disposal1, partial, -1));
            ShouldMatchMagick(ThreeFrames(disposal0, disposal1, partial, 3));
        }
    }

    [Fact]
    public void DisposalBackgroundKeepsTheRgbOfWhatItClears_AsCoalesceDoes()
    {
        // The policy the decoder matches, pinned on its own so a change to it is not hidden among whole-frame comparisons:
        // a cleared red pixel reads 255,0,0 with alpha 0, and a transparent pixel of the next frame over an
        // already transparent one reads 0,0,0,0 (Magick's Over of two transparent pixels).
        var gif = ThreeFrames(2, 0, partialFirst: false, transparent0: -1);
        var frames = GifDecoder.DecodeAnimation(gif).Frames;

        frames[1].Rgba.AsSpan(0, 4).ToArray().ShouldBe([255, 0, 0, 0], "frame 0 was disposed: red, alpha 0");
        frames[2].Rgba.AsSpan((2 * 6 + 0) * 4, 4).ToArray().ShouldBe([0, 0, 0, 0], "transparent over transparent");
        frames[2].Rgba.AsSpan((2 * 6 + 1) * 4, 4).ToArray().ShouldBe([0, 0, 255, 255], "an opaque pixel of frame 2");
        frames[2].Rgba.AsSpan((2 * 6 + 4) * 4, 4).ToArray().ShouldBe([0, 255, 0, 0], "outside frame 2: left as disposal made it");
        ShouldMatchMagick(gif);
    }

    [Fact]
    public void DisposalPreviousOnTheFirstFrame_GoesBackToTheInitialCanvas_NotToTheBackgroundOfNothing()
    {
        var gif = ThreeFrames(3, 0, partialFirst: true, transparent0: -1);
        var frames = GifDecoder.DecodeAnimation(gif).Frames;

        // Frame 0's rectangle held the background colour (blue, opaque) before it was drawn.
        frames[1].Rgba.AsSpan((1 * 6 + 1) * 4, 4).ToArray().ShouldBe([0, 0, 255, 255]);
        ShouldMatchMagick(gif);

        var transparentCanvas = GifDecoder.DecodeAnimation(ThreeFrames(3, 0, partialFirst: true, transparent0: 3)).Frames;
        transparentCanvas[1].Rgba.AsSpan((1 * 6 + 1) * 4, 4).ToArray().ShouldBe([0, 0, 255, 0], "the initial canvas is alpha 0 when frame 0 has a transparent index");
    }

    [Fact]
    public void TheLastFramesDisposal_IsNeverApplied()
    {
        // Frame 0 disposes to background and is the only frame: what comes out is the drawn frame, not a cleared canvas.
        var gif = new GifBuilder().Header(3, 2, FourColours).GraphicControl(disposal: 2)
            .Image(0, 0, 3, 2, [0, 1, 2, 0, 1, 2], 2).Trailer().ToArray();
        var frames = GifDecoder.DecodeAnimation(gif).Frames;

        frames.Count.ShouldBe(1);
        frames[0].Rgba.Where((_, i) => i % 4 == 3).ShouldAllBe(a => a == 255);
        ShouldMatchMagick(gif);
    }

    [Fact]
    public void RawDelaysIncludingZeroAndTheMaximum_AndTheLoopCount_AreReported()
    {
        var gif = new GifBuilder().Header(2, 2, FourColours).Netscape(0x1234)
            .GraphicControl(delay: 0).Image(0, 0, 2, 2, [0, 1, 2, 3], 2)
            .GraphicControl(delay: 65535, disposal: 2).Image(0, 0, 2, 2, [3, 2, 1, 0], 2)
            .Image(0, 0, 2, 2, [0, 0, 0, 0], 2)
            .Trailer().ToArray();

        var animation = GifDecoder.DecodeAnimation(gif);
        animation.LoopCount.ShouldBe(0x1234);
        animation.Frames.Select(f => f.DelayCentiseconds).ShouldBe([0, 65535, 0], "a frame with no GCE has delay 0, and none of 0/100 ms policy is applied");
        animation.Frames.Select(f => f.DisposalMethod).ShouldBe([0, 2, 0]);
        animation.Frames[1].Frame.DisposalMethod.ShouldBe(2);
    }

    [Fact]
    public void AGifWithNoLoopExtension_HasANullLoopCount()
    {
        var gif = new GifBuilder().Header(2, 2, FourColours).Image(0, 0, 2, 2, [0, 1, 2, 3], 2).Trailer().ToArray();
        GifDecoder.DecodeAnimation(gif).LoopCount.ShouldBeNull();
    }

    [Fact]
    public void AStill_IsOneFrame_EqualToDecode()
    {
        var gif = ThreeFrames(0, 0, partialFirst: true, transparent0: 3);
        var still = GifDecoder.Decode(gif).Rgba;
        GifDecoder.DecodeAnimation(gif).Frames[0].Rgba.ShouldBe(still, "frame 0 of an animation is the still, byte for byte");

        var single = new GifBuilder().Header(3, 2, FourColours).Image(0, 0, 3, 2, [0, 1, 2, 0, 1, 2], 2).Trailer().ToArray();
        var animation = GifDecoder.DecodeAnimation(single);
        animation.Frames.Count.ShouldBe(1);
        animation.Frames[0].Rgba.ShouldBe(GifDecoder.Decode(single).Rgba);
    }

    [Fact]
    public void FramesOwnTheirPixels_SoDisposalOfALaterFrameDoesNotReachBack()
    {
        var gif = ThreeFrames(2, 2, partialFirst: false, transparent0: -1);
        var frames = GifDecoder.DecodeAnimation(gif).Frames;
        frames[0].Rgba.ShouldNotBeSameAs(frames[1].Rgba);
        frames[0].Rgba.Where((_, i) => i % 4 == 3).ShouldAllBe(a => a == 255, "frame 0 stays opaque although frame 1 shows it disposed");
    }

    // ---- streaming ----------------------------------------------------------------------------------------------------

    public static TheoryData<string> StreamingCases() => new(Fixtures.Keys.Append("hand-built-chain"));

    [Theory]
    [MemberData(nameof(StreamingCases))]
    public void Streaming_GivesTheFramesMaterialisingGives(string name)
    {
        var gif = name == "hand-built-chain" ? ThreeFrames(3, 2, partialFirst: true, transparent0: 3) : Fixtures[name].Build();
        var materialised = GifDecoder.DecodeAnimation(gif);

        var streamed = new GifAnimationDecoder(gif);
        streamed.Width.ShouldBe(materialised.Width);
        streamed.Height.ShouldBe(materialised.Height);
        streamed.FrameCount.ShouldBe(materialised.Frames.Count);
        streamed.LoopCount.ShouldBe(materialised.LoopCount);

        int i = 0;
        while (streamed.MoveNext())
        {
            var current = streamed.Current;
            current.Index.ShouldBe(i);
            current.DelayCentiseconds.ShouldBe(materialised.Frames[i].DelayCentiseconds);
            current.DisposalMethod.ShouldBe(materialised.Frames[i].DisposalMethod);
            current.IsTruncated.ShouldBe(materialised.Frames[i].IsTruncated);
            current.Rgba.AsSpan().SequenceEqual(materialised.Frames[i].Rgba).ShouldBeTrue($"frame {i}");
            i++;
        }

        i.ShouldBe(materialised.Frames.Count);
        streamed.IsTruncated.ShouldBeFalse();
        streamed.MoveNext().ShouldBeFalse("after the last frame it stays at the end");
    }

    [Fact]
    public void StreamedFrames_ShareOneCanvas_AndCurrentIsNotAvailableBeforeTheFirstMoveNext()
    {
        var gif = ThreeFrames(0, 0, partialFirst: false, transparent0: -1);
        var streamed = new GifAnimationDecoder(gif);
        Should.Throw<InvalidOperationException>(() => _ = streamed.Current);

        streamed.MoveNext().ShouldBeTrue();
        var first = streamed.Current.Rgba;
        var firstCopy = (byte[])first.Clone();
        streamed.MoveNext().ShouldBeTrue();
        streamed.Current.Rgba.ShouldBeSameAs(first, "the decoder hands out its one working canvas");
        first.ShouldNotBe(firstCopy, "and drawing the second frame changed it, so a caller that keeps frames copies them");
    }

    [Fact]
    public void Streaming_HoldsOneCanvas_WhereMaterialisingHoldsOnePerFrame()
    {
        // 1024x1024 screen (4 MiB canvas), 24 small frames, one of which asks for disposal 3 (the only one that costs a snapshot).
        var b = new GifBuilder().Header(1024, 1024, FourColours);
        for (int k = 0; k < 24; k++)
        {
            b.GraphicControl(disposal: k == 5 ? 3 : 1).Image(k * 4, k * 4, 8, 8, Enumerable.Range(0, 64).Select(i => (byte)((i + k) % 4)).ToArray(), 2);
        }
        var gif = b.Trailer().ToArray();
        long canvasBytes = 1024L * 1024 * 4;

        // Warm up everything once.
        var warm = new GifAnimationDecoder(gif);
        while (warm.MoveNext()) { }
        GifDecoder.DecodeAnimation(gif);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var streamed = new GifAnimationDecoder(gif);
        int count = 0;
        while (streamed.MoveNext())
        {
            count++;
        }
        long streamedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        GifDecoder.DecodeAnimation(gif).Frames.Count.ShouldBe(24);
        long materialisedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        count.ShouldBe(24);
        streamedBytes.ShouldBeLessThan(canvasBytes * 3 / 2, "one canvas, a rectangle snapshot and small per-frame scratch");
        materialisedBytes.ShouldBeGreaterThan(canvasBytes * 24, "a full canvas per frame, plus the working one");
    }

    // ---- limits ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFewHundredBytesDeclaringManyLargeFrames_IsRefusedBeforeAnythingIsAllocated()
    {
        // 8192x8192 is exactly the screen limit (2^26 pixels), 256 MiB of RGBA per frame. A 1x1 frame costs about 15 bytes.
        var b = new GifBuilder().Header(8192, 8192, FourColours);
        for (int k = 0; k < 40; k++)
        {
            b.Image(0, 0, 1, 1, [(byte)(k % 4)], 2);
        }
        var gif = b.Trailer().ToArray();
        gif.Length.ShouldBeLessThan(1000);
        GifDecoder.ReadInfo(gif).FrameCount.ShouldBe(40);

        // Warm the throw path so the measurement sees steady-state allocation only.
        Should.Throw<InvalidDataException>(() => GifDecoder.DecodeAnimation(gif));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Should.Throw<InvalidDataException>(() => GifDecoder.DecodeAnimation(gif));
        Should.Throw<InvalidDataException>(() => GifDecoder.DecodeAnimation(gif, 1L << 28));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 40 frames of 256 MiB would be 10 GiB; the refusal costs the parse of 40 frame headers and the exceptions.
        allocated.ShouldBeLessThan(1 << 20);
    }

    [Fact]
    public void TheDefaultLimitIs2Pow28Pixels_FiveFramesOfAMaximalScreenAreOver_AndALimitOneUnderFourIsRefused()
    {
        GifDecoder.DefaultMaximumAnimationPixels.ShouldBe(1L << 28);

        // Four frames of 2^26 pixels sit exactly at the limit, five are over it; only refusals are exercised here (a passing
        // decode of four would allocate 1 GiB), the exact-fit pass is in the caller-limit test below.
        GifBuilder Frames(int n)
        {
            var b = new GifBuilder().Header(8192, 8192, FourColours);
            for (int k = 0; k < n; k++)
            {
                b.Image(0, 0, 1, 1, [0], 2);
            }
            return b;
        }
        Should.Throw<InvalidDataException>(() => GifDecoder.DecodeAnimation(Frames(5).Trailer().ToArray()));
        Should.Throw<InvalidDataException>(() => GifDecoder.DecodeAnimation(Frames(4).Trailer().ToArray(), (1L << 28) - 1));
    }

    [Fact]
    public void ACallerLimit_IsChargedAsScreenTimesFrames_ExactlyAtItPasses_OneUnderIsRefused()
    {
        var b = new GifBuilder().Header(64, 64, FourColours);
        for (int k = 0; k < 10; k++)
        {
            b.Image(0, 0, 2, 2, [0, 1, 2, 3], 2);
        }
        var gif = b.Trailer().ToArray();

        GifDecoder.DecodeAnimation(gif, 64 * 64 * 10).Frames.Count.ShouldBe(10);
        Should.Throw<InvalidDataException>(() => GifDecoder.DecodeAnimation(gif, 64 * 64 * 10 - 1));
        Should.Throw<ArgumentOutOfRangeException>(() => GifDecoder.DecodeAnimation(gif, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => GifDecoder.DecodeAnimation(gif, -5));
    }

    [Fact]
    public void AScreenOverTheScreenLimit_IsRefusedByTheStreamingDecoderToo()
    {
        var gif = new GifBuilder().Header(8192, 8193, FourColours).Image(0, 0, 1, 1, [0], 2).Trailer().ToArray();
        Should.Throw<InvalidDataException>(() => new GifAnimationDecoder(gif));
        Should.Throw<InvalidDataException>(() => GifDecoder.DecodeAnimation(gif));
    }

    // ---- damage -----------------------------------------------------------------------------------------------------------

    private static byte[] ThreeFullFrames(bool trailer = true)
    {
        var b = new GifBuilder().Header(9, 7, FourColours);
        for (int k = 0; k < 3; k++)
        {
            b.GraphicControl(delay: 4, disposal: 1, transparentIndex: k == 0 ? -1 : 3)
                .Image(0, 0, 9, 7, Enumerable.Range(0, 63).Select(i => (byte)((i * (k + 1) + k) % 3)).ToArray(), 2);
        }
        if (trailer)
        {
            b.Trailer();
        }
        return b.ToArray();
    }

    [Fact]
    public void AFileCutInsideTheLastFrame_GivesTheFramesBeforeItIntact_AndSaysTruncated()
    {
        var whole = ThreeFullFrames();
        var expected = GifDecoder.DecodeAnimation(whole);
        expected.IsTruncated.ShouldBeFalse();

        var cut = whole[..^12];
        var animation = GifDecoder.DecodeAnimation(cut);

        animation.IsTruncated.ShouldBeTrue();
        animation.Info.HasTrailer.ShouldBeFalse();
        animation.Frames.Count.ShouldBe(3);
        animation.Frames[0].IsTruncated.ShouldBeFalse();
        animation.Frames[1].IsTruncated.ShouldBeFalse();
        animation.Frames[2].IsTruncated.ShouldBeTrue();
        animation.Frames[2].Frame.IsTruncated.ShouldBeTrue();
        animation.Frames[0].Rgba.ShouldBe(expected.Frames[0].Rgba);
        animation.Frames[1].Rgba.ShouldBe(expected.Frames[1].Rgba);

        // The last frame is a prefix of the whole one: the last row, which the cut data did not reach, is frame 1's.
        animation.Frames[2].Rgba.AsSpan(6 * 9 * 4).ToArray().ShouldBe(animation.Frames[1].Rgba.AsSpan(6 * 9 * 4).ToArray());
        animation.Frames[2].Rgba.ShouldNotBe(expected.Frames[2].Rgba);

        var streamed = new GifAnimationDecoder(cut);
        int n = 0;
        while (streamed.MoveNext())
        {
            n++;
        }
        n.ShouldBe(3);
        streamed.IsTruncated.ShouldBeTrue();
        streamed.Current.IsTruncated.ShouldBeTrue();
    }

    [Fact]
    public void AMissingTrailer_LosesNoFrame_AndIsNotTruncation()
    {
        var animation = GifDecoder.DecodeAnimation(ThreeFullFrames(trailer: false));
        animation.Frames.Count.ShouldBe(3);
        animation.IsTruncated.ShouldBeFalse();
        animation.Info.HasTrailer.ShouldBeFalse();
    }

    [Fact]
    public void AnLzwStreamThatEndsEarlyInAMiddleFrame_IsTruncatedThere_AndLaterFramesStillDraw()
    {
        var b = new GifBuilder().Header(9, 7, FourColours);
        b.Image(0, 0, 9, 7, Enumerable.Range(0, 63).Select(i => (byte)(i % 3)).ToArray(), 2);
        b.GraphicControl(transparentIndex: 3).RawImage(0, 0, 9, 7, GifBuilder.Lzw(Enumerable.Range(0, 20).Select(i => (byte)(i % 3)).ToArray(), 2), 2);
        b.Image(0, 0, 9, 7, Enumerable.Range(0, 63).Select(i => (byte)(2 - i % 3)).ToArray(), 2);
        var gif = b.Trailer().ToArray();

        var animation = GifDecoder.DecodeAnimation(gif);
        animation.Frames.Select(f => f.IsTruncated).ShouldBe([false, true, false]);
        animation.IsTruncated.ShouldBeTrue();
        animation.Frames[1].Frame.IsTruncated.ShouldBeFalse("the file is intact; the pixel stream is what is short");
    }
}
