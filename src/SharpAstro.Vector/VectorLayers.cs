using System.Collections.Immutable;
using System.Numerics;

namespace SharpAstro.VectorGraphics;

/// <summary>
/// One outline of a <see cref="VectorClip"/>: the inside of <paramref name="Path"/>, by <paramref name="Rule"/>, once
/// <paramref name="Transform"/> has taken it to scene coordinates. A clip has no paint and no stroke: a point is inside
/// or it is not, and the edge is anti-aliased like a fill's.
/// </summary>
/// <param name="Path">The outline, in local coordinates.</param>
/// <param name="Transform">Local coordinates to scene coordinates.</param>
/// <param name="Rule">Which points count as inside.</param>
/// <param name="Clip">A further clip on this one outline, or null: only where both are inside does the outline count.</param>
public sealed record VectorClipShape(VectorPath Path, Matrix3x2 Transform, FillRule Rule, VectorClip? Clip = null);

/// <summary>
/// A region that drawing is kept to: the union of <see cref="Shapes"/>, and of that only what is also inside
/// <see cref="Intersect"/> when there is one. With no shapes the region is empty and nothing is drawn.
/// </summary>
public sealed class VectorClip
{
    /// <summary>The outlines, whose union is the region.</summary>
    public required ImmutableArray<VectorClipShape> Shapes { get; init; }

    /// <summary>Another clip the region is cut down to, or null.</summary>
    public VectorClip? Intersect { get; init; }

    /// <summary>The clip to one rectangle, in the coordinates <paramref name="transform"/> takes to the scene.</summary>
    public static VectorClip Rectangle(VectorRect rectangle, Matrix3x2 transform, VectorClip? intersect = null)
    {
        var builder = new PathBuilder();
        builder.MoveTo(rectangle.X, rectangle.Y);
        builder.LineTo(rectangle.Right, rectangle.Y);
        builder.LineTo(rectangle.Right, rectangle.Bottom);
        builder.LineTo(rectangle.X, rectangle.Bottom);
        builder.Close();
        return new VectorClip { Shapes = [new VectorClipShape(builder.ToPath(), transform, FillRule.NonZero)], Intersect = intersect };
    }
}

/// <summary>What of a mask's picture says how much of the masked layer is kept.</summary>
public enum VectorMaskKind : byte
{
    /// <summary>
    /// Its brightness times its alpha: <c>(0.2125 R + 0.7154 G + 0.0721 B) * A</c>, with R, G and B the sRGB values as
    /// they are (0 to 1, not linearised). White and opaque keeps everything, black or transparent nothing.
    /// </summary>
    Luminance,
    /// <summary>Its alpha alone.</summary>
    Alpha,
}

/// <summary>
/// A picture that says, pixel by pixel, how much of a layer is kept. The <see cref="Children"/> are drawn, in order, into
/// a transparent layer of their own; each pixel of that gives a factor by <see cref="Kind"/>, and the masked layer is
/// multiplied by it. Where the mask's picture has nothing, the factor is zero.
/// </summary>
public sealed class VectorMask
{
    /// <summary>The mask's picture, bottom first, in scene coordinates like every other node.</summary>
    public required ImmutableArray<VectorNode> Children { get; init; }

    /// <summary>How the picture becomes a factor.</summary>
    public VectorMaskKind Kind { get; init; }
}

/// <summary>How a <see cref="VectorImage"/>'s picture is fitted into its box when the two have different proportions.</summary>
public enum ImageFit : byte
{
    /// <summary>Stretched to fill the box exactly; the proportions change.</summary>
    Stretch,
    /// <summary>Scaled, proportions kept, to the largest size at which all of it is inside the box.</summary>
    Meet,
    /// <summary>Scaled, proportions kept, to the smallest size at which it covers the box; what is outside the box is not drawn.</summary>
    Slice,
}

/// <summary>
/// A raster picture placed in the scene. The scene carries the picture as the file it came as (PNG, JPEG, ...), not as
/// pixels: decoding is the renderer's or its caller's, with whatever codecs it has. The picture's own size is therefore
/// not known here, so how it is fitted into <see cref="Bounds"/> is given as a rule, not as a rectangle.
/// </summary>
public sealed class VectorImage : VectorNode
{
    /// <summary>The picture's file bytes, when the source embedded them. Empty when it only named the picture (<see cref="Source"/>).</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The media type the source gave for <see cref="Data"/> (<c>image/png</c>), when it gave one.</summary>
    public string? MediaType { get; init; }

    /// <summary>The address the source named instead of embedding the picture. Nothing here fetches it.</summary>
    public string? Source { get; init; }

    /// <summary>The box the picture is fitted into, in local coordinates. Nothing of the picture is drawn outside it.</summary>
    public required VectorRect Bounds { get; init; }

    /// <summary>Local coordinates to scene coordinates.</summary>
    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;

    /// <summary>0 to 1.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>How the picture is scaled into the box.</summary>
    public ImageFit Fit { get; init; } = ImageFit.Meet;

    /// <summary>Where the picture sits across the box when it does not fill it exactly: 0 left, 0.5 centred, 1 right.</summary>
    public float AlignX { get; init; } = 0.5f;

    /// <summary>Where the picture sits down the box when it does not fill it exactly: 0 top, 0.5 centred, 1 bottom.</summary>
    public float AlignY { get; init; } = 0.5f;
}

/// <summary>
/// A picture repeated in both directions. <see cref="Children"/> are drawn in pattern space, clipped to
/// <see cref="Tile"/>; that tile is then repeated at steps of its width and height over the whole plane, and
/// <see cref="Transform"/> takes pattern space to the painted shape's local coordinates (so the tiles may be rotated or
/// skewed with respect to the shape).
/// </summary>
/// <param name="Children">The tile's picture, bottom first, in pattern space.</param>
/// <param name="Tile">One tile, in pattern space. Its width and height are above zero.</param>
/// <param name="Transform">Pattern space to the shape's local coordinates. Always invertible.</param>
public sealed record PatternPaint(ImmutableArray<VectorNode> Children, VectorRect Tile, Matrix3x2 Transform) : VectorPaint;
