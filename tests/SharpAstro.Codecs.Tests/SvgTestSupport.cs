using System.Globalization;
using System.Numerics;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.Vector;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>Builders and readers the SVG tests share.</summary>
internal static class SvgTestSupport
{
    public const string Namespace = "http://www.w3.org/2000/svg";

    /// <summary>A 100 x 100 root, unless <paramref name="root"/> says otherwise, around <paramref name="body"/>.</summary>
    public static string Wrap(string body, string root = "width=\"100\" height=\"100\"") =>
        $"<svg xmlns=\"{Namespace}\" {root}>{body}</svg>";

    public static SvgDocument Doc(string body, string root = "width=\"100\" height=\"100\"", SvgReadOptions? options = null) =>
        SvgReader.Parse(Wrap(body, root), options);

    /// <summary>The scene's one node, which must be a shape.</summary>
    public static VectorShape OnlyShape(string body, string root = "width=\"100\" height=\"100\"") =>
        Doc(body, root).Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();

    /// <summary>A path as text: <c>M x y L x y Q .. C .. Z</c>, numbers to three decimals.</summary>
    public static string Describe(VectorPath path)
    {
        var text = new StringBuilder();
        ReadOnlySpan<float> c = path.Coordinates;
        int i = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(verb switch { PathVerb.Move => 'M', PathVerb.Line => 'L', PathVerb.Quad => 'Q', PathVerb.Cubic => 'C', _ => 'Z' });
            for (int n = VectorPath.CoordinateCount(verb); n > 0; n--)
            {
                double value = Math.Round(c[i++], 3);
                text.Append(' ').Append((value == 0 ? 0 : value).ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        return text.ToString();
    }

    /// <summary>What a <c>d</c> attribute becomes.</summary>
    public static string PathOf(string d) => Describe(OnlyShape($"<path d=\"{d}\"/>").Path);

    public static Vector2 Map(VectorShape shape, float x, float y) => Vector2.Transform(new Vector2(x, y), shape.Transform);

    public static Rgba32 FillColor(VectorShape shape) => shape.Fill.ShouldNotBeNull().Paint.ShouldBeOfType<SolidPaint>().Color;

    public static Rgba32 StrokeColor(VectorShape shape) => shape.Stroke.ShouldNotBeNull().Paint.ShouldBeOfType<SolidPaint>().Color;

    /// <summary>
    /// The path as closed polygons in scene coordinates, curves cut into straight pieces. Every subpath is closed, which
    /// is what filling does with an open one.
    /// </summary>
    public static List<List<Vector2>> Flatten(VectorPath path, Matrix3x2 transform, int pieces = 24)
    {
        var polygons = new List<List<Vector2>>();
        List<Vector2>? current = null;
        ReadOnlySpan<float> c = path.Coordinates;
        Vector2 last = default;
        int i = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    last = new Vector2(c[i], c[i + 1]);
                    current = [Vector2.Transform(last, transform)];
                    polygons.Add(current);
                    break;
                case PathVerb.Line:
                    last = new Vector2(c[i], c[i + 1]);
                    current!.Add(Vector2.Transform(last, transform));
                    break;
                case PathVerb.Quad:
                {
                    Vector2 p0 = last, p1 = new(c[i], c[i + 1]), p2 = new(c[i + 2], c[i + 3]);
                    for (int k = 1; k <= pieces; k++)
                    {
                        float t = k / (float)pieces, u = 1 - t;
                        current!.Add(Vector2.Transform(u * u * p0 + 2 * u * t * p1 + t * t * p2, transform));
                    }
                    last = p2;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Vector2 p0 = last, p1 = new(c[i], c[i + 1]), p2 = new(c[i + 2], c[i + 3]), p3 = new(c[i + 4], c[i + 5]);
                    for (int k = 1; k <= pieces; k++)
                    {
                        float t = k / (float)pieces, u = 1 - t;
                        current!.Add(Vector2.Transform(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3, transform));
                    }
                    last = p3;
                    break;
                }
            }
            i += VectorPath.CoordinateCount(verb);
        }
        return polygons;
    }

    /// <summary>The points of a path's curves and lines in its own coordinates, for checks against a known curve.</summary>
    public static List<Vector2> Sample(VectorPath path) => [.. Flatten(path, Matrix3x2.Identity).SelectMany(polygon => polygon)];
}
