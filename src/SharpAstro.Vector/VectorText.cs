using System.Collections.Immutable;
using System.Numerics;

namespace SharpAstro.VectorGraphics;

/// <summary>Upright, italic or slanted.</summary>
public enum FontSlant : byte
{
    /// <summary>Upright.</summary>
    Normal,
    /// <summary>The face's italic.</summary>
    Italic,
    /// <summary>A slanted upright.</summary>
    Oblique,
}

/// <summary>Which point of a piece of text is put where the text was told to start.</summary>
public enum TextAnchor : byte
{
    /// <summary>Its start: the left end of left-to-right text.</summary>
    Start,
    /// <summary>Its middle.</summary>
    Middle,
    /// <summary>Its end.</summary>
    End,
}

/// <summary>Which line of the font lies on the text's y coordinate.</summary>
public enum TextBaseline : byte
{
    /// <summary>The baseline letters stand on.</summary>
    Alphabetic,
    /// <summary>Half the height of a lower-case x above the alphabetic baseline.</summary>
    Middle,
    /// <summary>Halfway between the font's ascent and descent.</summary>
    Central,
    /// <summary>The line some scripts hang from, near the top of the capitals.</summary>
    Hanging,
    /// <summary>The line mathematical symbols are centred on.</summary>
    Mathematical,
    /// <summary>The bottom of ideographic characters.</summary>
    Ideographic,
    /// <summary>The top of the font: its ascent.</summary>
    TextTop,
    /// <summary>The bottom of the font: its descent.</summary>
    TextBottom,
}

/// <summary>Lines drawn with text.</summary>
[Flags]
public enum TextDecorations : byte
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Under the baseline.</summary>
    Underline = 1,
    /// <summary>Over the top.</summary>
    Overline = 2,
    /// <summary>Through the middle.</summary>
    LineThrough = 4,
}

/// <summary>What is changed to make text as long as it was told to be.</summary>
public enum TextLengthAdjust : byte
{
    /// <summary>The gaps between characters.</summary>
    Spacing,
    /// <summary>The gaps and the characters themselves, along the line.</summary>
    SpacingAndGlyphs,
}

/// <summary>
/// The font a piece of text asks for, as its source describes it. Nothing here names a font file: whoever draws the
/// text finds a face, and that is the only place a font is known.
/// </summary>
/// <param name="Families">Family names in order of preference, generic ones (<c>serif</c>, <c>monospace</c>) included as written. May be empty.</param>
/// <param name="Size">The em, in the text's local units.</param>
/// <param name="Weight">1 to 1000: 400 is regular, 700 bold.</param>
/// <param name="Slant">Upright, italic or oblique.</param>
/// <param name="Stretch">The width, as a percentage of normal: 100 is normal, 75 condensed.</param>
public sealed record VectorFont(ImmutableArray<string> Families, float Size, int Weight = 400, FontSlant Slant = FontSlant.Normal, float Stretch = 100f)
{
    /// <inheritdoc/>
    public bool Equals(VectorFont? other) =>
        other is not null && Size == other.Size && Weight == other.Weight && Slant == other.Slant && Stretch == other.Stretch
        && Families.AsSpan().SequenceEqual(other.Families.AsSpan());

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Size);
        hash.Add(Weight);
        hash.Add(Slant);
        hash.Add(Stretch);
        foreach (string family in Families.IsDefault ? [] : Families) hash.Add(family);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Where the source puts one character, for the characters it says anything about. A character with no entry
/// follows the one before it and is not turned.
/// </summary>
/// <param name="Index">Where the character starts in its span's <see cref="VectorTextSpan.Text"/> (a UTF-16 offset; the character may be a surrogate pair).</param>
/// <param name="X">An absolute x for the character, in the text's local units. With this or <paramref name="Y"/> set, the character starts a new chunk: what follows is laid out, and anchored, from here.</param>
/// <param name="Y">An absolute y for the character.</param>
/// <param name="Dx">A shift along x, of this character and everything after it.</param>
/// <param name="Dy">A shift along y, of this character and everything after it.</param>
/// <param name="Rotate">Degrees the character is turned, clockwise, about its own origin. It does not turn what follows.</param>
public readonly record struct VectorTextPosition(int Index, float? X, float? Y, float Dx, float Dy, float Rotate);

/// <summary>
/// A path text is set along. A character's place along the line becomes a distance along the path, measured from
/// <see cref="StartOffset"/>; a character whose middle is off an open path is not drawn.
/// </summary>
/// <param name="Path">The path, in its own coordinates.</param>
/// <param name="Transform">The path's coordinates to the text's local ones.</param>
/// <param name="StartOffset">Where along the path the text starts: a length in the text's local units, or a fraction of the path's whole length when <paramref name="StartIsFraction"/>.</param>
/// <param name="StartIsFraction">Whether <paramref name="StartOffset"/> is a fraction of the path's length (0 to 1 for a place on it).</param>
/// <param name="RightSide">Set on the path's right side: the path is followed backwards.</param>
public sealed record VectorTextPath(VectorPath Path, Matrix3x2 Transform, float StartOffset, bool StartIsFraction, bool RightSide);

/// <summary>
/// Lines drawn with text (an underline, an overline, a line through it), in the paints of the element that asked for
/// them: a red word inside underlined black text is underlined in black. How thick a line is and where it lies follow
/// the span it runs under, so under a larger word it is thicker and lower. Spans that are under the same lines hold
/// the same object.
/// </summary>
/// <param name="Kinds">Which lines.</param>
/// <param name="Fill">The paint inside the lines, or null.</param>
/// <param name="Stroke">The paint of their outlines, or null.</param>
/// <param name="FillInBox">As <see cref="VectorTextSpan.FillInBox"/>.</param>
/// <param name="StrokeInBox">As <see cref="VectorTextSpan.StrokeInBox"/>.</param>
public sealed record VectorTextLine(TextDecorations Kinds, VectorFill? Fill, VectorStroke? Stroke, bool FillInBox, bool StrokeInBox);

/// <summary>
/// A run of text with one style. A <see cref="VectorText"/> is its spans one after the other on a line: where a span
/// starts is where the one before ended, unless a <see cref="Positions"/> entry says otherwise.
/// </summary>
public sealed record VectorTextSpan
{
    /// <summary>The characters, with white space already handled as the source's rules say: nothing here is collapsed or trimmed again.</summary>
    public required string Text { get; init; }

    /// <summary>The font asked for.</summary>
    public required VectorFont Font { get; init; }

    /// <summary>The paint inside the glyphs, or null for none.</summary>
    public VectorFill? Fill { get; init; }

    /// <summary>The paint of the glyphs' outlines, or null for none.</summary>
    public VectorStroke? Stroke { get; init; }

    /// <summary>
    /// Whether <see cref="Fill"/>'s paint is in fractions of the bounding box of the whole <see cref="VectorText"/> (a
    /// gradient in bounding-box units). Only whoever lays the text out knows that box: the paint's own transform
    /// then follows the map from the unit square to the box.
    /// </summary>
    public bool FillInBox { get; init; }

    /// <summary>As <see cref="FillInBox"/>, for <see cref="Stroke"/>.</summary>
    public bool StrokeInBox { get; init; }

    /// <summary>The stroke is painted before the fill, so that the fill lies over it.</summary>
    public bool StrokeFirst { get; init; }

    /// <summary>Takes its room on the line and is not drawn.</summary>
    public bool Hidden { get; init; }

    /// <summary>For the chunk that starts in this span: which point of it is put at the chunk's position.</summary>
    public TextAnchor Anchor { get; init; }

    /// <summary>Which line of the font lies on the y coordinate.</summary>
    public TextBaseline Baseline { get; init; }

    /// <summary>How far the span is raised above where its baseline would be, in the text's local units (negative: lowered).</summary>
    public float BaselineShift { get; init; }

    /// <summary>
    /// How far the span is raised as a superscript, or lowered as a subscript when negative, given as a font size
    /// in the text's local units: the sizes of the superscripts the span is in, less those of the subscripts. How far
    /// that is in the end is the font's to say: half the height of a line (ascent plus descent) at this size.
    /// </summary>
    public float ScriptRise { get; init; }

    /// <summary>Added after every character, in the text's local units.</summary>
    public float LetterSpacing { get; init; }

    /// <summary>Added after every space, in the text's local units.</summary>
    public float WordSpacing { get; init; }

    /// <summary>
    /// The lines the span is under, outermost first: one entry for each element around it (or itself) that asked for
    /// any. Empty for most text.
    /// </summary>
    public ImmutableArray<VectorTextLine> Lines { get; init; } = [];

    /// <summary>What the source says about single characters, in the order of <see cref="VectorTextPosition.Index"/>. Empty for most text.</summary>
    public ImmutableArray<VectorTextPosition> Positions { get; init; } = [];

    /// <summary>The path this span is set along, or null. Spans that follow one another on the same path hold the same object.</summary>
    public VectorTextPath? Path { get; init; }
}

/// <summary>
/// Text, as its source describes it: what to write, in which font, and where. It is NOT laid out and holds no
/// glyphs, because that takes a font, and a scene knows none. Whoever draws the scene shapes each span with the text
/// code it already has, and places the result as the spans say.
/// </summary>
/// <remarks>
/// The first character is at the first span's first position (the origin when it has none), on the baseline. Each
/// following character is where the one before ended, moved by any <see cref="VectorTextPosition"/> it has. Characters
/// from one that has an absolute position up to the next such form a chunk, and a chunk is then moved as a whole so
/// that its <see cref="VectorTextSpan.Anchor"/> point is where the chunk was told to start.
/// </remarks>
public sealed class VectorText : VectorNode
{
    /// <summary>The spans, in reading order.</summary>
    public required ImmutableArray<VectorTextSpan> Spans { get; init; }

    /// <summary>Local coordinates to scene coordinates.</summary>
    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;

    /// <summary>How long the whole text is to be, in local units, or null for its natural length.</summary>
    public float? TextLength { get; init; }

    /// <summary>What is changed to reach <see cref="TextLength"/>.</summary>
    public TextLengthAdjust LengthAdjust { get; init; }
}
