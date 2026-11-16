using System.Numerics;
using SharpAstro.Svg;
using SharpAstro.Vector;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>Gradients as scene paints: geometry, the space they are in, stops, inheritance, and the cases that are not a gradient at all.</summary>
public sealed class SvgGradientTests
{
    private const string TwoStops = "<stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/>";

    /// <summary>The fill of a 80 x 40 rectangle at (10, 20) that uses the gradient <c>#g</c> defined by <paramref name="defs"/>.</summary>
    private static VectorPaint Paint(string defs, string fill = "url(#g)") =>
        OnlyShape($"<defs>{defs}</defs><rect x=\"10\" y=\"20\" width=\"80\" height=\"40\" fill=\"{fill}\"/>").Fill.ShouldNotBeNull().Paint;

    private static Vector2 At(Matrix3x2 transform, float x, float y) => Vector2.Transform(new Vector2(x, y), transform);

    [Fact]
    public void ALinearGradient_IsByDefault_AcrossTheShapesBoundingBox()
    {
        LinearGradientPaint paint = Paint($"<linearGradient id=\"g\">{TwoStops}</linearGradient>").ShouldBeOfType<LinearGradientPaint>();
        (paint.X1, paint.Y1, paint.X2, paint.Y2).ShouldBe((0f, 0f, 1f, 0f));
        paint.Extend.ShouldBe(GradientExtend.Pad);
        // Gradient space is the unit square; the transform lays it over the box.
        At(paint.Transform, 0, 0).ShouldBe(new Vector2(10, 20));
        At(paint.Transform, 1, 1).ShouldBe(new Vector2(90, 60));
        paint.Stops.ToArray().ShouldBe([new GradientStop(0, new Rgba32(255, 0, 0)), new GradientStop(1, new Rgba32(0, 0, 255))]);
    }

    [Fact]
    public void BoundingBoxCoordinates_AreFractions_WrittenAsNumbersOrPercentages()
    {
        LinearGradientPaint paint = Paint($"<linearGradient id=\"g\" x1=\"25%\" y1=\"0.5\" x2=\"75%\" y2=\"100%\">{TwoStops}</linearGradient>")
            .ShouldBeOfType<LinearGradientPaint>();
        (paint.X1, paint.Y1, paint.X2, paint.Y2).ShouldBe((0.25f, 0.5f, 0.75f, 1f));
    }

    [Fact]
    public void UserSpaceCoordinates_AreLengths_AndTheBoxPlaysNoPart()
    {
        LinearGradientPaint paint = Paint($"<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"5\" y1=\"1in\" x2=\"50%\" y2=\"25%\">{TwoStops}</linearGradient>")
            .ShouldBeOfType<LinearGradientPaint>();
        // Percentages are of the 100 x 100 viewport.
        (paint.X1, paint.Y1, paint.X2, paint.Y2).ShouldBe((5f, 96f, 50f, 25f));
        paint.Transform.ShouldBe(Matrix3x2.Identity);
    }

    [Fact]
    public void GradientTransform_IsAppliedInsideTheBoundingBoxMapping()
    {
        LinearGradientPaint paint = Paint($"<linearGradient id=\"g\" gradientTransform=\"translate(0.5 0) scale(0.5 1)\">{TwoStops}</linearGradient>")
            .ShouldBeOfType<LinearGradientPaint>();
        // (0,0) -> scale -> (0,0) -> translate -> (0.5,0) -> box -> (50,20).
        At(paint.Transform, 0, 0).ShouldBe(new Vector2(50, 20));
        At(paint.Transform, 1, 1).ShouldBe(new Vector2(90, 60));

        paint = Paint($"<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" gradientTransform=\"rotate(90)\">{TwoStops}</linearGradient>")
            .ShouldBeOfType<LinearGradientPaint>();
        Vector2 turned = At(paint.Transform, 1, 0);
        turned.X.ShouldBe(0f, 1e-5f);
        turned.Y.ShouldBe(1f, 1e-5f);
    }

    [Theory]
    [InlineData("pad", GradientExtend.Pad)]
    [InlineData("reflect", GradientExtend.Reflect)]
    [InlineData("repeat", GradientExtend.Repeat)]
    public void SpreadMethod_IsTheExtendMode(string spread, GradientExtend expected)
    {
        Paint($"<linearGradient id=\"g\" spreadMethod=\"{spread}\">{TwoStops}</linearGradient>").ShouldBeOfType<LinearGradientPaint>().Extend.ShouldBe(expected);
        Paint($"<radialGradient id=\"g\" spreadMethod=\"{spread}\">{TwoStops}</radialGradient>").ShouldBeOfType<RadialGradientPaint>().Extend.ShouldBe(expected);
    }

    [Fact]
    public void ARadialGradient_IsTwoCircles_FromTheFocalPointToTheCircle()
    {
        RadialGradientPaint paint = Paint($"<radialGradient id=\"g\">{TwoStops}</radialGradient>").ShouldBeOfType<RadialGradientPaint>();
        (paint.StartX, paint.StartY, paint.StartRadius).ShouldBe((0.5f, 0.5f, 0f));
        (paint.EndX, paint.EndY, paint.EndRadius).ShouldBe((0.5f, 0.5f, 0.5f));
        At(paint.Transform, 0.5f, 0.5f).ShouldBe(new Vector2(50, 40));

        paint = Paint($"<radialGradient id=\"g\" cx=\"0.4\" cy=\"0.6\" r=\"0.3\" fx=\"0.2\" fy=\"0.1\" fr=\"5%\">{TwoStops}</radialGradient>").ShouldBeOfType<RadialGradientPaint>();
        (paint.StartX, paint.StartY, paint.StartRadius).ShouldBe((0.2f, 0.1f, 0.05f));
        (paint.EndX, paint.EndY, paint.EndRadius).ShouldBe((0.4f, 0.6f, 0.3f));

        // One focal coordinate given: the other is the centre's.
        paint = Paint($"<radialGradient id=\"g\" cx=\"0.4\" cy=\"0.6\" fx=\"0.2\">{TwoStops}</radialGradient>").ShouldBeOfType<RadialGradientPaint>();
        (paint.StartX, paint.StartY).ShouldBe((0.2f, 0.6f));

        // A focal point outside the circle is kept as written (SVG 2, and what a browser draws), not pulled onto the edge.
        paint = Paint($"<radialGradient id=\"g\" r=\"0.3\" fx=\"0.95\">{TwoStops}</radialGradient>").ShouldBeOfType<RadialGradientPaint>();
        (paint.StartX, paint.EndRadius).ShouldBe((0.95f, 0.3f));

        // In user space the radius is a length, a percentage of it of the viewport's diagonal measure.
        paint = Paint($"<radialGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" cx=\"30\" cy=\"40\" r=\"10%\">{TwoStops}</radialGradient>").ShouldBeOfType<RadialGradientPaint>();
        (paint.EndX, paint.EndY, paint.EndRadius).ShouldBe((30f, 40f, 10f));
    }

    [Fact]
    public void StopOffsets_AreClampedToZeroAndOne_AndNeverGoBack()
    {
        LinearGradientPaint paint = Paint(
            "<linearGradient id=\"g\"><stop offset=\"-1\" stop-color=\"#000\"/><stop offset=\"60%\" stop-color=\"#fff\"/>" +
            "<stop offset=\"0.3\" stop-color=\"#c0c\"/><stop offset=\"7\" stop-color=\"#0cc\"/><stop stop-color=\"#111\"/></linearGradient>")
            .ShouldBeOfType<LinearGradientPaint>();
        paint.Stops.Select(stop => stop.Offset).ShouldBe([0f, 0.6f, 0.6f, 1f, 1f]);
    }

    [Fact]
    public void StopColourAndOpacity_AreProperties_AndTheOpacityGoesIntoTheAlpha()
    {
        LinearGradientPaint paint = Paint(
            "<linearGradient id=\"g\">" +
            "<stop offset=\"0\"/>" +                                                              // black by default
            "<stop offset=\"0.2\" stop-color=\"#f00\" stop-opacity=\"0.5\"/>" +
            "<stop offset=\"0.4\" stop-color=\"red\" style=\"stop-color: rgba(0, 255, 0, 0.5); stop-opacity: 50%\"/>" +
            "<stop offset=\"0.6\" stop-color=\"currentColor\" color=\"#00f\"/>" +
            "<stop offset=\"0.8\" stop-color=\"bogus\"/>" +
            "</linearGradient>").ShouldBeOfType<LinearGradientPaint>();
        paint.Stops.Select(stop => stop.Color).ShouldBe(
        [
            new Rgba32(0, 0, 0), new Rgba32(255, 0, 0, 128), new Rgba32(0, 255, 0, 64), new Rgba32(0, 0, 255), new Rgba32(0, 0, 0),
        ]);
    }

    [Fact]
    public void AGradient_TakesWhatItDoesNotSay_FromTheOneItsHrefNames()
    {
        const string defs =
            "<linearGradient id=\"base\" x2=\"0\" y2=\"1\" spreadMethod=\"reflect\" gradientUnits=\"userSpaceOnUse\" gradientTransform=\"scale(2)\">" + TwoStops + "</linearGradient>" +
            "<linearGradient id=\"mid\" href=\"#base\" spreadMethod=\"repeat\"/>";

        // Everything from two links away, except what the link between overrides.
        LinearGradientPaint linear = Paint(defs + "<linearGradient id=\"g\" href=\"#mid\" x1=\"3\"/>").ShouldBeOfType<LinearGradientPaint>();
        (linear.X1, linear.Y1, linear.X2, linear.Y2).ShouldBe((3f, 0f, 0f, 1f));
        linear.Extend.ShouldBe(GradientExtend.Repeat);
        At(linear.Transform, 1, 1).ShouldBe(new Vector2(2, 2));
        linear.Stops.Length.ShouldBe(2);

        // A radial gradient takes stops, units, spread and transform from a linear one, but not x1..y2.
        RadialGradientPaint radial = Paint(defs + "<radialGradient id=\"g\" href=\"#mid\"/>").ShouldBeOfType<RadialGradientPaint>();
        (radial.EndX, radial.EndY).ShouldBe((50f, 50f));   // 50% of the viewport, since the units are inherited
        radial.Extend.ShouldBe(GradientExtend.Repeat);
        radial.Stops.Length.ShouldBe(2);

        // x1..y2 come only from linear gradients along the chain, cx..fr only from radial ones: a stray x1 on a radial
        // gradient is nothing to a linear one that refers to it.
        Paint("<radialGradient id=\"r\" x1=\"0.5\" cx=\"0.9\">" + TwoStops + "</radialGradient><linearGradient id=\"g\" href=\"#r\"/>")
            .ShouldBeOfType<LinearGradientPaint>().X1.ShouldBe(0f);

        // Its own stops win.
        Paint(defs + "<linearGradient id=\"g\" href=\"#base\"><stop offset=\"0\" stop-color=\"#111\"/><stop offset=\"0.5\"/><stop offset=\"1\"/></linearGradient>")
            .ShouldBeOfType<LinearGradientPaint>().Stops.Length.ShouldBe(3);

        // A chain that comes back to itself ends there.
        Paint("<linearGradient id=\"g\" href=\"#h\"/><linearGradient id=\"h\" href=\"#g\">" + TwoStops + "</linearGradient>")
            .ShouldBeOfType<LinearGradientPaint>().Stops.Length.ShouldBe(2);
    }

    [Theory]
    // One stop is that colour everywhere; a gradient vector of no length, or a radius of zero, is the last stop's.
    [InlineData("<linearGradient id=\"g\"><stop offset=\"0.3\" stop-color=\"#c60\"/></linearGradient>", "204,102,0")]
    [InlineData("<linearGradient id=\"g\" x1=\"0.5\" x2=\"0.5\">" + TwoStops + "</linearGradient>", "0,0,255")]
    [InlineData("<radialGradient id=\"g\" r=\"0\">" + TwoStops + "</radialGradient>", "0,0,255")]
    public void AGradientThatIsOneColour_IsASolidPaint(string defs, string expected)
    {
        Rgba32 colour = Paint(defs).ShouldBeOfType<SolidPaint>().Color;
        $"{colour.R},{colour.G},{colour.B}".ShouldBe(expected);
    }

    [Fact]
    public void AGradientWithNoStops_PaintsNothing_EvenWithAFallback()
    {
        Doc("<defs><linearGradient id=\"g\"/></defs><rect width=\"5\" height=\"5\" fill=\"url(#g) red\"/>").Scene.Nodes.ShouldBeEmpty();
    }

    [Theory]
    // Bounding-box units on a box with no height or no width: the fallback, or nothing.
    [InlineData("<linearGradient id=\"g\">" + TwoStops + "</linearGradient>", "<path d=\"M0 5 H50\" fill=\"none\" stroke-width=\"4\" stroke=\"url(#g) #0a0\"/>", "0,170,0")]
    [InlineData("<linearGradient id=\"g\">" + TwoStops + "</linearGradient>", "<path d=\"M5 0 V50\" fill=\"none\" stroke-width=\"4\" stroke=\"url(#g) currentColor\" color=\"#123\"/>", "17,34,51")]
    [InlineData("<linearGradient id=\"g\">" + TwoStops + "</linearGradient>", "<path d=\"M0 5 H50\" fill=\"none\" stroke-width=\"4\" stroke=\"url(#g)\"/>", null)]
    // A gradient transform with no inverse: the same.
    [InlineData("<linearGradient id=\"g\" gradientTransform=\"scale(1 0)\">" + TwoStops + "</linearGradient>", "<path d=\"M0 0 L9 9\" fill=\"none\" stroke=\"url(#g) #c0c\"/>", "204,0,204")]
    public void AGradientThatCannotBeApplied_GivesWayToItsFallback(string defs, string shape, string? expected)
    {
        SvgDocument document = Doc($"<defs>{defs}</defs>{shape}");
        document.Issues.ShouldBeEmpty();
        if (expected is null)
        {
            document.Scene.Nodes.ShouldBeEmpty();
            return;
        }
        Rgba32 colour = StrokeColor(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>());
        $"{colour.R},{colour.G},{colour.B}".ShouldBe(expected);
    }

    [Fact]
    public void TheBoundingBox_IsOfTheCurve_NotOfItsControlPoints_AndOfTheGeometryWithoutItsStroke()
    {
        // A circle's control points reach no further than the circle, but an S-curve's do.
        VectorShape shape = OnlyShape(
            "<defs><linearGradient id=\"g\">" + TwoStops + "</linearGradient></defs>" +
            "<path d=\"M0 0 C 0 100 100 -100 100 0\" fill=\"url(#g)\" stroke=\"url(#g)\" stroke-width=\"20\"/>");
        LinearGradientPaint fill = shape.Fill!.Paint.ShouldBeOfType<LinearGradientPaint>();
        // The curve's extremes are at y = +-28.87 (t = (3 -+ sqrt 3) / 6), not at the control points' +-100.
        At(fill.Transform, 0, 0).Y.ShouldBe(-28.8675f, 1e-3f);
        At(fill.Transform, 1, 1).X.ShouldBe(100f, 1e-3f);
        At(fill.Transform, 1, 1).Y.ShouldBe(28.8675f, 1e-3f);
        shape.Stroke!.Paint.ShouldBe(fill);

        shape.Path.TryGetBounds(out VectorRect bounds).ShouldBeTrue();
        (bounds.X, bounds.Width).ShouldBe((0f, 100f));
        bounds.Height.ShouldBe(57.735f, 1e-3f);

        OnlyShape("<circle cx=\"50\" cy=\"40\" r=\"30\"/>").Path.TryGetBounds(out bounds).ShouldBeTrue();
        bounds.ShouldBe(new VectorRect(20, 10, 60, 60));
        OnlyShape("<path d=\"M0 0 Q 50 100 100 0\" />").Path.TryGetBounds(out bounds).ShouldBeTrue();
        bounds.ShouldBe(new VectorRect(0, 0, 100, 50));
        VectorPath.Empty.TryGetBounds(out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("gradientUnits=\"sideways\"", "gradientUnits")]
    [InlineData("spreadMethod=\"mirror\"", "spreadMethod")]
    [InlineData("gradientTransform=\"twist(3)\"", "gradientTransform")]
    [InlineData("x1=\"left\"", "x1")]
    public void AnInvalidGradientAttribute_IsIgnored_AndReported(string attribute, string what)
    {
        SvgDocument document = Doc($"<defs><linearGradient id=\"g\" {attribute}>{TwoStops}</linearGradient></defs><rect width=\"9\" height=\"9\" fill=\"url(#g)\"/>");
        LinearGradientPaint paint = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Fill!.Paint.ShouldBeOfType<LinearGradientPaint>();
        (paint.X1, paint.X2, paint.Extend).ShouldBe((0f, 1f, GradientExtend.Pad));
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, what, 1));
    }

    [Fact]
    public void AGradient_IsReadOnce_HoweverManyShapesUseIt_AndInheritsLikeAnyPaint()
    {
        SvgDocument document = Doc(
            "<defs><linearGradient id=\"g\" x1=\"oops\">" + TwoStops + "</linearGradient></defs>" +
            "<g fill=\"url(#g)\"><rect width=\"10\" height=\"10\"/><rect x=\"20\" width=\"30\" height=\"10\"/></g>");
        VectorShape[] shapes = [.. document.Scene.Nodes.Cast<VectorShape>()];
        // Each shape gets the gradient over its own box.
        At(shapes[0].Fill!.Paint.ShouldBeOfType<LinearGradientPaint>().Transform, 1, 1).ShouldBe(new Vector2(10, 10));
        At(shapes[1].Fill!.Paint.ShouldBeOfType<LinearGradientPaint>().Transform, 1, 1).ShouldBe(new Vector2(50, 10));
        // The bad attribute is reported once, not once a shape.
        document.Issues.ShouldHaveSingleItem().Count.ShouldBe(1);
    }
}
