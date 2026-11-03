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

    /// <summary>An acTL after the first IDAT does not make the stream an APNG: a plain PNG with an unknown ancillary chunk, decoded as before.</summary>
    [Theory]
    [InlineData(Base)]
    [InlineData(DefaultImageFile)]
    public void AnActlAfterTheIdat_IsSkippedAsAnUnknownAncillaryChunk(string file)
    {
        var apng = Bytes(file);
        var chunks = Parse(apng).ToList();
        var actl = Nth(chunks, "acTL", 0);
        chunks.Remove(actl);
        chunks.Insert(chunks.IndexOf(Nth(chunks, "IDAT", 0)) + 1, actl);
        var late = Build(chunks);
        var stripped = Build(Parse(apng).Where(c => c.Type != "acTL").ToList());

        var img = PngReader.Decode(late);

        img.Animation.ShouldBeNull();
        img.IsAnimated.ShouldBeFalse();
        img.Pixels.ShouldBe(PngReader.Decode(stripped).Pixels);
        PngReader.TryDecode(late, out _).ShouldBeTrue();
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

    // ---------------------------------------------------------------- compositing

    private const string PartialAlphaFile = "apng-blend-over-partial-alpha.png";

    /// <summary>
    /// Every frame of every fixture, composited by <see cref="PngReader.EnumerateFrames"/>, against the SHA-256 of the
    /// canvas Pillow composited. The partial-alpha OVER file is the one place Pillow's rounding may differ from the
    /// specification's formula; it has its own test below, which measures the difference.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void EveryFrame_MatchesPillowsCanvas(string file)
    {
        var rows = RowsFor(file);
        var frames = PngReader.EnumerateFrames(Bytes(file)).Select(f => (f.Index, Sha: Sha(f.Rgba), f.DelayNumerator, f.DelayDenominator)).ToList();

        frames.Count.ShouldBe(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            frames[i].Index.ShouldBe(i);
            (frames[i].DelayNumerator, frames[i].DelayDenominator).ShouldBe((rows[i].DelayNum, rows[i].DelayDen), $"{file} frame {i}");
            if (file == PartialAlphaFile)
                continue;
            frames[i].Sha.ShouldBe(rows[i].CanvasSha, $"{file} frame {i}");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void DecodeFrames_GivesTheSameCanvasesAsTheStreamingEnumerator_EachItsOwn(string file)
    {
        var bytes = Bytes(file);
        var streamed = PngReader.EnumerateFrames(bytes).Select(f => (byte[])f.Rgba.Clone()).ToList();

        var sequence = PngReader.DecodeFrames(bytes);

        var img = PngReader.Decode(bytes);
        (sequence.CanvasWidth, sequence.CanvasHeight, sequence.PlayCount).ShouldBe((img.Width, img.Height, img.Animation!.PlayCount));
        sequence.Frames.Count.ShouldBe(streamed.Count);
        for (var i = 0; i < streamed.Count; i++)
        {
            sequence.Frames[i].Rgba.ShouldBe(streamed[i], $"{file} frame {i}");
            sequence.Frames[i].Index.ShouldBe(i);
            sequence.Frames[i].DelayNumerator.ShouldBe(img.Animation.Frames[i].DelayNumerator);
        }
        sequence.Frames.Select(f => f.Rgba).Distinct().Count().ShouldBe(sequence.Frames.Count); // no two share a buffer
    }

    /// <summary>The enumerator keeps one working canvas and says so: every yielded frame is that array, rewritten in place.</summary>
    [Fact]
    public void EnumerateFrames_ReusesOneCanvas()
    {
        var canvases = PngReader.EnumerateFrames(Bytes(Base)).Select(f => f.Rgba).ToList();

        canvases.Count.ShouldBe(4);
        canvases.Distinct().Count().ShouldBe(1);
        // a second enumeration starts again from a transparent canvas, with its own working canvas
        var second = PngReader.EnumerateFrames(Bytes(Base)).First().Rgba;
        second.ShouldNotBeSameAs(canvases[0]);
    }

    [Fact]
    public void DefaultImageFile_YieldsOnlyTheAnimationFrames_NotTheIdat()
    {
        var rows = RowsFor(DefaultImageFile);
        var bytes = Bytes(DefaultImageFile);

        var frames = PngReader.DecodeFrames(bytes).Frames;

        frames.Count.ShouldBe(4);                       // the cover is not one of them
        PngReader.Decode(bytes).Animation!.FrameCount.ShouldBe(4);
        Sha(frames[0].Rgba).ShouldBe(rows[0].CanvasSha);
        Sha(frames[0].Rgba).ShouldNotBe(rows[0].IdatSha);
        frames.Any(f => Sha(f.Rgba) == rows[0].IdatSha).ShouldBeFalse();
        // and the still-image paths still show the cover
        Sha(PngReader.Decode(bytes).ToRgba8()).ShouldBe(rows[0].IdatSha);
        PngImageDecoder.TryDecodeRgba8(bytes, out var rgba, out _, out _).ShouldBeTrue();
        Sha(rgba!).ShouldBe(rows[0].IdatSha);
    }

    [Fact]
    public void ZeroDelayDenominator_ReadsAsHundred_OnTheYieldedFrames()
    {
        var chunks = Parse(Bytes(Base));
        var fcTl = Nth(chunks, "fcTL", 1).Data;
        BinaryPrimitives.WriteUInt16BigEndian(fcTl.AsSpan(20), 5);
        BinaryPrimitives.WriteUInt16BigEndian(fcTl.AsSpan(22), 0);

        var frames = PngReader.DecodeFrames(Build(chunks)).Frames;

        (frames[1].DelayNumerator, frames[1].DelayDenominator).ShouldBe(((ushort)5, (ushort)0)); // as stored
        frames[1].DelaySeconds.ShouldBe(0.05, 1e-12);
        frames[0].DelaySeconds.ShouldBe(0.1, 1e-12);
    }

    /// <summary>
    /// The specification: a first frame whose dispose_op is PREVIOUS is treated as BACKGROUND. With frame 0 clearing its
    /// rectangle after itself, what shows outside frame 1's rectangle is transparent; with NONE it is frame 0's pixels.
    /// </summary>
    [Fact]
    public void FirstFrameDisposePrevious_IsTreatedAsBackground()
    {
        byte[] SecondCanvas(PngDisposeOp op)
        {
            var chunks = Parse(Bytes(Base));
            Nth(chunks, "fcTL", 0).Data[24] = (byte)op;
            return (byte[])PngReader.DecodeFrames(Build(chunks)).Frames[1].Rgba.Clone();
        }

        var previous = SecondCanvas(PngDisposeOp.Previous);
        previous.ShouldBe(SecondCanvas(PngDisposeOp.Background));
        previous.ShouldNotBe(SecondCanvas(PngDisposeOp.None));
        previous.Count(b => b != 0).ShouldBeLessThan(1500 * 4); // only frame 1's 24x14 patch is left
    }

    [Fact]
    public void StillPng_YieldsItsOneFrame()
    {
        var rgb = new byte[7 * 5 * 3];
        new Random(11).NextBytes(rgb);
        var png = PngWriter.EncodeRgb8(rgb, 7, 5);

        var sequence = PngReader.DecodeFrames(png);

        sequence.Frames.Count.ShouldBe(1);
        sequence.Frames[0].Rgba.ShouldBe(PngReader.Decode(png).ToRgba8());
        PngReader.EnumerateFrames(png).Count().ShouldBe(1);
    }

    /// <summary>
    /// The specification's OVER, transcribed as plainly as it is written (floating point, fractions of 1), against the
    /// integer one in the reader, over every pair of alphas and a spread of colours. The reader rounds the exact
    /// rational to nearest, halves up, and the alpha is exact; so a double can only disagree on a colour channel whose
    /// exact value is a half, where it lands a hair either side. Every disagreement is checked to be exactly that: one
    /// unit, on an exact half.
    /// </summary>
    [Fact]
    public void Over_MatchesTheSpecificationsFormula_ForEveryPairOfAlphas()
    {
        var colors = new[] { 0, 1, 17, 100, 128, 200, 254, 255 };
        var checkedPixels = 0;
        var ties = 0;
        var target = new byte[4];
        for (var af = 0; af < 256; af++)
            for (var ab = 0; ab < 256; ab++)
                foreach (var cf in colors)
                    foreach (var cb in colors)
                    {
                        byte[] source = [(byte)cf, (byte)(255 - cf), (byte)((cf + cb) / 2), (byte)af];
                        byte[] background = [(byte)cb, (byte)(255 - cb), (byte)((cf * 3 + cb) / 4), (byte)ab];
                        background.CopyTo(target, 0);
                        PngCompositor.Over(source, target);
                        checkedPixels++;

                        var expected = SpecOver(source, background);
                        target[3].ShouldBe(expected[3], $"alpha of {af} over {ab}");
                        for (var c = 0; c < 3; c++)
                        {
                            if (target[c] == expected[c])
                                continue;
                            // the exact value is (af*255*cf + (255-af)*ab*cb) / (af*255 + ab*(255-af)); a tie is an odd multiple of 1/2
                            long numerator = af * 255L * source[c] + (255 - af) * (long)ab * background[c];
                            long denominator = af * 255L + ab * (255L - af);
                            (2 * numerator % denominator).ShouldBe(0, $"channel {c}, alpha {af} over {ab}: not a tie");
                            (2 * numerator / denominator % 2).ShouldBe(1);
                            Math.Abs(target[c] - expected[c]).ShouldBe(1);
                            ties++;
                        }
                    }
        checkedPixels.ShouldBe(256 * 256 * 64);
        ties.ShouldBeLessThan(checkedPixels / 1000); // a rounding detail, not a different formula
    }

    private static byte[] SpecOver(byte[] fg, byte[] bg)
    {
        double afr = fg[3] / 255.0, abr = bg[3] / 255.0;
        var outAlpha = afr + abr * (1 - afr);
        var result = new byte[4];
        for (var c = 0; c < 3; c++)
            result[c] = outAlpha == 0 ? (byte)0 : (byte)Math.Floor((afr * fg[c] + (1 - afr) * abr * bg[c]) / outAlpha + 0.5);
        result[3] = (byte)Math.Floor(outAlpha * 255 + 0.5);
        return result;
    }

    /// <summary>
    /// What Pillow's APNG loader does for blend_op OVER, found by measurement: not the specification's alpha-over but
    /// <c>Image.paste</c> with the frame as its own mask, a lerp of <em>every</em> band, alpha included, by the frame's
    /// alpha (<c>DIV255(bg * (255 - a) + fg * a)</c>), applied to every frame but the first (which is shown as it is).
    /// Where the canvas is opaque it is the spec's formula exactly; where the canvas is itself partly transparent it
    /// is not (an alpha of 50% over 50% stays 50% instead of becoming 75%, and the colours are weighted by the wrong
    /// alphas).
    /// </summary>
    private static byte[] PillowOver(byte[] fg, byte[] bg, bool firstFrame)
    {
        if (firstFrame)
            return fg;
        static byte Div255(int a) { var t = a + 128; return (byte)(((t >> 8) + t) >> 8); }
        var result = new byte[4];
        for (var c = 0; c < 4; c++)
            result[c] = Div255(bg[c] * (255 - fg[3]) + fg[c] * fg[3]);
        return result;
    }

    /// <summary>
    /// The partial-alpha fixture, composited by the specification's formula (a straight loop here, over the frames'
    /// own RGBA8 pixels, sharing nothing with the reader's compositor but the sample expansion): the reader's frames
    /// are exactly those. Pillow's canvases are not, and the difference is measured and pinned: Pillow's own OVER (see
    /// <see cref="PillowOver"/>) reproduces its digests in <c>apng-reference.tsv</c> for every frame, so the
    /// difference is the formula and nothing else, and the figures below are how far apart the two answers are.
    /// </summary>
    [Fact]
    public void PartialAlphaOver_MatchesTheSpecFormulaExactly_AndPillowsDifferenceIsMeasured()
    {
        var bytes = Bytes(PartialAlphaFile);
        var img = PngReader.Decode(bytes);
        var rows = RowsFor(PartialAlphaFile);
        var ours = PngReader.DecodeFrames(bytes).Frames;
        var spec = new byte[img.Width * img.Height * 4];
        var pillow = new byte[img.Width * img.Height * 4];

        var partialOverPixels = 0;
        var differing = new List<int>();
        var largest = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var frame = img.Animation!.Frames[i];
            frame.BlendOp.ShouldBe(PngBlendOp.Over);
            frame.DisposeOp.ShouldBe(PngDisposeOp.None);
            var rgba = (img with { Width = frame.Width, Height = frame.Height, Pixels = PngReader.DecodeFrameSamples(bytes, img, i) }).ToRgba8();
            for (var y = 0; y < frame.Height; y++)
                for (var x = 0; x < frame.Width; x++)
                {
                    var from = (y * frame.Width + x) * 4;
                    var to = ((frame.YOffset + y) * img.Width + frame.XOffset + x) * 4;
                    var fg = rgba[from..(from + 4)];
                    if (fg[3] is > 0 and < 255 && spec[to + 3] != 0)
                        partialOverPixels++;
                    SpecOver(fg, spec[to..(to + 4)]).CopyTo(spec, to);
                    PillowOver(fg, pillow[to..(to + 4)], i == 0).CopyTo(pillow, to);
                }

            ours[i].Rgba.ShouldBe(spec, $"frame {i}: the reader against the specification's formula");
            Sha(pillow).ShouldBe(rows[i].CanvasSha, $"frame {i}: the Pillow model against Pillow");
            differing.Add(Enumerable.Range(0, spec.Length / 4).Count(p => !spec.AsSpan(p * 4, 4).SequenceEqual(pillow.AsSpan(p * 4, 4))));
            largest.Add(Enumerable.Range(0, spec.Length).Max(k => Math.Abs(spec[k] - pillow[k])));
        }
        partialOverPixels.ShouldBeGreaterThan(100); // the fixture does exercise partial alpha over something

        // measured: the first frame is the same, then Pillow's alpha-over-alpha is off by up to 91 of 255 in alpha
        differing.ShouldBe(new[] { 0, 264, 571, 667 });
        largest.ShouldBe(new[] { 0, 64, 91, 91 });
    }

    // ---------------------------------------------------------------- the animation limit

    [Fact]
    public void DecodeFrames_RefusesMoreThanTheLimit_AtTheBoundary()
    {
        var bytes = Bytes(Base);   // 48x32, 4 frames
        const long total = 48 * 32 * 4;

        PngReader.DecodeFrames(bytes, total).Frames.Count.ShouldBe(4);
        var thrown = Should.Throw<InvalidDataException>(() => PngReader.DecodeFrames(bytes, total - 1));
        thrown.Message.ShouldContain("EnumerateFrames");
        Should.Throw<ArgumentOutOfRangeException>(() => PngReader.DecodeFrames(bytes, 0));
        // streaming has no such limit
        PngReader.EnumerateFrames(bytes).Count().ShouldBe(4);
    }

    /// <summary>
    /// A small file that declares many frames of a big canvas: 65 one-pixel frames of a 2048×2048 canvas is 2^28 + 2^22
    /// pixels, which the default limit refuses. The refusal comes before any canvas or frame exists (it allocates less
    /// than one RGBA canvas, 16 MiB, where the IDAT's own samples are 4 MiB), while the streaming enumerator walks the
    /// same file one canvas at a time.
    /// </summary>
    [Fact]
    public void ManyFramesOfABigCanvas_AreRefusedByTheDefaultLimit_BeforeAllocating_ButCanBeStreamed()
    {
        const int side = 2048, frames = 65;
        var cover = Parse(PngWriter.EncodeGray8(new byte[side * side], side, side));
        var pixel = Parse(PngWriter.EncodeGray8(new byte[1], 1, 1)).Single(c => c.Type == "IDAT").Data;
        var chunks = new List<Chunk>
        {
            cover.Single(c => c.Type == "IHDR"),
            new("acTL", new byte[8]),
        };
        BinaryPrimitives.WriteUInt32BigEndian(chunks[1].Data, frames);
        chunks.AddRange(cover.Where(c => c.Type == "IDAT"));
        for (var i = 0; i < frames; i++)
        {
            var control = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), 1);   // width
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8), 1);   // height
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20), 1);  // delay 1/1
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22), 1);
            chunks.Add(new Chunk("fcTL", control));
            chunks.Add(FdAt(pixel));
        }
        chunks.Add(new Chunk("IEND", []));
        Renumber(chunks);
        var bytes = Build(chunks);
        bytes.Length.ShouldBeLessThan(20_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var thrown = Should.Throw<InvalidDataException>(() => PngReader.DecodeFrames(bytes));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        thrown.Message.ShouldContain(PngReader.DefaultMaximumAnimationPixels.ToString("N0"));
        allocated.ShouldBeLessThan(side * side * 4);
        PngReader.TryDecode(bytes, out var image).ShouldBeTrue();
        image!.Animation!.FrameCount.ShouldBe(frames);

        var firstTwo = PngReader.EnumerateFrames(bytes).Take(2).Select(f => f.Index).ToList();
        firstTwo.ShouldBe(new[] { 0, 1 });
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
