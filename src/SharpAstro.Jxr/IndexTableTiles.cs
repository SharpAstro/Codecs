namespace SharpAstro.Jxr;

/// <summary>
/// INDEX_TABLE_TILES from the JXR codestream — T.832 §8.7.1.3. Each entry
/// is a byte offset (relative to the first CODED_TILE) of one tile's data,
/// letting decoders seek directly to any tile without sequential parsing.
/// Present only when <see cref="ImageHeader.IndexTablePresentFlag"/> is true.
/// </summary>
/// <remarks>
/// <para>The structure is a 16-bit start code (0x0001) followed by one
/// <c>vlw_esc</c>-encoded entry per (tile × band). For spatial-mode
/// codestreams there's one entry per tile; for frequency-mode codestreams
/// there's typically one entry per (tile, band) pair so DC / LP / HP /
/// FlexBits can be located independently.</para>
/// <para>This implementation supports reading the table — enough to walk
/// past it during sequential decode of external producer files. The
/// offsets themselves are exposed via <see cref="Offsets"/> for callers
/// that want random-access decode later.</para>
/// </remarks>
public sealed class IndexTableTiles
{
    /// <summary>Required start code value (T.832 §8.7.1.3).</summary>
    public const ushort IndexTableStartCode = 0x0001;

    /// <summary>Byte offsets (from the start of CODED_TILES) for each entry in
    /// the table, in tile-raster (× band, in frequency mode) order.</summary>
    public required long[] Offsets { get; init; }

    /// <summary>
    /// Write INDEX_TABLE_TILES with the supplied byte offsets. Each offset
    /// is encoded as a <c>vlw_esc</c> value, using the 16-bit short form
    /// when possible (offsets &lt; 0xFFFC) and the 32-bit escape otherwise.
    /// </summary>
    public void Write(BitWriter writer)
    {
        writer.WriteBits(IndexTableStartCode, 16);
        foreach (var off in Offsets)
            WriteVlwEsc(writer, off);
    }

    private static void WriteVlwEsc(BitWriter writer, long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "vlw_esc value must be non-negative");
        if (value < 0xFFFC)
        {
            writer.WriteBits((uint)value, 16);
        }
        else if (value <= uint.MaxValue)
        {
            writer.WriteBits(0xFFFC, 16);
            writer.WriteBits((uint)value, 32);
        }
        else
        {
            writer.WriteBits(0xFFFD, 16);
            writer.WriteBits((uint)(value >> 32), 32);
            writer.WriteBits((uint)(value & 0xFFFFFFFF), 32);
        }
    }

    /// <summary>
    /// Read INDEX_TABLE_TILES from <paramref name="reader"/>. The caller
    /// supplies the expected entry count derived from the IMAGE_HEADER:
    /// <c>NumVerTiles × NumHorTiles</c> for spatial mode, with a multiplier
    /// for frequency mode (DC / LP / HP / FlexBits bands).
    /// </summary>
    public static IndexTableTiles Read(ref BitReader reader, int expectedEntries)
    {
        var start = reader.ReadBits(16);
        if (start != IndexTableStartCode)
            throw new InvalidDataException(
                $"INDEX_TABLE_TILES start code mismatch: expected 0x{IndexTableStartCode:X4}, got 0x{start:X4}");

        var offsets = new long[expectedEntries];
        for (var i = 0; i < expectedEntries; i++)
            offsets[i] = ReadVlwEsc(ref reader);

        return new IndexTableTiles { Offsets = offsets };
    }

    /// <summary>
    /// Decode a <c>vlw_esc</c> value — T.832 §8.7.1.3 variable-length word
    /// with escape codes. Most values fit in 16 bits; the escape codes
    /// 0xFFFC / 0xFFFD let larger values use a 32-bit or 64-bit follow-up.
    /// </summary>
    private static long ReadVlwEsc(ref BitReader reader)
    {
        var first = reader.ReadBits(16);
        if (first < 0xFFFC) return first;
        if (first == 0xFFFC)
        {
            // 32-bit follow-up.
            return reader.ReadBits(32);
        }
        if (first == 0xFFFD)
        {
            // 64-bit follow-up.
            var hi = reader.ReadBits(32);
            var lo = reader.ReadBits(32);
            return unchecked((long)(((ulong)hi << 32) | lo));
        }
        throw new InvalidDataException(
            $"vlw_esc escape code 0x{first:X4} reserved / not yet supported");
    }
}
