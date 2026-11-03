using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

// dwebp (libwebp, a black-box oracle) reading what the third encoder rung writes: every forced
// pipeline, each of the 14 predictor modes, cross colour extremes, palettes at odd widths and an
// entropy image. These skip locally when dwebp is missing and fail under REQUIRE_ORACLES.

public sealed class WebpLosslessEncoderTransformOracleTests
{
    public static IEnumerable<object[]> NamedNames() => WebpLosslessEncoderPipelines.NamedNames();

    private static void AssertDwebp(string dwebp, int[] argb, int width, int height, byte[] file, string what)
    {
        (int w, int h, byte[] pixels) = WebpLosslessEncoderOracleTests.RunDwebpPam(dwebp, file);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
        byte[] expected = WebpLosslessEncoderTransformImages.ToRgba(argb);
        Assert.True(expected.AsSpan().SequenceEqual(pixels), $"{what}: {WebpLosslessEncoderOracleTests.Describe(expected, pixels)}");
    }

    [Theory]
    [MemberData(nameof(NamedNames))]
    public void DwebpReadsEveryForcedPipelineOnEveryShape(string pipelineName)
    {
        string dwebp = WebpOracle.RequireDwebp();
        foreach (var (name, width, height, argb) in WebpLosslessEncoderPipelines.Images())
        {
            byte[] file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, WebpLosslessEncoderPipelines.Named[pipelineName]);
            AssertDwebp(dwebp, argb, width, height, file, $"{pipelineName} on {name}");
        }
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
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void DwebpReadsEachPredictorModeForced(int mode)
    {
        string dwebp = WebpOracle.RequireDwebp();
        foreach (string name in new[] { "random-33x17", "smooth-alpha-61x45", "random-1x97", "random-97x1", "transparent-rgb-31x17" })
        {
            var (width, height, argb) = WebpLosslessEncoderPipelines.Image(name);
            foreach (int bits in new[] { 2, 4 })
            {
                byte[] file = WebpLosslessEncoderTransformImages.Encode(
                    argb, width, height, new Vp8LPipeline { PredictorBits = bits, ForcedPredictorMode = mode });
                AssertDwebp(dwebp, argb, width, height, file, $"mode {mode}, bits {bits}, {name}");
            }
        }
    }

    [Theory]
    [InlineData(-128, -128, -128)]
    [InlineData(127, 127, 127)]
    [InlineData(-128, 127, 33)]
    [InlineData(9, -77, 1)]
    public void DwebpReadsForcedCrossColourMultipliers(int greenToRed, int greenToBlue, int redToBlue)
    {
        string dwebp = WebpOracle.RequireDwebp();
        foreach (string name in new[] { "random-33x17", "smooth-alpha-61x45" })
        {
            var (width, height, argb) = WebpLosslessEncoderPipelines.Image(name);
            byte[] file = WebpLosslessEncoderTransformImages.Encode(
                argb,
                width,
                height,
                new Vp8LPipeline { CrossColor = true, CrossBits = 3, ForcedCross = (greenToRed, greenToBlue, redToBlue) });
            AssertDwebp(dwebp, argb, width, height, file, $"({greenToRed}, {greenToBlue}, {redToBlue}) on {name}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(256)]
    public void DwebpReadsPalettesOfEverySizeAtOddWidths(int colours)
    {
        string dwebp = WebpOracle.RequireDwebp();
        foreach (int width in new[] { 1, 3, 7, 9, 17, 31 })
        {
            foreach (int height in new[] { 1, 5 })
            {
                int[] argb = WebpLosslessEncoderTransformImages.Colours(width, height, colours, (uint)(colours * 37 + width), colours % 2 == 0);
                foreach ((string pipelineName, Vp8LPipeline pipeline) in WebpLosslessEncoderPipelines.Palettes)
                {
                    if (pipeline.PredictorBits != 0 && width * height < 2)
                    {
                        continue;
                    }

                    byte[] file = WebpLosslessEncoderTransformImages.Encode(argb, width, height, pipeline);
                    AssertDwebp(dwebp, argb, width, height, file, $"{pipelineName}, {colours} colours, {width}x{height}");
                }

                foreach (int effort in new[] { 0, 1, 4, 9 })
                {
                    byte[] file = WebpEncoder.EncodeLossless(
                        WebpLosslessEncoderTransformImages.ToRgba(argb), width, height, options: new WebpEncodeOptions { Effort = effort });
                    AssertDwebp(dwebp, argb, width, height, file, $"effort {effort}, {colours} colours, {width}x{height}");
                }
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(9)]
    public void DwebpReadsAForcedEntropyImage(int metaBits)
    {
        string dwebp = WebpOracle.RequireDwebp();
        foreach ((int width, int height) in new[] { (70, 45), (1, 33), (200, 130) })
        {
            int[] argb = WebpLosslessEncoderPipelines.TwoRegions(width, height);
            foreach (int cacheBits in new[] { 0, 5 })
            {
                byte[] file = WebpLosslessEncoderTransformImages.Encode(
                    argb, width, height, new Vp8LPipeline { SubtractGreen = true, PredictorBits = 3 }, 9, cacheBits, copies: true, metaBits);
                AssertDwebp(dwebp, argb, width, height, file, $"meta {metaBits}, cache {cacheBits}, {width}x{height}");
            }
        }
    }

    [Fact]
    public void DwebpReadsTheEncodersOwnChoiceAtEveryEffort()
    {
        string dwebp = WebpOracle.RequireDwebp();
        foreach (var (name, width, height, argb) in WebpLosslessEncoderPipelines.Images())
        {
            byte[] rgba = WebpLosslessEncoderTransformImages.ToRgba(argb);
            for (int effort = 0; effort <= 9; effort++)
            {
                byte[] file = WebpEncoder.EncodeLossless(rgba, width, height, options: new WebpEncodeOptions { Effort = effort });
                AssertDwebp(dwebp, argb, width, height, file, $"{name} at effort {effort}");
            }
        }
    }

    [Fact]
    public void DwebpReadsTheEncodersOwnEntropyImageChoice()
    {
        string dwebp = WebpOracle.RequireDwebp();
        int[] argb = WebpLosslessEncoderPipelines.TwoRegions(256, 128);
        byte[] file = WebpEncoder.EncodeLossless(
            WebpLosslessEncoderTransformImages.ToRgba(argb), 256, 128, options: new WebpEncodeOptions { Effort = 9 });
        AssertDwebp(dwebp, argb, 256, 128, file, "two regions at effort 9");
    }
}
