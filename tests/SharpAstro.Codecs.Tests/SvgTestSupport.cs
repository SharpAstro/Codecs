using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>Builders and readers the SVG tests share.</summary>
internal static class SvgTestSupport
{
    public const string Namespace = "http://www.w3.org/2000/svg";

    /// <summary>A 100 x 100 root, unless <paramref name="root"/> says otherwise, around <paramref name="body"/>.</summary>
    public static string Wrap(string body, string root = "width=\"100\" height=\"100\"") =>
        $"<svg xmlns=\"{Namespace}\" {root}>{body}</svg>";

    public static SvgDocument Doc(string body, string root = "width=\"100\" height=\"100\"", SvgReadOptions? options = null) =>
        SvgReader.Parse(Wrap(body, root), options);

    /// <summary>The scene's one node, which must be a shape.</summary>
    public static VectorShape OnlyShape(string body, string root = "width=\"100\" height=\"100\"") =>
        Doc(body, root).Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();

    /// <summary>A path as text: <c>M x y L x y Q .. C .. Z</c>, numbers to three decimals.</summary>
    public static string Describe(VectorPath path)
    {
        var text = new StringBuilder();
        ReadOnlySpan<float> c = path.Coordinates;
        int i = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(verb switch { PathVerb.Move => 'M', PathVerb.Line => 'L', PathVerb.Quad => 'Q', PathVerb.Cubic => 'C', _ => 'Z' });
            for (int n = VectorPath.CoordinateCount(verb); n > 0; n--)
            {
                double value = Math.Round(c[i++], 3);
                text.Append(' ').Append((value == 0 ? 0 : value).ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// Everything about a scene, as text, so that two scenes can be compared whole and a test can say what it expects to
    /// find. One node a line, children indented under their group; a group's clip and mask come before its children.
    /// </summary>
    public static string Dump(VectorScene scene)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"scene {scene.Width:R} x {scene.Height:R}\n"));
        DumpNodes(text, scene.Nodes, 1);
        return text.ToString();
    }

    private static void DumpNodes(StringBuilder text, ImmutableArray<VectorNode> nodes, int indent)
    {
        foreach (VectorNode node in nodes)
        {
            text.Append(' ', indent * 2);
            switch (node)
            {
                case VectorGroup group:
                    text.Append(CultureInfo.InvariantCulture, $"group id={group.Id ?? "(none)"} opacity={group.Opacity:R}\n");
                    if (group.Clip is { } clip) DumpClip(text, clip, "clip", indent + 1);
                    if (group.Mask is { } mask)
                    {
                        text.Append(' ', (indent + 1) * 2).Append(CultureInfo.InvariantCulture, $"mask {mask.Kind}\n");
                        DumpNodes(text, mask.Children, indent + 2);
                    }
                    DumpNodes(text, group.Children, indent + 1);
                    break;

                case VectorImage image:
                    text.Append(CultureInfo.InvariantCulture,
                        $"image id={image.Id ?? "(none)"} transform={image.Transform} bounds={image.Bounds} {image.Fit} align={image.AlignX:R},{image.AlignY:R} opacity={image.Opacity:R}");
                    text.Append(CultureInfo.InvariantCulture,
                        $" type={image.MediaType ?? "(none)"} source={image.Source ?? "(none)"} bytes={image.Data.Length} sha={Convert.ToHexString(SHA256.HashData(image.Data.Span))[..12]}\n");
                    break;

                case VectorText words:
                {
                    text.Append(CultureInfo.InvariantCulture, $"text id={words.Id ?? "(none)"} transform={words.Transform}");
                    if (words.TextLength is { } length) text.Append(CultureInfo.InvariantCulture, $" length={length:R} {words.LengthAdjust}");
                    text.Append('\n');
                    foreach (VectorTextSpan span in words.Spans)
                    {
                        text.Append(' ', (indent + 1) * 2);
                        VectorFont font = span.Font;
                        text.Append(CultureInfo.InvariantCulture,
                            $"span \"{span.Text}\" font=[{string.Join(", ", font.Families)}] {font.Size:R} w={font.Weight} {font.Slant} {font.Stretch:R}% {span.Anchor} {span.Baseline}");
                        if (span.BaselineShift != 0) text.Append(CultureInfo.InvariantCulture, $" shift={span.BaselineShift:R}");
                        if (span.ScriptRise != 0) text.Append(CultureInfo.InvariantCulture, $" rise={span.ScriptRise:R}");
                        if (span.LetterSpacing != 0) text.Append(CultureInfo.InvariantCulture, $" letter={span.LetterSpacing:R}");
                        if (span.WordSpacing != 0) text.Append(CultureInfo.InvariantCulture, $" word={span.WordSpacing:R}");
                        if (span.Hidden) text.Append(" hidden");
                        foreach (VectorTextLine line in span.Lines)
                        {
                            // Which object it is is part of what a span says: spans under one line say so by sharing it.
                            int which = words.Spans.SelectMany(s => s.Lines).Distinct(ReferenceEqualityComparer.Instance).ToList().IndexOf(line);
                            text.Append(CultureInfo.InvariantCulture, $" line#{which}({line.Kinds}");
                            if (line.Fill is { } lineFill) text.Append(CultureInfo.InvariantCulture, $" fill({DumpPaint(lineFill.Paint)} {lineFill.Opacity:R}{(line.FillInBox ? " in-box" : "")})");
                            if (line.Stroke is { } lineStroke) text.Append(CultureInfo.InvariantCulture, $" stroke({DumpPaint(lineStroke.Paint)} {lineStroke.Opacity:R} w={lineStroke.Width:R}{(line.StrokeInBox ? " in-box" : "")})");
                            text.Append(')');
                        }
                        if (span.Fill is { } spanFill) text.Append(CultureInfo.InvariantCulture, $" fill({DumpPaint(spanFill.Paint)} {spanFill.Opacity:R} {spanFill.Rule}{(span.FillInBox ? " in-box" : "")})");
                        if (span.Stroke is { } spanStroke)
                        {
                            text.Append(CultureInfo.InvariantCulture,
                                $" stroke({DumpPaint(spanStroke.Paint)} {spanStroke.Opacity:R} w={spanStroke.Width:R} {spanStroke.Cap} {spanStroke.Join}{(span.StrokeInBox ? " in-box" : "")}{(span.StrokeFirst ? " first" : "")})");
                        }
                        foreach (VectorTextPosition p in span.Positions)
                        {
                            text.Append(CultureInfo.InvariantCulture, $" @{p.Index}(");
                            if (p.X is { } x) text.Append(CultureInfo.InvariantCulture, $"x={x:R} ");
                            if (p.Y is { } y) text.Append(CultureInfo.InvariantCulture, $"y={y:R} ");
                            if (p.Dx != 0) text.Append(CultureInfo.InvariantCulture, $"dx={p.Dx:R} ");
                            if (p.Dy != 0) text.Append(CultureInfo.InvariantCulture, $"dy={p.Dy:R} ");
                            if (p.Rotate != 0) text.Append(CultureInfo.InvariantCulture, $"turn={p.Rotate:R} ");
                            if (text[^1] == ' ') text.Length--;
                            text.Append(')');
                        }
                        if (span.Path is { } along)
                        {
                            // The object's identity is part of what a span says: spans on one path say so by sharing it.
                            int number = words.Spans.Select(s => s.Path).Where(p => p is not null).Distinct(ReferenceEqualityComparer.Instance).ToList().IndexOf(along);
                            text.Append(CultureInfo.InvariantCulture,
                                $" along#{number}([{Describe(along.Path)}] {along.Transform} start={along.StartOffset:R}{(along.StartIsFraction ? " of the length" : "")}{(along.RightSide ? " right" : "")})");
                        }
                        text.Append('\n');
                    }
                    break;
                }

                default:
                {
                    var shape = (VectorShape)node;
                    text.Append(CultureInfo.InvariantCulture, $"shape id={shape.Id ?? "(none)"} transform={shape.Transform} path=[{Describe(shape.Path)}]");
                    if (shape.Fill is { } fill) text.Append(CultureInfo.InvariantCulture, $" fill({DumpPaint(fill.Paint)} {fill.Opacity:R} {fill.Rule})");
                    if (shape.Stroke is { } s)
                    {
                        text.Append(CultureInfo.InvariantCulture,
                            $" stroke({DumpPaint(s.Paint)} {s.Opacity:R} w={s.Width:R} {s.Cap} {s.Join} m={s.MiterLimit:R} dashes=[{string.Join(' ', s.Dashes.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))}] off={s.DashOffset:R})");
                    }
                    text.Append('\n');
                    break;
                }
            }
        }
    }

    private static void DumpClip(StringBuilder text, VectorClip clip, string label, int indent)
    {
        text.Append(' ', indent * 2).Append(label).Append('\n');
        foreach (VectorClipShape shape in clip.Shapes)
        {
            text.Append(' ', (indent + 1) * 2).Append(CultureInfo.InvariantCulture, $"outline {shape.Rule} transform={shape.Transform} path=[{Describe(shape.Path)}]\n");
            if (shape.Clip is { } inner) DumpClip(text, inner, "within", indent + 2);
        }
        if (clip.Intersect is { } intersect) DumpClip(text, intersect, "intersect", indent + 1);
    }

    private static string DumpPaint(VectorPaint paint)
    {
        switch (paint)
        {
            case SolidPaint solid:
                return $"solid {solid.Color}";
            case LinearGradientPaint l:
                return string.Create(CultureInfo.InvariantCulture, $"linear {l.X1:R},{l.Y1:R} {l.X2:R},{l.Y2:R} {l.Extend} {l.Transform} [{Stops(l.Stops)}]");
            case RadialGradientPaint r:
                return string.Create(CultureInfo.InvariantCulture, $"radial {r.StartX:R},{r.StartY:R},{r.StartRadius:R} {r.EndX:R},{r.EndY:R},{r.EndRadius:R} {r.Extend} {r.Transform} [{Stops(r.Stops)}]");
            case PatternPaint pattern:
            {
                // The tile's own scene on one line, so that a shape stays one line.
                var tile = new StringBuilder();
                DumpNodes(tile, pattern.Children, 0);
                return string.Create(CultureInfo.InvariantCulture, $"pattern tile={pattern.Tile} {pattern.Transform} <{tile.ToString().TrimEnd('\n').Replace("\n", " | ")}>");
            }
            default:
                return paint.GetType().Name;
        }

        static string Stops(ImmutableArray<GradientStop> stops) =>
            string.Join("; ", stops.Select(stop => string.Create(CultureInfo.InvariantCulture, $"{stop.Offset:R} {stop.Color}")));
    }

    /// <summary>What a <c>d</c> attribute becomes.</summary>
    public static string PathOf(string d) => Describe(OnlyShape($"<path d=\"{d}\"/>").Path);

    public static Vector2 Map(VectorShape shape, float x, float y) => Vector2.Transform(new Vector2(x, y), shape.Transform);

    public static Rgba32 FillColor(VectorShape shape) => shape.Fill.ShouldNotBeNull().Paint.ShouldBeOfType<SolidPaint>().Color;

    public static Rgba32 StrokeColor(VectorShape shape) => shape.Stroke.ShouldNotBeNull().Paint.ShouldBeOfType<SolidPaint>().Color;

    /// <summary>
    /// The path as closed polygons in scene coordinates, curves cut into straight pieces. Every subpath is closed, which
    /// is what filling does with an open one.
    /// </summary>
    public static List<List<Vector2>> Flatten(VectorPath path, Matrix3x2 transform, int pieces = 24)
    {
        var polygons = new List<List<Vector2>>();
        List<Vector2>? current = null;
        ReadOnlySpan<float> c = path.Coordinates;
        Vector2 last = default;
        int i = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    last = new Vector2(c[i], c[i + 1]);
                    current = [Vector2.Transform(last, transform)];
                    polygons.Add(current);
                    break;
                case PathVerb.Line:
                    last = new Vector2(c[i], c[i + 1]);
                    current!.Add(Vector2.Transform(last, transform));
                    break;
                case PathVerb.Quad:
                {
                    Vector2 p0 = last, p1 = new(c[i], c[i + 1]), p2 = new(c[i + 2], c[i + 3]);
                    for (int k = 1; k <= pieces; k++)
                    {
                        float t = k / (float)pieces, u = 1 - t;
                        current!.Add(Vector2.Transform(u * u * p0 + 2 * u * t * p1 + t * t * p2, transform));
                    }
                    last = p2;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Vector2 p0 = last, p1 = new(c[i], c[i + 1]), p2 = new(c[i + 2], c[i + 3]), p3 = new(c[i + 4], c[i + 5]);
                    for (int k = 1; k <= pieces; k++)
                    {
                        float t = k / (float)pieces, u = 1 - t;
                        current!.Add(Vector2.Transform(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3, transform));
                    }
                    last = p3;
                    break;
                }
            }
            i += VectorPath.CoordinateCount(verb);
        }
        return polygons;
    }

    /// <summary>The points of a path's curves and lines in its own coordinates, for checks against a known curve.</summary>
    public static List<Vector2> Sample(VectorPath path) => [.. Flatten(path, Matrix3x2.Identity).SelectMany(polygon => polygon)];
}
