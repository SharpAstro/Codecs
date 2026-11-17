using System.Collections.Immutable;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

/// <summary>A <c>viewBox</c>: the rectangle of user space that is mapped onto the picture.</summary>
public readonly record struct SvgViewBox(float X, float Y, float Width, float Height);

/// <summary>What kind of thing a reader left out.</summary>
public enum SvgIssueKind
{
    /// <summary>Valid SVG that this reader does not read yet. The scene is drawn without it.</summary>
    Unsupported,
    /// <summary>Something the document got wrong. It was handled the way the specification says an error is.</summary>
    Invalid,
}

/// <summary>One thing the scene does not show the way the document asked, and how many times it came up.</summary>
/// <param name="Kind">Whether the reader or the document is the reason.</param>
/// <param name="What">The element, property or value, in words.</param>
/// <param name="Count">How many times.</param>
public readonly record struct SvgIssue(SvgIssueKind Kind, string What, int Count);

/// <summary>
/// A read SVG document: the resolved scene, the size it was laid out at, and everything the scene leaves out.
/// An empty <see cref="Issues"/> means the scene is the whole document.
/// </summary>
public sealed class SvgDocument
{
    internal SvgDocument(VectorScene scene, SvgViewBox? viewBox, ImmutableArray<SvgIssue> issues)
    {
        Scene = scene;
        ViewBox = viewBox;
        Issues = issues;
    }

    /// <summary>The drawing, in pixels at 96 to the inch, with the <c>viewBox</c> mapping already applied to every shape.</summary>
    public VectorScene Scene { get; }

    /// <summary>The picture's width in pixels: the root's <c>width</c>, or what it defaults to.</summary>
    public float Width => Scene.Width;

    /// <summary>The picture's height in pixels.</summary>
    public float Height => Scene.Height;

    /// <summary>The root's <c>viewBox</c>, when it has a usable one.</summary>
    public SvgViewBox? ViewBox { get; }

    /// <summary>What the scene leaves out or had to correct, most frequent first.</summary>
    public ImmutableArray<SvgIssue> Issues { get; }
}

/// <summary>How a document is read. The limits bound work and memory by what the document declares.</summary>
public sealed record SvgReadOptions
{
    /// <summary>The defaults.</summary>
    public static SvgReadOptions Default { get; } = new();

    /// <summary>
    /// The width a root <c>width</c> that is absent or a percentage resolves against. Without it an absent width comes
    /// from the <c>viewBox</c> (keeping its aspect ratio against a given height), or is 300.
    /// </summary>
    public float? ViewportWidth { get; init; }

    /// <summary>As <see cref="ViewportWidth"/>, for the height; the last resort is 150.</summary>
    public float? ViewportHeight { get; init; }

    /// <summary>Throw <see cref="NotSupportedException"/> at the first <see cref="SvgIssueKind.Unsupported"/> issue, where the default is to record it.</summary>
    public bool ThrowOnUnsupported { get; init; }

    /// <summary>
    /// Draw only the element with this <c>id</c>, as a <c>use</c> of it standing alone in the root would: its own
    /// properties and transform apply, its ancestors' do not, and whatever it refers to is found in the rest of the
    /// document. This is how the OpenType <c>SVG </c> table names a glyph (<c>glyph14</c>) in a document that holds
    /// several. When the root itself has the id the whole document is drawn. No such element is an
    /// <see cref="SvgIssueKind.Invalid"/> issue and an empty scene.
    /// </summary>
    public string? ElementId { get; init; }

    /// <summary>
    /// What <c>currentColor</c> is where the document sets no <c>color</c>: the colour of the text around the
    /// picture. Black when not given.
    /// </summary>
    public Rgba32 CurrentColor { get; init; } = new(0, 0, 0);

    /// <summary>
    /// CSS custom properties the surroundings define, by name with the two dashes (<c>--color0</c>), for the
    /// document's <c>var()</c> to find. A font's palette reaches its glyphs this way. The document's own declarations
    /// win over these, as they would over an ancestor's.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomProperties { get; init; }

    /// <summary>The most characters the XML may hold, after any gzip is undone. Default 2^26.</summary>
    public long MaxCharacters { get; init; } = 1L << 26;

    /// <summary>
    /// The most characters the entities a document declares may expand to, in total. A document chooses how often an
    /// entity is used and what is in it, so this, not its size, is what bounds the expansion. Default 2^22.
    /// </summary>
    public long MaxEntityCharacters { get; init; } = 1L << 22;

    /// <summary>
    /// The most elements the document may hold, and the most it may draw once every <c>use</c> is followed: an element
    /// that is used a hundred times counts a hundred times. Default 2^21.
    /// </summary>
    public int MaxElements { get; init; } = 1 << 21;

    /// <summary>The deepest element nesting, in the document and again once every <c>use</c> is followed. Default 256.</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>The most path verbs the scene may hold in total, each instance a <c>use</c> makes counted. Default 2^24.</summary>
    public int MaxPathVerbs { get; init; } = 1 << 24;

    /// <summary>
    /// The most selector tests the style sheets may cost. Applying a sheet is elements times rules, and a document
    /// chooses both. Default 2^27.
    /// </summary>
    public long MaxSelectorTests { get; init; } = 1L << 27;
}
