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
///   <item><see cref="JxrBandsPresent.NoFlexbits"/> — emits MB_DC + MB_LP + MB_CBPHP + MB_HP.
///         The CBPHP bitmap is computed via <see cref="MbHp.ComputeCbphp"/> in a pre-pass
///         and written before the HP coefficient data, per T.832 §8.7.16.</item>
/// </list>
/// <para>AllBands path (NoFlexbits + FlexBits refinement) lands in a follow-on commit.</para>
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
        var hasHp = bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass;
        var cbphpState = hasHp ? new MbCbphpState() : null;
        var hpState = hasHp ? new MbHpState() : null;
        var cbphpBuf = hasHp ? new int[numComponents] : null;
        var hpDummyCbphp = hasHp ? new int[numComponents] : null;

        for (var row = 0; row < heightInMb; row++)
        {
            for (var col = 0; col < widthInMb; col++)
            {
                var mb = mbs[row * widthInMb + col];
                MbDc.EncodeMb(writer, dcState, format, numComponents, mb.Dc);
                if (lpState is not null)
                    MbLp.EncodeMb(writer, lpState, format, numComponents, mb.Lp);
                if (hasHp)
                {
                    // CBPHP must appear in the bitstream before HP coefficient data,
                    // so compute it in a pre-pass and feed it to MbCbphp.
                    MbHp.ComputeCbphp(numComponents, mb.Hp, cbphpBuf!);
                    MbCbphp.EncodeMb(writer, cbphpState!, numComponents, cbphpBuf!);
                    MbHp.EncodeMb(writer, hpState!, mb.MbHpMode, format, numComponents, mb.Hp, hpDummyCbphp!);
                }
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
        if (bands == JxrBandsPresent.AllBands)
            throw new NotSupportedException("TILE_SPATIAL.Read AllBands (with FlexBits refinement) not yet supported");

        headers = TileBandHeaders.Read(ref reader, bands, trimFlexBitsFlag);

        var dcState = new MbDcState();
        var lpState = bands != JxrBandsPresent.DcOnly ? new MbLpState() : null;
        var hasHp = bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass;
        var cbphpState = hasHp ? new MbCbphpState() : null;
        var hpState = hasHp ? new MbHpState() : null;
        var cbphpBuf = hasHp ? new int[numComponents] : null;
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
                if (hasHp)
                {
                    MbCbphp.DecodeMb(ref reader, cbphpState!, numComponents, cbphpBuf!);
                    mb.Hp = new int[numComponents * 256];
                    // Note: mb.MbHpMode is supplied by the caller-side state machine
                    // (derived from LP coefficients); for this orchestrator we default
                    // to mode 0 (horizontal scan) — same value the encoder used.
                    MbHp.DecodeMb(ref reader, hpState!, mb.MbHpMode, format, numComponents, cbphpBuf!, mb.Hp);
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
        if (bands == JxrBandsPresent.AllBands)
            throw new NotSupportedException("TILE_SPATIAL.Write AllBands (with FlexBits refinement) not yet supported");
        if (widthInMb < 1 || heightInMb < 1)
            throw new ArgumentOutOfRangeException(nameof(widthInMb), "tile must contain at least one macroblock");
        if (mbs.Length != widthInMb * heightInMb)
            throw new ArgumentException(
                $"mbs has length {mbs.Length}, expected {widthInMb * heightInMb} ({widthInMb}×{heightInMb})",
                nameof(mbs));
        var hasHp = bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass;
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
            if (hasHp && mbs[i].Hp.Length < numComponents * 256)
                throw new ArgumentException(
                    $"mbs[{i}].Hp has length {mbs[i].Hp.Length}, expected ≥ {numComponents * 256} for BANDS_PRESENT={bands}",
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
