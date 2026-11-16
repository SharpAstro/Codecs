using System.Numerics;
using SharpAstro.Svg;
using SharpAstro.Vector;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>Path data and the basic shapes, as the absolute move / line / quad / cubic / close the scene carries.</summary>
public sealed class SvgGeometryTests
{
    [Theory]
    // Coordinates after a moveto's first pair are linetos; relative ones are relative to the point before.
    [InlineData("M10 20 30 40 50 60", "M 10 20 L 30 40 L 50 60")]
    [InlineData("m10 20 30 40 50 60", "M 10 20 L 40 60 L 90 120")]
    [InlineData("M0 0 H10 V10 h-5 v-5", "M 0 0 L 10 0 L 10 10 L 5 10 L 5 5")]
    [InlineData("M0 0 L10 0 l0 10 Z", "M 0 0 L 10 0 L 10 10 Z")]
    // After a closepath the current point is the subpath's start, and drawing on from there is a new subpath.
    [InlineData("m10 10 l5 0 0 5 z l-1 -1", "M 10 10 L 15 10 L 15 15 Z M 10 10 L 9 9")]
    [InlineData("M0 0 L1 1 Z Z", "M 0 0 L 1 1 Z")]
    // S mirrors the previous cubic's second control point, or starts from the current point when there is none.
    [InlineData("M0 0 C 0 10 10 10 10 0 S 20 -10 20 0", "M 0 0 C 0 10 10 10 10 0 C 10 -10 20 -10 20 0")]
    [InlineData("M0 0 S 5 5 10 0", "M 0 0 C 0 0 5 5 10 0")]
    // The same with a control point that is not straight above the end point, so both coordinates are mirrored.
    [InlineData("M0 0 C 0 10 5 10 10 0 S 20 -10 20 0", "M 0 0 C 0 10 5 10 10 0 C 15 -10 20 -10 20 0")]
    [InlineData("M0 0 c 0 10 10 10 10 0 s 10 -10 10 0", "M 0 0 C 0 10 10 10 10 0 C 10 -10 20 -10 20 0")]
    [InlineData("M0 0 Q 5 10 10 0 L 20 0 S 25 5 30 0", "M 0 0 Q 5 10 10 0 L 20 0 C 20 0 25 5 30 0")]
    // T does the same for quadratics, and a T after a T mirrors the mirrored point.
    [InlineData("M0 0 Q 5 10 10 0 T 20 0", "M 0 0 Q 5 10 10 0 Q 15 -10 20 0")]
    [InlineData("M0 0 Q 5 10 10 0 T 20 0 T 30 0", "M 0 0 Q 5 10 10 0 Q 15 -10 20 0 Q 25 10 30 0")]
    [InlineData("M0 0 T 10 0", "M 0 0 Q 0 0 10 0")]
    // A control point left over from an earlier curve is not mirrored once another command has come between.
    [InlineData("M0 0 Q 5 10 10 0 L 20 0 T 30 0", "M 0 0 Q 5 10 10 0 L 20 0 Q 20 0 30 0")]
    [InlineData("M0 0 C 0 10 5 10 10 0 L 20 0 T 30 0", "M 0 0 C 0 10 5 10 10 0 L 20 0 Q 20 0 30 0")]
    [InlineData("M0 0 Q 5 10 10 0 S 25 5 30 0", "M 0 0 Q 5 10 10 0 C 10 0 25 5 30 0")]
    [InlineData("M0 0 q 5 10 10 0 t 10 0", "M 0 0 Q 5 10 10 0 Q 15 -10 20 0")]
    // Numbers run together wherever the grammar still tells them apart.
    [InlineData("M10-20L.5.5 1e1-1E1", "M 10 -20 L 0.5 0.5 L 10 -10")]
    [InlineData("M 1,2,3,4", "M 1 2 L 3 4")]
    [InlineData("\n\tM1 2\r\nL3 4 ", "M 1 2 L 3 4")]
    public void PathData_BecomesAbsoluteSegments(string d, string expected)
    {
        PathOf(d).ShouldBe(expected);
    }

    [Theory]
    [InlineData("M0 0 L10 10 L20", "M 0 0 L 10 10")]
    [InlineData("M0 0 L10 10 X 5 5 L 9 9", "M 0 0 L 10 10")]
    [InlineData("M0 0 L10 10 C 1 2 3 4 5", "M 0 0 L 10 10")]
    [InlineData("M0 0 L10 10 Z 5 5", "M 0 0 L 10 10 Z")]
    [InlineData("M0 0 L10 10 A 5 5 0 2 0 9 9", "M 0 0 L 10 10")]
    public void PathData_WithAnError_IsDrawnUpToTheError_AndReported(string d, string expected)
    {
        SvgDocument document = Doc($"<path d=\"{d}\"/>");
        Describe(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Path).ShouldBe(expected);
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "path data", 1));
    }

    [Theory]
    [InlineData("L10 10")]
    [InlineData("10 10")]
    [InlineData("M")]
    public void PathData_ThatDoesNotStartWithAMoveto_DrawsNothing(string d)
    {
        SvgDocument document = Doc($"<path d=\"{d}\"/>");
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "path data", 1));
    }

    [Fact]
    public void APathOfOnlyMovetos_OrWithNoData_IsNotInTheScene()
    {
        Doc("<path d=\"M1 1 M2 2\"/><path d=\"\"/><path/>").Scene.Nodes.ShouldBeEmpty();
    }

    // ---- Arcs ----------------------------------------------------------------------------------------------------

    [Theory]
    // A half circle of radius 50 from (0,0) to (100,0), centre (50,0). Sweep 1 is the direction of increasing angle,
    // from +x towards +y: starting at the left end (angle 180) that leads to 270, which is (50,-50), up the screen.
    [InlineData("M0 0 A 50 50 0 0 1 100 0", -50f)]
    [InlineData("M0 0 A 50 50 0 1 1 100 0", -50f)]
    [InlineData("M0 0 A 50 50 0 0 0 100 0", 50f)]
    // Radii too small to span the end points are scaled up until they do: here to 50.
    [InlineData("M0 0 A 10 10 0 0 1 100 0", -50f)]
    [InlineData("M0 0 A 1 1 0 0 0 100 0", 50f)]
    // A relative arc, and one with its flags run into the next number.
    [InlineData("M0 0 a 50 50 0 0 1 100 0", -50f)]
    [InlineData("M0 0a50,50 0 01100,0", -50f)]
    public void AHalfCircleArc_FollowsTheCircle_OnTheSideItsSweepFlagNames(string d, float apexY)
    {
        VectorPath path = OnlyShape($"<path d=\"{d}\"/>").Path;
        path.Verbs.ToArray().ShouldBe([PathVerb.Move, PathVerb.Cubic, PathVerb.Cubic]);
        List<Vector2> points = Sample(path);
        foreach (Vector2 p in points)
        {
            Vector2.Distance(p, new Vector2(50, 0)).ShouldBe(50f, 0.02f);
            (p.Y * apexY).ShouldBeGreaterThanOrEqualTo(-0.001f);
        }
        points.ShouldContain(p => Vector2.Distance(p, new Vector2(50, apexY)) < 0.02f);
        points[^1].ShouldBe(new Vector2(100, 0));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheLargeArcFlag_PicksTheLongWayRound_AndTheSweepFlagTheDirection(bool large, bool sweep)
    {
        // From (50,0) to (0,50) with radius 50: the centre is (0,0) or (50,50), a quarter turn or three quarters.
        string d = $"M50 0 A 50 50 0 {(large ? 1 : 0)} {(sweep ? 1 : 0)} 0 50";
        VectorPath path = OnlyShape($"<path d=\"{d}\"/>").Path;
        List<Vector2> points = Sample(path);

        // Sweep 1 from (50,0) to (0,50) round (0,0) is the positive direction; so the small sweep-1 arc and the large
        // sweep-0 arc share the centre (0,0).
        Vector2 centre = large == sweep ? new Vector2(50, 50) : new Vector2(0, 0);
        double turned = 0;
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 a = points[i - 1] - centre, b = points[i] - centre;
            a.Length().ShouldBe(50f, 0.02f);
            turned += Math.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b));
        }
        Math.Abs(turned).ShouldBe(large ? 1.5 * Math.PI : 0.5 * Math.PI, 1e-3);
        Math.Sign(turned).ShouldBe(sweep ? 1 : -1);
        path.Verbs.Length.ShouldBe(large ? 4 : 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(-75)]
    [InlineData(390)]
    public void ARotatedEllipticalArc_StaysOnItsEllipse(double degrees)
    {
        // The end points are the two ends of the ellipse's major axis, so the centre is the origin whatever the flags.
        double phi = degrees * Math.PI / 180;
        var axis = new Vector2((float)(40 * Math.Cos(phi)), (float)(40 * Math.Sin(phi)));
        string d = FormattableString.Invariant($"M{axis.X} {axis.Y} A 40 20 {degrees} 0 1 {-axis.X} {-axis.Y} A 40 20 {degrees} 0 1 {axis.X} {axis.Y}");
        foreach (Vector2 p in Sample(OnlyShape($"<path d=\"{d}\"/>").Path))
        {
            double u = p.X * Math.Cos(phi) + p.Y * Math.Sin(phi), v = -p.X * Math.Sin(phi) + p.Y * Math.Cos(phi);
            (u * u / 1600 + v * v / 400).ShouldBe(1.0, 2e-3);
        }
    }

    [Fact]
    public void AnArcWithAZeroRadius_IsALine_AndOneThatGoesNowhere_IsNothing()
    {
        PathOf("M0 0 A 0 5 0 0 1 10 10").ShouldBe("M 0 0 L 10 10");
        PathOf("M0 0 A 5 0 0 0 1 10 10").ShouldBe("M 0 0 L 10 10");
        // A relative arc's end point is relative, like every other command's.
        PathOf("M5 5 a 0 5 0 0 1 10 10").ShouldBe("M 5 5 L 15 15");
        PathOf("M0 0 L5 5 A 5 5 0 0 1 5 5 L 9 9").ShouldBe("M 0 0 L 5 5 L 9 9");
    }

    // ---- Basic shapes --------------------------------------------------------------------------------------------

    [Fact]
    public void Rect_IsFourSides_FromItsTopLeftClockwise()
    {
        Describe(OnlyShape("<rect x=\"10\" y=\"20\" width=\"30\" height=\"40\"/>").Path).ShouldBe("M 10 20 L 40 20 L 40 60 L 10 60 Z");
        Describe(OnlyShape("<rect width=\"5\" height=\"6\"/>").Path).ShouldBe("M 0 0 L 5 0 L 5 6 L 0 6 Z");
    }

    [Theory]
    [InlineData("rx=\"10\"", 10, 10)]
    [InlineData("ry=\"5\"", 5, 5)]
    [InlineData("rx=\"10\" ry=\"5\"", 10, 5)]
    [InlineData("rx=\"auto\" ry=\"5\"", 5, 5)]
    // Radii are clamped to half the sides, each on its own axis.
    [InlineData("rx=\"100\"", 20, 10)]
    [InlineData("rx=\"100\" ry=\"3\"", 20, 3)]
    public void RoundedRect_UsesOneRadiusForBothWhenOnlyOneIsGiven_AndClampsThem(string radii, float rx, float ry)
    {
        VectorPath path = OnlyShape($"<rect width=\"40\" height=\"20\" {radii}/>").Path;
        path.Verbs[0].ShouldBe(PathVerb.Move);
        path.Coordinates[..2].ToArray().ShouldBe([rx, 0f]);
        path.Verbs.ToArray().Count(v => v == PathVerb.Cubic).ShouldBe(4);
        // Straight sides exist only where a radius leaves room for one.
        path.Verbs.ToArray().Count(v => v == PathVerb.Line).ShouldBe((rx < 20 ? 2 : 0) + (ry < 10 ? 2 : 0));

        // Every corner point is on its corner's ellipse, and everything is inside the rectangle.
        foreach (Vector2 p in Sample(path))
        {
            p.X.ShouldBeInRange(-0.001f, 40.001f);
            p.Y.ShouldBeInRange(-0.001f, 20.001f);
            float dx = Math.Max(0, Math.Max(rx - p.X, p.X - (40 - rx))), dy = Math.Max(0, Math.Max(ry - p.Y, p.Y - (20 - ry)));
            if (dx > 0 && dy > 0) (dx * dx / (rx * rx) + dy * dy / (ry * ry)).ShouldBe(1f, 2e-3f);
        }
    }

    [Fact]
    public void Circle_IsFourArcs_FromItsRightmostPointByTheBottom()
    {
        VectorPath path = OnlyShape("<circle cx=\"50\" cy=\"40\" r=\"30\"/>").Path;
        path.Verbs.ToArray().ShouldBe([PathVerb.Move, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Close]);
        ReadOnlySpan<float> c = path.Coordinates;
        new Vector2(c[0], c[1]).ShouldBe(new Vector2(80, 40));
        new Vector2(c[6], c[7]).ShouldBe(new Vector2(50, 70));
        new Vector2(c[12], c[13]).ShouldBe(new Vector2(20, 40));
        new Vector2(c[18], c[19]).ShouldBe(new Vector2(50, 10));
        new Vector2(c[24], c[25]).ShouldBe(new Vector2(80, 40));
        foreach (Vector2 p in Sample(path)) Vector2.Distance(p, new Vector2(50, 40)).ShouldBe(30f, 0.01f);
    }

    [Fact]
    public void Ellipse_FollowsItsEquation_AndOneRadiusStandsInForAMissingOne()
    {
        foreach (Vector2 p in Sample(OnlyShape("<ellipse cx=\"10\" cy=\"20\" rx=\"40\" ry=\"15\"/>").Path))
        {
            ((p.X - 10) * (p.X - 10) / 1600 + (p.Y - 20) * (p.Y - 20) / 225).ShouldBe(1f, 1e-3f);
        }
        Describe(OnlyShape("<ellipse rx=\"7\"/>").Path).ShouldBe(Describe(OnlyShape("<circle r=\"7\"/>").Path));
    }

    [Fact]
    public void Line_IsOneSegment_AndIsNeverFilled()
    {
        VectorShape line = OnlyShape("<line x1=\"1\" y1=\"2\" x2=\"3\" y2=\"4\" fill=\"red\" stroke=\"blue\"/>");
        Describe(line.Path).ShouldBe("M 1 2 L 3 4");
        line.Fill.ShouldBeNull();
        line.Stroke.ShouldNotBeNull();
        // Without a stroke there is nothing to draw at all.
        Doc("<line x2=\"3\" y2=\"4\"/>").Scene.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public void Polyline_IsOpen_AndPolygon_IsClosed()
    {
        Describe(OnlyShape("<polyline points=\"0,0 10,0 10,10\"/>").Path).ShouldBe("M 0 0 L 10 0 L 10 10");
        Describe(OnlyShape("<polygon points=\"0,0 10,0 10,10\"/>").Path).ShouldBe("M 0 0 L 10 0 L 10 10 Z");
        Describe(OnlyShape("<polygon points=\" 0 0,10 0\n10 10 \"/>").Path).ShouldBe("M 0 0 L 10 0 L 10 10 Z");
    }

    [Fact]
    public void APointsList_WithAnOddCount_KeepsItsCompletePairs_AndIsReported()
    {
        SvgDocument document = Doc("<polyline points=\"0,0 10,0 10\"/>");
        Describe(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Path).ShouldBe("M 0 0 L 10 0");
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "points", 1));
    }

    [Theory]
    [InlineData("<rect width=\"0\" height=\"10\"/>")]
    [InlineData("<rect width=\"10\"/>")]
    [InlineData("<circle cx=\"5\" cy=\"5\"/>")]
    [InlineData("<circle r=\"0\"/>")]
    [InlineData("<ellipse rx=\"5\" ry=\"0\"/>")]
    [InlineData("<ellipse/>")]
    [InlineData("<polyline points=\"5,5\"/>")]
    [InlineData("<polygon/>")]
    public void AShapeWithNoExtent_IsNotInTheScene(string shape)
    {
        Doc(shape).Scene.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public void ANegativeSize_IsAnError_AndIsReported()
    {
        SvgDocument document = Doc("<rect width=\"-5\" height=\"5\"/><circle r=\"-1\"/>");
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "negative rect size", 1));
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "negative radius", 1));
    }

    [Theory]
    [InlineData("1in", 96f)]
    [InlineData("72pt", 96f)]
    [InlineData("6pc", 96f)]
    [InlineData("25.4mm", 96f)]
    [InlineData("2.54cm", 96f)]
    [InlineData("101.6Q", 96f)]
    [InlineData("12px", 12f)]
    [InlineData("12", 12f)]
    [InlineData("1e1", 10f)]
    [InlineData("2em", 32f)]   // the initial font size is 16
    [InlineData("2ex", 16f)]   // taken as half an em
    [InlineData("50%", 100f)]  // of the viewport's width, 200
    public void Lengths_ResolveToPixels(string width, float expected)
    {
        VectorShape rect = OnlyShape($"<rect width=\"{width}\" height=\"1\"/>", "width=\"200\" height=\"100\"");
        rect.Path.Coordinates[2].ShouldBe(expected, 1e-3f);
    }

    [Fact]
    public void Percentages_AreOfTheViewBox_WidthHeightOrDiagonal()
    {
        const string root = "width=\"10\" height=\"10\" viewBox=\"0 0 400 300\"";
        Describe(OnlyShape("<rect x=\"25%\" y=\"10%\" width=\"50%\" height=\"50%\"/>", root).Path).ShouldBe("M 100 30 L 300 30 L 300 180 L 100 180 Z");
        // A radius is a percentage of sqrt((w^2 + h^2) / 2), 353.553 here.
        OnlyShape("<circle r=\"10%\"/>", root).Path.Coordinates[0].ShouldBe(35.3553f, 1e-3f);
    }

    [Fact]
    public void AnEmLength_UsesTheElementsOwnFontSize()
    {
        Describe(OnlyShape("<g font-size=\"10\"><rect width=\"2em\" height=\"1em\" style=\"font-size: 200%\"/></g>").Path)
            .ShouldBe("M 0 0 L 40 0 L 40 20 L 0 20 Z");
    }

    [Fact]
    public void ACoordinateTooLargeForAFloat_DropsTheShape_AndIsReported()
    {
        SvgDocument document = Doc("<path d=\"M0 0 L1e300 0\" stroke=\"red\"/><rect width=\"1\" height=\"1\"/>");
        document.Scene.Nodes.Length.ShouldBe(1);
        document.Issues.ShouldContain(new SvgIssue(SvgIssueKind.Invalid, "coordinate out of range", 1));
    }
}
