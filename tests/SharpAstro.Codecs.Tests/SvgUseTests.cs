using System.Numerics;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary><c>use</c>, <c>symbol</c> and nested <c>svg</c>: what is drawn where, and what a document may ask for.</summary>
public sealed class SvgUseTests
{
    private const string Box = "<defs><rect id=\"r\" width=\"10\" height=\"10\"/></defs>";
    private const string XLink = "width=\"100\" height=\"100\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    private static VectorShape[] Shapes(SvgDocument document) => [.. document.Scene.Nodes.Cast<VectorShape>()];

    [Fact]
    public void Use_DrawsTheElementItNames_MovedByXAndY_InsideItsOwnTransform()
    {
        VectorShape shape = OnlyShape(Box + "<use href=\"#r\" x=\"5\" y=\"7\" transform=\"scale(2)\"/>");
        // transform, then translate(x, y), then the element: the offset is scaled too.
        Map(shape, 0, 0).ShouldBe(new Vector2(10, 14));
        Map(shape, 10, 10).ShouldBe(new Vector2(30, 34));
        Describe(shape.Path).ShouldBe("M 0 0 L 10 0 L 10 10 L 0 10 Z");
        // The thing in defs is not drawn where it stands.
        Doc(Box).Scene.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public void WhatIsUsed_InheritsFromTheUse_ButKeepsWhatItSaysItself()
    {
        SvgDocument document = Doc(
            "<defs><rect id=\"plain\" width=\"1\" height=\"1\"/><rect id=\"own\" width=\"1\" height=\"1\" fill=\"blue\"/></defs>" +
            "<g fill=\"red\" stroke=\"lime\"><use href=\"#plain\"/><use href=\"#plain\" fill=\"#ff0\" stroke-width=\"3\"/><use href=\"#own\"/></g>");
        VectorShape[] shapes = Shapes(document);
        FillColor(shapes[0]).ShouldBe(new Rgba32(255, 0, 0));
        FillColor(shapes[1]).ShouldBe(new Rgba32(255, 255, 0));
        shapes[1].Stroke!.Width.ShouldBe(3f);
        StrokeColor(shapes[1]).ShouldBe(new Rgba32(0, 255, 0));
        FillColor(shapes[2]).ShouldBe(new Rgba32(0, 0, 255));
        document.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void XlinkHref_IsRead_AndThePlainHrefWinsWhenBothAreThere()
    {
        const string defs = "<defs><rect id=\"a\" width=\"1\" height=\"1\"/><rect id=\"b\" width=\"2\" height=\"2\"/></defs>";
        Describe(OnlyShape(defs + "<use xlink:href=\"#a\"/>", XLink).Path).ShouldBe("M 0 0 L 1 0 L 1 1 L 0 1 Z");
        Describe(OnlyShape(defs + "<use xlink:href=\"#a\" href=\"#b\"/>", XLink).Path).ShouldBe("M 0 0 L 2 0 L 2 2 L 0 2 Z");
    }

    [Fact]
    public void EveryInstanceOfAPath_SharesOnePathObject()
    {
        SvgDocument document = Doc(
            "<defs><path id=\"p\" d=\"M0 0 L5 5 L0 5 Z\"/><polygon id=\"q\" points=\"0,0 4,0 4,4\"/></defs>" +
            "<use href=\"#p\"/><use href=\"#p\" x=\"10\"/><use href=\"#q\"/><use href=\"#q\" y=\"10\"/>");
        VectorShape[] shapes = Shapes(document);
        shapes[1].Path.ShouldBeSameAs(shapes[0].Path);
        shapes[3].Path.ShouldBeSameAs(shapes[2].Path);
        shapes[1].Transform.ShouldNotBe(shapes[0].Transform);
    }

    [Fact]
    public void OpacityOnAUse_IsALayer_NamedAfterTheUse()
    {
        VectorGroup group = Doc(Box + "<use id=\"u\" href=\"#r\" opacity=\"0.5\" stroke=\"red\"/>")
            .Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Id.ShouldBe("u");
        group.Opacity.ShouldBe(0.5f);
        group.Children.ShouldHaveSingleItem().Id.ShouldBe("r");
    }

    [Theory]
    [InlineData("<use href=\"#nothing\"/>", SvgIssueKind.Invalid, "use: no element with that id")]
    [InlineData("<use href=\"other.svg#r\"/>", SvgIssueKind.Unsupported, "use of an element in another document")]
    public void AUseThatCannotBeFollowed_DrawsNothing_AndIsReported(string use, SvgIssueKind kind, string what)
    {
        SvgDocument document = Doc(Box + use);
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(kind, what, 1));
    }

    [Theory]
    [InlineData("<use/>")]
    [InlineData("<use href=\"\"/>")]
    [InlineData("<use href=\"#g\"/>")]            // a gradient is not something to draw
    [InlineData("<use href=\"#r\" display=\"none\"/>")]
    public void AUseWithNothingToDraw_IsSilent(string use)
    {
        SvgDocument document = Doc(Box + "<defs><linearGradient id=\"g\"/></defs>" + use);
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("<g id=\"a\"><use href=\"#a\"/></g>")]
    [InlineData("<use id=\"a\" href=\"#a\"/>")]
    [InlineData("<g id=\"a\"><rect width=\"1\" height=\"1\"/><use href=\"#b\"/></g><g id=\"b\"><use href=\"#a\"/></g>")]
    [InlineData("<defs><g id=\"a\"><use href=\"#b\"/></g><g id=\"b\"><use href=\"#c\"/></g><g id=\"c\"><use href=\"#a\"/></g></defs><use href=\"#a\"/>")]
    public void AUseThatReachesItself_Stops_AndIsReported(string body)
    {
        SvgDocument document = Doc(body);
        document.Issues.ShouldContain(issue => issue.Kind == SvgIssueKind.Invalid && issue.What == "use: circular reference");
    }

    [Fact]
    public void OfTwoGroupsThatUseEachOther_EachIsDrawnOnceWithTheOtherInside_AndNoFurther()
    {
        // Measured against a browser (the use-cycles document of the browser oracle).
        SvgDocument document = Doc(
            "<g id=\"a\"><rect id=\"ra\" width=\"1\" height=\"1\"/><use href=\"#b\"/></g>" +
            "<g id=\"b\"><rect id=\"rb\" width=\"1\" height=\"1\"/><use href=\"#a\"/></g>");
        document.Scene.Nodes.Select(node => node.Id).ShouldBe(["ra", "rb", "rb", "ra"]);
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "use: circular reference", 2));

        // A use of its own ancestor draws nothing at all.
        Doc("<g id=\"a\"><rect id=\"ra\" width=\"1\" height=\"1\"/><use href=\"#a\"/></g>").Scene.Nodes.ShouldHaveSingleItem();
    }

    // ---- symbol and nested svg ------------------------------------------------------------------------------------

    private const string Symbol = "<defs><symbol id=\"s\" viewBox=\"0 0 20 10\"><rect width=\"20\" height=\"10\"/></symbol></defs>";

    [Theory]
    // The 20 x 10 symbol in a box of its own shape, a square one (fitted and centred), and a square one it is stretched over.
    [InlineData("width=\"60\" height=\"30\"", 4f, 4f, 64f, 34f)]
    [InlineData("width=\"30\" height=\"30\"", 4f, 11.5f, 34f, 26.5f)]
    [InlineData("width=\"30\" height=\"30\" preserveAspectRatio=\"none\"", 4f, 4f, 34f, 34f)]
    public void ASymbol_IsFittedIntoTheBoxItsUseGives(string size, float left, float top, float right, float bottom)
    {
        string symbol = size.Contains("preserve") ? Symbol.Replace("viewBox", "preserveAspectRatio=\"none\" viewBox") : Symbol;
        size = size.Replace(" preserveAspectRatio=\"none\"", "");
        VectorShape shape = OnlyShape(symbol + $"<use href=\"#s\" x=\"4\" y=\"4\" {size}/>");
        Map(shape, 0, 0).ShouldBe(new Vector2(left, top));
        Map(shape, 20, 10).ShouldBe(new Vector2(right, bottom));
    }

    [Fact]
    public void ASymbol_IsNotDrawnWhereItStands_AndWithoutASizeFillsTheViewport()
    {
        Doc(Symbol).Scene.Nodes.ShouldBeEmpty();
        // No width or height on the use: 100% of the 100 x 100 viewport, so the 20 x 10 box is scaled by 5 and centred.
        VectorShape shape = OnlyShape(Symbol + "<use href=\"#s\"/>");
        Map(shape, 0, 0).ShouldBe(new Vector2(0, 25));
        Map(shape, 20, 10).ShouldBe(new Vector2(100, 75));
    }

    [Fact]
    public void ANestedSvg_IsAViewportOfItsOwn_WithItsOwnSizeForPercentages()
    {
        VectorShape shape = OnlyShape(
            "<svg x=\"10\" y=\"5\" width=\"60\" height=\"30\" viewBox=\"0 0 20 10\"><rect x=\"25%\" y=\"50%\" width=\"50%\" height=\"50%\"/></svg>");
        // Percentages are of the viewBox, 20 x 10.
        Describe(shape.Path).ShouldBe("M 5 5 L 15 5 L 15 10 L 5 10 Z");
        Map(shape, 0, 0).ShouldBe(new Vector2(10, 5));
        Map(shape, 20, 10).ShouldBe(new Vector2(70, 35));

        // Without a viewBox the percentages are of its width and height, and without a size it is the whole of its parent.
        Describe(OnlyShape("<svg width=\"40\" height=\"20\"><rect width=\"50%\" height=\"50%\"/></svg>").Path).ShouldBe("M 0 0 L 20 0 L 20 10 L 0 10 Z");
        Describe(OnlyShape("<svg><rect width=\"50%\" height=\"50%\"/></svg>").Path).ShouldBe("M 0 0 L 50 0 L 50 50 L 0 50 Z");
    }

    [Fact]
    public void AUsedSvg_TakesTheSizeTheUseGives()
    {
        const string defs = "<defs><svg id=\"v\" width=\"20\" height=\"20\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg></defs>";
        Map(OnlyShape(defs + "<use href=\"#v\"/>"), 10, 10).ShouldBe(new Vector2(20, 20));
        Map(OnlyShape(defs + "<use href=\"#v\" width=\"50\" height=\"50\"/>"), 10, 10).ShouldBe(new Vector2(50, 50));
    }

    [Theory]
    [InlineData("<svg width=\"0\" height=\"10\"><rect width=\"5\" height=\"5\"/></svg>", null)]
    [InlineData("<svg width=\"10\" height=\"10\" viewBox=\"0 0 0 5\"><rect width=\"5\" height=\"5\"/></svg>", null)]
    [InlineData("<svg width=\"-10\" height=\"10\"><rect width=\"5\" height=\"5\"/></svg>", "width")]
    [InlineData("<svg width=\"10\" height=\"10\" viewBox=\"nope\"><rect width=\"5\" height=\"5\"/></svg>", "viewBox")]
    public void ANestedSvgWithNoSize_DrawsNothing_AndABadOneIsReported(string body, string? invalid)
    {
        SvgDocument document = Doc(body);
        if (invalid is null)
        {
            document.Scene.Nodes.ShouldBeEmpty();
            document.Issues.ShouldBeEmpty();
        }
        else
        {
            document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, invalid, 1));
        }
    }

    [Theory]
    // Inside the viewport: nothing would be clipped, so nothing is said.
    [InlineData("", "<rect x=\"1\" y=\"1\" width=\"18\" height=\"8\"/>", false)]
    [InlineData("", "<rect width=\"20\" height=\"10\"/>", false)]
    // Outside it, by the shape or only by its stroke.
    [InlineData("", "<rect x=\"15\" width=\"10\" height=\"5\"/>", true)]
    [InlineData("", "<rect x=\"1\" y=\"1\" width=\"18\" height=\"8\" stroke=\"red\" stroke-width=\"4\"/>", true)]
    [InlineData("", "<g opacity=\"0.5\"><rect y=\"-3\" width=\"5\" height=\"5\"/><rect width=\"1\" height=\"1\"/></g>", true)]
    // A thin stroke on a round shape that sits inside: its reach is half its width, not the miter limit's worth.
    [InlineData("", "<circle cx=\"10\" cy=\"5\" r=\"4\" fill=\"none\" stroke=\"red\" stroke-width=\"1.5\"/>", false)]
    [InlineData("", "<rect x=\"1\" y=\"1\" width=\"18\" height=\"8\" rx=\"3\" fill=\"none\" stroke=\"red\" stroke-width=\"1.5\"/>", false)]
    [InlineData("", "<path d=\"M2 5 H18\" stroke=\"red\" stroke-width=\"2\" stroke-linecap=\"round\"/>", false)]
    // A right-angled corner's miter reaches 1.414 half-widths, which here is past the edge; bevelled, it is not.
    [InlineData("", "<path d=\"M1.2 9 V1.2 H19\" fill=\"none\" stroke=\"red\" stroke-width=\"2\"/>", true)]
    [InlineData("", "<path d=\"M1.2 9 V1.2 H19\" fill=\"none\" stroke=\"red\" stroke-width=\"2\" stroke-linejoin=\"bevel\"/>", false)]
    // A square cap reaches further than half the width (the reach is taken in every direction, so this is the diagonal).
    [InlineData("", "<path d=\"M1.2 5 H18\" stroke=\"red\" stroke-width=\"2\" stroke-linecap=\"square\"/>", true)]
    [InlineData("", "<path d=\"M1.2 5 H18\" stroke=\"red\" stroke-width=\"2\"/>", false)]
    // overflow says not to clip.
    [InlineData("overflow=\"visible\"", "<rect x=\"15\" width=\"10\" height=\"5\"/>", false)]
    [InlineData("style=\"overflow: auto\"", "<rect x=\"15\" width=\"10\" height=\"5\"/>", false)]
    [InlineData("overflow=\"hidden\"", "<rect x=\"15\" width=\"10\" height=\"5\"/>", true)]
    public void ContentThatLeavesANestedViewport_IsClippedToIt_AndOnlyThenIsThereAClipInTheScene(string overflow, string content, bool clipped)
    {
        foreach (string body in (string[])
        [
            $"<svg x=\"10\" y=\"10\" width=\"20\" height=\"10\" {overflow}>{content}</svg>",
            $"<defs><symbol id=\"s\" {overflow}>{content}</symbol></defs><use href=\"#s\" x=\"10\" y=\"10\" width=\"20\" height=\"10\"/>",
        ])
        {
            SvgDocument document = Doc(body);
            document.Issues.ShouldBeEmpty();
            if (!clipped)
            {
                // Nothing can reach outside, so the scene stays as flat as it was: no layer for a renderer to make.
                document.Scene.Nodes.ShouldNotBeEmpty();
                Dump(document.Scene).ShouldNotContain("clip");
                continue;
            }
            // Everything the viewport drew is in one group, kept to the viewport's rectangle in the picture.
            VectorGroup group = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
            group.Opacity.ShouldBe(1f);
            group.Mask.ShouldBeNull();
            VectorClipShape region = group.Clip.ShouldNotBeNull().Shapes.ShouldHaveSingleItem();
            group.Clip.Intersect.ShouldBeNull();
            Flatten(region.Path, region.Transform).ShouldHaveSingleItem().ShouldBe([new(10, 10), new(30, 10), new(30, 20), new(10, 20)]);
        }
    }

    // ---- What a document may ask for --------------------------------------------------------------------------------

    /// <summary>Level 0 is <paramref name="leaf"/>; every level above is a group that uses the one below twice.</summary>
    private static string Doubling(int levels, string leaf)
    {
        var body = new StringBuilder($"<defs><g id=\"l0\">{leaf}</g>");
        for (int i = 1; i <= levels; i++)
        {
            body.Append($"<g id=\"l{i}\"><use href=\"#l{i - 1}\"/><use href=\"#l{i - 1}\" x=\"1\"/></g>");
        }
        return body.Append($"</defs><use href=\"#l{levels}\"/>").ToString();
    }

    [Fact]
    public void ADoublingChainOfUses_IsCountedByWhatItDraws_NotByWhatItHolds()
    {
        // 5 levels: 32 rectangles from a document of 20 elements.
        Doc(Doubling(5, "<rect width=\"1\" height=\"1\"/>")).Scene.Nodes.Length.ShouldBe(32);

        // 40 levels would be 2^40 rectangles, from about 3 KB. It is refused once the count passes the limit...
        string bomb = Doubling(40, "<rect width=\"1\" height=\"1\"/>");
        bomb.Length.ShouldBeLessThan(4096);
        Should.Throw<InvalidDataException>(() => Doc(bomb, options: new SvgReadOptions { MaxElements = 20_000 }))
            .Message.ShouldContain("draws more than 20000 elements");
        // ... and an empty leaf is no way round it: nothing would be drawn, and the walk would still take for ever.
        Should.Throw<InvalidDataException>(() => Doc(Doubling(40, ""), options: new SvgReadOptions { MaxElements = 20_000 }))
            .Message.ShouldContain("draws more than 20000 elements");
    }

    [Fact]
    public void TheDefaultLimits_StopADoublingChain_InGoodTime()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<InvalidDataException>(() => Doc(Doubling(40, "<rect width=\"1\" height=\"1\"/>")));
        // Measured at well under a second; the bound only says it is not minutes.
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void PathVerbs_AreCountedForEveryInstance()
    {
        // 3 verbs, used 4 times.
        const string body = "<defs><path id=\"p\" d=\"M0 0 L1 1 L2 0\" stroke=\"red\"/></defs><use href=\"#p\"/><use href=\"#p\"/><use href=\"#p\"/><use href=\"#p\"/>";
        Doc(body, options: new SvgReadOptions { MaxPathVerbs = 12 }).Scene.Nodes.Length.ShouldBe(4);
        Should.Throw<InvalidDataException>(() => Doc(body, options: new SvgReadOptions { MaxPathVerbs = 11 })).Message.ShouldContain("more than 11 segments");
    }

    [Fact]
    public void AChainOfUses_CannotNestDeeperThanTheDepthLimit()
    {
        // Flat in the document (depth 3), but each group uses the one before: followed, they nest 2 deep a link.
        var body = new StringBuilder("<defs><g id=\"c0\"><rect width=\"1\" height=\"1\"/></g>");
        for (int i = 1; i <= 200; i++) body.Append($"<g id=\"c{i}\"><use href=\"#c{i - 1}\"/></g>");
        string chain = body.Append("</defs><use href=\"#c200\"/>").ToString();

        Should.Throw<InvalidDataException>(() => Doc(chain)).Message.ShouldContain("more than 256 deep once its use elements are followed");
        Doc(chain, options: new SvgReadOptions { MaxDepth = 1000 }).Scene.Nodes.ShouldHaveSingleItem();
    }
}
