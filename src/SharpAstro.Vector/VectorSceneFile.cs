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
/// then coordinates), which the shapes refer to by index, so a path many shapes share is stored once and is one object
/// again after loading; then the nodes, depth first.</para>
/// <para><b>The bytes are not trusted.</b> Every count is checked against what is left of the input before anything is
/// allocated for it, so a short file cannot ask for a large array; verbs and tags are checked; nesting has a limit.
/// What does not check out is an <see cref="InvalidDataException"/>.</para>
/// </remarks>
public static class VectorSceneFile
{
    /// <summary>The first four bytes of a scene file.</summary>
    public static ReadOnlySpan<byte> Magic => "SAVS"u8;

    /// <summary>The layout this build writes, and the only one it reads.</summary>
    public const ushort Version = 1;

    /// <summary>The deepest nesting of groups a file may hold.</summary>
    public const int MaxDepth = 256;

    private const byte ShapeTag = 1, GroupTag = 2;
    private const byte SolidTag = 1, LinearTag = 2, RadialTag = 3;
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
            }
            else if (node is VectorGroup group)
            {
                Collect(group.Children, paths, indexOf, depth + 1);
            }
            else
            {
                throw new NotSupportedException($"A scene node of type {node.GetType().Name} cannot be written.");
            }
        }
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
                WriteNodes(writer, group.Children, indexOf);
                continue;
            }

            var shape = (VectorShape)node;
            writer.Write(ShapeTag);
            WriteString(writer, shape.Id);
            writer.Write(indexOf[shape.Path]);
            WriteMatrix(writer, shape.Transform);

            writer.Write(shape.Fill is not null);
            if (shape.Fill is { } fill)
            {
                WritePaint(writer, fill.Paint);
                writer.Write(fill.Opacity);
                writer.Write((byte)fill.Rule);
            }

            writer.Write(shape.Stroke is not null);
            if (shape.Stroke is { } stroke)
            {
                WritePaint(writer, stroke.Paint);
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
        }
    }

    private static void WriteString(BinaryWriter writer, string? text)
    {
        if (text is null)
        {
            writer.Write(NoString);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length >= NoString) throw new NotSupportedException("An id longer than 65,534 bytes cannot be written.");
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

    private static void WritePaint(BinaryWriter writer, VectorPaint paint)
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
        if (version != Version)
        {
            throw new InvalidDataException($"The vector scene file is version {version}; this build reads version {Version}.");
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

        ImmutableArray<VectorNode> nodes = ReadNodes(ref reader, paths, 0);
        if (!reader.AtEnd) throw new InvalidDataException("The vector scene file has bytes after its last node.");
        return new VectorScene(width, height, nodes);
    }

    private static ImmutableArray<VectorNode> ReadNodes(ref Reader reader, VectorPath[] paths, int depth)
    {
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
                nodes.Add(new VectorGroup { Id = id, Opacity = opacity, Children = ReadNodes(ref reader, paths, depth + 1) });
                continue;
            }
            if (tag != ShapeTag) throw new InvalidDataException($"The vector scene file has a node tag {tag}, which is not one.");

            int pathIndex = reader.Int32();
            if ((uint)pathIndex >= (uint)paths.Length)
            {
                throw new InvalidDataException($"The vector scene file refers to path {pathIndex} of {paths.Length}.");
            }
            Matrix3x2 transform = reader.Matrix();

            VectorFill? fill = null;
            if (reader.Boolean())
            {
                VectorPaint paint = ReadPaint(ref reader);
                float opacity = reader.Single();
                fill = new VectorFill(paint, opacity, reader.Enum<FillRule>((byte)FillRule.EvenOdd, "fill rule"));
            }

            VectorStroke? stroke = null;
            if (reader.Boolean())
            {
                VectorPaint paint = ReadPaint(ref reader);
                float opacity = reader.Single(), strokeWidth = reader.Single();
                LineCap cap = reader.Enum<LineCap>((byte)LineCap.Square, "line cap");
                LineJoin join = reader.Enum<LineJoin>((byte)LineJoin.Bevel, "line join");
                float miterLimit = reader.Single();
                int dashCount = reader.UInt16();
                float[] dashes = reader.Singles(dashCount);
                stroke = new VectorStroke(paint, opacity, strokeWidth, cap, join, miterLimit,
                    ImmutableCollectionsMarshal.AsImmutableArray(dashes), reader.Single());
            }

            nodes.Add(new VectorShape { Id = id, Path = paths[pathIndex], Transform = transform, Fill = fill, Stroke = stroke });
        }
        return nodes.MoveToImmutable();
    }

    private static VectorPaint ReadPaint(ref Reader reader)
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
