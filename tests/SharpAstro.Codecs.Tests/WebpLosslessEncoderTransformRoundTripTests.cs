using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

// Round trips of the third encoder rung through our own decoder: forced pipelines, every predictor
// mode, cross colour extremes, palettes of every size at odd widths, entropy images, and the sizes
// the encoder's choices must beat. dwebp's side of the same cases is in
// WebpLosslessEncoderTransformOracleTests.

internal static class WebpLosslessEncoderPipelines
{
    public static readonly Dictionary<string, Vp8LPipeline> Named = new()
    {
        ["none"] = Vp8LPipeline.None,
        ["sg"] = new() { SubtractGreen = true },
        ["p5"] = new() { PredictorBits = 5 },
        ["sg-p4"] = new() { SubtractGreen = true, PredictorBits = 4 },
        ["sg-p3-c"] = new() { SubtractGreen = true, PredictorBits = 3, CrossColor = true },
        ["p2-c"] = new() { PredictorBits = 2, CrossColor = true },
        ["c-only"] = new() { CrossColor = true, CrossBits = 4 },
        ["sg-p9"] = new() { PredictorBits = 9, SubtractGreen = true },
    };

    public static readonly Dictionary<string, Vp8LPipeline> Palettes = new()
    {
        ["palette"] = new() { Palette = true },
        ["palette-first"] = new() { Palette = true, FirstSeenPalette = true },
        ["palette-p4"] = new() { Palette = true, PredictorBits = 4 },
        ["palette-first-p2"] = new() { Palette = true, FirstSeenPalette = true, PredictorBits = 2 },
    };

    public static IEnumerable<object[]> NamedNames() => Named.Keys.Select(static k => new object[] { k });

    public static IEnumerable<object[]> PaletteNames() => Palettes.Keys.Select(static k => new object[] { k });

    /// <summary>The shapes the transforms have edge cases at: one row, one column, tiny, odd, wider than a tile.</summary>
    public static IEnumerable<(string Name, int Width, int Height, int[] Argb)> Images()
    {
        yield return ("random-1x1", 1, 1, WebpLosslessEncoderTransformImages.Random(1, 1, 5));
        yield return ("random-97x1", 97, 1, WebpLosslessEncoderTransformImages.Random(97, 1, 6));
        yield return ("random-1x97", 1, 97, WebpLosslessEncoderTransformImages.Random(1, 97, 7));
        yield return ("random-2x2", 2, 2, WebpLosslessEncoderTransformImages.Random(2, 2, 8));
        yield return ("random-33x17", 33, 17, WebpLosslessEncoderTransformImages.Random(33, 17, 9));
        yield return ("smooth-alpha-61x45", 61, 45, WebpLosslessEncoderTransformImages.Smooth(61, 45, 10, true));
        yield return ("smooth-70x45", 70, 45, WebpLosslessEncoderTransformImages.Smooth(70, 45, 11, false));
        yield return ("transparent-rgb-31x17", 31, 17, TransparentWithColour(31, 17));
    }

    public static (int Width, int Height, int[] Argb) Image(string name)
    {
        var image = Images().Single(i => i.Name == name);
        return (image.Width, image.Height, image.Argb);
    }

    // Alpha 0 with an RGB that differs from pixel to pixel, mixed with opaque pixels: lossless
    // means the RGB under the transparency must survive every transform.
    private static int[] TransparentWithColour(int w, int h)
    {
        uint s = 91;
        int[] argb = new int[w * h];
        for (int i = 0; i < argb.Length; i++)
        {
            int rgb = (int)(WebpLosslessEncoderTransformImages.Next(ref s) & 0xffffff);
            argb[i] = (i % 5 == 0 ? unchecked((int)0xff000000) : i % 3 == 0 ? 0x40000000 : 0) | rgb;
        }

        return argb;
    }

    /// <summary>
    /// Left half: red is noise, the rest constant. Right half: green is noise, the rest constant.
    /// One prefix-code group has to carry both, so two groups save about a bit a pixel.
    /// </summary>
    public static int[] TwoRegions(int width, int height)
    {
        uint s = 12345;
        int[] argb = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int noise = (int)(WebpLosslessEncoderTransformImages.Next(ref s) & 0xff);
                argb[y * width + x] = unchecked((int)0xff000000) | (x < width / 2 ? noise << 16 : (noise << 8) | 7);
            }
        }

        return argb;
    }
}

public sealed class WebpLosslessEncoderTransformRoundTripTests
{
    public static IEnumerable<object[]> NamedNames() => WebpLosslessEncoderPipelines.NamedNames();

    public static IEnumerable<object[]> PaletteNames() => WebpLosslessEncoderPipelines.PaletteNames();

    public static IEnumerable<object[]> CorpusNamesByEffort() => WebpLosslessEncoderCorpus.NamesByEffort();

    public static IEnumerable<object[]> ColourCounts() =>
        new[] { 1, 2, 3, 4, 5, 15, 16, 17, 255, 256 }.Select(static c => new object[] { c });

    private static void AssertDecodesTo(int[] argb, int width, int height, byte[] file, string what)
    {
        WebpImage decoded = WebpDecoder.Decode(file);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.True(WebpLosslessEncoderTransformImages.ToRgba(argb).AsSpan().SequenceEqual(decoded.Rgba), what);
    }

    [Theory]
    [MemberData(nameof(CorpusNamesByEffort))]
    public void EveryCorpusImageDecodesByteForByteAtEveryEffort(string name, int effort)
    {
        var image = WebpLosslessEncoderCorpus.Get(name);
        byte[] file = WebpEncoder.EncodeLossless(
            image.Rgba, image.Width, image.Height, options: new WebpEncodeOptions { Effort = effort });
        Assert.True(image.Rgba.AsSpan().SequenceEqual(WebpDecoder.Decode(file).Rgba));
    }

    [Theory]
    [MemberData(nameof(NamedNames))]
    public void EveryForcedPipelineDecodesExactlyOnEveryShape(string pipelineName)
    {
        foreach (var (name, width, height, argb) in WebpLosslessEncoderPipelines.Images())
        {
            byte[] file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, WebpLosslessEncoderPipelines.Named[pipelineName]);
            AssertDecodesTo(argb, width, height, file, $"{pipelineName} on {name}");
        }
    }

    [Theory]
    [MemberData(nameof(NamedNames))]
    public void EveryForcedPipelineDecodesWithCopiesAndACache(string pipelineName)
    {
        foreach (string name in new[] { "random-33x17", "smooth-alpha-61x45", "transparent-rgb-31x17" })
        {
            var (width, height, argb) = WebpLosslessEncoderPipelines.Image(name);
            foreach (int cacheBits in new[] { 0, 1, 4, 11 })
            {
                byte[] file = WebpLosslessEncoderTransformImages.Encode(
                    argb, width, height, WebpLosslessEncoderPipelines.Named[pipelineName], 9, cacheBits, copies: true);
                AssertDecodesTo(argb, width, height, file, $"{pipelineName} on {name}, cache {cacheBits}");
            }
        }
    }

    [Fact]
    public void EachOfTheFourteenModesDecodesExactlyOnEveryShapeAndTileSize()
    {
        for (int mode = 0; mode < Vp8LTransforms.PredictorModes; mode++)
        {
            foreach (var (name, width, height, argb) in WebpLosslessEncoderPipelines.Images())
            {
                foreach (int bits in new[] { 2, 3, 5 })
                {
                    byte[] file = WebpLosslessEncoderTransformImages.Encode(
                        argb, width, height, new Vp8LPipeline { PredictorBits = bits, ForcedPredictorMode = mode });
                    AssertDecodesTo(argb, width, height, file, $"mode {mode}, bits {bits}, {name}");
                }
            }
        }
    }

    [Fact]
    public void ForcedPipelinesAreWrittenWithTheirFirstTransformFirst()
    {
        var (width, height, argb) = WebpLosslessEncoderPipelines.Image("smooth-70x45");
        byte[] file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, new Vp8LPipeline { PredictorBits = 4, ForcedPredictorMode = 12 });
        Assert.Equal(0, WebpLosslessEncoderTransformImages.FirstTransform(file));
        file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, new Vp8LPipeline { SubtractGreen = true });
        Assert.Equal(2, WebpLosslessEncoderTransformImages.FirstTransform(file));
        file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, new Vp8LPipeline { CrossColor = true });
        Assert.Equal(1, WebpLosslessEncoderTransformImages.FirstTransform(file));
        file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, Vp8LPipeline.None);
        Assert.Equal(-1, WebpLosslessEncoderTransformImages.FirstTransform(file));
    }

    [Fact]
    public void EveryPredictorTileSizeFromTwoToNineDecodesAtSizesThatAreNotTileMultiples()
    {
        foreach ((int width, int height) in new[] { (70, 45), (513, 3), (3, 513), (1, 600) })
        {
            int[] argb = WebpLosslessEncoderTransformImages.Smooth(width, height, 77, true);
            for (int bits = 2; bits <= 9; bits++)
            {
                byte[] file = WebpLosslessEncoderTransformImages.Encode(
                    argb, width, height, new Vp8LPipeline { SubtractGreen = true, PredictorBits = bits, CrossColor = true });
                AssertDecodesTo(argb, width, height, file, $"{width}x{height}, bits {bits}");
            }
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-128, -128, -128)]
    [InlineData(127, 127, 127)]
    [InlineData(-128, 127, 33)]
    [InlineData(9, -77, 1)]
    public void ForcedCrossColourMultipliersDecodeExactly(int greenToRed, int greenToBlue, int redToBlue)
    {
        foreach (var (name, width, height, argb) in WebpLosslessEncoderPipelines.Images())
        {
            byte[] file = WebpLosslessEncoderTransformImages.Encode(
                argb,
                width,
                height,
                new Vp8LPipeline { CrossColor = true, CrossBits = 3, ForcedCross = (greenToRed, greenToBlue, redToBlue) });
            AssertDecodesTo(argb, width, height, file, $"({greenToRed}, {greenToBlue}, {redToBlue}) on {name}");
        }
    }

    [Theory]
    [MemberData(nameof(ColourCounts))]
    public void PalettesOfEverySizeDecodeAtOddWidthsForcedAndChosen(int colours)
    {
        foreach (int width in new[] { 1, 2, 3, 5, 7, 9, 13, 17, 31 })
        {
            foreach (int height in new[] { 1, 2, 7 })
            {
                foreach (bool alpha in new[] { false, true })
                {
                    int[] argb = WebpLosslessEncoderTransformImages.Colours(width, height, colours, (uint)(colours * 131 + width), alpha);
                    foreach ((string pipelineName, Vp8LPipeline pipeline) in WebpLosslessEncoderPipelines.Palettes)
                    {
                        if (pipeline.PredictorBits != 0 && width * height < 2)
                        {
                            continue;
                        }

                        byte[] file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, pipeline);
                        AssertDecodesTo(argb, width, height, file, $"{pipelineName}, {colours} colours, {width}x{height}, alpha {alpha}");
                    }

                    for (int effort = 0; effort <= 9; effort++)
                    {
                        byte[] file = WebpEncoder.EncodeLossless(
                            WebpLosslessEncoderTransformImages.ToRgba(argb), width, height, options: new WebpEncodeOptions { Effort = effort });
                        AssertDecodesTo(argb, width, height, file, $"effort {effort}, {colours} colours, {width}x{height}, alpha {alpha}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(256)]
    public void ThePaletteIsChosenAtEverySizeWhenItPays(int colours)
    {
        int[] argb = WebpLosslessEncoderTransformImages.Colours(64, 48, colours, 99, false);
        byte[] file = WebpEncoder.EncodeLossless(
            WebpLosslessEncoderTransformImages.ToRgba(argb), 64, 48, options: new WebpEncodeOptions { Effort = 6 });
        Assert.Equal(3, WebpLosslessEncoderTransformImages.FirstTransform(file));
        AssertDecodesTo(argb, 64, 48, file, $"{colours} colours");
    }

    [Theory]
    [MemberData(nameof(PaletteNames))]
    public void ForcedPalettePipelinesAreWrittenFirstAsColourIndexing(string pipelineName)
    {
        int[] argb = WebpLosslessEncoderTransformImages.Colours(41, 13, 9, 5, true);
        byte[] file = WebpLosslessEncoderTransformImages.Encode(argb, 41, 13, WebpLosslessEncoderPipelines.Palettes[pipelineName]);
        Assert.Equal(3, WebpLosslessEncoderTransformImages.FirstTransform(file));
        AssertDecodesTo(argb, 41, 13, file, pipelineName);
    }

    [Fact]
    public void APaletteOfMoreThan256ColoursIsRefused()
    {
        int[] argb = WebpLosslessEncoderTransformImages.Colours(40, 40, 257, 3, false);
        Assert.Throws<ArgumentException>(
            () => WebpLosslessEncoderTransformImages.Encode(argb, 40, 40, new Vp8LPipeline { Palette = true }));
    }

    [Fact]
    public void ASmoothImageEmitsATransformAndTheLiteralOnlyEffortDoesNot()
    {
        int[] argb = WebpLosslessEncoderTransformImages.Smooth(90, 60, 5, true);
        byte[] rgba = WebpLosslessEncoderTransformImages.ToRgba(argb);
        foreach (int effort in new[] { 1, 3, 6, 9 })
        {
            byte[] file = WebpEncoder.EncodeLossless(rgba, 90, 60, options: new WebpEncodeOptions { Effort = effort });
            Assert.NotEqual(-1, WebpLosslessEncoderTransformImages.FirstTransform(file));
            AssertDecodesTo(argb, 90, 60, file, $"effort {effort}");
        }

        byte[] plain = WebpEncoder.EncodeLossless(rgba, 90, 60, options: new WebpEncodeOptions { Effort = 0 });
        Assert.Equal(-1, WebpLosslessEncoderTransformImages.FirstTransform(plain));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public void AForcedEntropyImageDecodesExactly(int metaBits)
    {
        foreach ((int width, int height) in new[] { (70, 45), (1, 33), (33, 1), (200, 130) })
        {
            int[] argb = WebpLosslessEncoderPipelines.TwoRegions(width, height);
            foreach (string pipelineName in new[] { "none", "sg-p4" })
            {
                foreach (int cacheBits in new[] { 0, 3 })
                {
                    byte[] file = WebpLosslessEncoderTransformImages.Encode(
                        argb, width, height, WebpLosslessEncoderPipelines.Named[pipelineName], 9, cacheBits, copies: true, metaBits);
                    AssertDecodesTo(argb, width, height, file, $"meta {metaBits}, {pipelineName}, cache {cacheBits}, {width}x{height}");
                }
            }
        }
    }

    [Fact]
    public void TwoDifferentRegionsCostLessWithGroupsThanWithOnePrefixCodeGroup()
    {
        int[] argb = WebpLosslessEncoderPipelines.TwoRegions(256, 128);
        int single = WebpLosslessEncoderTransformImages.Encode(argb, 256, 128, Vp8LPipeline.None).Length;
        int grouped = WebpLosslessEncoderTransformImages.Encode(argb, 256, 128, Vp8LPipeline.None, metaBits: 5).Length;
        Assert.True(grouped < single * 0.95, $"grouped {grouped} bytes, single {single}");
    }

    [Fact]
    public void TheEncoderPicksTheEntropyImageWhereItPays()
    {
        int[] argb = WebpLosslessEncoderPipelines.TwoRegions(256, 128);
        var plan = Vp8LEncoderPlan.Create(256 * 128, 9, WebpLimits.MaximumEncoderWorkingBytes);
        int without = Vp8LEncoder.Encode(argb, 256, 128, false, plan with { MetaPrefix = false }).Length;
        byte[] with = Vp8LEncoder.Encode(argb, 256, 128, false, plan);
        Assert.True(with.Length < without * 0.95, $"with {with.Length} bytes, without {without}");

        byte[] file = WebpEncoder.EncodeLossless(
            WebpLosslessEncoderTransformImages.ToRgba(argb), 256, 128, options: new WebpEncodeOptions { Effort = 9 });
        AssertDecodesTo(argb, 256, 128, file, "effort 9");
    }

    [Fact]
    public void EncodingIsDeterministicAtEveryEffort()
    {
        var image = WebpLosslessEncoderCorpus.Get("alpha-gradient");
        for (int effort = 0; effort <= 9; effort++)
        {
            var options = new WebpEncodeOptions { Effort = effort };
            Assert.Equal(
                WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height, options: options),
                WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height, options: options));
        }
    }
}

public sealed class WebpLosslessEncoderTransformSizeTests
{
    public static IEnumerable<object[]> CorpusNames() => WebpLosslessEncoderCorpus.Names();

    private static int Size(string name, int effort, bool transforms)
    {
        var image = WebpLosslessEncoderCorpus.Get(name);
        int[] argb = WebpLosslessEncoderCorpus.ToArgb(image.Rgba);
        bool alpha = argb.Any(static p => (p >>> 24) != 255);
        var plan = Vp8LEncoderPlan.Create((long)image.Width * image.Height, effort, WebpLimits.MaximumEncoderWorkingBytes);
        if (!transforms)
        {
            // The second rung's configuration.
            plan = plan with { Transforms = false, MetaPrefix = false };
        }

        return Vp8LEncoder.Encode(argb, image.Width, image.Height, alpha, plan).Length;
    }

    [Theory]
    [InlineData("photo-noise")]
    [InlineData("alpha-gradient")]
    public void ThePhotoAndTheAlphaGradientComeOutSmallerThanWithTransformsOff(string name)
    {
        foreach (int effort in new[] { 1, 3, 6, 9 })
        {
            int off = Size(name, effort, transforms: false);
            int on = Size(name, effort, transforms: true);
            Assert.True(on < off, $"{name} at effort {effort}: {on} bytes with transforms, {off} without");
        }
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void NoCorpusImageIsLargerThanWithTransformsOffFromEffortThree(string name)
    {
        for (int effort = 3; effort <= 9; effort += 3)
        {
            int off = Size(name, effort, transforms: false);
            int on = Size(name, effort, transforms: true);
            Assert.True(on <= off, $"{name} at effort {effort}: {on} bytes with transforms, {off} without");
        }
    }

    [Fact]
    public void ThePaletteBeatsLiteralPixelsOnAFewColourImage()
    {
        int off = Size("palette-16", 6, transforms: false);
        int on = Size("palette-16", 6, transforms: true);
        Assert.True(on < off * 0.9, $"palette-16: {on} bytes with transforms, {off} without");
    }
}
