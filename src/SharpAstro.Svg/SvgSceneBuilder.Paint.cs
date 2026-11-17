using System.Collections.Immutable;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

// Paint servers: what a url(#id) fill or stroke becomes for the shape that uses it.
internal sealed partial class SvgSceneBuilder
{
    private readonly Dictionary<SvgElement, GradientDefinition> _gradients = [];

    /// <summary>A gradient element with everything it inherits through <c>href</c> folded in, before any shape is known.</summary>
    private sealed class GradientDefinition
    {
        public bool Radial, UserSpace;
        public SvgAffine Transform = SvgAffine.Identity;
        public GradientExtend Extend;

        /// <summary>Linear: x1, y1, x2, y2. Radial: cx, cy, r, fx, fy, fr.</summary>
        public SvgLength[] Geometry = [];

        /// <summary>Radial only: whether fx and fy were given. When not, the focal point is the centre.</summary>
        public bool HasFocalX, HasFocalY;

        public ImmutableArray<GradientStop> Stops = [];
    }

    private static VectorPaint? Solid(Rgba32 color) => color.A == 0 ? null : new SolidPaint(color);

    /// <summary>What a paint draws with on this shape, or null when it draws nothing.</summary>
    private VectorPaint? ResolvePaint(SvgPaint paint, in SvgStyle style, VectorPath path, in SvgViewport viewport)
    {
        switch (paint.Kind)
        {
            case SvgPaintKind.Color:
                return Solid(paint.Color);
            case SvgPaintKind.CurrentColor:
                return Solid(style.Color);
            case SvgPaintKind.Server:
                if (paint.Server!.Name == "pattern")
                {
                    if (TryPattern(paint.Server, path, viewport, out VectorPaint? pattern)) return pattern;
                }
                else if (TryGradient(paint.Server, path, viewport, out VectorPaint? gradient))
                {
                    return gradient;
                }
                // The server cannot paint this shape: its fallback does.
                return paint.Fallback switch
                {
                    SvgPaintKind.Color => Solid(paint.Color),
                    SvgPaintKind.CurrentColor => Solid(style.Color),
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// The gradient as a paint for one shape. False when it cannot be applied to this shape at all: bounding-box units on
    /// a shape whose box has no width or no height (a straight horizontal line, say), or a gradient transform with no
    /// inverse. Then the fallback colour is used. True with a null paint means the gradient itself says "nothing".
    /// </summary>
    private bool TryGradient(SvgElement server, VectorPath path, in SvgViewport viewport, out VectorPaint? paint)
    {
        paint = null;
        if (!_gradients.TryGetValue(server, out GradientDefinition? definition))
        {
            _gradients[server] = definition = ReadGradient(server);
        }

        ImmutableArray<GradientStop> stops = definition.Stops;
        if (stops.Length == 0) return true;                 // no stops: as if the paint were none
        if (stops.Length == 1)
        {
            paint = Solid(stops[0].Color);                  // one stop: that colour everywhere
            return true;
        }

        SvgAffine transform = definition.Transform;
        if (!definition.UserSpace)
        {
            if (!path.TryGetBounds(out VectorRect box) || box.Width <= 0 || box.Height <= 0) return false;
            // Gradient coordinates are fractions of the shape's bounding box.
            transform = new SvgAffine(box.Width, 0, 0, box.Height, box.X, box.Y) * transform;
        }
        // A gradient transform that cannot be inverted leaves nothing to project onto: the fallback again (as a browser does).
        if (!transform.IsFinite || transform.Determinant == 0) return false;

        SvgLength[] g = definition.Geometry;
        Rgba32 last = stops[^1].Color;
        if (!definition.Radial)
        {
            double x1 = Value(g[0], viewport.Width), y1 = Value(g[1], viewport.Height);
            double x2 = Value(g[2], viewport.Width), y2 = Value(g[3], viewport.Height);
            // A gradient vector of no length is the last stop's colour everywhere.
            paint = x1 == x2 && y1 == y2
                ? Solid(last)
                : new LinearGradientPaint((float)x1, (float)y1, (float)x2, (float)y2, stops, definition.Extend, transform.ToMatrix());
            return true;
        }

        double cx = Value(g[0], viewport.Width), cy = Value(g[1], viewport.Height), r = Value(g[2], viewport.Diagonal);
        double fx = definition.HasFocalX ? Value(g[3], viewport.Width) : cx;
        double fy = definition.HasFocalY ? Value(g[4], viewport.Height) : cy;
        double fr = Value(g[5], viewport.Diagonal);
        if (r <= 0)
        {
            paint = Solid(last);                            // a radius of zero: the last stop's colour everywhere
            return true;
        }
        paint = new RadialGradientPaint((float)fx, (float)fy, (float)Math.Max(0, fr), (float)cx, (float)cy, (float)r,
            stops, definition.Extend, transform.ToMatrix());
        return true;

        double Value(SvgLength length, double percentBase) => definition.UserSpace
            ? length.Resolve(SvgStyle.Initial.FontSize, percentBase)
            : length.Unit == SvgUnit.Percent ? length.Value / 100.0 : length.Value;
    }

    private Dictionary<SvgElement, (VectorRect Box, SvgViewport Viewport, VectorPaint? Paint, long Weight)>? _patternCache;

    /// <summary>Made when first needed: most documents have no pattern.</summary>
    private Dictionary<SvgElement, (VectorRect Box, SvgViewport Viewport, VectorPaint? Paint, long Weight)> _patterns => _patternCache ??= [];

    /// <summary>
    /// The pattern as a paint for one shape. False when it cannot be applied to this shape (bounding-box units on a shape
    /// whose box has no width or no height, a pattern transform with no inverse): then the fallback colour is used.
    /// True with a null paint means the pattern itself is nothing (a tile of no size, no content).
    /// </summary>
    private bool TryPattern(SvgElement server, VectorPath path, in SvgViewport viewport, out VectorPaint? paint)
    {
        paint = null;
        // The element, then whatever its href leads to: an attribute not given here is taken from the first pattern
        // along that chain that gives it, and the content from the first that has any.
        var chain = new List<SvgElement>();
        for (SvgElement? e = server; e is not null && e.Name == "pattern" && !chain.Contains(e);)
        {
            chain.Add(e);
            e = e.Get("href") is { } href && href.Trim() is ['#', .. var id] && tree.Ids.TryGetValue(id, out SvgElement? next) ? next : null;
            if (chain.Count > 64) break;
        }
        string? Find(string attribute)
        {
            foreach (SvgElement e in chain)
            {
                if (e.Get(attribute) is { } value) return value;
            }
            return null;
        }

        bool tileInBox = true, contentInBox = false;
        switch (Find("patternUnits")?.Trim())
        {
            case null or "objectBoundingBox": break;
            case "userSpaceOnUse": tileInBox = false; break;
            default: Issue(SvgIssueKind.Invalid, "patternUnits"); break;
        }
        switch (Find("patternContentUnits")?.Trim())
        {
            case null or "userSpaceOnUse": break;
            case "objectBoundingBox": contentInBox = true; break;
            default: Issue(SvgIssueKind.Invalid, "patternContentUnits"); break;
        }
        SvgAffine transform = SvgAffine.Identity;
        if (Find("patternTransform") is { } transformText && !SvgAffine.TryParse(transformText, out transform))
        {
            Issue(SvgIssueKind.Invalid, "patternTransform");
            transform = SvgAffine.Identity;
        }
        if (!transform.IsFinite || transform.Determinant == 0) return false;

        bool hasViewBox = false;
        double vbX = 0, vbY = 0, vbW = 0, vbH = 0;
        if (Find("viewBox") is { } viewBoxText)
        {
            hasViewBox = TryParseViewBox(viewBoxText, out vbX, out vbY, out vbW, out vbH);
            if (!hasViewBox) Issue(SvgIssueKind.Invalid, "viewBox");
        }
        // With a viewBox the content is fitted to the tile and patternContentUnits says nothing.
        if (hasViewBox) contentInBox = false;

        VectorRect box = default;
        if (tileInBox || contentInBox)
        {
            if (!path.TryGetBounds(out box) || box.Width <= 0 || box.Height <= 0) return false;
        }
        // The same pattern on the same box is the same paint: a drawing often fills many shapes with one pattern in
        // user-space units, where the box does not come into it at all.
        if (_patterns.TryGetValue(server, out var cached) && cached.Box == box && cached.Viewport == viewport)
        {
            // Not walked again, but charged again: the scene holds the tile's content once more for whoever draws or
            // writes it, and patterns filled with patterns would otherwise double it at every level for nothing.
            _visits += cached.Weight;
            if (_visits > options.MaxElements)
            {
                throw new InvalidDataException($"The SVG draws more than {options.MaxElements} elements once its patterns are followed.");
            }
            paint = cached.Paint;
            return true;
        }

        double fontSize = SvgStyle.Initial.FontSize;
        double x, y, w, h;
        if (tileInBox)
        {
            x = box.X + Fraction("x") * box.Width;
            y = box.Y + Fraction("y") * box.Height;
            w = Fraction("width") * box.Width;
            h = Fraction("height") * box.Height;
        }
        else
        {
            x = Tile("x", viewport.Width);
            y = Tile("y", viewport.Height);
            w = Tile("width", viewport.Width);
            h = Tile("height", viewport.Height);
        }
        SvgElement? owner = chain.Find(e => e.Children.Count > 0);
        if (!(w > 0) || !(h > 0) || owner is null || (hasViewBox && (vbW <= 0 || vbH <= 0)))
        {
            _patterns[server] = (box, viewport, null, 0);
            return true;
        }

        // Pattern space is the shape's own coordinates before the pattern transform; the tile sits at (x, y) in it.
        SvgAffine content = new(1, 0, 0, 1, x, y);
        SvgViewport inner = viewport;
        if (hasViewBox)
        {
            content *= ViewBoxTransform(Find("preserveAspectRatio"), vbX, vbY, vbW, vbH, w, h);
            inner = new SvgViewport(vbW, vbH);
        }
        else if (contentInBox)
        {
            content *= new SvgAffine(box.Width, 0, 0, box.Height, 0, 0);
        }

        if (!_active.Add(server))
        {
            // A pattern painted with itself cannot be applied: the fallback colour is (measured in a browser).
            Issue(SvgIssueKind.Invalid, "pattern: circular reference");
            return false;
        }
        var nodes = new List<VectorNode>();
        int depth = _paintDepth + 1;
        long before = _visits;
        try
        {
            Charge(depth);
            SvgStyle style = InheritedStyle(owner, viewport);
            if (style.OverflowVisible == true) Issue(SvgIssueKind.Unsupported, "pattern with overflow: visible");
            foreach (SvgElement child in owner.Children)
            {
                Walk(child, style, content, inner, nodes, depth + 1);
            }
        }
        finally
        {
            _active.Remove(server);
        }

        if (nodes.Count > 0)
        {
            paint = new PatternPaint([.. nodes], new VectorRect((float)x, (float)y, (float)w, (float)h), transform.ToMatrix());
        }
        _patterns[server] = (box, viewport, paint, _visits - before);
        return true;

        double Fraction(string attribute)
        {
            if (Find(attribute) is not { } text) return 0;
            if (SvgLength.TryParse(text, out SvgLength length)) return length.Unit == SvgUnit.Percent ? length.Value / 100.0 : length.Value;
            Issue(SvgIssueKind.Invalid, attribute);
            return 0;
        }

        double Tile(string attribute, double percentBase)
        {
            if (Find(attribute) is not { } text) return 0;
            if (SvgLength.TryParse(text, out SvgLength length)) return length.Resolve(fontSize, percentBase);
            Issue(SvgIssueKind.Invalid, attribute);
            return 0;
        }
    }

    private GradientDefinition ReadGradient(SvgElement element)
    {
        // The element, then whatever its href leads to: an attribute not given here is taken from the first gradient
        // along that chain that gives it, and so are the stops.
        var chain = new List<SvgElement>();
        for (SvgElement? e = element; e is not null && e.Name is "linearGradient" or "radialGradient" && !chain.Contains(e);)
        {
            chain.Add(e);
            e = e.Get("href") is { } href && href.Trim() is ['#', .. var id] && tree.Ids.TryGetValue(id, out SvgElement? next) ? next : null;
            if (chain.Count > 64) break;
        }

        var definition = new GradientDefinition { Radial = element.Name == "radialGradient" };
        string? Find(string attribute, bool sameKind)
        {
            foreach (SvgElement e in chain)
            {
                if ((!sameKind || e.Name == element.Name) && e.Get(attribute) is { } value) return value;
            }
            return null;
        }

        switch (Find("gradientUnits", false)?.Trim())
        {
            case null or "objectBoundingBox": break;
            case "userSpaceOnUse": definition.UserSpace = true; break;
            default: Issue(SvgIssueKind.Invalid, "gradientUnits"); break;
        }
        switch (Find("spreadMethod", false)?.Trim())
        {
            case null or "pad": break;
            case "reflect": definition.Extend = GradientExtend.Reflect; break;
            case "repeat": definition.Extend = GradientExtend.Repeat; break;
            default: Issue(SvgIssueKind.Invalid, "spreadMethod"); break;
        }
        if (Find("gradientTransform", false) is { } transformText && !SvgAffine.TryParse(transformText, out definition.Transform))
        {
            Issue(SvgIssueKind.Invalid, "gradientTransform");
            definition.Transform = SvgAffine.Identity;
        }

        SvgLength Geometry(string attribute, double defaultPercent)
        {
            if (Find(attribute, true) is not { } text) return new SvgLength(defaultPercent, SvgUnit.Percent);
            if (SvgLength.TryParse(text, out SvgLength length)) return length;
            Issue(SvgIssueKind.Invalid, attribute);
            return new SvgLength(defaultPercent, SvgUnit.Percent);
        }

        if (definition.Radial)
        {
            definition.HasFocalX = Find("fx", true) is not null;
            definition.HasFocalY = Find("fy", true) is not null;
            definition.Geometry = [Geometry("cx", 50), Geometry("cy", 50), Geometry("r", 50), Geometry("fx", 50), Geometry("fy", 50), Geometry("fr", 0)];
            if (definition.Geometry[2].Value < 0 || definition.Geometry[5].Value < 0) Issue(SvgIssueKind.Invalid, "negative gradient radius");
        }
        else
        {
            definition.Geometry = [Geometry("x1", 0), Geometry("y1", 0), Geometry("x2", 100), Geometry("y2", 0)];
        }

        SvgElement? owner = chain.Find(e => e.Children.Any(child => child.Name == "stop"));
        if (owner is not null) definition.Stops = ReadStops(owner);
        return definition;
    }

    /// <summary>The stops of a gradient, offsets clamped to 0..1 and never going back, each colour with its opacity in its alpha.</summary>
    private ImmutableArray<GradientStop> ReadStops(SvgElement gradient)
    {
        var stops = ImmutableArray.CreateBuilder<GradientStop>();
        double previous = 0;
        foreach (SvgElement stop in gradient.Children)
        {
            if (stop.Name != "stop") continue;

            double offset = 0;
            if (stop.Get("offset") is { } offsetText)
            {
                var scanner = new SvgScanner(offsetText);
                scanner.SkipWhitespace();
                if (scanner.TryNumber(out offset))
                {
                    if (scanner.Peek == '%') offset /= 100.0;
                }
                else
                {
                    Issue(SvgIssueKind.Invalid, "offset");
                }
            }
            previous = offset = Math.Max(previous, Math.Clamp(offset, 0, 1));

            // stop-color and stop-opacity are properties: attribute, sheet or style, the last one standing wins.
            var color = new Rgba32(0, 0, 0);
            Rgba32 current = new(0, 0, 0);
            double opacity = 1;
            bool currentColor = false;
            foreach (SvgDeclaration declaration in Cascade(stop))
            {
                ReadOnlySpan<char> v = declaration.Value.AsSpan().Trim();
                switch (declaration.Name)
                {
                    case "stop-color":
                        currentColor = v.Equals("currentColor", StringComparison.OrdinalIgnoreCase);
                        if (currentColor || v.Equals("inherit", StringComparison.OrdinalIgnoreCase)) break;
                        // An invalid colour is as if it were not written: what was there before stays.
                        if (SvgColor.TryParse(v, out Rgba32 parsed)) color = parsed;
                        else Issue(SvgIssueKind.Invalid, "stop-color");
                        break;
                    case "stop-opacity":
                        if (!TryOpacity(v, out opacity))
                        {
                            Issue(SvgIssueKind.Invalid, "stop-opacity");
                            opacity = 1;
                        }
                        break;
                    case "color":
                        if (SvgColor.TryParse(v, out Rgba32 own)) current = own;
                        break;
                }
            }
            if (currentColor) color = current;
            byte alpha = (byte)Math.Clamp(Math.Round(color.A * opacity, MidpointRounding.AwayFromZero), 0, 255);
            stops.Add(new GradientStop((float)offset, color with { A = alpha }));
        }
        return stops.ToImmutable();
    }
}
