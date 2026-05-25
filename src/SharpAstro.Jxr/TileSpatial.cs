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
///   <item><see cref="JxrBandsPresent.AllBands"/> — adds the per-MB FlexBits refinement
///         (MB_HP_FLEX inlined) after the HP coefficient data. CBPHP is computed
///         from the post-iModelBits-split values via
///         <see cref="MbHp.ComputeCbphpWithSplit"/>. TRIM_FLEXBITS comes from the
///         per-tile HP band header.</item>
/// </list>
/// </remarks>
public static class TileSpatial
{
    /// <summary>
    /// Write a complete TILE_SPATIAL block: band headers followed by per-MB
    /// data in raster order, terminated by byte-alignment.
    /// </summary>
    /// <param name="mbs"><c>widthInMb × heightInMb</c> macroblocks in row-major order.</param>
    /// <summary>Compat overload — constructs a default plane header from (format, numComponents) for legacy callers.</summary>
    public static void Write(BitWriter writer, TileBandHeaders headers,
        JxrBandsPresent bands, bool trimFlexBitsFlag,
        JxrInternalColorFormat format, int numComponents,
        int widthInMb, int heightInMb, Macroblock[] mbs)
        => Write(writer, headers, bands, trimFlexBitsFlag,
            new ImagePlaneHeader
            {
                InternalClrFmt = format,
                NumComponents = numComponents,
                BandsPresent = bands,
            },
            widthInMb, heightInMb, mbs);

    public static void Write(
        BitWriter writer,
        TileBandHeaders headers,
        JxrBandsPresent bands,
        bool trimFlexBitsFlag,
        ImagePlaneHeader plane,
        int widthInMb,
        int heightInMb,
        Macroblock[] mbs)
    {
        var format = plane.InternalClrFmt;
        var numComponents = plane.NumComponents;
        ValidateBandsAndMbs(bands, widthInMb, heightInMb, mbs, numComponents);

        // T.832 Table 39 — TILE_SPATIAL starts with TILE_STARTCODE (24 bits =
        // 0x000001) + ARBITRARY_BYTE (8 bits, ignored). TRIM_FLEXBITS (4 bits)
        // would follow when TRIM_FLEXBITS_FLAG is set; we currently keep that
        // in the HP band header which round-trips for our own files, and the
        // seagull fixture has TRIM_FLEXBITS_FLAG=false so the placement gap
        // doesn't bite.
        TileFrequency.WriteTileStartCode(writer);
        headers.Write(writer, bands, trimFlexBitsFlag, plane);

        var dcState = new MbDcState();
        var lpState = bands != JxrBandsPresent.DcOnly ? new MbLpState() : null;
        var hasHp = bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass;
        var cbphpState = hasHp ? new MbCbphpState() : null;
        if (hasHp) cbphpState!.InitMbCbphpGrid(widthInMb, heightInMb, numComponents);
        var hpState = hasHp ? new MbHpState() : null;
        var cbphpBuf = hasHp ? new int[numComponents] : null;
        var hpDummyCbphp = hasHp ? new int[numComponents] : null;

        // Determine per-MB QP_INDEX widths based on the band-header dispatch:
        //   - For LP: NumLPQPs > 1 only when plane is non-uniform AND tile didn't
        //     opt into USE_DC_QP_FLAG. Otherwise NumLPQPs == 1 and no index emitted.
        //   - For HP: NumHPQPs > 1 only when plane is non-uniform AND tile didn't
        //     opt into USE_LP_QP_FLAG.
        int numLpQPs = headers.Lowpass?.LpQp?.NumQPs ?? 1;
        int numHpQPs = headers.Highpass?.HpQp?.NumQPs ?? 1;
        // TRIM_FLEXBITS (4-bit per-tile field) is meaningful only for AllBands.
        // When the IMAGE_HEADER's TRIM_FLEX_BITS_FLAG is false, this stays 0.
        var trimFlexBits = bands == JxrBandsPresent.AllBands
            ? (trimFlexBitsFlag ? headers.Highpass?.TrimFlexBits ?? 0 : 0)
            : 0;

        for (var row = 0; row < heightInMb; row++)
        {
            for (var col = 0; col < widthInMb; col++)
            {
                var mb = mbs[row * widthInMb + col];
                // T.832 8.7.16.1 / 8.7.18.2: scan totals reset at the start of
                // each 16-MB column stride.
                if (col % 16 == 0)
                {
                    lpState?.Scan.ResetTotals();
                    if (hasHp)
                    {
                        hpState!.ScanHorizontal.ResetTotals();
                        hpState.ScanVertical.ResetTotals();
                    }
                }
                // jxrlib (segenc.c:138-142) emits both LP_QP_INDEX and
                // HP_QP_INDEX in EncodeMacroblockDC BEFORE any DC bits. We
                // match that order so the resulting bitstream is jxrlib-
                // compatible.
                if (lpState is not null)
                    QpIndex.Write(writer, numLpQPs, mb.LpQpIndex);
                if (hasHp)
                    QpIndex.Write(writer, numHpQPs, mb.HpQpIndex);
                MbDc.EncodeMb(writer, dcState, format, numComponents, mb.Dc);
                if (lpState is not null)
                {
                    MbLp.EncodeMb(writer, lpState, format, numComponents, mb.Lp);
                }
                if (hasHp)
                {
                    // CBPHP must appear in the bitstream before HP coefficient data,
                    // so compute it in a pre-pass and feed it to MbCbphp. For
                    // AllBands the CBPHP is computed from post-iModelBits-split
                    // values — snapshot iModelBits from hpState.Model BEFORE the
                    // MB's encode call (which will update the model at MB-end).
                    if (bands == JxrBandsPresent.AllBands)
                    {
                        MbHp.ComputeCbphpWithSplit(numComponents,
                            hpState!.Model.MBits0, hpState.Model.MBits1,
                            mb.Hp, cbphpBuf!);
                    }
                    else
                    {
                        MbHp.ComputeCbphp(numComponents, mb.Hp, cbphpBuf!);
                    }
                    MbCbphp.EncodeMb(writer, cbphpState!, format, numComponents,
                        mbX: col, mbY: row, isLeftEdge: col == 0, isTopEdge: row == 0, cbphpBuf!);
                    if (bands == JxrBandsPresent.AllBands)
                    {
                        MbHp.EncodeMb(writer, hpState!, mb.MbHpMode, format, numComponents,
                            trimFlexBits, mb.Hp, hpDummyCbphp!);
                    }
                    else
                    {
                        MbHp.EncodeMb(writer, hpState!, mb.MbHpMode, format, numComponents,
                            mb.Hp, hpDummyCbphp!);
                    }
                }
                // T.832 8.8.4: AdaptDC/LP/HP fire at end of MB on the bResetContext
                // boundary (last col in tile, or col at a 16-MB stride start).
                if (col == widthInMb - 1 || col % 16 == 0)
                {
                    dcState.Adapt();
                    lpState?.Adapt();
                    if (hasHp)
                    {
                        hpState!.Adapt();
                        cbphpState!.Adapt();
                    }
                }
            }
        }

        WriteByteAlignment(writer);
    }

    /// <summary>Read a complete TILE_SPATIAL block; mirror of <see cref="Write"/>.</summary>
    /// <summary>Compat overload — see <see cref="Write(BitWriter, TileBandHeaders, JxrBandsPresent, bool, JxrInternalColorFormat, int, int, int, Macroblock[])"/>.</summary>
    public static Macroblock[] Read(ref BitReader reader,
        JxrBandsPresent bands, bool trimFlexBitsFlag,
        JxrInternalColorFormat format, int numComponents,
        int widthInMb, int heightInMb, out TileBandHeaders headers)
        => Read(ref reader, bands, trimFlexBitsFlag,
            new ImagePlaneHeader
            {
                InternalClrFmt = format,
                NumComponents = numComponents,
                BandsPresent = bands,
            },
            widthInMb, heightInMb, out headers);

    public static Macroblock[] Read(
        ref BitReader reader,
        JxrBandsPresent bands,
        bool trimFlexBitsFlag,
        ImagePlaneHeader plane,
        int widthInMb,
        int heightInMb,
        out TileBandHeaders headers)
    {
        var format = plane.InternalClrFmt;
        var numComponents = plane.NumComponents;
        TileFrequency.ReadTileStartCode(ref reader);
        headers = TileBandHeaders.Read(ref reader, bands, trimFlexBitsFlag, plane);

        var dcState = new MbDcState();
        var lpState = bands != JxrBandsPresent.DcOnly ? new MbLpState() : null;
        var hasHp = bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass;
        var cbphpState = hasHp ? new MbCbphpState() : null;
        if (hasHp) cbphpState!.InitMbCbphpGrid(widthInMb, heightInMb, numComponents);
        var hpState = hasHp ? new MbHpState() : null;
        var cbphpBuf = hasHp ? new int[numComponents] : null;
        int numLpQPs = headers.Lowpass?.LpQp?.NumQPs ?? 1;
        int numHpQPs = headers.Highpass?.HpQp?.NumQPs ?? 1;
        var trimFlexBits = bands == JxrBandsPresent.AllBands
            ? (trimFlexBitsFlag ? headers.Highpass?.TrimFlexBits ?? 0 : 0)
            : 0;
        var mbs = new Macroblock[widthInMb * heightInMb];
        for (var row = 0; row < heightInMb; row++)
        {
            for (var col = 0; col < widthInMb; col++)
            {
                if (col % 16 == 0)
                {
                    lpState?.Scan.ResetTotals();
                    if (hasHp)
                    {
                        hpState!.ScanHorizontal.ResetTotals();
                        hpState.ScanVertical.ResetTotals();
                    }
                }
                var mb = new Macroblock { Dc = new int[numComponents] };
                // Match jxrlib's bitstream order: both QP indices first,
                // then DC band data. See encoder side comment.
                if (lpState is not null)
                    mb.LpQpIndex = QpIndex.Read(ref reader, numLpQPs);
                if (hasHp)
                    mb.HpQpIndex = QpIndex.Read(ref reader, numHpQPs);
                MbDc.DecodeMb(ref reader, dcState, format, numComponents, mb.Dc);
                if (lpState is not null)
                {
                    mb.Lp = new int[numComponents * 16];
                    MbLp.DecodeMb(ref reader, lpState, format, numComponents, mb.Lp);
                }
                if (hasHp)
                {
                    MbCbphp.DecodeMb(ref reader, cbphpState!, format, numComponents,
                        mbX: col, mbY: row, isLeftEdge: col == 0, isTopEdge: row == 0, cbphpBuf!);
                    mb.Hp = new int[numComponents * 256];
                    // mbHpMode must match the encoder's choice. T.832 derives it from
                    // the just-decoded LP coefficients of this MB — same input both
                    // sides see, so the choice matches.
                    mb.MbHpMode = DeriveMbHpMode(mb.Lp, format, numComponents);
                    if (bands == JxrBandsPresent.AllBands)
                    {
                        MbHp.DecodeMb(ref reader, hpState!, mb.MbHpMode, format, numComponents,
                            trimFlexBits, cbphpBuf!, mb.Hp);
                    }
                    else
                    {
                        MbHp.DecodeMb(ref reader, hpState!, mb.MbHpMode, format, numComponents,
                            cbphpBuf!, mb.Hp);
                    }
                }
                mbs[row * widthInMb + col] = mb;
                if (col == widthInMb - 1 || col % 16 == 0)
                {
                    dcState.Adapt();
                    lpState?.Adapt();
                    if (hasHp)
                    {
                        hpState!.Adapt();
                        cbphpState!.Adapt();
                    }
                }
            }
        }

        AlignToByte(ref reader);
        return mbs;
    }

    private static void ValidateBandsAndMbs(
        JxrBandsPresent bands, int widthInMb, int heightInMb, Macroblock[] mbs, int numComponents)
    {
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

    /// <summary>
    /// Derive mbHpMode from a just-decoded MB's LP coefficients — T.832 §9.6.3.2 /
    /// Table 135. Same input/output as <see cref="HpPrediction.CalcMode"/>, but
    /// works on the flat per-MB array directly so we don't need to allocate a
    /// singleton 4D buffer on the decode path.
    /// </summary>
    private static int DeriveMbHpMode(int[] mbLp, JxrInternalColorFormat format, int numComponents)
    {
        // Luma channel (component 0). T.832 Table 135 also folds in chroma
        // contributions for YUV* formats; for non-YUV (and YOnly / RGB / etc.)
        // only the luma plane contributes here, matching HpPrediction.CalcMode.
        var strHor = Abs(mbLp[1]) + Abs(mbLp[2]) + Abs(mbLp[3]);
        var strVer = Abs(mbLp[4]) + Abs(mbLp[8]) + Abs(mbLp[12]);

        if (format != JxrInternalColorFormat.YOnly && format != JxrInternalColorFormat.NComponent && numComponents >= 3)
        {
            for (var c = 1; c <= 2; c++)
            {
                var b = c * 16;
                strHor += Abs(mbLp[b + 1]);
                if (format == JxrInternalColorFormat.YUV420)
                {
                    strVer += Abs(mbLp[b + 2]);
                }
                else if (format == JxrInternalColorFormat.YUV422)
                {
                    strVer += Abs(mbLp[b + 2]) + Abs(mbLp[b + 6]);
                    strHor += Abs(mbLp[b + 5]);
                }
                else
                {
                    strVer += Abs(mbLp[b + 4]);
                }
            }
        }

        const int iOrWt = 4;
        if (strHor * iOrWt < strVer) return 0; // predict from left
        if (strVer * iOrWt < strHor) return 1; // predict from top
        return 2;                              // no prediction
    }

    private static int Abs(int x) => x < 0 ? -x : x;

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
