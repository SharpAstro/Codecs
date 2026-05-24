namespace SharpAstro.Jxr;

/// <summary>
/// Frequency-mode tile orchestrator — T.832 §8.7. Where
/// <see cref="TileSpatial"/> interleaves all bands per macroblock
/// (<c>MB_DC → MB_LP → MB_CBPHP → MB_HP</c> together per MB), frequency mode
/// pulls each band onto its own sub-stream and ships them back-to-back:
/// all DC for all MBs first, then all LP, then all CBPHP+HP. Each
/// sub-stream is prefixed by its own band header
/// (<see cref="TileHeaderDc"/>, <see cref="TileHeaderLowpass"/>,
/// <see cref="TileHeaderHighpass"/>) and ends on a byte boundary.
/// </summary>
/// <remarks>
/// <para>This is the wire format external producers (PIX dump, jxrlib's
/// default, the seagull/HDR-float fixtures) use, so frequency-mode read
/// support unlocks decoding files we didn't author.</para>
/// <para>Currently supported <c>BANDS_PRESENT</c> values: <c>DcOnly</c>,
/// <c>NoHighpass</c>, <c>NoFlexbits</c>. <c>AllBands</c> (which adds a
/// fourth FlexBits sub-stream) is not supported yet — same restriction as
/// <see cref="TileSpatial"/>.</para>
/// <para>The per-band state machines (<see cref="MbDcState"/>,
/// <see cref="MbLpState"/>, <see cref="MbCbphpState"/>,
/// <see cref="MbHpState"/>) are independent: DC predictions only look at
/// DC neighbours, LP at LP neighbours, etc. So running each band as its
/// own raster-order pass produces the same syntax-element stream as the
/// per-MB interleaved order — only the bit layout differs.</para>
/// </remarks>
public static class TileFrequency
{
    /// <summary>
    /// Number of band sub-streams emitted for a given <c>BANDS_PRESENT</c>:
    /// DC → 1, NoHighpass → 2, NoFlexbits → 3, AllBands → 4 (when supported).
    /// Useful for sizing INDEX_TABLE_TILES in frequency mode.
    /// </summary>
    public static int BandCount(JxrBandsPresent bands) => bands switch
    {
        JxrBandsPresent.DcOnly => 1,
        JxrBandsPresent.NoHighpass => 2,
        JxrBandsPresent.NoFlexbits => 3,
        JxrBandsPresent.AllBands => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(bands)),
    };

    /// <summary>
    /// Encode the tile in frequency mode, returning one byte array per
    /// present band. The caller concatenates them (in order) to form the
    /// tile's coded body; INDEX_TABLE_TILES offsets are the running sum of
    /// element lengths. Each returned buffer already ends on a byte
    /// boundary, so concatenation needs no re-alignment.
    /// </summary>
    public static byte[][] WriteBands(
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

        var bandCount = BandCount(bands);
        var result = new byte[bandCount][];
        var mbCount = widthInMb * heightInMb;

        // --- DC sub-stream ---
        var dcW = new BitWriter();
        headers.Dc.Write(dcW);
        var dcState = new MbDcState();
        for (var i = 0; i < mbCount; i++)
            MbDc.EncodeMb(dcW, dcState, format, numComponents, mbs[i].Dc);
        ByteAlign(dcW);
        result[0] = dcW.ToArray();

        if (bands == JxrBandsPresent.DcOnly) return result;

        // --- LP sub-stream ---
        var lpW = new BitWriter();
        headers.Lowpass!.Write(lpW);
        var lpState = new MbLpState();
        for (var i = 0; i < mbCount; i++)
            MbLp.EncodeMb(lpW, lpState, format, numComponents, mbs[i].Lp);
        ByteAlign(lpW);
        result[1] = lpW.ToArray();

        if (bands == JxrBandsPresent.NoHighpass) return result;

        // --- HP sub-stream (CBPHP + HP interleaved per MB) ---
        var hpW = new BitWriter();
        headers.Highpass!.Write(hpW, trimFlexBitsFlag);
        var cbphpState = new MbCbphpState();
        var hpState = new MbHpState();
        var cbphpBuf = new int[numComponents];
        var hpDummyCbphp = new int[numComponents];
        for (var i = 0; i < mbCount; i++)
        {
            MbHp.ComputeCbphp(numComponents, mbs[i].Hp, cbphpBuf);
            MbCbphp.EncodeMb(hpW, cbphpState, numComponents, cbphpBuf);
            MbHp.EncodeMb(hpW, hpState, mbs[i].MbHpMode, format, numComponents, mbs[i].Hp, hpDummyCbphp);
        }
        ByteAlign(hpW);
        result[2] = hpW.ToArray();

        return result;
    }

    /// <summary>
    /// Encode the tile in frequency mode by streaming the bands consecutively
    /// into <paramref name="writer"/>. Equivalent to calling
    /// <see cref="WriteBands"/> and writing the resulting byte arrays in
    /// order. Use this when there's no INDEX_TABLE_TILES to populate.
    /// </summary>
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
        var bandBytes = WriteBands(headers, bands, trimFlexBitsFlag, format, numComponents, widthInMb, heightInMb, mbs);
        // Writer state must be byte-aligned for splicing pre-encoded sub-streams;
        // the callers (CodedImage.Encode tile loops) hold this invariant.
        if ((writer.BitPosition & 7) != 0)
            throw new InvalidOperationException(
                "TileFrequency.Write requires writer to be byte-aligned when called");
        foreach (var band in bandBytes)
            for (var i = 0; i < band.Length; i++)
                writer.WriteBits(band[i], 8);
    }

    /// <summary>Read a frequency-mode tile; mirror of <see cref="Write"/>.</summary>
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
            throw new NotSupportedException("TILE_FREQUENCY.Read AllBands (with FlexBits refinement) not yet supported");

        var mbCount = widthInMb * heightInMb;
        var mbs = new Macroblock[mbCount];
        for (var i = 0; i < mbCount; i++)
            mbs[i] = new Macroblock { Dc = new int[numComponents] };

        // --- DC sub-stream ---
        var dcHdr = TileHeaderDc.Read(ref reader);
        var dcState = new MbDcState();
        for (var i = 0; i < mbCount; i++)
            MbDc.DecodeMb(ref reader, dcState, format, numComponents, mbs[i].Dc);
        AlignToByte(ref reader);

        TileHeaderLowpass? lpHdr = null;
        TileHeaderHighpass? hpHdr = null;

        if (bands != JxrBandsPresent.DcOnly)
        {
            // --- LP sub-stream ---
            lpHdr = TileHeaderLowpass.Read(ref reader);
            var lpState = new MbLpState();
            for (var i = 0; i < mbCount; i++)
            {
                mbs[i].Lp = new int[numComponents * 16];
                MbLp.DecodeMb(ref reader, lpState, format, numComponents, mbs[i].Lp);
            }
            AlignToByte(ref reader);
        }

        if (bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass)
        {
            // --- HP sub-stream (CBPHP + HP interleaved per MB) ---
            hpHdr = TileHeaderHighpass.Read(ref reader, trimFlexBitsFlag);
            var cbphpState = new MbCbphpState();
            var hpState = new MbHpState();
            var cbphpBuf = new int[numComponents];
            for (var i = 0; i < mbCount; i++)
            {
                MbCbphp.DecodeMb(ref reader, cbphpState, numComponents, cbphpBuf);
                mbs[i].Hp = new int[numComponents * 256];
                // mbHpMode is derived from this MB's LP coefficients (already decoded
                // in the LP pass above), matching what the encoder saw — same input
                // both sides agree on.
                mbs[i].MbHpMode = DeriveMbHpMode(mbs[i].Lp, format, numComponents);
                MbHp.DecodeMb(ref reader, hpState, mbs[i].MbHpMode, format, numComponents, cbphpBuf, mbs[i].Hp);
            }
            AlignToByte(ref reader);
        }

        headers = new TileBandHeaders { Dc = dcHdr, Lowpass = lpHdr, Highpass = hpHdr };
        return mbs;
    }

    private static void ValidateBandsAndMbs(
        JxrBandsPresent bands, int widthInMb, int heightInMb, Macroblock[] mbs, int numComponents)
    {
        if (bands == JxrBandsPresent.AllBands)
            throw new NotSupportedException("TILE_FREQUENCY.Write AllBands (with FlexBits refinement) not yet supported");
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

    // Same mbHpMode derivation as TileSpatial — see TileSpatial.DeriveMbHpMode
    // for the spec rationale (T.832 §9.6.3.2 / Table 135).
    private static int DeriveMbHpMode(int[] mbLp, JxrInternalColorFormat format, int numComponents)
    {
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
        if (strHor * iOrWt < strVer) return 0;
        if (strVer * iOrWt < strHor) return 1;
        return 2;
    }

    private static int Abs(int x) => x < 0 ? -x : x;

    private static void ByteAlign(BitWriter writer)
    {
        while ((writer.BitPosition & 7) != 0) writer.WriteBit(false);
    }

    private static void AlignToByte(ref BitReader reader)
    {
        var slack = (8 - (reader.BitPosition & 7)) & 7;
        if (slack > 0) reader.SkipBits(slack);
    }
}
