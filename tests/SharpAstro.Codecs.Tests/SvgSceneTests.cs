using System.Numerics;

using SharpAstro.Svg;
using SharpAstro.Vector;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>Styles, paints, transforms, the viewBox and the shape of the scene that comes out.</summary>
public sealed class SvgSceneTests
{
    private const string Rect = "<rect width=\"10\" height=\"10\"";

    // ---- Paint ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ByDefault_AShapeIsFilledBlack_AndNotStroked()
    {
        VectorShape shape = OnlyShape($"{Rect}/>");
        shape.Fill.ShouldBe(new VectorFill(new SolidPaint(new Rgba32(0, 0, 0)), 1f, FillRule.NonZero));
        shape.Stroke.ShouldBeNull();
        shape.Transform.ShouldBe(Matrix3x2.Identity);
        shape.Id.ShouldBeNull();
    }

    [Fact]
    public void AStroke_HasTheInitialValues_UntilPropertiesChangeThem()
    {
        VectorStroke stroke = OnlyShape($"{Rect} stroke=\"red\"/>").Stroke.ShouldNotBeNull();
        stroke.Paint.ShouldBe(new SolidPaint(new Rgba32(255, 0, 0)));
        (stroke.Opacity, stroke.Width, stroke.Cap, stroke.Join, stroke.MiterLimit, stroke.DashOffset)
            .ShouldBe((1f, 1f, LineCap.Butt, LineJoin.Miter, 4f, 0f));
        stroke.Dashes.ShouldBeEmpty();

        stroke = OnlyShape($"{Rect} stroke=\"red\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"bevel\" " +
            "stroke-miterlimit=\"10\" stroke-dasharray=\"4, 2\" stroke-dashoffset=\"1\" stroke-opacity=\"0.25\"/>").Stroke.ShouldNotBeNull();
        (stroke.Opacity, stroke.Width, stroke.Cap, stroke.Join, stroke.MiterLimit, stroke.DashOffset)
            .ShouldBe((0.25f, 2.5f, LineCap.Round, LineJoin.Bevel, 10f, 1f));
        stroke.Dashes.ToArray().ShouldBe([4f, 2f]);
    }

    [Theory]
    [InlineData("5", new[] { 5f, 5f })]
    [InlineData("5 3 2", new[] { 5f, 3f, 2f, 5f, 3f, 2f })]   // an odd list is the list twice
    [InlineData("1mm 2", new[] { 3.7795f, 2f })]
    [InlineData("none", new float[0])]
    [InlineData("0 0", new float[0])]                          // all zero is a solid line
    public void DashArrays_AreAnEvenNumberOfLengths(string value, float[] expected)
    {
        float[] dashes = [.. OnlyShape($"{Rect} stroke=\"red\" stroke-dasharray=\"{value}\"/>").Stroke.ShouldNotBeNull().Dashes];
        dashes.Length.ShouldBe(expected.Length);
        for (int i = 0; i < dashes.Length; i++) dashes[i].ShouldBe(expected[i], 1e-3f);
    }

    [Theory]
    [InlineData("#f80", 255, 136, 0, 255)]
    [InlineData("#F80c", 255, 136, 0, 204)]
    [InlineData("#ff8800", 255, 136, 0, 255)]
    [InlineData("#ff880080", 255, 136, 0, 128)]
    [InlineData("rgb(255, 136, 0)", 255, 136, 0, 255)]
    [InlineData("rgb(100%, 50%, 0%)", 255, 128, 0, 255)]
    [InlineData("RGBA(255,136,0,0.5)", 255, 136, 0, 128)]
    [InlineData("rgb(255 136 0 / 50%)", 255, 136, 0, 128)]
    [InlineData("rgb(300, -5, 0.4)", 255, 0, 0, 255)]          // clamped and rounded
    [InlineData("hsl(120, 100%, 50%)", 0, 255, 0, 255)]
    [InlineData("hsl(240deg 100% 50%)", 0, 0, 255, 255)]
    [InlineData("hsla(0, 100%, 25%, 0.2)", 128, 0, 0, 51)]
    [InlineData("hsl(-120, 100%, 50%)", 0, 0, 255, 255)]
    [InlineData("hsl(30, 0%, 50%)", 128, 128, 128, 255)]
    [InlineData("  Tomato ", 255, 99, 71, 255)]
    [InlineData("rebeccapurple", 102, 51, 153, 255)]
    public void Colours_AreRead(string value, int r, int g, int b, int a)
    {
        FillColor(OnlyShape($"{Rect} fill=\"{value}\"/>")).ShouldBe(new Rgba32((byte)r, (byte)g, (byte)b, (byte)a));
    }

    [Theory]
    [InlineData("#ff880")]     // five digits
    [InlineData("#ggg")]
    [InlineData("rgb(1, 2)")]
    [InlineData("rgb(1, 2%, 3)")]
    [InlineData("hsl(1, 2, 3)")]
    [InlineData("notacolour")]
    [InlineData("")]
    public void AnInvalidPaint_IsIgnored_AsIfItWereNotWritten_AndReported(string value)
    {
        SvgDocument document = Doc($"<g fill=\"blue\">{Rect} fill=\"{value}\"/></g>");
        FillColor(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0, 0, 255));
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "fill", 1));
    }

    /// <summary>
    /// The colour names against ImageMagick's table, which was typed by someone else. They agree on 145 of the 148.
    /// The three that differ are pinned, so that a typo in ours cannot hide among them: ImageMagick has its own
    /// <c>gray</c> (126) and X11's <c>grey</c> (190) where CSS has 128 for both, and does not know <c>rebeccapurple</c>
    /// (CSS Color 4, #663399).
    /// </summary>
    [Fact]
    public void NamedColours_AreCssColours_CheckedAgainstImageMagicks()
    {
        SvgNamedColors.Names.Count.ShouldBe(148);
        var different = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in SvgNamedColors.Names)
        {
            SvgNamedColors.TryGet(name, out Rgba32 ours).ShouldBeTrue();
            SvgNamedColors.TryGet(name.ToUpperInvariant(), out Rgba32 upper).ShouldBeTrue();
            upper.ShouldBe(ours);
            byte[] theirs;
            try
            {
                theirs = new ImageMagick.MagickColor(name).ToByteArray();
            }
            catch (ArgumentException)
            {
                different[name] = "not a name ImageMagick knows";
                continue;
            }
            if (theirs[0] != ours.R || theirs[1] != ours.G || theirs[2] != ours.B)
            {
                different[name] = $"{ours.R},{ours.G},{ours.B} vs {theirs[0]},{theirs[1]},{theirs[2]}";
            }
        }
        different.ShouldBe(ExpectedNameDifferences);
    }

    private static readonly SortedDictionary<string, string> ExpectedNameDifferences = new(StringComparer.Ordinal)
    {
        ["gray"] = "128,128,128 vs 126,126,126",
        ["grey"] = "128,128,128 vs 190,190,190",
        ["rebeccapurple"] = "not a name ImageMagick knows",
    };

    [Fact]
    public void Properties_Inherit_AndTheStyleAttributeBeatsThePresentationAttribute()
    {
        SvgDocument document = Doc(
            "<g fill=\"red\" stroke=\"blue\" stroke-width=\"3\" fill-rule=\"evenodd\">" +
            $"{Rect}/>" +
            $"{Rect} fill=\"lime\" style=\"fill: #00f; stroke-width:5 !important; /* c; */ stroke : none\"/>" +
            $"<g stroke-width=\"7\">{Rect} fill=\"inherit\" stroke-width=\"inherit\"/></g>" +
            "</g>");
        VectorShape[] shapes = [.. document.Scene.Nodes.Cast<VectorShape>()];
        shapes.Length.ShouldBe(3);

        FillColor(shapes[0]).ShouldBe(new Rgba32(255, 0, 0));
        shapes[0].Fill!.Rule.ShouldBe(FillRule.EvenOdd);
        StrokeColor(shapes[0]).ShouldBe(new Rgba32(0, 0, 255));
        shapes[0].Stroke!.Width.ShouldBe(3f);

        FillColor(shapes[1]).ShouldBe(new Rgba32(0, 0, 255));
        shapes[1].Stroke.ShouldBeNull();

        FillColor(shapes[2]).ShouldBe(new Rgba32(255, 0, 0));
        shapes[2].Stroke!.Width.ShouldBe(7f);
        document.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void CurrentColor_InheritsAsAKeyword_AndTakesTheColourOfTheElementThatDrawsWithIt()
    {
        SvgDocument document = Doc(
            "<g color=\"red\"><g fill=\"currentColor\" stroke=\"currentcolor\">" +
            $"{Rect}/>{Rect} color=\"blue\"/>{Rect} style=\"color: rgb(0,255,0)\"/></g></g>");
        VectorShape[] shapes = [.. document.Scene.Nodes.Cast<VectorShape>()];
        FillColor(shapes[0]).ShouldBe(new Rgba32(255, 0, 0));
        FillColor(shapes[1]).ShouldBe(new Rgba32(0, 0, 255));
        StrokeColor(shapes[1]).ShouldBe(new Rgba32(0, 0, 255));
        FillColor(shapes[2]).ShouldBe(new Rgba32(0, 255, 0));
    }

    [Theory]
    [InlineData("fill=\"none\"")]
    [InlineData("fill=\"transparent\"")]
    [InlineData("fill-opacity=\"0\"")]
    [InlineData("opacity=\"0\"")]
    [InlineData("display=\"none\"")]
    [InlineData("style=\"display:none\"")]
    [InlineData("visibility=\"hidden\"")]
    [InlineData("visibility=\"collapse\"")]
    [InlineData("fill=\"none\" stroke=\"red\" stroke-width=\"0\"")]
    [InlineData("fill=\"none\" stroke=\"red\" stroke-opacity=\"0\"")]
    public void WhatDrawsNothing_IsNotInTheScene(string attributes)
    {
        Doc($"{Rect} {attributes}/>").Scene.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public void Visibility_IsInherited_SoAChildCanTurnItBackOn_ButDisplayNoneRemovesTheSubtree()
    {
        Doc($"<g visibility=\"hidden\">{Rect}/>{Rect} visibility=\"visible\" id=\"shown\"/></g>")
            .Scene.Nodes.ShouldHaveSingleItem().Id.ShouldBe("shown");
        Doc($"<g display=\"none\">{Rect} display=\"inline\"/></g>").Scene.Nodes.ShouldBeEmpty();
    }

    // ---- Opacity and groups --------------------------------------------------------------------------------------

    [Fact]
    public void OpacityOnAShapeWithOnePaint_IsFoldedIntoThatPaint()
    {
        VectorShape shape = OnlyShape($"{Rect} opacity=\"0.5\" fill-opacity=\"50%\"/>");
        shape.Fill!.Opacity.ShouldBe(0.25f);
        shape = OnlyShape($"{Rect} opacity=\".5\" fill=\"none\" stroke=\"red\"/>");
        shape.Stroke!.Opacity.ShouldBe(0.5f);
    }

    [Fact]
    public void OpacityOnAShapeWithFillAndStroke_NeedsALayer_BecauseTheyOverlap()
    {
        VectorGroup group = Doc($"{Rect} opacity=\"0.5\" stroke=\"red\" id=\"r\"/>").Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Opacity.ShouldBe(0.5f);
        VectorShape shape = group.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        shape.Id.ShouldBe("r");
        shape.Fill!.Opacity.ShouldBe(1f);
        shape.Stroke!.Opacity.ShouldBe(1f);
    }

    [Fact]
    public void AGroup_IsOnlyInTheSceneWhereItsOpacityNeedsALayer()
    {
        SvgDocument document = Doc(
            $"<g id=\"plain\">{Rect} id=\"a\"/></g>" +
            $"<g id=\"faded\" opacity=\"0.4\">{Rect} id=\"b\"/><g>{Rect} id=\"c\"/></g></g>" +
            $"<g opacity=\"0\">{Rect}/></g>" +
            "<g opacity=\"0.5\"></g>" +
            $"<a opacity=\"2\">{Rect} id=\"d\"/></a>");
        document.Scene.Nodes.Select(node => $"{node.GetType().Name}:{node.Id}").ShouldBe(["VectorShape:a", "VectorGroup:faded", "VectorShape:d"]);
        var group = (VectorGroup)document.Scene.Nodes[1];
        group.Opacity.ShouldBe(0.4f);
        group.Children.Select(node => node.Id).ShouldBe(["b", "c"]);
        document.Issues.ShouldBeEmpty();
    }

    // ---- Transforms ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("translate(10, 20)", 11f, 21f)]
    [InlineData("translate(10)", 11f, 1f)]
    [InlineData("scale(2)", 2f, 2f)]
    [InlineData("scale(2 3)", 2f, 3f)]
    [InlineData("rotate(90)", -1f, 1f)]
    [InlineData("rotate(90, 1, 0)", 0f, 0f)]
    [InlineData("skewX(45)", 2f, 1f)]
    [InlineData("skewY(45)", 1f, 2f)]
    [InlineData("matrix(1 2 3 4 5 6)", 9f, 12f)]            // x' = a x + c y + e, y' = b x + d y + f
    // The list is applied right to left: the last transform touches the point first.
    [InlineData("translate(10,0) scale(2)", 12f, 2f)]
    [InlineData("scale(2) translate(10,0)", 22f, 2f)]
    [InlineData("translate(10,0),rotate(90)scale(2)", 8f, 2f)]
    [InlineData("  translate( 1e1 , 0 )  ", 11f, 1f)]
    public void ATransform_MapsThePointOneOne(string transform, float x, float y)
    {
        Vector2 mapped = Map(OnlyShape($"{Rect} transform=\"{transform}\"/>"), 1, 1);
        mapped.X.ShouldBe(x, 1e-4f);
        mapped.Y.ShouldBe(y, 1e-4f);
    }

    [Fact]
    public void NestedTransforms_MultiplyOutOntoTheShape_OutermostLast()
    {
        VectorShape shape = OnlyShape($"<g transform=\"translate(100,0)\"><g transform=\"scale(2)\">{Rect} transform=\"translate(1,1)\"/></g></g>");
        Map(shape, 0, 0).ShouldBe(new Vector2(102, 2));
        Map(shape, 10, 10).ShouldBe(new Vector2(122, 22));
        // The path itself stays in its own units.
        Describe(shape.Path).ShouldBe("M 0 0 L 10 0 L 10 10 L 0 10 Z");
    }

    [Theory]
    [InlineData("scale(0)")]
    [InlineData("scale(1, 0)")]
    [InlineData("matrix(1 2 2 4 0 0)")]
    public void ATransformThatCannotBeInverted_TurnsTheElementOff(string transform)
    {
        Doc($"<g transform=\"{transform}\">{Rect}/></g>").Scene.Nodes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("translate(1,2,3)")]
    [InlineData("rotate(1,2)")]
    [InlineData("spin(1)")]
    [InlineData("translate(1")]
    [InlineData("translate 1")]
    [InlineData("matrix(1 2 3 4 5)")]
    public void AnInvalidTransform_IsIgnored_AndReported(string transform)
    {
        SvgDocument document = Doc($"{Rect} transform=\"{transform}\"/>");
        document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Transform.ShouldBe(Matrix3x2.Identity);
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "transform", 1));
    }

    // ---- The root: size and viewBox ------------------------------------------------------------------------------

    [Theory]
    [InlineData("", 300f, 150f)]
    [InlineData("viewBox=\"0 0 40 20\"", 40f, 20f)]
    [InlineData("width=\"80\" viewBox=\"0 0 40 20\"", 80f, 40f)]
    [InlineData("height=\"80\" viewBox=\"0 0 40 20\"", 160f, 80f)]
    [InlineData("width=\"80\"", 80f, 150f)]
    [InlineData("width=\"1in\" height=\"2em\"", 96f, 32f)]
    [InlineData("width=\"100%\" height=\"100%\" viewBox=\"0 0 40 20\"", 40f, 20f)]
    public void TheSize_ComesFromWidthAndHeight_ThenTheViewBox_ThenTheDefault(string root, float width, float height)
    {
        SvgDocument document = Doc("", root);
        (document.Width, document.Height).ShouldBe((width, height));
        (document.Scene.Width, document.Scene.Height).ShouldBe((width, height));
    }

    [Fact]
    public void AViewport_ResolvesPercentagesAndAbsentSizes()
    {
        var options = new SvgReadOptions { ViewportWidth = 400, ViewportHeight = 200 };
        SvgDocument document = Doc("", "width=\"50%\"", options);
        (document.Width, document.Height).ShouldBe((200f, 200f));
        document = Doc("", "viewBox=\"0 0 10 10\"", options);
        (document.Width, document.Height).ShouldBe((400f, 200f));
        Map(OnlyShapeIn(Doc($"{Rect}/>", "viewBox=\"0 0 10 10\"", options)), 0, 0).ShouldBe(new Vector2(100, 0));
    }

    private static VectorShape OnlyShapeIn(SvgDocument document) => document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();

    [Theory]
    // A 50 x 50 box in a 200 x 100 picture. meet fits it inside (scale 2), slice covers the picture (scale 4).
    [InlineData("", 50f, 0f, 150f, 100f)]
    [InlineData("preserveAspectRatio=\"xMidYMid meet\"", 50f, 0f, 150f, 100f)]
    [InlineData("preserveAspectRatio=\"xMinYMin\"", 0f, 0f, 100f, 100f)]
    [InlineData("preserveAspectRatio=\"xMaxYMax meet\"", 100f, 0f, 200f, 100f)]
    [InlineData("preserveAspectRatio=\"xMidYMid slice\"", 0f, -50f, 200f, 150f)]
    [InlineData("preserveAspectRatio=\"xMinYMin slice\"", 0f, 0f, 200f, 200f)]
    [InlineData("preserveAspectRatio=\"xMaxYMax slice\"", 0f, -100f, 200f, 100f)]
    [InlineData("preserveAspectRatio=\"none\"", 0f, 0f, 200f, 100f)]
    [InlineData("preserveAspectRatio=\"defer xMinYMin meet\"", 0f, 0f, 100f, 100f)]
    public void TheViewBox_IsMappedOntoThePicture(string preserve, float left, float top, float right, float bottom)
    {
        SvgDocument document = Doc($"{Rect}/>", $"width=\"200\" height=\"100\" viewBox=\"0 0 50 50\" {preserve}");
        document.ViewBox.ShouldBe(new SvgViewBox(0, 0, 50, 50));
        VectorShape shape = OnlyShapeIn(document);
        Map(shape, 0, 0).ShouldBe(new Vector2(left, top));
        Map(shape, 50, 50).ShouldBe(new Vector2(right, bottom));
        document.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void TheViewBoxOrigin_IsMovedToThePicturesCorner()
    {
        VectorShape shape = OnlyShape($"{Rect}/>", "width=\"100\" height=\"100\" viewBox=\"-10,20 50 50\"");
        Map(shape, -10, 20).ShouldBe(new Vector2(0, 0));
        Map(shape, 40, 70).ShouldBe(new Vector2(100, 100));
    }

    [Theory]
    [InlineData("viewBox=\"0 0 50\"", "viewBox")]
    [InlineData("viewBox=\"0 0 -50 50\"", "viewBox")]
    [InlineData("viewBox=\"0 0 50 50\" preserveAspectRatio=\"xMidYMid shrink\"", "preserveAspectRatio")]
    [InlineData("viewBox=\"0 0 50 50\" preserveAspectRatio=\"middle\"", "preserveAspectRatio")]
    [InlineData("width=\"wide\"", "width")]
    [InlineData("height=\"-5\"", "height")]
    public void AnInvalidRootAttribute_IsIgnored_AndReported(string root, string what)
    {
        SvgDocument document = Doc($"{Rect}/>", root);
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, what, 1));
        document.Scene.Nodes.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("width=\"0\" height=\"10\"")]
    [InlineData("width=\"10\" height=\"10\" viewBox=\"0 0 0 10\"")]
    public void AZeroSize_TurnsRenderingOff(string root)
    {
        Doc($"{Rect}/>", root).Scene.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public void TheRootsOwnProperties_ApplyLikeAGroups()
    {
        SvgDocument document = Doc($"{Rect}/>{Rect}/>", "width=\"100\" height=\"100\" viewBox=\"0 0 50 50\" fill=\"red\" opacity=\"0.5\" transform=\"translate(10,0)\"");
        VectorGroup group = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Opacity.ShouldBe(0.5f);
        group.Children.Length.ShouldBe(2);
        var shape = (VectorShape)group.Children[0];
        FillColor(shape).ShouldBe(new Rgba32(255, 0, 0));
        // The root's transform is in picture units, outside the viewBox mapping (scale 2 here).
        Map(shape, 1, 1).ShouldBe(new Vector2(12, 2));
    }
}
