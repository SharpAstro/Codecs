using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Numerics;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

internal enum SvgPaintKind : byte { None, Color, CurrentColor, Server }

/// <summary>
/// A <c>fill</c> or <c>stroke</c> value. <c>currentColor</c> stays a keyword so that it inherits as one, and a paint
/// server stays a reference, because what it paints depends on the shape that uses it. For a server, <paramref name="Color"/>
/// and <paramref name="Fallback"/> are what is used when the server cannot be.
/// </summary>
internal readonly record struct SvgPaint(SvgPaintKind Kind, Rgba32 Color, SvgElement? Server = null, SvgPaintKind Fallback = SvgPaintKind.None);

/// <summary>The size percentages resolve against: the nearest viewport's, in its own user units.</summary>
internal readonly record struct SvgViewport(double Width, double Height)
{
    /// <summary>What a percentage that is neither horizontal nor vertical is of: sqrt((w^2 + h^2) / 2).</summary>
    public double Diagonal => Math.Sqrt((Width * Width + Height * Height) / 2);
}

/// <summary>The computed values of the properties this reader acts on, for one element.</summary>
internal struct SvgStyle
{
    // Inherited.
    public SvgPaint Fill, Stroke;
    public FillRule FillRule;
    public double FillOpacity, StrokeOpacity;
    public double StrokeWidth, MiterLimit, DashOffset;
    public double[]? Dashes;
    public LineCap Cap;
    public LineJoin Join;
    public Rgba32 Color;
    public bool Visible;
    public double FontSize;
    public FillRule ClipRule;
    public SvgElement? MarkerStart, MarkerMid, MarkerEnd;

    /// <summary>What is painted first, second and third: see <see cref="SvgSceneBuilder.NormalPaintOrder"/>.</summary>
    public byte PaintOrder;

    // Not inherited.
    public double Opacity;
    public bool Display;
    public bool? OverflowVisible;
    public SvgElement? ClipPath, Mask;

    /// <summary>The initial values: black fill, no stroke, a 1-wide butt/miter stroke when one is asked for, 16 px text.</summary>
    public static SvgStyle Initial => new()
    {
        Fill = new SvgPaint(SvgPaintKind.Color, new Rgba32(0, 0, 0)),
        Stroke = new SvgPaint(SvgPaintKind.None, default),
        FillOpacity = 1,
        StrokeOpacity = 1,
        StrokeWidth = 1,
        MiterLimit = 4,
        Color = new Rgba32(0, 0, 0),
        Visible = true,
        FontSize = 16,
        PaintOrder = SvgSceneBuilder.NormalPaintOrder,
        Opacity = 1,
        Display = true,
    };
}

/// <summary>
/// Walks the element tree and emits the scene: styles cascaded, transforms multiplied out, shapes turned into paths,
/// <c>use</c> followed and paint servers resolved against the shape that uses them.
/// </summary>
internal sealed partial class SvgSceneBuilder(SvgReadOptions options, SvgTree tree)
{
    /// <summary>The properties read from presentation attributes, style sheets and <c>style</c>.</summary>
    private static readonly FrozenSet<string> Properties = FrozenSet.ToFrozenSet(
    [
        "fill", "stroke", "fill-rule", "fill-opacity", "stroke-opacity", "opacity", "stroke-width", "stroke-linecap",
        "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "color", "visibility", "display",
        "font-size", "overflow", "stop-color", "stop-opacity", "clip-path", "mask", "filter", "marker", "marker-start",
        "marker-mid", "marker-end", "mix-blend-mode", "paint-order", "vector-effect", "transform-origin", "clip-rule", "mask-type",
    ], StringComparer.Ordinal);

    /// <summary>Elements that are never drawn where they stand. What refers to them is reported where the reference is.</summary>
    private static readonly FrozenSet<string> NeverRendered = FrozenSet.ToFrozenSet(
    [
        "defs", "title", "desc", "metadata", "style", "symbol", "marker", "clipPath", "mask", "pattern", "linearGradient",
        "radialGradient", "stop", "filter", "view", "cursor",
    ], StringComparer.Ordinal);

    private readonly Dictionary<(SvgIssueKind Kind, string What), int> _issues = [];
    private readonly HashSet<SvgElement> _active = [];
    private readonly List<SvgElement> _uses = [];
    private readonly PathBuilder _builder = new();
    private VectorFill? _lastFill;
    private VectorStroke? _lastStroke;
    private long _verbs, _visits;

    /// <summary>How deep the shape being painted is: a pattern's content is walked from inside a paint, which has no depth of its own.</summary>
    private int _paintDepth;

    public SvgDocument Build()
    {
        SvgElement root = tree.Root;
        ReadStyleSheets();

        bool hasViewBox = false;
        double vbX = 0, vbY = 0, vbW = 0, vbH = 0;
        if (root.Get("viewBox") is { } viewBoxText)
        {
            hasViewBox = TryParseViewBox(viewBoxText, out vbX, out vbY, out vbW, out vbH);
            if (!hasViewBox) Issue(SvgIssueKind.Invalid, "viewBox");
        }

        double? w = RootLength(root, "width", options.ViewportWidth);
        double? h = RootLength(root, "height", options.ViewportHeight);
        bool ratio = hasViewBox && vbW > 0 && vbH > 0;
        double width, height;
        if (w is null && h is null)
        {
            (width, height) = hasViewBox ? (vbW, vbH) : (300, 150);
        }
        else
        {
            width = w ?? (ratio ? h!.Value * vbW / vbH : 300);
            height = h ?? (ratio ? w!.Value * vbH / vbW : 150);
        }

        var nodes = new List<VectorNode>();
        SvgViewBox? viewBox = hasViewBox ? new SvgViewBox((float)vbX, (float)vbY, (float)vbW, (float)vbH) : null;
        // A zero width or height, on the picture or on the viewBox, turns rendering off.
        if (width > 0 && height > 0 && (!hasViewBox || (vbW > 0 && vbH > 0)))
        {
            SvgAffine toPicture = SvgAffine.Identity;
            if (hasViewBox)
            {
                toPicture = ViewBoxTransform(root.Get("preserveAspectRatio"), vbX, vbY, vbW, vbH, width, height);
            }
            var viewport = hasViewBox ? new SvgViewport(vbW, vbH) : new SvgViewport(width, height);
            if (Enter(root, SvgStyle.Initial, toPicture, viewport, isRoot: true, out SvgStyle style, out SvgAffine matrix))
            {
                List<VectorNode> target = BeginLayer(style, nodes);
                foreach (SvgElement child in root.Children)
                {
                    Walk(child, style, matrix, viewport, target, 1);
                }
                EndLayer(target, nodes, style, root.Get("id"), matrix, viewport, 0);
            }
        }

        ImmutableArray<SvgIssue> issues =
        [
            .. _issues.Select(pair => new SvgIssue(pair.Key.Kind, pair.Key.What, pair.Value))
                .OrderByDescending(issue => issue.Count).ThenBy(issue => issue.What, StringComparer.Ordinal),
        ];
        return new SvgDocument(new VectorScene((float)width, (float)height, [.. nodes]), viewBox, issues);
    }

    private void Issue(SvgIssueKind kind, string what)
    {
        if (kind == SvgIssueKind.Unsupported && options.ThrowOnUnsupported)
        {
            throw new NotSupportedException($"SVG: {what} is not read yet.");
        }
        _issues[(kind, what)] = _issues.GetValueOrDefault((kind, what)) + 1;
    }

    // ---- Sizes and viewports ------------------------------------------------------------------------------------

    /// <summary>The root's width or height in pixels, or null when the document leaves it to its surroundings.</summary>
    private double? RootLength(SvgElement root, string attribute, float? viewport)
    {
        if (root.Get(attribute) is not { } text) return viewport;
        if (!SvgLength.TryParse(text, out SvgLength length) || length.Value < 0)
        {
            Issue(SvgIssueKind.Invalid, attribute);
            return viewport;
        }
        if (length.Unit == SvgUnit.Percent) return viewport is { } size ? length.Value * size / 100.0 : null;
        return length.Resolve(SvgStyle.Initial.FontSize, 0);
    }

    private static bool TryParseViewBox(string text, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        var s = new SvgScanner(text);
        s.SkipWhitespace();
        if (!s.TryNumber(out x)) return false;
        s.SkipCommaWhitespace();
        if (!s.TryNumber(out y)) return false;
        s.SkipCommaWhitespace();
        if (!s.TryNumber(out w)) return false;
        s.SkipCommaWhitespace();
        if (!s.TryNumber(out h)) return false;
        s.SkipWhitespace();
        return s.AtEnd && w >= 0 && h >= 0;
    }

    /// <summary>The map from <c>viewBox</c> units to a viewport of the given size, as <c>preserveAspectRatio</c> places it.</summary>
    private SvgAffine ViewBoxTransform(string? preserve, double vbX, double vbY, double vbW, double vbH, double width, double height)
    {
        double alignX = 0.5, alignY = 0.5;
        bool uniform = true, slice = false;
        if (preserve is not null && !TryParsePreserveAspectRatio(preserve, out uniform, out alignX, out alignY, out slice))
        {
            Issue(SvgIssueKind.Invalid, "preserveAspectRatio");
            (uniform, alignX, alignY, slice) = (true, 0.5, 0.5, false);
        }

        double sx = width / vbW, sy = height / vbH;
        if (uniform) sx = sy = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
        return new SvgAffine(sx, 0, 0, sy,
            -vbX * sx + alignX * (width - vbW * sx),
            -vbY * sy + alignY * (height - vbH * sy));
    }

    private static bool TryParsePreserveAspectRatio(string text, out bool uniform, out double alignX, out double alignY, out bool slice)
    {
        (uniform, alignX, alignY, slice) = (true, 0.5, 0.5, false);
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        if (i < parts.Length && parts[i] == "defer") i++;
        if (i >= parts.Length) return false;
        string align = parts[i++];
        if (align == "none")
        {
            uniform = false;
        }
        else
        {
            if (align.Length != 8 || align[0] != 'x' || align[4] != 'Y') return false;
            if (!Part(align.AsSpan(1, 3), out alignX) || !Part(align.AsSpan(5, 3), out alignY)) return false;
        }
        if (i < parts.Length)
        {
            if (parts[i] == "slice") slice = true;
            else if (parts[i] != "meet") return false;
            i++;
        }
        return i == parts.Length;

        static bool Part(ReadOnlySpan<char> word, out double fraction)
        {
            fraction = word is "Min" ? 0 : word is "Mid" ? 0.5 : 1;
            return word is "Min" or "Mid" or "Max";
        }
    }

    // ---- The tree -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Every element drawn is counted, and so is every instance a <c>use</c> makes of one: a few nested <c>use</c>
    /// elements can ask for far more than the document holds, and running out of input is no limit on that.
    /// </summary>
    private void Charge(int depth)
    {
        if (++_visits > options.MaxElements)
        {
            throw new InvalidDataException($"The SVG draws more than {options.MaxElements} elements once its use elements are followed.");
        }
        if (depth >= options.MaxDepth)
        {
            throw new InvalidDataException($"The SVG nests elements more than {options.MaxDepth} deep once its use elements are followed.");
        }
    }

    /// <summary>
    /// What every drawn element starts with: its style, and its transform on top of what is above it. False when the
    /// element and everything in it is not drawn.
    /// </summary>
    private bool Enter(SvgElement element, in SvgStyle parent, SvgAffine outer, in SvgViewport viewport, bool isRoot, out SvgStyle style, out SvgAffine matrix)
    {
        matrix = outer;
        style = ComputeStyle(element, parent, viewport);
        if (!style.Display) return false;

        SvgAffine local = SvgAffine.Identity;
        if (element.Get("transform") is { } transformText && !SvgAffine.TryParse(transformText, out local))
        {
            Issue(SvgIssueKind.Invalid, "transform");
            local = SvgAffine.Identity;
        }
        // A transform on the outermost svg applies outside its viewBox mapping; everywhere else it nests inside.
        matrix = isRoot ? local * outer : outer * local;
        // A transform that cannot be inverted (scale(0), say) turns the element off.
        return matrix.IsFinite && matrix.Determinant != 0 && style.Opacity > 0;
    }

    private static bool NeedsLayer(in SvgStyle style) => style.Opacity < 1 || style.ClipPath is not null || style.Mask is not null;

    /// <summary>
    /// Opacity on a container needs a layer, because its children may overlap; so do a clip and a mask, which apply
    /// to what the element draws as a whole. Everything else is drawn straight into what is above.
    /// </summary>
    private static List<VectorNode> BeginLayer(in SvgStyle style, List<VectorNode> output) => NeedsLayer(style) ? [] : output;

    /// <summary>
    /// Closes a layer <see cref="BeginLayer"/> opened: resolves the element's clip and mask against what it drew and
    /// adds the group. <paramref name="matrix"/> is the element's user space, which is what a clip or mask is defined in.
    /// </summary>
    /// <param name="shapePath">For a shape, its path: its bounding box is the path's, whatever markers were drawn with it.</param>
    private void EndLayer(List<VectorNode> target, List<VectorNode> output, in SvgStyle style, string? id, SvgAffine matrix,
        in SvgViewport viewport, int depth, VectorPath? shapePath = null)
    {
        if (ReferenceEquals(target, output) || target.Count == 0) return;

        VectorClip? clip = null;
        VectorMask? mask = null;
        if (style.ClipPath is not null || style.Mask is not null)
        {
            VectorRect? box = shapePath is null ? BoundsOf(target, matrix) : shapePath.TryGetBounds(out VectorRect own) ? own : null;
            if (style.ClipPath is { } clipElement)
            {
                clip = Clip(clipElement, matrix, box, viewport, depth + 1);
                if (clip is { Shapes.IsEmpty: true }) return;       // a region with nothing in it: nothing is left to draw
            }
            if (style.Mask is { } maskElement)
            {
                mask = Mask(maskElement, matrix, box, viewport, depth + 1);
                if (mask is { Children.IsEmpty: true }) return;     // a mask with no picture keeps nothing
            }
        }
        if (clip is null && mask is null && style.Opacity >= 1)
        {
            // The clip or mask turned out to be one that is ignored: no layer is needed after all.
            output.AddRange(target);
            return;
        }
        output.Add(new VectorGroup { Opacity = (float)style.Opacity, Clip = clip, Mask = mask, Children = [.. target], Id = id });
    }

    private void Walk(SvgElement element, in SvgStyle parent, SvgAffine outer, in SvgViewport viewport, List<VectorNode> output, int depth)
    {
        Charge(depth);
        switch (element.Name)
        {
            case "g" or "a":
            {
                if (!Enter(element, parent, outer, viewport, false, out SvgStyle style, out SvgAffine matrix)) return;
                List<VectorNode> target = BeginLayer(style, output);
                foreach (SvgElement child in element.Children)
                {
                    Walk(child, style, matrix, viewport, target, depth + 1);
                }
                EndLayer(target, output, style, element.Get("id"), matrix, viewport, depth);
                return;
            }

            case "svg":
                Viewport(element, parent, outer, viewport, output, depth, symbol: false, null, null);
                return;

            case "use":
                Use(element, parent, outer, viewport, output, depth);
                return;

            case "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon":
            {
                if (Enter(element, parent, outer, viewport, false, out SvgStyle style, out SvgAffine matrix))
                {
                    Shape(element, style, matrix, viewport, output, depth);
                }
                return;
            }

            case "image":
            {
                if (Enter(element, parent, outer, viewport, false, out SvgStyle style, out SvgAffine matrix))
                {
                    Image(element, style, matrix, viewport, output, depth);
                }
                return;
            }

            default:
                if (!NeverRendered.Contains(element.Name)) Issue(SvgIssueKind.Unsupported, $"<{element.Name}> element");
                return;
        }
    }

    /// <summary>
    /// A nested <c>svg</c>, or a <c>symbol</c> a <c>use</c> points at: a new viewport, with its own <c>viewBox</c> mapping
    /// and its own size for percentages. The width and height a <c>use</c> gives win over the element's own.
    /// </summary>
    private void Viewport(SvgElement element, in SvgStyle parent, SvgAffine outer, in SvgViewport viewport, List<VectorNode> output, int depth,
        bool symbol, double? useWidth, double? useHeight)
    {
        if (!Enter(element, parent, outer, viewport, false, out SvgStyle style, out SvgAffine matrix)) return;
        double fontSize = style.FontSize;
        double x = symbol ? 0 : Length(element, "x", viewport.Width, fontSize);
        double y = symbol ? 0 : Length(element, "y", viewport.Height, fontSize);
        double w = useWidth ?? ViewportSize(element, "width", viewport.Width, fontSize);
        double h = useHeight ?? ViewportSize(element, "height", viewport.Height, fontSize);
        if (w <= 0 || h <= 0) return;

        var inner = new SvgAffine(1, 0, 0, 1, x, y);
        var innerViewport = new SvgViewport(w, h);
        if (element.Get("viewBox") is { } viewBoxText)
        {
            if (TryParseViewBox(viewBoxText, out double vbX, out double vbY, out double vbW, out double vbH))
            {
                if (vbW <= 0 || vbH <= 0) return;
                inner *= ViewBoxTransform(element.Get("preserveAspectRatio"), vbX, vbY, vbW, vbH, w, h);
                innerViewport = new SvgViewport(vbW, vbH);
            }
            else
            {
                Issue(SvgIssueKind.Invalid, "viewBox");
            }
        }

        List<VectorNode> target = BeginLayer(style, output);
        int first = target.Count;
        foreach (SvgElement child in element.Children)
        {
            Walk(child, style, matrix * inner, innerViewport, target, depth + 1);
        }
        // These viewports clip what leaves them unless overflow says otherwise. The clip is only put in the scene when
        // something really can reach outside: most symbols stay inside their box, and a clip is a layer to a renderer.
        if (style.OverflowVisible != true && ReachesOutside(target, first, matrix, x, y, w, h))
        {
            ClipToRectangle(target, first, matrix, x, y, w, h);
        }
        // A clip or mask on the element is in the coordinates of its content: inside its x and y and its viewBox
        // mapping (measured in a browser).
        EndLayer(target, output, style, element.Get("id"), matrix * inner, innerViewport, depth);
    }

    /// <summary>A width or height that defaults to the whole of what it is a percentage of. Below zero is an error and draws nothing.</summary>
    private double ViewportSize(SvgElement element, string attribute, double percentBase, double fontSize)
    {
        if (element.Get(attribute) is not { } text || text.AsSpan().Trim() is "auto") return percentBase;
        if (!SvgLength.TryParse(text, out SvgLength length))
        {
            Issue(SvgIssueKind.Invalid, attribute);
            return percentBase;
        }
        if (length.Value < 0) Issue(SvgIssueKind.Invalid, attribute);
        return length.Resolve(fontSize, percentBase);
    }

    /// <summary>
    /// Whether any of the nodes from <paramref name="first"/> on may draw outside the rectangle, which is in the
    /// coordinates <paramref name="matrix"/> maps to the picture. Shapes are taken as their bounding box grown by the most
    /// their stroke can add, so this can say yes for a shape that only comes close.
    /// </summary>
    private static bool ReachesOutside(List<VectorNode> nodes, int first, SvgAffine matrix, double x, double y, double w, double h)
    {
        if (!Matrix3x2.Invert(matrix.ToMatrix(), out Matrix3x2 toViewport)) return false;
        float slack = (float)(1e-3 * (Math.Max(w, h) + 1));
        float left = (float)x - slack, top = (float)y - slack, right = (float)(x + w) + slack, bottom = (float)(y + h) + slack;
        for (int i = first; i < nodes.Count; i++)
        {
            if (Outside(nodes[i])) return true;
        }
        return false;

        bool Outside(VectorNode node)
        {
            if (node is VectorGroup group)
            {
                foreach (VectorNode child in group.Children)
                {
                    if (Outside(child)) return true;
                }
                return false;
            }

            VectorRect bounds;
            float grow = 0;
            Matrix3x2 map;
            if (node is VectorImage image)
            {
                bounds = image.Bounds;
                map = image.Transform * toViewport;
            }
            else
            {
                var shape = (VectorShape)node;
                if (!shape.Path.TryGetBounds(out bounds)) return false;
                if (shape.Stroke is { } stroke) grow = StrokeReach(shape.Path, stroke);
                map = shape.Transform * toViewport;
            }
            ReadOnlySpan<Vector2> corners =
            [
                new(bounds.X - grow, bounds.Y - grow), new(bounds.Right + grow, bounds.Y - grow),
                new(bounds.X - grow, bounds.Bottom + grow), new(bounds.Right + grow, bounds.Bottom + grow),
            ];
            foreach (Vector2 corner in corners)
            {
                Vector2 p = Vector2.Transform(corner, map);
                if (p.X < left || p.X > right || p.Y < top || p.Y > bottom) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// How far a stroke can reach beyond its path, at most: half its width, more where a miter is drawn at a corner
    /// and by a square cap. Worked out from the corners the path really has, because the blunt answer (half the width
    /// times the miter limit, everywhere) says that a round icon with a thin stroke leaves a box it sits well inside.
    /// </summary>
    private static float StrokeReach(VectorPath path, VectorStroke stroke)
    {
        float half = stroke.Width / 2;
        float most = 1f;
        ReadOnlySpan<float> c = path.Coordinates;
        Vector2 point = default, start = default, startTangent = default, endTangent = default;
        bool open = false, first = true, anyOpenEnd = false;
        int i = 0;

        void Corner(Vector2 arriving, Vector2 leaving)
        {
            if (stroke.Join != LineJoin.Miter || arriving == default || leaving == default) return;
            float cos = Vector2.Dot(Vector2.Normalize(arriving), Vector2.Normalize(leaving));
            // Miter length over stroke width is 1 / cos(half the turn); past the limit the corner is bevelled.
            float ratio = MathF.Sqrt(2f / MathF.Max(1e-6f, 1f + cos));
            if (ratio <= stroke.MiterLimit) most = MathF.Max(most, ratio);
        }

        void Segment(Vector2 leaving, Vector2 arriving)
        {
            if (first) startTangent = leaving;
            else Corner(endTangent, leaving);
            endTangent = arriving;
            first = false;
        }

        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    anyOpenEnd |= open && !first;
                    point = start = new Vector2(c[i], c[i + 1]);
                    open = true;
                    first = true;
                    break;
                case PathVerb.Line:
                {
                    var to = new Vector2(c[i], c[i + 1]);
                    if (to != point) Segment(to - point, to - point);
                    point = to;
                    break;
                }
                case PathVerb.Quad:
                {
                    Vector2 q = new(c[i], c[i + 1]), to = new(c[i + 2], c[i + 3]);
                    Segment(q != point ? q - point : to - point, to != q ? to - q : to - point);
                    point = to;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Vector2 q1 = new(c[i], c[i + 1]), q2 = new(c[i + 2], c[i + 3]), to = new(c[i + 4], c[i + 5]);
                    Vector2 leaving = q1 != point ? q1 - point : q2 != point ? q2 - point : to - point;
                    Vector2 arriving = to != q2 ? to - q2 : to != q1 ? to - q1 : to - point;
                    Segment(leaving, arriving);
                    point = to;
                    break;
                }
                default:
                    if (!first)
                    {
                        // The closing line, if there is one, and the corner where the subpath meets its start.
                        if (point != start) Segment(start - point, start - point);
                        Corner(endTangent, startTangent);
                    }
                    point = start;
                    open = false;
                    first = true;
                    break;
            }
            i += VectorPath.CoordinateCount(verb);
        }
        anyOpenEnd |= open && !first;

        // A square cap reaches half the width beyond the end and half to each side: the diagonal.
        if (anyOpenEnd && stroke.Cap == LineCap.Square) most = MathF.Max(most, 1.4143f);
        return half * most;
    }

    /// <summary>
    /// <c>use</c>: the element it names, drawn here as if it were the content of a group that carries the
    /// <c>use</c> element's own properties and transform, moved by its <c>x</c> and <c>y</c>.
    /// </summary>
    private void Use(SvgElement element, in SvgStyle parent, SvgAffine outer, in SvgViewport viewport, List<VectorNode> output, int depth)
    {
        if (!Enter(element, parent, outer, viewport, false, out SvgStyle style, out SvgAffine matrix)) return;
        if (element.Get("href") is not { } href || (href = href.Trim()).Length == 0) return;
        if (href[0] != '#')
        {
            Issue(SvgIssueKind.Unsupported, "use of an element in another document");
            return;
        }
        if (!tree.Ids.TryGetValue(href[1..], out SvgElement? target))
        {
            Issue(SvgIssueKind.Invalid, "use: no element with that id");
            return;
        }
        // A use that leads back to itself is an error and draws nothing. "Back" is: to an element already being
        // instantiated, or to an ancestor of this use or of any use we are inside of. The second half is what a
        // browser does and it matters for what is left: of two groups that use each other, each is drawn once with
        // the other inside it, and no further.
        bool circular = _active.Contains(target);
        for (int i = _uses.Count; i >= 0 && !circular; i--)
        {
            for (SvgElement? ancestor = i == _uses.Count ? element : _uses[i]; ancestor is not null && !circular; ancestor = ancestor.Parent)
            {
                circular = ReferenceEquals(ancestor, target);
            }
        }
        if (circular)
        {
            Issue(SvgIssueKind.Invalid, "use: circular reference");
            return;
        }

        double fontSize = style.FontSize;
        // A clip or mask on the use is in the coordinates its content is drawn in, the move by x and y included
        // (measured in a browser).
        matrix *= new SvgAffine(1, 0, 0, 1, Length(element, "x", viewport.Width, fontSize), Length(element, "y", viewport.Height, fontSize));

        List<VectorNode> target_ = BeginLayer(style, output);
        _active.Add(target);
        _uses.Add(element);
        try
        {
            if (target.Name is "symbol" or "svg")
            {
                Charge(depth + 1);
                Viewport(target, style, matrix, viewport, target_, depth + 1, target.Name == "symbol",
                    UseSize(element, "width", viewport.Width, fontSize), UseSize(element, "height", viewport.Height, fontSize));
            }
            else
            {
                Walk(target, style, matrix, viewport, target_, depth + 1);
            }
        }
        finally
        {
            _active.Remove(target);
            _uses.RemoveAt(_uses.Count - 1);
        }
        EndLayer(target_, output, style, element.Get("id"), matrix, viewport, depth);
    }

    /// <summary>The width or height a <c>use</c> gives the viewport it instantiates, or null when it gives none.</summary>
    private double? UseSize(SvgElement element, string attribute, double percentBase, double fontSize)
    {
        if (element.Get(attribute) is not { } text || text.AsSpan().Trim() is "auto") return null;
        if (!SvgLength.TryParse(text, out SvgLength length))
        {
            Issue(SvgIssueKind.Invalid, attribute);
            return null;
        }
        if (length.Value < 0) Issue(SvgIssueKind.Invalid, attribute);
        return length.Resolve(fontSize, percentBase);
    }

    /// <summary>
    /// The outline of a shape element, in its own coordinates. False when it has none (a zero size, an error that
    /// leaves nothing, coordinates out of range). <paramref name="fillable"/> is false for a <c>line</c>.
    /// </summary>
    private bool TryGeometry(SvgElement element, in SvgStyle style, in SvgViewport viewport, out VectorPath path, out bool fillable)
    {
        fillable = true;
        path = VectorPath.Empty;
        double fontSize = style.FontSize;
        switch (element.Name)
        {
            case "path":
                // Path data and point lists do not depend on where the element is drawn, so one path serves every
                // instance a use makes of it.
                if (element.Path is null)
                {
                    _builder.Clear();
                    if (element.Get("d") is { } d && !SvgGeometry.ParsePath(d, _builder)) Issue(SvgIssueKind.Invalid, "path data");
                    element.Path = _builder.ToPath();
                }
                path = element.Path;
                break;

            case "polyline" or "polygon":
                if (element.Path is null)
                {
                    _builder.Clear();
                    if (element.Get("points") is { } points && !SvgGeometry.Polyline(points, element.Name == "polygon", _builder))
                    {
                        Issue(SvgIssueKind.Invalid, "points");
                    }
                    element.Path = _builder.ToPath();
                }
                path = element.Path;
                break;

            default:
            {
                PathBuilder builder = _builder;
                builder.Clear();
                switch (element.Name)
                {
                    case "rect":
                    {
                        double x = Length(element, "x", viewport.Width, fontSize), y = Length(element, "y", viewport.Height, fontSize);
                        double w = Length(element, "width", viewport.Width, fontSize), h = Length(element, "height", viewport.Height, fontSize);
                        if (w < 0 || h < 0) Issue(SvgIssueKind.Invalid, "negative rect size");
                        if (w <= 0 || h <= 0) return false;
                        double? rx = Radius(element, "rx", viewport.Width, fontSize), ry = Radius(element, "ry", viewport.Height, fontSize);
                        SvgGeometry.Rectangle(builder, x, y, w, h, Math.Min(rx ?? ry ?? 0, w / 2), Math.Min(ry ?? rx ?? 0, h / 2));
                        break;
                    }

                    case "circle":
                    {
                        double r = Length(element, "r", viewport.Diagonal, fontSize);
                        if (r < 0) Issue(SvgIssueKind.Invalid, "negative radius");
                        if (r <= 0) return false;
                        SvgGeometry.Ellipse(builder, Length(element, "cx", viewport.Width, fontSize), Length(element, "cy", viewport.Height, fontSize), r, r);
                        break;
                    }

                    case "ellipse":
                    {
                        double? rx = Radius(element, "rx", viewport.Width, fontSize), ry = Radius(element, "ry", viewport.Height, fontSize);
                        double rxValue = rx ?? ry ?? 0, ryValue = ry ?? rx ?? 0;
                        if (rxValue <= 0 || ryValue <= 0) return false;
                        SvgGeometry.Ellipse(builder, Length(element, "cx", viewport.Width, fontSize), Length(element, "cy", viewport.Height, fontSize), rxValue, ryValue);
                        break;
                    }

                    default: // line
                        // A line has no interior, so it is never filled.
                        fillable = false;
                        builder.MoveTo((float)Length(element, "x1", viewport.Width, fontSize), (float)Length(element, "y1", viewport.Height, fontSize));
                        builder.LineTo((float)Length(element, "x2", viewport.Width, fontSize), (float)Length(element, "y2", viewport.Height, fontSize));
                        break;
                }
                path = builder.ToPath();
                break;
            }
        }

        if (path.IsEmpty) return false;
        foreach (float coordinate in path.Coordinates)
        {
            if (!float.IsFinite(coordinate))
            {
                Issue(SvgIssueKind.Invalid, "coordinate out of range");
                return false;
            }
        }
        _verbs += path.Verbs.Length;
        if (_verbs > options.MaxPathVerbs)
        {
            throw new InvalidDataException($"The SVG's paths have more than {options.MaxPathVerbs} segments in total.");
        }
        return true;
    }

    private void Shape(SvgElement element, in SvgStyle style, SvgAffine matrix, in SvgViewport viewport, List<VectorNode> output, int depth)
    {
        if (!TryGeometry(element, style, viewport, out VectorPath path, out bool fillable) || !style.Visible) return;

        _paintDepth = depth;
        VectorFill? fill = fillable ? MakeFill(style, path, viewport) : null;
        _paintDepth = depth;
        VectorStroke? stroke = MakeStroke(style, path, viewport);
        // Markers belong to the shapes that have vertices of their own.
        bool markers = (style.MarkerStart ?? style.MarkerMid ?? style.MarkerEnd) is not null
            && element.Name is "path" or "line" or "polyline" or "polygon";
        if (fill is null && stroke is null && !markers) return;

        string? id = element.Get("id");
        float opacity = (float)style.Opacity;
        Matrix3x2 transform = matrix.ToMatrix();
        bool layered = style.ClipPath is not null || style.Mask is not null;
        // The stroke is painted after the fill unless paint-order puts it first; with one of the two it does not matter.
        // (The usual order is checked first: this is every shape of every drawing, and almost none says otherwise.)
        bool strokeFirst = style.PaintOrder != NormalPaintOrder && fill is not null && stroke is not null
            && Array.IndexOf(PaintSlots(style.PaintOrder), 1) < Array.IndexOf(PaintSlots(style.PaintOrder), 0);
        if (!markers && !strokeFirst && !layered)
        {
            if (opacity < 1 && fill is not null && stroke is not null)
            {
                // Fill and stroke overlap along the outline, so the pair has to be faded as one.
                var shape = new VectorShape { Path = path, Transform = transform, Fill = fill, Stroke = stroke, Id = id };
                output.Add(new VectorGroup { Opacity = opacity, Children = [shape] });
                return;
            }
            // One paint cannot overlap itself, so its opacity and the element's multiply exactly.
            if (fill is not null) fill = fill with { Opacity = fill.Opacity * opacity };
            if (stroke is not null) stroke = stroke with { Opacity = stroke.Opacity * opacity };
            output.Add(new VectorShape { Path = path, Transform = transform, Fill = fill, Stroke = stroke, Id = id });
            return;
        }

        // The general case: the fill, the stroke and the markers as separate pieces, in the order paint-order gives.
        var pieces = new List<VectorNode>(4);
        int[] slots = PaintSlots(style.PaintOrder);
        for (int slot = 0; slot < 3; slot++)
        {
            switch (slots[slot])
            {
                case 0 when fill is not null:
                {
                    // The stroke straight after the fill is the usual shape with both.
                    bool both = stroke is not null && slot < 2 && slots[slot + 1] == 1;
                    pieces.Add(new VectorShape { Path = path, Transform = transform, Fill = fill, Stroke = both ? stroke : null, Id = id });
                    id = null;
                    if (both) slot++;
                    break;
                }
                case 1 when stroke is not null:
                    pieces.Add(new VectorShape { Path = path, Transform = transform, Stroke = stroke, Id = id });
                    id = null;
                    break;
                case 2 when markers:
                    Markers(element, style, matrix, viewport, path, pieces, depth);
                    break;
            }
        }
        if (pieces.Count == 0) return;

        if (!layered)
        {
            if (opacity >= 1)
            {
                output.AddRange(pieces);
                return;
            }
            if (pieces is [VectorShape { Fill: null } or VectorShape { Stroke: null }])
            {
                var only = (VectorShape)pieces[0];
                output.Add(new VectorShape
                {
                    Path = only.Path, Transform = only.Transform, Id = only.Id,
                    Fill = only.Fill is { } f ? f with { Opacity = f.Opacity * opacity } : null,
                    Stroke = only.Stroke is { } s ? s with { Opacity = s.Opacity * opacity } : null,
                });
                return;
            }
        }
        EndLayer(pieces, output, style, null, matrix, viewport, depth, path);
    }

    private static readonly int[][] PaintOrders =
        [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

    /// <summary>The three things painted (0 the fill, 1 the stroke, 2 the markers) in the order a <see cref="SvgStyle.PaintOrder"/> gives.</summary>
    private static int[] PaintSlots(byte order)
    {
        foreach (int[] slots in PaintOrders)
        {
            if ((slots[0] | slots[1] << 2 | slots[2] << 4) == order) return slots;
        }
        return PaintOrders[0];
    }

    private VectorFill? MakeFill(in SvgStyle style, VectorPath path, in SvgViewport viewport)
    {
        if (style.FillOpacity <= 0 || ResolvePaint(style.Fill, style, path, viewport) is not { } paint) return null;
        // A drawing has few fills and many shapes: the one before is very often this one again.
        if (_lastFill is { } last && last.Opacity == (float)style.FillOpacity && last.Rule == style.FillRule && SamePaint(last.Paint, paint)) return last;
        return _lastFill = new VectorFill(paint, (float)style.FillOpacity, style.FillRule);
    }

    private VectorStroke? MakeStroke(in SvgStyle style, VectorPath path, in SvgViewport viewport)
    {
        if (style.StrokeOpacity <= 0 || style.StrokeWidth <= 0 || ResolvePaint(style.Stroke, style, path, viewport) is not { } paint) return null;

        ImmutableArray<float> dashes = [];
        if (style.Dashes is { Length: > 0 } pattern && pattern.Sum() > 0)
        {
            // An odd number of values is the list written twice.
            dashes = pattern.Length % 2 == 0
                ? [.. pattern.Select(d => (float)d)]
                : [.. pattern.Concat(pattern).Select(d => (float)d)];
        }
        if (_lastStroke is { } last && dashes.IsEmpty && last.Dashes.IsEmpty && last.Opacity == (float)style.StrokeOpacity
            && last.Width == (float)style.StrokeWidth && last.Cap == style.Cap && last.Join == style.Join
            && last.MiterLimit == (float)style.MiterLimit && last.DashOffset == (float)style.DashOffset && SamePaint(last.Paint, paint))
        {
            return last;
        }
        return _lastStroke = new VectorStroke(paint, (float)style.StrokeOpacity, (float)style.StrokeWidth, style.Cap, style.Join,
            (float)style.MiterLimit, dashes, (float)style.DashOffset);
    }

    /// <summary>The same object, or two solid paints of one colour. A gradient is only ever the same when it is the same object.</summary>
    private static bool SamePaint(VectorPaint a, VectorPaint b) =>
        ReferenceEquals(a, b) || (a is SolidPaint x && b is SolidPaint y && x.Color == y.Color);

    private double Length(SvgElement element, string attribute, double percentBase, double fontSize)
    {
        if (element.Get(attribute) is not { } text) return 0;
        if (SvgLength.TryParse(text, out SvgLength length)) return length.Resolve(fontSize, percentBase);
        Issue(SvgIssueKind.Invalid, attribute);
        return 0;
    }

    /// <summary>A corner or ellipse radius: null when absent, <c>auto</c> or unusable, so that the other radius stands in.</summary>
    private double? Radius(SvgElement element, string attribute, double percentBase, double fontSize)
    {
        if (element.Get(attribute) is not { } text || text.AsSpan().Trim() is "auto") return null;
        if (SvgLength.TryParse(text, out SvgLength length) && length.Value >= 0) return length.Resolve(fontSize, percentBase);
        Issue(SvgIssueKind.Invalid, attribute);
        return null;
    }
}
