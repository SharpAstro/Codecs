using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

// The cascade: where an element's declarations come from, in what order they win, and what each property does.
internal sealed partial class SvgSceneBuilder
{
    private readonly List<SvgStyleRule> _rules = [];
    private readonly List<SvgDeclaration> _scratch = [];
    private long _selectorTests;

    private static bool Keep(string property) => Properties.Contains(property) || property == "transform" || IsCustom(property) || IsGeometry(property);

    /// <summary>
    /// The geometry SVG 2 lets CSS set. It is read from attributes only, so in a sheet or a <c>style</c> attribute it
    /// is kept just far enough to be reported: a browser would draw the shape somewhere else.
    /// </summary>
    private static bool IsGeometry(string property) => property is "x" or "y" or "width" or "height" or "cx" or "cy" or "r" or "rx" or "ry";

    private static bool IsCustom(string property) => property.Length > 2 && property.StartsWith("--", StringComparison.Ordinal);

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
        if (element.Cascade is { } cached) return cached;

        _scratch.Clear();
        foreach (KeyValuePair<string, string> attribute in element.Attributes)
        {
            // The marker shorthand is a property of style sheets only: as an attribute a browser does not read it.
            if (Properties.Contains(attribute.Key) && attribute.Key != "marker") _scratch.Add(new SvgDeclaration(attribute.Key, attribute.Value, false));
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
        element.Cascade = result;
        return result;
    }

    private SvgStyle ComputeStyle(SvgElement element, in SvgStyle parent, in SvgViewport viewport)
    {
        SvgStyle style = parent;
        style.Opacity = 1;
        style.Display = true;
        style.OverflowVisible = null;
        style.ClipPath = null;
        style.Mask = null;
        style.AlignmentBaseline = null;
        style.OwnLines = TextDecorations.None;

        IReadOnlyList<SvgDeclaration> declarations = Cascade(element);
        if (declarations.Count == 0) return style;

        // Custom properties before everything: a var() on this element finds the ones declared on it, wherever they stand.
        for (int i = 0; i < declarations.Count; i++)
        {
            if (!IsCustom(declarations[i].Name)) continue;
            style.Variables = DeclareVariables(declarations, parent.Variables);
            break;
        }

        // font-size before the rest: em lengths on this element are in its own font size.
        for (int i = 0; i < declarations.Count; i++)
        {
            // The font shorthand sets the size too, and the later of the two stands.
            bool shorthand = declarations[i].Name == "font";
            if (declarations[i].Name != "font-size" && !shorthand) continue;
            string raw = declarations[i].Value;
            if (HasVar(raw) && !TrySubstitute(raw, style.Variables, null, 0, out raw))
            {
                style.FontSize = parent.FontSize;
                continue;
            }
            ReadOnlySpan<char> v = raw.AsSpan().Trim();
            if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase)) style.FontSize = parent.FontSize;
            else if (shorthand) TryFontShorthand(v, ref style, parent, sizeOnly: true);
            else if (TryFontSize(v, parent.FontSize, out double size)) style.FontSize = size;
        }
        for (int i = 0; i < declarations.Count; i++)
        {
            string name = declarations[i].Name;
            if (name != "font-size" && !IsCustom(name)) Apply(ref style, parent, viewport, name, declarations[i].Value);
        }
        // Lines are drawn through everything in the element, in the element's own paints: kept with its style as it
        // now is, on top of the lines it is already under.
        if (style.OwnLines != TextDecorations.None)
        {
            style.Lines = [.. parent.Lines ?? [], new SvgTextLines(style.OwnLines, style)];
        }
        return style;
    }

    // ---- Custom properties and var() ----------------------------------------------------------------------------

    private static bool HasVar(string value) => value.Contains("var(", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The custom properties in force on an element that declares some: its parent's, with its own on top. A
    /// declaration's own <c>var()</c> is replaced here, against the element's own set (so two of them may refer to each
    /// other in either order); one that cannot be, or that leads back to itself, is as if it were not declared at all,
    /// not even by an ancestor.
    /// </summary>
    private Dictionary<string, string>? DeclareVariables(IReadOnlyList<SvgDeclaration> declarations, Dictionary<string, string>? inherited)
    {
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SvgDeclaration declaration in declarations)
        {
            if (IsCustom(declaration.Name)) own[declaration.Name] = declaration.Value;       // least important first: the last one stands
        }
        var result = inherited is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(inherited, StringComparer.Ordinal);
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in own.Keys.ToArray()) Resolve(name);
        return result.Count == 0 ? null : result;

        // The value of a name as this element sees it, or null when it has none. An own one is worked out on first use.
        string? Resolve(string name)
        {
            if (!own.Remove(name, out string? raw)) return open.Contains(name) ? null : result.GetValueOrDefault(name);
            open.Add(name);
            bool ok = !HasVar(raw) || TrySubstitute(raw, null, Resolve, 0, out raw);
            open.Remove(name);
            if (ok) result[name] = raw;
            else result.Remove(name);
            return ok ? raw : null;
        }
    }

    /// <summary>
    /// Replaces every <c>var(--name)</c> and <c>var(--name, fallback)</c> in a value. False when a name has no value and
    /// no fallback, or the text is not a <c>var()</c> at all: the declaration is then one that cannot be used.
    /// </summary>
    private bool TrySubstitute(string value, Dictionary<string, string>? variables, Func<string, string?>? lookup, int depth, out string result)
    {
        result = value;
        if (depth > 16) return false;
        var text = new System.Text.StringBuilder(value.Length);
        int from = 0;
        while (true)
        {
            int at = value.IndexOf("var(", from, StringComparison.OrdinalIgnoreCase);
            // Part of a longer name is not the function.
            while (at > 0 && (char.IsAsciiLetterOrDigit(value[at - 1]) || value[at - 1] is '-' or '_'))
            {
                at = value.IndexOf("var(", at + 4, StringComparison.OrdinalIgnoreCase);
            }
            if (at < 0) break;
            text.Append(value, from, at - from);

            int close = at + 4, nesting = 1, comma = -1;
            for (; close < value.Length && nesting > 0; close++)
            {
                char c = value[close];
                if (c == '(') nesting++;
                else if (c == ')') nesting--;
                else if (c == ',' && nesting == 1 && comma < 0) comma = close;
            }
            if (nesting != 0) return false;
            int end = close - 1;                                                             // the closing parenthesis
            string name = value[(at + 4)..(comma < 0 ? end : comma)].Trim();
            if (!IsCustom(name)) return false;

            string? found = lookup is not null ? lookup(name) : variables?.GetValueOrDefault(name);
            if (found is null)
            {
                if (comma < 0) return false;
                found = value[(comma + 1)..end];
                if (HasVar(found) && !TrySubstitute(found, variables, lookup, depth + 1, out found)) return false;
            }
            if ((_substitution -= found.Length) < 0)
            {
                throw new InvalidDataException($"The SVG's var() uses put more than {options.MaxCharacters} characters into its properties.");
            }
            text.Append(found);
            from = close;
        }
        text.Append(value, from, value.Length - from);
        result = text.ToString();
        return true;
    }

    /// <summary>
    /// What a property is when its declaration turned out to be unusable only once its <c>var()</c> was looked up: the
    /// inherited value for a property that inherits, the initial one for the rest. Not "as if it were not written":
    /// the declarations it won over stay beaten.
    /// </summary>
    private static void Unset(ref SvgStyle s, in SvgStyle parent, string name)
    {
        switch (name)
        {
            case "opacity": s.Opacity = 1; break;
            case "display": s.Display = true; break;
            case "overflow": s.OverflowVisible = null; break;
            case "clip-path": s.ClipPath = null; break;
            case "mask": s.Mask = null; break;
            default: CopyFromParent(ref s, parent, name); break;
        }
    }

    private void Apply(ref SvgStyle s, in SvgStyle parent, in SvgViewport viewport, string name, string raw)
    {
        if (HasVar(raw) && !TrySubstitute(raw, s.Variables, null, 0, out raw))
        {
            Issue(SvgIssueKind.Invalid, "var() with no value and no fallback");
            Unset(ref s, parent, name);
            return;
        }
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

            case "clip-path":
                s.ClipPath = v is "none" ? null : Reference(v, name, "clipPath");
                break;
            case "mask":
                s.Mask = v is "none" ? null : Reference(v, name, "mask");
                break;
            case "clip-rule":
                if (v is "nonzero") s.ClipRule = FillRule.NonZero;
                else if (v is "evenodd") s.ClipRule = FillRule.EvenOdd;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "marker-start":
                s.MarkerStart = v is "none" ? null : Reference(v, name, "marker");
                break;
            case "marker-mid":
                s.MarkerMid = v is "none" ? null : Reference(v, name, "marker");
                break;
            case "marker-end":
                s.MarkerEnd = v is "none" ? null : Reference(v, name, "marker");
                break;
            case "marker":
                s.MarkerStart = s.MarkerMid = s.MarkerEnd = v is "none" ? null : Reference(v, name, "marker");
                break;
            case "paint-order":
                if (TryPaintOrder(v, out byte order)) s.PaintOrder = order; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "mask-type":
                // Read where the mask is used: it is a property of the mask element alone.
                break;

            // Read far enough to know the scene is missing something.
            case "filter" or "vector-effect":
                if (v is not "none") Issue(SvgIssueKind.Unsupported, name);
                break;
            case "mix-blend-mode":
                if (v is not "normal") Issue(SvgIssueKind.Unsupported, name);
                break;
            case "transform":
                Issue(SvgIssueKind.Unsupported, "transform in a style attribute or sheet");
                break;
            case "transform-origin":
                Issue(SvgIssueKind.Unsupported, name);
                break;

            // ---- Text ----
            case "font":
                // Its size was taken before everything else; here the rest of it.
                if (!TryFontShorthand(v, ref s, parent, sizeOnly: false))
                {
                    // One of the system fonts (caption, menu and so on) is a font of the machine's, which a scene cannot name.
                    Issue(v.ContainsAny(" \t") ? SvgIssueKind.Invalid : SvgIssueKind.Unsupported, name);
                }
                break;
            case "font-family":
                s.FontFamilies = FontFamilies(v);
                break;
            case "font-weight":
                if (TryFontWeight(v, parent.FontWeight, out int weight)) s.FontWeight = weight; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "font-style":
                if (TryFontSlant(v, out FontSlant slant)) s.FontSlant = slant; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "font-stretch":
                if (TryFontStretch(v, out double stretch)) s.FontStretch = stretch; else Issue(SvgIssueKind.Invalid, name);
                break;
            case "font-variant":
                if (v is not "normal") Issue(SvgIssueKind.Unsupported, name);
                break;
            case "text-anchor":
                if (v is "start") s.TextAnchor = TextAnchor.Start;
                else if (v is "middle") s.TextAnchor = TextAnchor.Middle;
                else if (v is "end") s.TextAnchor = TextAnchor.End;
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "letter-spacing":
                if (v is "normal") s.LetterSpacing = 0;
                else if (SvgLength.TryParse(v, out SvgLength letter) && letter.Unit != SvgUnit.Percent) s.LetterSpacing = letter.Resolve(s.FontSize, 0);
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "word-spacing":
                if (v is "normal") s.WordSpacing = 0;
                else if (SvgLength.TryParse(v, out SvgLength word) && word.Unit != SvgUnit.Percent) s.WordSpacing = word.Resolve(s.FontSize, 0);
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "text-decoration" or "text-decoration-line":
            {
                // An element's lines are drawn through everything inside it, so they add to the lines it is already
                // under: "none" on a tspan does not take its text's underline away.
                TextDecorations lines = TextDecorations.None;
                bool more = false;
                foreach (Range range in v.SplitAny(' ', '\t', '\n', '\r'))
                {
                    ReadOnlySpan<char> word = v[range];
                    if (word is "underline") lines |= TextDecorations.Underline;
                    else if (word is "overline") lines |= TextDecorations.Overline;
                    else if (word is "line-through") lines |= TextDecorations.LineThrough;
                    else if (!word.IsEmpty && word is not "none") more = true;
                }
                // A colour, a style or a thickness of the line: the line is drawn in the text's own paint, solid.
                if (more) Issue(SvgIssueKind.Unsupported, "text-decoration with a colour, a style or a thickness");
                s.OwnLines = lines;
                break;
            }
            case "dominant-baseline":
                // "auto" inside text with another baseline keeps that baseline: it does not go back to the alphabetic
                // one (measured in a browser).
                if (TryBaseline(v, out TextBaseline? dominant)) s.Baseline = dominant ?? parent.Baseline;
                else Issue(SvgIssueKind.Unsupported, $"{name}: {v}");
                break;
            case "alignment-baseline":
                if (TryBaseline(v, out TextBaseline? alignment)) s.AlignmentBaseline = alignment;
                else Issue(SvgIssueKind.Unsupported, $"{name}: {v}");
                break;
            case "baseline-shift":
                (s.BaselineShift, s.ScriptRise) = (parent.BaselineShift, parent.ScriptRise);
                if (v is "baseline") { }
                // How far a superscript is raised is the font's to say: the scene carries the size it is to be said for.
                else if (v is "super") s.ScriptRise += s.FontSize;
                else if (v is "sub") s.ScriptRise -= s.FontSize;
                else if (SvgLength.TryParse(v, out SvgLength shift)) s.BaselineShift += shift.Resolve(s.FontSize, s.FontSize);
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "writing-mode":
                if (v is not ("horizontal-tb" or "lr" or "lr-tb" or "rl" or "rl-tb")) Issue(SvgIssueKind.Unsupported, "vertical text");
                break;
            case "direction":
                if (v is not "ltr") Issue(SvgIssueKind.Unsupported, "text from right to left");
                break;
            case "white-space":
                if (v is "normal" or "nowrap") s.PreserveWhiteSpace = false;
                else if (v is "pre" or "pre-wrap" or "break-spaces") s.PreserveWhiteSpace = true;
                else if (v is "pre-line") Issue(SvgIssueKind.Unsupported, "white-space: pre-line");
                else Issue(SvgIssueKind.Invalid, name);
                break;
            case "inline-size":
                if (v is not ("auto" or "0")) Issue(SvgIssueKind.Unsupported, "text wrapped to a width (inline-size)");
                break;
            case "x" or "y" or "width" or "height" or "cx" or "cy" or "r" or "rx" or "ry":
                // Only a sheet or a style attribute brings these here; the attributes are read where the shape is made.
                if (v is not "auto") Issue(SvgIssueKind.Unsupported, $"{name} as a CSS property");
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
            case "clip-path": s.ClipPath = parent.ClipPath; break;
            case "mask": s.Mask = parent.Mask; break;
            case "clip-rule": s.ClipRule = parent.ClipRule; break;
            case "marker-start": s.MarkerStart = parent.MarkerStart; break;
            case "marker-mid": s.MarkerMid = parent.MarkerMid; break;
            case "marker-end": s.MarkerEnd = parent.MarkerEnd; break;
            case "marker": (s.MarkerStart, s.MarkerMid, s.MarkerEnd) = (parent.MarkerStart, parent.MarkerMid, parent.MarkerEnd); break;
            case "paint-order": s.PaintOrder = parent.PaintOrder; break;
            case "font":
                (s.FontFamilies, s.FontWeight, s.FontSlant, s.FontStretch) = (parent.FontFamilies, parent.FontWeight, parent.FontSlant, parent.FontStretch);
                break;
            case "font-family": s.FontFamilies = parent.FontFamilies; break;
            case "font-weight": s.FontWeight = parent.FontWeight; break;
            case "font-style": s.FontSlant = parent.FontSlant; break;
            case "font-stretch": s.FontStretch = parent.FontStretch; break;
            case "text-anchor": s.TextAnchor = parent.TextAnchor; break;
            case "letter-spacing": s.LetterSpacing = parent.LetterSpacing; break;
            case "word-spacing": s.WordSpacing = parent.WordSpacing; break;
            case "text-decoration" or "text-decoration-line": s.OwnLines = TextDecorations.None; break;
            case "dominant-baseline": s.Baseline = parent.Baseline; break;
            case "alignment-baseline": s.AlignmentBaseline = null; break;
            case "baseline-shift": (s.BaselineShift, s.ScriptRise) = (parent.BaselineShift, parent.ScriptRise); break;
            case "white-space": s.PreserveWhiteSpace = parent.PreserveWhiteSpace; break;
        }
    }

    /// <summary>A baseline keyword. Null is <c>auto</c> (and <c>baseline</c>): whatever is inherited, or the alphabetic one. False for a keyword this reader does not act on.</summary>
    private static bool TryBaseline(ReadOnlySpan<char> v, out TextBaseline? baseline)
    {
        baseline = v switch
        {
            "alphabetic" => TextBaseline.Alphabetic,
            "middle" => TextBaseline.Middle,
            "central" => TextBaseline.Central,
            "hanging" => TextBaseline.Hanging,
            "mathematical" => TextBaseline.Mathematical,
            "ideographic" => TextBaseline.Ideographic,
            "text-before-edge" or "text-top" => TextBaseline.TextTop,
            "text-after-edge" or "text-bottom" => TextBaseline.TextBottom,
            _ => null,
        };
        return baseline is not null || v is "auto" or "baseline";
    }

    /// <summary>
    /// A <c>url(#id)</c> that must name an element of one kind. Anything else is reported and is as if the property
    /// were <c>none</c>: a reference that leads nowhere does not hide the element.
    /// </summary>
    private SvgElement? Reference(ReadOnlySpan<char> v, string property, string kind)
    {
        if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || v[^1] != ')')
        {
            Issue(SvgIssueKind.Unsupported, $"{property}: a value that is not a reference");
            return null;
        }
        ReadOnlySpan<char> reference = v[4..^1].Trim().Trim("\"'").Trim();
        if (reference.IsEmpty || reference[0] != '#')
        {
            Issue(SvgIssueKind.Unsupported, $"{property}: an element in another document");
            return null;
        }
        if (!tree.Ids.TryGetValue(reference[1..].ToString(), out SvgElement? element))
        {
            Issue(SvgIssueKind.Invalid, $"{property}: no element with that id");
            return null;
        }
        if (element.Name != kind)
        {
            Issue(SvgIssueKind.Invalid, $"{property}: not a {kind} element");
            return null;
        }
        return element;
    }

    /// <summary>The first, second and third thing painted, two bits each: 0 the fill, 1 the stroke, 2 the markers.</summary>
    internal const byte NormalPaintOrder = 0 | 1 << 2 | 2 << 4;

    /// <summary><c>normal</c>, or one to three of <c>fill</c>, <c>stroke</c> and <c>markers</c>: those first, in that order, then the rest in the usual one.</summary>
    private static bool TryPaintOrder(ReadOnlySpan<char> v, out byte order)
    {
        order = NormalPaintOrder;
        if (v is "normal") return true;
        Span<int> slots = stackalloc int[3];
        int count = 0, seen = 0;
        foreach (Range range in v.SplitAny(' ', '\t', '\n', '\r'))
        {
            ReadOnlySpan<char> word = v[range];
            if (word.IsEmpty) continue;
            int which = word is "fill" ? 0 : word is "stroke" ? 1 : word is "markers" ? 2 : -1;
            if (which < 0 || (seen & 1 << which) != 0) return false;
            seen |= 1 << which;
            slots[count++] = which;
        }
        if (count == 0) return false;
        for (int which = 0; which < 3; which++)
        {
            if ((seen & 1 << which) == 0) slots[count++] = which;
        }
        order = (byte)(slots[0] | slots[1] << 2 | slots[2] << 4);
        return true;
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
        else if (server.Name is "linearGradient" or "radialGradient" or "pattern")
        {
            paint = new SvgPaint(SvgPaintKind.Server, fallback.Color, server, fallback.Kind);
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
