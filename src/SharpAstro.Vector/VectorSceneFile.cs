using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpAstro.VectorGraphics;

/// <summary>
/// A <see cref="VectorScene"/> as bytes: a compact binary form that loads by copying arrays.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> A scene is what is left of an SVG once the reading is done: no XML, no CSS, no units,
/// no <c>use</c> to follow. Writing that out, and reading it back, skips everything a reader does. It is a bake and
/// cache format: read an SVG once (at build time, or the first time a file is seen), keep the bytes, and load those
/// from then on.</para>
/// <para><b>It is fast to load. It is not reliably small.</b> Measured on three documents it loads 15 to 50 times
/// faster than the SVG is read and allocates a third to a tenth as much. Its SIZE is 0.6 to 1.6 times the SVG text's:
/// smaller for a sprite whose symbols repeat a shape (equal paths are stored once), larger for a single icon or a big
/// drawing, because a coordinate is four bytes where SVG often writes two characters. Gzipped, the SVG is the smaller
/// of the two in two cases out of three. Use it to save time, not bytes.</para>
/// <para><b>What it is not.</b> An interchange format. Nothing else writes it, the layout belongs to this package, and
/// a file says which version it is so that a later layout can refuse or convert an earlier one. For a file to hand to
/// another program, SVG is the format.</para>
/// <para><b>Layout</b>, little-endian: the magic <c>SAVS</c>, a version, the scene's size; a table of paths (verbs,
/// then coordinates), which the shapes and clips refer to by index, so a path many shapes share is stored once and is one object
/// again after loading; then the nodes, depth first. Version 2 added what version 1 had no place for: a group's clip
/// and mask, images, and pattern paints. Version 3 added text. Files of versions 1 and 2 are still read.</para>
/// <para><b>The bytes are not trusted.</b> Every count is checked against what is left of the input before anything is
/// allocated for it, so a short file cannot ask for a large array; verbs and tags are checked; nesting has a limit.
/// What does not check out is an <see cref="InvalidDataException"/>.</para>
/// </remarks>
public static class VectorSceneFile
{
    /// <summary>The first four bytes of a scene file.</summary>
    public static ReadOnlySpan<byte> Magic => "SAVS"u8;

    /// <summary>The layout this build writes. It reads this one and every earlier one.</summary>
    public const ushort Version = 3;

    /// <summary>The deepest nesting of groups a file may hold.</summary>
    public const int MaxDepth = 256;

    private const byte ShapeTag = 1, GroupTag = 2, ImageTag = 3, TextTag = 4;
    private const byte SpanFill = 1, SpanStroke = 2, SpanFillInBox = 4, SpanStrokeInBox = 8, SpanStrokeFirst = 16, SpanHidden = 32;
    private const ushort NoTextPath = 0xFFFF;
    private const byte SolidTag = 1, LinearTag = 2, RadialTag = 3, PatternTag = 4;
    private const byte ClipFlag = 1, MaskFlag = 2;
    private const ushort NoString = 0xFFFF;

    /// <summary>Whether the bytes start as a scene file does. Says nothing about whether the rest is sound.</summary>
    public static bool IsSceneFile(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    // ---- Writing -------------------------------------------------------------------------------------------------

    /// <summary>The scene as bytes.</summary>
    /// <exception cref="NotSupportedException">The scene holds a kind of node or paint this layout has no place for.</exception>
    public static byte[] Write(VectorScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Paths first, each once, in the order the shapes meet them. "Once" is by what a path holds, not by which
        // object it is: the two hundred symbols of an icon sprite that all start with the same circle store one circle.
        var paths = new List<VectorPath>();
        var indexOf = new Dictionary<VectorPath, int>(SameGeometry.Instance);
        Collect(scene.Nodes, paths, indexOf, 0);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((ushort)0);        // reserved
        writer.Write(scene.Width);
        writer.Write(scene.Height);

        writer.Write(paths.Count);
        foreach (VectorPath path in paths)
        {
            writer.Write(path.Verbs.Length);
            writer.Write(path.Coordinates.Length);
            writer.Write(MemoryMarshal.AsBytes(path.Verbs));
            WriteFloats(writer, path.Coordinates);
        }

        WriteNodes(writer, scene.Nodes, indexOf);
        writer.Flush();
        return stream.ToArray();
    }

    private static void Collect(ImmutableArray<VectorNode> nodes, List<VectorPath> paths, Dictionary<VectorPath, int> indexOf, int depth)
    {
        if (depth > MaxDepth) throw new NotSupportedException($"The scene nests groups more than {MaxDepth} deep.");
        foreach (VectorNode node in nodes)
        {
            if (node is VectorShape shape)
            {
                if (indexOf.TryAdd(shape.Path, paths.Count)) paths.Add(shape.Path);
                if (shape.Fill?.Paint is PatternPaint fillPattern) Collect(fillPattern.Children, paths, indexOf, depth + 1);
                if (shape.Stroke?.Paint is PatternPaint strokePattern) Collect(strokePattern.Children, paths, indexOf, depth + 1);
            }
            else if (node is VectorGroup group)
            {
                Collect(group.Children, paths, indexOf, depth + 1);
                if (group.Clip is { } clip) Collect(clip, paths, indexOf, depth + 1);
                if (group.Mask is { } mask) Collect(mask.Children, paths, indexOf, depth + 1);
            }
            else if (node is VectorText text)
            {
                foreach (VectorTextSpan span in text.Spans)
                {
                    if (span.Path is { } along && indexOf.TryAdd(along.Path, paths.Count)) paths.Add(along.Path);
                    if (span.Fill?.Paint is PatternPaint spanFill) Collect(spanFill.Children, paths, indexOf, depth + 1);
                    if (span.Stroke?.Paint is PatternPaint spanStroke) Collect(spanStroke.Children, paths, indexOf, depth + 1);
                    foreach (VectorTextLine line in span.Lines.IsDefault ? [] : span.Lines)
                    {
                        if (line.Fill?.Paint is PatternPaint lineFill) Collect(lineFill.Children, paths, indexOf, depth + 1);
                        if (line.Stroke?.Paint is PatternPaint lineStroke) Collect(lineStroke.Children, paths, indexOf, depth + 1);
                    }
                }
            }
            else if (node is not VectorImage)
            {
                throw new NotSupportedException($"A scene node of type {node.GetType().Name} cannot be written.");
            }
        }
    }

    private static void Collect(VectorClip clip, List<VectorPath> paths, Dictionary<VectorPath, int> indexOf, int depth)
    {
        if (depth > MaxDepth) throw new NotSupportedException($"The scene nests clips more than {MaxDepth} deep.");
        foreach (VectorClipShape shape in clip.Shapes)
        {
            if (indexOf.TryAdd(shape.Path, paths.Count)) paths.Add(shape.Path);
            if (shape.Clip is { } inner) Collect(inner, paths, indexOf, depth + 1);
        }
        if (clip.Intersect is { } intersect) Collect(intersect, paths, indexOf, depth + 1);
    }

    /// <summary>Two paths are the same when their verbs and coordinates are, bit for bit.</summary>
    private sealed class SameGeometry : IEqualityComparer<VectorPath>
    {
        public static readonly SameGeometry Instance = new();

        public bool Equals(VectorPath? a, VectorPath? b) =>
            ReferenceEquals(a, b) || (a is not null && b is not null
                && a.Verbs.SequenceEqual(b.Verbs)
                && MemoryMarshal.AsBytes(a.Coordinates).SequenceEqual(MemoryMarshal.AsBytes(b.Coordinates)));

        public int GetHashCode(VectorPath path)
        {
            var hash = new HashCode();
            hash.AddBytes(MemoryMarshal.AsBytes(path.Verbs));
            hash.AddBytes(MemoryMarshal.AsBytes(path.Coordinates));
            return hash.ToHashCode();
        }
    }

    private static void WriteFloats(BinaryWriter writer, ReadOnlySpan<float> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            writer.Write(MemoryMarshal.AsBytes(values));
            return;
        }
        foreach (float value in values) writer.Write(value);
    }

    private static void WriteNodes(BinaryWriter writer, ImmutableArray<VectorNode> nodes, Dictionary<VectorPath, int> indexOf)
    {
        writer.Write(nodes.Length);
        foreach (VectorNode node in nodes)
        {
            if (node is VectorGroup group)
            {
                writer.Write(GroupTag);
                WriteString(writer, group.Id);
                writer.Write(group.Opacity);
                writer.Write((byte)((group.Clip is null ? 0 : ClipFlag) | (group.Mask is null ? 0 : MaskFlag)));
                if (group.Clip is { } clip) WriteClip(writer, clip, indexOf);
                if (group.Mask is { } mask)
                {
                    writer.Write((byte)mask.Kind);
                    WriteNodes(writer, mask.Children, indexOf);
                }
                WriteNodes(writer, group.Children, indexOf);
                continue;
            }
            if (node is VectorImage image)
            {
                writer.Write(ImageTag);
                WriteString(writer, image.Id);
                writer.Write(image.Data.Length);
                writer.Write(image.Data.Span);
                WriteString(writer, image.MediaType);
                WriteString(writer, image.Source);
                writer.Write(image.Bounds.X);
                writer.Write(image.Bounds.Y);
                writer.Write(image.Bounds.Width);
                writer.Write(image.Bounds.Height);
                WriteMatrix(writer, image.Transform);
                writer.Write(image.Opacity);
                writer.Write((byte)image.Fit);
                writer.Write(image.AlignX);
                writer.Write(image.AlignY);
                continue;
            }

            if (node is VectorText text)
            {
                WriteText(writer, text, indexOf);
                continue;
            }

            var shape = (VectorShape)node;
            writer.Write(ShapeTag);
            WriteString(writer, shape.Id);
            writer.Write(indexOf[shape.Path]);
            WriteMatrix(writer, shape.Transform);

            writer.Write(shape.Fill is not null);
            if (shape.Fill is { } fill) WriteFill(writer, fill, indexOf);

            writer.Write(shape.Stroke is not null);
            if (shape.Stroke is { } stroke) WriteStroke(writer, stroke, indexOf);
        }
    }

    private static void WriteFill(BinaryWriter writer, VectorFill fill, Dictionary<VectorPath, int> indexOf)
    {
        WritePaint(writer, fill.Paint, indexOf);
        writer.Write(fill.Opacity);
        writer.Write((byte)fill.Rule);
    }

    private static void WriteStroke(BinaryWriter writer, VectorStroke stroke, Dictionary<VectorPath, int> indexOf)
    {
        WritePaint(writer, stroke.Paint, indexOf);
        writer.Write(stroke.Opacity);
        writer.Write(stroke.Width);
        writer.Write((byte)stroke.Cap);
        writer.Write((byte)stroke.Join);
        writer.Write(stroke.MiterLimit);
        ImmutableArray<float> dashes = stroke.Dashes.IsDefault ? [] : stroke.Dashes;
        if (dashes.Length > ushort.MaxValue) throw new NotSupportedException("A dash pattern of more than 65,535 lengths cannot be written.");
        writer.Write((ushort)dashes.Length);
        WriteFloats(writer, dashes.AsSpan());
        writer.Write(stroke.DashOffset);
    }

    /// <summary>
    /// A text node: its matrix and length, the paths its spans are set along (each once, so that spans on one path are on
    /// one object again after loading), then the spans.
    /// </summary>
    private static void WriteText(BinaryWriter writer, VectorText text, Dictionary<VectorPath, int> indexOf)
    {
        writer.Write(TextTag);
        WriteString(writer, text.Id);
        WriteMatrix(writer, text.Transform);
        writer.Write(text.TextLength is not null);
        writer.Write(text.TextLength ?? 0f);
        writer.Write((byte)text.LengthAdjust);

        ImmutableArray<VectorTextSpan> spans = text.Spans.IsDefault ? [] : text.Spans;
        var along = new List<VectorTextPath>();
        foreach (VectorTextSpan span in spans)
        {
            if (span.Path is { } path && !along.Exists(known => ReferenceEquals(known, path))) along.Add(path);
        }
        if (along.Count >= NoTextPath) throw new NotSupportedException("A text set along more than 65,534 paths cannot be written.");
        writer.Write((ushort)along.Count);
        foreach (VectorTextPath path in along)
        {
            writer.Write(indexOf[path.Path]);
            WriteMatrix(writer, path.Transform);
            writer.Write(path.StartOffset);
            writer.Write((byte)((path.StartIsFraction ? 1 : 0) | (path.RightSide ? 2 : 0)));
        }

        // The lines, each once, for the same reason as the paths.
        var lines = new List<VectorTextLine>();
        foreach (VectorTextSpan span in spans)
        {
            foreach (VectorTextLine line in span.Lines.IsDefault ? [] : span.Lines)
            {
                if (!lines.Exists(known => ReferenceEquals(known, line))) lines.Add(line);
            }
        }
        if (lines.Count > ushort.MaxValue) throw new NotSupportedException("A text with more than 65,535 sets of lines cannot be written.");
        writer.Write((ushort)lines.Count);
        foreach (VectorTextLine line in lines)
        {
            writer.Write((byte)line.Kinds);
            writer.Write((byte)((line.Fill is null ? 0 : SpanFill) | (line.Stroke is null ? 0 : SpanStroke)
                | (line.FillInBox ? SpanFillInBox : 0) | (line.StrokeInBox ? SpanStrokeInBox : 0)));
            if (line.Fill is { } lineFill) WriteFill(writer, lineFill, indexOf);
            if (line.Stroke is { } lineStroke) WriteStroke(writer, lineStroke, indexOf);
        }

        writer.Write(spans.Length);
        foreach (VectorTextSpan span in spans)
        {
            byte[] characters = Encoding.UTF8.GetBytes(span.Text);
            writer.Write(characters.Length);
            writer.Write(characters);
            WriteFont(writer, span.Font);

            writer.Write((byte)((span.Fill is null ? 0 : SpanFill) | (span.Stroke is null ? 0 : SpanStroke)
                | (span.FillInBox ? SpanFillInBox : 0) | (span.StrokeInBox ? SpanStrokeInBox : 0)
                | (span.StrokeFirst ? SpanStrokeFirst : 0) | (span.Hidden ? SpanHidden : 0)));
            if (span.Fill is { } fill) WriteFill(writer, fill, indexOf);
            if (span.Stroke is { } stroke) WriteStroke(writer, stroke, indexOf);

            writer.Write((byte)span.Anchor);
            writer.Write((byte)span.Baseline);
            writer.Write(span.BaselineShift);
            writer.Write(span.ScriptRise);
            writer.Write(span.LetterSpacing);
            writer.Write(span.WordSpacing);
            ImmutableArray<VectorTextLine> under = span.Lines.IsDefault ? [] : span.Lines;
            writer.Write((ushort)under.Length);
            foreach (VectorTextLine line in under) writer.Write((ushort)lines.FindIndex(known => ReferenceEquals(known, line)));

            ImmutableArray<VectorTextPosition> positions = span.Positions.IsDefault ? [] : span.Positions;
            writer.Write(positions.Length);
            foreach (VectorTextPosition position in positions)
            {
                writer.Write(position.Index);
                writer.Write((byte)((position.X is null ? 0 : 1) | (position.Y is null ? 0 : 2)));
                writer.Write(position.X ?? 0f);
                writer.Write(position.Y ?? 0f);
                writer.Write(position.Dx);
                writer.Write(position.Dy);
                writer.Write(position.Rotate);
            }

            writer.Write(span.Path is { } set ? (ushort)along.FindIndex(known => ReferenceEquals(known, set)) : NoTextPath);
        }
    }

    private static void WriteFont(BinaryWriter writer, VectorFont font)
    {
        ImmutableArray<string> families = font.Families.IsDefault ? [] : font.Families;
        if (families.Length > ushort.MaxValue) throw new NotSupportedException("A font with more than 65,535 family names cannot be written.");
        writer.Write((ushort)families.Length);
        foreach (string family in families) WriteString(writer, family);
        writer.Write(font.Size);
        writer.Write(font.Weight);
        writer.Write((byte)font.Slant);
        writer.Write(font.Stretch);
    }

    private static void WriteClip(BinaryWriter writer, VectorClip clip, Dictionary<VectorPath, int> indexOf)
    {
        ImmutableArray<VectorClipShape> shapes = clip.Shapes.IsDefault ? [] : clip.Shapes;
        writer.Write(shapes.Length);
        foreach (VectorClipShape shape in shapes)
        {
            writer.Write(indexOf[shape.Path]);
            WriteMatrix(writer, shape.Transform);
            writer.Write((byte)shape.Rule);
            writer.Write(shape.Clip is not null);
            if (shape.Clip is { } inner) WriteClip(writer, inner, indexOf);
        }
        writer.Write(clip.Intersect is not null);
        if (clip.Intersect is { } intersect) WriteClip(writer, intersect, indexOf);
    }

    private static void WriteString(BinaryWriter writer, string? text)
    {
        if (text is null)
        {
            writer.Write(NoString);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length >= NoString) throw new NotSupportedException("A text (an id, a media type, an address) longer than 65,534 bytes cannot be written.");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteMatrix(BinaryWriter writer, Matrix3x2 m)
    {
        writer.Write(m.M11);
        writer.Write(m.M12);
        writer.Write(m.M21);
        writer.Write(m.M22);
        writer.Write(m.M31);
        writer.Write(m.M32);
    }

    private static void WritePaint(BinaryWriter writer, VectorPaint paint, Dictionary<VectorPath, int> indexOf)
    {
        switch (paint)
        {
            case SolidPaint solid:
                writer.Write(SolidTag);
                WriteColor(writer, solid.Color);
                break;
            case LinearGradientPaint linear:
                writer.Write(LinearTag);
                writer.Write(linear.X1);
                writer.Write(linear.Y1);
                writer.Write(linear.X2);
                writer.Write(linear.Y2);
                WriteGradient(writer, linear.Stops, linear.Extend, linear.Transform);
                break;
            case RadialGradientPaint radial:
                writer.Write(RadialTag);
                writer.Write(radial.StartX);
                writer.Write(radial.StartY);
                writer.Write(radial.StartRadius);
                writer.Write(radial.EndX);
                writer.Write(radial.EndY);
                writer.Write(radial.EndRadius);
                WriteGradient(writer, radial.Stops, radial.Extend, radial.Transform);
                break;
            case PatternPaint pattern:
                writer.Write(PatternTag);
                writer.Write(pattern.Tile.X);
                writer.Write(pattern.Tile.Y);
                writer.Write(pattern.Tile.Width);
                writer.Write(pattern.Tile.Height);
                WriteMatrix(writer, pattern.Transform);
                WriteNodes(writer, pattern.Children.IsDefault ? [] : pattern.Children, indexOf);
                break;
            default:
                throw new NotSupportedException($"A paint of type {paint.GetType().Name} cannot be written.");
        }
    }

    private static void WriteGradient(BinaryWriter writer, ImmutableArray<GradientStop> stops, GradientExtend extend, Matrix3x2 transform)
    {
        writer.Write((byte)extend);
        WriteMatrix(writer, transform);
        if (stops.IsDefault) stops = [];
        if (stops.Length > ushort.MaxValue) throw new NotSupportedException("A gradient of more than 65,535 stops cannot be written.");
        writer.Write((ushort)stops.Length);
        foreach (GradientStop stop in stops)
        {
            writer.Write(stop.Offset);
            WriteColor(writer, stop.Color);
        }
    }

    private static void WriteColor(BinaryWriter writer, Rgba32 color)
    {
        writer.Write(color.R);
        writer.Write(color.G);
        writer.Write(color.B);
        writer.Write(color.A);
    }

    // ---- Reading -------------------------------------------------------------------------------------------------

    /// <summary>The scene the bytes hold.</summary>
    /// <exception cref="InvalidDataException">Not a scene file, a version this build does not read, or damaged.</exception>
    public static VectorScene Read(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        if (!IsSceneFile(data)) throw new InvalidDataException("Not a vector scene file: it does not start with 'SAVS'.");
        reader.Skip(4);
        ushort version = reader.UInt16();
        if (version is 0 or > Version)
        {
            throw new InvalidDataException($"The vector scene file is version {version}; this build reads versions 1 to {Version}.");
        }
        reader.Skip(2);
        float width = reader.Single(), height = reader.Single();

        // Each path is at least its two counts, so the table cannot claim more paths than the bytes could hold.
        int pathCount = reader.Count(minimumBytesEach: 8, "paths");
        var paths = new VectorPath[pathCount];
        for (int i = 0; i < pathCount; i++)
        {
            int verbCount = reader.Count(minimumBytesEach: 1, "path verbs");
            int coordinateCount = reader.Count(minimumBytesEach: 4, "path coordinates");
            ReadOnlySpan<byte> verbBytes = reader.Bytes(verbCount);
            int needed = 0;
            foreach (byte verb in verbBytes)
            {
                if (verb > (byte)PathVerb.Close) throw new InvalidDataException($"The vector scene file has a path verb {verb}, which is not one.");
                needed += VectorPath.CoordinateCount((PathVerb)verb);
            }
            if (needed != coordinateCount)
            {
                throw new InvalidDataException($"The vector scene file has a path whose verbs need {needed} coordinates and which holds {coordinateCount}.");
            }
            var verbs = new PathVerb[verbCount];
            verbBytes.CopyTo(MemoryMarshal.AsBytes(verbs.AsSpan()));
            paths[i] = verbCount == 0 ? VectorPath.Empty : new VectorPath(verbs, reader.Singles(coordinateCount));
        }

        ImmutableArray<VectorNode> nodes = ReadNodes(ref reader, paths, 0, version);
        if (!reader.AtEnd) throw new InvalidDataException("The vector scene file has bytes after its last node.");
        return new VectorScene(width, height, nodes);
    }

    private static ImmutableArray<VectorNode> ReadNodes(ref Reader reader, VectorPath[] paths, int depth, ushort version)
    {
        // Groups, masks and pattern tiles all nest nodes, and all count.
        if (depth > MaxDepth) throw new InvalidDataException($"The vector scene file nests groups more than {MaxDepth} deep.");
        // The smallest node is a group with no id and no children: a tag, a string length, an opacity, a count.
        int count = reader.Count(minimumBytesEach: 11, "nodes");
        var nodes = ImmutableArray.CreateBuilder<VectorNode>(count);
        for (int i = 0; i < count; i++)
        {
            byte tag = reader.Byte();
            string? id = reader.String();
            if (tag == GroupTag)
            {
                float opacity = reader.Single();
                VectorClip? clip = null;
                VectorMask? mask = null;
                if (version >= 2)
                {
                    byte flags = reader.Byte();
                    if ((flags & ~(ClipFlag | MaskFlag)) != 0) throw new InvalidDataException($"The vector scene file has group flags of {flags}.");
                    if ((flags & ClipFlag) != 0) clip = ReadClip(ref reader, paths, depth + 1);
                    if ((flags & MaskFlag) != 0)
                    {
                        VectorMaskKind kind = reader.Enum<VectorMaskKind>((byte)VectorMaskKind.Alpha, "mask kind");
                        mask = new VectorMask { Kind = kind, Children = ReadNodes(ref reader, paths, depth + 1, version) };
                    }
                }
                nodes.Add(new VectorGroup { Id = id, Opacity = opacity, Clip = clip, Mask = mask, Children = ReadNodes(ref reader, paths, depth + 1, version) });
                continue;
            }
            if (tag == ImageTag && version >= 2)
            {
                // The picture's bytes are copied out: the scene must not keep the caller's buffer alive, or be changed through it.
                int length = reader.Count(minimumBytesEach: 1, "image bytes");
                byte[] data = reader.Bytes(length).ToArray();
                string? mediaType = reader.String(), source = reader.String();
                var bounds = new VectorRect(reader.Single(), reader.Single(), reader.Single(), reader.Single());
                Matrix3x2 imageTransform = reader.Matrix();
                float imageOpacity = reader.Single();
                ImageFit fit = reader.Enum<ImageFit>((byte)ImageFit.Slice, "image fit");
                float alignX = reader.Single(), alignY = reader.Single();
                nodes.Add(new VectorImage
                {
                    Id = id, Data = data, MediaType = mediaType, Source = source, Bounds = bounds, Transform = imageTransform,
                    Opacity = imageOpacity, Fit = fit, AlignX = alignX, AlignY = alignY,
                });
                continue;
            }
            if (tag == TextTag && version >= 3)
            {
                nodes.Add(ReadText(ref reader, paths, depth, version, id));
                continue;
            }
            if (tag != ShapeTag) throw new InvalidDataException($"The vector scene file has a node tag {tag}, which is not one.");

            int pathIndex = reader.PathIndex(paths);
            Matrix3x2 transform = reader.Matrix();
            VectorFill? fill = reader.Boolean() ? ReadFill(ref reader, paths, depth, version) : null;
            VectorStroke? stroke = reader.Boolean() ? ReadStroke(ref reader, paths, depth, version) : null;
            nodes.Add(new VectorShape { Id = id, Path = paths[pathIndex], Transform = transform, Fill = fill, Stroke = stroke });
        }
        return nodes.MoveToImmutable();
    }

    private static VectorFill ReadFill(ref Reader reader, VectorPath[] paths, int depth, ushort version)
    {
        VectorPaint paint = ReadPaint(ref reader, paths, depth, version);
        float opacity = reader.Single();
        return new VectorFill(paint, opacity, reader.Enum<FillRule>((byte)FillRule.EvenOdd, "fill rule"));
    }

    private static VectorStroke ReadStroke(ref Reader reader, VectorPath[] paths, int depth, ushort version)
    {
        VectorPaint paint = ReadPaint(ref reader, paths, depth, version);
        float opacity = reader.Single(), strokeWidth = reader.Single();
        LineCap cap = reader.Enum<LineCap>((byte)LineCap.Square, "line cap");
        LineJoin join = reader.Enum<LineJoin>((byte)LineJoin.Bevel, "line join");
        float miterLimit = reader.Single();
        int dashCount = reader.UInt16();
        float[] dashes = reader.Singles(dashCount);
        return new VectorStroke(paint, opacity, strokeWidth, cap, join, miterLimit,
            ImmutableCollectionsMarshal.AsImmutableArray(dashes), reader.Single());
    }

    private static VectorFont ReadFont(ref Reader reader)
    {
        int familyCount = reader.UInt16();
        reader.Need((long)familyCount * 2, "font families");
        var families = ImmutableArray.CreateBuilder<string>(familyCount);
        for (int f = 0; f < familyCount; f++)
        {
            families.Add(reader.String() ?? throw new InvalidDataException("The vector scene file has a font family with no name."));
        }
        float size = reader.Single();
        int weight = reader.Int32();
        FontSlant slant = reader.Enum<FontSlant>((byte)FontSlant.Oblique, "font slant");
        return new VectorFont(families.MoveToImmutable(), size, weight, slant, reader.Single());
    }

    private static VectorText ReadText(ref Reader reader, VectorPath[] paths, int depth, ushort version, string? id)
    {
        Matrix3x2 transform = reader.Matrix();
        bool hasLength = reader.Boolean();
        float length = reader.Single();
        TextLengthAdjust adjust = reader.Enum<TextLengthAdjust>((byte)TextLengthAdjust.SpacingAndGlyphs, "text length adjustment");

        // A path to set text along is a path index, a matrix, an offset and a flag byte.
        int alongCount = reader.UInt16();
        reader.Need((long)alongCount * 33, "text paths");
        var along = new VectorTextPath[alongCount];
        for (int i = 0; i < alongCount; i++)
        {
            int pathIndex = reader.PathIndex(paths);
            Matrix3x2 pathTransform = reader.Matrix();
            float start = reader.Single();
            byte flags = reader.Byte();
            if (flags > 3) throw new InvalidDataException($"The vector scene file has text path flags of {flags}.");
            along[i] = new VectorTextPath(paths[pathIndex], pathTransform, start, (flags & 1) != 0, (flags & 2) != 0);
        }

        // A set of lines is at least its kinds and its flags.
        int lineCount = reader.UInt16();
        reader.Need((long)lineCount * 2, "text lines");
        var lines = new VectorTextLine[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            byte kinds = reader.Byte();
            if (kinds > 7) throw new InvalidDataException($"The vector scene file has text decorations of {kinds}.");
            byte lineFlags = reader.Byte();
            if (lineFlags >= 16) throw new InvalidDataException($"The vector scene file has text line flags of {lineFlags}.");
            VectorFill? lineFill = (lineFlags & SpanFill) != 0 ? ReadFill(ref reader, paths, depth, version) : null;
            VectorStroke? lineStroke = (lineFlags & SpanStroke) != 0 ? ReadStroke(ref reader, paths, depth, version) : null;
            lines[i] = new VectorTextLine((TextDecorations)kinds, lineFill, lineStroke,
                (lineFlags & SpanFillInBox) != 0, (lineFlags & SpanStrokeInBox) != 0);
        }

        // The smallest span: a text length, a family count, a size, a weight, a slant, a stretch, flags, an anchor, a
        // baseline, four lengths, a line count, a position count and a path.
        int spanCount = reader.Count(minimumBytesEach: 46, "text spans");
        var spans = ImmutableArray.CreateBuilder<VectorTextSpan>(spanCount);
        for (int i = 0; i < spanCount; i++)
        {
            string characters = Encoding.UTF8.GetString(reader.Bytes(reader.Count(minimumBytesEach: 1, "characters")));
            VectorFont font = ReadFont(ref reader);

            byte spanFlags = reader.Byte();
            if (spanFlags >= 64) throw new InvalidDataException($"The vector scene file has text span flags of {spanFlags}.");
            VectorFill? fill = (spanFlags & SpanFill) != 0 ? ReadFill(ref reader, paths, depth, version) : null;
            VectorStroke? stroke = (spanFlags & SpanStroke) != 0 ? ReadStroke(ref reader, paths, depth, version) : null;

            TextAnchor anchor = reader.Enum<TextAnchor>((byte)TextAnchor.End, "text anchor");
            TextBaseline baseline = reader.Enum<TextBaseline>((byte)TextBaseline.TextBottom, "text baseline");
            float shift = reader.Single(), rise = reader.Single(), letterSpacing = reader.Single(), wordSpacing = reader.Single();
            int underCount = reader.UInt16();
            reader.Need((long)underCount * 2, "a span's lines");
            var under = ImmutableArray.CreateBuilder<VectorTextLine>(underCount);
            for (int u = 0; u < underCount; u++)
            {
                int lineNumber = reader.UInt16();
                if (lineNumber >= lines.Length) throw new InvalidDataException($"The vector scene file puts a span under text line {lineNumber} of {lines.Length}.");
                under.Add(lines[lineNumber]);
            }

            int positionCount = reader.Count(minimumBytesEach: 25, "character positions");
            var positions = ImmutableArray.CreateBuilder<VectorTextPosition>(positionCount);
            int previous = -1;
            for (int p = 0; p < positionCount; p++)
            {
                int index = reader.Int32();
                // In order and inside the text: whoever lays the text out walks both at once.
                if (index <= previous || index >= characters.Length)
                {
                    throw new InvalidDataException($"The vector scene file places character {index} of a span of {characters.Length}, after character {previous}.");
                }
                previous = index;
                byte has = reader.Byte();
                if (has > 3) throw new InvalidDataException($"The vector scene file has character position flags of {has}.");
                float x = reader.Single(), y = reader.Single();
                positions.Add(new VectorTextPosition(index, (has & 1) != 0 ? x : null, (has & 2) != 0 ? y : null, reader.Single(), reader.Single(), reader.Single()));
            }

            ushort pathNumber = reader.UInt16();
            if (pathNumber != NoTextPath && pathNumber >= along.Length)
            {
                throw new InvalidDataException($"The vector scene file sets a span along text path {pathNumber} of {along.Length}.");
            }

            spans.Add(new VectorTextSpan
            {
                Text = characters, Font = font, Fill = fill, Stroke = stroke,
                FillInBox = (spanFlags & SpanFillInBox) != 0, StrokeInBox = (spanFlags & SpanStrokeInBox) != 0,
                StrokeFirst = (spanFlags & SpanStrokeFirst) != 0, Hidden = (spanFlags & SpanHidden) != 0,
                Anchor = anchor, Baseline = baseline, BaselineShift = shift, ScriptRise = rise, LetterSpacing = letterSpacing, WordSpacing = wordSpacing,
                Lines = under.MoveToImmutable(), Positions = positions.MoveToImmutable(),
                Path = pathNumber == NoTextPath ? null : along[pathNumber],
            });
        }
        return new VectorText
        {
            Id = id, Spans = spans.MoveToImmutable(), Transform = transform,
            TextLength = hasLength ? length : null, LengthAdjust = adjust,
        };
    }

    private static VectorClip ReadClip(ref Reader reader, VectorPath[] paths, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"The vector scene file nests clips more than {MaxDepth} deep.");
        // A clip shape is at least a path index, a matrix, a rule and a flag.
        int count = reader.Count(minimumBytesEach: 30, "clip shapes");
        var shapes = ImmutableArray.CreateBuilder<VectorClipShape>(count);
        for (int i = 0; i < count; i++)
        {
            int pathIndex = reader.Int32();
            if ((uint)pathIndex >= (uint)paths.Length)
            {
                throw new InvalidDataException($"The vector scene file refers to path {pathIndex} of {paths.Length}.");
            }
            Matrix3x2 transform = reader.Matrix();
            FillRule rule = reader.Enum<FillRule>((byte)FillRule.EvenOdd, "fill rule");
            VectorClip? inner = reader.Boolean() ? ReadClip(ref reader, paths, depth + 1) : null;
            shapes.Add(new VectorClipShape(paths[pathIndex], transform, rule, inner));
        }
        VectorClip? intersect = reader.Boolean() ? ReadClip(ref reader, paths, depth + 1) : null;
        return new VectorClip { Shapes = shapes.MoveToImmutable(), Intersect = intersect };
    }

    private static VectorPaint ReadPaint(ref Reader reader, VectorPath[] paths, int depth, ushort version)
    {
        byte tag = reader.Byte();
        switch (tag)
        {
            case SolidTag:
                return new SolidPaint(reader.Color());
            case LinearTag:
            {
                float x1 = reader.Single(), y1 = reader.Single(), x2 = reader.Single(), y2 = reader.Single();
                ReadGradient(ref reader, out ImmutableArray<GradientStop> stops, out GradientExtend extend, out Matrix3x2 transform);
                return new LinearGradientPaint(x1, y1, x2, y2, stops, extend, transform);
            }
            case RadialTag:
            {
                float sx = reader.Single(), sy = reader.Single(), sr = reader.Single(), ex = reader.Single(), ey = reader.Single(), er = reader.Single();
                ReadGradient(ref reader, out ImmutableArray<GradientStop> stops, out GradientExtend extend, out Matrix3x2 transform);
                return new RadialGradientPaint(sx, sy, sr, ex, ey, er, stops, extend, transform);
            }
            case PatternTag when version >= 2:
            {
                var tile = new VectorRect(reader.Single(), reader.Single(), reader.Single(), reader.Single());
                Matrix3x2 transform = reader.Matrix();
                return new PatternPaint(ReadNodes(ref reader, paths, depth + 1, version), tile, transform);
            }
            default:
                throw new InvalidDataException($"The vector scene file has a paint tag {tag}, which is not one.");
        }
    }

    private static void ReadGradient(ref Reader reader, out ImmutableArray<GradientStop> stops, out GradientExtend extend, out Matrix3x2 transform)
    {
        extend = reader.Enum<GradientExtend>((byte)GradientExtend.Reflect, "gradient extend");
        transform = reader.Matrix();
        int count = reader.UInt16();
        reader.Need(count * 8, "gradient stops");
        var builder = ImmutableArray.CreateBuilder<GradientStop>(count);
        for (int i = 0; i < count; i++) builder.Add(new GradientStop(reader.Single(), reader.Color()));
        stops = builder.MoveToImmutable();
    }

    /// <summary>A cursor over the input that refuses to run past its end.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly bool AtEnd => _position == _data.Length;

        public readonly void Need(long bytes, string what)
        {
            if (bytes > _data.Length - _position)
            {
                throw new InvalidDataException($"The vector scene file ends before its {what}.");
            }
        }

        public void Skip(int bytes)
        {
            Need(bytes, "header");
            _position += bytes;
        }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            Need(count, "data");
            ReadOnlySpan<byte> bytes = _data.Slice(_position, count);
            _position += count;
            return bytes;
        }

        public byte Byte() => Bytes(1)[0];

        public bool Boolean()
        {
            byte value = Byte();
            if (value > 1) throw new InvalidDataException($"The vector scene file has a flag of {value}.");
            return value == 1;
        }

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));

        /// <summary>An index into the path table, checked against it.</summary>
        public int PathIndex(VectorPath[] paths)
        {
            int index = Int32();
            if ((uint)index >= (uint)paths.Length)
            {
                throw new InvalidDataException($"The vector scene file refers to path {index} of {paths.Length}.");
            }
            return index;
        }

        public float Single() => BinaryPrimitives.ReadSingleLittleEndian(Bytes(4));

        /// <summary>A count of things that each take at least some bytes: more than the input could hold is refused before anything is allocated.</summary>
        public int Count(int minimumBytesEach, string what)
        {
            int count = Int32();
            if (count < 0 || (long)count * minimumBytesEach > _data.Length - _position)
            {
                throw new InvalidDataException($"The vector scene file claims {count} {what}, more than it has bytes for.");
            }
            return count;
        }

        public float[] Singles(int count)
        {
            if (count == 0) return [];
            ReadOnlySpan<byte> bytes = Bytes(checked(count * 4));
            var values = new float[count];
            if (BitConverter.IsLittleEndian)
            {
                bytes.CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
            }
            else
            {
                for (int i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
            }
            return values;
        }

        public Matrix3x2 Matrix() => new(Single(), Single(), Single(), Single(), Single(), Single());

        public Rgba32 Color()
        {
            ReadOnlySpan<byte> c = Bytes(4);
            return new Rgba32(c[0], c[1], c[2], c[3]);
        }

        public T Enum<T>(byte largest, string what) where T : struct, Enum
        {
            byte value = Byte();
            if (value > largest) throw new InvalidDataException($"The vector scene file has a {what} of {value}, which is not one.");
            return Unsafe(value);

            static T Unsafe(byte raw) => System.Runtime.CompilerServices.Unsafe.As<byte, T>(ref raw);
        }

        public string? String()
        {
            ushort length = UInt16();
            return length == NoString ? null : Encoding.UTF8.GetString(Bytes(length));
        }
    }
}
