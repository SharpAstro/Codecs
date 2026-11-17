using System.Numerics;
using System.Text;
using ImageMagick;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;
using FillRule = SharpAstro.VectorGraphics.FillRule;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The scene against a renderer that is not ours. The structural tests say what the reader produces; they cannot say
/// that it is what an SVG renderer would draw, because the same misreading of the specification (a sweep flag the wrong
/// way, a transform list applied left to right) would be in the expectation too. So each document here is drawn twice:
/// by ImageMagick's own SVG renderer, and by a deliberately plain rasteriser over OUR scene
/// (<see cref="SvgPlainRasteriser"/>: 16 samples a pixel, each asked "is this point inside"). The two are compared
/// pixel by pixel.
/// </summary>
/// <remarks>
/// <para>ImageMagick's renderer is a modest one, and two things were taken out of these documents because it gets them
/// wrong, each measured with the picture in hand (<c>SVG_ORACLE_DUMP</c>): it does not read <c>hsl()</c> (the shape keeps
/// its inherited fill), and it does not draw a <c>visibility="visible"</c> child of a hidden group, which SVG says is
/// drawn because visibility is inherited, not applied to the subtree. Both stay covered by the structural tests.</para>
/// These documents are fills only, as they were when the test rasteriser had no stroker; strokes, clips, masks,
/// markers and patterns are held against the browser (<see cref="SvgBrowserOracleTests"/>), which draws them right.
/// The comparison is a tolerance (the two anti-alias differently), kept in this file, apart from the exact tests.
/// </remarks>
public sealed class SvgRenderOracleTests
{
    public static TheoryData<string, string, string> Documents => new()
    {
        { "shapes", "width=\"96\" height=\"64\"",
            "<rect x=\"4\" y=\"6\" width=\"30\" height=\"20\" fill=\"#c33\"/>" +
            "<rect x=\"40\" y=\"6\" width=\"50\" height=\"24\" rx=\"12\" ry=\"6\" fill=\"#36c\"/>" +
            "<circle cx=\"20\" cy=\"46\" r=\"14\" fill=\"seagreen\"/>" +
            "<ellipse cx=\"60\" cy=\"46\" rx=\"26\" ry=\"11\" fill=\"rgb(200,160,0)\"/>" +
            "<polygon points=\"80,34 94,60 70,60\" fill=\"purple\"/>" },
        { "path-commands", "width=\"96\" height=\"96\"",
            "<path d=\"M10 10 h30 v20 h-30 z m40 0 l30 0 0 20 -30 0 z\" fill=\"#444\"/>" +
            "<path d=\"M10 50 C 10 20 30 15 40 50 S 70 80 70 50 Q 75 25 90 50 T 90 80 L 10 90 Z\" fill=\"#e80\"/>" },
        { "arcs", "width=\"120\" height=\"120\"",
            "<path d=\"M20 40 A 30 20 0 0 1 60 40 Z\" fill=\"#a00\"/>" +
            "<path d=\"M20 40 A 30 20 0 1 0 60 40 Z\" fill=\"#0a0\"/>" +
            "<path d=\"M70 60 a 25 12 35 1 1 20 30 l -30 10 z\" fill=\"#00a\"/>" +
            "<path d=\"M10 100 A 5 5 0 0 0 50 100 A 60 60 0 0 0 10 100\" fill=\"#088\"/>" },
        { "fill-rules", "width=\"120\" height=\"60\"",
            "<path d=\"M30 4 L50 56 L4 22 L56 22 L10 56 Z\" fill=\"#06c\" fill-rule=\"nonzero\"/>" +
            "<path d=\"M90 4 L110 56 L64 22 L116 22 L70 56 Z\" fill=\"#06c\" fill-rule=\"evenodd\"/>" },
        { "transforms", "width=\"120\" height=\"120\"",
            "<g transform=\"translate(60 60)\">" +
            "<rect x=\"-40\" y=\"-8\" width=\"80\" height=\"16\" fill=\"#b22\" transform=\"rotate(30)\"/>" +
            "<rect x=\"-40\" y=\"-8\" width=\"80\" height=\"16\" fill=\"#22b\" transform=\"rotate(-60) scale(1 0.5)\"/>" +
            "<g transform=\"skewX(30) translate(-10,25) scale(0.5)\"><rect width=\"40\" height=\"40\" fill=\"#282\"/></g>" +
            "<circle r=\"10\" fill=\"#cc0\" transform=\"matrix(1 0.5 -0.5 1 20 -35)\"/>" +
            "</g>" },
        { "viewbox-meet", "width=\"120\" height=\"60\" viewBox=\"10 10 50 50\"",
            "<rect x=\"10\" y=\"10\" width=\"50\" height=\"50\" fill=\"#ddd\"/><circle cx=\"35\" cy=\"35\" r=\"20\" fill=\"#c0c\"/>" +
            "<path d=\"M10 10 L60 10 L10 60 Z\" fill=\"#084\"/>" },
        { "viewbox-slice", "width=\"120\" height=\"60\" viewBox=\"0 0 50 50\" preserveAspectRatio=\"xMinYMax slice\"",
            "<circle cx=\"25\" cy=\"25\" r=\"24\" fill=\"#c0c\"/><rect x=\"0\" y=\"40\" width=\"25\" height=\"10\" fill=\"#084\"/>" },
        { "viewbox-none", "width=\"120\" height=\"60\" viewBox=\"0 0 50 50\" preserveAspectRatio=\"none\"",
            "<circle cx=\"25\" cy=\"25\" r=\"24\" fill=\"#c0c\"/><rect x=\"0\" y=\"40\" width=\"25\" height=\"10\" fill=\"#084\"/>" },
        { "opacity", "width=\"100\" height=\"80\"",
            "<rect x=\"5\" y=\"5\" width=\"50\" height=\"40\" fill=\"#f00\"/>" +
            "<rect x=\"30\" y=\"20\" width=\"50\" height=\"40\" fill=\"#00f\" fill-opacity=\"0.5\"/>" +
            "<rect x=\"45\" y=\"35\" width=\"50\" height=\"40\" fill=\"#0c0\" opacity=\"0.5\" fill-opacity=\"0.5\"/>" +
            "<rect x=\"10\" y=\"50\" width=\"30\" height=\"25\" fill=\"rgba(0,0,0,0.25)\"/>" },
        { "group-opacity", "width=\"100\" height=\"80\"",
            "<rect x=\"0\" y=\"30\" width=\"100\" height=\"20\" fill=\"#222\"/>" +
            // Two overlapping shapes faded as one layer: where they overlap must not be darker than where they do not.
            "<g opacity=\"0.5\"><circle cx=\"35\" cy=\"40\" r=\"28\" fill=\"#e00\"/><circle cx=\"65\" cy=\"40\" r=\"28\" fill=\"#e00\"/></g>" },
        { "styles", "width=\"100\" height=\"60\"",
            "<g fill=\"#07a\" color=\"#a70\">" +
            "<rect x=\"4\" y=\"4\" width=\"28\" height=\"52\"/>" +
            "<rect x=\"36\" y=\"4\" width=\"28\" height=\"52\" fill=\"red\" style=\"fill: currentColor\"/>" +
            "<rect x=\"68\" y=\"4\" width=\"28\" height=\"52\" style=\"fill:rgb(60%, 16%, 60%)\"/>" +
            "<rect x=\"10\" y=\"20\" width=\"80\" height=\"20\" display=\"none\"/>" +
            "<g visibility=\"hidden\"><rect x=\"10\" y=\"20\" width=\"80\" height=\"20\"/></g>" +
            "</g>" },
        { "units", "width=\"1in\" height=\"0.5in\"",
            "<rect x=\"10%\" y=\"10%\" width=\"12mm\" height=\"18pt\" fill=\"#555\"/><circle cx=\"75%\" cy=\"50%\" r=\"1pc\" fill=\"#c60\"/>" },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void OurScene_DrawnPlainly_MatchesImageMagicksRenderingOfTheDocument(string name, string root, string body)
    {
        string svg = Wrap(body, root);
        SvgDocument document = SvgReader.Parse(svg);
        document.Issues.ShouldBeEmpty(name);

        int width = (int)MathF.Round(document.Width), height = (int)MathF.Round(document.Height);
        Compare(name, "magick", document.Scene, width, height, MagickRender(svg, width, height), MaxMeanDifference);
    }

    /// <summary>
    /// Draws the scene with the test rasteriser and holds it against a reference rendering (RGB bytes over white).
    /// Two measures. Edge pixels differ a little everywhere (different anti-aliasing), so the mean must be small;
    /// and a pixel that is plainly a different colour is a shape in the wrong place, so there must be none.
    /// </summary>
    /// <param name="edgeSlack">
    /// False: a pixel is wrong when it is more than <see cref="MaxPixelDifference"/> from the reference's pixel. True: only
    /// when it is also outside the range of the reference's pixels around it (3 x 3) by more than <see cref="EdgeSlack"/>,
    /// which lets an edge sit up to a pixel away and nothing else. For a reference whose own curves are not exact.
    /// </param>
    internal static void Compare(string name, string reference, VectorScene scene, int width, int height, byte[] theirs, double maxMean,
        bool edgeSlack = false)
    {
        float[] ours = SvgPlainRasteriser.Render(scene, width, height);
        double sum = 0;
        int wrong = 0;
        var where = new StringBuilder();
        for (int i = 0; i < width * height; i++)
        {
            double worst = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                double d = Math.Abs(ours[i * 3 + ch] * 255.0 - theirs[i * 3 + ch]);
                sum += d;
                worst = Math.Max(worst, d);
            }
            if (worst > MaxPixelDifference && (!edgeSlack || OutsideNeighbourhood(ours, theirs, width, height, i)))
            {
                // Where, and what each side has there: enough to tell an edge from a misplaced shape.
                if (wrong++ < 8)
                {
                    where.Append($" ({i % width},{i / width}: ours {ours[i * 3] * 255:0},{ours[i * 3 + 1] * 255:0},{ours[i * 3 + 2] * 255:0}")
                        .Append($" theirs {theirs[i * 3]},{theirs[i * 3 + 1]},{theirs[i * 3 + 2]})");
                }
            }
        }
        double mean = sum / (width * height * 3);
        string report = $"{name} vs {reference}: mean {mean:0.000}, pixels over {MaxPixelDifference}: {wrong} of {width * height}{where}";
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        if (Environment.GetEnvironmentVariable("SVG_ORACLE_DUMP") is { Length: > 0 } dump)
        {
            // Both renderings as PNGs, for looking at a failure: SVG_ORACLE_DUMP=<directory>.
            Directory.CreateDirectory(dump);
            byte[] oursBytes = [.. ours.Select(v => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255))];
            var raw = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB);
            using (var image = new MagickImage(oursBytes, raw)) image.Write(Path.Combine(dump, name + "-ours.png"));
            using (var image = new MagickImage(theirs, raw)) image.Write(Path.Combine(dump, $"{name}-{reference}.png"));
        }
        mean.ShouldBeLessThan(maxMean, report);
        wrong.ShouldBe(0, report);
    }

    /// <summary>True when our pixel is not within <see cref="EdgeSlack"/> of the range the reference has in the 3 x 3 around it.</summary>
    private static bool OutsideNeighbourhood(float[] ours, byte[] theirs, int width, int height, int i)
    {
        int x = i % width, y = i / width;
        for (int ch = 0; ch < 3; ch++)
        {
            int low = 255, high = 0;
            for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
            {
                for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                {
                    int v = theirs[(ny * width + nx) * 3 + ch];
                    (low, high) = (Math.Min(low, v), Math.Max(high, v));
                }
            }
            double value = ours[i * 3 + ch] * 255.0;
            if (value < low - EdgeSlack || value > high + EdgeSlack) return true;
        }
        return false;
    }

    /// <summary>Of 255. How far outside its neighbourhood's range a pixel may be when an edge is allowed to sit a pixel away.</summary>
    private const double EdgeSlack = 24;

    /// <summary>Of 255. A pixel further off than this is not an anti-aliasing difference.</summary>
    internal const double MaxPixelDifference = 72;

    /// <summary>Of 255, over all pixels and channels.</summary>
    private const double MaxMeanDifference = 0.8;

    private static byte[] MagickRender(string svg, int width, int height)
    {
        var settings = new MagickReadSettings { Format = MagickFormat.Svg, BackgroundColor = MagickColors.White };
        using var image = new MagickImage(Encoding.UTF8.GetBytes(svg), settings);
        ((int)image.Width, (int)image.Height).ShouldBe((width, height));
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
        using IPixelCollection<float> pixels = image.GetPixels();
        return pixels.ToByteArray(PixelMapping.RGB).ShouldNotBeNull();
    }
}
