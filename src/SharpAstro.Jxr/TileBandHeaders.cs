namespace SharpAstro.Jxr;

/// <summary>
/// Composite of the three per-tile band headers that appear at the start
/// of a TILE_SPATIAL — T.832 §8.6.1. Which sub-headers are present is
/// determined by <see cref="JxrBandsPresent"/>; this helper enforces the
/// conditional emission rule in a single place so callers (the tile
/// orchestrator and round-trip tests) don't replicate the logic.
/// </summary>
public sealed class TileBandHeaders
{
    public TileHeaderDc Dc { get; init; } = new();
    public TileHeaderLowpass? Lowpass { get; init; }
    public TileHeaderHighpass? Highpass { get; init; }

    /// <summary>
    /// Build a default uniform-quantization triplet for the given bands.
    /// Returns the trio with LP/HP sub-headers populated only when the
    /// matching band is present.
    /// </summary>
    public static TileBandHeaders Uniform(JxrBandsPresent bands) => new()
    {
        Dc = new TileHeaderDc(),
        Lowpass = bands != JxrBandsPresent.DcOnly ? new TileHeaderLowpass() : null,
        Highpass = (bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass)
            ? new TileHeaderHighpass() : null,
    };

    public void Write(BitWriter writer, JxrBandsPresent bands, bool trimFlexBitsFlag)
    {
        Dc.Write(writer);
        if (bands != JxrBandsPresent.DcOnly)
        {
            if (Lowpass is null)
                throw new InvalidOperationException($"Lowpass header required when BandsPresent={bands}");
            Lowpass.Write(writer);
        }
        if (bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass)
        {
            if (Highpass is null)
                throw new InvalidOperationException($"Highpass header required when BandsPresent={bands}");
            Highpass.Write(writer, trimFlexBitsFlag);
        }
    }

    public static TileBandHeaders Read(ref BitReader reader, JxrBandsPresent bands, bool trimFlexBitsFlag)
    {
        var dc = TileHeaderDc.Read(ref reader);
        TileHeaderLowpass? lp = null;
        TileHeaderHighpass? hp = null;
        if (bands != JxrBandsPresent.DcOnly)
            lp = TileHeaderLowpass.Read(ref reader);
        if (bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass)
            hp = TileHeaderHighpass.Read(ref reader, trimFlexBitsFlag);
        return new TileBandHeaders { Dc = dc, Lowpass = lp, Highpass = hp };
    }
}
