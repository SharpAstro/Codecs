using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using Xunit;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// What a document takes from its surroundings, which is what a font needs to draw the glyphs of an OpenType
/// <c>SVG </c> table: one element drawn out of a document that holds several, the text colour as <c>currentColor</c>,
/// and the palette as CSS custom properties read with <c>var()</c>. Custom properties are read for every document,
/// whoever declares them. The pixels of <c>var()</c> are held against the browser in <see cref="SvgBrowserOracleTests"/>.
/// </summary>
public sealed class SvgRung4Tests
{
    private const string XLink = "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    private static Rgba32 Fill(SvgDocument document, int node = 0) => FillColor(document.Scene.Nodes[node].ShouldBeOfType<VectorShape>());

    private static SvgReadOptions Palette(params (string Name, string Value)[] entries) =>
        new() { CustomProperties = entries.ToDictionary(e => e.Name, e => e.Value) };

    // ---- var() and custom properties ------------------------------------------------------------------------------

    [Fact]
    public void AVar_TakesTheValueTheSurroundingsGive_InAnAttributeAStyleAndASheet()
    {
        SvgDocument document = Doc(
            "<style>.c { fill: var(--color2) }</style>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--color0, yellow)\"/>" +
            "<rect width=\"1\" height=\"1\" style=\"fill: var( --color1 )\"/>" +
            "<rect width=\"1\" height=\"1\" class=\"c\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"none\" stroke=\"VAR(--color0)\"/>",
            options: Palette(("--color0", "#102030"), ("--color1", "rgb(1, 2, 3)"), ("--color2", "red")));
        document.Issues.ShouldBeEmpty();
        Fill(document, 0).ShouldBe(new Rgba32(0x10, 0x20, 0x30));
        Fill(document, 1).ShouldBe(new Rgba32(1, 2, 3));
        Fill(document, 2).ShouldBe(new Rgba32(255, 0, 0));
        StrokeColor(document.Scene.Nodes[3].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0x10, 0x20, 0x30));
    }

    [Fact]
    public void AVarWithNoValue_TakesItsFallback_WhichMayHoldCommasAndAnotherVar()
    {
        SvgDocument document = Doc(
            "<rect width=\"1\" height=\"1\" fill=\"var(--color0, yellow)\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--color0, rgb(4, 5, 6))\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--color0, var(--color1, var(--color2)))\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--color0,)\" stroke=\"blue\"/>",
            options: Palette(("--color2", "#00f")));
        Fill(document, 0).ShouldBe(new Rgba32(255, 255, 0));
        Fill(document, 1).ShouldBe(new Rgba32(4, 5, 6));
        Fill(document, 2).ShouldBe(new Rgba32(0, 0, 255));
        // An empty fallback is a value: an empty one, which is not a paint. The fill is then as if it were not written.
        Fill(document, 3).ShouldBe(new Rgba32(0, 0, 0));
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "fill", 1));
    }

    [Fact]
    public void AVarWithNoValueAndNoFallback_LeavesTheInheritedValue_NotTheDeclarationItBeat()
    {
        // The style attribute beats the presentation attribute and then cannot be used: the fill is the group's, not red.
        SvgDocument document = Doc(
            "<g fill=\"#0a0\" opacity=\"0.5\"><rect width=\"1\" height=\"1\" fill=\"red\" style=\"fill: var(--nobody)\"/>" +
            // Opacity does not inherit: unusable, it is 1, and no layer is made for this shape.
            "<rect width=\"1\" height=\"1\" opacity=\"0.2\" style=\"opacity: var(--nobody)\"/>" +
            // Not var() at all, but a name that ends in it: a value nobody can read from the start, which IS as if it
            // were not written, so here the attribute stands.
            "<rect width=\"1\" height=\"1\" fill=\"red\" style=\"fill: lvar(--nobody)\"/></g>");
        VectorGroup group = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Opacity.ShouldBe(0.5f);
        FillColor(group.Children[0].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0, 0xAA, 0));
        FillColor(group.Children[1].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0, 0xAA, 0));
        FillColor(group.Children[2].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(255, 0, 0));
        document.Issues.ShouldBe(
        [
            new SvgIssue(SvgIssueKind.Invalid, "var() with no value and no fallback", 2),
            new SvgIssue(SvgIssueKind.Invalid, "fill", 1),
        ]);
    }

    [Fact]
    public void GeometryWrittenAsCss_IsNotRead_AndIsReported()
    {
        // SVG 2 lets a sheet set these. They are read from attributes only, and saying nothing would be a guess.
        SvgDocument document = Doc(
            "<style>circle { r: 4px }</style>" +
            "<rect width=\"5\" height=\"5\" style=\"width: 21px; x: 3\"/><circle cx=\"9\" cy=\"9\" r=\"2\"/>" +
            "<rect width=\"5\" height=\"5\" style=\"width: auto\"/>");
        Describe(document.Scene.Nodes[0].ShouldBeOfType<VectorShape>().Path).ShouldBe("M 0 0 L 5 0 L 5 5 L 0 5 Z");
        document.Issues.ShouldBe(
        [
            new SvgIssue(SvgIssueKind.Unsupported, "r as a CSS property", 1),
            new SvgIssue(SvgIssueKind.Unsupported, "width as a CSS property", 1),
            new SvgIssue(SvgIssueKind.Unsupported, "x as a CSS property", 1),
        ], ignoreOrder: true);
        // As attributes they are what they always were, and nothing is said.
        Doc("<rect x=\"3\" y=\"1\" width=\"5\" height=\"5\" rx=\"1\"/><circle cx=\"9\" cy=\"9\" r=\"2\"/>").Issues.ShouldBeEmpty();
    }

    [Fact]
    public void TheDocumentsOwnCustomProperties_Inherit_AndWinOverTheSurroundings()
    {
        SvgDocument document = Doc(
            "<style>g.t { --ink: #010203; --Ink: #040506 }</style>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--ink)\"/>" +                                  // the surroundings'
            "<g class=\"t\"><rect width=\"1\" height=\"1\" fill=\"var(--ink)\"/>" +                   // the group's
            "<rect width=\"1\" height=\"1\" fill=\"var(--Ink)\"/>" +                                  // names keep their case
            "<rect width=\"1\" height=\"1\" style=\"--ink: blue\" fill=\"var(--ink)\"/>" +           // its own, though the attribute is read first
            "<g style=\"--ink: lime\"><rect width=\"1\" height=\"1\" fill=\"var(--ink)\"/></g></g>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--ink)\"/>",                                   // and outside again
            options: Palette(("--ink", "red")));
        document.Issues.ShouldBeEmpty();
        Fill(document, 0).ShouldBe(new Rgba32(255, 0, 0));
        Fill(document, 1).ShouldBe(new Rgba32(1, 2, 3));
        Fill(document, 2).ShouldBe(new Rgba32(4, 5, 6));
        Fill(document, 3).ShouldBe(new Rgba32(0, 0, 255));
        Fill(document, 4).ShouldBe(new Rgba32(0, 255, 0));
        Fill(document, 5).ShouldBe(new Rgba32(255, 0, 0));
    }

    [Fact]
    public void ACustomProperty_MayUseAnother_InEitherOrder_AndOneThatLeadsBackToItselfIsNotDeclared()
    {
        SvgDocument document = Doc(
            "<g style=\"--a: var(--b); --b: var(--c, teal); --loop1: var(--loop2); --loop2: var(--loop1); --self: var(--self)\">" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--a)\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--loop1, navy)\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"var(--self, navy)\"/>" +
            // The parent's value is what a child's declaration sees, not the child's own.
            "<g style=\"--b: red; --d: var(--a)\"><rect width=\"1\" height=\"1\" fill=\"var(--d)\"/></g></g>",
            // A circle hides what the surroundings gave under that name too.
            options: Palette(("--loop1", "red"), ("--self", "red")));
        document.Issues.ShouldBeEmpty();
        Fill(document, 0).ShouldBe(new Rgba32(0, 128, 128));
        Fill(document, 1).ShouldBe(new Rgba32(0, 0, 128));
        Fill(document, 2).ShouldBe(new Rgba32(0, 0, 128));
        Fill(document, 3).ShouldBe(new Rgba32(0, 128, 128));
    }

    [Fact]
    public void AGradientStop_ReadsVarAndCurrentColor_FromWhereItStandsInTheDocument()
    {
        SvgDocument document = Doc(
            "<defs style=\"--mid: #00ff00; color: #000080\"><linearGradient id=\"g\">" +
            "<stop offset=\"0\" stop-color=\"var(--color0, darkblue)\"/>" +
            "<stop offset=\"0.3\" stop-color=\"var(--mid)\" stop-opacity=\"var(--half)\"/>" +
            "<stop offset=\"0.6\" stop-color=\"currentColor\"/>" +
            "<stop offset=\"1\" style=\"color: #fff\" stop-color=\"currentColor\"/></linearGradient></defs>" +
            // The shape's own colour and properties are not the stop's.
            "<rect width=\"10\" height=\"10\" fill=\"url(#g)\" style=\"--mid: red; color: red\"/>",
            options: new SvgReadOptions
            {
                CurrentColor = new Rgba32(9, 9, 9),
                CustomProperties = new Dictionary<string, string> { ["--color0"] = "#ff000080", ["--half"] = "0.5" },
            });
        document.Issues.ShouldBeEmpty();
        var paint = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Fill.ShouldNotBeNull().Paint.ShouldBeOfType<LinearGradientPaint>();
        paint.Stops.Select(stop => stop.Color).ShouldBe(
        [
            new Rgba32(255, 0, 0, 128),          // a palette entry's alpha comes with it
            new Rgba32(0, 255, 0, 128),
            new Rgba32(0, 0, 128),
            new Rgba32(255, 255, 255),
        ]);
    }

    [Fact]
    public void WhatVarPutsIntoValues_IsBounded_HoweverLittleInputAsksForIt()
    {
        // Each level holds the one above twice: 2^40 characters from forty short groups.
        // (A property written in terms of itself would be a circle, so each level has a name of its own.)
        var body = new System.Text.StringBuilder("<g style=\"--v0: aaaaaaaa\">");
        for (int i = 1; i <= 40; i++) body.Append($"<g style=\"--v{i}: var(--v{i - 1}) var(--v{i - 1})\">");
        body.Append("<rect width=\"1\" height=\"1\" fill=\"var(--v40)\"/>").Insert(body.Length, "</g>", 41);
        body.Length.ShouldBeLessThan(3000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<InvalidDataException>(() => Doc(body.ToString(), options: new SvgReadOptions { MaxCharacters = 1 << 16 }))
            .Message.ShouldContain("var()");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    // ---- currentColor from the surroundings -----------------------------------------------------------------------

    [Fact]
    public void CurrentColor_IsTheColourTheSurroundingsGive_UntilTheDocumentSetsOne()
    {
        var options = new SvgReadOptions { CurrentColor = new Rgba32(200, 10, 20) };
        SvgDocument document = Doc(
            "<rect width=\"1\" height=\"1\" fill=\"currentColor\"/>" +
            "<g color=\"#00f\"><rect width=\"1\" height=\"1\" fill=\"currentColor\"/></g>" +
            "<rect width=\"1\" height=\"1\"/>", options: options);
        Fill(document, 0).ShouldBe(new Rgba32(200, 10, 20));
        Fill(document, 1).ShouldBe(new Rgba32(0, 0, 255));
        Fill(document, 2).ShouldBe(new Rgba32(0, 0, 0));        // the initial fill is black, not the current colour
        Fill(Doc("<rect width=\"1\" height=\"1\" fill=\"currentColor\"/>")).ShouldBe(new Rgba32(0, 0, 0));
    }

    // ---- One element of a document --------------------------------------------------------------------------------

    /// <summary>The OpenType specification's Example 4, with a second colour so that the glyphs differ in more than shape.</summary>
    private const string Glyphs =
        "<defs><linearGradient id=\"grad\" x1=\"0%\" y1=\"0%\" x2=\"0%\" y2=\"100%\">" +
        "<stop offset=\"0%\" stop-color=\"darkblue\"/><stop offset=\"100%\" stop-color=\"#00aab3\"/></linearGradient>" +
        "<g id=\"i-base\"><rect x=\"100\" y=\"570\" width=\"200\" height=\"430\" fill=\"url(#grad)\"/></g></defs>" +
        "<g id=\"glyph2\" transform=\"translate(0,-1000)\"><use xlink:href=\"#i-base\"/></g>" +
        "<g id=\"glyph13\" transform=\"translate(0,-1000)\"><use xlink:href=\"#i-base\"/><rect x=\"100\" y=\"365\" width=\"200\" height=\"135\" fill=\"darkblue\"/></g>" +
        "<g id=\"glyph14\" transform=\"translate(0,-1000)\"><use xlink:href=\"#i-base\"/><polygon fill=\"currentColor\" points=\"120,500 280,500 435,342 208,342\"/></g>";

    [Theory]
    [InlineData("glyph2", 1)]
    [InlineData("glyph13", 2)]
    [InlineData("glyph14", 2)]
    public void OneElement_IsDrawnAsAUseOfIt_StandingAloneInTheRoot(string id, int shapes)
    {
        string root = $"version=\"1.1\" {XLink}";
        var options = new SvgReadOptions { ElementId = id, ViewportWidth = 1000, ViewportHeight = 1000, CurrentColor = new Rgba32(7, 8, 9) };
        SvgDocument one = Doc(Glyphs, root, options);
        one.Issues.ShouldBeEmpty();
        one.Scene.Nodes.Length.ShouldBe(shapes);

        // The specification says what the picture is by giving this document: everything in defs, and one use.
        SvgDocument asTheSpecificationPutsIt = Doc($"<defs>{Glyphs}</defs><use xlink:href=\"#{id}\"/>", root, options with { ElementId = null });
        Dump(one.Scene).ShouldBe(Dump(asTheSpecificationPutsIt.Scene));

        // And it is where the glyph should be: above the baseline, which is y = 0.
        one.Scene.Nodes[0].ShouldBeOfType<VectorShape>().Path.TryGetBounds(out VectorRect box).ShouldBeTrue();
        VectorShape stem = one.Scene.Nodes[0].ShouldBeOfType<VectorShape>();
        Map(stem, box.X, box.Y).ShouldBe(new System.Numerics.Vector2(100, -430));
        Map(stem, box.Right, box.Bottom).ShouldBe(new System.Numerics.Vector2(300, 0));
        if (id == "glyph14") Fill(one, 1).ShouldBe(new Rgba32(7, 8, 9));
    }

    [Fact]
    public void OneElement_TakesNothingFromWhatIsAroundIt_ButTheRoot()
    {
        // The group around the glyph moves and colours what is in it. A use of the glyph is not in that group.
        const string body =
            "<g transform=\"translate(500 500)\" fill=\"red\" opacity=\"0.5\">" +
            "<rect id=\"glyph1\" width=\"10\" height=\"10\" transform=\"translate(1 2)\"/><rect id=\"other\" width=\"99\" height=\"99\"/></g>" +
            "<rect width=\"50\" height=\"50\"/>";
        SvgDocument one = Doc(body, "fill=\"#00f\" viewBox=\"0 0 50 50\" width=\"100\" height=\"100\"", new SvgReadOptions { ElementId = "glyph1" });
        VectorShape shape = one.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        FillColor(shape).ShouldBe(new Rgba32(0, 0, 255));                           // the root's fill, inherited
        Map(shape, 0, 0).ShouldBe(new System.Numerics.Vector2(2, 4));               // its own transform, inside the root's viewBox mapping
        Map(shape, 10, 10).ShouldBe(new System.Numerics.Vector2(22, 24));
        one.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void TheRootAskedForByItsId_IsTheWholeDocument()
    {
        // The specification's Example 2: the glyph is the svg element itself.
        const string svg = $"<svg id=\"glyph7\" version=\"1.1\" xmlns=\"{Namespace}\"><rect x=\"100\" y=\"-430\" width=\"200\" height=\"430\"/>" +
            "<rect x=\"100\" y=\"-635\" width=\"200\" height=\"135\" fill=\"darkblue\"/></svg>";
        var options = new SvgReadOptions { ViewportWidth = 1000, ViewportHeight = 1000 };
        Dump(SvgReader.Parse(svg, options with { ElementId = "glyph7" }).Scene).ShouldBe(Dump(SvgReader.Parse(svg, options).Scene));
        SvgReader.Parse(svg, options with { ElementId = "glyph7" }).Scene.Nodes.Length.ShouldBe(2);
    }

    [Fact]
    public void AnIdNoElementHas_IsReported_AndNothingIsDrawn()
    {
        SvgDocument document = Doc(Glyphs, XLink, new SvgReadOptions { ElementId = "glyph99" });
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "no element with the id asked for", 1));
    }

    [Fact]
    public void OneElement_IsChargedAndLimitedLikeAnyUse()
    {
        // The glyph is forty levels of doubling: the count of what is drawn stops it, as it does for the whole document.
        var body = new System.Text.StringBuilder("<defs><rect id=\"u0\" width=\"1\" height=\"1\"/>");
        for (int i = 1; i <= 40; i++) body.Append($"<g id=\"u{i}\"><use href=\"#u{i - 1}\"/><use href=\"#u{i - 1}\"/></g>");
        body.Append("</defs>");
        Should.Throw<InvalidDataException>(() => Doc(body.ToString(), options: new SvgReadOptions { ElementId = "u40", MaxElements = 10_000 }));
        // A symbol asked for is drawn, as a use of it is, in its own viewport: it is the use that draws, not the place.
        const string sprite = "<symbol id=\"s\" viewBox=\"0 0 10 10\"><rect x=\"2\" y=\"2\" width=\"6\" height=\"6\"/></symbol>";
        SvgDocument symbol = Doc(sprite, options: new SvgReadOptions { ElementId = "s" });
        symbol.Scene.Nodes.ShouldHaveSingleItem();
        Dump(symbol.Scene).ShouldBe(Dump(Doc(sprite + "<use href=\"#s\"/>").Scene));
        // An element that is not drawn where it stands is not drawn by asking for it either.
        Doc("<defs><linearGradient id=\"g\"/></defs>", options: new SvgReadOptions { ElementId = "g" }).Scene.Nodes.ShouldBeEmpty();
        Doc("<rect id=\"r\" width=\"1\" height=\"1\" display=\"none\"/>", options: new SvgReadOptions { ElementId = "r" }).Scene.Nodes.ShouldBeEmpty();
    }
}
