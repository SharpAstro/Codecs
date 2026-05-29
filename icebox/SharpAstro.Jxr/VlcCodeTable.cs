namespace SharpAstro.Jxr;

/// <summary>
/// A static variable-length code (VLC) table — maps integer values to
/// bit-codes for encoding and back for decoding. Used by the JPEG XR
/// syntax elements VAL_DC_YUV, ABS_LEVEL_INDEX, etc. (T.832 §8.7.14 onward).
/// </summary>
/// <remarks>
/// Construction is from a list of <c>(value, code, length)</c> triples
/// where <c>code</c> packs the MSB-first bit pattern in its low
/// <c>length</c> bits. The encode side is an O(1) array lookup by value;
/// the decode side walks the codestream one bit at a time through an
/// internal jump table (effectively a small binary prefix tree). This
/// is fast enough for tables with up to ~64 entries — every JXR VLC
/// table is well under that.
/// </remarks>
public sealed class VlcCodeTable
{
    public readonly record struct Entry(int Value, uint Code, byte Length);

    private readonly Entry[] _entries;
    private readonly (uint code, byte length)[] _encodeByValue;
    private readonly int _maxValue;

    public VlcCodeTable(ReadOnlySpan<Entry> entries)
    {
        _entries = entries.ToArray();
        _maxValue = 0;
        foreach (var e in entries)
            if (e.Value > _maxValue) _maxValue = e.Value;

        _encodeByValue = new (uint, byte)[_maxValue + 1];
        // Initialise lengths to 0 so callers passing an out-of-table value
        // hit the explicit check below rather than emitting a stale code.
        foreach (var e in entries)
        {
            if (e.Length is 0 or > 32)
                throw new ArgumentException($"VLC entry {e.Value}: length must be in [1, 32], got {e.Length}");
            if (_encodeByValue[e.Value].length != 0)
                throw new ArgumentException($"VLC table has duplicate entry for value {e.Value}");
            _encodeByValue[e.Value] = (e.Code, e.Length);
        }
    }

    /// <summary>
    /// Look up the <c>(code, length)</c> pair for <paramref name="value"/>.
    /// Throws if <paramref name="value"/> is outside the table.
    /// </summary>
    public (uint code, int length) Encode(int value)
    {
        if ((uint)value > (uint)_maxValue)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"VLC table has no entry for value {value} (max defined: {_maxValue})");
        var (code, length) = _encodeByValue[value];
        if (length == 0)
            throw new ArgumentException($"VLC table has no entry for value {value}");
        return (code, length);
    }

    /// <summary>Emit the code for <paramref name="value"/> into <paramref name="writer"/>.</summary>
    public void Encode(BitWriter writer, int value)
    {
        var (code, length) = Encode(value);
        writer.WriteBits(code, length);
    }

    /// <summary>
    /// Match the next bit-prefix in <paramref name="reader"/> against the
    /// table and consume it, returning the matched value. Throws
    /// <see cref="InvalidDataException"/> if no prefix matches (the
    /// codestream has been corrupted or the wrong table was selected).
    /// </summary>
    public int Decode(ref BitReader reader)
    {
        // Walk bits one at a time, accumulating a prefix, until exactly one
        // table entry matches. This is O(maxLength) per decode — fine for
        // the small JXR tables.
        uint prefix = 0;
        for (var len = 1; len <= 32; len++)
        {
            prefix = (prefix << 1) | (reader.ReadBit() ? 1u : 0u);
            foreach (var e in _entries)
                if (e.Length == len && e.Code == prefix)
                    return e.Value;
        }
        throw new InvalidDataException("VLC decode failed: no table entry matches the bitstream prefix");
    }
}
