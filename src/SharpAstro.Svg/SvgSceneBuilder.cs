using System.Collections.Frozen;
using System.Collections.Immutable;
using SharpAstro.Vector;

namespace SharpAstro.Svg;

internal enum SvgPaintKind : byte { None, Color, CurrentColor }

/// <summary>A <c>fill</c> or <c>stroke</c> value. <c>currentColor</c> stays a keyword so that it inherits as one.</summary>
internal readonly record struct SvgPaint(SvgPaintKind Kind, Rgba32 Color);

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

    // Not inherited.
    public double Opacity;
    public bool Display;

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
        Opacity = 1,
        Display = true,
    };
}

/// <summary>Walks the element tree and emits the scene: styles cascaded, transforms multiplied out, shapes turned into paths.</summary>
internal sealed class SvgSceneBuilder(SvgReadOptions options)
{
    /// <summary>The properties read from presentation attributes and from <c>style</c>.</summary>
    private static readonly FrozenSet<string> Properties = FrozenSet.ToFrozenSet(
    [
        "fill", "stroke", "fill-rule", "fill-opacity", "stroke-opacity", "opacity", "stroke-width", "stroke-linecap",
        "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "color", "visibility", "display",
        "font-size", "clip-path", "mask", "filter", "marker", "marker-start", "marker-mid", "marker-end", "mix-blend-mode",
        "paint-order", "vector-effect", "transform-origin",
    ], StringComparer.Ordinal);

    /// <summary>Elements that are never drawn where they stand. What refers to them is reported where the reference is.</summary>
    private static readonly FrozenSet<string> NeverRendered = FrozenSet.ToFrozenSet(
    [
        "defs", "title", "desc", "metadata", "symbol", "marker", "clipPath", "mask", "pattern", "linearGradient",
        "radialGradient", "filter", "view", "cursor",
    ], StringComparer.Ordinal);

    private readonly Dictionary<(SvgIssueKind Kind, string What), int> _issues = [];
    private readonly List<(string Name, string Value)> _declarations = [];
    private double _percentWidth, _percentHeight, _percentDiagonal;
    private long _verbs;

    public SvgDocument Build(SvgElement root)
    {
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
            _percentWidth = hasViewBox ? vbW : width;
            _percentHeight = hasViewBox ? vbH : height;
            _percentDiagonal = Math.Sqrt((_percentWidth * _percentWidth + _percentHeight * _percentHeight) / 2);
            Walk(root, SvgStyle.Initial, toPicture, nodes, isRoot: true);
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

    // ---- The root's size ----------------------------------------------------------------------------------------

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

    /// <summary>The map from <c>viewBox</c> units to the picture, as <c>preserveAspectRatio</c> places it.</summary>
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

    private void Walk(SvgElement element, in SvgStyle parent, SvgAffine outer, List<VectorNode> output, bool isRoot = false)
    {
        string name = element.Name;
        bool container = isRoot || name is "g" or "a";
        if (!container && name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
        {
            if (!NeverRendered.Contains(name)) Issue(SvgIssueKind.Unsupported, $"<{name}> element");
            return;
        }

        SvgStyle style = ComputeStyle(element, parent);
        if (!style.Display) return;

        SvgAffine local = SvgAffine.Identity;
        if (element.Get("transform") is { } transformText && !SvgAffine.TryParse(transformText, out local))
        {
            Issue(SvgIssueKind.Invalid, "transform");
            local = SvgAffine.Identity;
        }
        // A transform on the outermost svg applies outside its viewBox mapping; everywhere else it nests inside.
        SvgAffine matrix = isRoot ? local * outer : outer * local;
        // A transform that cannot be inverted (scale(0), say) turns the element off.
        if (!matrix.IsFinite || matrix.Determinant == 0) return;
        if (style.Opacity <= 0) return;

        if (container)
        {
            // Opacity on a container is the one thing here that needs a layer: its children may overlap.
            List<VectorNode> target = style.Opacity < 1 ? [] : output;
            foreach (SvgElement child in element.Children)
            {
                Walk(child, style, matrix, target);
            }
            if (!ReferenceEquals(target, output) && target.Count > 0)
            {
                output.Add(new VectorGroup { Opacity = (float)style.Opacity, Children = [.. target], Id = element.Get("id") });
            }
            return;
        }

        Shape(element, style, matrix, output);
    }

    private void Shape(SvgElement element, in SvgStyle style, SvgAffine matrix, List<VectorNode> output)
    {
        var builder = new PathBuilder();
        bool fillable = true;
        double fontSize = style.FontSize;
        switch (element.Name)
        {
            case "path":
                if (element.Get("d") is not { } data) return;
                if (!SvgGeometry.ParsePath(data, builder)) Issue(SvgIssueKind.Invalid, "path data");
                break;

            case "rect":
            {
                double x = Length(element, "x", _percentWidth, fontSize), y = Length(element, "y", _percentHeight, fontSize);
                double w = Length(element, "width", _percentWidth, fontSize), h = Length(element, "height", _percentHeight, fontSize);
                if (w < 0 || h < 0) Issue(SvgIssueKind.Invalid, "negative rect size");
                if (w <= 0 || h <= 0) return;
                double? rx = Radius(element, "rx", _percentWidth, fontSize), ry = Radius(element, "ry", _percentHeight, fontSize);
                SvgGeometry.Rectangle(builder, x, y, w, h, Math.Min(rx ?? ry ?? 0, w / 2), Math.Min(ry ?? rx ?? 0, h / 2));
                break;
            }

            case "circle":
            {
                double r = Length(element, "r", _percentDiagonal, fontSize);
                if (r < 0) Issue(SvgIssueKind.Invalid, "negative radius");
                if (r <= 0) return;
                SvgGeometry.Ellipse(builder, Length(element, "cx", _percentWidth, fontSize), Length(element, "cy", _percentHeight, fontSize), r, r);
                break;
            }

            case "ellipse":
            {
                double? rx = Radius(element, "rx", _percentWidth, fontSize), ry = Radius(element, "ry", _percentHeight, fontSize);
                double rxValue = rx ?? ry ?? 0, ryValue = ry ?? rx ?? 0;
                if (rxValue <= 0 || ryValue <= 0) return;
                SvgGeometry.Ellipse(builder, Length(element, "cx", _percentWidth, fontSize), Length(element, "cy", _percentHeight, fontSize), rxValue, ryValue);
                break;
            }

            case "line":
                // A line has no interior, so it is never filled.
                fillable = false;
                builder.MoveTo((float)Length(element, "x1", _percentWidth, fontSize), (float)Length(element, "y1", _percentHeight, fontSize));
                builder.LineTo((float)Length(element, "x2", _percentWidth, fontSize), (float)Length(element, "y2", _percentHeight, fontSize));
                break;

            default: // polyline, polygon
                if (element.Get("points") is not { } points) return;
                if (!SvgGeometry.Polyline(points, element.Name == "polygon", builder)) Issue(SvgIssueKind.Invalid, "points");
                break;
        }

        VectorPath path = builder.ToPath();
        if (path.IsEmpty) return;
        foreach (float coordinate in path.Coordinates)
        {
            if (!float.IsFinite(coordinate))
            {
                Issue(SvgIssueKind.Invalid, "coordinate out of range");
                return;
            }
        }
        _verbs += path.Verbs.Length;
        if (_verbs > options.MaxPathVerbs)
        {
            throw new InvalidDataException($"The SVG's paths have more than {options.MaxPathVerbs} segments in total.");
        }
        if (!style.Visible) return;

        VectorFill? fill = fillable ? MakeFill(style) : null;
        VectorStroke? stroke = MakeStroke(style);
        if (fill is null && stroke is null) return;

        string? id = element.Get("id");
        float opacity = (float)style.Opacity;
        if (opacity < 1 && fill is not null && stroke is not null)
        {
            // Fill and stroke overlap along the outline, so the pair has to be faded as one.
            var shape = new VectorShape { Path = path, Transform = matrix.ToMatrix(), Fill = fill, Stroke = stroke, Id = id };
            output.Add(new VectorGroup { Opacity = opacity, Children = [shape] });
            return;
        }
        // One paint cannot overlap itself, so its opacity and the element's multiply exactly.
        if (fill is not null) fill = fill with { Opacity = fill.Opacity * opacity };
        if (stroke is not null) stroke = stroke with { Opacity = stroke.Opacity * opacity };
        output.Add(new VectorShape { Path = path, Transform = matrix.ToMatrix(), Fill = fill, Stroke = stroke, Id = id });
    }

    private static VectorFill? MakeFill(in SvgStyle style)
    {
        if (Resolve(style.Fill, style) is not { } color || style.FillOpacity <= 0) return null;
        return new VectorFill(new SolidPaint(color), (float)style.FillOpacity, style.FillRule);
    }

    private static VectorStroke? MakeStroke(in SvgStyle style)
    {
        if (Resolve(style.Stroke, style) is not { } color || style.StrokeOpacity <= 0 || style.StrokeWidth <= 0) return null;

        ImmutableArray<float> dashes = [];
        if (style.Dashes is { Length: > 0 } pattern && pattern.Sum() > 0)
        {
            // An odd number of values is the list written twice.
            dashes = pattern.Length % 2 == 0
                ? [.. pattern.Select(d => (float)d)]
                : [.. pattern.Concat(pattern).Select(d => (float)d)];
        }
        return new VectorStroke(new SolidPaint(color), (float)style.StrokeOpacity, (float)style.StrokeWidth, style.Cap, style.Join,
            (float)style.MiterLimit, dashes, (float)style.DashOffset);
    }

    /// <summary>The colour a paint draws with, or null when it draws nothing.</summary>
    private static Rgba32? Resolve(SvgPaint paint, in SvgStyle style)
    {
        Rgba32 color;
        switch (paint.Kind)
        {
            case SvgPaintKind.Color: color = paint.Color; break;
            case SvgPaintKind.CurrentColor: color = style.Color; break;
            default: return null;
        }
        return color.A == 0 ? null : color;
    }

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

    // ---- Style --------------------------------------------------------------------------------------------------

    private SvgStyle ComputeStyle(SvgElement element, in SvgStyle parent)
    {
        SvgStyle style = parent;
        style.Opacity = 1;
        style.Display = true;

        // Presentation attributes first, then the style attribute, which wins.
        _declarations.Clear();
        foreach (KeyValuePair<string, string> attribute in element.Attributes)
        {
            if (Properties.Contains(attribute.Key)) _declarations.Add((attribute.Key, attribute.Value));
        }
        if (element.Get("style") is { } css) ParseDeclarations(css, _declarations);
        if (_declarations.Count == 0) return style;

        // font-size before the rest: em lengths on this element are in its own font size.
        foreach ((string name, string value) in _declarations)
        {
            if (name != "font-size") continue;
            ReadOnlySpan<char> v = value.AsSpan().Trim();
            if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase)) style.FontSize = parent.FontSize;
            else if (SvgLength.TryParse(v, out SvgLength size) && size.Value >= 0) style.FontSize = size.Resolve(parent.FontSize, parent.FontSize);
        }
        foreach ((string name, string value) in _declarations)
        {
            if (name != "font-size") Apply(ref style, parent, name, value);
        }
        return style;
    }

    /// <summary>The declarations of a <c>style</c> attribute that this reader acts on, in order.</summary>
    private static void ParseDeclarations(string css, List<(string Name, string Value)> declarations)
    {
        // Comments out first.
        for (int open = css.IndexOf("/*", StringComparison.Ordinal); open >= 0; open = css.IndexOf("/*", open, StringComparison.Ordinal))
        {
            int close = css.IndexOf("*/", open + 2, StringComparison.Ordinal);
            css = close < 0 ? css[..open] : string.Concat(css.AsSpan(0, open), " ", css.AsSpan(close + 2));
        }

        int start = 0, depth = 0;
        char quote = '\0';
        for (int i = 0; i <= css.Length; i++)
        {
            char c = i < css.Length ? css[i] : ';';
            if (i < css.Length)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c is '"' or '\'') { quote = c; continue; }
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth = Math.Max(0, depth - 1); continue; }
                if (c != ';' || depth > 0) continue;
            }

            ReadOnlySpan<char> declaration = css.AsSpan(start, i - start);
            start = i + 1;
            int colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            string name = declaration[..colon].Trim().ToString().ToLowerInvariant();
            ReadOnlySpan<char> value = declaration[(colon + 1)..].Trim();
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase)) value = value[..^10].TrimEnd();
            if (Properties.Contains(name) || name == "transform") declarations.Add((name, value.ToString()));
        }
    }

    private void Apply(ref SvgStyle s, in SvgStyle parent, string name, string raw)
    {
        ReadOnlySpan<char> v = raw.AsSpan().Trim();
        if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            CopyFromParent(ref s, parent, name);
            return;
        }

        switch (name)
        {
            case "fill":
                if (TryPaint(v, out SvgPaint fill)) s.Fill = fill; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke":
                if (TryPaint(v, out SvgPaint stroke)) s.Stroke = stroke; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "color":
                if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) break; // on color itself this means inherit
                if (SvgColor.TryParse(v, out Rgba32 color)) s.Color = color; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "fill-rule":
                if (v is "nonzero") s.FillRule = FillRule.NonZero;
                else if (v is "evenodd") s.FillRule = FillRule.EvenOdd;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "fill-opacity":
                if (TryOpacity(v, out double fillOpacity)) s.FillOpacity = fillOpacity; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke-opacity":
                if (TryOpacity(v, out double strokeOpacity)) s.StrokeOpacity = strokeOpacity; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "opacity":
                if (TryOpacity(v, out double opacity)) s.Opacity = opacity; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke-width":
                if (SvgLength.TryParse(v, out SvgLength width) && width.Value >= 0) s.StrokeWidth = width.Resolve(s.FontSize, _percentDiagonal);
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke-linecap":
                if (v is "butt") s.Cap = LineCap.Butt;
                else if (v is "round") s.Cap = LineCap.Round;
                else if (v is "square") s.Cap = LineCap.Square;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke-linejoin":
                if (v is "miter") s.Join = LineJoin.Miter;
                else if (v is "round") s.Join = LineJoin.Round;
                else if (v is "bevel") s.Join = LineJoin.Bevel;
                else if (v is "miter-clip" or "arcs")
                {
                    Issue(SvgIssueKind.Unsupported, $"stroke-linejoin: {v}");
                    s.Join = LineJoin.Miter;
                }
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke-miterlimit":
            {
                var scanner = new SvgScanner(v);
                if (scanner.TryNumber(out double limit) && scanner.AtEnd && limit >= 1) s.MiterLimit = limit;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            }
            case "stroke-dasharray":
                if (v is "none") s.Dashes = null;
                else if (TryDashes(v, s.FontSize, out double[]? dashes)) s.Dashes = dashes;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke-dashoffset":
                if (SvgLength.TryParse(v, out SvgLength offset)) s.DashOffset = offset.Resolve(s.FontSize, _percentDiagonal);
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "visibility":
                if (v is "visible") s.Visible = true;
                else if (v is "hidden" or "collapse") s.Visible = false;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "display":
                s.Display = v is not "none";
                break;

            // Read far enough to know the scene is missing something.
            case "clip-path" or "mask" or "filter" or "marker" or "marker-start" or "marker-mid" or "marker-end" or "vector-effect":
                if (v is not "none") Issue(SvgIssueKind.Unsupported, name);
                break;
            case "mix-blend-mode" or "paint-order":
                if (v is not "normal") Issue(SvgIssueKind.Unsupported, name);
                break;
            case "transform":
                Issue(SvgIssueKind.Unsupported, "transform in a style attribute");
                break;
            case "transform-origin":
                Issue(SvgIssueKind.Unsupported, name);
                break;
        }
    }

    private static void CopyFromParent(ref SvgStyle s, in SvgStyle parent, string name)
    {
        switch (name)
        {
            case "fill": s.Fill = parent.Fill; break;
            case "stroke": s.Stroke = parent.Stroke; break;
            case "color": s.Color = parent.Color; break;
            case "fill-rule": s.FillRule = parent.FillRule; break;
            case "fill-opacity": s.FillOpacity = parent.FillOpacity; break;
            case "stroke-opacity": s.StrokeOpacity = parent.StrokeOpacity; break;
            case "opacity": s.Opacity = parent.Opacity; break;
            case "stroke-width": s.StrokeWidth = parent.StrokeWidth; break;
            case "stroke-linecap": s.Cap = parent.Cap; break;
            case "stroke-linejoin": s.Join = parent.Join; break;
            case "stroke-miterlimit": s.MiterLimit = parent.MiterLimit; break;
            case "stroke-dasharray": s.Dashes = parent.Dashes; break;
            case "stroke-dashoffset": s.DashOffset = parent.DashOffset; break;
            case "visibility": s.Visible = parent.Visible; break;
            case "display": s.Display = parent.Display; break;
        }
    }

    /// <summary>
    /// A paint. A reference to a paint server is not followed yet: it is reported, and the fallback after it is
    /// used, or nothing.
    /// </summary>
    private bool TryPaint(ReadOnlySpan<char> v, out SvgPaint paint)
    {
        paint = new SvgPaint(SvgPaintKind.None, default);
        if (v is "none") return true;
        if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            paint = new SvgPaint(SvgPaintKind.CurrentColor, default);
            return true;
        }
        if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            Issue(SvgIssueKind.Unsupported, "paint server (gradient or pattern)");
            int close = v.IndexOf(')');
            ReadOnlySpan<char> fallback = close < 0 ? default : v[(close + 1)..].Trim();
            if (fallback.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) paint = new SvgPaint(SvgPaintKind.CurrentColor, default);
            else if (SvgColor.TryParse(fallback, out Rgba32 fallbackColor)) paint = new SvgPaint(SvgPaintKind.Color, fallbackColor);
            return true;
        }
        if (v is "context-fill" or "context-stroke")
        {
            Issue(SvgIssueKind.Unsupported, v.ToString());
            return true;
        }
        if (!SvgColor.TryParse(v, out Rgba32 color)) return false;
        paint = new SvgPaint(SvgPaintKind.Color, color);
        return true;
    }

    private static bool TryOpacity(ReadOnlySpan<char> v, out double opacity)
    {
        var scanner = new SvgScanner(v);
        if (!scanner.TryNumber(out opacity)) return false;
        if (scanner.Peek == '%')
        {
            scanner.Advance();
            opacity /= 100.0;
        }
        opacity = Math.Clamp(opacity, 0, 1);
        return scanner.AtEnd;
    }

    private bool TryDashes(ReadOnlySpan<char> v, double fontSize, out double[]? dashes)
    {
        dashes = null;
        var values = new List<double>();
        var scanner = new SvgScanner(v);
        scanner.SkipWhitespace();
        while (!scanner.AtEnd)
        {
            if (!SvgLength.TryScan(ref scanner, out SvgLength length) || length.Value < 0) return false;
            values.Add(length.Resolve(fontSize, _percentDiagonal));
            scanner.SkipCommaWhitespace();
        }
        if (values.Count == 0) return false;
        dashes = [.. values];
        return true;
    }
}
