using System.Collections.Immutable;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using Xunit;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 6: blend modes, isolation and filters, as the scene holds them. Nothing here draws a filter: this repository has
/// no pictures to work on (the test rasteriser is a colour at a point), so what a filter LOOKS like is held against a
/// browser in Fonts.Lib, as text is. Blend modes are drawn here too (<c>blend-modes</c> in the browser oracle).
/// </summary>
public sealed class SvgEffectsTests
{
    private static VectorGroup OnlyGroup(SvgDocument document) => document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();

    private static VectorFilter FilterOf(string filter, string element = "<rect x=\"10\" y=\"10\" width=\"40\" height=\"20\" filter=\"url(#f)\"/>", bool clean = true)
    {
        SvgDocument document = Doc($"<defs>{filter}</defs>{element}");
        if (clean) document.Issues.ShouldBeEmpty();
        return OnlyGroup(document).Filter.ShouldNotBeNull();
    }

    private static VectorRect Rect(float x, float y, float w, float h) => new(x, y, w, h);

    // ---- Blend modes -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("multiply", VectorBlendMode.Multiply)]
    [InlineData("screen", VectorBlendMode.Screen)]
    [InlineData("overlay", VectorBlendMode.Overlay)]
    [InlineData("darken", VectorBlendMode.Darken)]
    [InlineData("lighten", VectorBlendMode.Lighten)]
    [InlineData("color-dodge", VectorBlendMode.ColorDodge)]
    [InlineData("color-burn", VectorBlendMode.ColorBurn)]
    [InlineData("hard-light", VectorBlendMode.HardLight)]
    [InlineData("soft-light", VectorBlendMode.SoftLight)]
    [InlineData("difference", VectorBlendMode.Difference)]
    [InlineData("exclusion", VectorBlendMode.Exclusion)]
    [InlineData("hue", VectorBlendMode.Hue)]
    [InlineData("saturation", VectorBlendMode.Saturation)]
    [InlineData("color", VectorBlendMode.Color)]
    [InlineData("luminosity", VectorBlendMode.Luminosity)]
    public void ABlendMode_IsOnAGroupOfWhatTheElementDraws(string name, VectorBlendMode mode)
    {
        SvgDocument document = Doc($"<rect width=\"10\" height=\"10\" fill=\"#c00\" opacity=\"0.5\" style=\"mix-blend-mode: {name}\"/>");
        document.Issues.ShouldBeEmpty();
        VectorGroup group = OnlyGroup(document);
        (group.Blend, group.Opacity).ShouldBe((mode, 0.5f));
        // The opacity is the group's, not folded into the paint: it is applied to the layer that is then blended.
        group.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Fill!.Opacity.ShouldBe(1f);
    }

    [Fact]
    public void Normal_AndABlendModeNotRead_MakeNoGroup()
    {
        SvgDocument normal = Doc("<rect width=\"10\" height=\"10\" style=\"mix-blend-mode: normal\"/>");
        normal.Issues.ShouldBeEmpty();
        normal.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();

        SvgDocument plus = Doc("<rect width=\"10\" height=\"10\" style=\"mix-blend-mode: plus-lighter\"/>");
        plus.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Unsupported, "mix-blend-mode: plus-lighter", 1));
        plus.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();

        SvgDocument wrong = Doc("<rect width=\"10\" height=\"10\" style=\"mix-blend-mode: loud\"/>");
        wrong.Issues.ShouldHaveSingleItem().Kind.ShouldBe(SvgIssueKind.Invalid);
        wrong.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
    }

    [Fact]
    public void ABlendMode_IsNotInherited()
    {
        VectorGroup group = OnlyGroup(Doc("<g style=\"mix-blend-mode: screen\"><rect width=\"10\" height=\"10\"/><rect width=\"5\" height=\"5\"/></g>"));
        group.Blend.ShouldBe(VectorBlendMode.Screen);
        group.Children.Length.ShouldBe(2);
        group.Children.ShouldAllBe(child => child is VectorShape);
    }

    [Fact]
    public void Isolation_IsAGroup_OnlyWhereSomethingInsideBlends()
    {
        const string Inside = "<rect width=\"10\" height=\"10\" fill=\"#06c\"/><circle r=\"5\" fill=\"#c00\" style=\"mix-blend-mode: multiply\"/>";

        // Without it the circle blends with whatever the scene has below it: the two stand in the scene itself.
        SvgDocument open = Doc($"<g>{Inside}</g>");
        open.Scene.Nodes.Length.ShouldBe(2);
        open.Scene.Nodes[1].ShouldBeOfType<VectorGroup>().Blend.ShouldBe(VectorBlendMode.Multiply);

        // With it the two are a layer of their own, which is laid over what is below in the usual way.
        SvgDocument closed = Doc($"<g style=\"isolation: isolate\">{Inside}</g>");
        closed.Issues.ShouldBeEmpty();
        VectorGroup layer = OnlyGroup(closed);
        (layer.Blend, layer.Opacity, layer.Filter, layer.Clip, layer.Mask).ShouldBe((VectorBlendMode.Normal, 1f, null, null, null));
        layer.Children.Length.ShouldBe(2);
        layer.Children[1].ShouldBeOfType<VectorGroup>().Blend.ShouldBe(VectorBlendMode.Multiply);

        // Nothing inside blends: the picture is the same without a layer, and none is made.
        Doc("<g style=\"isolation: isolate\"><rect width=\"10\" height=\"10\"/><circle r=\"5\"/></g>").Scene.Nodes.ShouldAllBe(node => node is VectorShape);
        // What blends inside a group further in blends in that group, and needs nothing from this one.
        SvgDocument further = Doc($"<g style=\"isolation: isolate\"><g opacity=\"0.5\">{Inside}</g></g>");
        OnlyGroup(further).Opacity.ShouldBe(0.5f);
    }

    // ---- Filters: the area and the units -----------------------------------------------------------------------------

    [Fact]
    public void AFilter_IsOnAGroup_WithItsAreaATenthLargerThanTheBoxAllRound()
    {
        VectorFilter filter = FilterOf("<filter id=\"f\"><feGaussianBlur stdDeviation=\"2\"/></filter>");
        filter.Region.ShouldBe(Rect(6, 8, 48, 24));
        filter.Transform.ShouldBe(System.Numerics.Matrix3x2.Identity);
        filter.Primitives.ShouldHaveSingleItem().ShouldBe(new FilterBlur(VectorFilterInput.SourceGraphic, 2, 2) { Region = Rect(6, 8, 48, 24) });
        filter.Primitives[0].LinearRgb.ShouldBeTrue();
    }

    [Fact]
    public void TheFiltersCoordinates_AreTheElementsUserSpace()
    {
        SvgDocument document = Doc("<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"1\" width=\"50%\" height=\"30\"><feOffset dx=\"3\" dy=\"-4\"/></filter></defs>" +
            "<g transform=\"translate(10 20) scale(2)\"><rect width=\"5\" height=\"5\" filter=\"url(#f)\"/></g>");
        document.Issues.ShouldBeEmpty();
        VectorFilter filter = OnlyGroup(document).Filter.ShouldNotBeNull();
        // The distances stay in user units, and the matrix says what a unit is: a renderer scales them, not the reader.
        filter.Region.ShouldBe(Rect(0, 1, 50, 30));
        filter.Transform.ShouldBe(new System.Numerics.Matrix3x2(2, 0, 0, 2, 10, 20));
        filter.Primitives.ShouldHaveSingleItem().ShouldBe(new FilterOffset(VectorFilterInput.SourceGraphic, 3, -4) { Region = Rect(0, 1, 50, 30) });
    }

    [Fact]
    public void StepsInBoxUnits_AreSharesOfTheBox()
    {
        VectorFilter filter = FilterOf("<filter id=\"f\" primitiveUnits=\"objectBoundingBox\" x=\"0\" y=\"0\" width=\"1\" height=\"1\">" +
            "<feGaussianBlur stdDeviation=\"0.1 0.2\" x=\"0.25\" width=\"50%\"/><feOffset dx=\"0.5\" dy=\"0.5\"/><feDropShadow dx=\"0.1\" dy=\"0.1\" stdDeviation=\"0.05\"/></filter>");
        filter.Region.ShouldBe(Rect(10, 10, 40, 20));
        // 0.1 of the width 40 and 0.2 of the height 20; x and width as shares of the box, y and height not said.
        filter.Primitives[0].ShouldBe(new FilterBlur(VectorFilterInput.SourceGraphic, 4, 4) { Region = Rect(20, 10, 20, 20) });
        // Not said at all: the area of what it works on, which is the blur's.
        filter.Primitives[1].ShouldBe(new FilterOffset(VectorFilterInput.Result(0), 20, 10) { Region = Rect(20, 10, 20, 20) });
        filter.Primitives[2].ShouldBe(new FilterBlur(VectorFilterInput.Result(1), 2, 1) { Region = Rect(20, 10, 20, 20) });
        filter.Primitives[3].ShouldBe(new FilterOffset(VectorFilterInput.Result(2), 4, 2) { Region = Rect(20, 10, 20, 20) });
    }

    [Fact]
    public void AStepsArea_IsWhatItSays_ElseThatOfWhatItWorksOn_AndNeverMoreThanTheFilters()
    {
        VectorFilter filter = FilterOf("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"60\" height=\"40\">" +
            "<feFlood x=\"-5\" y=\"5\" width=\"20\" height=\"100\" result=\"a\"/>" +
            "<feFlood x=\"30\" y=\"10\" width=\"10\" height=\"10\" result=\"b\"/>" +
            "<feMerge><feMergeNode in=\"a\"/><feMergeNode in=\"b\"/></feMerge>" +
            "<feOffset in=\"b\" y=\"0\"/>" +
            "<feMerge><feMergeNode in=\"a\"/><feMergeNode in=\"SourceAlpha\"/></feMerge>" +
            "<feFlood width=\"0\"/>" +
            "<feFlood x=\"70\"/></filter>");
        VectorRect[] areas = [.. filter.Primitives.Select(step => step.Region)];
        areas[0].ShouldBe(Rect(0, 5, 15, 35));          // cut to the filter's area on the left and at the bottom
        areas[1].ShouldBe(Rect(30, 10, 10, 10));
        areas[2].ShouldBe(Rect(0, 5, 40, 35));          // the two it merges, together
        areas[3].ShouldBe(Rect(30, 0, 10, 10));         // b's, but for the y it says
        areas[4].ShouldBe(Rect(0, 0, 60, 40));          // one of them is the layer: the whole area
        (areas[5].Width, areas[5].Height).ShouldBe((0f, 0f));   // no width: it gives nothing
        (areas[6].Width, areas[6].Height).ShouldBe((0f, 0f));   // outside the filter's area: nothing either
    }

    // ---- Filters: which picture a step works on ----------------------------------------------------------------------

    [Fact]
    public void AnInput_IsTheLayer_AStepByName_OrTheStepBefore()
    {
        SvgDocument document = Doc("<defs><filter id=\"f\">" +
            "<feOffset dx=\"1\" result=\"moved\"/>" +                                       // 0: nothing said, first: the layer
            "<feGaussianBlur stdDeviation=\"1\"/>" +                                        // 1: nothing said: the step before
            "<feOffset in=\"SourceAlpha\" result=\"moved\"/>" +                             // 2: the layer's alpha; the name again
            "<feComposite in=\"SourceGraphic\" in2=\"moved\" operator=\"out\"/>" +          // 3: the later of the two "moved"
            "<feBlend in=\"nobody\" in2=\"moved\" mode=\"screen\"/>" +                      // 4: no such name: the step before
            "<feMerge><feMergeNode/><feMergeNode in=\"moved\"/></feMerge>" +                // 5
            "</filter></defs><rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "feBlend: in names no earlier result", 1));
        ImmutableArray<VectorFilterPrimitive> steps = OnlyGroup(document).Filter!.Primitives;
        ((FilterOffset)steps[0]).Input.ShouldBe(VectorFilterInput.SourceGraphic);
        ((FilterBlur)steps[1]).Input.ShouldBe(VectorFilterInput.Result(0));
        ((FilterOffset)steps[2]).Input.ShouldBe(VectorFilterInput.SourceAlpha);
        var composite = (FilterComposite)steps[3];
        (composite.Input, composite.Input2, composite.Operator).ShouldBe((VectorFilterInput.SourceGraphic, VectorFilterInput.Result(2), FilterCompositeOperator.Out));
        var blend = (FilterBlend)steps[4];
        (blend.Input, blend.Input2, blend.Mode).ShouldBe((VectorFilterInput.Result(3), VectorFilterInput.Result(2), VectorBlendMode.Screen));
        ((FilterMerge)steps[5]).Inputs.ShouldBe([VectorFilterInput.Result(4), VectorFilterInput.Result(2)]);
    }

    // ---- Filters: each step ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", false)]
    [InlineData("0 0", false)]
    [InlineData("-1", false)]
    [InlineData("3 -1", false)]
    [InlineData("0 3", true)]
    [InlineData("3 0", true)]
    public void ABlurThatBlursNothing_IsTheInputAsItIs(string deviation, bool blurs)
    {
        VectorFilterPrimitive step = FilterOf($"<filter id=\"f\"><feGaussianBlur stdDeviation=\"{deviation}\"/></filter>").Primitives.ShouldHaveSingleItem();
        if (blurs) step.ShouldBeOfType<FilterBlur>();
        else step.ShouldBe(new FilterOffset(VectorFilterInput.SourceGraphic, 0, 0) { Region = Rect(6, 8, 48, 24) });
    }

    [Fact]
    public void AFloodsColour_IsAPropertyOfTheStep()
    {
        SvgDocument document = Doc("<defs><style>.s { flood-color: #0f0 }</style><filter id=\"f\" color=\"#102030\">" +
            "<feFlood/>" +
            "<feFlood flood-color=\"red\" flood-opacity=\"0.5\"/>" +
            "<feFlood flood-color=\"red\" class=\"s\" style=\"flood-opacity: 20%\"/>" +
            "<feFlood flood-color=\"currentColor\"/>" +
            "<feFlood flood-color=\"rgba(0,0,255,0.5)\" flood-opacity=\"0.5\"/>" +
            "<feFlood style=\"--c: #abc; flood-color: var(--c)\"/>" +
            "</filter></defs><rect width=\"10\" height=\"10\" color=\"#fff\" filter=\"url(#f)\"/>");
        document.Issues.ShouldBeEmpty();
        OnlyGroup(document).Filter!.Primitives.Select(step => ((FilterFlood)step).Color).ShouldBe(
        [
            new Rgba32(0, 0, 0),
            new Rgba32(255, 0, 0, 128),
            new Rgba32(0, 255, 0, 51),
            // The colour of the filter's own surroundings, not of the element that is filtered.
            new Rgba32(0x10, 0x20, 0x30),
            new Rgba32(0, 0, 255, 64),
            new Rgba32(0xaa, 0xbb, 0xcc),
        ]);
    }

    [Fact]
    public void ACompositesOperator_AndItsNumbers()
    {
        ImmutableArray<VectorFilterPrimitive> steps = FilterOf("<filter id=\"f\"><feComposite in2=\"SourceAlpha\"/><feComposite in2=\"SourceAlpha\" operator=\"in\"/>" +
            "<feComposite in2=\"SourceAlpha\" operator=\"atop\"/><feComposite in2=\"SourceAlpha\" operator=\"xor\"/><feComposite in2=\"SourceAlpha\" operator=\"lighter\"/>" +
            "<feComposite in2=\"SourceAlpha\" operator=\"arithmetic\" k1=\"0.1\" k2=\"0.2\" k3=\"-0.3\" k4=\"0.4\"/></filter>").Primitives;
        steps.Select(step => ((FilterComposite)step).Operator).ShouldBe(
            [FilterCompositeOperator.Over, FilterCompositeOperator.In, FilterCompositeOperator.Atop, FilterCompositeOperator.Xor, FilterCompositeOperator.Lighter, FilterCompositeOperator.Arithmetic]);
        var arithmetic = (FilterComposite)steps[5];
        (arithmetic.K1, arithmetic.K2, arithmetic.K3, arithmetic.K4).ShouldBe((0.1f, 0.2f, -0.3f, 0.4f));
    }

    [Fact]
    public void AColourMatrix_IsTwentyNumbers_HoweverItIsWritten()
    {
        const string Numbers = "0.1 0.2 0.3 0.4 0.5, 0.6 0.7 0.8 0.9 1, 1.1 1.2 1.3 1.4 1.5, 1.6 1.7 1.8 1.9 2";
        ImmutableArray<VectorFilterPrimitive> steps = FilterOf("<filter id=\"f\">" +
            $"<feColorMatrix values=\"{Numbers}\"/>" +
            "<feColorMatrix type=\"saturate\" values=\"0\"/>" +
            "<feColorMatrix type=\"saturate\"/>" +
            "<feColorMatrix type=\"hueRotate\" values=\"90\"/>" +
            "<feColorMatrix type=\"hueRotate\"/>" +
            "<feColorMatrix type=\"luminanceToAlpha\"/>" +
            "<feColorMatrix/></filter>").Primitives;
        float[][] m = [.. steps.Select(step => ((FilterColorMatrix)step).Matrix.ToArray())];
        m.ShouldAllBe(matrix => matrix.Length == 20);
        m[0].ShouldBe([0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.7f, 1.8f, 1.9f, 2f]);
        // No saturation: every channel is the same weighted sum of the three, and the weights are not the same as each other.
        m[1].ShouldBe([0.213f, 0.715f, 0.072f, 0, 0, 0.213f, 0.715f, 0.072f, 0, 0, 0.213f, 0.715f, 0.072f, 0, 0, 0, 0, 0, 1, 0], 1e-6f);
        float[] identity = [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];
        m[2].ShouldBe(identity, 1e-6f);
        // A quarter turn: cos 0, sin 1.
        m[3].ShouldBe([0, 0, 1, 0, 0, 0.356f, 0.855f, -0.211f, 0, 0, -0.574f, 1.43f, 0.144f, 0, 0, 0, 0, 0, 1, 0], 1e-5f);
        m[4].ShouldBe(identity, 1e-6f);
        m[5].ShouldBe([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.2125f, 0.7154f, 0.0721f, 0, 0]);
        m[6].ShouldBe(identity);

        // Not twenty: it changes nothing, and says so.
        SvgDocument wrong = Doc("<defs><filter id=\"f\"><feColorMatrix values=\"1 2 3\"/></filter></defs><rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");
        wrong.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "feColorMatrix: values", 1));
        ((FilterColorMatrix)OnlyGroup(wrong).Filter!.Primitives[0]).Matrix.ToArray().ShouldBe(identity);
    }

    [Fact]
    public void AComponentTransfer_HasAFunctionForEveryChannel()
    {
        var transfer = (FilterComponentTransfer)FilterOf("<filter id=\"f\"><feComponentTransfer>" +
            "<feFuncR type=\"table\" tableValues=\"0 0.5, 1\"/>" +
            "<feFuncG type=\"discrete\" tableValues=\"0 1\"/>" +
            "<feFuncB type=\"linear\" slope=\"2\" intercept=\"-0.5\"/>" +
            "<feFuncA type=\"identity\"/><feFuncA type=\"gamma\" amplitude=\"2\" exponent=\"3\" offset=\"0.1\"/>" +
            "</feComponentTransfer></filter>").Primitives.ShouldHaveSingleItem();
        transfer.Red.Kind.ShouldBe(FilterTransferKind.Table);
        transfer.Red.Table.ToArray().ShouldBe([0, 0.5f, 1]);
        transfer.Green.Kind.ShouldBe(FilterTransferKind.Discrete);
        transfer.Green.Table.ToArray().ShouldBe([0, 1]);
        (transfer.Blue.Kind, transfer.Blue.Slope, transfer.Blue.Intercept).ShouldBe((FilterTransferKind.Linear, 2f, -0.5f));
        // Of two for one channel the last stands.
        (transfer.Alpha.Kind, transfer.Alpha.Amplitude, transfer.Alpha.Exponent, transfer.Alpha.Offset).ShouldBe((FilterTransferKind.Gamma, 2f, 3f, 0.1f));

        // A channel with no function, and a table with nothing in it, are left as they are.
        var bare = (FilterComponentTransfer)FilterOf("<filter id=\"f\"><feComponentTransfer><feFuncR type=\"table\" tableValues=\"\"/></feComponentTransfer></filter>").Primitives[0];
        new[] { bare.Red, bare.Green, bare.Blue, bare.Alpha }.ShouldAllBe(function => function.Kind == FilterTransferKind.Identity);
    }

    [Fact]
    public void ADropShadow_IsItsDefinition_InSteps()
    {
        VectorFilter filter = FilterOf("<filter id=\"f\"><feOffset dx=\"1\"/><feDropShadow dx=\"3\" dy=\"4\" stdDeviation=\"1.5\" flood-color=\"#036\" flood-opacity=\"0.5\"/></filter>");
        filter.Primitives.Take(5).Select(step => step with { Region = default }).ShouldBe(
        [
            new FilterOffset(VectorFilterInput.SourceGraphic, 1, 0) { Region = default },
            new FilterBlur(VectorFilterInput.Result(0), 1.5f, 1.5f) { Region = default },
            new FilterOffset(VectorFilterInput.Result(1), 3, 4) { Region = default },
            new FilterFlood(new Rgba32(0, 0x33, 0x66, 128)) { Region = default },
            new FilterComposite(VectorFilterInput.Result(3), VectorFilterInput.Result(2), FilterCompositeOperator.In) { Region = default },
        ], ignoreOrder: false, "the first five");
        // Last, the shadow with what cast it over it: the merge is a record holding an array, so it is looked at by hand.
        ((FilterMerge)filter.Primitives[5]).Inputs.ShouldBe([VectorFilterInput.Result(4), VectorFilterInput.Result(0)]);

        // The usual shadow, of nothing said: 2 down, 2 across, blurred by 2, black.
        ImmutableArray<VectorFilterPrimitive> usual = FilterOf("<filter id=\"f\"><feDropShadow/></filter>").Primitives;
        usual[0].ShouldBe(new FilterBlur(VectorFilterInput.SourceGraphic, 2, 2) { Region = Rect(6, 8, 48, 24) });
        usual[1].ShouldBe(new FilterOffset(VectorFilterInput.Result(0), 2, 2) { Region = Rect(6, 8, 48, 24) });
        ((FilterFlood)usual[2]).Color.ShouldBe(new Rgba32(0, 0, 0));
    }

    [Fact]
    public void LinearLight_IsTheRule_AndAStepOrTheFilterMaySayOtherwise()
    {
        VectorFilter filter = FilterOf("<filter id=\"f\"><feOffset/><feOffset color-interpolation-filters=\"sRGB\"/><feOffset style=\"color-interpolation-filters: linearRGB\"/></filter>");
        filter.Primitives.Select(step => step.LinearRgb).ShouldBe([true, false, true]);

        // On the filter element it is inherited by the steps; on the filtered element it means nothing.
        filter = FilterOf("<filter id=\"f\" color-interpolation-filters=\"sRGB\"><feOffset/><feOffset color-interpolation-filters=\"linearRGB\"/></filter>",
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\" color-interpolation-filters=\"linearRGB\"/>");
        filter.Primitives.Select(step => step.LinearRgb).ShouldBe([false, true]);

        // "auto" leaves the choice to the renderer; a browser takes the values as they are (measured, in Fonts.Lib's filter-edges).
        FilterOf("<filter id=\"f\" color-interpolation-filters=\"auto\"><feOffset/></filter>").Primitives[0].LinearRgb.ShouldBeFalse();
    }

    [Fact]
    public void TheScenesColour_HasWhatAPaletteNeeds()
    {
        // These came with the colour type when Fonts.Lib gave up its own (2.0): a COLR glyph's palette and gradients use them.
        Rgba32.Black.ShouldBe(new Rgba32(0, 0, 0, 255));
        Rgba32.Transparent.ShouldBe(new Rgba32(0, 0, 0, 0));
        new Rgba32(1, 2, 3).A.ShouldBe((byte)255);
        new Rgba32(10, 20, 30, 201).WithMultipliedAlpha(0.5f).ShouldBe(new Rgba32(10, 20, 30, 100));    // 100.5 rounds to even
        new Rgba32(10, 20, 30, 203).WithMultipliedAlpha(0.5f).ShouldBe(new Rgba32(10, 20, 30, 102));    // 101.5 too
        new Rgba32(10, 20, 30, 200).WithMultipliedAlpha(7f).A.ShouldBe((byte)255);
        var from = new Rgba32(0, 100, 200, 0);
        var to = new Rgba32(100, 0, 220, 255);
        Rgba32.Lerp(from, to, 0.25f).ShouldBe(new Rgba32(25, 75, 205, 63));
        Rgba32.Lerp(from, to, -1f).ShouldBe(from);
        Rgba32.Lerp(from, to, 2f).ShouldBe(to);
    }

    // ---- Filters: nothing drawn, and not read ------------------------------------------------------------------------

    [Theory]
    [InlineData("<filter id=\"f\"/>", "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>")]                                   // no steps: nothing comes out
    [InlineData("<filter id=\"f\" width=\"0\"><feOffset/></filter>", "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>")]   // no area
    [InlineData("<filter id=\"f\" height=\"-1\"><feOffset/></filter>", "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>")]
    [InlineData("<filter id=\"f\"><feOffset/></filter>", "<path d=\"M0 5 H20\" stroke=\"#000\" filter=\"url(#f)\"/>")]           // a box with no height
    public void AFilterThatGivesNothing_LeavesNothing(string filter, string element)
    {
        SvgDocument document = Doc($"<defs>{filter}</defs>{element}<circle r=\"3\"/>");
        document.Issues.ShouldBeEmpty();
        document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Path.Verbs.Length.ShouldBeGreaterThan(3);
    }

    [Fact]
    public void AStraightLine_IsFiltered_WhenTheAreaIsInUserSpace()
    {
        SvgDocument document = Doc("<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"30\" height=\"10\"><feOffset/></filter></defs>" +
            "<path d=\"M0 5 H20\" stroke=\"#000\" filter=\"url(#f)\"/>");
        document.Issues.ShouldBeEmpty();
        OnlyGroup(document).Filter.ShouldNotBeNull().Region.ShouldBe(Rect(0, 0, 30, 10));
    }

    [Theory]
    [InlineData("<filter id=\"f\"><feOffset/><feTurbulence baseFrequency=\"0.1\"/></filter>", "url(#f)", SvgIssueKind.Unsupported, "<feTurbulence>")]
    [InlineData("<filter id=\"f\"><feMorphology radius=\"1\"/></filter>", "url(#f)", SvgIssueKind.Unsupported, "<feMorphology>")]
    [InlineData("<filter id=\"f\"><feOffset in=\"BackgroundImage\"/></filter>", "url(#f)", SvgIssueKind.Unsupported, "filter input BackgroundImage")]
    [InlineData("<filter id=\"f\"><feMerge><feMergeNode in=\"FillPaint\"/></feMerge></filter>", "url(#f)", SvgIssueKind.Unsupported, "filter input FillPaint")]
    [InlineData("<filter id=\"f\"><feGaussianBlur stdDeviation=\"1\" edgeMode=\"wrap\"/></filter>", "url(#f)", SvgIssueKind.Unsupported, "feGaussianBlur: edgeMode wrap")]
    [InlineData("<filter id=\"f\" href=\"#g\"><feOffset/></filter>", "url(#f)", SvgIssueKind.Unsupported, "filter: href to another filter")]
    [InlineData("<filter id=\"f\"><feOffset/></filter>", "blur(2px)", SvgIssueKind.Unsupported, "filter: a value that is not a reference")]
    [InlineData("<filter id=\"f\"><feOffset/></filter>", "url(#f) blur(2px)", SvgIssueKind.Unsupported, "filter: a value that is not one reference")]
    [InlineData("<filter id=\"f\"><feOffset/></filter>", "url(#nobody)", SvgIssueKind.Invalid, "filter: no element with that id")]
    [InlineData("<filter id=\"g\"><feOffset/></filter><mask id=\"f\"/>", "url(#f)", SvgIssueKind.Invalid, "filter: not a filter element")]
    public void AFilterThatIsNotRead_IsReported_AndTheElementDrawnAsItIs(string filter, string value, SvgIssueKind kind, string what)
    {
        SvgDocument document = Doc($"<defs>{filter}</defs><rect width=\"10\" height=\"10\" style=\"filter: {value}\"/>");
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(kind, what, 1));
        document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
    }

    [Fact]
    public void TextInAFilterOfItsBox_IsDrawnUnfiltered_AndInUserSpaceItIsFiltered()
    {
        const string Words = "<text y=\"20\" filter=\"url(#f)\">ab</text>";
        SvgDocument inBox = Doc($"<defs><filter id=\"f\"><feOffset/></filter></defs>{Words}");
        inBox.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Unsupported, "a filter in the units of a text's bounding box", 1));
        inBox.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorText>();

        SvgDocument inUserSpace = Doc($"<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"50\" height=\"30\"><feOffset/></filter></defs>{Words}");
        inUserSpace.Issues.ShouldBeEmpty();
        OnlyGroup(inUserSpace).Children.ShouldHaveSingleItem().ShouldBeOfType<VectorText>();
    }

    // ---- Filters with the rest ---------------------------------------------------------------------------------------

    [Fact]
    public void AFilter_AClip_AMask_Opacity_AndABlendMode_AreOneGroup()
    {
        SvgDocument document = Doc("<defs><filter id=\"f\"><feOffset dx=\"1\"/></filter><clipPath id=\"c\"><circle r=\"8\"/></clipPath><mask id=\"m\"><rect width=\"9\" height=\"9\" fill=\"#fff\"/></mask></defs>" +
            "<g id=\"all\" filter=\"url(#f)\" clip-path=\"url(#c)\" mask=\"url(#m)\" opacity=\"0.5\" style=\"mix-blend-mode: darken\"><rect width=\"10\" height=\"10\"/><circle r=\"4\"/></g>");
        document.Issues.ShouldBeEmpty();
        VectorGroup group = OnlyGroup(document);
        (group.Id, group.Opacity, group.Blend).ShouldBe(("all", 0.5f, VectorBlendMode.Darken));
        group.Filter.ShouldNotBeNull();
        group.Clip.ShouldNotBeNull();
        group.Mask.ShouldNotBeNull();
        group.Children.Length.ShouldBe(2);
        string dump = Dump(document.Scene);
        dump.ShouldContain("group id=all opacity=0.5 blend=Darken\n");
        dump.ShouldContain("0: offset SourceGraphic 1,0 ");
    }

    [Fact]
    public void AFilterOnAUse_IsWorkedOutForEveryUse_InItsOwnBox()
    {
        SvgDocument document = Doc("<defs><filter id=\"f\"><feOffset/></filter><rect id=\"r\" width=\"10\" height=\"20\" filter=\"url(#f)\"/></defs>" +
            "<use href=\"#r\"/><use href=\"#r\" x=\"50\" y=\"5\"/>");
        document.Issues.ShouldBeEmpty();
        VectorFilter[] filters = [.. document.Scene.Nodes.Select(node => ((VectorGroup)node).Filter!)];
        filters.Length.ShouldBe(2);
        // The box is the rectangle's own both times; where the second one stands is in the matrix.
        filters.ShouldAllBe(filter => filter.Region == Rect(-1, -2, 12, 24));
        (filters[1].Transform.M31, filters[1].Transform.M32).ShouldBe((50f, 5f));
    }

    [Fact]
    public void WhatIsFiltered_MayReachOutsideAViewport_SoTheViewportClips()
    {
        // Without the filter the square is inside the inner svg, and no clip is put in the scene (see SvgUseTests).
        const string Inner = "<svg x=\"10\" y=\"10\" width=\"20\" height=\"20\"><rect width=\"20\" height=\"20\" FILTER/></svg>";
        Dump(Doc(Inner.Replace("FILTER", "")).Scene).ShouldNotContain("clip");
        string filtered = Dump(Doc("<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"3\"/></filter></defs>" + Inner.Replace("FILTER", "filter=\"url(#f)\"")).Scene);
        filtered.ShouldContain("clip");
    }

    [Fact]
    public void FilterSteps_AreCounted_ForEveryElementThatIsFiltered()
    {
        var steps = new StringBuilder();
        for (int i = 0; i < 40; i++) steps.Append("<feOffset/>");
        var body = new StringBuilder($"<defs><filter id=\"f\">{steps}</filter></defs>");
        for (int i = 0; i < 40; i++) body.Append("<rect width=\"5\" height=\"5\" filter=\"url(#f)\"/>");
        // 40 rectangles of 40 steps each is 1,600 steps: over a limit of 1,000, within one of 2,000.
        Doc(body.ToString(), options: new SvgReadOptions { MaxElements = 2_000 }).Scene.Nodes.Length.ShouldBe(40);
        Should.Throw<InvalidDataException>(() => Doc(body.ToString(), options: new SvgReadOptions { MaxElements = 1_000 }));
    }

    // ---- The scene file ----------------------------------------------------------------------------------------------

    [Fact]
    public void AFilterThatRefersToALaterStep_IsRefusedByTheSceneFile()
    {
        static VectorScene With(VectorFilterInput input) => new(10, 10,
        [
            new VectorGroup
            {
                Opacity = 1, Children = [],
                Filter = new VectorFilter
                {
                    Region = new VectorRect(0, 0, 10, 10),
                    Primitives = [new FilterOffset(VectorFilterInput.SourceGraphic, 1, 1) { Region = default }, new FilterOffset(input, 1, 1) { Region = default }],
                },
            },
        ]);
        VectorSceneFile.Read(VectorSceneFile.Write(With(VectorFilterInput.Result(0)))).Nodes.ShouldHaveSingleItem();
        // Its own result, and one that is not there: a renderer would index past what it has worked out.
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(VectorSceneFile.Write(With(VectorFilterInput.Result(1)))));
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(VectorSceneFile.Write(With(VectorFilterInput.Result(7)))));
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(VectorSceneFile.Write(With(VectorFilterInput.Result(-1)))));
    }

    [Fact]
    public void AVersion3File_IsStillRead_AndMayNotHoldWhatVersion4Added()
    {
        byte[] plain = VectorSceneFile.Write(Doc("<g id=\"g\" opacity=\"0.5\"><rect width=\"5\" height=\"5\"/><circle r=\"2\"/></g>").Scene);
        string expected = Dump(VectorSceneFile.Read(plain));
        // A group with no filter and no blend mode is written as version 3 wrote it, so the same bytes are a version 3 file.
        plain[4].ShouldBe((byte)4);
        plain[4] = 3;
        Dump(VectorSceneFile.Read(plain)).ShouldBe(expected);

        byte[] blended = VectorSceneFile.Write(Doc("<g opacity=\"0.5\" style=\"mix-blend-mode: screen\"><rect width=\"5\" height=\"5\"/></g>").Scene);
        VectorSceneFile.Read(blended).Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>().Blend.ShouldBe(VectorBlendMode.Screen);
        blended[4] = 3;
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(blended)).Message.ShouldContain("group flags");
    }
}
