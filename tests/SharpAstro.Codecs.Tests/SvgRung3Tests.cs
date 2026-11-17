using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 3 as a scene: clips, masks, markers, <c>paint-order</c>, images and patterns. What these LOOK like is held
/// against a browser in <see cref="SvgBrowserOracleTests"/>; here is what pixels cannot say: that the scene stays flat
/// where nothing needs a layer, what is reported, what a document may ask for, and that a picture is carried and not
/// fetched.
/// </summary>
public sealed class SvgRung3Tests
{
    private const string Box = "<rect id=\"box\" x=\"10\" y=\"20\" width=\"40\" height=\"20\"/>";

    private static SvgIssue Only(SvgDocument document) => document.Issues.ShouldHaveSingleItem();

    // ---- clip-path ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AClipPath_IsAGroupAroundTheElement_KeptToTheClipsOutlines()
    {
        SvgDocument document = Doc("<clipPath id=\"c\"><circle cx=\"30\" cy=\"30\" r=\"8\"/><rect width=\"5\" height=\"5\" clip-rule=\"evenodd\"/></clipPath>" +
            Box.Replace("/>", " clip-path=\"url(#c)\" opacity=\"0.5\"/>"));
        document.Issues.ShouldBeEmpty();

        VectorGroup group = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Opacity.ShouldBe(0.5f);
        group.Mask.ShouldBeNull();
        // The shape inside keeps its own paint untouched: the opacity is the layer's.
        VectorShape shape = group.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        shape.Id.ShouldBe("box");
        shape.Fill!.Opacity.ShouldBe(1f);

        VectorClip clip = group.Clip.ShouldNotBeNull();
        clip.Intersect.ShouldBeNull();
        clip.Shapes.Length.ShouldBe(2);
        clip.Shapes[0].Rule.ShouldBe(FillRule.NonZero);
        clip.Shapes[1].Rule.ShouldBe(FillRule.EvenOdd);
        Describe(clip.Shapes[1].Path).ShouldBe("M 0 0 L 5 0 L 5 5 L 0 5 Z");
        clip.Shapes[1].Transform.ShouldBe(Matrix3x2.Identity);
    }

    [Fact]
    public void BoundingBoxUnits_PutTheClipInTheElementsBox_AndUserUnitsInItsCoordinates()
    {
        VectorGroup box = Doc("<clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect width=\"0.5\" height=\"1\"/></clipPath>" +
            $"<g transform=\"translate(100 0)\">{Box.Replace("/>", " clip-path=\"url(#c)\"/>")}</g>").Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        // Half the box's width, all its height, where the box is once the group has moved it.
        Flatten(box.Clip!.Shapes[0].Path, box.Clip.Shapes[0].Transform).ShouldHaveSingleItem()
            .ShouldBe([new(110, 20), new(130, 20), new(130, 40), new(110, 40)]);

        VectorGroup user = Doc("<clipPath id=\"c\" transform=\"scale(2)\"><rect width=\"5\" height=\"5\" transform=\"translate(1 0)\"/></clipPath>" +
            $"<g transform=\"translate(100 0)\">{Box.Replace("/>", " clip-path=\"url(#c)\"/>")}</g>").Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        Flatten(user.Clip!.Shapes[0].Path, user.Clip.Shapes[0].Transform).ShouldHaveSingleItem()
            .ShouldBe([new(102, 0), new(112, 0), new(112, 10), new(102, 10)]);
    }

    [Fact]
    public void AClipOnAClipPathsChild_CutsThatOutline_AndOneOnTheClipPath_CutsAllOfIt()
    {
        VectorGroup group = Doc(
            "<clipPath id=\"c\" clip-path=\"url(#outer)\"><rect width=\"30\" height=\"30\" clip-path=\"url(#inner)\"/><circle cx=\"40\" cy=\"40\" r=\"5\"/></clipPath>" +
            "<clipPath id=\"inner\"><circle cx=\"10\" cy=\"10\" r=\"9\"/></clipPath><clipPath id=\"outer\"><rect width=\"42\" height=\"42\"/></clipPath>" +
            Box.Replace("/>", " clip-path=\"url(#c)\"/>")).Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        VectorClip clip = group.Clip.ShouldNotBeNull();
        clip.Shapes.Length.ShouldBe(2);
        clip.Shapes[0].Clip.ShouldNotBeNull().Shapes.ShouldHaveSingleItem().Path.Verbs.ToArray().ShouldContain(PathVerb.Cubic);
        clip.Shapes[1].Clip.ShouldBeNull();
        Describe(clip.Intersect.ShouldNotBeNull().Shapes.ShouldHaveSingleItem().Path).ShouldBe("M 0 0 L 42 0 L 42 42 L 0 42 Z");
    }

    [Theory]
    // A reference that leads nowhere, or not to a clipPath, does not hide the element: it is drawn unclipped.
    [InlineData("<clipPath id=\"other\"/>", "url(#missing)", true, SvgIssueKind.Invalid, "clip-path: no element with that id")]
    [InlineData("<mask id=\"m\"/>", "url(#m)", true, SvgIssueKind.Invalid, "clip-path: not a clipPath element")]
    [InlineData("", "inset(2px)", true, SvgIssueKind.Unsupported, "clip-path: a value that is not a reference")]
    [InlineData("", "url(other.svg#c)", true, SvgIssueKind.Unsupported, "clip-path: an element in another document")]
    // A clipPath with nothing in it, or nothing that makes a region, clips everything away, and that is not an error.
    [InlineData("<clipPath id=\"c\"/>", "url(#c)", false, null, null)]
    [InlineData("<clipPath id=\"c\"><rect width=\"9\" height=\"9\" display=\"none\"/><rect width=\"9\" height=\"9\" visibility=\"hidden\"/></clipPath>", "url(#c)", false, null, null)]
    [InlineData("<clipPath id=\"c\"><rect width=\"9\" height=\"0\"/><line x2=\"50\" y2=\"50\"/></clipPath>", "url(#c)", false, null, null)]
    [InlineData("<clipPath id=\"c\" transform=\"scale(0)\"><rect width=\"9\" height=\"9\"/></clipPath>", "url(#c)", false, null, null)]
    // Only shapes make a region: a group in a clipPath is not looked at, and says so.
    [InlineData("<clipPath id=\"c\"><g><rect width=\"99\" height=\"99\"/></g></clipPath>", "url(#c)", false, SvgIssueKind.Invalid, "clipPath: <g> is not a shape")]
    [InlineData("<clipPath id=\"c\"><text>words</text></clipPath>", "url(#c)", false, SvgIssueKind.Unsupported, "text in a clipPath")]
    public void WhatAClipPathPropertyCannotUse(string defs, string value, bool drawn, SvgIssueKind? kind, string? what)
    {
        SvgDocument document = Doc(defs + Box.Replace("/>", $" clip-path=\"{value}\"/>"));
        if (drawn) document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Id.ShouldBe("box");
        else document.Scene.Nodes.ShouldBeEmpty();
        if (kind is null) document.Issues.ShouldBeEmpty();
        else Only(document).ShouldBe(new SvgIssue(kind.Value, what!, 1));
    }

    [Fact]
    public void AClipThatLeadsBackToItself_IsDroppedWhereItClosesTheCircle_AndTheRestStillClips()
    {
        // Measured in a browser: the element is drawn, clipped by the outline, without the clip that refers back.
        SvgDocument document = Doc("<clipPath id=\"loop\"><rect width=\"20\" height=\"20\" clip-path=\"url(#loop)\"/></clipPath>" + Box.Replace("/>", " clip-path=\"url(#loop)\"/>"));
        Only(document).ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "clip-path: circular reference", 1));
        VectorClipShape outline = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>().Clip!.Shapes.ShouldHaveSingleItem();
        outline.Clip.ShouldBeNull();
        Describe(outline.Path).ShouldBe("M 0 0 L 20 0 L 20 20 L 0 20 Z");
    }

    [Fact]
    public void AUseInAClipPath_StandsForOneShape_MovedAsAUseMovesIt()
    {
        SvgDocument document = Doc("<defs><path id=\"q\" d=\"M0 0 h3 v3 z\" transform=\"scale(2)\"/><g id=\"group\"><rect width=\"9\" height=\"9\"/></g></defs>" +
            "<clipPath id=\"c\"><use href=\"#q\" x=\"4\" y=\"1\" transform=\"translate(10 0)\"/><use href=\"#group\"/></clipPath>" + Box.Replace("/>", " clip-path=\"url(#c)\"/>"));
        Only(document).ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "clipPath: a use of something that is not a shape", 1));
        VectorClipShape outline = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>().Clip!.Shapes.ShouldHaveSingleItem();
        // translate(10 0), then the use's x and y, then the path's own scale(2).
        Flatten(outline.Path, outline.Transform).ShouldHaveSingleItem().ShouldBe([new(14, 1), new(20, 1), new(20, 7)]);
    }

    // ---- mask ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", VectorMaskKind.Luminance)]
    [InlineData("mask-type=\"alpha\"", VectorMaskKind.Alpha)]
    [InlineData("style=\"mask-type: alpha\"", VectorMaskKind.Alpha)]
    [InlineData("mask-type=\"alpha\" style=\"mask-type: luminance\"", VectorMaskKind.Luminance)]
    public void AMask_IsAPictureOnTheGroup_OfTheKindTheMaskElementSays(string attributes, VectorMaskKind kind)
    {
        SvgDocument document = Doc($"<mask id=\"m\" {attributes}><circle cx=\"30\" cy=\"30\" r=\"8\" fill=\"#fff\"/></mask>" + Box.Replace("/>", " mask=\"url(#m)\"/>"));
        document.Issues.ShouldBeEmpty();
        VectorGroup group = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Clip.ShouldBeNull();
        VectorMask mask = group.Mask.ShouldNotBeNull();
        mask.Kind.ShouldBe(kind);
        // The circle sits inside the mask's area (the box grown by a tenth each way), so nothing clips the picture.
        FillColor(mask.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(255, 255, 255));
    }

    [Fact]
    public void WhatAMaskDrawsOutsideItsArea_IsCutOff_AndTheAreaIsTheBoxGrownByATenthUnlessGiven()
    {
        VectorGroup byDefault = Doc("<mask id=\"m\"><rect width=\"100\" height=\"100\" fill=\"#fff\"/></mask>" + Box.Replace("/>", " mask=\"url(#m)\"/>"))
            .Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        VectorClipShape area = byDefault.Mask!.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>().Clip!.Shapes.ShouldHaveSingleItem();
        // The box is 10,20 40x20: a tenth of its width and height further out on every side.
        Flatten(area.Path, area.Transform).ShouldHaveSingleItem().ShouldBe([new(6, 18), new(54, 18), new(54, 42), new(6, 42)]);

        VectorGroup given = Doc("<mask id=\"m\" maskUnits=\"userSpaceOnUse\" x=\"1\" y=\"2\" width=\"30\" height=\"25%\"><rect width=\"100\" height=\"100\" fill=\"#fff\"/></mask>" +
            Box.Replace("/>", " mask=\"url(#m)\"/>")).Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        area = given.Mask!.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>().Clip!.Shapes.ShouldHaveSingleItem();
        Flatten(area.Path, area.Transform).ShouldHaveSingleItem().ShouldBe([new(1, 2), new(31, 2), new(31, 27), new(1, 27)]);
    }

    [Fact]
    public void MaskContentInBoundingBoxUnits_IsDrawnAcrossTheBox()
    {
        VectorGroup group = Doc("<mask id=\"m\" maskContentUnits=\"objectBoundingBox\"><rect x=\"0.25\" width=\"0.5\" height=\"1\" fill=\"#fff\"/></mask>" +
            Box.Replace("/>", " mask=\"url(#m)\"/>")).Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        var shape = group.Mask!.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        Flatten(shape.Path, shape.Transform).ShouldHaveSingleItem().ShouldBe([new(20, 20), new(40, 20), new(40, 40), new(20, 40)]);
    }

    [Theory]
    [InlineData("<mask id=\"m\"/>", "url(#m)", false, null)]
    [InlineData("<mask id=\"m\" width=\"0\"><rect width=\"99\" height=\"99\" fill=\"#fff\"/></mask>", "url(#m)", false, null)]
    [InlineData("", "url(#missing)", true, "mask: no element with that id")]
    [InlineData("<clipPath id=\"c\"/>", "url(#c)", true, "mask: not a mask element")]
    public void WhatAMaskPropertyCannotUse(string defs, string value, bool drawn, string? invalid)
    {
        SvgDocument document = Doc(defs + Box.Replace("/>", $" mask=\"{value}\"/>"));
        if (drawn) document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        else document.Scene.Nodes.ShouldBeEmpty();
        if (invalid is null) document.Issues.ShouldBeEmpty();
        else Only(document).ShouldBe(new SvgIssue(SvgIssueKind.Invalid, invalid, 1));
    }

    [Fact]
    public void AMaskThatLeadsBackToItself_AndAMaskOnAMaskElement_AreAsIfNotWritten()
    {
        // Both measured in a browser. The mask that refers back is dropped where it closes the circle...
        SvgDocument loop = Doc("<mask id=\"m\"><rect width=\"20\" height=\"28\" fill=\"#fff\" mask=\"url(#m)\"/></mask>" + Box.Replace("/>", " mask=\"url(#m)\"/>"));
        Only(loop).ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "mask: circular reference", 1));
        VectorMask mask = loop.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>().Mask.ShouldNotBeNull();
        Dump(loop.Scene).Split("mask Luminance").Length.ShouldBe(2);
        mask.Children.Length.ShouldBe(1);

        // ...and a mask property on a mask element does nothing at all.
        SvgDocument onMask = Doc("<mask id=\"m\" mask=\"url(#half)\"><rect x=\"10\" y=\"20\" width=\"40\" height=\"20\" fill=\"#fff\"/></mask>" +
            "<mask id=\"half\"><rect width=\"1\" height=\"1\" fill=\"#fff\"/></mask>" + Box.Replace("/>", " mask=\"url(#m)\"/>"));
        onMask.Issues.ShouldBeEmpty();
        Dump(onMask.Scene).Split("mask Luminance").Length.ShouldBe(2);
    }

    // ---- markers ------------------------------------------------------------------------------------------------------

    private static string Angles(string d, int[]? ends = null)
    {
        VectorPath path = OnlyShape($"<path d=\"{d}\" fill=\"none\" stroke=\"black\"/>").Path;
        if (ends is null)
        {
            var list = new List<int>();
            SvgGeometry.ParsePath(d, new PathBuilder(), list);
            ends = [.. list];
        }
        return string.Join(" ", SvgSceneBuilder.MarkerVertices(path, ends).Select(v => FormattableString.Invariant($"{v.Point.X:0.#},{v.Point.Y:0.#}@{MathF.Round(v.Angle):0}")));
    }

    [Theory]
    // An open line: both ends face along it.
    [InlineData("M0 0 L10 0", "0,0@0 10,0@0")]
    // A corner faces half way between the way in and the way out (y runs down, so "down" is +90).
    [InlineData("M0 0 L10 0 L10 10", "0,0@0 10,0@45 10,10@90")]
    [InlineData("M0 0 L10 0 L0 1", "0,0@0 10,0@87 0,1@174")]
    // A curve leaves along its first control point and arrives along its last.
    [InlineData("M0 0 C 0 10 10 10 10 0", "0,0@90 10,0@-90")]
    [InlineData("M0 0 Q 10 0 10 10 T 20 20", "0,0@0 10,10@90 20,20@0")]
    // A closed subpath: one more vertex where it closes, and it and the first face between the closing line and the first segment.
    [InlineData("M0 0 L10 0 L10 10 Z", "0,0@-68 10,0@45 10,10@158 0,0@-68")]
    // A second subpath starts afresh: its first vertex only leaves, the one before it only arrives.
    [InlineData("M0 0 L10 0 M20 0 L20 10", "0,0@0 10,0@0 20,0@90 20,10@90")]
    // An arc is one command and one vertex, however many curves it is drawn as.
    [InlineData("M0 0 A 10 10 0 1 1 20 0 L 30 0", "0,0@-90 20,0@45 30,0@0")]
    public void AVertex_FacesAlongThePath_HalfWayBetweenArrivingAndLeaving(string d, string expected)
    {
        Angles(d).ShouldBe(expected);
    }

    [Fact]
    public void WithoutTheCommandEnds_AnArcWouldBeAVertexForEveryCurveItBecame()
    {
        // The control: every verb as its own command gives the arc's inner joints as vertices. This is what asking the
        // parser where commands end is for.
        VectorPath path = OnlyShape("<path d=\"M0 0 A 10 10 0 1 1 20 0\" fill=\"none\" stroke=\"black\"/>").Path;
        path.Verbs.Length.ShouldBeGreaterThan(2);
        SvgSceneBuilder.MarkerVertices(path, null).Count.ShouldBe(path.Verbs.Length);
        SvgSceneBuilder.MarkerVertices(path, [1, path.Verbs.Length]).Count.ShouldBe(2);
    }

    private const string Arrow = "<marker id=\"a\" markerWidth=\"4\" markerHeight=\"4\" refX=\"4\" refY=\"2\" orient=\"auto\"><path id=\"head\" d=\"M0 0 L4 2 L0 4 Z\"/></marker>";

    [Fact]
    public void AMarker_IsItsContentDrawnAtTheVertex_TurnedAndScaledByTheStrokeWidth()
    {
        SvgDocument document = Doc(Arrow + "<path id=\"line\" d=\"M10 10 V 30\" stroke=\"black\" stroke-width=\"3\" marker-end=\"url(#a)\"/>");
        document.Issues.ShouldBeEmpty();
        document.Scene.Nodes.Length.ShouldBe(2);
        document.Scene.Nodes[0].Id.ShouldBe("line");
        var head = document.Scene.Nodes[1].ShouldBeOfType<VectorShape>();
        head.Id.ShouldBe("head");
        // The path runs down. The reference point (4, 2), the arrow's tip, lands on the end; the arrow is three times
        // its size and turned a quarter, so its back edge is 12 above the end and 12 wide.
        List<Vector2> points = Flatten(head.Path, head.Transform).ShouldHaveSingleItem();
        points[1].X.ShouldBe(10, 1e-3);
        points[1].Y.ShouldBe(30, 1e-3);
        points[0].X.ShouldBe(16, 1e-3);
        points[0].Y.ShouldBe(18, 1e-3);
        points[2].X.ShouldBe(4, 1e-3);
        points[2].Y.ShouldBe(18, 1e-3);
        // Its fill is the marker's own (black by default), not the path's lack of one.
        FillColor(head).ShouldBe(new Rgba32(0, 0, 0));
    }

    [Theory]
    [InlineData("marker-start=\"url(#a)\"", 1)]
    [InlineData("marker-mid=\"url(#a)\"", 2)]
    [InlineData("marker-end=\"url(#a)\"", 1)]
    [InlineData("style=\"marker: url(#a)\"", 4)]
    // The shorthand is a property of style sheets; as an attribute a browser does not read it, and neither is it read here.
    [InlineData("marker=\"url(#a)\"", 0)]
    [InlineData("marker-mid=\"url(#a)\" style=\"marker: none\"", 0)]
    public void WhichVerticesGetAMarker(string attributes, int markers)
    {
        SvgDocument document = Doc(Arrow + $"<polyline points=\"0,0 10,0 10,10 0,10\" fill=\"none\" stroke=\"red\" {attributes}/>");
        document.Issues.ShouldBeEmpty();
        document.Scene.Nodes.Count(node => node.Id == "head").ShouldBe(markers);
    }

    [Fact]
    public void MarkersAreForShapesWithVertices_AndInheritFromTheMarkersOwnAncestors()
    {
        // A rect has none, whatever the property says.
        Doc(Arrow + "<rect width=\"9\" height=\"9\" marker-start=\"url(#a)\" marker-mid=\"url(#a)\"/>").Scene.Nodes.ShouldHaveSingleItem();

        // The marker's content takes its fill from the marker's parent, not from the red path that carries it; and a
        // marker is drawn although it, or what it is in, is display: none.
        SvgDocument document = Doc("<g fill=\"blue\" display=\"none\"><marker id=\"a\" markerWidth=\"2\" markerHeight=\"2\"><rect id=\"head\" width=\"1\" height=\"1\"/></marker></g>" +
            "<line x2=\"9\" stroke=\"red\" fill=\"red\" marker-end=\"url(#a)\"/>");
        FillColor(document.Scene.Nodes[1].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0, 0, 255));
    }

    [Theory]
    // markerUnits: the stroke's width (the default) or the path's own units; orient: an angle in any unit, or along the path.
    [InlineData("", "stroke-width=\"2\"", 2, 0)]
    [InlineData("markerUnits=\"userSpaceOnUse\"", "stroke-width=\"2\"", 1, 0)]
    [InlineData("orient=\"90\"", "", 1, 90)]
    [InlineData("orient=\"0.25turn\"", "", 1, 90)]
    [InlineData("orient=\"100grad\"", "", 1, 90)]
    [InlineData("orient=\"auto\"", "", 1, 45)]
    [InlineData("orient=\"auto-start-reverse\"", "", 1, 225)]
    public void AMarkersSizeAndTurn(string markerAttributes, string pathAttributes, float scale, float degrees)
    {
        SvgDocument document = Doc($"<marker id=\"a\" {markerAttributes}><rect id=\"head\" width=\"1\" height=\"1\"/></marker>" +
            $"<path d=\"M5 5 L 9 9\" stroke=\"red\" {pathAttributes} marker-start=\"url(#a)\"/>");
        document.Issues.ShouldBeEmpty();
        Matrix3x2 m = document.Scene.Nodes.Single(node => node.Id == "head").ShouldBeOfType<VectorShape>().Transform;
        // Where the unit vector along x ends up says both.
        var along = new Vector2(m.M11, m.M12);
        along.Length().ShouldBe(scale, 1e-4);
        float turn = MathF.Atan2(along.Y, along.X) * 180 / MathF.PI;
        ((turn - degrees + 540) % 360 - 180).ShouldBe(0, 1e-3);
        (m.M31, m.M32).ShouldBe((5f, 5f));
    }

    [Fact]
    public void AMarkerClipsToItsBox_OnlyWhenItsContentLeavesIt_AndItsOpacityFadesItWhole()
    {
        string Marked(string attributes, string content) =>
            Dump(Doc($"<marker id=\"a\" markerWidth=\"2\" markerHeight=\"2\" {attributes}>{content}</marker><line x2=\"9\" stroke=\"red\" marker-end=\"url(#a)\"/>").Scene);

        Marked("", "<rect width=\"2\" height=\"2\"/>").ShouldNotContain("group");
        Marked("", "<rect width=\"3\" height=\"2\"/>").ShouldContain("clip\n");
        Marked("overflow=\"visible\"", "<rect width=\"3\" height=\"2\"/>").ShouldNotContain("group");
        // Measured in a browser: opacity on the marker element applies to the marker.
        Marked("opacity=\"0.25\"", "<rect width=\"1\" height=\"1\"/><rect width=\"2\" height=\"1\"/>").ShouldContain("group id=(none) opacity=0.25");
        string both = Marked("opacity=\"0.25\"", "<rect width=\"3\" height=\"2\"/>");
        both.ShouldContain("group id=(none) opacity=0.25\n    clip\n");
        both.Split("group").Length.ShouldBe(2);
    }

    [Fact]
    public void BoundingBoxUnitsOnAShapeWithMarkers_AreOfTheShapesOwnOutline_NotOfItsMarkers()
    {
        // The arrow at the end is 8 wide and reaches to x = 34; the path's own box ends at 30.
        SvgDocument document = Doc(Arrow + "<clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect width=\"1\" height=\"1\"/></clipPath>" +
            "<path d=\"M10 10 H30 V20\" fill=\"none\" stroke=\"black\" stroke-width=\"2\" marker-end=\"url(#a)\" clip-path=\"url(#c)\"/>");
        document.Issues.ShouldBeEmpty();
        VectorGroup group = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Children.Length.ShouldBe(2);
        VectorClipShape outline = group.Clip!.Shapes.ShouldHaveSingleItem();
        Flatten(outline.Path, outline.Transform).ShouldHaveSingleItem().ShouldBe([new(10, 10), new(30, 10), new(30, 20), new(10, 20)]);
    }

    [Fact]
    public void AMarkerThatCarriesItself_IsDrawnOnce()
    {
        SvgDocument document = Doc("<marker id=\"a\" overflow=\"visible\"><path id=\"inner\" d=\"M0 0 H2\" stroke=\"blue\" marker-end=\"url(#a)\"/></marker>" +
            "<line x2=\"9\" stroke=\"red\" marker-end=\"url(#a)\"/>");
        Only(document).ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "marker: circular reference", 1));
        document.Scene.Nodes.Count(node => node.Id == "inner").ShouldBe(1);
    }

    // ---- paint-order --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("normal", "FS")]
    [InlineData("fill", "FS")]
    [InlineData("fill stroke markers", "FS")]
    [InlineData("stroke", "S F")]
    [InlineData("stroke fill", "S F")]
    [InlineData("markers", "FS")]
    [InlineData("markers stroke", "S F")]
    public void PaintOrder_PutsTheStrokeUnderTheFill_AsTwoShapes_AndOtherwiseLeavesOne(string value, string expected)
    {
        SvgDocument document = Doc($"<circle r=\"5\" fill=\"red\" stroke=\"blue\" paint-order=\"{value}\"/>");
        document.Issues.ShouldBeEmpty();
        string.Join(" ", document.Scene.Nodes.Cast<VectorShape>().Select(shape => (shape.Fill is null ? "" : "F") + (shape.Stroke is null ? "" : "S"))).ShouldBe(expected);
    }

    [Theory]
    [InlineData("normal", "line head head")]
    [InlineData("stroke", "line head head")]
    [InlineData("markers", "head head line")]
    [InlineData("markers fill", "head head line")]
    public void PaintOrder_PutsTheMarkersWhereItSays(string value, string expected)
    {
        SvgDocument document = Doc(Arrow + $"<path id=\"line\" d=\"M0 0 H9\" fill=\"none\" stroke=\"red\" marker-start=\"url(#a)\" marker-end=\"url(#a)\" paint-order=\"{value}\"/>");
        string.Join(" ", document.Scene.Nodes.Select(node => node.Id)).ShouldBe(expected);
    }

    [Fact]
    public void PaintOrder_InheritsLikeAnyProperty_RefusesNonsense_AndFadesItsPiecesAsOne()
    {
        Doc("<g paint-order=\"stroke\"><circle r=\"5\" fill=\"red\" stroke=\"blue\"/></g>").Scene.Nodes.Length.ShouldBe(2);

        SvgDocument nonsense = Doc("<circle r=\"5\" fill=\"red\" stroke=\"blue\" paint-order=\"stroke stroke\"/><circle r=\"5\" fill=\"red\" stroke=\"blue\" paint-order=\"outline\"/>");
        Only(nonsense).ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "paint-order", 2));
        nonsense.Scene.Nodes.Length.ShouldBe(2);

        // Stroke and fill overlap along the outline, so with opacity the two pieces are one layer.
        VectorGroup group = Doc("<circle r=\"5\" fill=\"red\" stroke=\"blue\" paint-order=\"stroke\" opacity=\"0.5\"/>").Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Opacity.ShouldBe(0.5f);
        group.Children.Length.ShouldBe(2);
    }

    // ---- image --------------------------------------------------------------------------------------------------------

    private static readonly byte[] SomeBytes = [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3, 250, 251, 252, 253, 254, 255];

    [Fact]
    public void AnEmbeddedPicture_IsCarriedAsTheFileItIs_WithWhereAndHowItIsFitted()
    {
        string base64 = Convert.ToBase64String(SomeBytes);
        SvgDocument document = Doc($"<image id=\"i\" x=\"5\" y=\"6\" width=\"20\" height=\"10\" opacity=\"0.5\" transform=\"translate(100 0)\" preserveAspectRatio=\"xMaxYMin slice\" href=\"data:image/PNG;base64,{base64}\"/>");
        document.Issues.ShouldBeEmpty();
        VectorImage image = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorImage>();
        image.Data.ToArray().ShouldBe(SomeBytes);
        (image.Id, image.MediaType, image.Source).ShouldBe(("i", "image/png", null));
        image.Bounds.ShouldBe(new VectorRect(5, 6, 20, 10));
        (image.Transform.M31, image.Opacity, image.Fit, image.AlignX, image.AlignY).ShouldBe((100f, 0.5f, ImageFit.Slice, 1f, 0f));
    }

    [Theory]
    [InlineData("", ImageFit.Meet, 0.5f, 0.5f)]
    [InlineData("preserveAspectRatio=\"none\"", ImageFit.Stretch, 0.5f, 0.5f)]
    [InlineData("preserveAspectRatio=\"xMinYMax\"", ImageFit.Meet, 0f, 1f)]
    [InlineData("preserveAspectRatio=\"xMidYMid slice\"", ImageFit.Slice, 0.5f, 0.5f)]
    public void HowAPictureIsFitted(string attribute, ImageFit fit, float alignX, float alignY)
    {
        VectorImage image = Doc($"<image width=\"2\" height=\"2\" {attribute} href=\"a.png\"/>").Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorImage>();
        (image.Fit, image.AlignX, image.AlignY).ShouldBe((fit, alignX, alignY));
    }

    [Theory]
    // Base64 broken over lines, without its padding, and with its + and / percent-encoded.
    [InlineData("data:image/png;base64,iVBORwABAgP6\n  +/z9/v8=")]
    [InlineData("data:image/png;charset=x;base64,iVBORwABAgP6+/z9/v8")]
    [InlineData("data:image/png;base64,iVBORwABAgP6%2B%2Fz9%2Fv8%3D")]
    // Not base64: bytes as they are, or percent-encoded.
    [InlineData("data:application/octet-stream,%89PNG%00%01%02%03%FA%FB%FC%FD%FE%FF")]
    [InlineData("data:,%89PNG%00%01%02%03%fa%fb%fc%fd%fe%ff")]
    public void TheWaysADataUriIsWritten(string uri)
    {
        SvgSceneBuilder.TryDataUri(uri, out byte[]? bytes, out string? type).ShouldBeTrue();
        bytes.ShouldBe(SomeBytes);
        type.ShouldBe(uri.StartsWith("data:,") ? null : uri.Contains("octet") ? "application/octet-stream" : "image/png");
    }

    [Fact]
    public void APictureInAnotherFile_IsNamed_AndNothingIsFetched()
    {
        // There is nothing here that could fetch: the scene carries the address and that is all. An address that
        // would hang or fail if it were opened reads at once.
        foreach (string href in (string[])["https://203.0.113.1/never.png", "file:///C:/no/such/file.png", "pictures/a b.jpg", "\\\\nowhere\\share\\x.png"])
        {
            SvgDocument document = Doc($"<image width=\"4\" height=\"4\" href=\"{href}\"/>");
            document.Issues.ShouldBeEmpty();
            VectorImage image = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorImage>();
            (image.Source, image.Data.Length, image.MediaType).ShouldBe((href, 0, null));
        }
    }

    [Theory]
    [InlineData("<image href=\"a.png\"/>", SvgIssueKind.Unsupported, "image with no width or height (sized by the picture)")]
    [InlineData("<image width=\"auto\" height=\"4\" href=\"a.png\"/>", SvgIssueKind.Unsupported, "image with no width or height (sized by the picture)")]
    [InlineData("<image width=\"4\" height=\"4\" href=\"data:image/svg+xml,%3Csvg/%3E\"/>", SvgIssueKind.Unsupported, "image that is an SVG document")]
    [InlineData("<image width=\"4\" height=\"4\" href=\"data:image/png;base64,!!!!\"/>", SvgIssueKind.Invalid, "image: a data URI that cannot be read")]
    [InlineData("<image width=\"4\" height=\"4\" href=\"data:image/png;base64\"/>", SvgIssueKind.Invalid, "image: a data URI that cannot be read")]
    [InlineData("<image width=\"4\" height=\"4\" href=\"#box\"/>", SvgIssueKind.Invalid, "image: a reference to an element, not to a picture")]
    [InlineData("<image width=\"-4\" height=\"4\" href=\"a.png\"/>", SvgIssueKind.Invalid, "negative image size")]
    public void APictureThatCannotBeCarried_IsLeftOutAndReported(string body, SvgIssueKind kind, string what)
    {
        SvgDocument document = Doc(body);
        document.Scene.Nodes.ShouldBeEmpty();
        Only(document).ShouldBe(new SvgIssue(kind, what, 1));
    }

    [Fact]
    public void APictureWithNoSizeOrNoAddress_OrHidden_IsNothing_AndSaysNothing()
    {
        SvgDocument document = Doc("<image width=\"0\" height=\"4\" href=\"a.png\"/><image width=\"4\" height=\"4\"/><image width=\"4\" height=\"4\" href=\" \"/>" +
            "<image width=\"4\" height=\"4\" href=\"a.png\" visibility=\"hidden\"/><image width=\"4\" height=\"4\" href=\"a.png\" display=\"none\"/>");
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void APictureUsedManyTimes_IsDecodedFromItsUriOnce_AndEveryInstanceSharesTheBytes()
    {
        string base64 = Convert.ToBase64String(SomeBytes);
        SvgDocument document = Doc($"<defs><image id=\"i\" width=\"4\" height=\"4\" href=\"data:image/png;base64,{base64}\"/></defs>" +
            string.Concat(Enumerable.Range(0, 20).Select(n => $"<use href=\"#i\" x=\"{n}\"/>")));
        VectorImage[] images = [.. document.Scene.Nodes.Cast<VectorImage>()];
        images.Length.ShouldBe(20);
        MemoryMarshal.TryGetArray(images[0].Data, out ArraySegment<byte> first).ShouldBeTrue();
        foreach (VectorImage image in images)
        {
            MemoryMarshal.TryGetArray(image.Data, out ArraySegment<byte> segment).ShouldBeTrue();
            ReferenceEquals(segment.Array, first.Array).ShouldBeTrue();
        }
        images[7].Transform.M31.ShouldBe(7f);
    }

    [Fact]
    public void AClipOrMaskOnAPicture_UsesThePicturesBox()
    {
        VectorGroup group = Doc("<clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect width=\"0.5\" height=\"0.5\"/></clipPath>" +
            "<image x=\"10\" y=\"20\" width=\"40\" height=\"20\" opacity=\"0.5\" clip-path=\"url(#c)\" href=\"a.png\"/>").Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        group.Opacity.ShouldBe(0.5f);
        group.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorImage>().Opacity.ShouldBe(1f);
        VectorClipShape outline = group.Clip!.Shapes.ShouldHaveSingleItem();
        Flatten(outline.Path, outline.Transform).ShouldHaveSingleItem().ShouldBe([new(10, 20), new(30, 20), new(30, 30), new(10, 30)]);
    }

    // ---- pattern ------------------------------------------------------------------------------------------------------

    private static PatternPaint PatternOf(string defs, string shape = Box, string property = "fill")
    {
        SvgDocument document = Doc($"<defs>{defs}</defs>" + shape.Replace("/>", $" {property}=\"url(#p)\"/>"));
        document.Issues.ShouldBeEmpty();
        var drawn = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        return (property == "fill" ? drawn.Fill!.Paint : drawn.Stroke!.Paint).ShouldBeOfType<PatternPaint>();
    }

    private const string Dot = "<rect id=\"dot\" width=\"2\" height=\"1\"/>";

    [Fact]
    public void APattern_IsATileAndItsContent_InTheShapesCoordinates()
    {
        // Tile in bounding-box units (the default): a quarter of the box's width, half its height, from its corner.
        PatternPaint boxed = PatternOf($"<pattern id=\"p\" width=\"0.25\" height=\"0.5\">{Dot}</pattern>");
        boxed.Tile.ShouldBe(new VectorRect(10, 20, 10, 10));
        boxed.Transform.ShouldBe(Matrix3x2.Identity);
        // The content is in user units, drawn from the tile's corner.
        var dot = boxed.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        Flatten(dot.Path, dot.Transform).ShouldHaveSingleItem().ShouldBe([new(10, 20), new(12, 20), new(12, 21), new(10, 21)]);

        // Tile in user units, with a transform of the whole pattern.
        PatternPaint user = PatternOf($"<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" x=\"1\" y=\"2\" width=\"6\" height=\"4\" patternTransform=\"translate(3 0) scale(2)\">{Dot}</pattern>");
        user.Tile.ShouldBe(new VectorRect(1, 2, 6, 4));
        user.Transform.ShouldBe(new Matrix3x2(2, 0, 0, 2, 3, 0));
        dot = user.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        (dot.Transform.M31, dot.Transform.M32).ShouldBe((1f, 2f));
    }

    [Fact]
    public void PatternContent_InBoundingBoxUnits_OrFittedByAViewBox()
    {
        PatternPaint boxed = PatternOf("<pattern id=\"p\" width=\"0.5\" height=\"0.5\" patternContentUnits=\"objectBoundingBox\"><rect width=\"0.25\" height=\"0.5\"/></pattern>");
        var content = boxed.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        Flatten(content.Path, content.Transform).ShouldHaveSingleItem().ShouldBe([new(10, 20), new(20, 20), new(20, 30), new(10, 30)]);

        // A viewBox fits the content to the tile (here 12 x 8 into 6 x 4: half size) and patternContentUnits says nothing.
        PatternPaint fitted = PatternOf("<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"6\" height=\"4\" viewBox=\"0 0 12 8\" patternContentUnits=\"objectBoundingBox\"><rect x=\"2\" width=\"4\" height=\"8\"/></pattern>");
        content = fitted.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        Flatten(content.Path, content.Transform).ShouldHaveSingleItem().ShouldBe([new(1, 0), new(3, 0), new(3, 4), new(1, 4)]);
    }

    [Fact]
    public void APattern_TakesWhatItLacksFromThePatternItRefersTo_AndPaintsStrokesToo()
    {
        PatternPaint paint = PatternOf(
            $"<pattern id=\"base\" patternUnits=\"userSpaceOnUse\" width=\"6\" height=\"4\" patternTransform=\"scale(3)\">{Dot}</pattern>" +
            "<pattern id=\"p\" href=\"#base\" width=\"7\"/>", "<line x1=\"0\" y1=\"0\" x2=\"9\" y2=\"9\" stroke-width=\"2\"/>", "stroke");
        paint.Tile.ShouldBe(new VectorRect(0, 0, 7, 4));
        paint.Transform.ShouldBe(Matrix3x2.CreateScale(3));
        paint.Children.ShouldHaveSingleItem().Id.ShouldBe("dot");
    }

    [Theory]
    // Nothing to paint with: as if the fill were none, and the fallback is not used.
    [InlineData("<pattern id=\"p\" width=\"0\" height=\"1\"><rect width=\"1\" height=\"1\"/></pattern>", "url(#p) red", null)]
    [InlineData("<pattern id=\"p\" width=\"1\" height=\"1\"/>", "url(#p) red", null)]
    [InlineData("<pattern id=\"p\" width=\"1\" height=\"1\"><rect width=\"1\" height=\"1\" display=\"none\"/></pattern>", "url(#p) red", null)]
    // Cannot be applied to this shape: the fallback colour is.
    [InlineData("<pattern id=\"p\" width=\"1\" height=\"1\" patternTransform=\"scale(0 1)\"><rect width=\"1\" height=\"1\"/></pattern>", "url(#p) red", "255,0,0")]
    [InlineData("<pattern id=\"p\" width=\"1\" height=\"1\" patternTransform=\"scale(0 1)\"><rect width=\"1\" height=\"1\"/></pattern>", "url(#p)", null)]
    public void APatternThatPaintsNothing_AndOneThatCannotBeApplied(string defs, string fill, string? colour)
    {
        SvgDocument document = Doc($"<defs>{defs}</defs>" + Box.Replace("/>", $" fill=\"{fill}\"/>"));
        document.Issues.ShouldBeEmpty();
        if (colour is null)
        {
            document.Scene.Nodes.ShouldBeEmpty();
            return;
        }
        Rgba32 c = FillColor(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>());
        $"{c.R},{c.G},{c.B}".ShouldBe(colour);
    }

    [Fact]
    public void APatternOnAShapeWithNoBox_FallsBack_AndAPatternPaintedWithItself_FallsBackInside()
    {
        // Bounding-box units on a straight horizontal line mean nothing.
        VectorShape line = OnlyShape("<defs><pattern id=\"p\" width=\"1\" height=\"1\"><rect width=\"1\" height=\"1\"/></pattern></defs><path d=\"M0 5 H9\" stroke=\"url(#p) #0f0\"/>");
        StrokeColor(line).ShouldBe(new Rgba32(0, 255, 0));

        // Measured in a browser: the pattern is used, and the shape inside it that asks for the same pattern gets its fallback.
        SvgDocument loop = Doc("<defs><pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"8\" height=\"8\"><rect id=\"inside\" width=\"8\" height=\"8\" fill=\"url(#p) #c0c\"/></pattern></defs>" +
            Box.Replace("/>", " fill=\"url(#p) #0a0\"/>"));
        Only(loop).ShouldBe(new SvgIssue(SvgIssueKind.Invalid, "pattern: circular reference", 1));
        var paint = loop.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>().Fill!.Paint.ShouldBeOfType<PatternPaint>();
        FillColor(paint.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(204, 0, 204));
    }

    [Fact]
    public void OnePatternInUserUnits_IsOnePaintForEveryShapeThatUsesIt_AndABoxedOneIsWorkedOutAShape()
    {
        string shapes = "<rect width=\"9\" height=\"9\" fill=\"url(#p)\"/><rect x=\"20\" width=\"5\" height=\"5\" fill=\"url(#p)\"/><circle r=\"3\" fill=\"url(#p)\"/>";
        VectorPaint[] user = [.. Doc($"<defs><pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"4\" height=\"4\">{Dot}</pattern></defs>" + shapes).Scene.Nodes.Cast<VectorShape>().Select(s => s.Fill!.Paint)];
        user.ShouldAllBe(paint => ReferenceEquals(paint, user[0]));

        VectorPaint[] boxed = [.. Doc($"<defs><pattern id=\"p\" width=\"0.5\" height=\"0.5\">{Dot}</pattern></defs>" + shapes).Scene.Nodes.Cast<VectorShape>().Select(s => s.Fill!.Paint)];
        boxed.Cast<PatternPaint>().Select(paint => paint.Tile.Width).ShouldBe([4.5f, 2.5f, 3f]);
    }

    // ---- What a document may ask for --------------------------------------------------------------------------------

    private static readonly SvgReadOptions Small = new() { MaxElements = 20_000 };

    [Fact]
    public void PatternsFilledWithPatterns_AreChargedForEveryUse_AlthoughEachIsWorkedOutOnce()
    {
        // Each level's tile holds two shapes painted with the level below: 2^40 leaves for whoever draws or writes the
        // scene, from under 5 KB. It must be refused, and at once: nothing here may walk them.
        var defs = new StringBuilder("<pattern id=\"p0\" patternUnits=\"userSpaceOnUse\" width=\"4\" height=\"4\"><rect width=\"1\" height=\"1\"/></pattern>");
        for (int i = 1; i <= 40; i++)
        {
            defs.Append($"<pattern id=\"p{i}\" patternUnits=\"userSpaceOnUse\" width=\"4\" height=\"4\"><rect width=\"2\" height=\"2\" fill=\"url(#p{i - 1})\"/><rect x=\"2\" width=\"2\" height=\"2\" fill=\"url(#p{i - 1})\"/></pattern>");
        }
        string body = $"<defs>{defs}</defs><rect width=\"50\" height=\"50\" fill=\"url(#p40)\"/>";
        body.Length.ShouldBeLessThan(8_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<InvalidDataException>(() => Doc(body)).Message.ShouldContain("elements");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));

        // A few levels are fine, and the paint really is shared.
        string few = body.Replace("url(#p40)", "url(#p3)");
        Doc(few, options: Small).Issues.ShouldBeEmpty();
    }

    [Theory]
    // A marker on every vertex, each marker a hundred shapes.
    [InlineData("marker")]
    // A mask whose picture is masked shapes, each by the mask a level down.
    [InlineData("mask")]
    [InlineData("clip")]
    public void MarkersMasksAndClips_AreChargedForEveryInstance(string what)
    {
        string body;
        if (what == "marker")
        {
            string hundred = string.Concat(Enumerable.Repeat("<rect width=\"1\" height=\"1\"/>", 100));
            string points = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"{i},{i % 2}"));
            body = $"<marker id=\"a\">{hundred}</marker><polyline points=\"{points}\" fill=\"none\" stroke=\"red\" style=\"marker: url(#a)\"/>";
        }
        else
        {
            string tag = what == "mask" ? "mask" : "clipPath", property = what == "mask" ? "mask" : "clip-path";
            var defs = new StringBuilder($"<{tag} id=\"m0\"><rect width=\"9\" height=\"9\" fill=\"#fff\"/></{tag}>");
            for (int i = 1; i <= 30; i++)
            {
                defs.Append($"<{tag} id=\"m{i}\"><rect width=\"9\" height=\"9\" fill=\"#fff\" {property}=\"url(#m{i - 1})\"/><rect x=\"1\" width=\"9\" height=\"9\" fill=\"#fff\" {property}=\"url(#m{i - 1})\"/></{tag}>");
            }
            body = $"<defs>{defs}</defs><rect width=\"9\" height=\"9\" {property}=\"url(#m30)\"/>";
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<InvalidDataException>(() => Doc(body, options: Small)).Message.ShouldContain("elements");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ThingsThatReferToEachOtherTooDeep_AreRefusedByTheDepthLimit_NotByTheStack()
    {
        // A chain of clip paths, each clipped by the next: one reference deep a level.
        var defs = new StringBuilder();
        for (int i = 0; i < 2000; i++) defs.Append($"<clipPath id=\"c{i}\" clip-path=\"url(#c{i + 1})\"><rect width=\"9\" height=\"9\"/></clipPath>");
        defs.Append("<clipPath id=\"c2000\"><rect width=\"9\" height=\"9\"/></clipPath>");
        Should.Throw<InvalidDataException>(() => Doc($"<defs>{defs}</defs><rect width=\"9\" height=\"9\" clip-path=\"url(#c0)\"/>")).Message.ShouldContain("deep");
    }
}
