using ImageMagick;
using SharpAstro.Gif;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// GIF stills against Magick.NET. ImageMagick writes the files (global palette, interlaced,
/// transparent, a 2-colour image, odd sizes, a first frame that does not cover the screen) and reads
/// them back; the decoder must produce ImageMagick's <c>Coalesce()</c>d first frame byte for byte, so
/// there is no install and no skip. Every fixture is first parsed and asserted to really carry what its
/// name says, so a Magick release that quietly stops writing an interlace bit or a transparency index
/// fails here instead of turning the case into a plain one.
/// <para>
/// Files ImageMagick will not write on request (a first frame with a local palette, an index past the
/// end of its table) come from <see cref="GifBuilder"/> and are still read by Magick for the expected
/// pixels.
/// </para>
/// </summary>
public sealed class GifStillTests
{
    // ---- the oracle -----------------------------------------------------------------------------------

    private static byte[] MagickFrame0(byte[] gif, out int width, out int height)
    {
        using var collection = new MagickImageCollection(gif);
        collection.Coalesce();
        var frame = collection[0];
        width = (int)frame.Width;
        height = (int)frame.Height;
        return frame.GetPixels().ToByteArray(PixelMapping.RGBA)!;
    }

    private static void ShouldMatchMagick(byte[] gif)
    {
        var expected = MagickFrame0(gif, out int w, out int h);
        var ours = GifDecoder.Decode(gif);
        ours.Width.ShouldBe(w);
        ours.Height.ShouldBe(h);
        ours.IsTruncated.ShouldBeFalse();
        ours.Rgba.Length.ShouldBe(expected.Length);
        ours.Rgba.AsSpan().SequenceEqual(expected).ShouldBeTrue(FirstDifference(ours.Rgba, expected, w));
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

    // ---- fixtures written by ImageMagick ---------------------------------------------------------------

    private static readonly (byte R, byte G, byte B)[] Sixteen = Enumerable.Range(0, 16)
        .Select(i => ((byte)(i * 17), (byte)(255 - i * 13), (byte)((i * 61) % 256))).ToArray();

    private static byte[] MagickGif(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel, Action<MagickImage>? tweak = null)
    {
        var raw = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int o = (y * w + x) * 4;
                raw[o] = r;
                raw[o + 1] = g;
                raw[o + 2] = b;
                raw[o + 3] = a;
            }
        }

        using var image = new MagickImage(raw, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));
        image.Format = MagickFormat.Gif;
        tweak?.Invoke(image);
        return image.ToByteArray();
    }

    // A cheap deterministic noise so the LZW table really fills, not a periodic pattern that compresses to a handful of codes.
    private static int Noise(int x, int y, int n)
    {
        uint s = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ 0x9E3779B9u;
        s ^= s >> 13;
        s *= 0x85EBCA6Bu;
        s ^= s >> 16;
        return (int)(s % (uint)n);
    }

    private static (byte, byte, byte, byte) Opaque16(int x, int y)
    {
        var c = Sixteen[Noise(x, y, 16)];
        return (c.R, c.G, c.B, 255);
    }

    private static readonly Dictionary<string, (Func<byte[]> Build, Action<GifInfo> Check)> Fixtures = new()
    {
        ["global-palette-64x48"] = (
            () => MagickGif(64, 48, Opaque16),
            i =>
            {
                i.HasGlobalColorTable.ShouldBeTrue();
                i.Frames[0].HasLocalColorTable.ShouldBeFalse();
                i.Frames[0].TransparentIndex.ShouldBe(-1);
                i.Frames[0].Interlaced.ShouldBeFalse();
            }),
        ["noise-16-colours-128x128"] = (
            () => MagickGif(128, 128, Opaque16),
            i => i.Frames[0].ColorTableSize.ShouldBeGreaterThan(8)),
        ["noise-256-colours-96x96"] = (
            () => MagickGif(96, 96, (x, y) =>
            {
                int k = Noise(x, y, 256);
                return ((byte)k, (byte)(k * 3), (byte)(255 - k), 255);
            }),
            i =>
            {
                i.Frames[0].ColorTableSize.ShouldBe(256);
                i.Frames[0].MinCodeSize.ShouldBe(8);
            }),
        ["interlaced-31x37"] = (
            () => MagickGif(31, 37, Opaque16, im => im.Settings.Interlace = Interlace.Gif),
            i => i.Frames[0].Interlaced.ShouldBeTrue()),
        ["transparent-40x25"] = (
            () => MagickGif(40, 25, (x, y) => Noise(x, y, 4) == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : Opaque16(x, y)),
            i => i.Frames[0].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0)),
        ["transparent-interlaced-23x19"] = (
            () => MagickGif(23, 19, (x, y) => Noise(x, y, 3) == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : Opaque16(x, y),
                im => im.Settings.Interlace = Interlace.Gif),
            i =>
            {
                i.Frames[0].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
                i.Frames[0].Interlaced.ShouldBeTrue();
            }),
        ["two-colour-minimum-code-size-40x30"] = (
            () => MagickGif(40, 30, (x, y) => ((x / 5 + y / 3) % 2 == 0) ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255)),
            i =>
            {
                i.Frames[0].ColorTableSize.ShouldBe(2);
                i.Frames[0].MinCodeSize.ShouldBe(2);
            }),
        ["odd-1x1"] = (() => MagickGif(1, 1, Opaque16), i => { i.Width.ShouldBe(1); i.Height.ShouldBe(1); }),
        ["odd-7x5"] = (() => MagickGif(7, 5, Opaque16), i => { i.Width.ShouldBe(7); i.Height.ShouldBe(5); }),
        ["odd-333x17"] = (() => MagickGif(333, 17, Opaque16), i => { i.Width.ShouldBe(333); i.Height.ShouldBe(17); }),
        // A 20x12 frame at (10,6) on a 64x48 screen: what Magick's coalesced frame 0 holds around it is
        // the thing the decoder must match.
        ["first-frame-smaller-than-the-screen"] = (
            () => MagickGif(20, 12, Opaque16, im => im.Page = new MagickGeometry(10, 6, 64, 48)),
            i =>
            {
                i.Width.ShouldBe(64);
                i.Height.ShouldBe(48);
                i.Frames[0].X.ShouldBe(10);
                i.Frames[0].Y.ShouldBe(6);
                i.Frames[0].Width.ShouldBe(20);
            }),
        ["transparent-first-frame-smaller-than-the-screen"] = (
            () => MagickGif(20, 12, (x, y) => Noise(x, y, 4) == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : Opaque16(x, y),
                im => im.Page = new MagickGeometry(5, 9, 40, 30)),
            i =>
            {
                i.Frames[0].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
                i.Frames[0].X.ShouldBe(5);
            }),
    };

    public static TheoryData<string> FixtureNames() => new(Fixtures.Keys);

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void MagickWrittenStill_IsWhatItsNameSays_AndDecodesToMagicksPixels(string name)
    {
        var (build, check) = Fixtures[name];
        var gif = build();

        var info = GifDecoder.ReadInfo(gif);
        info.FrameCount.ShouldBe(1);
        info.HasTrailer.ShouldBeTrue();
        info.UnparsedByteCount.ShouldBe(0);
        check(info);

        ShouldMatchMagick(gif);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(17)]
    public void InterlacedStillsOfEveryPassShape_MatchMagick(int height)
    {
        // Heights below 5 leave passes empty (pass 2 starts at row 4, pass 3 at row 2); 8 and 9 sit on the pass-1 boundary.
        var gif = MagickGif(6, height, Opaque16, im => im.Settings.Interlace = Interlace.Gif);
        GifDecoder.ReadInfo(gif).Frames[0].Interlaced.ShouldBeTrue();
        ShouldMatchMagick(gif);
    }

    // ---- hand-built files, expected pixels still from Magick -----------------------------------------------

    private static readonly byte[] FourColours = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];

    private static byte[] Pattern(int w, int h, int colours) =>
        Enumerable.Range(0, w * h).Select(i => (byte)Noise(i % w, i / w, colours)).ToArray();

    [Fact]
    public void ALocalPaletteOnTheFirstFrame_IsUsedAndMatchesMagick()
    {
        var gif = new GifBuilder().Header(9, 7).Image(0, 0, 9, 7, Pattern(9, 7, 4), 2, localRgb: FourColours).Trailer().ToArray();

        var info = GifDecoder.ReadInfo(gif);
        info.HasGlobalColorTable.ShouldBeFalse();
        info.Frames[0].HasLocalColorTable.ShouldBeTrue();
        info.Frames[0].ColorTableSize.ShouldBe(4);

        ShouldMatchMagick(gif);
    }

    [Fact]
    public void ALocalPaletteBeatsTheGlobalOne()
    {
        byte[] global = [10, 10, 10, 20, 20, 20, 30, 30, 30, 40, 40, 40];
        var gif = new GifBuilder().Header(9, 7, global).Image(0, 0, 9, 7, Pattern(9, 7, 4), 2, localRgb: FourColours).Trailer().ToArray();

        var info = GifDecoder.ReadInfo(gif);
        info.HasGlobalColorTable.ShouldBeTrue();
        info.Frames[0].HasLocalColorTable.ShouldBeTrue();

        ShouldMatchMagick(gif);
        GifDecoder.Decode(gif).Rgba[..4].ShouldBe([FourColours[Pattern(9, 7, 4)[0] * 3], FourColours[Pattern(9, 7, 4)[0] * 3 + 1], FourColours[Pattern(9, 7, 4)[0] * 3 + 2], (byte)255]);
    }

    [Fact]
    public void ALocalPaletteInterlacedWithTransparency_MatchesMagick()
    {
        var gif = new GifBuilder().Header(11, 13)
            .GraphicControl(delay: 7, transparentIndex: 1)
            .Image(0, 0, 11, 13, Pattern(11, 13, 4), 2, localRgb: FourColours, interlaced: true).Trailer().ToArray();

        var info = GifDecoder.ReadInfo(gif).Frames[0];
        info.Interlaced.ShouldBeTrue();
        info.TransparentIndex.ShouldBe(1);
        info.HasLocalColorTable.ShouldBeTrue();

        ShouldMatchMagick(gif);
    }

    [Fact]
    public void GifBuiltAs87a_DecodesTheSame()
    {
        var gif = new GifBuilder().Header(9, 7, FourColours, gif89a: false).Image(0, 0, 9, 7, Pattern(9, 7, 4), 2).Trailer().ToArray();
        "GIF87a"u8.SequenceEqual(gif.AsSpan(0, 6)).ShouldBeTrue();
        ShouldMatchMagick(gif);
    }

    [Fact]
    public void AnIndexPastTheEndOfItsPalette_IsOpaqueBlack()
    {
        // Root 3 allows indices 0-7 but the palette has 4 entries. Magick refuses such a file
        // ("invalid colormap index"), so there is no oracle; opaque black is this decoder's own policy,
        // chosen over failing a file every browser shows.
        var gif = new GifBuilder().Header(8, 4, FourColours).Image(0, 0, 8, 4, Enumerable.Range(0, 32).Select(i => (byte)(i % 8)).ToArray(), 3).Trailer().ToArray();
        var rgba = GifDecoder.Decode(gif).Rgba;
        for (int i = 0; i < 32; i++)
        {
            var expected = i % 8 < 4 ? [FourColours[i % 8 * 3], FourColours[i % 8 * 3 + 1], FourColours[i % 8 * 3 + 2], (byte)255] : new byte[] { 0, 0, 0, 255 };
            rgba.AsSpan(i * 4, 4).ToArray().ShouldBe(expected, $"pixel {i}");
        }
    }

    [Theory]
    [InlineData(2, -1)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(0, 2)]
    [InlineData(3, -1)]
    [InlineData(7, -1)]
    [InlineData(7, 1)]
    public void PixelsAroundAFirstFrameThatDoesNotCoverTheScreen_AreTheBackgroundColour_AsMagickHasThem(int background, int transparentIndex)
    {
        // Observed on Magick's coalesced frame 0: the colour is the global table's entry at the
        // background index, opaque, or with alpha 0 when the frame has a transparent index. An index
        // past the table (7 of 4) reads as the last entry.
        var b = new GifBuilder().Header(8, 6, FourColours, background: background);
        if (transparentIndex >= 0)
        {
            b.GraphicControl(transparentIndex: transparentIndex);
        }
        var gif = b.Image(2, 1, 3, 2, [0, 1, 0, 1, 0, 1], 2).Trailer().ToArray();

        ShouldMatchMagick(gif);
        var corner = GifDecoder.Decode(gif).Rgba.AsSpan(0, 4).ToArray();
        corner[3].ShouldBe((byte)(transparentIndex >= 0 ? 0 : 255));
    }

    [Fact]
    public void WithoutAGlobalTable_TheAreaAroundTheFrameIsWhite_AsMagickHasIt()
    {
        var gif = new GifBuilder().Header(8, 6, null, background: 1).Image(2, 1, 3, 2, [0, 1, 0, 1, 0, 1], 2, localRgb: FourColours).Trailer().ToArray();
        ShouldMatchMagick(gif);
        GifDecoder.Decode(gif).Rgba.AsSpan(0, 4).ToArray().ShouldBe([255, 255, 255, 255]);
    }

    [Fact]
    public void ASecondFrameIsReportedButNotDecodedIntoTheStill()
    {
        byte[] other = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var gif = new GifBuilder().Header(5, 5, FourColours).Netscape(3)
            .GraphicControl(delay: 0, disposal: 1).Image(0, 0, 5, 5, Pattern(5, 5, 4), 2)
            .GraphicControl(delay: 25, disposal: 2, transparentIndex: 3).Image(1, 1, 3, 3, Pattern(3, 3, 4), 2, localRgb: other)
            .Trailer().ToArray();

        var info = GifDecoder.ReadInfo(gif);
        info.FrameCount.ShouldBe(2);
        info.LoopCount.ShouldBe(3);
        info.Frames[0].DelayCentiseconds.ShouldBe(0, "delays are reported raw, with no 0-means-100ms policy");
        info.Frames[0].DisposalMethod.ShouldBe(1);
        info.Frames[1].DelayCentiseconds.ShouldBe(25);
        info.Frames[1].DisposalMethod.ShouldBe(2);
        info.Frames[1].TransparentIndex.ShouldBe(3);
        info.Frames[1].HasLocalColorTable.ShouldBeTrue();

        // Frame 0 alone, first-frame pixels: the second frame must not leak in.
        var alone = new GifBuilder().Header(5, 5, FourColours).Image(0, 0, 5, 5, Pattern(5, 5, 4), 2).Trailer().ToArray();
        GifDecoder.Decode(gif).Rgba.ShouldBe(GifDecoder.Decode(alone).Rgba);
        ShouldMatchMagick(gif);
    }

    [Fact]
    public void ANetscapeAndAnAnimextsLoopCount_AreBothRead_AndAnAbsentOneIsNull()
    {
        byte[] app = [0x21, 0xFF, 11, .. "ANIMEXTS1.0"u8.ToArray(), 3, 1, 0x34, 0x12, 0];
        var animexts = new GifBuilder().Header(2, 2, FourColours).Append(app).Image(0, 0, 2, 2, [0, 1, 2, 3], 2).Trailer().ToArray();
        GifDecoder.ReadInfo(animexts).LoopCount.ShouldBe(0x1234);

        var forever = new GifBuilder().Header(2, 2, FourColours).Netscape(0).Image(0, 0, 2, 2, [0, 1, 2, 3], 2).Trailer().ToArray();
        GifDecoder.ReadInfo(forever).LoopCount.ShouldBe(0);

        var none = new GifBuilder().Header(2, 2, FourColours).Image(0, 0, 2, 2, [0, 1, 2, 3], 2).Trailer().ToArray();
        GifDecoder.ReadInfo(none).LoopCount.ShouldBeNull();

        // An application extension of another vendor is skipped, not read as a loop count.
        byte[] other = [0x21, 0xFF, 11, .. "XMP DataXMP"u8.ToArray(), 3, 1, 9, 0, 0];
        var foreign = new GifBuilder().Header(2, 2, FourColours).Append(other).Image(0, 0, 2, 2, [0, 1, 2, 3], 2).Trailer().ToArray();
        GifDecoder.ReadInfo(foreign).LoopCount.ShouldBeNull();
    }

    // ---- tolerated damage ---------------------------------------------------------------------------------------

    private static byte[] Plain(Func<GifBuilder, GifBuilder>? between = null, bool trailer = true)
    {
        var b = new GifBuilder().Header(9, 7, FourColours);
        if (between is not null)
        {
            b = between(b);
        }
        b.Image(0, 0, 9, 7, Pattern(9, 7, 4), 2);
        if (trailer)
        {
            b.Trailer();
        }
        return b.ToArray();
    }

    // 9x7, colours 0-2 drawn, index 3 transparent: every drawn pixel is opaque, every pixel the data does not reach is alpha 0.
    private static byte[] Transparent3() =>
        new GifBuilder().Header(9, 7, FourColours).GraphicControl(transparentIndex: 3)
            .Image(0, 0, 9, 7, Pattern(9, 7, 3), 2).Trailer().ToArray();

    [Fact]
    public void AnUnknownExtension_IsSkippedBySubBlockLength()
    {
        var baseline = GifDecoder.Decode(Plain()).Rgba;

        // 600 payload bytes: three sub-blocks (255 + 255 + 90), one of which contains 0x2C and 0x3B.
        var payload = Enumerable.Range(0, 600).Select(i => i % 7 == 0 ? (byte)0x2C : i % 11 == 0 ? (byte)0x3B : (byte)i).ToArray();
        var gif = Plain(b => b.Extension(0x99, payload).Extension(0xFE, "a comment"u8.ToArray())
            .Extension(0x01, new byte[12 + 5])); // a plain-text extension, whose sub-blocks hold text

        var decoded = GifDecoder.Decode(gif);
        decoded.Info.FrameCount.ShouldBe(1);
        decoded.IsTruncated.ShouldBeFalse();
        decoded.Rgba.ShouldBe(baseline);
    }

    [Fact]
    public void TrailingGarbageAfterTheTrailer_IsReportedAndTheImageIsComplete()
    {
        var baseline = GifDecoder.Decode(Plain()).Rgba;
        var gif = Plain().Concat(new byte[] { 0x2C, 0x00, 0x21, 0xFF, 0x12, 0x34 }).ToArray();

        var decoded = GifDecoder.Decode(gif);
        decoded.Info.HasTrailer.ShouldBeTrue();
        decoded.Info.UnparsedByteCount.ShouldBe(6);
        decoded.IsTruncated.ShouldBeFalse();
        decoded.Rgba.ShouldBe(baseline);
    }

    [Fact]
    public void AMissingTrailer_IsReported_NotFatal()
    {
        var baseline = GifDecoder.Decode(Plain()).Rgba;
        var decoded = GifDecoder.Decode(Plain(trailer: false));

        decoded.Info.HasTrailer.ShouldBeFalse();
        decoded.Info.UnparsedByteCount.ShouldBe(0);
        decoded.IsTruncated.ShouldBeFalse();
        decoded.Rgba.ShouldBe(baseline);
    }

    [Fact]
    public void AnUnrecognisedBlockIntroducerAfterTheImage_EndsTheParse_AndKeepsTheFrame()
    {
        var gif = Plain(trailer: false).Concat(new byte[] { 0x7E, 1, 2, 3 }).ToArray();
        var decoded = GifDecoder.Decode(gif);

        decoded.Info.FrameCount.ShouldBe(1);
        decoded.Info.HasTrailer.ShouldBeFalse();
        decoded.Info.UnparsedByteCount.ShouldBe(4);
        decoded.Rgba.ShouldBe(GifDecoder.Decode(Plain()).Rgba);
    }

    [Fact]
    public void AFileCutInsideTheLzwData_DecodesTheRowsItHas_AndSaysTruncated()
    {
        var whole = Transparent3();
        var expected = GifDecoder.Decode(whole);

        // Cut off the last 12 bytes: part of the data, the block terminator and the trailer.
        var cut = whole[..^12];
        var decoded = GifDecoder.Decode(cut);

        decoded.IsTruncated.ShouldBeTrue();
        decoded.Info.Frames[0].IsTruncated.ShouldBeTrue();
        decoded.Info.HasTrailer.ShouldBeFalse();

        // What was decoded is a prefix of the full decode; the rest is the canvas, which is alpha 0
        // here (the frame has a transparent index) with the background colour's RGB (entry 0, red).
        // Colours 0-2 only are drawn, so every drawn pixel is opaque.
        int painted = 0;
        for (int p = 0; p < 9 * 7; p++)
        {
            var got = decoded.Rgba.AsSpan(p * 4, 4);
            if (got[3] == 0)
            {
                got.ToArray().ShouldBe([255, 0, 0, 0]);
                continue;
            }
            got.SequenceEqual(expected.Rgba.AsSpan(p * 4, 4)).ShouldBeTrue($"pixel {p}");
            painted++;
        }
        painted.ShouldBeGreaterThan(0);
        painted.ShouldBeLessThan(9 * 7);
    }

    [Fact]
    public void AnLzwStreamThatEndsEarlyInsideCompleteBlocks_IsTruncatedToo()
    {
        var indices = Pattern(9, 7, 3);
        var lzw = GifBuilder.Lzw(indices[..20], 2); // 20 of 63 pixels, then EOI
        var gif = new GifBuilder().Header(9, 7, FourColours).GraphicControl(transparentIndex: 3).RawImage(0, 0, 9, 7, lzw, 2).Trailer().ToArray();

        var decoded = GifDecoder.Decode(gif);
        decoded.IsTruncated.ShouldBeTrue();
        decoded.Info.Frames[0].IsTruncated.ShouldBeFalse("the file is intact; the pixel stream is what is short");
        decoded.Info.HasTrailer.ShouldBeTrue();
        decoded.Rgba[(20 * 4 + 3)..].Where((_, i) => i % 4 == 0).ShouldAllBe(a => a == 0);
        decoded.Rgba.AsSpan(19 * 4 + 3, 1)[0].ShouldBe((byte)255);
    }

    [Fact]
    public void AShortFrameWithoutTransparency_LeavesTheRestAsTheOpaqueBackgroundColour()
    {
        var lzw = GifBuilder.Lzw(Pattern(9, 7, 3)[..20], 2);
        var gif = new GifBuilder().Header(9, 7, FourColours, background: 2).RawImage(0, 0, 9, 7, lzw, 2).Trailer().ToArray();

        var decoded = GifDecoder.Decode(gif);
        decoded.IsTruncated.ShouldBeTrue();
        decoded.Rgba.AsSpan(62 * 4, 4).ToArray().ShouldBe([0, 0, 255, 255]);
    }

    [Fact]
    public void AnInterlacedTruncatedFrame_LeavesTheUnreachedRowsTransparent()
    {
        // 12 rows of 4; only the first 5 file rows are present. In file order those land on rows 0, 8, 4, 2, 6.
        var indices = Pattern(4, 12, 3);
        var lzw = GifBuilder.Lzw(GifBuilder.Interlace(indices, 4, 12)[..(5 * 4)], 2);
        var gif = new GifBuilder().Header(4, 12, FourColours).GraphicControl(transparentIndex: 3)
            .Append(0x2C, 0, 0, 0, 0, 4, 0, 12, 0, 0x40, 2)
            .Append((byte)lzw.Length).Append(lzw).Append(0).Trailer().ToArray();

        var decoded = GifDecoder.Decode(gif);
        decoded.IsTruncated.ShouldBeTrue();
        var painted = Enumerable.Range(0, 12).Where(y => decoded.Rgba[(y * 4) * 4 + 3] == 255).ToArray();
        painted.ShouldBe([0, 2, 4, 6, 8]);
    }

    [Fact]
    public void MorePixelsThanTheFrameHolds_StopAtTheFrameSize_AndAreNotTruncation()
    {
        var indices = Pattern(9, 7, 4);
        var plain = GifDecoder.Decode(Plain());
        var extra = indices.Concat(Enumerable.Repeat((byte)3, 500)).ToArray();
        var gif = new GifBuilder().Header(9, 7, FourColours).Image(0, 0, 9, 7, extra, 2).Trailer().ToArray();

        var decoded = GifDecoder.Decode(gif);
        decoded.IsTruncated.ShouldBeFalse();
        decoded.Rgba.ShouldBe(plain.Rgba);
    }

    // ---- refusals -------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFrameOutsideTheScreen_IsRefused()
    {
        foreach (var (x, y, w, h) in new[] { (5, 0, 9, 7), (0, 3, 9, 7), (65535, 0, 65535, 1), (0, 65535, 1, 65535) })
        {
            var gif = new GifBuilder().Header(9, 7, FourColours).Image(x, y, w, h, [0, 1], 2).Trailer().ToArray();
            Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(gif), $"{w}x{h} at {x},{y}");
            Should.Throw<InvalidDataException>(() => GifDecoder.Decode(gif));
            GifDecoder.TryReadInfo(gif, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void AnEmptyFrameOrScreen_IsRefused()
    {
        var emptyFrame = new GifBuilder().Header(9, 7, FourColours).Image(0, 0, 0, 7, [], 2).Trailer().ToArray();
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(emptyFrame));

        var emptyScreen = new GifBuilder().Header(0, 7, FourColours).Image(0, 0, 1, 1, [0], 2).Trailer().ToArray();
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(emptyScreen));
    }

    [Fact]
    public void AHugeDeclaredScreen_IsRefusedBeforeAnythingIsAllocated()
    {
        var gif = new GifBuilder().Header(65535, 65535, FourColours).Image(0, 0, 65535, 65535, [0, 1], 2).Trailer().ToArray();

        // Warm the throw paths so the measurement sees steady-state allocation only.
        Should.Throw<InvalidDataException>(() => GifDecoder.Decode(gif));
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(gif));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Should.Throw<InvalidDataException>(() => GifDecoder.Decode(gif));
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(gif));
        GifDecoder.TryReadInfo(gif, out _).ShouldBeFalse();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A 65535x65535 RGBA raster is 16 GiB; the refusals cost the exceptions and nothing more.
        allocated.ShouldBeLessThan(1 << 20);
    }

    [Fact]
    public void TheScreenLimitIs2Pow26Pixels_ExactlyAtItPasses_OneRowOverIsRefused()
    {
        var atLimit = new GifBuilder().Header(8192, 8192, FourColours).Image(0, 0, 1, 1, [0], 2).Trailer().ToArray();
        GifDecoder.ReadInfo(atLimit).Width.ShouldBe(8192);

        var over = new GifBuilder().Header(8192, 8193, FourColours).Image(0, 0, 1, 1, [0], 2).Trailer().ToArray();
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(over));
    }

    [Fact]
    public void ALzwMinimumCodeSizeOutsideTwoToEight_IsRefused()
    {
        foreach (var root in new[] { 0, 1, 9, 12 })
        {
            var gif = new GifBuilder().Header(2, 2, FourColours).RawImage(0, 0, 2, 2, [0x44, 0x01], root).Trailer().ToArray();
            Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(gif), $"root {root}");
        }
    }

    [Fact]
    public void NoLocalAndNoGlobalColourTable_ReadsAsAnAllBlackPalette_AsMagickDoes()
    {
        var gif = new GifBuilder().Header(2, 2).Image(0, 0, 2, 2, [0, 1, 0, 1], 2).Trailer().ToArray();
        GifDecoder.ReadInfo(gif).Frames[0].ColorTableSize.ShouldBe(0);
        ShouldMatchMagick(gif);
        GifDecoder.Decode(gif).Rgba.ShouldBe([0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    public void AHeaderThatIsCutShort_IsRefused(int length)
    {
        var gif = Plain()[..length];
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(gif));
        GifDecoder.TryReadInfo(gif, out _).ShouldBeFalse();
    }

    [Fact]
    public void AGlobalTableCutShort_AndAFileWithNoImage_AreRefused()
    {
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(Plain()[..20]));
        var noImage = new GifBuilder().Header(2, 2, FourColours).Trailer().ToArray();
        Should.Throw<InvalidDataException>(() => GifDecoder.ReadInfo(noImage));
    }

    [Fact]
    public void DecodeTo_FillsTheCallersBuffer_AndRefusesOneThatIsTooSmall()
    {
        var gif = Plain();
        var expected = GifDecoder.Decode(gif);

        var dst = new byte[9 * 7 * 4 + 16];
        Array.Fill(dst, (byte)0xEE);
        var result = GifDecoder.DecodeTo(gif, dst);
        result.IsTruncated.ShouldBeFalse();
        dst.AsSpan(0, 9 * 7 * 4).SequenceEqual(expected.Rgba).ShouldBeTrue();
        dst.AsSpan(9 * 7 * 4).ToArray().ShouldAllBe(b => b == 0xEE, "only the width*height*4 prefix is written");

        Should.Throw<ArgumentException>(() => GifDecoder.DecodeTo(gif, new byte[9 * 7 * 4 - 1]));
    }
}
