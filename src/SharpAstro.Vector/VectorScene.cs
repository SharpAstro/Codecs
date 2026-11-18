using System.Collections.Immutable;
using System.Numerics;

namespace SharpAstro.VectorGraphics;

/// <summary>
/// An sRGB colour, 8 bits a channel, alpha not premultiplied. The same shape as SharpAstro.Fonts' <c>Rgba32</c>, so that
/// one can be replaced by this when Fonts.Lib takes this package.
/// </summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Nothing: every channel zero.</summary>
    public static readonly Rgba32 Transparent = new(0, 0, 0, 0);

    /// <summary>Opaque black.</summary>
    public static readonly Rgba32 Black = new(0, 0, 0, 255);

    /// <summary>The colour with its alpha multiplied by <paramref name="multiplier"/> (0 to 1), rounded to the nearest level.</summary>
    public Rgba32 WithMultipliedAlpha(float multiplier)
        => new(R, G, B, (byte)Math.Clamp((int)MathF.Round(A * multiplier), 0, 255));

    /// <summary>
    /// The colour a share <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>, channel by
    /// channel and rounded down: <paramref name="a"/> at 0 or below, <paramref name="b"/> at 1 or above.
    /// </summary>
    public static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        return new Rgba32(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }
}

/// <summary>
/// What an area is painted with: a <see cref="SolidPaint"/>, a <see cref="LinearGradientPaint"/>, a
/// <see cref="RadialGradientPaint"/> or a <see cref="PatternPaint"/>. A renderer that meets a kind it does not know
/// should say so, not guess.
/// </summary>
public abstract record VectorPaint;

/// <summary>One colour everywhere.</summary>
public sealed record SolidPaint(Rgba32 Color) : VectorPaint;

/// <summary>Which points of a self-overlapping path are inside it.</summary>
public enum FillRule : byte
{
    /// <summary>Inside where the winding number is not zero.</summary>
    NonZero,
    /// <summary>Inside where the winding number is odd.</summary>
    EvenOdd,
}

/// <summary>The shape of a stroke's open ends.</summary>
public enum LineCap : byte
{
    /// <summary>The stroke stops at the end point.</summary>
    Butt,
    /// <summary>A half disc around the end point.</summary>
    Round,
    /// <summary>A half square around the end point.</summary>
    Square,
}

/// <summary>The shape of a stroke where two segments meet.</summary>
public enum LineJoin : byte
{
    /// <summary>The outer edges are extended to a point, up to the miter limit.</summary>
    Miter,
    /// <summary>A disc around the corner.</summary>
    Round,
    /// <summary>The corner is cut off straight.</summary>
    Bevel,
}

/// <summary>How a shape's interior is painted.</summary>
/// <param name="Paint">What it is painted with.</param>
/// <param name="Opacity">0 to 1, multiplied with the paint's own alpha.</param>
/// <param name="Rule">Which points count as interior.</param>
public sealed record VectorFill(VectorPaint Paint, float Opacity, FillRule Rule);

/// <summary>
/// How a shape's outline is painted. <see cref="Width"/>, <see cref="Dashes"/> and <see cref="DashOffset"/> are in the
/// shape's local units, so the shape's transform scales (and, when it is not uniform, distorts) the stroke.
/// </summary>
/// <param name="Paint">What it is painted with.</param>
/// <param name="Opacity">0 to 1, multiplied with the paint's own alpha.</param>
/// <param name="Width">The stroke's width, above zero.</param>
/// <param name="Cap">The open ends.</param>
/// <param name="Join">The corners.</param>
/// <param name="MiterLimit">For <see cref="LineJoin.Miter"/>: the largest miter length over stroke width before the corner is bevelled; at least 1.</param>
/// <param name="Dashes">Alternating dash and gap lengths, an even number of them, or empty for a solid line.</param>
/// <param name="DashOffset">How far into the dash pattern the stroke starts.</param>
public sealed record VectorStroke(
    VectorPaint Paint,
    float Opacity,
    float Width,
    LineCap Cap,
    LineJoin Join,
    float MiterLimit,
    ImmutableArray<float> Dashes,
    float DashOffset);

/// <summary>
/// Something in a <see cref="VectorScene"/>: a <see cref="VectorShape"/>, a <see cref="VectorImage"/>,
/// a <see cref="VectorText"/> or a <see cref="VectorGroup"/>.
/// </summary>
public abstract class VectorNode
{
    /// <summary>The source's own name for this node (an SVG <c>id</c>), when it had one.</summary>
    public string? Id { get; init; }
}

/// <summary>
/// A path with its paints. The fill is drawn first and the stroke over it. <see cref="Transform"/> takes the
/// path's local coordinates to the scene's, and is already the product of everything above the shape in its source.
/// </summary>
public sealed class VectorShape : VectorNode
{
    /// <summary>The outline, in local coordinates.</summary>
    public required VectorPath Path { get; init; }

    /// <summary>Local coordinates to scene coordinates (<c>Vector2.Transform(point, Transform)</c>).</summary>
    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;

    /// <summary>The interior's paint, or null for none.</summary>
    public VectorFill? Fill { get; init; }

    /// <summary>The outline's paint, or null for none.</summary>
    public VectorStroke? Stroke { get; init; }
}

/// <summary>
/// Nodes that must be drawn together and then composited as one: the children are drawn, in order, into
/// a transparent layer; the layer is replaced by what <see cref="Filter"/> makes of it, cut down to <see cref="Clip"/>
/// and multiplied by <see cref="Mask"/> when there are any, in that order, and then put over what is below it at
/// <see cref="Opacity"/>, its colour mixed with the colour below by <see cref="Blend"/>.
/// A reader only emits a group where that differs from drawing the children one by one.
/// </summary>
/// <remarks>
/// "What is below" is what was drawn before this group into the layer this group is in: the scene, or the nearest
/// group around it. A group is thereby also what keeps a blend inside it from reaching what is below the group.
/// </remarks>
public sealed class VectorGroup : VectorNode
{
    /// <summary>0 to 1.</summary>
    public required float Opacity { get; init; }

    /// <summary>The children, bottom first.</summary>
    public required ImmutableArray<VectorNode> Children { get; init; }

    /// <summary>The region the layer is kept to, or null for all of it.</summary>
    public VectorClip? Clip { get; init; }

    /// <summary>The picture the layer is multiplied by, or null.</summary>
    public VectorMask? Mask { get; init; }

    /// <summary>What the layer is replaced by before it is clipped and masked, or null for the layer itself.</summary>
    public VectorFilter? Filter { get; init; }

    /// <summary>How the layer's colour is mixed with what is below it.</summary>
    public VectorBlendMode Blend { get; init; }
}

/// <summary>
/// A resolved drawing: nodes in painting order (the first is at the bottom) in a coordinate system with the origin at
/// the top left, x to the right and y down. Nothing outside <c>[0, Width] x [0, Height]</c> is part of the picture, so a
/// renderer clips to it.
/// </summary>
public sealed class VectorScene
{
    /// <summary>Creates a scene.</summary>
    public VectorScene(float width, float height, ImmutableArray<VectorNode> nodes)
    {
        Width = width;
        Height = height;
        Nodes = nodes.IsDefault ? [] : nodes;
    }

    /// <summary>The picture's width in scene units.</summary>
    public float Width { get; }

    /// <summary>The picture's height in scene units.</summary>
    public float Height { get; }

    /// <summary>The top-level nodes, bottom first.</summary>
    public ImmutableArray<VectorNode> Nodes { get; }
}
