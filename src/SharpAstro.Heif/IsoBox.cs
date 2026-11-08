using System.Buffers.Binary;
using System.Text;

namespace SharpAstro.Heif;

/// <summary>Four-character codes used by the HEIF reader, as big-endian <c>uint</c>s.</summary>
internal static class FourCc
{
    public const uint Ftyp = 0x66747970; // 'ftyp'
    public const uint Meta = 0x6D657461; // 'meta'
    public const uint Hdlr = 0x68646C72; // 'hdlr'
    public const uint Pitm = 0x7069746D; // 'pitm'
    public const uint Iinf = 0x69696E66; // 'iinf'
    public const uint Infe = 0x696E6665; // 'infe'
    public const uint Iloc = 0x696C6F63; // 'iloc'
    public const uint Idat = 0x69646174; // 'idat'
    public const uint Iref = 0x69726566; // 'iref'
    public const uint Iprp = 0x69707270; // 'iprp'
    public const uint Ipco = 0x6970636F; // 'ipco'
    public const uint Ipma = 0x69706D61; // 'ipma'
    public const uint Ispe = 0x69737065; // 'ispe'
    public const uint HvcC = 0x68766343; // 'hvcC'
    public const uint Colr = 0x636F6C72; // 'colr'
    public const uint Pixi = 0x70697869; // 'pixi'
    public const uint Irot = 0x69726F74; // 'irot'
    public const uint Imir = 0x696D6972; // 'imir'
    public const uint Clap = 0x636C6170; // 'clap'
    public const uint Uuid = 0x75756964; // 'uuid'
    public const uint Grid = 0x67726964; // 'grid'
    public const uint Hvc1 = 0x68766331; // 'hvc1'
    public const uint Exif = 0x45786966; // 'Exif'
    public const uint Mime = 0x6D696D65; // 'mime'
    public const uint Thmb = 0x74686D62; // 'thmb'
    public const uint Dimg = 0x64696D67; // 'dimg'
    public const uint Cdsc = 0x63647363; // 'cdsc'
    public const uint Auxl = 0x6175786C; // 'auxl'
    public const uint Pict = 0x70696374; // 'pict'
    public const uint Nclx = 0x6E636C78; // 'nclx'
    public const uint Prof = 0x70726F66; // 'prof'
    public const uint RICC = 0x72494343; // 'rICC'

    /// <summary>The code as its four characters (Latin-1, so any byte value round-trips).</summary>
    public static string ToString(uint code)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, code);
        return Encoding.Latin1.GetString(bytes);
    }
}

/// <summary>
/// One ISOBMFF box (ISO/IEC 14496-12 4.2) located inside a file: offsets are absolute positions in the
/// span the reader was given, so a box never has to be copied out to be read. <see cref="BodyStart"/> is
/// past the size, type, any 64-bit <c>largesize</c> and any <c>uuid</c> extended type, but NOT past a
/// FullBox's version and flags, which are box-specific and read by <see cref="IsoBoxes.FullBoxHeader"/>.
/// </summary>
internal readonly record struct IsoBox(uint Type, int Start, int BodyStart, int End)
{
    public int BodyLength => End - BodyStart;

    public ReadOnlySpan<byte> Body(ReadOnlySpan<byte> file) => file[BodyStart..End];

    public override string ToString() => $"{FourCc.ToString(Type)} @{Start}..{End}";
}

internal static class IsoBoxes
{
    /// <summary>
    /// The boxes laid end to end in <c>[start, end)</c>. Every box must fit inside the range: a size that
    /// runs past its parent is the commonest form of corruption, and reading on would take the next box's
    /// bytes from inside this one's payload. A size of 0 means "to the end of the enclosing range"
    /// (14496-12 allows it for the last top-level box; it is accepted at any level since it can only ever
    /// shrink what is read). Trailing bytes too few to be a box header are ignored, as readers commonly do
    /// with padding.
    /// </summary>
    public static List<IsoBox> Children(ReadOnlySpan<byte> file, int start, int end)
    {
        if (start < 0 || end > file.Length || start > end)
            throw new InvalidDataException("Box range lies outside the file.");

        var boxes = new List<IsoBox>();
        var position = start;
        while (end - position >= 8)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file[position..]);
            var type = BinaryPrimitives.ReadUInt32BigEndian(file[(position + 4)..]);
            var header = 8;

            if (size == 1)
            {
                if (end - position < 16) throw new InvalidDataException($"Box '{FourCc.ToString(type)}' is cut short in its 64-bit size.");
                var large = BinaryPrimitives.ReadUInt64BigEndian(file[(position + 8)..]);
                size = large > long.MaxValue ? long.MaxValue : (long)large;
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (type == FourCc.Uuid) header += 16;

            if (size < header)
                throw new InvalidDataException($"Box '{FourCc.ToString(type)}' at {position} declares {size} bytes, less than its own header.");
            if (size > end - position)
                throw new InvalidDataException($"Box '{FourCc.ToString(type)}' at {position} declares {size} bytes, past the end of its parent ({end - position} left).");

            boxes.Add(new IsoBox(type, position, position + header, position + (int)size));
            position += (int)size;
        }

        return boxes;
    }

    public static List<IsoBox> Children(ReadOnlySpan<byte> file, IsoBox parent, int skip = 0) =>
        Children(file, parent.BodyStart + skip, parent.End);

    /// <summary>The FullBox version and 24-bit flags (14496-12 4.2) at the start of <paramref name="box"/>'s body.</summary>
    public static (byte Version, uint Flags) FullBoxHeader(ReadOnlySpan<byte> file, IsoBox box)
    {
        if (box.BodyLength < 4)
            throw new InvalidDataException($"Box '{FourCc.ToString(box.Type)}' is too short for its version and flags.");
        var word = BinaryPrimitives.ReadUInt32BigEndian(file[box.BodyStart..]);
        return ((byte)(word >> 24), word & 0xFFFFFF);
    }

    /// <summary>The first child of <paramref name="type"/>, or null.</summary>
    public static IsoBox? Find(List<IsoBox> boxes, uint type)
    {
        foreach (var box in boxes)
            if (box.Type == type) return box;
        return null;
    }
}

/// <summary>
/// A big-endian cursor over a box body that throws <see cref="InvalidDataException"/> rather than reading
/// past its end, so a box whose fields claim more than it holds is refused rather than read into its
/// neighbour.
/// </summary>
internal ref struct BigEndianReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string _what;
    private int _position;

    public BigEndianReader(ReadOnlySpan<byte> data, string what)
    {
        _data = data;
        _what = what;
    }

    public readonly int Position => _position;
    public readonly int Remaining => _data.Length - _position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException($"'{_what}' is cut short: needs {count} more bytes at offset {_position}, has {Remaining}.");
        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public uint U24() { var b = Take(3); return (uint)(b[0] << 16 | b[1] << 8 | b[2]); }
    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
    public int S32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    /// <summary>An unsigned field of 0, 4 or 8 bytes, the widths iloc's size fields allow (14496-12 8.11.3).</summary>
    public ulong Sized(int bytes) => bytes switch
    {
        0 => 0,
        4 => U32(),
        8 => U64(),
        _ => throw new InvalidDataException($"'{_what}' declares a {bytes}-byte field; only 0, 4 and 8 are defined."),
    };

    public ReadOnlySpan<byte> Bytes(int count) => Take(count);

    public void Skip(int count) => Take(count);

    /// <summary>A null-terminated UTF-8 string; a missing terminator takes the rest of the body (seen in the wild).</summary>
    public string NullTerminated()
    {
        var rest = _data[_position..];
        var nul = rest.IndexOf((byte)0);
        var length = nul < 0 ? rest.Length : nul;
        var text = Encoding.UTF8.GetString(rest[..length]);
        _position += nul < 0 ? length : length + 1;
        return text;
    }
}
