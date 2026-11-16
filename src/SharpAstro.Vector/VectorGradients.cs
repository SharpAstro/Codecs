using System.Collections.Immutable;
using System.Numerics;

namespace SharpAstro.VectorGraphics;

/// <summary>What a gradient does outside its first and last stop.</summary>
public enum GradientExtend : byte
{
    /// <summary>The end colours go on for ever.</summary>
    Pad,
    /// <summary>The gradient starts again.</summary>
    Repeat,
    /// <summary>The gradient runs back and forth.</summary>
    Reflect,
}

/// <summary>A colour at a place along a gradient.</summary>
/// <param name="Offset">0 to 1. A paint's stops are in order of offset; two may share one, which is a hard edge.</param>
/// <param name="Color">The colour there, its alpha included.</param>
public readonly record struct GradientStop(float Offset, Rgba32 Color);

/// <summary>
/// A gradient along the line from (<paramref name="X1"/>, <paramref name="Y1"/>), where the stops' offset is 0, to
/// (<paramref name="X2"/>, <paramref name="Y2"/>), where it is 1; the colour is constant across the line.
/// </summary>
/// <remarks>
/// The points are in the gradient's own space. <paramref name="Transform"/> takes that space to the shape's local
/// coordinates, so a renderer maps a local point back through its inverse and then projects it onto the line. Colours
/// between two stops are a straight blend of their sRGB channels and alpha, not premultiplied.
/// </remarks>
/// <param name="Stops">At least two, in order of offset.</param>
/// <param name="Extend">What happens before 0 and after 1.</param>
/// <param name="Transform">Gradient space to the shape's local coordinates. Always invertible.</param>
public sealed record LinearGradientPaint(
    float X1, float Y1, float X2, float Y2,
    ImmutableArray<GradientStop> Stops, GradientExtend Extend, Matrix3x2 Transform) : VectorPaint;

/// <summary>
/// A gradient between two circles: offset 0 on the start circle, 1 on the end circle, and in between on the circles
/// whose centre and radius are blended between the two. With the start circle a point at the end circle's centre
/// this is the plain radial gradient; a start point elsewhere is a focal point.
/// </summary>
/// <remarks>
/// As for <see cref="LinearGradientPaint"/>, the circles are in the gradient's own space and
/// <paramref name="Transform"/> takes it to the shape's local coordinates (so a circle may be drawn as an ellipse).
/// The same two-circle model as an OpenType COLR radial gradient. Where no circle of the family passes through a
/// point (outside the cone, when the start point lies outside the end circle) nothing is painted there.
/// </remarks>
/// <param name="Stops">At least two, in order of offset.</param>
/// <param name="Extend">What happens before 0 and after 1.</param>
/// <param name="Transform">Gradient space to the shape's local coordinates. Always invertible.</param>
public sealed record RadialGradientPaint(
    float StartX, float StartY, float StartRadius, float EndX, float EndY, float EndRadius,
    ImmutableArray<GradientStop> Stops, GradientExtend Extend, Matrix3x2 Transform) : VectorPaint;

/// <summary>An axis-aligned rectangle.</summary>
public readonly record struct VectorRect(float X, float Y, float Width, float Height)
{
    /// <summary>The right edge.</summary>
    public float Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public float Bottom => Y + Height;
}
