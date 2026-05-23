namespace SharpAstro.Jxr;

/// <summary>
/// TILE_HEADER_LOWPASS from T.832 §8.6.3 — per-tile LP-band header. Only
/// emitted when <c>BANDS_PRESENT</c> includes the LP band. In the
/// uniform-quantization path the header is just a single
/// <c>TILE_LP_UNIFORM_FLAG=1</c> bit.
/// </summary>
public sealed class TileHeaderLowpass
{
    public bool LpUniformFlag = true;

    public void Write(BitWriter writer)
    {
        if (!LpUniformFlag)
            throw new NotSupportedException("Non-uniform per-tile LP quantization not yet supported");
        writer.WriteBit(true);
    }

    public static TileHeaderLowpass Read(ref BitReader reader)
    {
        var h = new TileHeaderLowpass { LpUniformFlag = reader.ReadBit() };
        if (!h.LpUniformFlag)
            throw new NotSupportedException("Non-uniform per-tile LP quantization not yet supported");
        return h;
    }
}
