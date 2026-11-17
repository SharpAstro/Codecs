using System.Collections.Immutable;
using System.Text;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

// Text: what a text element writes, in which font, and where. Nothing here knows a font: the text is not laid out,
// it is described, and whoever draws the scene lays it out with the text code it has.
internal sealed partial class SvgSceneBuilder
{
    /// <summary>A character of a <c>text</c> element, before and after white space is handled.</summary>
    private struct TextCharacter
    {
        /// <summary>The code point: a tab or a line end has already become a space.</summary>
        public int Rune;

        /// <summary>Which piece of character data it is in.</summary>
        public int Piece;

        /// <summary>Whether it is still there once white space is collapsed and trimmed. Only these can be positioned.</summary>
        public bool Kept;

        /// <summary>A space that may be collapsed (its element does not preserve white space).</summary>
        public bool Collapsible;
    }

    /// <summary>One piece of character data: the characters between two tags, with the style of the element they stand in.</summary>
    private readonly record struct TextPiece(int Style, VectorTextPath? Path, int Start, int End);

    /// <summary>A <c>text</c>, <c>tspan</c> or <c>a</c> element, and the characters in it and in everything inside it.</summary>
    private struct TextRange
    {
        public SvgElement Element;
        public int Start, End, Style;
    }

    /// <summary>What is being gathered for one <c>text</c> element.</summary>
    private sealed class TextGathering
    {
        public readonly List<TextCharacter> Characters = [];
        public readonly List<TextPiece> Pieces = [];
        public readonly List<TextRange> Ranges = [];
        public readonly List<SvgStyle> Styles = [];
    }

    private long _textCharacters;

    /// <summary>
    /// A <c>text</c> element: every piece of character data in it, in order, as a span with the style of the element
    /// it stands in, and whatever <c>x</c>, <c>y</c>, <c>dx</c>, <c>dy</c> and <c>rotate</c> say about single characters.
    /// </summary>
    private void Text(SvgElement element, in SvgStyle parent, SvgAffine outer, in SvgViewport viewport, List<VectorNode> output, int depth)
    {
        if (!Enter(element, parent, outer, viewport, false, out SvgStyle style, out SvgAffine matrix)) return;

        var text = new TextGathering();
        _paintDepth = depth;
        Gather(text, element, style, viewport, null, depth);
        CollapseWhiteSpace(text);

        int kept = 0;
        foreach (TextCharacter character in text.Characters) kept += character.Kept ? 1 : 0;
        if (kept == 0) return;
        if ((_textCharacters += kept) > options.MaxTextCharacters)
        {
            throw new InvalidDataException($"The SVG's text holds more than {options.MaxTextCharacters} characters once its use elements are followed.");
        }

        // What the elements say about single characters, outermost first, so that an inner element's word is the last.
        int count = text.Characters.Count;
        float?[]? xs = null, ys = null, turns = null;
        float[]? dxs = null, dys = null;
        foreach (TextRange range in text.Ranges)
        {
            // Only text and tspan place characters: a textPath and a link have no such attributes, and one written
            // on them is not read (measured in a browser).
            if (range.Element.Name is not ("text" or "tspan")) continue;
            SvgStyle own = text.Styles[range.Style];
            double fontSize = own.FontSize;
            double[]? x = LengthList(range.Element, "x", fontSize, viewport.Width);
            double[]? y = LengthList(range.Element, "y", fontSize, viewport.Height);
            double[]? dx = LengthList(range.Element, "dx", fontSize, viewport.Width);
            double[]? dy = LengthList(range.Element, "dy", fontSize, viewport.Height);
            double[]? rotate = NumberList(range.Element, "rotate");
            if (x is null && y is null && dx is null && dy is null && rotate is null) continue;

            int i = 0;
            for (int j = range.Start; j < range.End; j++)
            {
                if (!text.Characters[j].Kept) continue;
                if (x is not null && i < x.Length) (xs ??= new float?[count])[j] = (float)x[i];
                if (y is not null && i < y.Length) (ys ??= new float?[count])[j] = (float)y[i];
                if (dx is not null && i < dx.Length) (dxs ??= new float[count])[j] = (float)dx[i];
                if (dy is not null && i < dy.Length) (dys ??= new float[count])[j] = (float)dy[i];
                // The last angle of a list stands for every character after it in the element.
                if (rotate is not null) (turns ??= new float?[count])[j] = (float)rotate[Math.Min(i, rotate.Length - 1)];
                i++;
            }
        }

        var spans = ImmutableArray.CreateBuilder<VectorTextSpan>(text.Pieces.Count);
        var characters = new StringBuilder();
        var positions = ImmutableArray.CreateBuilder<VectorTextPosition>();
        foreach (TextPiece piece in text.Pieces)
        {
            characters.Clear();
            positions.Clear();
            for (int j = piece.Start; j < piece.End; j++)
            {
                TextCharacter character = text.Characters[j];
                if (!character.Kept) continue;
                // Along a path a character has no y of its own, whichever element gave it one: the text element's y is
                // for its first character, which may be the first on the path (measured in a browser).
                float? x = xs?[j], y = piece.Path is null ? ys?[j] : null;
                float dx = dxs?[j] ?? 0, dy = dys?[j] ?? 0, turn = turns?[j] ?? 0;
                if (x is not null || y is not null || dx != 0 || dy != 0 || turn != 0)
                {
                    positions.Add(new VectorTextPosition(characters.Length, x, y, dx, dy, turn));
                }
                characters.Append(new Rune(character.Rune).ToString());
            }
            if (characters.Length == 0) continue;
            spans.Add(Span(text.Styles[piece.Style], viewport, characters.ToString(), positions.ToImmutable(), piece.Path));
        }
        if (spans.Count == 0) return;

        float? textLength = null;
        var adjust = TextLengthAdjust.Spacing;
        if (element.Get("textLength") is { } lengthText)
        {
            if (SvgLength.TryParse(lengthText, out SvgLength length) && length.Value >= 0)
            {
                textLength = (float)length.Resolve(style.FontSize, viewport.Width);
            }
            else
            {
                Issue(SvgIssueKind.Invalid, "textLength");
            }
            if (element.Get("lengthAdjust") is { } adjustText)
            {
                if (adjustText.AsSpan().Trim() is "spacingAndGlyphs") adjust = TextLengthAdjust.SpacingAndGlyphs;
                else if (adjustText.AsSpan().Trim() is not "spacing") Issue(SvgIssueKind.Invalid, "lengthAdjust");
            }
        }

        // The element's own opacity, clip and mask are a layer's, as for any element. A clip or a mask in the units of
        // the text's bounding box needs the box, which only laid-out text has: EndLayer reports that.
        List<VectorNode> target = BeginLayer(style, output);
        target.Add(new VectorText
        {
            Id = element.Get("id"), Spans = spans.ToImmutable(), Transform = matrix.ToMatrix(), TextLength = textLength, LengthAdjust = adjust,
        });
        EndLayer(target, output, style, null, matrix, viewport, depth);
    }

    /// <summary>The character data of an element and of the text content elements in it, in document order.</summary>
    private void Gather(TextGathering text, SvgElement element, in SvgStyle style, in SvgViewport viewport, VectorTextPath? path, int depth)
    {
        int styleIndex = text.Styles.Count;
        text.Styles.Add(style);
        int rangeIndex = text.Ranges.Count;
        text.Ranges.Add(new TextRange { Element = element, Start = text.Characters.Count, Style = styleIndex });
        // A length asked for is read on the text element. On a part of it, it would stretch that part alone.
        if (element.Name != "text" && element.Get("textLength") is not null) Issue(SvgIssueKind.Unsupported, "textLength on a part of a text");
        bool preserve = style.PreserveWhiteSpace ?? PreservesSpace(element);

        if (element.Extras?.Content is { } content)
        {
            foreach (object item in content)
            {
                if (item is string data)
                {
                    int start = text.Characters.Count;
                    foreach (Rune rune in data.EnumerateRunes())
                    {
                        // A tab and a line end are a space, whether or not white space is preserved (measured in a browser).
                        bool space = rune.Value is ' ' or '\t' or '\n' or '\r';
                        text.Characters.Add(new TextCharacter { Rune = space ? ' ' : rune.Value, Piece = text.Pieces.Count, Kept = true, Collapsible = space && !preserve });
                    }
                    text.Pieces.Add(new TextPiece(styleIndex, path, start, text.Characters.Count));
                    continue;
                }

                var child = (SvgElement)item;
                Charge(depth + 1);
                switch (child.Name)
                {
                    case "tspan" or "a":
                    {
                        SvgStyle childStyle = ComputeStyle(child, style, viewport);
                        // What is not displayed is not there at all: its characters are not counted by x, y and the rest.
                        if (!childStyle.Display) break;
                        FoldOpacity(ref childStyle);
                        Gather(text, child, childStyle, viewport, path, depth + 1);
                        break;
                    }
                    case "textPath":
                    {
                        SvgStyle childStyle = ComputeStyle(child, style, viewport);
                        if (!childStyle.Display) break;
                        if (path is not null || element.Name is not ("text" or "a"))
                        {
                            Issue(SvgIssueKind.Invalid, "textPath inside a tspan or another textPath");
                            break;
                        }
                        // A textPath that names no path is in error, and what is in it is not drawn.
                        if (TextPath(child, childStyle, viewport) is not { } along) break;
                        FoldOpacity(ref childStyle);
                        Gather(text, child, childStyle, viewport, along, depth + 1);
                        break;
                    }
                    case "title" or "desc" or "metadata" or "style" or "script" or "animate" or "animateTransform" or "animateMotion" or "set":
                        break;
                    default:
                        Issue(SvgIssueKind.Unsupported, $"<{child.Name}> element in text");
                        break;
                }
            }
        }

        TextRange range = text.Ranges[rangeIndex];
        range.End = text.Characters.Count;
        text.Ranges[rangeIndex] = range;
    }

    /// <summary>
    /// The opacity of an element inside text, multiplied into its paints. Exact for one paint; with a fill and a stroke the
    /// two would have to be faded as one where they overlap, which spans cannot say, so that case is reported.
    /// </summary>
    private void FoldOpacity(ref SvgStyle style)
    {
        if (style.Opacity >= 1) return;
        if (style.Fill.Kind != SvgPaintKind.None && style.Stroke.Kind != SvgPaintKind.None)
        {
            Issue(SvgIssueKind.Unsupported, "opacity on a part of a text that has a fill and a stroke");
        }
        style.FillOpacity *= style.Opacity;
        style.StrokeOpacity *= style.Opacity;
        style.Opacity = 1;
    }

    /// <summary>Whether <c>xml:space="preserve"</c> is in force on an element: its own attribute, or the nearest ancestor's.</summary>
    private static bool PreservesSpace(SvgElement element)
    {
        for (SvgElement? e = element; e is not null; e = e.Parent)
        {
            if (e.Get("xml:space") is { } space) return space == "preserve";
        }
        return false;
    }

    /// <summary>
    /// White space, as CSS collapses it on one line: of a run of collapsible spaces the first stays, and those at the
    /// very start and the very end of the text go. A preserved space is never dropped and does not make a neighbour drop.
    /// </summary>
    private static void CollapseWhiteSpace(TextGathering text)
    {
        List<TextCharacter> characters = text.Characters;
        bool afterSpace = true;                 // the start of the text counts as after a space: leading spaces go
        for (int i = 0; i < characters.Count; i++)
        {
            TextCharacter character = characters[i];
            if (character.Collapsible)
            {
                if (afterSpace)
                {
                    character.Kept = false;
                    characters[i] = character;
                }
                afterSpace = true;
            }
            else
            {
                afterSpace = false;
            }
        }
        for (int i = characters.Count - 1; i >= 0; i--)
        {
            TextCharacter character = characters[i];
            if (!character.Kept) continue;
            if (!character.Collapsible) break;
            character.Kept = false;
            characters[i] = character;
        }
    }

    /// <summary>A list of lengths (<c>x</c>, <c>y</c>, <c>dx</c>, <c>dy</c>), or null when the attribute is absent, empty or in error.</summary>
    private double[]? LengthList(SvgElement element, string attribute, double fontSize, double percentBase)
    {
        if (element.Get(attribute) is not { } text) return null;
        var values = new List<double>();
        var scanner = new SvgScanner(text);
        scanner.SkipWhitespace();
        while (!scanner.AtEnd)
        {
            if (!SvgLength.TryScan(ref scanner, out SvgLength length))
            {
                Issue(SvgIssueKind.Invalid, attribute);
                return null;
            }
            values.Add(length.Resolve(fontSize, percentBase));
            scanner.SkipCommaWhitespace();
        }
        return values.Count == 0 ? null : [.. values];
    }

    private double[]? NumberList(SvgElement element, string attribute)
    {
        if (element.Get(attribute) is not { } text) return null;
        var values = new List<double>();
        var scanner = new SvgScanner(text);
        scanner.SkipWhitespace();
        while (!scanner.AtEnd)
        {
            if (!scanner.TryNumber(out double value))
            {
                Issue(SvgIssueKind.Invalid, attribute);
                return null;
            }
            values.Add(value);
            scanner.SkipCommaWhitespace();
        }
        return values.Count == 0 ? null : [.. values];
    }

    private static readonly VectorPath UnitSquare = MakeUnitSquare();

    private static VectorPath MakeUnitSquare()
    {
        var builder = new PathBuilder();
        builder.MoveTo(0, 0);
        builder.LineTo(1, 0);
        builder.LineTo(1, 1);
        builder.LineTo(0, 1);
        builder.Close();
        return builder.ToPath();
    }

    /// <summary>One piece of character data as a span: its characters, and the style of the element it stands in.</summary>
    private VectorTextSpan Span(in SvgStyle style, in SvgViewport viewport, string characters, ImmutableArray<VectorTextPosition> positions, VectorTextPath? path)
    {
        TextPaints(style, viewport, out VectorFill? fill, out VectorStroke? stroke, out bool fillInBox, out bool strokeInBox);
        var lines = ImmutableArray<VectorTextLine>.Empty;
        if (style.Lines is { } asked)
        {
            var made = ImmutableArray.CreateBuilder<VectorTextLine>(asked.Length);
            foreach (SvgTextLines line in asked)
            {
                if (line.Made is null)
                {
                    SvgStyle own = line.Style;
                    TextPaints(own, viewport, out VectorFill? lineFill, out VectorStroke? lineStroke, out bool lineFillInBox, out bool lineStrokeInBox);
                    line.Made = new VectorTextLine(line.Kinds, lineFill, lineStroke, lineFill is not null && lineFillInBox, lineStroke is not null && lineStrokeInBox);
                }
                made.Add(line.Made);
            }
            lines = made.MoveToImmutable();
        }
        bool strokeFirst = style.PaintOrder != NormalPaintOrder && fill is not null && stroke is not null
            && Array.IndexOf(PaintSlots(style.PaintOrder), 1) < Array.IndexOf(PaintSlots(style.PaintOrder), 0);

        return new VectorTextSpan
        {
            Text = characters,
            Font = FontOf(style),
            Fill = fill, Stroke = stroke,
            FillInBox = fill is not null && fillInBox, StrokeInBox = stroke is not null && strokeInBox,
            StrokeFirst = strokeFirst,
            Hidden = !style.Visible,
            Anchor = style.TextAnchor,
            Baseline = style.AlignmentBaseline ?? style.Baseline,
            BaselineShift = (float)style.BaselineShift,
            ScriptRise = (float)style.ScriptRise,
            LetterSpacing = (float)style.LetterSpacing,
            WordSpacing = (float)style.WordSpacing,
            Lines = lines,
            Positions = positions,
            Path = path,
        };
    }

    private static VectorFont FontOf(in SvgStyle style) =>
        new(style.FontFamilies.IsDefault ? [] : style.FontFamilies, (float)style.FontSize, style.FontWeight, style.FontSlant, (float)style.FontStretch);

    /// <summary>The fill and the stroke a style gives text, and whether each is in the units of the text's box.</summary>
    private void TextPaints(in SvgStyle style, in SvgViewport viewport, out VectorFill? fill, out VectorStroke? stroke, out bool fillInBox, out bool strokeInBox)
    {
        (fill, stroke, fillInBox, strokeInBox) = (null, null, false, false);
        if (style.FillOpacity > 0 && TextPaint(style.Fill, style, viewport, out fillInBox) is { } fillPaint)
        {
            fill = MakeFill(style, fillPaint);
        }
        if (style.StrokeOpacity > 0 && style.StrokeWidth > 0 && TextPaint(style.Stroke, style, viewport, out strokeInBox) is { } strokePaint)
        {
            stroke = MakeStroke(style, strokePaint);
        }
    }

    /// <summary>
    /// What a paint draws with on text. A gradient in bounding-box units is worked out against the unit square and
    /// marked (<paramref name="inBox"/>): the box is the laid-out text's, which only whoever draws it knows. A pattern
    /// in such units cannot be said that way (its tile and its content may be in different units) and is reported.
    /// </summary>
    private VectorPaint? TextPaint(SvgPaint paint, in SvgStyle style, in SvgViewport viewport, out bool inBox)
    {
        inBox = false;
        if (paint.Kind != SvgPaintKind.Server) return ResolvePaint(paint, style, VectorPath.Empty, viewport);

        if (paint.Server!.Name == "pattern")
        {
            if (TryPattern(paint.Server, VectorPath.Empty, viewport, out VectorPaint? pattern)) return pattern;
            Issue(SvgIssueKind.Unsupported, "a pattern in bounding-box units on text");
        }
        else
        {
            if (!_gradients.TryGetValue(paint.Server, out GradientDefinition? definition))
            {
                _gradients[paint.Server] = definition = ReadGradient(paint.Server);
            }
            if (TryGradient(paint.Server, definition.UserSpace ? VectorPath.Empty : UnitSquare, viewport, out VectorPaint? gradient))
            {
                inBox = !definition.UserSpace && gradient is not (null or SolidPaint);
                return gradient;
            }
        }
        return paint.Fallback switch
        {
            SvgPaintKind.Color => Solid(paint.Color),
            SvgPaintKind.CurrentColor => Solid(style.Color),
            _ => null,
        };
    }

    /// <summary>
    /// The path a <c>textPath</c> names: its own <c>path</c> data, or the path or basic shape its <c>href</c> leads to
    /// (with that element's transform). Null, and reported, when it names none.
    /// </summary>
    private VectorTextPath? TextPath(SvgElement element, in SvgStyle style, in SvgViewport viewport)
    {
        VectorPath? path = null;
        SvgAffine transform = SvgAffine.Identity;
        if (element.Get("path") is { } data)
        {
            _builder.Clear();
            SvgGeometry.ParsePath(data, _builder);         // an error keeps what came before it, as for a path element
            VectorPath own = _builder.ToPath();
            if (!own.Verbs.IsEmpty) path = own;
        }
        if (path is null)
        {
            if (element.Get("href") is not { } href || (href = href.Trim()).Length < 2 || href[0] != '#'
                || !tree.Ids.TryGetValue(href[1..], out SvgElement? target)
                || target.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
            {
                Issue(SvgIssueKind.Invalid, "textPath: no path to set the text along");
                return null;
            }
            // The shape is measured where it stands: its lengths in its own font size, its own transform.
            SvgStyle targetStyle = InheritedStyle(target, viewport);
            if (!TryGeometry(target, targetStyle, viewport, out VectorPath shape, out _) || shape.Verbs.IsEmpty)
            {
                Issue(SvgIssueKind.Invalid, "textPath: no path to set the text along");
                return null;
            }
            path = shape;
            if (target.Get("transform") is { } transformText && !SvgAffine.TryParse(transformText, out transform))
            {
                Issue(SvgIssueKind.Invalid, "transform");
                transform = SvgAffine.Identity;
            }
            if (target.Get("pathLength") is not null) Issue(SvgIssueKind.Unsupported, "pathLength on a path text is set along");
        }

        double start = 0;
        bool fraction = false;
        if (element.Get("startOffset") is { } offsetText)
        {
            if (SvgLength.TryParse(offsetText, out SvgLength offset))
            {
                fraction = offset.Unit == SvgUnit.Percent;
                start = fraction ? offset.Value / 100.0 : offset.Resolve(style.FontSize, viewport.Diagonal);
            }
            else
            {
                Issue(SvgIssueKind.Invalid, "startOffset");
            }
        }
        if (element.Get("method") is { } method && method.AsSpan().Trim() is "stretch") Issue(SvgIssueKind.Unsupported, "textPath method: stretch");
        if (element.Get("spacing") is { } spacing && spacing.AsSpan().Trim() is "auto") Issue(SvgIssueKind.Unsupported, "textPath spacing: auto");
        bool right = element.Get("side") is { } side && side.AsSpan().Trim() is "right";
        return new VectorTextPath(path, transform.ToMatrix(), (float)start, fraction, right);
    }

    // ---- The font properties ----------------------------------------------------------------------------------------

    /// <summary>A <c>font-size</c>: a keyword, a length, or a percentage of the size inherited. False when it is none of them.</summary>
    private static bool TryFontSize(ReadOnlySpan<char> v, double inherited, out double size)
    {
        size = v switch
        {
            "xx-small" => 9, "x-small" => 10, "small" => 13, "medium" => 16, "large" => 18, "x-large" => 24, "xx-large" => 32, "xxx-large" => 48,
            "smaller" => inherited / 1.2, "larger" => inherited * 1.2,
            _ => -1,
        };
        if (size >= 0) return true;
        if (!SvgLength.TryParse(v, out SvgLength length) || length.Value < 0) return false;
        size = length.Resolve(inherited, inherited);
        return true;
    }

    private static bool TryFontWeight(ReadOnlySpan<char> v, int inherited, out int weight)
    {
        weight = v switch
        {
            "normal" => 400,
            "bold" => 700,
            // The steps CSS gives for the relative keywords.
            "bolder" => inherited < 350 ? 400 : inherited < 550 ? 700 : Math.Max(inherited, 900),
            "lighter" => inherited < 100 ? inherited : inherited < 550 ? 100 : inherited < 750 ? 400 : 700,
            _ => 0,
        };
        if (weight != 0) return true;
        var scanner = new SvgScanner(v);
        if (!scanner.TryNumber(out double number) || !scanner.AtEnd || number < 1 || number > 1000) return false;
        weight = (int)Math.Round(number);
        return true;
    }

    private static bool TryFontStretch(ReadOnlySpan<char> v, out double stretch)
    {
        stretch = v switch
        {
            "ultra-condensed" => 50, "extra-condensed" => 62.5, "condensed" => 75, "semi-condensed" => 87.5, "normal" => 100,
            "semi-expanded" => 112.5, "expanded" => 125, "extra-expanded" => 150, "ultra-expanded" => 200,
            _ => -1,
        };
        if (stretch >= 0) return true;
        if (!SvgLength.TryParse(v, out SvgLength length) || length.Unit != SvgUnit.Percent || length.Value < 0) return false;
        stretch = length.Value;
        return true;
    }

    private static bool TryFontSlant(ReadOnlySpan<char> v, out FontSlant slant)
    {
        slant = v is "italic" ? FontSlant.Italic : v.StartsWith("oblique") ? FontSlant.Oblique : FontSlant.Normal;
        return v is "normal" or "italic" || v.StartsWith("oblique");
    }

    /// <summary>A <c>font-family</c> list: names apart by commas, quotes taken off, the spaces inside an unquoted name made single.</summary>
    private static ImmutableArray<string> FontFamilies(ReadOnlySpan<char> v)
    {
        var families = ImmutableArray.CreateBuilder<string>();
        foreach (Range range in v.Split(','))
        {
            ReadOnlySpan<char> name = v[range].Trim();
            if (name.Length >= 2 && name[0] is '"' or '\'' && name[^1] == name[0]) name = name[1..^1];
            else if (name.ContainsAny(" \t\n\r")) name = string.Join(' ', name.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!name.IsEmpty) families.Add(name.ToString());
        }
        return families.ToImmutable();
    }

    /// <summary>
    /// The <c>font</c> shorthand: style, variant, weight and stretch in any order, then the size (with an optional line
    /// height, which SVG text on one line has no use for), then the family. What it does not name goes back to its
    /// initial value. <paramref name="sizeOnly"/> takes just the size, which is needed before everything else.
    /// </summary>
    private bool TryFontShorthand(ReadOnlySpan<char> v, ref SvgStyle s, in SvgStyle parent, bool sizeOnly)
    {
        int weight = 400;
        double stretch = 100;
        var slant = FontSlant.Normal;
        while (true)
        {
            v = v.TrimStart();
            int end = v.IndexOfAny(" \t\n\r");
            if (end < 0) return false;                                          // a size and a family are both required
            ReadOnlySpan<char> word = v[..end];
            // The size is the first word that is not one of the keywords before it. Its line height comes after a slash.
            int slash = word.IndexOf('/');
            ReadOnlySpan<char> sizeWord = slash < 0 ? word : word[..slash];
            if (word is "normal" or "small-caps") { }
            else if (TryFontSlant(word, out FontSlant wordSlant)) slant = wordSlant;
            else if (!char.IsAsciiDigit(word[0]) && word[0] != '.' && TryFontStretch(word, out double wordStretch)) stretch = wordStretch;
            // A number with no unit is a weight: in CSS, which this is, a size has a unit.
            else if (slash < 0 && !(SvgLength.TryParse(word, out SvgLength bare) && bare.Unit != SvgUnit.None)
                && TryFontWeight(word, parent.FontWeight, out int wordWeight)) weight = wordWeight;
            else if (TryFontSize(sizeWord, parent.FontSize, out double size))
            {
                ReadOnlySpan<char> rest = v[end..].TrimStart();
                if (slash < 0 && rest.StartsWith("/"))
                {
                    // "12px / 1.5 serif": the line height stands apart.
                    rest = rest[1..].TrimStart();
                    int afterLineHeight = rest.IndexOfAny(" \t\n\r");
                    if (afterLineHeight < 0) return false;
                    rest = rest[afterLineHeight..].TrimStart();
                }
                if (rest.IsEmpty) return false;
                if (sizeOnly)
                {
                    s.FontSize = size;
                    return true;
                }
                (s.FontWeight, s.FontStretch, s.FontSlant) = (weight, stretch, slant);
                s.FontFamilies = FontFamilies(rest);
                return true;
            }
            else
            {
                return false;
            }
            v = v[end..];
        }
    }
}
