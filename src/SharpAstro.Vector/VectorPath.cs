namespace SharpAstro.Vector;

/// <summary>
/// Receives a path as absolute segments. The same five calls a glyph outline is made of, so one renderer
/// can take a font's outlines and a scene's shapes through the same entry point.
/// </summary>
public interface IPathSink
{
    /// <summary>Starts a new subpath at a point.</summary>
    void MoveTo(float x, float y);

    /// <summary>A straight segment from the current point.</summary>
    void LineTo(float x, float y);

    /// <summary>A quadratic Bezier segment from the current point.</summary>
    void QuadTo(float cx, float cy, float x, float y);

    /// <summary>A cubic Bezier segment from the current point.</summary>
    void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y);

    /// <summary>Closes the current subpath with a straight segment back to its start.</summary>
    void Close();
}

/// <summary>One step of a <see cref="VectorPath"/>.</summary>
public enum PathVerb : byte
{
    /// <summary>Start a subpath: 2 coordinates.</summary>
    Move,
    /// <summary>Straight segment: 2 coordinates.</summary>
    Line,
    /// <summary>Quadratic Bezier: 4 coordinates (control, end).</summary>
    Quad,
    /// <summary>Cubic Bezier: 6 coordinates (control 1, control 2, end).</summary>
    Cubic,
    /// <summary>Close the subpath: no coordinates.</summary>
    Close,
}

/// <summary>
/// An immutable path: verbs and the absolute coordinates they consume, in order. Every subpath starts
/// with a <see cref="PathVerb.Move"/>. Coordinates are in the units of whatever holds the path
/// (a <see cref="VectorShape"/>'s local space, before its transform).
/// </summary>
public sealed class VectorPath
{
    private readonly PathVerb[] _verbs;
    private readonly float[] _coordinates;

    /// <summary>A path with nothing in it.</summary>
    public static VectorPath Empty { get; } = new([], []);

    internal VectorPath(PathVerb[] verbs, float[] coordinates)
    {
        _verbs = verbs;
        _coordinates = coordinates;
    }

    /// <summary>The verbs, in drawing order.</summary>
    public ReadOnlySpan<PathVerb> Verbs => _verbs;

    /// <summary>The coordinates the verbs consume, as x, y pairs in order (see <see cref="CoordinateCount"/>).</summary>
    public ReadOnlySpan<float> Coordinates => _coordinates;

    /// <summary>True when the path has no segment at all: nothing, or only <see cref="PathVerb.Move"/>s.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (PathVerb verb in _verbs)
            {
                if (verb != PathVerb.Move) return false;
            }
            return true;
        }
    }

    /// <summary>How many coordinates (not points) a verb consumes: 2, 2, 4, 6, 0.</summary>
    public static int CoordinateCount(PathVerb verb) => verb switch
    {
        PathVerb.Move or PathVerb.Line => 2,
        PathVerb.Quad => 4,
        PathVerb.Cubic => 6,
        _ => 0,
    };

    /// <summary>
    /// The smallest rectangle that holds the path's segments: the curves themselves, not their control points. False when
    /// the path has no segment. A subpath that is only a move does not count.
    /// </summary>
    public bool TryGetBounds(out VectorRect bounds)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        ReadOnlySpan<float> c = _coordinates;
        Span<double> roots = stackalloc double[4];
        double x = 0, y = 0;
        int i = 0;
        foreach (PathVerb verb in _verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    x = c[i];
                    y = c[i + 1];
                    break;
                case PathVerb.Line:
                    Include(x, y);
                    x = c[i];
                    y = c[i + 1];
                    Include(x, y);
                    break;
                case PathVerb.Quad:
                {
                    double x1 = c[i], y1 = c[i + 1], x2 = c[i + 2], y2 = c[i + 3];
                    Include(x, y);
                    Include(x2, y2);
                    // Where the derivative is zero on each axis: t = (p0 - p1) / (p0 - 2 p1 + p2).
                    double dx = x - 2 * x1 + x2, dy = y - 2 * y1 + y2;
                    if (dx != 0 && (x - x1) / dx is > 0 and < 1 and var tx) Include(QuadAt(x, x1, x2, tx), QuadAt(y, y1, y2, tx));
                    if (dy != 0 && (y - y1) / dy is > 0 and < 1 and var ty) Include(QuadAt(x, x1, x2, ty), QuadAt(y, y1, y2, ty));
                    x = x2;
                    y = y2;
                    break;
                }
                case PathVerb.Cubic:
                {
                    double x1 = c[i], y1 = c[i + 1], x2 = c[i + 2], y2 = c[i + 3], x3 = c[i + 4], y3 = c[i + 5];
                    Include(x, y);
                    Include(x3, y3);
                    int n = Extrema(x, x1, x2, x3, roots);
                    n += Extrema(y, y1, y2, y3, roots[n..]);
                    for (int k = 0; k < n; k++) Include(CubicAt(x, x1, x2, x3, roots[k]), CubicAt(y, y1, y2, y3, roots[k]));
                    x = x3;
                    y = y3;
                    break;
                }
            }
            i += CoordinateCount(verb);
        }

        bounds = default;
        if (minX > maxX) return false;
        bounds = new VectorRect((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY));
        return true;

        void Include(double px, double py)
        {
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }
    }

    private static double QuadAt(double p0, double p1, double p2, double t) => (1 - t) * (1 - t) * p0 + 2 * (1 - t) * t * p1 + t * t * p2;

    private static double CubicAt(double p0, double p1, double p2, double p3, double t)
    {
        double u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }

    /// <summary>The roots in (0, 1) of a cubic's derivative on one axis: a t^2 + b t + c, up to a factor of 3.</summary>
    private static int Extrema(double p0, double p1, double p2, double p3, Span<double> roots)
    {
        double a = p3 - 3 * p2 + 3 * p1 - p0, b = 2 * (p2 - 2 * p1 + p0), k = p1 - p0;
        int n = 0;
        if (Math.Abs(a) < 1e-12)
        {
            if (b != 0 && -k / b is > 0 and < 1 and var t) roots[n++] = t;
            return n;
        }
        double discriminant = b * b - 4 * a * k;
        if (discriminant < 0) return 0;
        double root = Math.Sqrt(discriminant);
        if ((-b + root) / (2 * a) is > 0 and < 1 and var t1) roots[n++] = t1;
        if ((-b - root) / (2 * a) is > 0 and < 1 and var t2) roots[n++] = t2;
        return n;
    }

    /// <summary>Plays the path into a sink, verb by verb.</summary>
    public void Replay(IPathSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ReadOnlySpan<float> c = _coordinates;
        int i = 0;
        foreach (PathVerb verb in _verbs)
        {
            switch (verb)
            {
                case PathVerb.Move: sink.MoveTo(c[i], c[i + 1]); i += 2; break;
                case PathVerb.Line: sink.LineTo(c[i], c[i + 1]); i += 2; break;
                case PathVerb.Quad: sink.QuadTo(c[i], c[i + 1], c[i + 2], c[i + 3]); i += 4; break;
                case PathVerb.Cubic: sink.CubicTo(c[i], c[i + 1], c[i + 2], c[i + 3], c[i + 4], c[i + 5]); i += 6; break;
                default: sink.Close(); break;
            }
        }
    }
}

/// <summary>Builds a <see cref="VectorPath"/>. It is an <see cref="IPathSink"/>, so anything that can draw into a sink can be captured.</summary>
public sealed class PathBuilder : IPathSink
{
    private readonly List<PathVerb> _verbs = [];
    private readonly List<float> _coordinates = [];

    /// <summary>Verbs added so far.</summary>
    public int VerbCount => _verbs.Count;

    /// <inheritdoc/>
    public void MoveTo(float x, float y)
    {
        _verbs.Add(PathVerb.Move);
        _coordinates.Add(x);
        _coordinates.Add(y);
    }

    /// <inheritdoc/>
    public void LineTo(float x, float y)
    {
        _verbs.Add(PathVerb.Line);
        _coordinates.Add(x);
        _coordinates.Add(y);
    }

    /// <inheritdoc/>
    public void QuadTo(float cx, float cy, float x, float y)
    {
        _verbs.Add(PathVerb.Quad);
        _coordinates.AddRange([cx, cy, x, y]);
    }

    /// <inheritdoc/>
    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        _verbs.Add(PathVerb.Cubic);
        _coordinates.AddRange([c1x, c1y, c2x, c2y, x, y]);
    }

    /// <inheritdoc/>
    public void Close() => _verbs.Add(PathVerb.Close);

    /// <summary>The path built so far. The builder can go on being used; the returned path does not change.</summary>
    public VectorPath ToPath() => _verbs.Count == 0 ? VectorPath.Empty : new VectorPath([.. _verbs], [.. _coordinates]);
}
