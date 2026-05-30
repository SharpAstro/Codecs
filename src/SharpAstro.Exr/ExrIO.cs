using System.Buffers.Binary;
using System.Text;

namespace SharpAstro.Exr;

/// <summary>
/// Little-endian byte sink for assembling an OpenEXR stream. EXR is little-endian
/// throughout; strings in the header (attribute names/types, channel names) are
/// NUL-terminated, whereas <c>string</c>-typed attribute <i>values</i> are sized by
/// the attribute length and written without a terminator.
/// </summary>
internal sealed class ExrWriter
{
    private byte[] _buf;
    private int _len;

    public ExrWriter(int capacity = 1024) => _buf = new byte[capacity];

    public int Length => _len;

    private Span<byte> Reserve(int n)
    {
        if (_len + n > _buf.Length)
        {
            int cap = _buf.Length;
            while (cap < _len + n) cap *= 2;
            Array.Resize(ref _buf, cap);
        }
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void WriteByte(byte b) => Reserve(1)[0] = b;
    public void WriteInt32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), v);
    public void WriteUInt32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), v);
    public void WriteUInt64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), v);
    public void WriteSingle(float v) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), v);
    public void WriteBytes(ReadOnlySpan<byte> b) => b.CopyTo(Reserve(b.Length));

    /// <summary>Write a NUL-terminated ASCII string (attribute/channel names + type names).</summary>
    public void WriteNulString(string s)
    {
        int n = Encoding.ASCII.GetByteCount(s);
        Encoding.ASCII.GetBytes(s, Reserve(n));
        WriteByte(0);
    }

    /// <summary>Overwrite an 8-byte little-endian value already emitted at <paramref name="pos"/> (offset-table backfill).</summary>
    public void PatchUInt64At(int pos, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(pos, 8), v);

    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
}

/// <summary>Little-endian cursor over an OpenEXR byte stream.</summary>
internal ref struct ExrReader
{
    private readonly ReadOnlySpan<byte> _data;
    public int Pos;

    public ExrReader(ReadOnlySpan<byte> data) { _data = data; Pos = 0; }

    public readonly int Length => _data.Length;
    public readonly bool AtEnd => Pos >= _data.Length;

    public byte ReadByte() => _data[Pos++];

    public int ReadInt32() { int v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(Pos, 4)); Pos += 4; return v; }
    public uint ReadUInt32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(Pos, 4)); Pos += 4; return v; }
    public ulong ReadUInt64() { ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(Pos, 8)); Pos += 8; return v; }
    public float ReadSingle() { float v = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(Pos, 4)); Pos += 4; return v; }

    public ReadOnlySpan<byte> ReadBytes(int n) { var s = _data.Slice(Pos, n); Pos += n; return s; }

    public void Seek(int pos) => Pos = pos;

    /// <summary>Read a NUL-terminated ASCII string.</summary>
    public string ReadNulString()
    {
        int start = Pos;
        while (_data[Pos] != 0) Pos++;
        string s = Encoding.ASCII.GetString(_data.Slice(start, Pos - start));
        Pos++; // skip NUL
        return s;
    }
}
