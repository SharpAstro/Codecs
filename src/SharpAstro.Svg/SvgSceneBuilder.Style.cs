using SharpAstro.Vector;

namespace SharpAstro.Svg;

// The cascade: where an element's declarations come from, in what order they win, and what each property does.
internal sealed partial class SvgSceneBuilder
{
    private readonly List<SvgStyleRule> _rules = [];
    private readonly Dictionary<SvgElement, SvgDeclaration[]> _cascades = [];
    private readonly List<SvgDeclaration> _scratch = [];
    private long _selectorTests;

    private static bool Keep(string property) => Properties.Contains(property) || property == "transform";

    /// <summary>The <c>style</c> elements that apply to a screen, in document order.</summary>
    private void ReadStyleSheets()
    {
        _selectorTests = options.MaxSelectorTests;
        foreach (SvgElement sheet in tree.StyleSheets)
        {
            if (sheet.Text is not { Length: > 0 } css) continue;
            if (sheet.Get("type") is { } type && type.Trim() is not ("" or "text/css")) continue;
            if (sheet.Get("media") is { } media)
            {
                bool? applies = SvgStyleSheet.MediaApplies(media);
                if (applies is null) Issue(SvgIssueKind.Unsupported, "style sheet with a media condition");
                if (applies != true) continue;
            }
            SvgStyleSheet.Parse(css, _rules, Keep, Issue);
        }
    }

    /// <summary>
    /// The declarations that apply to an element, least important first, so that applying them in order leaves the
    /// winner: presentation attributes, then style-sheet rules by specificity and order, then the <c>style</c>
    /// attribute, then the <c>!important</c> ones of the sheets, then those of the <c>style</c> attribute.
    /// </summary>
    private IReadOnlyList<SvgDeclaration> Cascade(SvgElement element)
    {
        if (_rules.Count > 0 && _cascades.TryGetValue(element, out SvgDeclaration[]? cached)) return cached;

        _scratch.Clear();
        foreach (KeyValuePair<string, string> attribute in element.Attributes)
        {
            if (Properties.Contains(attribute.Key)) _scratch.Add(new SvgDeclaration(attribute.Key, attribute.Value, false));
        }
        int attributes = _scratch.Count;
        if (element.Get("style") is { } css) SvgStyleSheet.ParseDeclarations(css, _scratch, Keep, stripComments: true);

        if (_rules.Count == 0)
        {
            // No sheets: attributes, then the style attribute with its important declarations last.
            if (_scratch.Exists(d => d.Important))
            {
                SvgDeclaration[] ordered = [.. _scratch.Take(attributes), .. _scratch.Skip(attributes).Where(d => !d.Important), .. _scratch.Where(d => d.Important)];
                _scratch.Clear();
                _scratch.AddRange(ordered);
            }
            return _scratch;
        }

        var entries = new List<(int Level, int Specificity, int Order, SvgDeclaration Declaration)>(_scratch.Count);
        for (int i = 0; i < _scratch.Count; i++)
        {
            SvgDeclaration d = _scratch[i];
            entries.Add((i < attributes ? 0 : d.Important ? 4 : 2, 0, i, d));
        }
        foreach (SvgStyleRule rule in _rules)
        {
            bool matches = rule.Matches(element, ref _selectorTests);
            if (_selectorTests < 0)
            {
                throw new InvalidDataException($"The SVG's style sheets cost more than {options.MaxSelectorTests} selector tests to apply.");
            }
            if (!matches) continue;
            for (int i = 0; i < rule.Declarations.Length; i++)
            {
                SvgDeclaration d = rule.Declarations[i];
                entries.Add((d.Important ? 3 : 1, rule.Specificity, rule.Order * 4096 + Math.Min(i, 4095), d));
            }
        }
        entries.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level)
            : a.Specificity != b.Specificity ? a.Specificity.CompareTo(b.Specificity)
            : a.Order.CompareTo(b.Order));

        SvgDeclaration[] result = [.. entries.Select(entry => entry.Declaration)];
        _cascades[element] = result;
        return result;
    }

    private SvgStyle ComputeStyle(SvgElement element, in SvgStyle parent, in SvgViewport viewport)
    {
        SvgStyle style = parent;
        style.Opacity = 1;
        style.Display = true;
        style.OverflowVisible = null;

        IReadOnlyList<SvgDeclaration> declarations = Cascade(element);
        if (declarations.Count == 0) return style;

        // font-size before the rest: em lengths on this element are in its own font size.
        for (int i = 0; i < declarations.Count; i++)
        {
            if (declarations[i].Name != "font-size") continue;
            ReadOnlySpan<char> v = declarations[i].Value.AsSpan().Trim();
            if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase)) style.FontSize = parent.FontSize;
            else if (SvgLength.TryParse(v, out SvgLength size) && size.Value >= 0) style.FontSize = size.Resolve(parent.FontSize, parent.FontSize);
        }
        for (int i = 0; i < declarations.Count; i++)
        {
            if (declarations[i].Name != "font-size") Apply(ref style, parent, viewport, declarations[i].Name, declarations[i].Value);
        }
        return style;
    }

    private void Apply(ref SvgStyle s, in SvgStyle parent, in SvgViewport viewport, string name, string raw)
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
                if (SvgLength.TryParse(v, out SvgLength width) && width.Value >= 0) s.StrokeWidth = width.Resolve(s.FontSize, viewport.Diagonal);
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
                else if (TryDashes(v, s.FontSize, viewport, out double[]? dashes)) s.Dashes = dashes;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "stroke-dashoffset":
                if (SvgLength.TryParse(v, out SvgLength offset)) s.DashOffset = offset.Resolve(s.FontSize, viewport.Diagonal);
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
            case "overflow":
                if (v is "visible" or "auto") s.OverflowVisible = true;
                else if (v is "hidden" or "scroll" or "clip") s.OverflowVisible = false;
                else Issue(SvgIssueKind.Invalid, name);
                break;

            // Read far enough to know the scene is missing something.
            case "clip-path" or "mask" or "filter" or "marker" or "marker-start" or "marker-mid" or "marker-end" or "vector-effect":
                if (v is not "none") Issue(SvgIssueKind.Unsupported, name);
                break;
            case "mix-blend-mode" or "paint-order":
                if (v is not "normal") Issue(SvgIssueKind.Unsupported, name);
                break;
            case "transform":
                Issue(SvgIssueKind.Unsupported, "transform in a style attribute or sheet");
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
            case "overflow": s.OverflowVisible = parent.OverflowVisible; break;
        }
    }

    /// <summary>
    /// A paint: <c>none</c>, a colour, <c>currentColor</c>, or <c>url(#id)</c> with an optional fallback after it. A
    /// reference that cannot be used (no such element, not a gradient, another document) is reported and its fallback
    /// stands in, or nothing.
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
        if (v is "context-fill" or "context-stroke")
        {
            Issue(SvgIssueKind.Unsupported, v.ToString());
            return true;
        }
        if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            if (!SvgColor.TryParse(v, out Rgba32 color)) return false;
            paint = new SvgPaint(SvgPaintKind.Color, color);
            return true;
        }

        int close = v.IndexOf(')');
        if (close < 0) return false;
        ReadOnlySpan<char> reference = v[4..close].Trim().Trim("\"'").Trim();
        ReadOnlySpan<char> fallbackText = v[(close + 1)..].Trim();
        var fallback = new SvgPaint(SvgPaintKind.None, default);
        if (fallbackText.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) fallback = new SvgPaint(SvgPaintKind.CurrentColor, default);
        else if (SvgColor.TryParse(fallbackText, out Rgba32 fallbackColor)) fallback = new SvgPaint(SvgPaintKind.Color, fallbackColor);
        else if (!fallbackText.IsEmpty && fallbackText is not "none") return false;

        paint = fallback;
        if (reference.IsEmpty || reference[0] != '#')
        {
            Issue(SvgIssueKind.Unsupported, "paint server in another document");
        }
        else if (!tree.Ids.TryGetValue(reference[1..].ToString(), out SvgElement? server))
        {
            Issue(SvgIssueKind.Invalid, "paint: no element with that id");
        }
        else if (server.Name is "linearGradient" or "radialGradient")
        {
            paint = new SvgPaint(SvgPaintKind.Server, fallback.Color, server, fallback.Kind);
        }
        else if (server.Name == "pattern")
        {
            Issue(SvgIssueKind.Unsupported, "pattern");
        }
        else
        {
            Issue(SvgIssueKind.Invalid, "paint: not a paint server");
        }
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

    private static bool TryDashes(ReadOnlySpan<char> v, double fontSize, in SvgViewport viewport, out double[]? dashes)
    {
        dashes = null;
        var values = new List<double>();
        var scanner = new SvgScanner(v);
        scanner.SkipWhitespace();
        while (!scanner.AtEnd)
        {
            if (!SvgLength.TryScan(ref scanner, out SvgLength length) || length.Value < 0) return false;
            values.Add(length.Resolve(fontSize, viewport.Diagonal));
            scanner.SkipCommaWhitespace();
        }
        if (values.Count == 0) return false;
        dashes = [.. values];
        return true;
    }
}
