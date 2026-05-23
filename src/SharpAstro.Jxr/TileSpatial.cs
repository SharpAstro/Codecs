namespace SharpAstro.Jxr;

/// <summary>
/// Spatial-mode tile orchestrator — T.832 §8.6. Wraps the band-header trio
/// (<see cref="TileBandHeaders"/>) around a raster-order loop that dispatches
/// each macroblock through the existing per-MB syntax-element encoders
/// (<see cref="MbDc"/>, <see cref="MbLp"/>, <see cref="MbCbphp"/>,
/// <see cref="MbHp"/>) in spec-mandated order:
/// <c>MB_DC → MB_LP? → MB_CBPHP? → MB_HP? → FlexBits?</c>.
/// </summary>
/// <remarks>
/// <para>Currently supported <c>BANDS_PRESENT</c> values:</para>
/// <list type="bullet">
///   <item><see cref="JxrBandsPresent.DcOnly"/> — emits only MB_DC per macroblock.</item>
///   <item><see cref="JxrBandsPresent.NoHighpass"/> — emits MB_DC + MB_LP. Restricted to
///         CBPLP_CH_BIT formats (YOnly / YUVK / NComponent / Rgb); the YUV420/422/444
///         CBPLP_YUV1/YUV2 joint-VLC path lives in MbLp and is not yet wired.</item>
/// </list>
/// <para>NoFlexbits and AllBands paths (which add MB_CBPHP + MB_HP + FlexBits) land
/// in follow-on commits.</para>
/// </remarks>
public static class TileSpatial
{
    /// <summary>
    /// Write a complete TILE_SPATIAL block: band headers followed by per-MB
    /// data in raster order, terminated by byte-alignment.
    /// </summary>
    /// <param name="mbs"><c>widthInMb × heightInMb</c> macroblocks in row-major order.</param>
    public static void Write(
        BitWriter writer,
        TileBandHeaders headers,
        JxrBandsPresent bands,
        bool trimFlexBitsFlag,
        JxrInternalColorFormat format,
        int numComponents,
        int widthInMb,
        int heightInMb,
        Macroblock[] mbs)
    {
        ValidateBandsAndMbs(bands, widthInMb, heightInMb, mbs, numComponents);

        headers.Write(writer, bands, trimFlexBitsFlag);

        var dcState = new MbDcState();
        var lpState = bands != JxrBandsPresent.DcOnly ? new MbLpState() : null;
        // HP / CBPHP states will land here when bands include the HP subband.

        for (var row = 0; row < heightInMb; row++)
        {
            for (var col = 0; col < widthInMb; col++)
            {
                var mb = mbs[row * widthInMb + col];
                MbDc.EncodeMb(writer, dcState, format, numComponents, mb.Dc);
                if (lpState is not null)
                    MbLp.EncodeMb(writer, lpState, format, numComponents, mb.Lp);
                // HP+CBPHP dispatch slots in here.
            }
        }

        WriteByteAlignment(writer);
    }

    /// <summary>Read a complete TILE_SPATIAL block; mirror of <see cref="Write"/>.</summary>
    public static Macroblock[] Read(
        ref BitReader reader,
        JxrBandsPresent bands,
        bool trimFlexBitsFlag,
        JxrInternalColorFormat format,
        int numComponents,
        int widthInMb,
        int heightInMb,
        out TileBandHeaders headers)
    {
        if (bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass)
            throw new NotSupportedException($"TILE_SPATIAL.Read currently supports BANDS_PRESENT ∈ {{DcOnly, NoHighpass}} (got {bands})");

        headers = TileBandHeaders.Read(ref reader, bands, trimFlexBitsFlag);

        var dcState = new MbDcState();
        var lpState = bands != JxrBandsPresent.DcOnly ? new MbLpState() : null;
        var mbs = new Macroblock[widthInMb * heightInMb];
        for (var row = 0; row < heightInMb; row++)
        {
            for (var col = 0; col < widthInMb; col++)
            {
                var mb = new Macroblock { Dc = new int[numComponents] };
                MbDc.DecodeMb(ref reader, dcState, format, numComponents, mb.Dc);
                if (lpState is not null)
                {
                    mb.Lp = new int[numComponents * 16];
                    MbLp.DecodeMb(ref reader, lpState, format, numComponents, mb.Lp);
                }
                mbs[row * widthInMb + col] = mb;
            }
        }

        AlignToByte(ref reader);
        return mbs;
    }

    private static void ValidateBandsAndMbs(
        JxrBandsPresent bands, int widthInMb, int heightInMb, Macroblock[] mbs, int numComponents)
    {
        if (bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass)
            throw new NotSupportedException($"TILE_SPATIAL.Write currently supports BANDS_PRESENT ∈ {{DcOnly, NoHighpass}} (got {bands})");
        if (widthInMb < 1 || heightInMb < 1)
            throw new ArgumentOutOfRangeException(nameof(widthInMb), "tile must contain at least one macroblock");
        if (mbs.Length != widthInMb * heightInMb)
            throw new ArgumentException(
                $"mbs has length {mbs.Length}, expected {widthInMb * heightInMb} ({widthInMb}×{heightInMb})",
                nameof(mbs));
        for (var i = 0; i < mbs.Length; i++)
        {
            if (mbs[i] is null)
                throw new ArgumentException($"mbs[{i}] is null", nameof(mbs));
            if (mbs[i].Dc.Length < numComponents)
                throw new ArgumentException(
                    $"mbs[{i}].Dc has length {mbs[i].Dc.Length}, expected ≥ {numComponents}",
                    nameof(mbs));
            if (bands != JxrBandsPresent.DcOnly && mbs[i].Lp.Length < numComponents * 16)
                throw new ArgumentException(
                    $"mbs[{i}].Lp has length {mbs[i].Lp.Length}, expected ≥ {numComponents * 16} for BANDS_PRESENT={bands}",
                    nameof(mbs));
        }
    }

    private static void WriteByteAlignment(BitWriter writer)
    {
        while ((writer.BitPosition & 7) != 0) writer.WriteBit(false);
    }

    private static void AlignToByte(ref BitReader reader)
    {
        var slack = (8 - (reader.BitPosition & 7)) & 7;
        if (slack > 0) reader.SkipBits(slack);
    }
}
