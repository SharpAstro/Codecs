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
