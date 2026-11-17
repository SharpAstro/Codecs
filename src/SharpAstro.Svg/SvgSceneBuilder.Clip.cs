using System.Collections.Immutable;
using System.Numerics;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

// Clipping and masking: what a clip-path or mask property becomes for the element that has it.
internal sealed partial class SvgSceneBuilder
{
    /// <summary>The region with nothing in it. An element clipped to it is not drawn.</summary>
    private static readonly VectorClip NoRegion = new() { Shapes = [] };

    /// <summary>The mask with no picture. An element masked by it is not drawn.</summary>
    private static readonly VectorMask NoPicture = new() { Children = [] };

    private PathBuilder? _boundsScratch;

    /// <summary>Scratch for measuring a turned outline. Made when first needed: most documents clip and mask nothing.</summary>
    private PathBuilder _boundsBuilder => _boundsScratch ??= new PathBuilder();

    /// <summary>
    /// The style an element that is only ever referred to (a <c>clipPath</c>, a <c>mask</c>, a <c>marker</c>, a
    /// <c>pattern</c>) hands to its content: its own properties on top of what it inherits from ITS ancestors in the
    /// document. Nothing comes from the element that refers to it, and <c>display</c> on the way down does not matter.
    /// </summary>
    private SvgStyle InheritedStyle(SvgElement element, in SvgViewport viewport)
    {
        SvgStyle parent = element.Parent is { } above ? InheritedStyle(above, viewport) : SvgStyle.Initial;
        SvgStyle style = ComputeStyle(element, parent, viewport);
        style.Display = true;
        return style;
    }

    /// <summary>Replaces the nodes from <paramref name="first"/> on by one group that clips them to a rectangle of the coordinates <paramref name="matrix"/> maps to the picture.</summary>
    private static void ClipToRectangle(List<VectorNode> nodes, int first, SvgAffine matrix, double x, double y, double w, double h)
    {
        VectorClip clip = VectorClip.Rectangle(new VectorRect((float)x, (float)y, (float)w, (float)h), matrix.ToMatrix());
        ImmutableArray<VectorNode> inside = [.. nodes.Skip(first)];
        nodes.RemoveRange(first, nodes.Count - first);
        nodes.Add(new VectorGroup { Opacity = 1, Clip = clip, Children = inside });
    }

    /// <summary>
    /// The bounding box of what was drawn, in the coordinates <paramref name="user"/> maps to the picture: of the
    /// outlines themselves, not of their strokes, and not cut down by any clip. Null when nothing has a box.
    /// </summary>
    private VectorRect? BoundsOf(List<VectorNode> nodes, SvgAffine user)
    {
        if (!Matrix3x2.Invert(user.ToMatrix(), out Matrix3x2 toUser)) return null;
        var extent = new Extent();
        foreach (VectorNode node in nodes) Include(node, toUser, extent);
        return extent.Any ? new VectorRect(extent.Left, extent.Top, extent.Right - extent.Left, extent.Bottom - extent.Top) : null;
    }

    private sealed class Extent
    {
        public float Left = float.PositiveInfinity, Top = float.PositiveInfinity, Right = float.NegativeInfinity, Bottom = float.NegativeInfinity;
        public bool Any;

        public void Add(Vector2 p)
        {
            (Left, Right) = (MathF.Min(Left, p.X), MathF.Max(Right, p.X));
            (Top, Bottom) = (MathF.Min(Top, p.Y), MathF.Max(Bottom, p.Y));
            Any = true;
        }

        public void Add(VectorRect box, Matrix3x2 map)
        {
            Add(Vector2.Transform(new Vector2(box.X, box.Y), map));
            Add(Vector2.Transform(new Vector2(box.Right, box.Y), map));
            Add(Vector2.Transform(new Vector2(box.X, box.Bottom), map));
            Add(Vector2.Transform(new Vector2(box.Right, box.Bottom), map));
        }
    }

    private void Include(VectorNode node, Matrix3x2 toUser, Extent extent)
    {
        switch (node)
        {
            case VectorGroup group:
                foreach (VectorNode child in group.Children) Include(child, toUser, extent);
                break;

            case VectorImage image:
                extent.Add(image.Bounds, image.Transform * toUser);
                break;

            case VectorShape shape:
            {
                Matrix3x2 map = shape.Transform * toUser;
                VectorPath path = shape.Path;
                if (map.M12 != 0 || map.M21 != 0)
                {
                    // Turned or skewed: the box of the turned outline is not the turned box of the outline. An affine
                    // map of a Bezier curve is the curve of the mapped points, so map the points and measure that.
                    _boundsBuilder.Clear();
                    ReadOnlySpan<float> c = path.Coordinates;
                    int i = 0;
                    foreach (PathVerb verb in path.Verbs)
                    {
                        switch (verb)
                        {
                            case PathVerb.Move:
                            {
                                Vector2 p = Vector2.Transform(new Vector2(c[i], c[i + 1]), map);
                                _boundsBuilder.MoveTo(p.X, p.Y);
                                break;
                            }
                            case PathVerb.Line:
                            {
                                Vector2 p = Vector2.Transform(new Vector2(c[i], c[i + 1]), map);
                                _boundsBuilder.LineTo(p.X, p.Y);
                                break;
                            }
                            case PathVerb.Quad:
                            {
                                Vector2 q = Vector2.Transform(new Vector2(c[i], c[i + 1]), map), p = Vector2.Transform(new Vector2(c[i + 2], c[i + 3]), map);
                                _boundsBuilder.QuadTo(q.X, q.Y, p.X, p.Y);
                                break;
                            }
                            case PathVerb.Cubic:
                            {
                                Vector2 q1 = Vector2.Transform(new Vector2(c[i], c[i + 1]), map), q2 = Vector2.Transform(new Vector2(c[i + 2], c[i + 3]), map);
                                Vector2 p = Vector2.Transform(new Vector2(c[i + 4], c[i + 5]), map);
                                _boundsBuilder.CubicTo(q1.X, q1.Y, q2.X, q2.Y, p.X, p.Y);
                                break;
                            }
                            default:
                                _boundsBuilder.Close();
                                break;
                        }
                        i += VectorPath.CoordinateCount(verb);
                    }
                    path = _boundsBuilder.ToPath();
                    map = Matrix3x2.Identity;
                }
                if (path.TryGetBounds(out VectorRect box)) extent.Add(box, map);
                break;
            }
        }
    }

    /// <summary>Bounding-box units: 0..1 across the box. Null when the box has no width or no height, where they mean nothing.</summary>
    private static SvgAffine? BoxUnits(VectorRect? box) =>
        box is { Width: > 0, Height: > 0 } b ? new SvgAffine(b.Width, 0, 0, b.Height, b.X, b.Y) : null;

    /// <summary>
    /// A <c>clipPath</c> element as a region, for an element whose user space is <paramref name="user"/> and whose
    /// bounding box there is <paramref name="box"/>. What cannot be a region (a box with no area under bounding-box
    /// units, a transform with no inverse, no shapes) is the empty one, and the element is then not drawn. Null is
    /// different: a reference that leads back to a clipPath being worked out is as if it were not written (measured
    /// in a browser: the clip that closes the circle is dropped and the ones around it still clip).
    /// </summary>
    private VectorClip? Clip(SvgElement element, SvgAffine user, VectorRect? box, in SvgViewport viewport, int depth)
    {
        Charge(depth);
        if (!_active.Add(element))
        {
            Issue(SvgIssueKind.Invalid, "clip-path: circular reference");
            return null;
        }
        try
        {
            SvgStyle style = InheritedStyle(element, viewport);
            SvgAffine matrix = user;
            switch (element.Get("clipPathUnits")?.Trim())
            {
                case null or "userSpaceOnUse":
                    break;
                case "objectBoundingBox":
                    if (BoxUnits(box) is not { } units) return NoRegion;
                    matrix *= units;
                    break;
                default:
                    Issue(SvgIssueKind.Invalid, "clipPathUnits");
                    break;
            }
            if (element.Get("transform") is { } transformText)
            {
                if (SvgAffine.TryParse(transformText, out SvgAffine local)) matrix *= local;
                else Issue(SvgIssueKind.Invalid, "transform");
            }
            if (!matrix.IsFinite || matrix.Determinant == 0) return NoRegion;

            var shapes = ImmutableArray.CreateBuilder<VectorClipShape>();
            foreach (SvgElement child in element.Children)
            {
                Charge(depth + 1);
                SvgStyle childStyle = ComputeStyle(child, style, viewport);
                if (!childStyle.Display) continue;
                SvgAffine childMatrix = matrix;
                if (child.Get("transform") is { } childTransform)
                {
                    if (SvgAffine.TryParse(childTransform, out SvgAffine local)) childMatrix *= local;
                    else Issue(SvgIssueKind.Invalid, "transform");
                }
                // A clip on the child is in the child's coordinates, before a use moves its target.
                SvgAffine childUser = childMatrix;

                SvgElement shape = child;
                if (child.Name == "use")
                {
                    // A use may stand for one shape, and for nothing else.
                    if (child.Get("href")?.Trim() is not ['#', .. var id] || !tree.Ids.TryGetValue(id, out SvgElement? target)
                        || target.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
                    {
                        Issue(SvgIssueKind.Invalid, "clipPath: a use of something that is not a shape");
                        continue;
                    }
                    double fontSize = childStyle.FontSize;
                    childMatrix *= new SvgAffine(1, 0, 0, 1, Length(child, "x", viewport.Width, fontSize), Length(child, "y", viewport.Height, fontSize));
                    SvgElement? clipOnUse = childStyle.ClipPath;
                    childStyle = ComputeStyle(target, childStyle, viewport);
                    if (!childStyle.Display) continue;
                    if (childStyle.ClipPath is not null) Issue(SvgIssueKind.Unsupported, "clip-path on a shape that a clipPath uses");
                    childStyle.ClipPath = clipOnUse;
                    if (target.Get("transform") is { } targetTransform)
                    {
                        if (SvgAffine.TryParse(targetTransform, out SvgAffine local)) childMatrix *= local;
                        else Issue(SvgIssueKind.Invalid, "transform");
                    }
                    shape = target;
                }
                else if (child.Name == "text")
                {
                    Issue(SvgIssueKind.Unsupported, "text in a clipPath");
                    continue;
                }
                else if (child.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
                {
                    // Only shapes, text and use make a clip; a group or anything else in here is not looked at.
                    if (!NeverRendered.Contains(child.Name)) Issue(SvgIssueKind.Invalid, $"clipPath: <{child.Name}> is not a shape");
                    continue;
                }

                if (!childStyle.Visible || !childMatrix.IsFinite || childMatrix.Determinant == 0) continue;
                if (!TryGeometry(shape, childStyle, viewport, out VectorPath path, out bool fillable) || !fillable) continue;

                VectorClip? inner = null;
                if (childStyle.ClipPath is { } innerElement)
                {
                    inner = Clip(innerElement, childUser, path.TryGetBounds(out VectorRect own) ? own : null, viewport, depth + 2);
                    if (inner is { Shapes.IsEmpty: true }) continue;
                }
                shapes.Add(new VectorClipShape(path, childMatrix.ToMatrix(), childStyle.ClipRule, inner));
            }
            if (shapes.Count == 0) return NoRegion;

            VectorClip? intersect = null;
            if (style.ClipPath is { } outer)
            {
                intersect = Clip(outer, user, box, viewport, depth + 1);
                if (intersect is { Shapes.IsEmpty: true }) return NoRegion;
            }
            return new VectorClip { Shapes = shapes.ToImmutable(), Intersect = intersect };
        }
        finally
        {
            _active.Remove(element);
        }
    }

    /// <summary>
    /// A <c>mask</c> element as a mask, for an element whose user space is <paramref name="user"/> and whose bounding
    /// box there is <paramref name="box"/>. What cannot be a mask is the one with no picture, and the element is then
    /// not drawn; null is a reference that leads back to a mask being worked out, which is as if it were not written.
    /// </summary>
    private VectorMask? Mask(SvgElement element, SvgAffine user, VectorRect? box, in SvgViewport viewport, int depth)
    {
        Charge(depth);
        if (!_active.Add(element))
        {
            Issue(SvgIssueKind.Invalid, "mask: circular reference");
            return null;
        }
        try
        {
            SvgStyle style = InheritedStyle(element, viewport);
            double fontSize = style.FontSize;

            bool regionInBox = true, contentInBox = false;
            switch (element.Get("maskUnits")?.Trim())
            {
                case null or "objectBoundingBox": break;
                case "userSpaceOnUse": regionInBox = false; break;
                default: Issue(SvgIssueKind.Invalid, "maskUnits"); break;
            }
            switch (element.Get("maskContentUnits")?.Trim())
            {
                case null or "userSpaceOnUse": break;
                case "objectBoundingBox": contentInBox = true; break;
                default: Issue(SvgIssueKind.Invalid, "maskContentUnits"); break;
            }
            SvgAffine? units = BoxUnits(box);
            if ((regionInBox || contentInBox) && units is null) return NoPicture;

            // The area the mask covers: outside it nothing is kept, whatever the content draws there.
            double x, y, w, h;
            if (regionInBox)
            {
                VectorRect b = box!.Value;
                x = b.X + Fraction("x", -0.1) * b.Width;
                y = b.Y + Fraction("y", -0.1) * b.Height;
                w = Fraction("width", 1.2) * b.Width;
                h = Fraction("height", 1.2) * b.Height;
            }
            else
            {
                x = Region("x", -10, viewport.Width);
                y = Region("y", -10, viewport.Height);
                w = Region("width", 120, viewport.Width);
                h = Region("height", 120, viewport.Height);
            }
            if (!(w > 0) || !(h > 0)) return NoPicture;

            var kind = VectorMaskKind.Luminance;
            foreach (SvgDeclaration declaration in Cascade(element))
            {
                if (declaration.Name != "mask-type") continue;
                ReadOnlySpan<char> v = declaration.Value.AsSpan().Trim();
                if (v is "alpha") kind = VectorMaskKind.Alpha;
                else if (v is "luminance") kind = VectorMaskKind.Luminance;
                else Issue(SvgIssueKind.Invalid, "mask-type");
            }

            var content = new List<VectorNode>();
            SvgAffine contentMatrix = contentInBox ? user * units!.Value : user;
            foreach (SvgElement child in element.Children)
            {
                Walk(child, style, contentMatrix, viewport, content, depth + 1);
            }
            if (content.Count == 0) return NoPicture;
            if (ReachesOutside(content, 0, user, x, y, w, h)) ClipToRectangle(content, 0, user, x, y, w, h);

            // A mask property on the mask element itself does nothing (measured in a browser), unlike a clip-path on a clipPath.
            return new VectorMask { Children = [.. content], Kind = kind };

            double Fraction(string attribute, double fallback)
            {
                if (element.Get(attribute) is not { } text) return fallback;
                if (SvgLength.TryParse(text, out SvgLength length)) return length.Unit == SvgUnit.Percent ? length.Value / 100.0 : length.Value;
                Issue(SvgIssueKind.Invalid, attribute);
                return fallback;
            }

            double Region(string attribute, double fallbackPercent, double percentBase)
            {
                if (element.Get(attribute) is { } text)
                {
                    if (SvgLength.TryParse(text, out SvgLength length)) return length.Resolve(fontSize, percentBase);
                    Issue(SvgIssueKind.Invalid, attribute);
                }
                return fallbackPercent * percentBase / 100.0;
            }
        }
        finally
        {
            _active.Remove(element);
        }
    }
}
