using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="PngReader"/> reads the APNG chunks (<c>acTL</c>, <c>fcTL</c>, <c>fdAT</c>) and validates them. The
/// fixtures under <c>Fixtures/apng</c> are written by Pillow, not by this repo's code, and
/// <c>apng-reference.tsv</c> holds what the generator script recorded from the bytes of each file (the <c>acTL</c> and
/// <c>fcTL</c> fields) and from Pillow's own decode (the composited canvases, and the default image alone). These tests
/// pin: the parsed animation against that record; that the <c>IDAT</c> image decodes exactly as it did before the
/// animation chunks were read and as an APNG-unaware decoder shows it; that each frame's own sub-image inflates to its
/// size and (for the frames simple enough to place without a compositor) to the pixels Pillow shows; and that a
/// malformed animation is refused, each fault built by changing one thing in a good file and recomputing its CRCs, so
/// the check that fires is the one named.
/// </summary>
public sealed class PngApngTests
{
    private const string Base = "apng-dispose-none-blend-source.png"; // IDAT is frame 0; 4 frames at fixed rectangles
    private const string DefaultImageFile = "apng-default-image.png";  // IDAT is a cover outside the animation

    // ---------------------------------------------------------------- the generator's record

    private sealed record Row(
        string File, int Frame, int NumFrames, uint NumPlays, bool IdatIsFrame0, int CanvasW, int CanvasH, int Sequence,
        int X, int Y, int W, int H, ushort DelayNum, ushort DelayDen, int Dispose, int Blend, string CanvasSha, string IdatSha);

    private static readonly Lazy<IReadOnlyList<Row>> AllRows = new(() =>
    {
        var rows = new List<Row>();
        var header = true;
        foreach (var line in File.ReadAllLines(Path.Combine(FixtureDir, "apng-reference.tsv")))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (header) // the column names
            {
                header = false;
                continue;
            }
            var f = line.Split('\t');
            rows.Add(new Row(f[0], int.Parse(f[1]), int.Parse(f[2]), uint.Parse(f[3]), f[4] == "1", int.Parse(f[5]),
                int.Parse(f[6]), int.Parse(f[7]), int.Parse(f[8]), int.Parse(f[9]), int.Parse(f[10]), int.Parse(f[11]),
                ushort.Parse(f[12]), ushort.Parse(f[13]), int.Parse(f[14]), int.Parse(f[15]), f[16], f[17]));
        }
        return rows;
    });

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "apng");

    private static List<Row> RowsFor(string file) => [.. AllRows.Value.Where(r => r.File == file)];

    private static byte[] Bytes(string file) => File.ReadAllBytes(Path.Combine(FixtureDir, file));

    public static TheoryData<string> FixtureNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in AllRows.Value.Select(r => r.File).Distinct())
                data.Add(file);
            return data;
        }
    }

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---------------------------------------------------------------- the parsed animation

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Animation_MatchesWhatTheGeneratorRecorded(string file)
    {
        var rows = RowsFor(file);
        var img = PngReader.Decode(Bytes(file));

        var animation = img.Animation.ShouldNotBeNull();
        img.IsAnimated.ShouldBeTrue();
        img.Width.ShouldBe(rows[0].CanvasW);
        img.Height.ShouldBe(rows[0].CanvasH);
        animation.FrameCount.ShouldBe(rows[0].NumFrames);
        animation.PlayCount.ShouldBe(rows[0].NumPlays);
        animation.DefaultImageIsFirstFrame.ShouldBe(rows[0].IdatIsFrame0);
        animation.Frames.Count.ShouldBe(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var frame = animation.Frames[i];
            var row = rows[i];
            var what = $"{file} frame {i}";
            frame.SequenceNumber.ShouldBe(row.Sequence, what);
            (frame.XOffset, frame.YOffset, frame.Width, frame.Height).ShouldBe((row.X, row.Y, row.W, row.H), what);
            (frame.DelayNumerator, frame.DelayDenominator).ShouldBe((row.DelayNum, row.DelayDen), what);
            ((int)frame.DisposeOp).ShouldBe(row.Dispose, what);
            ((int)frame.BlendOp).ShouldBe(row.Blend, what);
        }
    }

    [Fact]
    public void Fixtures_CoverAllSixDisposeBlendPairs_AndTheUnusualFiles()
    {
        // every pair appears, on a frame that is not the first, so it is applied to something
        var pairs = AllRows.Value.Where(r => r.File.StartsWith("apng-dispose-") && r.Frame > 0)
            .Select(r => (r.Dispose, r.Blend)).Distinct().Order().ToArray();
        pairs.ShouldBe(new[] { (0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (2, 1) });

        // frames that sit away from the origin, so offsets matter
        AllRows.Value.Count(r => r.X != 0 && r.Y != 0).ShouldBeGreaterThan(20);
        // a file whose IDAT is not part of the animation
        AllRows.Value.Select(r => r.IdatIsFrame0).Distinct().Count().ShouldBe(2);
        // an RGB file, a palette file, a gray + alpha one and RGBA ones
        var colorTypes = AllRows.Value.Select(r => r.File).Distinct()
            .Select(f => PngReader.Decode(Bytes(f)).ColorType).Distinct().Order().ToArray();
        colorTypes.ShouldBe(new[] { 2, 3, 4, 6 });
    }

    [Fact]
    public void DefaultImageFile_ReportsTheIdatAsNotPartOfTheAnimation()
    {
        var img = PngReader.Decode(Bytes(DefaultImageFile));
        img.Animation!.DefaultImageIsFirstFrame.ShouldBeFalse();
        img.Animation.FrameCount.ShouldBe(4);
        // the first frame's data is in fdAT, so it is not what Pixels holds
        var frame0 = PngReader.DecodeFrameSamples(Bytes(DefaultImageFile), img, 0);
        frame0.Length.ShouldBe(img.Pixels.Length);
        frame0.ShouldNotBe(img.Pixels);
    }

    [Fact]
    public void SingleFrameAnimation_HasAnAnimation_ButIsNotAnimated()
    {
        var chunks = Parse(Bytes(Base));
        BinaryPrimitives.WriteUInt32BigEndian(Nth(chunks, "acTL", 0).Data, 1);
        chunks.RemoveAll(c => c.Type == "fdAT");
        var laterFrames = new[] { Nth(chunks, "fcTL", 1), Nth(chunks, "fcTL", 2), Nth(chunks, "fcTL", 3) };
        chunks.RemoveAll(c => laterFrames.Contains(c));
        Renumber(chunks);

        var img = PngReader.Decode(Build(chunks));

        img.Animation.ShouldNotBeNull().FrameCount.ShouldBe(1);
        img.Animation.Frames.Count.ShouldBe(1);
        img.IsAnimated.ShouldBeFalse();
    }

    [Fact]
    public void ZeroDelayDenominator_MeansHundredths()
    {
        var chunks = Parse(Bytes(Base));
        var fcTl = Nth(chunks, "fcTL", 1).Data;
        BinaryPrimitives.WriteUInt16BigEndian(fcTl.AsSpan(20), 5);
        BinaryPrimitives.WriteUInt16BigEndian(fcTl.AsSpan(22), 0);

        var animation = PngReader.Decode(Build(chunks)).Animation!;

        animation.Frames[0].DelaySeconds.ShouldBe(0.1, 1e-12);   // 1/10 as written
        animation.Frames[1].DelaySeconds.ShouldBe(0.05, 1e-12);  // 5/0 -> 5/100
    }

    // ---------------------------------------------------------------- still images, and the IDAT image

    [Fact]
    public void StillPng_ReportsNotAnimated_AndDecodesAsBefore()
    {
        var rgb = new byte[7 * 5 * 3];
        new Random(11).NextBytes(rgb);

        var img = PngReader.Decode(PngWriter.EncodeRgb8(rgb, 7, 5));

        img.Animation.ShouldBeNull();
        img.IsAnimated.ShouldBeFalse();
        img.Pixels.ShouldBe(rgb);
    }

    /// <summary>
    /// The default image is the IDAT image whatever the animation chunks say. Two checks: it is identical to what the
    /// same file decodes to with its animation chunks removed (so reading them changed nothing), and it is the image
    /// Pillow decodes from that stripped file (so it is right, not just stable).
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Decode_ReturnsTheIdatImageExactlyAsBefore(string file)
    {
        var apng = Bytes(file);
        var stripped = Build(Parse(apng).Where(c => c.Type is not ("acTL" or "fcTL" or "fdAT")).ToList());

        var withAnimation = PngReader.Decode(apng);
        var still = PngReader.Decode(stripped);

        still.Animation.ShouldBeNull();
        withAnimation.Width.ShouldBe(still.Width);
        withAnimation.Height.ShouldBe(still.Height);
        withAnimation.BitDepth.ShouldBe(still.BitDepth);
        withAnimation.SourceBitDepth.ShouldBe(still.SourceBitDepth);
        withAnimation.ColorType.ShouldBe(still.ColorType);
        withAnimation.Pixels.ShouldBe(still.Pixels);
        withAnimation.Palette.ShouldBe(still.Palette);
        withAnimation.PaletteAlpha.ShouldBe(still.PaletteAlpha);
        Sha(withAnimation.ToRgba8()).ShouldBe(RowsFor(file)[0].IdatSha);
    }

    /// <summary>fcTL and fdAT with no acTL in front of the IDAT do not declare an animation: skipped, as before.</summary>
    [Theory]
    [InlineData(Base)]
    [InlineData(DefaultImageFile)]
    public void FrameChunksWithoutAnActl_AreSkippedAsUnknownAncillaryChunks(string file)
    {
        var apng = Bytes(file);
        var withoutActl = Build(Parse(apng).Where(c => c.Type != "acTL").ToList());

        var img = PngReader.Decode(withoutActl);

        img.Animation.ShouldBeNull();
        img.IsAnimated.ShouldBeFalse();
        img.Pixels.ShouldBe(PngReader.Decode(apng).Pixels);
    }

    // ---------------------------------------------------------------- each frame's own sub-image

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void FrameSamples_HaveTheSizeOfTheFrame(string file)
    {
        var bytes = Bytes(file);
        var img = PngReader.Decode(bytes);
        var bytesPerPixel = img.SamplesPerPixel * img.BitDepth / 8; // sub-byte samples come back one byte each

        for (var i = 0; i < img.Animation!.Frames.Count; i++)
        {
            var frame = img.Animation.Frames[i];
            PngReader.DecodeFrameSamples(bytes, img, i).Length.ShouldBe(frame.Width * frame.Height * bytesPerPixel, $"{file} frame {i}");
        }
    }

    /// <summary>The IDAT image is frame 0's data exactly when the file says so: the samples of that frame are <c>Pixels</c>; when the IDAT is a separate default image they are not.</summary>
    [Fact]
    public void FirstFrameSamples_ArePixels_ExactlyWhenTheIdatIsTheFirstFrame()
    {
        var sawBoth = new HashSet<bool>();
        foreach (var file in AllRows.Value.Select(r => r.File).Distinct())
        {
            var bytes = Bytes(file);
            var img = PngReader.Decode(bytes);
            var frame0 = PngReader.DecodeFrameSamples(bytes, img, 0);
            if (img.Animation!.DefaultImageIsFirstFrame)
                frame0.ShouldBe(img.Pixels, file);
            else
                frame0.ShouldNotBe(img.Pixels, file);
            sawBoth.Add(img.Animation.DefaultImageIsFirstFrame);
        }
        sawBoth.Count.ShouldBe(2);
    }

    /// <summary>
    /// With dispose_op none and blend_op source every frame simply replaces its rectangle, which can be placed with a
    /// loop here rather than a compositor. So these two files check the frames' pixels, fdAT inflate and unfilter
    /// included, against Pillow's own canvases, not just their sizes.
    /// </summary>
    [Theory]
    [InlineData(Base)]
    [InlineData("apng-rgb8.png")]
    public void PastingTheFramesWhereTheyBelong_ReproducesPillowsCanvases(string file)
    {
        var bytes = Bytes(file);
        var img = PngReader.Decode(bytes);
        var rows = RowsFor(file);
        var spp = img.SamplesPerPixel;
        spp.ShouldBeOneOf(3, 4);
        var canvas = new byte[img.Width * img.Height * 4];

        for (var i = 0; i < rows.Count; i++)
        {
            var frame = img.Animation!.Frames[i];
            ((int)frame.DisposeOp, (int)frame.BlendOp).ShouldBe((0, 0));
            var samples = PngReader.DecodeFrameSamples(bytes, img, i);
            for (var y = 0; y < frame.Height; y++)
                for (var x = 0; x < frame.Width; x++)
                {
                    var from = (y * frame.Width + x) * spp;
                    var to = ((frame.YOffset + y) * img.Width + frame.XOffset + x) * 4;
                    canvas[to] = samples[from];
                    canvas[to + 1] = samples[from + 1];
                    canvas[to + 2] = samples[from + 2];
                    canvas[to + 3] = spp == 4 ? samples[from + 3] : (byte)255;
                }
            Sha(canvas).ShouldBe(rows[i].CanvasSha, $"{file} after frame {i}");
        }
    }

    /// <summary>The indexed file's frames are 23 and 25 pixels wide at 4 bits: rows padded to whole bytes, unpacked to one byte each.</summary>
    [Fact]
    public void IndexedFrames_AreUnpackedFromPaddedFourBitRows()
    {
        const string file = "apng-indexed-trns.png";
        var bytes = Bytes(file);
        var img = PngReader.Decode(bytes);
        img.SourceBitDepth.ShouldBe(4);

        // the generator draws each patch with a one-pixel ring of palette index 4 (frame 1) or 6 (frame 2)
        foreach (var (index, ring) in new[] { (1, (byte)4), (2, (byte)6) })
        {
            var frame = img.Animation!.Frames[index];
            (frame.Width % 2).ShouldBe(1);
            var samples = PngReader.DecodeFrameSamples(bytes, img, index);
            samples.ShouldAllBe(s => s < 8);
            for (var x = 0; x < frame.Width; x++)
            {
                samples[x].ShouldBe(ring);                                      // top row
                samples[(frame.Height - 1) * frame.Width + x].ShouldBe(ring);   // bottom row
            }
            for (var y = 0; y < frame.Height; y++)
            {
                samples[y * frame.Width].ShouldBe(ring);                        // left column
                samples[y * frame.Width + frame.Width - 1].ShouldBe(ring);      // right column
            }
        }
    }

    [Fact]
    public void FrameDataSplitAcrossManyFdAts_DecodesTheSame()
    {
        var original = Bytes(Base);
        var whole = PngReader.Decode(original);
        var expected = Enumerable.Range(0, 4).Select(i => PngReader.DecodeFrameSamples(original, whole, i)).ToList();
        var chunks = Parse(original);
        var at = chunks.IndexOf(Nth(chunks, "fdAT", 1)); // frame 2's data
        var payload = chunks[at].Data[4..];
        chunks.RemoveAt(at);
        var pieces = new List<Chunk>();
        for (var o = 0; o < payload.Length; o += 5)
            pieces.Add(FdAt(payload[o..Math.Min(o + 5, payload.Length)]));
        pieces.Count.ShouldBeGreaterThan(3);
        chunks.InsertRange(at, pieces);
        Renumber(chunks);

        var split = Build(chunks);
        var img = PngReader.Decode(split);

        for (var i = 0; i < 4; i++)
            PngReader.DecodeFrameSamples(split, img, i).ShouldBe(expected[i], $"frame {i}");
    }

    [Fact]
    public void DecodeFrameSamples_RefusesWhatIsNotAFrame()
    {
        var bytes = Bytes(Base);
        var img = PngReader.Decode(bytes);

        Should.Throw<ArgumentOutOfRangeException>(() => PngReader.DecodeFrameSamples(bytes, img, 4));
        Should.Throw<ArgumentOutOfRangeException>(() => PngReader.DecodeFrameSamples(bytes, img, -1));
        Should.Throw<ArgumentException>(() => PngReader.DecodeFrameSamples(bytes.AsSpan(0, bytes.Length / 2), img, 3));
        var still = PngReader.Decode(PngWriter.EncodeRgb8(new byte[3 * 3 * 3], 3, 3));
        Should.Throw<InvalidOperationException>(() => PngReader.DecodeFrameSamples(bytes, still, 0));
    }

    [Fact]
    public void AFramesCorruptData_FailsWhenThatFrameIsInflated_NotWhenTheFileIsParsed()
    {
        var chunks = Parse(Bytes(Base));
        var data = Nth(chunks, "fdAT", 0).Data;
        Array.Resize(ref data, data.Length / 2); // the zlib stream of frame 1 now stops half way
        chunks[chunks.IndexOf(Nth(chunks, "fdAT", 0))] = new Chunk("fdAT", data);
        var bytes = Build(chunks);

        var img = PngReader.Decode(bytes); // the animation's structure is fine

        Should.Throw<InvalidDataException>(() => PngReader.DecodeFrameSamples(bytes, img, 1));
        PngReader.DecodeFrameSamples(bytes, img, 2).Length.ShouldBe(img.Animation!.Frames[2].Width * img.Animation.Frames[2].Height * 4);
    }

    // ---------------------------------------------------------------- malformed animations

    private static void SetFcTl(List<Chunk> chunks, int frame, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(Nth(chunks, "fcTL", frame).Data.AsSpan(offset), value);

    private static Chunk FdAt(params byte[] zlibBytes) => new("fdAT", [0, 0, 0, 0, .. zlibBytes]);

    /// <summary>
    /// One fault each, made in <see cref="Base"/> (chunk order IHDR acTL fcTL IDAT fcTL fdAT fcTL fdAT fcTL fdAT IEND;
    /// sequence numbers fcTL 0, 1, 3, 5 and fdAT 2, 4, 6). The expected message says which check must be the one that
    /// fires. Edits that add, remove or move a frame renumber afterwards, so the sequence check is not what trips.
    /// </summary>
    private static readonly Dictionary<string, (Action<List<Chunk>> Edit, string Message)> Faults = new()
    {
        // sequence numbers across fcTL and fdAT
        ["sequence-repeated"] = (c => BinaryPrimitives.WriteUInt32BigEndian(Nth(c, "fdAT", 0).Data, 1), "sequence number"),
        ["sequence-decreasing"] = (c => SetFcTl(c, 2, 0, 0), "sequence number"),
        ["sequence-swapped"] = (c => { SetFcTl(c, 2, 0, 4); BinaryPrimitives.WriteUInt32BigEndian(Nth(c, "fdAT", 1).Data, 3); }, "sequence number"),
        ["sequence-gap"] = (c => SetFcTl(c, 3, 0, 6), "sequence number"),
        ["sequence-first-not-zero"] = (c => SetFcTl(c, 0, 0, 1), "sequence number"),
        ["sequence-fdat-too-high"] = (c => BinaryPrimitives.WriteUInt32BigEndian(Nth(c, "fdAT", 2).Data, 99), "sequence number"),

        // a frame rectangle outside the canvas, or empty
        ["rect-x-past-canvas"] = (c => SetFcTl(c, 1, 12, 40), "canvas"),
        ["rect-y-past-canvas"] = (c => SetFcTl(c, 1, 16, 30), "canvas"),
        ["rect-wider-than-canvas"] = (c => SetFcTl(c, 1, 4, 49), "canvas"),
        ["rect-taller-than-canvas"] = (c => SetFcTl(c, 1, 8, 33), "canvas"),
        ["rect-offset-wraps-an-int"] = (c => SetFcTl(c, 1, 12, 0xFFFFFFF0), "canvas"),
        ["rect-width-wraps-an-int"] = (c => SetFcTl(c, 1, 4, 0xFFFFFFFF), "canvas"),
        ["rect-zero-width"] = (c => SetFcTl(c, 1, 4, 0), "is empty"),
        ["rect-zero-height"] = (c => SetFcTl(c, 1, 8, 0), "is empty"),

        // the fcTL count and acTL's num_frames
        ["count-more-declared-than-fctl"] = (c => BinaryPrimitives.WriteUInt32BigEndian(Nth(c, "acTL", 0).Data, 5), "frames but the file has"),
        ["count-fewer-declared-than-fctl"] = (c => BinaryPrimitives.WriteUInt32BigEndian(Nth(c, "acTL", 0).Data, 3), "more fcTL chunks"),
        ["count-last-frame-removed"] = (c => { var last = new[] { Nth(c, "fcTL", 3), Nth(c, "fdAT", 2) }; c.RemoveAll(x => last.Contains(x)); }, "frames but the file has"),
        ["count-zero"] = (c => BinaryPrimitives.WriteUInt32BigEndian(Nth(c, "acTL", 0).Data, 0), "frame count"),
        ["count-no-fctl-at-all"] = (c => { c.RemoveAll(x => x.Type is "fcTL" or "fdAT"); }, "frames but the file has"),

        // operations the specification does not define
        ["dispose-op-3"] = (c => Nth(c, "fcTL", 1).Data[24] = 3, "dispose_op"),
        ["blend-op-2"] = (c => Nth(c, "fcTL", 1).Data[25] = 2, "blend_op"),

        // acTL itself
        ["actl-twice"] = (c => { c.Insert(2, Nth(c, "acTL", 0) with { }); }, "Multiple acTL"),
        ["actl-after-idat"] = (c => { var a = Nth(c, "acTL", 0); c.Remove(a); c.Insert(c.IndexOf(Nth(c, "IDAT", 0)) + 1, a); }, "follows the first IDAT"),
        ["actl-wrong-length"] = (c => { var i = c.IndexOf(Nth(c, "acTL", 0)); c[i] = new Chunk("acTL", c[i].Data[..7]); }, "acTL length"),

        // the first frame is the IDAT image, so it must be the canvas
        ["first-frame-narrower-than-canvas"] = (c => SetFcTl(c, 0, 4, 47), "whole canvas"),
        ["first-frame-offset"] = (c => { SetFcTl(c, 0, 12, 1); SetFcTl(c, 0, 4, 47); }, "whole canvas"),

        // chunks in the wrong place
        ["fcTL-wrong-length"] = (c => { var i = c.IndexOf(Nth(c, "fcTL", 1)); c[i] = new Chunk("fcTL", c[i].Data[..25]); }, "fcTL length"),
        ["fdAT-too-short"] = (c => { var i = c.IndexOf(Nth(c, "fdAT", 0)); c[i] = new Chunk("fdAT", c[i].Data[..3]); }, "fdAT length"),
        ["fdAT-before-any-fctl"] = (c => { c.Insert(2, FdAt(1, 2, 3)); Renumber(c); }, "before any fcTL"),
        ["fdAT-after-the-idat-frame"] = (c => { c.Insert(c.IndexOf(Nth(c, "IDAT", 0)) + 1, FdAt(1, 2, 3)); Renumber(c); }, "whose data is the IDAT"),
        ["idat-after-a-later-fctl"] = (c => c.Insert(c.IndexOf(Nth(c, "fcTL", 1)) + 1, Nth(c, "IDAT", 0) with { }), "follows the fcTL"),
        ["frame-with-no-data"] = (c => { c.Remove(Nth(c, "fdAT", 1)); Renumber(c); }, "no image data"),
        ["last-frame-with-no-data"] = (c => { c.Remove(Nth(c, "fdAT", 2)); Renumber(c); }, "no image data"),
    };

    public static TheoryData<string> FaultNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Faults.Keys)
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FaultNames))]
    public void MalformedAnimation_IsRefused(string fault)
    {
        var (edit, message) = Faults[fault];
        var chunks = Parse(Bytes(Base));
        edit(chunks);
        var bytes = Build(chunks);

        var thrown = Should.Throw<InvalidDataException>(() => PngReader.Decode(bytes), fault);
        thrown.Message.Contains(message).ShouldBeTrue($"{fault}: expected \"{message}\" in \"{thrown.Message}\"");
        PngReader.TryDecode(bytes, out var image).ShouldBeFalse(fault);
        image.ShouldBeNull();
    }

    /// <summary>The control: rebuilding the good file the same way, with nothing changed, decodes. So each fault above is the fault.</summary>
    [Fact]
    public void TheUnchangedFixture_RebuiltWithRecomputedCrcs_Decodes()
    {
        var original = Bytes(Base);
        var rebuilt = Build(Parse(original));

        rebuilt.ShouldBe(original);
        PngReader.TryDecode(rebuilt, out var image).ShouldBeTrue();
        image!.Animation!.FrameCount.ShouldBe(4);
    }

    /// <summary>The default-image file has its own faults: an IDAT after the first fcTL is data that belongs to no frame.</summary>
    [Fact]
    public void DefaultImageFile_WithAnIdatAfterItsFirstFctl_IsRefused()
    {
        var chunks = Parse(Bytes(DefaultImageFile));
        chunks.Insert(chunks.IndexOf(Nth(chunks, "fcTL", 0)) + 1, Nth(chunks, "IDAT", 0) with { });

        var thrown = Should.Throw<InvalidDataException>(() => PngReader.Decode(Build(chunks)));

        thrown.Message.Contains("follows the fcTL").ShouldBeTrue(thrown.Message);
    }

    // ---------------------------------------------------------------- a PNG chunk toolkit for the mutations

    private sealed record Chunk(string Type, byte[] Data);

    private static List<Chunk> Parse(byte[] png)
    {
        var chunks = new List<Chunk>();
        for (var pos = 8; pos < png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            chunks.Add(new Chunk(Encoding.ASCII.GetString(png, pos + 4, 4), png.AsSpan(pos + 8, length).ToArray()));
            pos += 12 + length;
        }
        return chunks;
    }

    private static Chunk Nth(List<Chunk> chunks, string type, int n) => chunks.Where(c => c.Type == type).ElementAt(n);

    /// <summary>Gives every fcTL and fdAT the next number in file order again, after an edit that added, removed or moved one.</summary>
    private static void Renumber(List<Chunk> chunks)
    {
        uint next = 0;
        foreach (var chunk in chunks.Where(c => c.Type is "fcTL" or "fdAT"))
            BinaryPrimitives.WriteUInt32BigEndian(chunk.Data, next++);
    }

    private static byte[] Build(IEnumerable<Chunk> chunks)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> word = stackalloc byte[4];
        foreach (var chunk in chunks)
        {
            var type = Encoding.ASCII.GetBytes(chunk.Type);
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)chunk.Data.Length);
            ms.Write(word);
            ms.Write(type);
            ms.Write(chunk.Data);
            BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(type, chunk.Data));
            ms.Write(word);
        }
        return ms.ToArray();
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
