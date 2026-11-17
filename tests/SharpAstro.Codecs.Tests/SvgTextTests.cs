using System.Numerics;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using Xunit;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Text read into the scene: what is written, in which font, and where, as the document says it. There is no font in
/// this repository, so nothing here is laid out or drawn: these tests hold what the reader hands on, and Fonts.Lib's
/// tests hold the drawing against a browser.
/// </summary>
public sealed class SvgTextTests
{
    private static VectorText Text(string body, SvgReadOptions? options = null)
    {
        SvgDocument document = Doc(body, options: options);
        document.Issues.ShouldBeEmpty();
        return document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorText>();
    }

    private static string[] Strings(VectorText text) => [.. text.Spans.Select(span => span.Text)];

    // ---- A text element ---------------------------------------------------------------------------------------------

    [Fact]
    public void ATextElement_IsOneNode_WithItsCharactersItsFontAndWhereItStarts()
    {
        VectorText text = Text("<text id=\"t\" x=\"10\" y=\"20\" transform=\"translate(5 6) scale(2)\">Hello</text>");
        text.Id.ShouldBe("t");
        text.Transform.ShouldBe(new Matrix3x2(2, 0, 0, 2, 5, 6));
        text.TextLength.ShouldBeNull();

        VectorTextSpan span = text.Spans.ShouldHaveSingleItem();
        span.Text.ShouldBe("Hello");
        span.Font.ShouldBe(new VectorFont([], 16));                    // the initial font: no family named, 16 px, regular
        (span.Font.Weight, span.Font.Slant, span.Font.Stretch).ShouldBe((400, FontSlant.Normal, 100f));
        span.Fill.ShouldNotBeNull().Paint.ShouldBe(new SolidPaint(new Rgba32(0, 0, 0)));
        span.Stroke.ShouldBeNull();
        (span.Anchor, span.Baseline, span.Hidden).ShouldBe((TextAnchor.Start, TextBaseline.Alphabetic, false));
        span.Lines.ShouldBeEmpty();
        // Only the first character is told where to be; the rest follow it.
        span.Positions.ShouldBe([new VectorTextPosition(0, 10, 20, 0, 0, 0)]);
        span.Path.ShouldBeNull();
    }

    [Theory]
    [InlineData("<text/>")]
    [InlineData("<text>  \n\t </text>")]
    [InlineData("<text><tspan> </tspan></text>")]
    [InlineData("<text display=\"none\">gone</text>")]
    [InlineData("<defs><text>kept for a use</text></defs>")]
    public void TextWithNothingToWrite_IsNoNode(string body)
    {
        SvgDocument document = Doc(body);
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void EveryPieceOfCharacterData_IsASpan_WithTheStyleOfTheElementItStandsIn()
    {
        VectorText text = Text(
            "<text font-family=\"Georgia, 'Times New Roman',serif\" font-size=\"20\" fill=\"#102030\">one " +
            "<tspan font-weight=\"bold\" fill=\"red\">two <tspan font-style=\"italic\" font-size=\"50%\">three</tspan> four</tspan> five</text>");
        Strings(text).ShouldBe(["one ", "two ", "three", " four", " five"]);

        ImmutableArrayOf(text.Spans[0].Font.Families).ShouldBe(["Georgia", "Times New Roman", "serif"]);
        text.Spans.Select(span => span.Font.Size).ShouldBe([20f, 20f, 10f, 20f, 20f]);
        text.Spans.Select(span => span.Font.Weight).ShouldBe([400, 700, 700, 700, 400]);
        text.Spans.Select(span => span.Font.Slant).ShouldBe([FontSlant.Normal, FontSlant.Normal, FontSlant.Italic, FontSlant.Normal, FontSlant.Normal]);
        text.Spans.Select(span => ((SolidPaint)span.Fill!.Paint).Color.R).ShouldBe([(byte)0x10, (byte)255, (byte)255, (byte)255, (byte)0x10]);
        // The family is the text's everywhere: nothing inside it names another.
        text.Spans.ShouldAllBe(span => span.Font.Families.SequenceEqual(text.Spans[0].Font.Families));

        static string[] ImmutableArrayOf(System.Collections.Immutable.ImmutableArray<string> names) => [.. names];
    }

    [Fact]
    public void WhatIsNotText_InsideText_IsLeftOut_AndALinkIsReadThrough()
    {
        SvgDocument document = Doc(
            "<text>a<title>not drawn</title><desc>nor this</desc><a href=\"#x\" fill=\"blue\">b<tspan>c</tspan></a>" +
            "<rect width=\"9\" height=\"9\"/><tref href=\"#x\"/>d</text>" +
            "<a href=\"#y\"><text>e</text></a>");
        document.Scene.Nodes.Length.ShouldBe(2);
        Strings(document.Scene.Nodes[0].ShouldBeOfType<VectorText>()).ShouldBe(["a", "b", "c", "d"]);
        Strings(document.Scene.Nodes[1].ShouldBeOfType<VectorText>()).ShouldBe(["e"]);
        document.Issues.ShouldBe(
        [
            new SvgIssue(SvgIssueKind.Unsupported, "<rect> element in text", 1),
            new SvgIssue(SvgIssueKind.Unsupported, "<tref> element in text", 1),
        ], ignoreOrder: true);
    }

    // ---- White space ------------------------------------------------------------------------------------------------

    [Fact]
    public void WhiteSpace_IsCollapsedAcrossTheWholeText_AndTrimmedAtItsEnds()
    {
        // Line ends and tabs are spaces; a run of spaces is one, whichever elements its spaces stand in; the text's
        // first and last spaces go.
        VectorText text = Text("<text>\n   one  \t two\n<tspan>  three </tspan>  <tspan> </tspan> four  </text>");
        Strings(text).ShouldBe(["one two ", "three ", "four"]);
    }

    [Fact]
    public void XmlSpacePreserve_KeepsEverySpace_AndIsInheritedOrTakenBack()
    {
        Strings(Text("<text xml:space=\"preserve\">  a \n b\t</text>")).ShouldBe(["  a   b "]);
        // From the root, and taken back by an element inside.
        SvgDocument document = SvgReader.Parse(
            $"<svg xmlns=\"{Namespace}\" xml:space=\"preserve\"><text> a  <tspan xml:space=\"default\">  b  c  </tspan>  d </text></svg>");
        // The tspan's first space follows a kept one that is not collapsible, so it stays; its last is collapsible and is followed by kept ones.
        Strings(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorText>()).ShouldBe([" a  ", " b c ", "  d "]);
        // CSS says the same thing its own way, and wins over the attribute.
        Strings(Text("<text style=\"white-space: pre\"> a  b </text>")).ShouldBe([" a  b "]);
        Strings(Text("<text xml:space=\"preserve\" style=\"white-space: normal\"> a  b </text>")).ShouldBe(["a b"]);
    }

    // ---- Single characters ------------------------------------------------------------------------------------------

    [Fact]
    public void ListsOfPositions_AreForTheCharactersThatAreLeft_OneValueACharacter()
    {
        // Five values for four characters that are left (the leading spaces are not there to be placed): the fifth is not used.
        VectorText text = Text("<text x=\"1 2 3 4 5\" y=\"9\" dx=\"0 0.5\" dy=\"0 0 -2\">   abcd</text>");
        text.Spans.ShouldHaveSingleItem().Positions.ShouldBe(
        [
            new VectorTextPosition(0, 1, 9, 0, 0, 0),
            new VectorTextPosition(1, 2, null, 0.5f, 0, 0),
            new VectorTextPosition(2, 3, null, 0, -2, 0),
            new VectorTextPosition(3, 4, null, 0, 0, 0),
        ]);
    }

    [Fact]
    public void AnInnerElementsList_WinsOverAnOuterOne_CharacterByCharacter()
    {
        // The text places its first six characters; the tspan holds the fourth to sixth and places its first two itself.
        VectorText text = Text("<text x=\"10 20 30 40 50 60\" y=\"5\">abc<tspan x=\"41 51\" dy=\"1\">def</tspan>g</text>");
        Strings(text).ShouldBe(["abc", "def", "g"]);
        text.Spans[0].Positions.ShouldBe([new(0, 10, 5, 0, 0, 0), new(1, 20, null, 0, 0, 0), new(2, 30, null, 0, 0, 0)]);
        text.Spans[1].Positions.ShouldBe([new(0, 41, null, 0, 1, 0), new(1, 51, null, 0, 0, 0), new(2, 60, null, 0, 0, 0)]);
        text.Spans[2].Positions.ShouldBeEmpty();
    }

    [Fact]
    public void TheLastAngleOfAList_TurnsEveryCharacterAfterIt_InThatElementOnly()
    {
        VectorText text = Text("<text rotate=\"10 20\">ab<tspan rotate=\"-5\">cd</tspan>ef</text>");
        text.Spans[0].Positions.ShouldBe([new(0, null, null, 0, 0, 10), new(1, null, null, 0, 0, 20)]);
        text.Spans[1].Positions.ShouldBe([new(0, null, null, 0, 0, -5), new(1, null, null, 0, 0, -5)]);
        // After the tspan the text's own list goes on where it stood: its last angle.
        text.Spans[2].Positions.ShouldBe([new(0, null, null, 0, 0, 20), new(1, null, null, 0, 0, 20)]);
    }

    [Fact]
    public void ACharacterOutsideTheBasicPlane_IsOneCharacter_AndWhatIsNotDisplayedIsNotCounted()
    {
        // The face is two UTF-16 units and one character: the third value is for the b after it, which is at offset 3
        // of its span although it is the third character.
        VectorText text = Text("<text x=\"1 2 3 4\">a\U0001F600b<tspan display=\"none\">XYZ</tspan>c</text>");
        Strings(text).ShouldBe(["a\U0001F600b", "c"]);
        text.Spans[0].Positions.ShouldBe([new(0, 1, null, 0, 0, 0), new(1, 2, null, 0, 0, 0), new(3, 3, null, 0, 0, 0)]);
        text.Spans[1].Positions.ShouldBe([new(0, 4, null, 0, 0, 0)]);
    }

    [Fact]
    public void PositionsAreLengths_InTheUnitsOfTheElementThatGivesThem()
    {
        VectorText text = Text("<text font-size=\"10\" x=\"2em\" y=\"50%\"><tspan font-size=\"20\" dx=\"1em\" dy=\"1pt\">a</tspan></text>");
        VectorTextPosition position = text.Spans.ShouldHaveSingleItem().Positions.ShouldHaveSingleItem();
        (position.X, position.Y, position.Dx).ShouldBe((20f, 50f, 20f));
        position.Dy.ShouldBe(96f / 72f, 1e-5f);
    }

    // ---- The properties of text -------------------------------------------------------------------------------------

    [Fact]
    public void AnchorSpacingBaselineAndLines_AreOnEverySpan()
    {
        VectorText text = Text(
            "<text text-anchor=\"middle\" letter-spacing=\"2\" word-spacing=\"0.5em\" dominant-baseline=\"middle\" text-decoration=\"underline\" font-size=\"10\">a" +
            "<tspan text-anchor=\"end\" letter-spacing=\"normal\" alignment-baseline=\"hanging\" baseline-shift=\"3\" text-decoration=\"none\">b" +
            "<tspan baseline-shift=\"-50%\" text-decoration=\"overline\" visibility=\"hidden\">c</tspan></tspan>d</text>");
        text.Spans.Select(span => span.Anchor).ShouldBe([TextAnchor.Middle, TextAnchor.End, TextAnchor.End, TextAnchor.Middle]);
        text.Spans.Select(span => span.LetterSpacing).ShouldBe([2f, 0f, 0f, 2f]);
        text.Spans.Select(span => span.WordSpacing).ShouldBe([5f, 5f, 5f, 5f]);
        // The baseline of one element alone does not go on to what is inside it; the dominant one does.
        text.Spans.Select(span => span.Baseline).ShouldBe([TextBaseline.Middle, TextBaseline.Hanging, TextBaseline.Middle, TextBaseline.Middle]);
        // "auto" keeps the baseline that is inherited; naming the alphabetic one goes back to it.
        Text("<text dominant-baseline=\"central\">a<tspan dominant-baseline=\"auto\">b</tspan><tspan dominant-baseline=\"alphabetic\">c</tspan></text>")
            .Spans.Select(span => span.Baseline).ShouldBe([TextBaseline.Central, TextBaseline.Central, TextBaseline.Alphabetic]);
        // A shift adds to the shift of what the element is in: 3, then 3 less half of the 10 px em.
        text.Spans.Select(span => span.BaselineShift).ShouldBe([0f, 3f, -2f, 0f]);
        // An element's lines are drawn through all that is in it: "none" inside takes nothing away. And they are the
        // element's own: one object for every span under them.
        VectorTextLine underline = text.Spans[0].Lines.ShouldHaveSingleItem();
        underline.Kinds.ShouldBe(TextDecorations.Underline);
        text.Spans.ShouldAllBe(span => ReferenceEquals(span.Lines[0], underline));
        text.Spans.Select(span => span.Lines.Length).ShouldBe([1, 1, 2, 1]);
        text.Spans[2].Lines[1].Kinds.ShouldBe(TextDecorations.Overline);
        text.Spans.Select(span => span.Hidden).ShouldBe([false, false, true, false]);
    }

    [Theory]
    [InlineData("font-weight=\"bold\"", 700)]
    [InlineData("font-weight=\"normal\"", 400)]
    [InlineData("font-weight=\"350\"", 350)]
    [InlineData("font-weight=\"bolder\"", 700)]                                       // from 400
    [InlineData("font-weight=\"lighter\"", 100)]
    [InlineData("font-weight=\"900\" style=\"font-weight: lighter\"", 100)]          // relative to what is inherited, not to the attribute it beat
    [InlineData("font-weight=\"heavy\"", 400)]
    [InlineData("font-weight=\"1001\"", 400)]
    public void AFontWeight_IsANumber(string attributes, int weight)
    {
        SvgDocument document = Doc($"<text {attributes}>a</text>");
        document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorText>().Spans[0].Font.Weight.ShouldBe(weight);
        document.Issues.IsEmpty.ShouldBe(weight != 400 || attributes.Contains("normal"));
    }

    [Theory]
    [InlineData("12", 12f)]
    [InlineData("12pt", 16f)]
    [InlineData("1.5em", 15f)]                 // of the size inherited
    [InlineData("150%", 15f)]
    [InlineData("medium", 16f)]
    [InlineData("x-large", 24f)]
    [InlineData("larger", 12f)]
    [InlineData("smaller", 8.333333f)]
    [InlineData("-3", 10f)]                    // an error: the size inherited stands
    public void AFontSize_IsALength_AKeyword_OrAShareOfTheSizeInherited(string size, float expected)
    {
        Doc($"<g font-size=\"10\"><text font-size=\"{size}\">a</text></g>").Scene.Nodes.ShouldHaveSingleItem()
            .ShouldBeOfType<VectorText>().Spans[0].Font.Size.ShouldBe(expected, 1e-4f);
    }

    [Theory]
    [InlineData("font-stretch=\"condensed\"", 75f)]
    [InlineData("font-stretch=\"ultra-expanded\"", 200f)]
    [InlineData("font-stretch=\"62.5%\"", 62.5f)]
    [InlineData("font-style=\"oblique\"", 100f)]
    public void StretchAndSlant(string attributes, float stretch)
    {
        VectorFont font = Text($"<text {attributes}>a</text>").Spans[0].Font;
        font.Stretch.ShouldBe(stretch);
        font.Slant.ShouldBe(attributes.Contains("oblique") ? FontSlant.Oblique : FontSlant.Normal);
    }

    [Theory]
    [InlineData("12px serif", 12f, 400, FontSlant.Normal, 100f, "serif")]
    [InlineData("italic bold 20px/1.5 'Fira Sans', sans-serif", 20f, 700, FontSlant.Italic, 100f, "Fira Sans|sans-serif")]
    [InlineData("condensed 300 2em Arial Narrow", 20f, 300, FontSlant.Normal, 75f, "Arial Narrow")]
    [InlineData("small-caps normal large / 20px monospace", 18f, 400, FontSlant.Normal, 100f, "monospace")]
    public void TheFontShorthand_SetsAllOfTheFont_AndWhatItDoesNotNameGoesBackToNormal(string value, float size, int weight, FontSlant slant, float stretch, string families)
    {
        // The attributes before it in the cascade are beaten whole: the weight and the stretch too.
        VectorFont font = Text($"<g font-size=\"10\"><text font-weight=\"900\" font-stretch=\"expanded\" font-family=\"Old\" style=\"font: {value}\">a</text></g>").Spans[0].Font;
        (font.Size, font.Weight, font.Slant, font.Stretch).ShouldBe((size, weight, slant, stretch));
        string.Join('|', font.Families).ShouldBe(families);
    }

    [Fact]
    public void AFontShorthandThatIsNotOne_ChangesNothing_AndASizeAfterItWins()
    {
        SvgDocument document = Doc("<text font-size=\"11\" font-family=\"Kept\" style=\"font: bold; font: caption\">a</text><text style=\"font: 12px serif; font-size: 30px\">b</text>");
        VectorFont kept = document.Scene.Nodes[0].ShouldBeOfType<VectorText>().Spans[0].Font;
        (kept.Size, kept.Weight, string.Join('|', kept.Families)).ShouldBe((11f, 400, "Kept"));
        document.Scene.Nodes[1].ShouldBeOfType<VectorText>().Spans[0].Font.Size.ShouldBe(30f);
        document.Issues.ShouldBe([new SvgIssue(SvgIssueKind.Unsupported, "font", 2)]);
    }

    [Fact]
    public void WhatTextCanAskForAndThisReaderDoesNotGive_IsReported()
    {
        SvgDocument document = Doc(
            "<text writing-mode=\"vertical-rl\" direction=\"rtl\" font-variant=\"small-caps\" style=\"inline-size: 100px\">a" +
            "<tspan dominant-baseline=\"use-script\" text-decoration=\"underline wavy red\">b</tspan></text>");
        VectorText text = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorText>();
        // The text is there, written the plain way.
        Strings(text).ShouldBe(["a", "b"]);
        text.Spans[1].Lines.ShouldHaveSingleItem().Kinds.ShouldBe(TextDecorations.Underline);
        document.Issues.Select(issue => issue.What).ShouldBe(
        [
            "vertical text", "text from right to left", "font-variant", "text wrapped to a width (inline-size)",
            "dominant-baseline: use-script", "text-decoration with a colour, a style or a thickness",
        ], ignoreOrder: true);
        document.Issues.ShouldAllBe(issue => issue.Kind == SvgIssueKind.Unsupported);
    }

    [Fact]
    public void LinesAreDrawnInThePaintOfTheElementThatAskedForThem()
    {
        VectorText text = Text(
            "<text fill=\"#102030\" stroke=\"#00f\" font-size=\"20\" text-decoration=\"underline\">black <tspan fill=\"red\" stroke=\"none\" font-size=\"40\">red</tspan></text>");
        VectorTextLine line = text.Spans[1].Lines.ShouldHaveSingleItem();
        ReferenceEquals(line, text.Spans[0].Lines[0]).ShouldBeTrue();
        // The red word is underlined in the text's colour and stroke.
        ((SolidPaint)line.Fill!.Paint).Color.ShouldBe(new Rgba32(0x10, 0x20, 0x30));
        ((SolidPaint)line.Stroke!.Paint).Color.ShouldBe(new Rgba32(0, 0, 255));
        ((SolidPaint)text.Spans[1].Fill!.Paint).Color.ShouldBe(new Rgba32(255, 0, 0));
        text.Spans[1].Stroke.ShouldBeNull();
        // Asked for on a group around the text, they are still lines of everything in it.
        Text("<g text-decoration=\"line-through\" fill=\"lime\"><text fill=\"red\">a</text></g>").Spans[0].Lines.ShouldHaveSingleItem()
            .Fill!.Paint.ShouldBe(new SolidPaint(new Rgba32(0, 255, 0)));
    }

    [Fact]
    public void SuperscriptsAndSubscripts_AreCarriedAsTheSizeTheyAreRaisedFor()
    {
        // How far is the font's to say (half a line's height): the scene says for which size, and adds up what nests.
        VectorText text = Text(
            "<text font-size=\"20\">x<tspan baseline-shift=\"super\" font-size=\"10\">2<tspan baseline-shift=\"sub\" font-size=\"6\">i</tspan></tspan>" +
            "<tspan baseline-shift=\"sub\">k</tspan><tspan baseline-shift=\"super\" style=\"baseline-shift: baseline\">b</tspan><tspan baseline-shift=\"3\">l</tspan></text>");
        text.Spans.Select(span => span.ScriptRise).ShouldBe([0f, 10f, 4f, -20f, 0f, 0f]);
        text.Spans.Select(span => span.BaselineShift).ShouldBe([0f, 0f, 0f, 0f, 0f, 3f]);
    }

    // ---- Paints -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AFillAndAStroke_AreOnTheSpan_AndPaintOrderSaysWhichIsFirst()
    {
        VectorTextSpan span = Text("<text fill=\"none\" stroke=\"#0000ff\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-dasharray=\"3 1\">a</text>").Spans[0];
        span.Fill.ShouldBeNull();
        VectorStroke stroke = span.Stroke.ShouldNotBeNull();
        (stroke.Width, stroke.Join, stroke.Dashes.Length).ShouldBe((2f, LineJoin.Round, 2));
        span.StrokeFirst.ShouldBeFalse();

        Text("<text stroke=\"red\" paint-order=\"stroke\">a</text>").Spans[0].StrokeFirst.ShouldBeTrue();
        Text("<text stroke=\"red\" paint-order=\"fill\">a</text>").Spans[0].StrokeFirst.ShouldBeFalse();
        // With one paint there is no order to keep.
        Text("<text fill=\"none\" stroke=\"red\" paint-order=\"stroke\">a</text>").Spans[0].StrokeFirst.ShouldBeFalse();
    }

    [Fact]
    public void AGradientInBoundingBoxUnits_IsLeftInTheUnitSquare_ForWhoeverKnowsTheTextsBox()
    {
        const string gradients =
            "<defs><linearGradient id=\"box\" x2=\"0.5\"><stop stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<linearGradient id=\"user\" gradientUnits=\"userSpaceOnUse\" x1=\"10\" x2=\"90\"><stop stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<linearGradient id=\"flat\"><stop stop-color=\"lime\"/></linearGradient></defs>";
        VectorText text = Text(gradients + "<text fill=\"url(#box)\" stroke=\"url(#user)\">a<tspan fill=\"url(#flat)\" stroke=\"url(#box)\">b</tspan></text>");

        VectorTextSpan first = text.Spans[0];
        first.FillInBox.ShouldBeTrue();
        var inBox = first.Fill!.Paint.ShouldBeOfType<LinearGradientPaint>();
        (inBox.X1, inBox.X2, inBox.Transform).ShouldBe((0f, 0.5f, Matrix3x2.Identity));        // fractions of the box, the box not yet there
        first.StrokeInBox.ShouldBeFalse();
        var user = first.Stroke!.Paint.ShouldBeOfType<LinearGradientPaint>();
        (user.X1, user.X2).ShouldBe((10f, 90f));

        // One stop is one colour, which needs no box.
        text.Spans[1].FillInBox.ShouldBeFalse();
        text.Spans[1].Fill!.Paint.ShouldBe(new SolidPaint(new Rgba32(0, 255, 0)));
        text.Spans[1].StrokeInBox.ShouldBeTrue();
    }

    [Fact]
    public void APatternInBoundingBoxUnits_OnText_IsReported_AndItsFallbackPaints()
    {
        const string patterns =
            "<defs><pattern id=\"box\" width=\"0.5\" height=\"0.5\"><rect width=\"2\" height=\"2\"/></pattern>" +
            "<pattern id=\"user\" patternUnits=\"userSpaceOnUse\" width=\"4\" height=\"4\"><rect width=\"2\" height=\"2\"/></pattern></defs>";
        SvgDocument document = Doc(patterns + "<text fill=\"url(#box) #00f\" stroke=\"url(#user)\">a</text>");
        VectorTextSpan span = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorText>().Spans[0];
        span.Fill!.Paint.ShouldBe(new SolidPaint(new Rgba32(0, 0, 255)));
        span.Stroke!.Paint.ShouldBeOfType<PatternPaint>();
        document.Issues.ShouldBe([new SvgIssue(SvgIssueKind.Unsupported, "a pattern in bounding-box units on text", 1)]);
    }

    [Fact]
    public void OpacityOnTheText_IsALayer_AndOnAPartOfItIsInThePaints()
    {
        SvgDocument document = Doc("<text opacity=\"0.5\" fill-opacity=\"0.8\">a<tspan opacity=\"0.5\">b</tspan><tspan opacity=\"0.5\" stroke=\"red\">c</tspan></text>");
        VectorGroup layer = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        layer.Opacity.ShouldBe(0.5f);
        VectorText text = layer.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorText>();
        text.Spans.Select(span => span.Fill!.Opacity).ShouldBe([0.8f, 0.4f, 0.4f]);
        text.Spans[2].Stroke!.Opacity.ShouldBe(0.5f);
        // A fill and a stroke overlap along the outline and would have to be faded as one: that is said.
        document.Issues.ShouldBe([new SvgIssue(SvgIssueKind.Unsupported, "opacity on a part of a text that has a fill and a stroke", 1)]);
    }

    [Fact]
    public void AClipInTheUnitsOfTheTextsBox_CannotBeWorkedOutHere_AndIsReported()
    {
        const string clips =
            "<defs><clipPath id=\"box\" clipPathUnits=\"objectBoundingBox\"><rect width=\"0.5\" height=\"1\"/></clipPath>" +
            "<clipPath id=\"user\"><rect width=\"50\" height=\"50\"/></clipPath></defs>";
        // In user units the text's box is not needed.
        SvgDocument user = Doc(clips + "<text clip-path=\"url(#user)\">a</text>");
        user.Issues.ShouldBeEmpty();
        user.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>().Clip.ShouldNotBeNull();

        // In the box's units the clip cannot be placed. The text is drawn without it, and that is said. So is a group that holds text.
        foreach (string body in (string[])["<text clip-path=\"url(#box)\">a</text>", "<g clip-path=\"url(#box)\"><rect width=\"9\" height=\"9\"/><text>a</text></g>"])
        {
            SvgDocument box = Doc(clips + body);
            box.Issues.ShouldBe([new SvgIssue(SvgIssueKind.Unsupported, "a clip in the units of a text's bounding box", 1)]);
            box.Scene.Nodes.OfType<VectorGroup>().ShouldBeEmpty();
            box.Scene.Nodes.OfType<VectorText>().ShouldHaveSingleItem();
        }

        // A mask's area is in the box's units unless it says otherwise: the area is left out, and its picture still masks.
        SvgDocument masked = Doc("<defs><mask id=\"m\"><rect width=\"50\" height=\"50\" fill=\"white\"/></mask>" +
            "<mask id=\"content\" maskContentUnits=\"objectBoundingBox\"><rect width=\"1\" height=\"1\" fill=\"white\"/></mask></defs>" +
            "<text mask=\"url(#m)\">a</text><text mask=\"url(#content)\">b</text>");
        masked.Issues.ShouldBe([new SvgIssue(SvgIssueKind.Unsupported, "a mask in the units of a text's bounding box", 2)]);
        masked.Scene.Nodes[0].ShouldBeOfType<VectorGroup>().Mask.ShouldNotBeNull().Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        masked.Scene.Nodes[1].ShouldBeOfType<VectorText>();
    }

    // ---- Text along a path ------------------------------------------------------------------------------------------

    [Fact]
    public void TextOnAPath_NamesThePathWithItsTransform_AndWhereItStarts()
    {
        VectorText text = Text(
            "<defs><path id=\"p\" d=\"M 10 20 L 90 20\" transform=\"translate(0 5)\"/></defs>" +
            "<text x=\"3\" y=\"4\">before <textPath href=\"#p\" startOffset=\"12\" x=\"7\" y=\"99\">on <tspan fill=\"red\" x=\"30\" y=\"40\">it</tspan></textPath> after</text>");
        Strings(text).ShouldBe(["before ", "on ", "it", " after"]);
        text.Spans[0].Path.ShouldBeNull();
        text.Spans[3].Path.ShouldBeNull();

        VectorTextPath path = text.Spans[1].Path.ShouldNotBeNull();
        ReferenceEquals(text.Spans[2].Path, path).ShouldBeTrue("the spans of one textPath are on one path");
        Describe(path.Path).ShouldBe("M 10 20 L 90 20");
        path.Transform.ShouldBe(Matrix3x2.CreateTranslation(0, 5));
        (path.StartOffset, path.StartIsFraction, path.RightSide).ShouldBe((12f, false, false));
        // A textPath places no characters: it has no x or y, and one written on it is not read. A tspan in it does,
        // and along a path x is a place along it, while a y is not looked at.
        text.Spans[1].Positions.ShouldBeEmpty();
        text.Spans[2].Positions.ShouldBe([new VectorTextPosition(0, 30, null, 0, 0, 0)]);

        // Nor is the text's own y, when its first character is the first on the path. Its x is how far along the path the text starts.
        Text("<defs><path id=\"p\" d=\"M 10 20 L 90 20\"/></defs><text x=\"5\" y=\"6\" dy=\"2\"><textPath href=\"#p\">on</textPath></text>")
            .Spans[0].Positions.ShouldBe([new VectorTextPosition(0, 5, null, 0, 2, 0)]);
    }

    [Theory]
    [InlineData("<textPath href=\"#c\" startOffset=\"25%\" side=\"right\">a</textPath>", true, 0.25f, true, 6)]         // a circle: a move, four curves and a close
    [InlineData("<textPath path=\"M0 0 H 50\" href=\"#c\">a</textPath>", false, 0f, false, 2)]                          // its own path data wins over href
    [InlineData("<textPath path=\"\" href=\"#c\">a</textPath>", false, 0f, false, 6)]                                   // data with nothing in it: href
    public void ThePath_MayBeAShape_OrTheTextPathsOwnData(string textPath, bool fraction, float start, bool right, int verbs)
    {
        VectorTextPath path = Text($"<defs><circle id=\"c\" cx=\"50\" cy=\"50\" r=\"20\"/></defs><text>{textPath}</text>").Spans[0].Path.ShouldNotBeNull();
        (path.StartIsFraction, path.StartOffset, path.RightSide).ShouldBe((fraction, start, right));
        path.Path.Verbs.Length.ShouldBe(verbs);
    }

    [Theory]
    [InlineData("<textPath>a</textPath>", "textPath: no path to set the text along")]
    [InlineData("<textPath href=\"#nowhere\">a</textPath>", "textPath: no path to set the text along")]
    [InlineData("<textPath href=\"#g\">a</textPath>", "textPath: no path to set the text along")]
    [InlineData("<tspan><textPath href=\"#p\">a</textPath></tspan>", "textPath inside a tspan or another textPath")]
    public void ATextPathThatNamesNoPath_IsInError_AndWhatIsInItIsNotWritten(string textPath, string issue)
    {
        SvgDocument document = Doc($"<defs><path id=\"p\" d=\"M0 0 H9\"/><g id=\"g\"/></defs><text>x{textPath}y</text>");
        Strings(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorText>()).ShouldBe(["x", "y"]);
        document.Issues.ShouldBe([new SvgIssue(SvgIssueKind.Invalid, issue, 1)]);
    }

    // ---- The length asked for ---------------------------------------------------------------------------------------

    [Fact]
    public void TextLength_IsOnTheText()
    {
        VectorText text = Text("<text textLength=\"120\" lengthAdjust=\"spacingAndGlyphs\">a</text>");
        (text.TextLength, text.LengthAdjust).ShouldBe((120f, TextLengthAdjust.SpacingAndGlyphs));
        Text("<text textLength=\"50%\">a</text>").TextLength.ShouldBe(50f);
        Text("<text lengthAdjust=\"spacingAndGlyphs\">a</text>").TextLength.ShouldBeNull();
        Doc("<text textLength=\"-4\">a</text>").Issues.ShouldBe([new SvgIssue(SvgIssueKind.Invalid, "textLength", 1)]);
        // On a part of a text it is not read, and that is said.
        Doc("<text>a<tspan textLength=\"50\">b</tspan></text>").Issues.ShouldBe([new SvgIssue(SvgIssueKind.Unsupported, "textLength on a part of a text", 1)]);
    }

    // ---- Where text may stand ---------------------------------------------------------------------------------------

    [Fact]
    public void TextIsUsed_AndStandsInPatternsMasksAndMarkers_LikeAnyElement()
    {
        SvgDocument document = Doc(
            "<defs><text id=\"t\" x=\"1\" y=\"2\">used</text>" +
            "<mask id=\"m\"><text fill=\"white\">mask</text></mask>" +
            "<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"9\" height=\"9\"><text>tile</text></pattern></defs>" +
            "<use href=\"#t\" x=\"10\" fill=\"red\"/><use href=\"#t\" y=\"30\"/>" +
            "<rect width=\"20\" height=\"20\" mask=\"url(#m)\" fill=\"url(#p)\"/>");
        document.Issues.ShouldBeEmpty();
        VectorText first = document.Scene.Nodes[0].ShouldBeOfType<VectorText>(), second = document.Scene.Nodes[1].ShouldBeOfType<VectorText>();
        first.Transform.ShouldBe(Matrix3x2.CreateTranslation(10, 0));
        second.Transform.ShouldBe(Matrix3x2.CreateTranslation(0, 30));
        ((SolidPaint)first.Spans[0].Fill!.Paint).Color.ShouldBe(new Rgba32(255, 0, 0));     // inherited from the use
        ((SolidPaint)second.Spans[0].Fill!.Paint).Color.ShouldBe(new Rgba32(0, 0, 0));

        VectorGroup masked = document.Scene.Nodes[2].ShouldBeOfType<VectorGroup>();
        // Where text reaches is not known here, so the mask's area is kept as a clip around it.
        VectorGroup area = masked.Mask.ShouldNotBeNull().Children.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        area.Clip.ShouldNotBeNull();
        area.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorText>().Spans[0].Text.ShouldBe("mask");
        masked.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Fill!.Paint.ShouldBeOfType<PatternPaint>()
            .Children.ShouldHaveSingleItem().ShouldBeOfType<VectorText>().Spans[0].Text.ShouldBe("tile");
    }

    [Fact]
    public void TheCharactersOfText_AreCountedForEveryUse()
    {
        // Twenty characters, doubled thirty times by use: the count is of what the scene would hold.
        var body = new System.Text.StringBuilder("<defs><text id=\"u0\">twenty characters...</text>");
        for (int i = 1; i <= 30; i++) body.Append($"<g id=\"u{i}\"><use href=\"#u{i - 1}\"/><use href=\"#u{i - 1}\"/></g>");
        body.Append("</defs><use href=\"#u30\"/>");
        Should.Throw<InvalidDataException>(() => Doc(body.ToString(), options: new SvgReadOptions { MaxTextCharacters = 5_000, MaxElements = int.MaxValue }))
            .Message.ShouldContain("text");
        // Inside the limit it reads.
        Doc("<text>twenty characters...</text>", options: new SvgReadOptions { MaxTextCharacters = 20 }).Scene.Nodes.ShouldHaveSingleItem();
        Should.Throw<InvalidDataException>(() => Doc("<text>twenty characters...</text>", options: new SvgReadOptions { MaxTextCharacters = 19 }));
    }
}
