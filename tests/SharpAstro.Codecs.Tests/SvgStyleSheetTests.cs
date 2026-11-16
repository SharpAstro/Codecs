using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>The <c>style</c> element: which selectors match what, which declaration wins, and what is reported.</summary>
public sealed class SvgStyleSheetTests
{
    /// <summary>The fill of the element with <c>id="t"</c>, as <c>r,g,b</c>, under a sheet.</summary>
    private static string Fill(string css, string body, out SvgDocument document)
    {
        document = Doc($"<style>{css}</style>{body}");
        VectorShape target = Find(document.Scene.Nodes, "t").ShouldNotBeNull();
        Rgba32 c = FillColor(target);
        return $"{c.R},{c.G},{c.B}";
    }

    private static string Fill(string css, string body) => Fill(css, body, out _);

    private static VectorShape? Find(IEnumerable<VectorNode> nodes, string id)
    {
        foreach (VectorNode node in nodes)
        {
            if (node is VectorShape shape && shape.Id == id) return shape;
            if (node is VectorGroup group && Find(group.Children, id) is { } inside) return inside;
        }
        return null;
    }

    private const string Red = "255,0,0", Black = "0,0,0";
    private const string T = "<rect id=\"t\" width=\"1\" height=\"1\"";

    [Theory]
    // Matches.
    [InlineData("rect", T + "/>")]
    [InlineData("*", T + "/>")]
    [InlineData("#t", T + "/>")]
    [InlineData(".a", T + " class=\"a\"/>")]
    [InlineData(".a", T + " class=\"  b\ta \"/>")]
    [InlineData(".a.b", T + " class=\"b a\"/>")]
    [InlineData("rect.a#t", T + " class=\"a\"/>")]
    [InlineData("[width]", T + "/>")]
    [InlineData("[width=\"1\"]", T + "/>")]
    [InlineData("[width=1]", T + "/>")]
    [InlineData("[data-x~=two]", T + " data-x=\"one two three\"/>")]
    [InlineData("[data-x|=en]", T + " data-x=\"en-GB\"/>")]
    [InlineData("[data-x^=ab]", T + " data-x=\"abc\"/>")]
    [InlineData("[data-x$=bc]", T + " data-x=\"abc\"/>")]
    [InlineData("[data-x*=b]", T + " data-x=\"abc\"/>")]
    [InlineData("g rect", "<g><g>" + T + "/></g></g>")]
    [InlineData("svg rect", T + "/>")]
    [InlineData("g > rect", "<g>" + T + "/></g>")]
    [InlineData("g>rect", "<g>" + T + "/></g>")]
    [InlineData(".x g > rect", "<g class=\"x\"><g><g>" + T + "/></g></g></g>")]
    [InlineData("circle + rect", "<circle r=\"1\"/>" + T + "/>")]
    [InlineData("circle ~ rect", "<circle r=\"1\"/><path/>" + T + "/>")]
    [InlineData("rect:first-child", "<g>" + T + "/><path/></g>")]
    [InlineData("rect:last-child", "<g><path/>" + T + "/></g>")]
    [InlineData("rect:only-child", "<g>" + T + "/></g>")]
    [InlineData(":root > rect", T + "/>")]
    [InlineData("a:link rect", "<a href=\"x\">" + T + "/></a>")]
    [InlineData("path, rect , circle", T + "/>")]
    public void ASelectorThatMatches_AppliesItsDeclarations(string selector, string body)
    {
        Fill($"{selector} {{ fill: red }}", body, out SvgDocument document).ShouldBe(Red);
        document.Issues.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("circle", T + "/>")]
    [InlineData("RECT", T + "/>")]                                       // element names are case-sensitive in XML
    [InlineData("#T", T + "/>")]
    [InlineData(".a", T + " class=\"ab\"/>")]
    [InlineData(".a.b", T + " class=\"a\"/>")]
    [InlineData("[data-none]", T + "/>")]
    [InlineData("[width=\"2\"]", T + "/>")]
    [InlineData("[data-x~=tw]", T + " data-x=\"one two\"/>")]
    [InlineData("[data-x|=en]", T + " data-x=\"english\"/>")]
    [InlineData("a > rect", "<a><g>" + T + "/></g></a>")]
    [InlineData("svg > g > rect", "<g><a>" + T + "/></a></g>")]
    [InlineData("circle + rect", "<circle r=\"1\"/><path/>" + T + "/>")]
    [InlineData("rect ~ circle", "<circle r=\"1\"/>" + T + "/>")]
    [InlineData("circle ~ rect", T + "/><circle r=\"1\"/>")]
    [InlineData("rect:first-child", "<g><path/>" + T + "/></g>")]
    [InlineData("rect:last-child", "<g>" + T + "/><path/></g>")]
    [InlineData("rect:only-child", "<g>" + T + "/><path/></g>")]
    [InlineData("rect:root", T + "/>")]
    [InlineData("a:link rect", "<a>" + T + "/></a>")]
    // In a still picture nothing is hovered, focused or visited.
    [InlineData("rect:hover", T + "/>")]
    [InlineData("rect:focus", T + "/>")]
    [InlineData("a:visited rect", "<a href=\"x\">" + T + "/></a>")]
    public void ASelectorThatDoesNotMatch_ChangesNothing(string selector, string body)
    {
        Fill($"{selector} {{ fill: red }}", body, out SvgDocument document).ShouldBe(Black);
        document.Issues.ShouldBeEmpty();
    }

    [Theory]
    // Later beats earlier at the same specificity; higher specificity beats later.
    [InlineData("rect { fill: lime } rect { fill: red }", "")]
    [InlineData("#t { fill: red } rect { fill: lime }", "")]
    [InlineData(".a { fill: red } rect { fill: lime }", "class=\"a\"")]
    [InlineData("rect.a { fill: red } .a { fill: lime }", "class=\"a\"")]
    [InlineData("g rect { fill: red } rect { fill: lime }", "")]
    [InlineData("#t { fill: red } .a.b.c.d.e.f.g.h.i.j.k { fill: lime }", "class=\"a b c d e f g h i j k\"")]
    // A sheet beats a presentation attribute, whatever its specificity; the style attribute beats the sheet.
    [InlineData("* { fill: red }", "fill=\"lime\"")]
    [InlineData("#t { fill: lime }", "style=\"fill: red\"")]
    // !important beats what would otherwise win: in a sheet over the style attribute, in the style attribute over that.
    [InlineData("rect { fill: red !important } #t { fill: lime }", "")]
    [InlineData("rect { fill: red ! IMPORTANT }", "style=\"fill: lime\"")]
    [InlineData("rect { fill: lime !important }", "style=\"fill: red !important\"")]
    [InlineData("rect { fill: lime !important; fill: red !important }", "")]
    // Only the properties a rule sets are touched, and an invalid value leaves the one before it standing.
    [InlineData("rect { fill: red } #t { stroke: blue }", "")]
    [InlineData("rect { fill: red } #t { fill: notacolour }", "")]
    public void TheCascade_PicksTheWinner(string css, string attributes)
    {
        Fill(css, $"<g><rect id=\"t\" width=\"1\" height=\"1\" {attributes}/></g>").ShouldBe(Red);
    }

    [Fact]
    public void WithoutASheet_ImportantInAStyleAttribute_StillWins()
    {
        FillColor(OnlyShape("<rect width=\"1\" height=\"1\" style=\"fill: red !important; fill: lime\"/>")).ShouldBe(new Rgba32(255, 0, 0));
    }

    [Fact]
    public void SheetsAreReadInDocumentOrder_WhereverTheyStand_AndApplyToTheWholeDocument()
    {
        SvgDocument document = Doc(
            "<rect id=\"t\" width=\"1\" height=\"1\"/>" +
            "<style type=\"text/css\"><![CDATA[ rect { fill: lime; stroke-width: 3 } ]]></style>" +
            // The old habit of wrapping a sheet in <!-- -->: inside CDATA those are CSS tokens that mean nothing. (Outside
            // CDATA they are an XML comment, and the rule really is gone, in a browser too.)
            "<defs><style><![CDATA[\n<!--\n rect { fill: red } /* later, so it wins */\n-->\n]]></style></defs>" +
            "<style><!-- rect { fill: blue } --></style>" +
            "<style type=\"text/x-other\">rect { fill: blue }</style>" +
            "<style media=\"print\">rect { fill: blue }</style>" +
            "<style media=\"screen, print\">rect { stroke: #00f }</style>");
        VectorShape shape = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        FillColor(shape).ShouldBe(new Rgba32(255, 0, 0));
        StrokeColor(shape).ShouldBe(new Rgba32(0, 0, 255));
        shape.Stroke!.Width.ShouldBe(3f);
        document.Issues.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("@media screen { rect { fill: red } }", Red, null)]
    [InlineData("@media all { rect { fill: red } }", Red, null)]
    [InlineData("@media print, only screen { rect { fill: red } }", Red, null)]
    [InlineData("@media print { rect { fill: red } }", Black, null)]
    [InlineData("@media screen { @media print { rect { fill: red } } }", Black, null)]
    [InlineData("@media (min-width: 100px) { rect { fill: red } }", Black, "@media rule with a condition")]
    [InlineData("@media screen and (color) { rect { fill: red } }", Black, "@media rule with a condition")]
    [InlineData("@import url(other.css); rect { fill: red }", Red, "@import rule")]
    [InlineData("@supports (fill: red) { rect { fill: lime } } rect { fill: red }", Red, "@supports rule")]
    // What cannot change the picture is passed over without a word.
    [InlineData("@charset \"utf-8\"; @font-face { font-family: x; src: url(x.woff) } @namespace svg url(http://www.w3.org/2000/svg); rect { fill: red }", Red, null)]
    public void AtRules_AreDecidedSkippedOrReported(string css, string expected, string? reported)
    {
        Fill(css, T + "/>", out SvgDocument document).ShouldBe(expected);
        if (reported is null) document.Issues.ShouldBeEmpty();
        else document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Unsupported, reported, 1));
    }

    [Theory]
    [InlineData("rect:not(.a)")]
    [InlineData("rect:nth-child(1)")]
    [InlineData("rect::before")]
    [InlineData("svg|rect")]
    [InlineData("rect:unknown")]
    [InlineData("rect >")]
    [InlineData("[width=]")]
    [InlineData("rect..a")]
    public void ASelectorThatIsNotRead_IsLeftOut_AndReported_AndTheOthersInItsRuleStillApply(string selector)
    {
        Fill($"{selector} {{ fill: lime }}", T + "/>", out SvgDocument document).ShouldBe(Black);
        document.Issues.ShouldHaveSingleItem().Kind.ShouldBe(SvgIssueKind.Unsupported);
        document.Issues[0].What.ShouldBe($"CSS selector '{selector}'");

        Fill($"{selector}, #t {{ fill: red }}", T + "/>").ShouldBe(Red);
    }

    [Fact]
    public void OddCss_IsReadAsFarAsItGoes()
    {
        // No closing brace, a declaration with no value, strings with braces and semicolons in them, an empty rule.
        Fill("path {} circle { fill: lime; } rect { ; fill ; stroke: none; fill: red", T + "/>").ShouldBe(Red);
        Fill("[data-x=\"a;b}c\"] { fill: red }", T + " data-x=\"a;b}c\"/>").ShouldBe(Red);
        Fill("", T + "/>").ShouldBe(Black);
        Fill("   /* only a comment */ ", T + "/>").ShouldBe(Black);
        Fill("rect { fill: red } /* unterminated", T + "/>").ShouldBe(Red);
    }

    [Fact]
    public void SelectorsSeeAnElementWhereItStands_AndInheritanceComesFromTheUse()
    {
        // The rect is in a g in defs: "g rect" matches it there, although where it is drawn it is under no g. What it
        // inherits (the stroke) comes from the use that draws it.
        SvgDocument document = Doc(
            "<style>g rect { fill: red } use rect { fill: lime }</style>" +
            "<defs><g><rect id=\"t\" width=\"1\" height=\"1\"/></g></defs><use href=\"#t\" stroke=\"blue\"/>");
        VectorShape shape = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        FillColor(shape).ShouldBe(new Rgba32(255, 0, 0));
        StrokeColor(shape).ShouldBe(new Rgba32(0, 0, 255));
    }

    [Fact]
    public void ApplyingASheet_HasABudget_BecauseItsCostIsElementsTimesRules()
    {
        // 200 rules and 200 rectangles: 40,000 tests (and a few for the root).
        string css = string.Concat(Enumerable.Repeat("rect { stroke-width: 2 } ", 200));
        string body = string.Concat(Enumerable.Repeat("<rect width=\"1\" height=\"1\"/>", 200));
        Doc($"<style>{css}</style>{body}", options: new SvgReadOptions { MaxSelectorTests = 50_000 }).Scene.Nodes.Length.ShouldBe(200);
        Should.Throw<InvalidDataException>(() => Doc($"<style>{css}</style>{body}", options: new SvgReadOptions { MaxSelectorTests = 30_000 }))
            .Message.ShouldContain("more than 30000 selector tests");

        // A descendant selector that cannot match has to try every choice of ancestors before it knows: here about
        // 60 choose 4, half a million, for ONE element and ONE rule. Every one of those tests is charged.
        string deep = string.Concat(Enumerable.Repeat("<g>", 60)) + "<rect width=\"1\" height=\"1\"/>" + string.Concat(Enumerable.Repeat("</g>", 60));
        const string costly = "<style>path g g g g rect { fill: red }</style>";
        Should.Throw<InvalidDataException>(() => Doc(costly + deep, options: new SvgReadOptions { MaxSelectorTests = 100_000 }));
        Doc(costly + deep, options: new SvgReadOptions { MaxSelectorTests = 10_000_000 }).Scene.Nodes.ShouldHaveSingleItem();
    }

    [Fact]
    public void AnElementUsedManyTimes_IsMatchedAgainstTheSheetOnce()
    {
        string uses = string.Concat(Enumerable.Repeat("<use href=\"#t\"/>", 500));
        string css = string.Concat(Enumerable.Repeat("rect { fill: red } ", 50));
        // 502 elements stand in the document; were each instance matched again, 500 x 50 more tests would be needed.
        Doc($"<style>{css}</style><defs><rect id=\"t\" width=\"1\" height=\"1\"/></defs>{uses}", options: new SvgReadOptions { MaxSelectorTests = 26_000 + 500 })
            .Scene.Nodes.Length.ShouldBe(500);
    }
}
