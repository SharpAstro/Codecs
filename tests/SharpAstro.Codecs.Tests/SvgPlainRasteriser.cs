using System.Collections.Immutable;
using System.Numerics;
using ImageMagick;
using SharpAstro.VectorGraphics;
using FillRule = SharpAstro.VectorGraphics.FillRule;
using LineCap = SharpAstro.VectorGraphics.LineCap;
using LineJoin = SharpAstro.VectorGraphics.LineJoin;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Draws a scene over white the slow and obvious way, for the pixel oracles: the scene is a function from a point to a
/// colour, and a pixel is the mean of that function at a 4 x 4 grid of points inside it. Nothing is ever turned into
/// spans or coverage, so there is no rasteriser to get wrong, only "is this point inside":
/// a fill by winding number against the flattened outline; a stroke by distance to the outline, with the joins and caps
/// as the small polygons they are; a group as its children composited at that point, kept or dropped by its clip and
/// scaled by its mask and opacity; a pattern as its tile's scene at the point wrapped into the tile.
/// </summary>
/// <remarks>
/// It is test code and takes no care to be fast. It shares the reading of the SCENE with nothing: the scene's contract
/// (what a group, a clip, a mask, a pattern mean) is written out again here from its documentation.
/// </remarks>
internal static class SvgPlainRasteriser
{
    private const int Grid = 4;

    /// <summary>RGB floats, 0 to 1, over white.</summary>
    public static float[] Render(VectorScene scene, int width, int height)
    {
        List<Drawable> drawables = Compile(scene.Nodes);
        float[] rgb = new float[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector4 sum = default;
                for (int s = 0; s < Grid * Grid; s++)
                {
                    var sample = new Vector2(x + (s % Grid + 0.5f) / Grid, y + (s / Grid + 0.5f) / Grid);
                    Vector4 c = At(drawables, sample);
                    // Over opaque white.
                    sum += new Vector4(c.X + (1 - c.W), c.Y + (1 - c.W), c.Z + (1 - c.W), 1);
                }
                sum /= Grid * Grid;
                int i = (y * width + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = (sum.X, sum.Y, sum.Z);
            }
        }
        return rgb;
    }

    /// <summary>Something that has a colour at a point: premultiplied RGBA, nothing where it is not.</summary>
    private abstract class Drawable
    {
        public abstract Vector4 At(Vector2 p);
    }

    /// <summary>The drawables composited at a point, bottom first: premultiplied RGBA over nothing.</summary>
    private static Vector4 At(List<Drawable> drawables, Vector2 p)
    {
        Vector4 below = default;
        foreach (Drawable drawable in drawables)
        {
            Vector4 above = drawable.At(p);
            if (above.W > 0) below = above + below * (1 - above.W);
        }
        return below;
    }

    private static List<Drawable> Compile(ImmutableArray<VectorNode> nodes)
    {
        var drawables = new List<Drawable>();
        foreach (VectorNode node in nodes)
        {
            switch (node)
            {
                case VectorGroup group:
                    drawables.Add(new Layer(group));
                    break;
                case VectorImage image:
                    drawables.Add(new Picture(image));
                    break;
                case VectorShape shape:
                    if (shape.Fill is { } fill) drawables.Add(new Filled(shape, fill));
                    if (shape.Stroke is { } stroke) drawables.Add(new Stroked(shape, stroke));
                    break;
                case VectorText:
                    // Text takes a font, and there is none in this repository: it is drawn, and held against a
                    // browser, in Fonts.Lib. Here it is left out, and no oracle document of this repository has any.
                    break;
                default:
                    throw new NotSupportedException(node.GetType().Name);
            }
        }
        return drawables;
    }

    private static Vector4 Premultiplied(Vector4 colour, float opacity)
    {
        float a = colour.W * opacity;
        return new Vector4(colour.X * a, colour.Y * a, colour.Z * a, a);
    }

    // ---- Fills -------------------------------------------------------------------------------------------------------

    private sealed class Filled(VectorShape shape, VectorFill fill) : Drawable
    {
        private readonly Outline _outline = new(shape.Path, shape.Transform, fill.Rule);
        private readonly Func<Vector2, Vector4> _paint = Paint(fill.Paint, shape.Transform);

        public override Vector4 At(Vector2 p) => _outline.Contains(p) ? Premultiplied(_paint(p), fill.Opacity) : default;
    }

    /// <summary>The inside of a path, by a fill rule, in the coordinates its transform maps to.</summary>
    private sealed class Outline
    {
        private readonly List<List<Vector2>> _polygons;
        private readonly FillRule _rule;
        private readonly float _left = float.MaxValue, _top = float.MaxValue, _right = float.MinValue, _bottom = float.MinValue;

        public Outline(VectorPath path, Matrix3x2 transform, FillRule rule)
        {
            _polygons = SvgTestSupport.Flatten(path, transform);
            _rule = rule;
            foreach (Vector2 p in _polygons.SelectMany(polygon => polygon))
            {
                (_left, _right, _top, _bottom) = (Math.Min(_left, p.X), Math.Max(_right, p.X), Math.Min(_top, p.Y), Math.Max(_bottom, p.Y));
            }
        }

        public bool Contains(Vector2 p)
        {
            if (p.X < _left || p.X > _right || p.Y < _top || p.Y > _bottom) return false;
            int winding = 0;
            foreach (List<Vector2> polygon in _polygons) winding += Winding(polygon, p);
            return _rule == FillRule.NonZero ? winding != 0 : (winding & 1) != 0;
        }
    }

    /// <summary>How many times the closed polygon winds round the point, signed.</summary>
    private static int Winding(List<Vector2> polygon, Vector2 p)
    {
        int winding = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            float side = (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);
            if (a.Y <= p.Y)
            {
                if (b.Y > p.Y && side > 0) winding++;
            }
            else if (b.Y <= p.Y && side < 0)
            {
                winding--;
            }
        }
        return winding;
    }

    // ---- Strokes -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A stroke, tested in the shape's own coordinates (so that a transform that is not uniform distorts it, as it
    /// should): a point is in the stroke when it is within half the width of a segment, between the segment's ends, or
    /// in one of the pieces that fill a corner or finish an end.
    /// </summary>
    private sealed class Stroked : Drawable
    {
        private readonly VectorStroke _stroke;
        private readonly Func<Vector2, Vector4> _paint;
        private readonly Matrix3x2 _toLocal;
        private readonly List<(List<Vector2> Points, bool Closed)> _lines = [];
        private readonly float _half;
        private readonly float _left = float.MaxValue, _top = float.MaxValue, _right = float.MinValue, _bottom = float.MinValue;

        public Stroked(VectorShape shape, VectorStroke stroke)
        {
            _stroke = stroke;
            _half = stroke.Width / 2;
            _paint = Paint(stroke.Paint, shape.Transform);
            if (!Matrix3x2.Invert(shape.Transform, out _toLocal)) return;

            foreach ((List<Vector2> points, bool closed) in Lines(shape.Path))
            {
                if (stroke.Dashes.IsDefaultOrEmpty) _lines.Add((points, closed));
                else Dash(points, closed, stroke.Dashes, stroke.DashOffset, _lines);
            }
            float reach = _half * Math.Max(Math.Max(stroke.MiterLimit, 1.5f), 1.5f);
            foreach (Vector2 p in _lines.SelectMany(line => line.Points))
            {
                (_left, _right, _top, _bottom) = (Math.Min(_left, p.X - reach), Math.Max(_right, p.X + reach), Math.Min(_top, p.Y - reach), Math.Max(_bottom, p.Y + reach));
            }
        }

        public override Vector4 At(Vector2 p)
        {
            Vector2 q = Vector2.Transform(p, _toLocal);
            if (q.X < _left || q.X > _right || q.Y < _top || q.Y > _bottom) return default;
            foreach ((List<Vector2> points, bool closed) in _lines)
            {
                if (InLine(points, closed, q)) return Premultiplied(_paint(p), _stroke.Opacity);
            }
            return default;
        }

        private bool InLine(List<Vector2> points, bool closed, Vector2 q)
        {
            // The distinct points; a subpath that never leaves its start is a dot, drawn by its caps alone.
            var v = new List<Vector2>(points.Count);
            foreach (Vector2 point in points)
            {
                if (v.Count == 0 || point != v[^1]) v.Add(point);
            }
            if (closed && v.Count > 1 && v[^1] == v[0]) v.RemoveAt(v.Count - 1);
            if (v.Count == 1)
            {
                Vector2 d = q - v[0];
                return _stroke.Cap switch
                {
                    LineCap.Round => d.Length() <= _half,
                    LineCap.Square => Math.Abs(d.X) <= _half && Math.Abs(d.Y) <= _half,
                    _ => false,
                };
            }
            if (v.Count == 0) return false;

            int segments = closed ? v.Count : v.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % v.Count], along = b - a;
                float length = along.Length();
                Vector2 unit = along / length, to = q - a;
                float t = Vector2.Dot(to, unit), across = Math.Abs(unit.X * to.Y - unit.Y * to.X);
                if (t >= 0 && t <= length && across <= _half) return true;
            }

            // Corners: every inner vertex, and the start too when the subpath is closed.
            for (int i = closed ? 0 : 1; i < (closed ? v.Count : v.Count - 1); i++)
            {
                Vector2 before = v[(i - 1 + v.Count) % v.Count], at = v[i], after = v[(i + 1) % v.Count];
                if (InJoin(Vector2.Normalize(at - before), Vector2.Normalize(after - at), at, q)) return true;
            }

            if (!closed)
            {
                if (InCap(v[0], Vector2.Normalize(v[0] - v[1]), q) || InCap(v[^1], Vector2.Normalize(v[^1] - v[^2]), q)) return true;
            }
            return false;
        }

        /// <summary>The piece that fills the outside of a corner at <paramref name="at"/>, arriving along <paramref name="d1"/> and leaving along <paramref name="d2"/>.</summary>
        private bool InJoin(Vector2 d1, Vector2 d2, Vector2 at, Vector2 q)
        {
            if (_stroke.Join == LineJoin.Round) return (q - at).Length() <= _half;
            float turn = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(turn) < 1e-6f) return false;      // straight on, or straight back: nothing to fill
            // The outside of the corner is the side away from the turn.
            float side = turn > 0 ? -1 : 1;
            Vector2 n1 = new Vector2(-d1.Y, d1.X) * side, n2 = new Vector2(-d2.Y, d2.X) * side;
            Vector2 o1 = at + n1 * _half, o2 = at + n2 * _half;
            if (InTriangle(q, at, o1, o2)) return true;
            if (_stroke.Join != LineJoin.Miter) return false;
            // Miter length over stroke width is 1 / sin(half the angle between the segments).
            float cos = Vector2.Dot(d1, d2);
            float ratio = MathF.Sqrt(2f / MathF.Max(1e-6f, 1f + cos));
            if (ratio > _stroke.MiterLimit) return false;
            Vector2 tip = at + (n1 + n2) * (_half / (1 + Vector2.Dot(n1, n2)));
            return InTriangle(q, o1, tip, o2);
        }

        /// <summary>What finishes an open end at <paramref name="at"/>, which points outward along <paramref name="outward"/>.</summary>
        private bool InCap(Vector2 at, Vector2 outward, Vector2 q)
        {
            Vector2 to = q - at;
            switch (_stroke.Cap)
            {
                case LineCap.Round:
                    return to.Length() <= _half;
                case LineCap.Square:
                {
                    float t = Vector2.Dot(to, outward), across = Math.Abs(outward.X * to.Y - outward.Y * to.X);
                    return t >= 0 && t <= _half && across <= _half;
                }
                default:
                    return false;
            }
        }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
            bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
            return !(negative && positive);

            static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
        }

        /// <summary>The path as polylines in its own coordinates, each open or closed as the path says.</summary>
        private static List<(List<Vector2> Points, bool Closed)> Lines(VectorPath path)
        {
            const int Pieces = 24;
            var lines = new List<(List<Vector2>, bool)>();
            List<Vector2>? current = null;
            ReadOnlySpan<float> c = path.Coordinates;
            Vector2 last = default, start = default;
            int i = 0;
            foreach (PathVerb verb in path.Verbs)
            {
                switch (verb)
                {
                    case PathVerb.Move:
                        if (current is not null) lines.Add((current, false));
                        last = start = new Vector2(c[i], c[i + 1]);
                        current = [last];
                        break;
                    case PathVerb.Line:
                        last = new Vector2(c[i], c[i + 1]);
                        current!.Add(last);
                        break;
                    case PathVerb.Quad:
                    {
                        Vector2 p0 = last, p1 = new(c[i], c[i + 1]), p2 = new(c[i + 2], c[i + 3]);
                        for (int k = 1; k <= Pieces; k++)
                        {
                            float t = k / (float)Pieces, u = 1 - t;
                            current!.Add(u * u * p0 + 2 * u * t * p1 + t * t * p2);
                        }
                        last = p2;
                        break;
                    }
                    case PathVerb.Cubic:
                    {
                        Vector2 p0 = last, p1 = new(c[i], c[i + 1]), p2 = new(c[i + 2], c[i + 3]), p3 = new(c[i + 4], c[i + 5]);
                        for (int k = 1; k <= Pieces; k++)
                        {
                            float t = k / (float)Pieces, u = 1 - t;
                            current!.Add(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3);
                        }
                        last = p3;
                        break;
                    }
                    default:
                        if (current is not null) lines.Add((current, true));
                        // What follows a closepath without a moveto starts where the subpath started.
                        current = null;
                        last = start;
                        break;
                }
                i += VectorPath.CoordinateCount(verb);
            }
            if (current is not null) lines.Add((current, false));
            return lines;
        }

        /// <summary>Cuts a polyline into its dashes: the stretches where the pattern, started <paramref name="offset"/> in, is "on".</summary>
        private static void Dash(List<Vector2> points, bool closed, ImmutableArray<float> pattern, float offset, List<(List<Vector2>, bool)> output)
        {
            float total = pattern.Sum();
            if (!(total > 0))
            {
                output.Add((points, closed));
                return;
            }
            var path = new List<Vector2>(points);
            if (closed) path.Add(points[0]);

            // Where in the pattern the line starts.
            float phase = offset % total;
            if (phase < 0) phase += total;
            int index = 0;
            while (phase >= pattern[index])
            {
                phase -= pattern[index];
                index = (index + 1) % pattern.Length;
            }
            float left = pattern[index] - phase;
            bool on = index % 2 == 0;
            List<Vector2>? dash = on ? [path[0]] : null;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector2 a = path[i], b = path[i + 1];
                float length = (b - a).Length(), done = 0;
                while (length - done > left)
                {
                    done += left;
                    Vector2 cut = a + (b - a) * (done / length);
                    if (on)
                    {
                        dash!.Add(cut);
                        output.Add((dash, false));
                        dash = null;
                    }
                    else
                    {
                        dash = [cut];
                    }
                    on = !on;
                    index = (index + 1) % pattern.Length;
                    left = pattern[index];
                    // A dash or gap of no length would never be got past.
                    if (left <= 0) left = 1e-6f;
                }
                left -= length - done;
                if (on) dash!.Add(b);
            }
            if (on && dash is { Count: > 0 }) output.Add((dash, false));
        }
    }

    // ---- Layers ------------------------------------------------------------------------------------------------------

    private sealed class Layer(VectorGroup group) : Drawable
    {
        private readonly List<Drawable> _children = Compile(group.Children);
        private readonly Region? _clip = group.Clip is { } clip ? new Region(clip) : null;
        private readonly List<Drawable>? _mask = group.Mask is { } mask ? Compile(mask.Children) : null;

        public override Vector4 At(Vector2 p)
        {
            if (_clip is not null && !_clip.Contains(p)) return default;
            Vector4 colour = SvgPlainRasteriser.At(_children, p);
            if (colour.W == 0) return default;
            float factor = group.Opacity;
            if (_mask is not null)
            {
                // The mask's picture at this point, premultiplied, so its colour already carries its alpha.
                Vector4 m = SvgPlainRasteriser.At(_mask, p);
                factor *= group.Mask!.Kind == VectorMaskKind.Alpha ? m.W : 0.2125f * m.X + 0.7154f * m.Y + 0.0721f * m.Z;
            }
            return colour * factor;
        }
    }

    /// <summary>A clip: the union of its outlines, each cut by its own clip, all of it cut by the one it intersects.</summary>
    private sealed class Region(VectorClip clip)
    {
        private readonly (Outline Outline, Region? Clip)[] _shapes =
            [.. clip.Shapes.Select(shape => (new Outline(shape.Path, shape.Transform, shape.Rule), shape.Clip is { } inner ? new Region(inner) : null))];
        private readonly Region? _intersect = clip.Intersect is { } intersect ? new Region(intersect) : null;

        public bool Contains(Vector2 p)
        {
            if (_intersect is not null && !_intersect.Contains(p)) return false;
            foreach ((Outline outline, Region? inner) in _shapes)
            {
                if (outline.Contains(p) && (inner is null || inner.Contains(p))) return true;
            }
            return false;
        }
    }

    // ---- Pictures ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// An image, decoded here by ImageMagick (the scene carries the file, not pixels) and sampled between its four
    /// nearest pixels. A picture the scene only names is not drawn.
    /// </summary>
    private sealed class Picture : Drawable
    {
        private readonly VectorImage _image;
        private readonly Matrix3x2 _toLocal;
        private readonly float[]? _pixels;      // RGBA, 0 to 1, not premultiplied
        private readonly int _width, _height;
        private readonly float _originX, _originY, _scaleX, _scaleY;

        public Picture(VectorImage image)
        {
            _image = image;
            if (image.Data.IsEmpty || !Matrix3x2.Invert(image.Transform, out _toLocal)) return;
            using var decoded = new MagickImage(image.Data.ToArray());
            decoded.ColorSpace = ColorSpace.sRGB;
            _width = (int)decoded.Width;
            _height = (int)decoded.Height;
            using IPixelCollection<float> pixels = decoded.GetPixels();
            byte[] bytes = pixels.ToByteArray(PixelMapping.RGBA)!;
            _pixels = [.. bytes.Select(b => b / 255f)];

            VectorRect box = image.Bounds;
            (_scaleX, _scaleY) = (box.Width / _width, box.Height / _height);
            if (image.Fit != ImageFit.Stretch)
            {
                _scaleX = _scaleY = image.Fit == ImageFit.Slice ? Math.Max(_scaleX, _scaleY) : Math.Min(_scaleX, _scaleY);
            }
            _originX = box.X + image.AlignX * (box.Width - _width * _scaleX);
            _originY = box.Y + image.AlignY * (box.Height - _height * _scaleY);
        }

        public override Vector4 At(Vector2 p)
        {
            if (_pixels is null) return default;
            Vector2 q = Vector2.Transform(p, _toLocal);
            VectorRect box = _image.Bounds;
            if (q.X < box.X || q.X >= box.Right || q.Y < box.Y || q.Y >= box.Bottom) return default;
            float u = (q.X - _originX) / _scaleX, v = (q.Y - _originY) / _scaleY;
            if (u < 0 || u >= _width || v < 0 || v >= _height) return default;

            // Pixel centres are at half-integers; between four of them, a blend of premultiplied colours.
            float fx = Math.Clamp(u - 0.5f, 0, _width - 1), fy = Math.Clamp(v - 0.5f, 0, _height - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, _width - 1), y1 = Math.Min(y0 + 1, _height - 1);
            float tx = fx - x0, ty = fy - y0;
            Vector4 top = Vector4.Lerp(Pixel(x0, y0), Pixel(x1, y0), tx), bottom = Vector4.Lerp(Pixel(x0, y1), Pixel(x1, y1), tx);
            return Vector4.Lerp(top, bottom, ty) * _image.Opacity;
        }

        private Vector4 Pixel(int x, int y)
        {
            int i = (y * _width + x) * 4;
            return Premultiplied(new Vector4(_pixels![i], _pixels[i + 1], _pixels[i + 2], _pixels[i + 3]), 1);
        }
    }

    // ---- Paints ------------------------------------------------------------------------------------------------------

    /// <summary>The paint as a function of a point of the picture: RGBA, 0 to 1, not premultiplied.</summary>
    private static Func<Vector2, Vector4> Paint(VectorPaint paint, Matrix3x2 shapeTransform)
    {
        switch (paint)
        {
            case SolidPaint solid:
            {
                Vector4 colour = ToVector(solid.Color);
                return _ => colour;
            }
            case LinearGradientPaint linear:
            {
                // Gradient space -> the shape's coordinates -> the picture; a point of the picture goes back the other way.
                if (!Matrix3x2.Invert(linear.Transform * shapeTransform, out Matrix3x2 toGradient)) throw new InvalidOperationException("A gradient transform with no inverse.");
                Vector2 start = new(linear.X1, linear.Y1), along = new Vector2(linear.X2, linear.Y2) - start;
                float lengthSquared = along.LengthSquared();
                return p => ColourAt(linear.Stops, linear.Extend, Vector2.Dot(Vector2.Transform(p, toGradient) - start, along) / lengthSquared);
            }
            case RadialGradientPaint radial:
            {
                if (!Matrix3x2.Invert(radial.Transform * shapeTransform, out Matrix3x2 toGradient)) throw new InvalidOperationException("A gradient transform with no inverse.");
                return p => TwoCircles(Vector2.Transform(p, toGradient), radial) is { } t
                    ? ColourAt(radial.Stops, radial.Extend, t)
                    : default;
            }
            case PatternPaint pattern:
            {
                // Pattern space -> the shape's coordinates -> the picture, and back; then into the one tile, whose
                // scene is asked for its colour there. What the tile's content draws outside the tile is never asked
                // for, which is the clip to the tile.
                if (!Matrix3x2.Invert(pattern.Transform * shapeTransform, out Matrix3x2 toPattern)) throw new InvalidOperationException("A pattern transform with no inverse.");
                List<Drawable> tile = Compile(pattern.Children);
                VectorRect t = pattern.Tile;
                return p =>
                {
                    Vector2 q = Vector2.Transform(p, toPattern);
                    float x = t.X + Wrap(q.X - t.X, t.Width), y = t.Y + Wrap(q.Y - t.Y, t.Height);
                    Vector4 c = At(tile, new Vector2(x, y));
                    return c.W > 0 ? new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, c.W) : default;
                };

                static float Wrap(float value, float period) => value - MathF.Floor(value / period) * period;
            }
            default:
                throw new NotSupportedException(paint.GetType().Name);
        }
    }

    private static Vector4 ToVector(Rgba32 c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    /// <summary>
    /// Where a point is between the two circles: the largest t for which it lies on the circle with centre
    /// lerp(c0, c1, t) and radius lerp(r0, r1, t) >= 0. Null where no such circle passes through it.
    /// </summary>
    private static float? TwoCircles(Vector2 p, RadialGradientPaint g)
    {
        Vector2 c0 = new(g.StartX, g.StartY), cd = new Vector2(g.EndX, g.EndY) - c0, pd = p - c0;
        double r0 = g.StartRadius, dr = g.EndRadius - g.StartRadius;
        // (pd - t cd)^2 = (r0 + t dr)^2  ->  a t^2 - 2 b t + c = 0
        double a = Vector2.Dot(cd, cd) - dr * dr, b = Vector2.Dot(pd, cd) + r0 * dr, c = Vector2.Dot(pd, pd) - r0 * r0;
        // The focal point on the end circle makes this linear; in floats "on" is only nearly so.
        if (Math.Abs(a) < 1e-6 * (Vector2.Dot(cd, cd) + dr * dr))
        {
            if (Math.Abs(b) < 1e-12) return null;
            double only = c / (2 * b);
            return r0 + only * dr >= 0 ? (float)only : null;
        }
        double discriminant = b * b - a * c;
        if (discriminant < 0) return null;
        double root = Math.Sqrt(discriminant), t1 = (b + root) / a, t2 = (b - root) / a;
        double larger = Math.Max(t1, t2), smaller = Math.Min(t1, t2);
        if (r0 + larger * dr >= 0) return (float)larger;
        if (r0 + smaller * dr >= 0) return (float)smaller;
        return null;
    }

    /// <summary>The colour at an offset: the extend rule first, then a straight blend between the two stops around it.</summary>
    private static Vector4 ColourAt(ImmutableArray<GradientStop> stops, GradientExtend extend, float t)
    {
        switch (extend)
        {
            case GradientExtend.Repeat:
                t -= MathF.Floor(t);
                break;
            case GradientExtend.Reflect:
                t = MathF.Abs(t) % 2;
                if (t > 1) t = 2 - t;
                break;
            default:
                t = Math.Clamp(t, 0, 1);
                break;
        }
        if (t < stops[0].Offset) return ToVector(stops[0].Color);
        for (int i = 1; i < stops.Length; i++)
        {
            if (t >= stops[i].Offset) continue;
            float span = stops[i].Offset - stops[i - 1].Offset;
            return Vector4.Lerp(ToVector(stops[i - 1].Color), ToVector(stops[i].Color), (t - stops[i - 1].Offset) / span);
        }
        return ToVector(stops[^1].Color);
    }
}
