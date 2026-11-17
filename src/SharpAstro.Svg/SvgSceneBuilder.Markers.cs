using System.Numerics;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

// Markers: the little drawings (arrowheads, dots) a path, line, polyline or polygon carries at its vertices.
internal sealed partial class SvgSceneBuilder
{
    /// <summary>A vertex of a path: where it is, and the direction (degrees, clockwise from +x as y runs down) an <c>orient="auto"</c> marker faces there.</summary>
    internal readonly record struct MarkerVertex(Vector2 Point, float Angle);

    /// <summary>
    /// Draws the shape's markers into <paramref name="output"/>: the start marker on the first vertex, the end marker on
    /// the last, the mid marker on every one between, in that order.
    /// </summary>
    private void Markers(SvgElement element, in SvgStyle style, SvgAffine matrix, in SvgViewport viewport, VectorPath path, List<VectorNode> output, int depth)
    {
        int[]? ends = null;
        if (element.Name == "path")
        {
            // A path command is one vertex, however many curves it became (an arc is up to four): ask the parser where
            // the commands end. Only paths with markers pay for this, once.
            SvgElementExtras extras = element.Extras ??= new SvgElementExtras();
            if (extras.CommandEnds is null)
            {
                var list = new List<int>();
                SvgGeometry.ParsePath(element.Get("d") ?? "", new PathBuilder(), list);
                extras.CommandEnds = [.. list];
            }
            ends = extras.CommandEnds;
        }

        List<MarkerVertex> vertices = MarkerVertices(path, ends);
        if (vertices.Count == 0) return;
        if (style.MarkerStart is { } start) Marker(start, vertices[0], true, style.StrokeWidth, matrix, viewport, output, depth);
        if (style.MarkerMid is { } mid)
        {
            for (int i = 1; i < vertices.Count - 1; i++) Marker(mid, vertices[i], false, style.StrokeWidth, matrix, viewport, output, depth);
        }
        if (style.MarkerEnd is { } end) Marker(end, vertices[^1], false, style.StrokeWidth, matrix, viewport, output, depth);
    }

    /// <summary>
    /// The vertices of a path with the direction at each. A vertex has the direction the path arrives in and the one it
    /// leaves in; where it has both, the marker faces half way between them. The first vertex of an open subpath only
    /// leaves and its last only arrives. A closed subpath has one more vertex, where it closes, and both that one and
    /// its first face half way between the closing line and the first segment.
    /// </summary>
    /// <param name="ends">The verb count after each path command, or null when every verb is a command of its own.</param>
    internal static List<MarkerVertex> MarkerVertices(VectorPath path, int[]? ends)
    {
        ReadOnlySpan<PathVerb> verbs = path.Verbs;
        ReadOnlySpan<float> c = path.Coordinates;
        var points = new List<Vector2>();
        var arriving = new List<Vector2>();
        var leaving = new List<Vector2>();
        Vector2 current = default, start = default;
        int first = -1;             // the first vertex of the subpath being drawn
        int verb = 0, i = 0, command = 0;

        while (verb < verbs.Length)
        {
            int end = ends is null ? verb + 1 : command < ends.Length ? ends[command] : verbs.Length;
            command++;
            if (end <= verb) continue;

            if (verbs[verb] == PathVerb.Move)
            {
                current = start = new Vector2(c[i], c[i + 1]);
                i += 2;
                verb++;
                if (verb < end && points.Count > 0)
                {
                    // A drawing command straight after a closepath: the subpath it starts begins at the vertex the
                    // closepath left there, so there is no new vertex.
                    first = points.Count - 1;
                }
                else
                {
                    first = points.Count;
                    points.Add(current);
                    arriving.Add(default);
                    leaving.Add(default);
                    continue;
                }
            }

            if (verbs[verb] == PathVerb.Close)
            {
                verb++;
                if (points.Count == 0) continue;
                Vector2 closing = start - current;
                if (closing == default) closing = arriving[^1];
                if (leaving[^1] == default) leaving[^1] = closing;
                points.Add(start);
                arriving.Add(closing);
                leaving.Add(first >= 0 ? leaving[first] : default);
                if (first >= 0) arriving[first] = closing;
                current = start;
                continue;
            }

            // One command: one curve, or the several an arc became. It leaves along the first and arrives along the last.
            Vector2 leave = default, arrive = default;
            for (; verb < end && verbs[verb] is PathVerb.Line or PathVerb.Quad or PathVerb.Cubic; verb++)
            {
                Vector2 from = current, to, l, a;
                switch (verbs[verb])
                {
                    case PathVerb.Line:
                        to = new Vector2(c[i], c[i + 1]);
                        l = a = to - from;
                        break;
                    case PathVerb.Quad:
                    {
                        var q = new Vector2(c[i], c[i + 1]);
                        to = new Vector2(c[i + 2], c[i + 3]);
                        l = q != from ? q - from : to - from;
                        a = to != q ? to - q : to - from;
                        break;
                    }
                    default:
                    {
                        Vector2 q1 = new(c[i], c[i + 1]), q2 = new(c[i + 2], c[i + 3]);
                        to = new Vector2(c[i + 4], c[i + 5]);
                        l = q1 != from ? q1 - from : q2 != from ? q2 - from : to - from;
                        a = to != q2 ? to - q2 : to != q1 ? to - q1 : to - from;
                        break;
                    }
                }
                i += VectorPath.CoordinateCount(verbs[verb]);
                if (leave == default) leave = l;
                if (a != default) arrive = a;
                current = to;
            }
            if (points.Count == 0) continue;
            if (leaving[^1] == default) leaving[^1] = leave;
            points.Add(current);
            arriving.Add(arrive);
            leaving.Add(default);
        }

        var vertices = new List<MarkerVertex>(points.Count);
        for (int v = 0; v < points.Count; v++)
        {
            Vector2 a = arriving[v], l = leaving[v];
            double angle;
            if (a == default) angle = l == default ? 0 : Math.Atan2(l.Y, l.X);
            else if (l == default) angle = Math.Atan2(a.Y, a.X);
            else
            {
                double into = Math.Atan2(a.Y, a.X), turn = Math.Atan2(l.Y, l.X) - into;
                if (turn > Math.PI) turn -= 2 * Math.PI;
                else if (turn <= -Math.PI) turn += 2 * Math.PI;
                angle = into + turn / 2;
            }
            vertices.Add(new MarkerVertex(points[v], (float)(angle * 180 / Math.PI)));
        }
        return vertices;
    }

    /// <summary>One marker on one vertex: its content, placed so that its reference point is on the vertex.</summary>
    private void Marker(SvgElement marker, MarkerVertex vertex, bool isStart, double strokeWidth, SvgAffine matrix, in SvgViewport viewport,
        List<VectorNode> output, int depth)
    {
        Charge(depth + 1);
        if (!_active.Add(marker))
        {
            Issue(SvgIssueKind.Invalid, "marker: circular reference");
            return;
        }
        try
        {
            // What a marker's content inherits comes from the marker's ancestors, not from the shape that carries it.
            SvgStyle style = InheritedStyle(marker, viewport);
            double fontSize = style.FontSize;
            double width = marker.Get("markerWidth") is null ? 3 : Length(marker, "markerWidth", viewport.Width, fontSize);
            double height = marker.Get("markerHeight") is null ? 3 : Length(marker, "markerHeight", viewport.Height, fontSize);
            if (width < 0 || height < 0) Issue(SvgIssueKind.Invalid, "negative marker size");
            if (width <= 0 || height <= 0) return;

            double angle = 0;
            switch (marker.Get("orient")?.Trim())
            {
                case null:
                    break;
                case "auto":
                    angle = vertex.Angle;
                    break;
                case "auto-start-reverse":
                    angle = isStart ? vertex.Angle + 180 : vertex.Angle;
                    break;
                case var text:
                    if (!TryAngle(text, out angle)) Issue(SvgIssueKind.Invalid, "orient");
                    break;
            }
            double radians = angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
            SvgAffine place = matrix * new SvgAffine(cos, sin, -sin, cos, vertex.Point.X, vertex.Point.Y);

            switch (marker.Get("markerUnits")?.Trim())
            {
                case null or "strokeWidth":
                    // In units of the stroke's width, whether or not the shape has a stroke.
                    if (strokeWidth <= 0) return;
                    place *= new SvgAffine(strokeWidth, 0, 0, strokeWidth, 0, 0);
                    break;
                case "userSpaceOnUse":
                    break;
                default:
                    Issue(SvgIssueKind.Invalid, "markerUnits");
                    place *= new SvgAffine(strokeWidth, 0, 0, strokeWidth, 0, 0);
                    break;
            }

            SvgAffine toViewport = SvgAffine.Identity;
            var inner = new SvgViewport(width, height);
            if (marker.Get("viewBox") is { } viewBoxText)
            {
                if (TryParseViewBox(viewBoxText, out double vbX, out double vbY, out double vbW, out double vbH))
                {
                    if (vbW <= 0 || vbH <= 0) return;
                    toViewport = ViewBoxTransform(marker.Get("preserveAspectRatio"), vbX, vbY, vbW, vbH, width, height);
                    inner = new SvgViewport(vbW, vbH);
                }
                else
                {
                    Issue(SvgIssueKind.Invalid, "viewBox");
                }
            }

            // The reference point is in the content's coordinates; it is what lands on the vertex.
            double refX = Length(marker, "refX", inner.Width, fontSize), refY = Length(marker, "refY", inner.Height, fontSize);
            place *= new SvgAffine(1, 0, 0, 1,
                -(toViewport.A * refX + toViewport.C * refY + toViewport.E),
                -(toViewport.B * refX + toViewport.D * refY + toViewport.F));
            if (!place.IsFinite || place.Determinant == 0) return;

            int first = output.Count;
            foreach (SvgElement child in marker.Children)
            {
                Walk(child, style, place * toViewport, inner, output, depth + 2);
            }
            // A marker is a viewport of its own and clips to it unless overflow says otherwise.
            if (style.OverflowVisible != true && ReachesOutside(output, first, place, 0, 0, width, height))
            {
                ClipToRectangle(output, first, place, 0, 0, width, height);
            }
            // Opacity on the marker element fades the whole marker (measured in a browser).
            if (style.Opacity < 1 && output.Count > first)
            {
                if (output.Count == first + 1 && output[first] is VectorGroup { Mask: null } only)
                {
                    output[first] = new VectorGroup { Opacity = only.Opacity * (float)style.Opacity, Clip = only.Clip, Children = only.Children, Id = only.Id };
                }
                else
                {
                    System.Collections.Immutable.ImmutableArray<VectorNode> drawn = [.. output.Skip(first)];
                    output.RemoveRange(first, output.Count - first);
                    output.Add(new VectorGroup { Opacity = (float)style.Opacity, Children = drawn });
                }
            }
        }
        finally
        {
            _active.Remove(marker);
        }
    }

    /// <summary>An angle in degrees: a bare number, or one with <c>deg</c>, <c>grad</c>, <c>rad</c> or <c>turn</c>.</summary>
    private static bool TryAngle(ReadOnlySpan<char> text, out double degrees)
    {
        text = text.Trim();
        int digits = text.Length;
        while (digits > 0 && char.IsAsciiLetter(text[digits - 1])) digits--;
        ReadOnlySpan<char> unit = text[digits..];
        var scanner = new SvgScanner(text[..digits]);
        if (!scanner.TryNumber(out degrees) || !scanner.AtEnd) return false;
        if (unit.IsEmpty || unit is "deg") return true;
        if (unit is "grad") degrees *= 0.9;
        else if (unit is "rad") degrees *= 180 / Math.PI;
        else if (unit is "turn") degrees *= 360;
        else return false;
        return true;
    }
}
