namespace SharpAstro.Jxr;

/// <summary>
/// TILE_HEADER_HIGHPASS from T.832 §8.6.4 — per-tile HP-band header.
/// Only emitted when <c>BANDS_PRESENT</c> includes the HP band. Carries
/// an optional <c>TRIM_FLEXBITS</c> 4-bit field that drops trailing
/// flexbit refinement (controlled by the IMAGE_HEADER
/// <c>TRIM_FLEX_BITS_FLAG</c> — caller passes that flag in).
/// </summary>
public sealed class TileHeaderHighpass
{
    /// <summary>
    /// TRIM_FLEXBITS — number of LSBs of the flexbits refinement that
    /// are dropped from the bitstream (0..15). Only meaningful when the
    /// enclosing IMAGE_HEADER set <c>TRIM_FLEX_BITS_FLAG = true</c>.
    /// </summary>
    public int TrimFlexBits;

    public bool HpUniformFlag = true;

    public void Write(BitWriter writer, bool trimFlexBitsFlag)
    {
        if (trimFlexBitsFlag)
        {
            if (TrimFlexBits is < 0 or > 15)
                throw new ArgumentOutOfRangeException(nameof(TrimFlexBits), TrimFlexBits, "TRIM_FLEXBITS must fit in 4 bits (0..15)");
            writer.WriteBits((uint)TrimFlexBits, 4);
        }

        if (!HpUniformFlag)
            throw new NotSupportedException("Non-uniform per-tile HP quantization not yet supported");
        writer.WriteBit(true);
    }

    public static TileHeaderHighpass Read(ref BitReader reader, bool trimFlexBitsFlag)
    {
        var h = new TileHeaderHighpass();
        if (trimFlexBitsFlag)
            h.TrimFlexBits = (int)reader.ReadBits(4);
        h.HpUniformFlag = reader.ReadBit();
        if (!h.HpUniformFlag)
            throw new NotSupportedException("Non-uniform per-tile HP quantization not yet supported");
        return h;
    }
}
