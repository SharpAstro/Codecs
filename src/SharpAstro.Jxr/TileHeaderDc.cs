namespace SharpAstro.Jxr;

/// <summary>
/// TILE_HEADER_DC from T.832 §8.6.2 — the per-tile DC-band header. In
/// the uniform-quantization path the header is just a single
/// <c>TILE_DC_UNIFORM_FLAG=1</c> bit; the per-tile QP override (the
/// <c>DC_QP()</c> block) lands when we add non-uniform support.
/// </summary>
public sealed class TileHeaderDc
{
    /// <summary>
    /// When true, the tile inherits the DC quantization step from the
    /// image-plane header. This is the only mode currently supported.
    /// </summary>
    public bool DcUniformFlag = true;

    public void Write(BitWriter writer)
    {
        if (!DcUniformFlag)
            throw new NotSupportedException("Non-uniform per-tile DC quantization not yet supported");
        writer.WriteBit(true);
    }

    public static TileHeaderDc Read(ref BitReader reader)
    {
        var h = new TileHeaderDc { DcUniformFlag = reader.ReadBit() };
        if (!h.DcUniformFlag)
            throw new NotSupportedException("Non-uniform per-tile DC quantization not yet supported");
        return h;
    }
}
