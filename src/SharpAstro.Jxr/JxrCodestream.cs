namespace SharpAstro.Jxr;

/// <summary>
/// Assembles / parses a complete single-tile <b>SPATIAL</b>, <b>OL_NONE</b>,
/// <b>YUV444</b>, <b>BD8 RGB</b> JPEG XR codestream — the "Rung 7e" bridge
/// between the working OL_NONE codec (<see cref="TileImageCodec"/>) and the
/// container (<see cref="JxrContainer"/>). The byte layout mirrors jxrlib's
/// reference encoder exactly (verified against <c>JxrEncApp -f -l 0 -d 3 -q 1</c>):
/// <code>
///   IMAGE_HEADER         (T.832 §8.3)   — WMPHOTO sig + flags + dims
///   IMAGE_PLANE_HEADER   (T.832 §8.4)   — YUV444, all bands, uniform QP (uQPMode 0x750)
///   PROFILE_LEVEL_INFO                  — 00 04 6f ff 00 01 (writeIndexTableNull, cNumBitIO==0)
///   PACKET_HEADER                       — 00 00 01 00 (spatial, tile 0)
///   CODED_TILE (spatial)                — per MB: DC, LP, HP(CBP + per-subblock AC/FL)
/// </code>
/// In SPATIAL mode jxrlib aliases all four band bit-streams to one stream
/// (strcodec.c:771), so the four band coders are driven through a single
/// <see cref="BitWriter"/>/<see cref="BitReader"/> — the per-MB write order
/// (DC → LP → CBP → per-subblock AC then FL) already matches segenc.c.
/// </summary>
internal static class JxrCodestream
{
    // jxrlib writeIndexTableNull (cNumBitIO==0): a 4-byte "subsequent bytes" vlw_esc
    // followed by the default conformance record + LAST_FLAG.
    private const int DefaultProfileIdc = 111; // 0x6f
    private const int DefaultLevelIdc = 255;   // 0xff

    // Trailing zero bytes appended to the decode buffer so the entropy decoder's
    // speculative peeks past the last macroblock never overrun the codestream.
    private const int EndPeekSlackBytes = 16;

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> BD8 RGB image
    /// (each channel <c>width*height</c> samples, raster order) into a JXR codestream.
    /// Dimensions must be multiples of 16. QP indices are 0 for lossless.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
                                int width, int height, int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        RequireMbAligned(width, height);
        int mbCols = width / 16, mbRows = height / 16;
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap);
        WritePlaneHeader(w, qpDc, qpLp, qpHp, scaled);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w);

        // Color-transform + load every macroblock into the whole-image YUV planes,
        // then run the overlap + 2-stage PCT across the grid (jxrlib's sliding
        // 2-MB-row window), then per-MB quantize + entropy code.
        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows);
        var (mr, mg, mb) = (new int[256], new int[256], new int[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMb(r, g, b, width, mbR, mbC, mr, mg, mb);
                SignalTransform.LoadColor(mr, mg, mb, planes[0], planes[1], planes[2],
                                          OverlapTransform.MbBase(mbCols, mbR, mbC));
            }

        OverlapTransform.Forward(planes, mbCols, mbRows, overlap, scaled);

        // SPATIAL: all four band streams alias one writer (BitWriter is a class).
        var ctx = new CodingContext(ColorFormat.Yuv444, 3);
        var tile = new TileCoder(mbCols);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(3);
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                for (var ch = 0; ch < 3; ch++)
                    SignalTransform.QuantizeExtract(planes[ch], baseOff, block, ch, qDc, qLp, qHp);
                tile.EncodeMacroblock(ctx, block, mbC, mbR, w, w, w, w);
            }
            tile.AdvanceRow();
        }

        FillToByte(w);
        return w.ToArray();
    }

    /// <summary>
    /// Decode a single-tile SPATIAL OL_NONE YUV444 BD8 JXR codestream back into a BD8 RGB
    /// image. Dimensions and per-band QP indices are read from the codestream headers.
    /// </summary>
    public static (int width, int height, int[] r, int[] g, int[] b) Decode(ReadOnlySpan<byte> codestream)
    {
        // jxrlib's BitIO always has a zero-filled packet buffer beyond the coded
        // bytes, so the entropy decoder's speculative peeks (VLC root, abs-level
        // escape) never read past valid memory. Mirror that with trailing zero
        // slack so the final macroblock's peeks don't overrun a tight buffer.
        var padded = new byte[codestream.Length + EndPeekSlackBytes];
        codestream.CopyTo(padded);
        var reader = new BitReader(padded);
        var ih = ImageHeader.Read(ref reader);
        if (ih.FrequencyModeCodestreamFlag)
            throw new NotSupportedException("JxrCodestream decodes SPATIAL codestreams only.");
        if (ih.OverlapMode > 2)
            throw new NotSupportedException($"JxrCodestream decodes OL_NONE / OL_ONE / OL_TWO only (got overlap mode {ih.OverlapMode}).");
        int overlap = ih.OverlapMode;

        int width = (int)ih.WidthMinus1 + 1, height = (int)ih.HeightMinus1 + 1;
        RequireMbAligned(width, height);
        var (qpDc, qpLp, qpHp, scaled) = ReadPlaneHeader(ref reader);
        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = width / 16, mbRows = height / 16;
        var (r, g, b) = (new int[width * height], new int[width * height], new int[width * height]);

        // Per-MB entropy decode + dequantize into the whole-image YUV planes, then
        // run the inverse overlap + 2-stage PCT across the grid, then color-store.
        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows);
        var ctx = new CodingContext(ColorFormat.Yuv444, 3);
        var tile = new TileCoder(mbCols);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(3);
                // SPATIAL: alias one reader to all four band slots (BitReader is a ref struct).
                tile.DecodeMacroblock(ctx, block, mbC, mbR, ref reader, ref reader, ref reader, ref reader, qHp.Qp);
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                for (var ch = 0; ch < 3; ch++)
                    SignalTransform.DequantizeRestore(block, ch, planes[ch], baseOff, qDc.Qp, qLp.Qp);
            }
            tile.AdvanceRow();
        }

        OverlapTransform.Inverse(planes, mbCols, mbRows, overlap, scaled);

        var (mr, mg, mb) = (new int[256], new int[256], new int[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.StoreColor(planes[0], planes[1], planes[2], baseOff, mr, mg, mb);
                StoreMb(r, g, b, width, mbR, mbC, mr, mg, mb);
            }
        return (width, height, r, g, b);
    }

    // ---------------------------------------------------------------- headers

    private static void WriteImageHeader(BitWriter w, int width, int height, int overlap)
    {
        int mbW = (width + 15) / 16, mbH = (height + 15) / 16;
        var ih = new ImageHeader
        {
            HardTilingFlag = false,
            TilingFlag = false,
            FrequencyModeCodestreamFlag = false,           // SPATIAL
            SpatialXfrmSubordinate = 0,
            IndexTablePresentFlag = false,
            OverlapMode = overlap,                          // OL_NONE / OL_ONE / OL_TWO
            ShortHeaderFlag = mbW <= 255 && mbH <= 255,     // jxrlib bAbbreviatedHeader
            LongWordFlag = true,                            // jxrlib always writes 1 here
            WindowingFlag = false,
            TrimFlexBitsFlag = false,
            RedBlueNotSwappedFlag = false,
            PremultipliedAlphaFlag = false,
            AlphaImagePlaneFlag = false,
            OutputClrFmt = JxrOutputColorFormat.Rgb,        // CF_RGB = 7
            OutputBitDepth = JxrOutputBitDepth.Bd8,         // BD_8 = 1
            WidthMinus1 = (uint)(width - 1),
            HeightMinus1 = (uint)(height - 1),
        };
        ih.Write(w);
    }

    // Faithful port of jxrlib WriteImagePlaneHeader (strenc.c:748) for the
    // YUV444 / BD8 / all-bands / uQPMode==0x750 case: every band carries its own
    // plane-uniform quantizer in channel-mode INDEPENDENT (2), three equal QP indices.
    private static void WritePlaneHeader(BitWriter w, int qpDc, int qpLp, int qpHp, bool scaled)
    {
        w.WriteBits((uint)JxrInternalColorFormat.YUV444, 3); // internal color format
        w.WriteBit(scaled);                                  // bScaledArith
        w.WriteBits((uint)JxrBandsPresent.AllBands, 4);      // SB_ALL
        w.WriteBits(0, 4); w.WriteBits(0, 4);                // YUV: RESERVED_F, RESERVED_H
        // BD8 → no SHIFT_BITS / LEN_MANTISSA.

        // DC: uniform flag = 1, then quantizer (chMode INDEPENDENT, 3 equal indices).
        w.WriteBit(true);
        WriteQuantizer(w, qpDc);

        // LP: USE_DC_QP flag = 0 (uQPMode|0x200 ⇒ own QP), uniform flag = 1, quantizer.
        w.WriteBit(false);
        w.WriteBit(true);
        WriteQuantizer(w, qpLp);

        // HP: USE_LP_QP flag = 0 (uQPMode|0x400 ⇒ own QP), uniform flag = 1, quantizer.
        w.WriteBit(false);
        w.WriteBit(true);
        WriteQuantizer(w, qpHp);

        FillToByte(w);
    }

    private static (int qpDc, int qpLp, int qpHp, bool scaled) ReadPlaneHeader(ref BitReader r)
    {
        var clrFmt = (JxrInternalColorFormat)r.ReadBits(3);
        if (clrFmt != JxrInternalColorFormat.YUV444)
            throw new NotSupportedException($"JxrCodestream decodes YUV444 internal format only (got {clrFmt}).");
        bool scaled = r.ReadBit();
        var bands = (JxrBandsPresent)r.ReadBits(4);
        if (bands != JxrBandsPresent.AllBands)
            throw new NotSupportedException($"JxrCodestream decodes all-bands codestreams only (got {bands}).");
        r.SkipBits(8); // RESERVED_F + RESERVED_H

        if (!r.ReadBit()) throw new NotSupportedException("Non-uniform DC quantization not supported.");
        int qpDc = ReadQuantizer(ref r);

        if (r.ReadBit()) throw new NotSupportedException("LP reusing DC quantizer not supported by this writer's mirror.");
        if (!r.ReadBit()) throw new NotSupportedException("Non-uniform LP quantization not supported.");
        int qpLp = ReadQuantizer(ref r);

        if (r.ReadBit()) throw new NotSupportedException("HP reusing LP quantizer not supported by this writer's mirror.");
        if (!r.ReadBit()) throw new NotSupportedException("Non-uniform HP quantization not supported.");
        int qpHp = ReadQuantizer(ref r);

        AlignToByte(ref r);
        return (qpDc, qpLp, qpHp, scaled);
    }

    // jxrlib writeQuantizer (strenc.c:59) for 3 channels in channel-mode INDEPENDENT (2):
    // PUTBITS(chMode,2) then Y, U, V QP indices. Our codec uses one QP per band, so all three equal.
    private static void WriteQuantizer(BitWriter w, int qpIndex)
    {
        w.WriteBits(2, 2);                       // cChMode = INDEPENDENT
        w.WriteBits((uint)qpIndex, 8);           // Y
        w.WriteBits((uint)qpIndex, 8);           // U
        w.WriteBits((uint)qpIndex, 8);           // V
    }

    private static int ReadQuantizer(ref BitReader r)
    {
        int chMode = (int)r.ReadBits(2);
        int y = (int)r.ReadBits(8);
        if (chMode == 1) { r.SkipBits(8); }              // MIXED: one chroma value
        else if (chMode == 2) { r.SkipBits(8); r.SkipBits(8); } // INDEPENDENT: U, V
        // chMode 0 (UNIFORM): no extra. We only consume Y; bands share one index in our codec.
        return y;
    }

    private static void WriteProfileLevelInfo(BitWriter w)
    {
        // jxrlib writeIndexTableNull: PutVLWordEsc(0,4) then profile/level/LAST_FLAG.
        WriteVlwEsc(w, 4);
        w.WriteBits(DefaultProfileIdc, 8);
        w.WriteBits(DefaultLevelIdc, 8);
        w.WriteBits(1, 16); // LAST_FLAG = 1 (single conformance record)
    }

    private static void ReadProfileLevelInfo(ref BitReader r)
    {
        AlignToByte(ref r);
        long subsequent = ReadVlwEsc(ref r);
        // The "subsequent bytes" count covers profile+level+last-flag; skip them.
        r.SkipBits((int)subsequent * 8);
    }

    private static void WritePacketHeader(BitWriter w)
    {
        // writePacketHeader(pIO, ptPacketType=0 spatial, pID=0): 00 00 01 00.
        w.WriteBits(0, 8); w.WriteBits(0, 8); w.WriteBits(1, 8); w.WriteBits(0, 8);
    }

    private static void ReadPacketHeader(ref BitReader r)
    {
        uint b0 = r.ReadBits(8), b1 = r.ReadBits(8), b2 = r.ReadBits(8), b3 = r.ReadBits(8);
        if (b0 != 0 || b1 != 0 || b2 != 1)
            throw new InvalidDataException($"Bad spatial packet header {b0:X2} {b1:X2} {b2:X2} {b3:X2} (expected 00 00 01 xx).");
    }

    // ---------------------------------------------------------------- vlw_esc (T.832 §8.2.4)

    private static void WriteVlwEsc(BitWriter w, long value)
    {
        if (value < 0xFB00) w.WriteBits((uint)value, 16);
        else { w.WriteBits(0xFB, 8); w.WriteBits((uint)value, 32); }
    }

    private static long ReadVlwEsc(ref BitReader r)
    {
        uint first = r.ReadBits(8);
        if (first < 0xFB) return (first << 8) | r.ReadBits(8);
        if (first == 0xFB) return r.ReadBits(32);
        if (first == 0xFC) { uint hi = r.ReadBits(32), lo = r.ReadBits(32); return ((long)hi << 32) | lo; }
        return 0; // 0xFD/0xFE/0xFF parser escape
    }

    // ---------------------------------------------------------------- helpers

    private static (JxrQuantizer dc, JxrQuantizer lp, JxrQuantizer hp) Quantizers(int qpDc, int qpLp, int qpHp, bool scaled)
        => (Quantization.Resolve(qpDc, scaled), Quantization.Resolve(qpLp, scaled), Quantization.Resolve(qpHp, scaled));

    // jxrlib StrEncInit: lossless (all bands QP index ≤ 1) ⇒ bScaledArith == FALSE.
    private static bool ScaledArith(int qpDc, int qpLp, int qpHp) => qpDc > 1 || qpLp > 1 || qpHp > 1;

    private static void FillToByte(BitWriter w)
    {
        while ((w.BitPosition & 7) != 0) w.WriteBit(false);
    }

    private static void AlignToByte(ref BitReader r)
    {
        int slack = (8 - (r.BitPosition & 7)) & 7;
        if (slack > 0) r.SkipBits(slack);
    }

    private static void RequireMbAligned(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 15) != 0 || (height & 15) != 0)
            throw new ArgumentException("JxrCodestream requires width and height that are positive multiples of 16.");
    }

    private static void ExtractMb(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b, int width,
                                  int mbR, int mbC, int[] mr, int[] mg, int[] mb)
    {
        for (var row = 0; row < 16; row++)
        {
            int src = (mbR * 16 + row) * width + mbC * 16;
            int dst = row * 16;
            for (var col = 0; col < 16; col++)
            {
                mr[dst + col] = r[src + col];
                mg[dst + col] = g[src + col];
                mb[dst + col] = b[src + col];
            }
        }
    }

    private static void StoreMb(Span<int> r, Span<int> g, Span<int> b, int width,
                                int mbR, int mbC, int[] mr, int[] mg, int[] mb)
    {
        for (var row = 0; row < 16; row++)
        {
            int dst = (mbR * 16 + row) * width + mbC * 16;
            int src = row * 16;
            for (var col = 0; col < 16; col++)
            {
                r[dst + col] = mr[src + col];
                g[dst + col] = mg[src + col];
                b[dst + col] = mb[src + col];
            }
        }
    }
}
