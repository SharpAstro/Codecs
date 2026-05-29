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
                                int width, int height, int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0,
                                JxrOutputBitDepth bd = JxrOutputBitDepth.Bd8)
    {
        RequireMbAligned(width, height);
        int mbCols = width / 16, mbRows = height / 16;
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int bias = LumaBias(bd);

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.Rgb, bd);
        WritePlaneHeader(w, qpDc, qpLp, qpHp, scaled, bd);
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
                                          OverlapTransform.MbBase(mbCols, mbR, mbC), bias);
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
    /// Encode a <paramref name="width"/>×<paramref name="height"/> BD8 grayscale image
    /// (<c>width*height</c> samples, raster order, values 0..255) into a single-tile
    /// SPATIAL <b>Y-only</b> JXR codestream. No colour transform — the single channel is
    /// the Y plane. Dimensions must be multiples of 16; QP indices are 0 for lossless.
    /// </summary>
    public static byte[] EncodeGray(ReadOnlySpan<int> y, int width, int height,
                                    int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0,
                                    JxrOutputBitDepth bd = JxrOutputBitDepth.Bd8)
    {
        RequireMbAligned(width, height);
        int mbCols = width / 16, mbRows = height / 16;
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int bias = LumaBias(bd);

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.YOnly, bd);
        WritePlaneHeaderGray(w, qpDc, qpLp, qpHp, scaled, bd);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w);

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1);
        var my = new int[256];
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMbGray(y, width, mbR, mbC, my);
                SignalTransform.LoadGray(my, planes[0], OverlapTransform.MbBase(mbCols, mbR, mbC), bias);
            }

        OverlapTransform.Forward(planes, mbCols, mbRows, overlap, scaled);

        var ctx = new CodingContext(ColorFormat.YOnly, 1);
        var tile = new TileCoder(mbCols, 1, ColorFormat.YOnly);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(1);
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.QuantizeExtract(planes[0], baseOff, block, 0, qDc, qLp, qHp);
                tile.EncodeMacroblock(ctx, block, mbC, mbR, w, w, w, w);
            }
            tile.AdvanceRow();
        }

        FillToByte(w);
        return w.ToArray();
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD32F</b> grayscale image
    /// (<c>width*height</c> floats, raster order, values verbatim — NOT normalized) into a single-
    /// tile SPATIAL Y-only JXR codestream. <paramref name="lenMantissa"/> (mantissa bits, jxrlib
    /// default 13) and <paramref name="expBias"/> (exponent bias, jxrlib default 0) parameterize
    /// the float↔pixel mapping and are written to the plane header. Dimensions must be multiples
    /// of 16; QP indices are 0 for lossless (the codec is lossless on the float-pixel values).
    /// </summary>
    public static byte[] EncodeGrayF32(ReadOnlySpan<float> y, int width, int height,
                                       int lenMantissa = 13, int expBias = 0,
                                       int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        RequireMbAligned(width, height);
        int mbCols = width / 16, mbRows = height / 16;
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.YOnly, JxrOutputBitDepth.Bd32F);
        WritePlaneHeaderGray(w, qpDc, qpLp, qpHp, scaled, JxrOutputBitDepth.Bd32F, lenMantissa, expBias);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w);

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1);
        var my = new float[256];
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMbGrayF(y, width, mbR, mbC, my);
                SignalTransform.LoadGrayFloat(my, planes[0], OverlapTransform.MbBase(mbCols, mbR, mbC), expBias, lenMantissa);
            }

        OverlapTransform.Forward(planes, mbCols, mbRows, overlap, scaled);

        var ctx = new CodingContext(ColorFormat.YOnly, 1);
        var tile = new TileCoder(mbCols, 1, ColorFormat.YOnly);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(1);
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.QuantizeExtract(planes[0], baseOff, block, 0, qDc, qLp, qHp);
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
        var bd = ih.OutputBitDepth;
        int bias = LumaBias(bd), max = SampleMax(bd);
        var (qpDc, qpLp, qpHp, scaled) = ReadPlaneHeader(ref reader, bd);
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
                SignalTransform.StoreColor(planes[0], planes[1], planes[2], baseOff, mr, mg, mb, bias, max);
                StoreMb(r, g, b, width, mbR, mbC, mr, mg, mb);
            }
        return (width, height, r, g, b);
    }

    /// <summary>
    /// Decode a single-tile SPATIAL Y-only BD8 JXR codestream back into a BD8 grayscale
    /// image. Dimensions and per-band QP indices are read from the codestream headers.
    /// </summary>
    public static (int width, int height, int[] y) DecodeGray(ReadOnlySpan<byte> codestream)
    {
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
        var bd = ih.OutputBitDepth;
        int bias = LumaBias(bd), max = SampleMax(bd);
        var (qpDc, qpLp, qpHp, scaled, _, _) = ReadPlaneHeaderGray(ref reader, bd);
        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = width / 16, mbRows = height / 16;
        var y = new int[width * height];

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1);
        var ctx = new CodingContext(ColorFormat.YOnly, 1);
        var tile = new TileCoder(mbCols, 1, ColorFormat.YOnly);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(1);
                tile.DecodeMacroblock(ctx, block, mbC, mbR, ref reader, ref reader, ref reader, ref reader, qHp.Qp);
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.DequantizeRestore(block, 0, planes[0], baseOff, qDc.Qp, qLp.Qp);
            }
            tile.AdvanceRow();
        }

        OverlapTransform.Inverse(planes, mbCols, mbRows, overlap, scaled);

        var my = new int[256];
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.StoreGray(planes[0], baseOff, my, bias, max);
                StoreMbGray(y, width, mbR, mbC, my);
            }
        return (width, height, y);
    }

    /// <summary>
    /// Decode a single-tile SPATIAL Y-only <b>BD32F</b> JXR codestream back into a grayscale float
    /// image. Dimensions, per-band QP, and the float mapping (LEN_MANTISSA / EXP_BIAS) are read
    /// from the codestream headers.
    /// </summary>
    public static (int width, int height, float[] y) DecodeGrayF32(ReadOnlySpan<byte> codestream)
    {
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
        var (qpDc, qpLp, qpHp, scaled, lenMantissa, expBias) = ReadPlaneHeaderGray(ref reader, ih.OutputBitDepth);
        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = width / 16, mbRows = height / 16;
        var y = new float[width * height];

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1);
        var ctx = new CodingContext(ColorFormat.YOnly, 1);
        var tile = new TileCoder(mbCols, 1, ColorFormat.YOnly);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(1);
                tile.DecodeMacroblock(ctx, block, mbC, mbR, ref reader, ref reader, ref reader, ref reader, qHp.Qp);
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.DequantizeRestore(block, 0, planes[0], baseOff, qDc.Qp, qLp.Qp);
            }
            tile.AdvanceRow();
        }

        OverlapTransform.Inverse(planes, mbCols, mbRows, overlap, scaled);

        var my = new float[256];
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.StoreGrayFloat(planes[0], baseOff, my, expBias, lenMantissa);
                StoreMbGrayF(y, width, mbR, mbC, my);
            }
        return (width, height, y);
    }

    // ---------------------------------------------------------------- headers

    private static void WriteImageHeader(BitWriter w, int width, int height, int overlap,
                                         JxrOutputColorFormat clrFmt = JxrOutputColorFormat.Rgb,
                                         JxrOutputBitDepth bitDepth = JxrOutputBitDepth.Bd8)
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
            OutputClrFmt = clrFmt,                          // CF_RGB = 7 / CF_YONLY = 0
            OutputBitDepth = bitDepth,                      // BD_8 = 1
            WidthMinus1 = (uint)(width - 1),
            HeightMinus1 = (uint)(height - 1),
        };
        ih.Write(w);
    }

    // Faithful port of jxrlib WriteImagePlaneHeader (strenc.c:748) for the
    // YUV444 / BD8 / all-bands / uQPMode==0x750 case: every band carries its own
    // plane-uniform quantizer in channel-mode INDEPENDENT (2), three equal QP indices.
    private static void WritePlaneHeader(BitWriter w, int qpDc, int qpLp, int qpHp, bool scaled, JxrOutputBitDepth bd,
                                         int lenMantissaOrShift = 0, int expBias = 0)
    {
        w.WriteBits((uint)JxrInternalColorFormat.YUV444, 3); // internal color format
        w.WriteBit(scaled);                                  // bScaledArith
        w.WriteBits((uint)JxrBandsPresent.AllBands, 4);      // SB_ALL
        w.WriteBits(0, 4); w.WriteBits(0, 4);                // YUV: RESERVED_F, RESERVED_H
        WriteBitDepthParams(w, bd, lenMantissaOrShift, expBias); // SHIFT_BITS/LEN_MANTISSA+EXP_BIAS

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

    private static (int qpDc, int qpLp, int qpHp, bool scaled) ReadPlaneHeader(ref BitReader r, JxrOutputBitDepth bd)
    {
        var clrFmt = (JxrInternalColorFormat)r.ReadBits(3);
        if (clrFmt != JxrInternalColorFormat.YUV444)
            throw new NotSupportedException($"JxrCodestream decodes YUV444 internal format only (got {clrFmt}).");
        bool scaled = r.ReadBit();
        var bands = (JxrBandsPresent)r.ReadBits(4);
        if (bands != JxrBandsPresent.AllBands)
            throw new NotSupportedException($"JxrCodestream decodes all-bands codestreams only (got {bands}).");
        r.SkipBits(8); // RESERVED_F + RESERVED_H
        ReadBitDepthParams(ref r, bd); // SHIFT_BITS for BD16 (RGB int path ignores the value)

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

    // jxrlib WriteImagePlaneHeader bit-depth params (strenc.c:777): BD16/BD16S write an 8-bit
    // SHIFT_BITS (the integer right-shift, 0 ⇒ full-precision lossless); BD32F writes LEN_MANTISSA(8)
    // then EXP_BIAS(8) for the custom-float mapping. BD8 and BD16F write nothing (BD16F's mantissa
    // length is implicit). `lenMantissaOrShift` is SHIFT_BITS for BD16 / LEN_MANTISSA for BD32F.
    private static void WriteBitDepthParams(BitWriter w, JxrOutputBitDepth bd, int lenMantissaOrShift, int expBias)
    {
        switch (bd)
        {
            case JxrOutputBitDepth.Bd16:
                w.WriteBits((uint)lenMantissaOrShift, 8);
                break;
            case JxrOutputBitDepth.Bd32F:
                w.WriteBits((uint)lenMantissaOrShift, 8);  // LEN_MANTISSA
                w.WriteBits((byte)expBias, 8);             // EXP_BIAS (signed, low 8 bits)
                break;
        }
    }

    private static (int lenMantissaOrShift, int expBias) ReadBitDepthParams(ref BitReader r, JxrOutputBitDepth bd)
    {
        switch (bd)
        {
            case JxrOutputBitDepth.Bd16:
                return ((int)r.ReadBits(8), 0);
            case JxrOutputBitDepth.Bd32F:
                int lm = (int)r.ReadBits(8);
                int c = (sbyte)r.ReadBits(8);             // EXP_BIAS is signed
                return (lm, c);
            default:
                return (0, 0);
        }
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

    // Faithful port of WriteImagePlaneHeader for the YONLY / BD8 / all-bands / uQPMode==0x750
    // case. Differs from the YUV444 writer in two ways (strenc.c:772 default + writeQuantizer
    // cChannel==1): no RESERVED_F/RESERVED_H bytes, and each band's quantizer is just the 8-bit
    // Y index (writeQuantizer forces cChMode 0 and writes no channel-mode bits for one channel).
    private static void WritePlaneHeaderGray(BitWriter w, int qpDc, int qpLp, int qpHp, bool scaled, JxrOutputBitDepth bd,
                                             int lenMantissaOrShift = 0, int expBias = 0)
    {
        w.WriteBits((uint)JxrInternalColorFormat.YOnly, 3); // internal color format = 0
        w.WriteBit(scaled);                                 // bScaledArith
        w.WriteBits((uint)JxrBandsPresent.AllBands, 4);     // SB_ALL
        // YONLY: color-params switch hits default — no RESERVED_F / RESERVED_H.
        WriteBitDepthParams(w, bd, lenMantissaOrShift, expBias); // SHIFT_BITS/LEN_MANTISSA+EXP_BIAS

        w.WriteBit(true);                 // DC uniform
        w.WriteBits((uint)qpDc, 8);       // single-channel quantizer (Y index only)

        w.WriteBit(false);                // use DC QP? no (own LP QP)
        w.WriteBit(true);                 // LP uniform
        w.WriteBits((uint)qpLp, 8);

        w.WriteBit(false);                // use LP QP? no (own HP QP)
        w.WriteBit(true);                 // HP uniform
        w.WriteBits((uint)qpHp, 8);

        FillToByte(w);
    }

    private static (int qpDc, int qpLp, int qpHp, bool scaled, int lenMantissa, int expBias) ReadPlaneHeaderGray(ref BitReader r, JxrOutputBitDepth bd)
    {
        var clrFmt = (JxrInternalColorFormat)r.ReadBits(3);
        if (clrFmt != JxrInternalColorFormat.YOnly)
            throw new NotSupportedException($"JxrCodestream.DecodeGray expects YONLY internal format (got {clrFmt}).");
        bool scaled = r.ReadBit();
        var bands = (JxrBandsPresent)r.ReadBits(4);
        if (bands != JxrBandsPresent.AllBands)
            throw new NotSupportedException($"JxrCodestream.DecodeGray expects all-bands codestreams (got {bands}).");
        // YONLY: no RESERVED bytes to skip.
        var (lenMantissa, expBias) = ReadBitDepthParams(ref r, bd); // SHIFT_BITS / LEN_MANTISSA + EXP_BIAS

        if (!r.ReadBit()) throw new NotSupportedException("Non-uniform DC quantization not supported.");
        int qpDc = (int)r.ReadBits(8);

        if (r.ReadBit()) throw new NotSupportedException("LP reusing DC quantizer not supported by this writer's mirror.");
        if (!r.ReadBit()) throw new NotSupportedException("Non-uniform LP quantization not supported.");
        int qpLp = (int)r.ReadBits(8);

        if (r.ReadBit()) throw new NotSupportedException("HP reusing LP quantizer not supported by this writer's mirror.");
        if (!r.ReadBit()) throw new NotSupportedException("Non-uniform HP quantization not supported.");
        int qpHp = (int)r.ReadBits(8);

        AlignToByte(ref r);
        return (qpDc, qpLp, qpHp, scaled, lenMantissa, expBias);
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

    // Luma/sample level shift: jxrlib `iOffset = (1 << (bits-1)) << cShift`, with cShift = 0 in
    // the lossless (non-scaled-arith) path — 128 for BD8, 32768 for BD16. (SHIFT_BITS nLen = 0:
    // full-precision integer, lossless. Lossy precision reduction is a future extension.)
    private static int LumaBias(JxrOutputBitDepth bd) => bd == JxrOutputBitDepth.Bd16 ? 32768 : 128;

    // Output clamp ceiling (jxrlib _CLIP8 / _CLIPU16).
    private static int SampleMax(JxrOutputBitDepth bd) => bd == JxrOutputBitDepth.Bd16 ? 65535 : 255;

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

    private static void ExtractMbGray(ReadOnlySpan<int> y, int width, int mbR, int mbC, int[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int src = (mbR * 16 + row) * width + mbC * 16;
            int dst = row * 16;
            for (var col = 0; col < 16; col++)
                my[dst + col] = y[src + col];
        }
    }

    private static void StoreMbGray(Span<int> y, int width, int mbR, int mbC, int[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int dst = (mbR * 16 + row) * width + mbC * 16;
            int src = row * 16;
            for (var col = 0; col < 16; col++)
                y[dst + col] = my[src + col];
        }
    }

    private static void ExtractMbGrayF(ReadOnlySpan<float> y, int width, int mbR, int mbC, float[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int src = (mbR * 16 + row) * width + mbC * 16;
            int dst = row * 16;
            for (var col = 0; col < 16; col++)
                my[dst + col] = y[src + col];
        }
    }

    private static void StoreMbGrayF(Span<float> y, int width, int mbR, int mbC, float[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int dst = (mbR * 16 + row) * width + mbC * 16;
            int src = row * 16;
            for (var col = 0; col < 16; col++)
                y[dst + col] = my[src + col];
        }
    }
}
