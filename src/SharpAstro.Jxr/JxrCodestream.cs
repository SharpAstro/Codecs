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
    /// Arbitrary dimensions are allowed (the partial right/bottom macroblocks are edge-
    /// replicated; see <see cref="RequirePositiveDims"/>). QP indices are 0 for lossless.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
                                int width, int height, int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0,
                                JxrOutputBitDepth bd = JxrOutputBitDepth.Bd8, JxrTileLayout? tiles = null,
                                JxrInternalColorFormat internalClrFmt = JxrInternalColorFormat.YUV444,
                                int trimFlexBits = 0, bool noFlexBits = false)
    {
        if (internalClrFmt is JxrInternalColorFormat.YUV420 or JxrInternalColorFormat.YUV422)
            return EncodeChroma(r, g, b, width, height, internalClrFmt, qpDc, qpLp, qpHp, overlap, bd);
        if (tiles is { } layout && (layout.NumVerTiles > 1 || layout.NumHorTiles > 1))
            return EncodeMultiTile(r, g, b, width, height, layout, qpDc, qpLp, qpHp, overlap, bd);
        RequirePositiveDims(width, height);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        // jxrlib forces scaled-arith unless (lossless QP ≤ 1 AND all bands present); NO_FLEXBITS
        // (sbSubband != SB_ALL) therefore scales BD8/BD16. (BD32* would override FALSE, but the
        // BD32F float path is a separate encoder.)
        bool scaled = ScaledArith(qpDc, qpLp, qpHp) || noFlexBits;
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int bias = LumaBias(bd);
        int shift = scaled ? SignalTransform.ScaledShift : 0; // <<3 scaled-arith input scaling
        var bands = noFlexBits ? JxrBandsPresent.NoFlexbits : JxrBandsPresent.AllBands;

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.Rgb, bd, trimFlexBits);
        WritePlaneHeader(w, qpDc, qpLp, qpHp, scaled, bd, bands: bands);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w, trimFlexBits);

        // Color-transform + load every macroblock into the whole-image YUV planes,
        // then run the overlap + 2-stage PCT across the grid (jxrlib's sliding
        // 2-MB-row window), then per-MB quantize + entropy code.
        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows);
        var (mr, mg, mb) = (new int[256], new int[256], new int[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMb(r, g, b, width, height, mbR, mbC, mr, mg, mb);
                SignalTransform.LoadColor(mr, mg, mb, planes[0], planes[1], planes[2],
                                          OverlapTransform.MbBase(mbCols, mbR, mbC), bias, shift);
            }

        OverlapTransform.Forward(planes, mbCols, mbRows, overlap, scaled);

        // SPATIAL: all four band streams alias one writer (BitWriter is a class).
        var ctx = new CodingContext(ColorFormat.Yuv444, 3) { TrimFlexBits = trimFlexBits, NoFlexBits = noFlexBits };
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

    // Single-tile SPATIAL YUV420/422 BD8 RGB encode — the inverse of DecodeChroma. jxrlib forces
    // scaled-arithmetic mode for subsampled chroma (even at QP 1, since the chroma resolution change
    // disqualifies the lossless fast-path), so the colour load scales the RGB input <<3 and the chroma
    // is downsampled (5-tap [1,4,6,4,1]/16, in the YCoCg-R domain) to the reduced grid before the
    // transform. Luma is a full 444-style plane. Ports strenc.c inputMBRow + downsampleUV + the 420_UV
    // / 422_UV forward transform loops (incl. the forward POT pre-filter for OL_ONE/OL_TWO).
    private static byte[] EncodeChroma(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
                                       int width, int height, JxrInternalColorFormat clrFmt,
                                       int qpDc, int qpLp, int qpHp, int overlap, JxrOutputBitDepth bd)
    {
        RequirePositiveDims(width, height);
        var cf = clrFmt == JxrInternalColorFormat.YUV420 ? ColorFormat.Yuv420 : ColorFormat.Yuv422;
        int mbCols = MbCount(width), mbRows = MbCount(height);
        bool scaled = true; // jxrlib m_bUVResolutionChange forces bScaledArith for subsampled chroma
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int bias = LumaBias(bd);
        int shift = SignalTransform.ScaledShift; // <<3 input scaling (SHIFTZERO + QPFRACBITS)
        int chromaBlocks = MacroblockLayout.ChromaBlocks(cf);

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.Rgb, bd); // external format is still RGB
        WritePlaneHeader(w, qpDc, qpLp, qpHp, scaled, bd, clrFmt: clrFmt);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w);

        // Colour-transform + <<3-scaled load into the full-res luma plane and full-res U,V planes.
        var luma = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1)[0];
        var full = OverlapTransform.AllocatePlanes(mbCols, mbRows, 2); // full-res chroma U,V (pre-downsample)
        var (mr, mg, mb) = (new int[256], new int[256], new int[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMb(r, g, b, width, height, mbR, mbC, mr, mg, mb);
                SignalTransform.LoadColor(mr, mg, mb, luma, full[0], full[1],
                                          OverlapTransform.MbBase(mbCols, mbR, mbC), bias, shift);
            }

        // Downsample full-res chroma → reduced chroma, then forward-transform both planes.
        var reduced = ChromaOverlapTransform.AllocatePlanes(mbCols, mbRows, cf);
        ChromaDownsample.Downsample(cf, full[0], full[1], reduced[0], reduced[1], mbCols, mbRows);

        OverlapTransform.Forward(new[] { luma }, mbCols, mbRows, overlap, scaled);
        if (overlap == 0)
            for (var mbR = 0; mbR < mbRows; mbR++)
                for (var mbC = 0; mbC < mbCols; mbC++)
                {
                    int rb = ChromaOverlapTransform.MbBase(mbCols, mbR, mbC, cf);
                    ChromaTransform.ForwardMbNoOverlap(reduced[0], rb, cf);
                    ChromaTransform.ForwardMbNoOverlap(reduced[1], rb, cf);
                }
        else
            ChromaOverlapTransform.Forward(reduced, mbCols, mbRows, overlap, cf);

        // Per-MB quantize (luma full, chroma reduced) + entropy code.
        var ctx = new CodingContext(cf, 3);
        var tile = new TileCoder(mbCols, 3, cf);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(3, chromaBlocks);
                int lumaBase = OverlapTransform.MbBase(mbCols, mbR, mbC);
                int rb = ChromaOverlapTransform.MbBase(mbCols, mbR, mbC, cf);
                SignalTransform.QuantizeExtract(luma, lumaBase, block, 0, qDc, qLp, qHp);
                SignalTransform.QuantizeExtractChroma(reduced[0], rb, block, 1, qDc, qLp, qHp, cf);
                SignalTransform.QuantizeExtractChroma(reduced[1], rb, block, 2, qDc, qLp, qHp, cf);
                tile.EncodeMacroblock(ctx, block, mbC, mbR, w, w, w, w);
            }
            tile.AdvanceRow();
        }

        FillToByte(w);
        return w.ToArray();
    }

    // Multi-tile SPATIAL encode (SOFT tiles — jxrlib's default). The overlap/PCT runs over the whole
    // image exactly as single-tile (soft tiling does NOT gate the overlap at tile edges), then each
    // tile is entropy-coded independently: a fresh CodingContext + TileCoder over the tile's MB
    // sub-rectangle in LOCAL coordinates, so DC/LP/CBP prediction and the adaptive contexts reset at
    // every tile edge (mirroring jxrlib's per-column context reset at tile-row boundaries — the MB
    // visiting order is identical). Tiles are concatenated in raster order behind an INDEX_TABLE_TILES
    // of cumulative byte offsets. Ports jxrlib strenc.c encodeMB / writeIndexTable / getTilePos.
    private static byte[] EncodeMultiTile(
        ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
        int width, int height, JxrTileLayout layout, int qpDc, int qpLp, int qpHp, int overlap, JxrOutputBitDepth bd)
    {
        RequirePositiveDims(width, height);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int bias = LumaBias(bd);

        int[] tileX = TileBoundaries(layout.TileWidthInMb, mbCols);
        int[] tileY = TileBoundaries(layout.TileHeightInMb, mbRows);
        int numCols = tileX.Length - 1, numRows = tileY.Length - 1;

        // Whole-image colour load + overlap — byte-identical to the single-tile path.
        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows);
        var (mr, mg, mb) = (new int[256], new int[256], new int[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMb(r, g, b, width, height, mbR, mbC, mr, mg, mb);
                SignalTransform.LoadColor(mr, mg, mb, planes[0], planes[1], planes[2],
                                          OverlapTransform.MbBase(mbCols, mbR, mbC), bias);
            }
        OverlapTransform.Forward(planes, mbCols, mbRows, overlap, scaled);

        // Entropy-code each tile independently into its own byte-aligned buffer.
        var tileBytes = new byte[numRows * numCols][];
        for (var tr = 0; tr < numRows; tr++)
            for (var tc = 0; tc < numCols; tc++)
            {
                int tileIdx = tr * numCols + tc;
                int tmbW = tileX[tc + 1] - tileX[tc], tmbH = tileY[tr + 1] - tileY[tr];
                var tw = new BitWriter();
                WritePacketHeaderTile(tw, tileIdx);

                var ctx = new CodingContext(ColorFormat.Yuv444, 3);
                var tile = new TileCoder(tmbW);
                for (var ly = 0; ly < tmbH; ly++)
                {
                    for (var lx = 0; lx < tmbW; lx++)
                    {
                        int baseOff = OverlapTransform.MbBase(mbCols, tileY[tr] + ly, tileX[tc] + lx);
                        var block = new Macroblock(3);
                        for (var ch = 0; ch < 3; ch++)
                            SignalTransform.QuantizeExtract(planes[ch], baseOff, block, ch, qDc, qLp, qHp);
                        tile.EncodeMacroblock(ctx, block, lx, ly, tw, tw, tw, tw);
                    }
                    tile.AdvanceRow();
                }
                FillToByte(tw);
                tileBytes[tileIdx] = tw.ToArray();
            }

        // Header (image header with tiling + plane header) + INDEX_TABLE_TILES, then the tiles.
        var hw = new BitWriter();
        WriteImageHeaderTiled(hw, width, height, overlap, layout, JxrOutputColorFormat.Rgb, bd);
        WritePlaneHeader(hw, qpDc, qpLp, qpHp, scaled, bd);
        WriteIndexTable(hw, tileBytes);
        return Concat(hw.ToArray(), tileBytes);
    }

    // Cumulative MB boundaries [0, …, totalMb] from per-tile sizes that EXCLUDE the last tile (its
    // extent is implicit; T.832 §8.3.10). Result length = numTiles + 1.
    private static int[] TileBoundaries(int[] sizesExclLast, int totalMb)
    {
        var b = new int[sizesExclLast.Length + 2];
        int acc = 0;
        for (var i = 0; i < sizesExclLast.Length; i++) { b[i] = acc; acc += sizesExclLast[i]; }
        b[sizesExclLast.Length] = acc;
        b[sizesExclLast.Length + 1] = totalMb;
        return b;
    }

    private static void WritePacketHeaderTile(BitWriter w, int tileIndex)
    {
        // jxrlib writePacketHeader: 00 00 01 [(pID<<3)|type]; SPATIAL type=0, pID = tileIndex & 0x1F.
        int pID = tileIndex & 0x1F;
        w.WriteBits(0, 8); w.WriteBits(0, 8); w.WriteBits(1, 8); w.WriteBits((uint)(pID << 3), 8);
    }

    private static void WriteImageHeaderTiled(BitWriter w, int width, int height, int overlap,
                                              JxrTileLayout layout, JxrOutputColorFormat clrFmt, JxrOutputBitDepth bitDepth)
    {
        int mbW = MbCount(width), mbH = MbCount(height);
        var ih = new ImageHeader
        {
            HardTilingFlag = false,            // SOFT tiles (jxrlib default): overlap spans tile edges
            TilingFlag = true,
            FrequencyModeCodestreamFlag = false,
            SpatialXfrmSubordinate = 0,
            IndexTablePresentFlag = true,      // required for multi-tile (strenc.c StrIOEncInit)
            OverlapMode = overlap,
            ShortHeaderFlag = mbW <= 255 && mbH <= 255,
            LongWordFlag = true,
            WindowingFlag = false,
            TrimFlexBitsFlag = false,
            RedBlueNotSwappedFlag = false,
            PremultipliedAlphaFlag = false,
            AlphaImagePlaneFlag = false,
            OutputClrFmt = clrFmt,
            OutputBitDepth = bitDepth,
            WidthMinus1 = (uint)(width - 1),
            HeightMinus1 = (uint)(height - 1),
            NumVerTilesMinus1 = layout.NumVerTiles - 1,
            NumHorTilesMinus1 = layout.NumHorTiles - 1,
            TileWidthInMb = layout.TileWidthInMb,
            TileHeightInMb = layout.TileHeightInMb,
        };
        ih.Write(w);
    }

    // INDEX_TABLE_TILES (T.832 §8.7.1.3): start code 0x0001, one cumulative-byte-offset entry per tile
    // (raster order, first entry = 0), then jxrlib's single 0xFF end escape — read back as a vlw_esc
    // "subsequent bytes = 0", so NO PROFILE_LEVEL_INFO block follows (unlike the single-tile
    // writeIndexTableNull path). All tiles here are far larger than MINIMUM_PACKET_LENGTH, so every
    // entry uses the plain (escByte 0) vlw_esc form.
    private static void WriteIndexTable(BitWriter w, byte[][] tileBytes)
    {
        w.WriteBits(IndexTableTiles.IndexTableStartCode, 16);
        long cum = 0;
        foreach (byte[] t in tileBytes) { WriteVlwEsc(w, cum); cum += t.Length; }
        w.WriteBits(0xFF, 8); // PutVLWordEsc(0xff, 0) end escape
        FillToByte(w);
    }

    private static byte[] Concat(byte[] header, byte[][] parts)
    {
        int total = header.Length;
        foreach (byte[] p in parts) total += p.Length;
        var outBuf = new byte[total];
        int pos = 0;
        Array.Copy(header, 0, outBuf, pos, header.Length); pos += header.Length;
        foreach (byte[] p in parts) { Array.Copy(p, 0, outBuf, pos, p.Length); pos += p.Length; }
        return outBuf;
    }

    // Multi-tile SPATIAL decode (inverse of EncodeMultiTile): consume INDEX_TABLE_TILES, then decode
    // each tile in raster order from its own packet — a fresh CodingContext + TileCoder over the tile's
    // MB sub-rectangle in LOCAL coordinates — dequantizing into the whole-image YUV planes. Tiles are
    // byte-aligned, so align the reader before each packet header. The whole-image inverse overlap runs
    // in the caller afterwards (soft tiling — the overlap spans tile edges).
    private static void DecodeTilesIntoPlanes(
        ref BitReader reader, ImageHeader ih, int[][] planes, int mbCols, int mbRows,
        JxrQuantizer qDc, JxrQuantizer qLp, JxrQuantizer qHp)
    {
        int[] tileX = TileBoundaries(ih.TileWidthInMb, mbCols);
        int[] tileY = TileBoundaries(ih.TileHeightInMb, mbRows);
        int numCols = tileX.Length - 1, numRows = tileY.Length - 1;

        // INDEX_TABLE_TILES: start code 0x0001, one cumulative-offset entry per tile, then the 0xFF end
        // escape (a vlw_esc that reads back as 0 ⇒ no PROFILE_LEVEL_INFO). We decode sequentially, so
        // the offsets themselves are unused. The plane header left the reader byte-aligned.
        uint startCode = reader.ReadBits(16);
        if (startCode != IndexTableTiles.IndexTableStartCode)
            throw new InvalidDataException($"INDEX_TABLE_TILES start code mismatch: got 0x{startCode:X4}.");
        for (var i = 0; i < numCols * numRows; i++) ReadVlwEsc(ref reader);
        ReadVlwEsc(ref reader); // 0xFF end escape
        AlignToByte(ref reader);

        for (var tr = 0; tr < numRows; tr++)
            for (var tc = 0; tc < numCols; tc++)
            {
                ReadPacketHeader(ref reader);
                int tmbW = tileX[tc + 1] - tileX[tc], tmbH = tileY[tr + 1] - tileY[tr];
                var ctx = new CodingContext(ColorFormat.Yuv444, 3);
                var tile = new TileCoder(tmbW);
                for (var ly = 0; ly < tmbH; ly++)
                {
                    for (var lx = 0; lx < tmbW; lx++)
                    {
                        var block = new Macroblock(3);
                        tile.DecodeMacroblock(ctx, block, lx, ly, ref reader, ref reader, ref reader, ref reader, qHp.Qp);
                        int baseOff = OverlapTransform.MbBase(mbCols, tileY[tr] + ly, tileX[tc] + lx);
                        for (var ch = 0; ch < 3; ch++)
                            SignalTransform.DequantizeRestore(block, ch, planes[ch], baseOff, qDc.Qp, qLp.Qp);
                    }
                    tile.AdvanceRow();
                }
                AlignToByte(ref reader); // each tile is byte-aligned (FillToByte on encode)
            }
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> BD8 grayscale image
    /// (<c>width*height</c> samples, raster order, values 0..255) into a single-tile
    /// SPATIAL <b>Y-only</b> JXR codestream. No colour transform — the single channel is
    /// the Y plane. Arbitrary dimensions are allowed (partial MBs edge-replicated); QP indices are 0 for lossless.
    /// </summary>
    public static byte[] EncodeGray(ReadOnlySpan<int> y, int width, int height,
                                    int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0,
                                    JxrOutputBitDepth bd = JxrOutputBitDepth.Bd8)
    {
        RequirePositiveDims(width, height);
        int mbCols = MbCount(width), mbRows = MbCount(height);
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
                ExtractMbGray(y, width, height, mbR, mbC, my);
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
    /// the float↔pixel mapping and are written to the plane header. Arbitrary dimensions are
    /// allowed (partial MBs edge-replicated); QP indices are 0 for lossless (the codec is lossless on the float-pixel values).
    /// </summary>
    public static byte[] EncodeGrayF32(ReadOnlySpan<float> y, int width, int height,
                                       int lenMantissa = 13, int expBias = 4,
                                       int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0,
                                       bool noFlexBits = false)
    {
        RequirePositiveDims(width, height);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        // BD32F forces bScaledArith FALSE (strenc.c), even with NO_FLEXBITS.
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        var bands = noFlexBits ? JxrBandsPresent.NoFlexbits : JxrBandsPresent.AllBands;

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.YOnly, JxrOutputBitDepth.Bd32F);
        WritePlaneHeaderGray(w, qpDc, qpLp, qpHp, scaled, JxrOutputBitDepth.Bd32F, lenMantissa, expBias, bands);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w);

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1);
        var my = new float[256];
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMbGrayF(y, width, height, mbR, mbC, my);
                SignalTransform.LoadGrayFloat(my, planes[0], OverlapTransform.MbBase(mbCols, mbR, mbC), expBias, lenMantissa);
            }

        OverlapTransform.Forward(planes, mbCols, mbRows, overlap, scaled);

        var ctx = new CodingContext(ColorFormat.YOnly, 1) { NoFlexBits = noFlexBits };
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
        RequirePositiveDims(width, height);
        var bd = ih.OutputBitDepth;
        int bias = LumaBias(bd), max = SampleMax(bd);
        var (clrFmt, qpDc, qpLp, qpHp, scaled, bands) = ReadPlaneHeader(ref reader, bd);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        if (clrFmt is JxrInternalColorFormat.YUV420 or JxrInternalColorFormat.YUV422)
        {
            if (ih.TilingFlag) throw new NotSupportedException("Multi-tile YUV420/422 decode is not yet supported.");
            if (bands != JxrBandsPresent.AllBands) throw new NotSupportedException("YUV420/422 decode supports all-bands codestreams only.");
            return DecodeChroma(ref reader, clrFmt, mbCols, mbRows, width, height, bias, max, overlap, scaled, qDc, qLp, qHp);
        }
        bool noFlexBits = bands == JxrBandsPresent.NoFlexbits;
        int outShift = scaled ? SignalTransform.ScaledShift : 0; // scaled-arith output >>3 (e.g. NO_FLEXBITS BD8/16)
        var (r, g, b) = (new int[width * height], new int[width * height], new int[width * height]);

        // Per-MB entropy decode + dequantize into the whole-image YUV planes, then
        // run the inverse overlap + 2-stage PCT across the grid, then color-store.
        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows);
        if (ih.TilingFlag)
        {
            DecodeTilesIntoPlanes(ref reader, ih, planes, mbCols, mbRows, qDc, qLp, qHp);
        }
        else
        {
            ReadProfileLevelInfo(ref reader);
            int trim = ReadPacketHeader(ref reader, ih.TrimFlexBitsFlag);
            var ctx = new CodingContext(ColorFormat.Yuv444, 3) { TrimFlexBits = trim, NoFlexBits = noFlexBits };
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
        }

        OverlapTransform.Inverse(planes, mbCols, mbRows, overlap, scaled);

        var (mr, mg, mb) = (new int[256], new int[256], new int[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.StoreColor(planes[0], planes[1], planes[2], baseOff, mr, mg, mb, bias, max, outShift);
                StoreMb(r, g, b, width, height, mbR, mbC, mr, mg, mb);
            }
        return (width, height, r, g, b);
    }

    // Single-tile SPATIAL YUV420/422 BD8 RGB decode. Luma is a full 444-style plane through the
    // OverlapTransform; chroma is decoded into reduced (64/128 per MB) planes, inverse-transformed
    // per-MB (OL_NONE), upsampled with interpolateUV into slack planes aligned with luma, then
    // colour-stored. OL_ONE/OL_TWO chroma (POT overlap on the reduced grid) is a separate rung.
    private static (int width, int height, int[] r, int[] g, int[] b) DecodeChroma(
        ref BitReader reader, JxrInternalColorFormat clrFmt, int mbCols, int mbRows,
        int width, int height, int bias, int max, int overlap, bool scaled,
        JxrQuantizer qDc, JxrQuantizer qLp, JxrQuantizer qHp)
    {
        var cf = clrFmt == JxrInternalColorFormat.YUV420 ? ColorFormat.Yuv420 : ColorFormat.Yuv422;
        int chromaBlocks = MacroblockLayout.ChromaBlocks(cf);

        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var luma = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1)[0]; // 256/MB slack plane
        var full = OverlapTransform.AllocatePlanes(mbCols, mbRows, 2);    // upsampled chroma U,V (slack)
        // Reduced-chroma U,V on the slacked ring grid (64/128 ints per MB) — the layout the
        // chroma POT windowed inverse indexes across MB boundaries; harmless for OL_NONE too.
        var reduced = ChromaOverlapTransform.AllocatePlanes(mbCols, mbRows, cf);
        var (reducedU, reducedV) = (reduced[0], reduced[1]);

        var ctx = new CodingContext(cf, 3);
        var tile = new TileCoder(mbCols, 3, cf);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(3, chromaBlocks);
                tile.DecodeMacroblock(ctx, block, mbC, mbR, ref reader, ref reader, ref reader, ref reader, qHp.Qp);
                int lumaBase = OverlapTransform.MbBase(mbCols, mbR, mbC);
                int reducedBase = ChromaOverlapTransform.MbBase(mbCols, mbR, mbC, cf);
                SignalTransform.DequantizeRestore(block, 0, luma, lumaBase, qDc.Qp, qLp.Qp);
                SignalTransform.DequantizeRestoreChroma(block, 1, reducedU, reducedBase, qDc.Qp, qLp.Qp, cf);
                SignalTransform.DequantizeRestoreChroma(block, 2, reducedV, reducedBase, qDc.Qp, qLp.Qp, cf);
            }
            tile.AdvanceRow();
        }

        OverlapTransform.Inverse(new[] { luma }, mbCols, mbRows, overlap, scaled);
        if (overlap == 0)
            for (var mbR = 0; mbR < mbRows; mbR++)
                for (var mbC = 0; mbC < mbCols; mbC++)
                {
                    int reducedBase = ChromaOverlapTransform.MbBase(mbCols, mbR, mbC, cf);
                    ChromaTransform.InverseMbNoOverlap(reducedU, reducedBase, cf);
                    ChromaTransform.InverseMbNoOverlap(reducedV, reducedBase, cf);
                }
        else
            ChromaOverlapTransform.Inverse(reduced, mbCols, mbRows, overlap, cf);

        ChromaUpsample.Interpolate(cf, reducedU, reducedV, full[0], full[1], mbCols, mbRows,
                                   (mbCols + 1) * 256, ChromaOverlapTransform.RowStride(mbCols, cf));

        var (r, g, b) = (new int[width * height], new int[width * height], new int[width * height]);
        var (mr, mg, mb) = (new int[256], new int[256], new int[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                // YUV420/422 always decode in scaled-arithmetic mode (scaled == true): the transform
                // ran on <<3-scaled input, so the output is shifted back down by ScaledShift.
                SignalTransform.StoreColor(luma, full[0], full[1], baseOff, mr, mg, mb, bias, max,
                                           scaled ? SignalTransform.ScaledShift : 0);
                StoreMb(r, g, b, width, height, mbR, mbC, mr, mg, mb);
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
        RequirePositiveDims(width, height);
        var bd = ih.OutputBitDepth;
        int bias = LumaBias(bd), max = SampleMax(bd);
        var (qpDc, qpLp, qpHp, scaled, _, _, _) = ReadPlaneHeaderGray(ref reader, bd);
        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = MbCount(width), mbRows = MbCount(height);
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
                StoreMbGray(y, width, height, mbR, mbC, my);
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
        RequirePositiveDims(width, height);
        var (qpDc, qpLp, qpHp, scaled, lenMantissa, expBias, bands) = ReadPlaneHeaderGray(ref reader, ih.OutputBitDepth);
        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        var y = new float[width * height];

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1);
        var ctx = new CodingContext(ColorFormat.YOnly, 1) { NoFlexBits = bands == JxrBandsPresent.NoFlexbits };
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
                StoreMbGrayF(y, width, height, mbR, mbC, my);
            }
        return (width, height, y);
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD16F</b> half-float grayscale
    /// image into a single-tile SPATIAL Y-only JXR codestream. The half is kept as its raw
    /// sign-magnitude bit pattern (no bias, no float params in the header), so the round-trip is
    /// bit-exact. Arbitrary dimensions are allowed (partial MBs edge-replicated); QP indices are 0 for lossless.
    /// </summary>
    public static byte[] EncodeGrayHalf(ReadOnlySpan<Half> y, int width, int height,
                                        int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        RequirePositiveDims(width, height);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.YOnly, JxrOutputBitDepth.Bd16F);
        WritePlaneHeaderGray(w, qpDc, qpLp, qpHp, scaled, JxrOutputBitDepth.Bd16F);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w);

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows, 1);
        var my = new Half[256];
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMb1(y, width, height, mbR, mbC, my);
                SignalTransform.LoadGrayHalf(my, planes[0], OverlapTransform.MbBase(mbCols, mbR, mbC));
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

    /// <summary>Decode a single-tile SPATIAL Y-only BD16F JXR codestream back into a half-float grayscale image.</summary>
    public static (int width, int height, Half[] y) DecodeGrayHalf(ReadOnlySpan<byte> codestream)
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
        RequirePositiveDims(width, height);
        var (qpDc, qpLp, qpHp, scaled, _, _, _) = ReadPlaneHeaderGray(ref reader, ih.OutputBitDepth);
        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        var y = new Half[width * height];

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

        var my = new Half[256];
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.StoreGrayHalf(planes[0], baseOff, my);
                StoreMb1(y, width, height, mbR, mbC, my);
            }
        return (width, height, y);
    }

    /// <summary>
    /// Encode a <paramref name="width"/>×<paramref name="height"/> <b>BD16F</b> half-float RGB image
    /// (three channels) into a single-tile SPATIAL YUV444 JXR codestream — YCoCg-R on the raw half
    /// magnitudes, bit-exact round-trip. Arbitrary dimensions are allowed (partial MBs edge-replicated); QP indices 0 for lossless.
    /// </summary>
    public static byte[] EncodeRgbHalf(ReadOnlySpan<Half> r, ReadOnlySpan<Half> g, ReadOnlySpan<Half> b,
                                       int width, int height, int qpDc = 0, int qpLp = 0, int qpHp = 0, int overlap = 0)
    {
        RequirePositiveDims(width, height);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        bool scaled = ScaledArith(qpDc, qpLp, qpHp);
        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);

        var w = new BitWriter();
        WriteImageHeader(w, width, height, overlap, JxrOutputColorFormat.Rgb, JxrOutputBitDepth.Bd16F);
        WritePlaneHeader(w, qpDc, qpLp, qpHp, scaled, JxrOutputBitDepth.Bd16F);
        WriteProfileLevelInfo(w);
        WritePacketHeader(w);

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows);
        var (mr, mg, mb) = (new Half[256], new Half[256], new Half[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                ExtractMb1(r, width, height, mbR, mbC, mr);
                ExtractMb1(g, width, height, mbR, mbC, mg);
                ExtractMb1(b, width, height, mbR, mbC, mb);
                SignalTransform.LoadColorHalf(mr, mg, mb, planes[0], planes[1], planes[2],
                                              OverlapTransform.MbBase(mbCols, mbR, mbC));
            }

        OverlapTransform.Forward(planes, mbCols, mbRows, overlap, scaled);

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

    /// <summary>Decode a single-tile SPATIAL YUV444 BD16F JXR codestream back into half-float RGB channels.</summary>
    public static (int width, int height, Half[] r, Half[] g, Half[] b) DecodeRgbHalf(ReadOnlySpan<byte> codestream)
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
        RequirePositiveDims(width, height);
        var (clrFmt, qpDc, qpLp, qpHp, scaled, bands) = ReadPlaneHeader(ref reader, ih.OutputBitDepth);
        if (clrFmt != JxrInternalColorFormat.YUV444)
            throw new NotSupportedException($"BD16F RGB decode supports YUV444 only (got {clrFmt}); chroma-subsampled BD16F is not yet wired.");
        if (bands != JxrBandsPresent.AllBands)
            throw new NotSupportedException("BD16F RGB decode supports all-bands codestreams only (scaled-arith NO_FLEXBITS not yet wired).");
        ReadProfileLevelInfo(ref reader);
        ReadPacketHeader(ref reader);

        var (qDc, qLp, qHp) = Quantizers(qpDc, qpLp, qpHp, scaled);
        int mbCols = MbCount(width), mbRows = MbCount(height);
        var (r, g, b) = (new Half[width * height], new Half[width * height], new Half[width * height]);

        var planes = OverlapTransform.AllocatePlanes(mbCols, mbRows);
        var ctx = new CodingContext(ColorFormat.Yuv444, 3);
        var tile = new TileCoder(mbCols);
        for (var mbR = 0; mbR < mbRows; mbR++)
        {
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                var block = new Macroblock(3);
                tile.DecodeMacroblock(ctx, block, mbC, mbR, ref reader, ref reader, ref reader, ref reader, qHp.Qp);
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                for (var ch = 0; ch < 3; ch++)
                    SignalTransform.DequantizeRestore(block, ch, planes[ch], baseOff, qDc.Qp, qLp.Qp);
            }
            tile.AdvanceRow();
        }

        OverlapTransform.Inverse(planes, mbCols, mbRows, overlap, scaled);

        var (mr, mg, mb) = (new Half[256], new Half[256], new Half[256]);
        for (var mbR = 0; mbR < mbRows; mbR++)
            for (var mbC = 0; mbC < mbCols; mbC++)
            {
                int baseOff = OverlapTransform.MbBase(mbCols, mbR, mbC);
                SignalTransform.StoreColorHalf(planes[0], planes[1], planes[2], baseOff, mr, mg, mb);
                StoreMb1(r, width, height, mbR, mbC, mr);
                StoreMb1(g, width, height, mbR, mbC, mg);
                StoreMb1(b, width, height, mbR, mbC, mb);
            }
        return (width, height, r, g, b);
    }

    // ---------------------------------------------------------------- headers

    private static void WriteImageHeader(BitWriter w, int width, int height, int overlap,
                                         JxrOutputColorFormat clrFmt = JxrOutputColorFormat.Rgb,
                                         JxrOutputBitDepth bitDepth = JxrOutputBitDepth.Bd8,
                                         int trimFlexBits = 0)
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
            TrimFlexBitsFlag = trimFlexBits > 0,            // jxrlib bTrimFlexbitsFlag = (uiTrimFlexBits > 0)
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
                                         int lenMantissaOrShift = 0, int expBias = 0,
                                         JxrInternalColorFormat clrFmt = JxrInternalColorFormat.YUV444,
                                         JxrBandsPresent bands = JxrBandsPresent.AllBands)
    {
        w.WriteBits((uint)clrFmt, 3);                        // internal color format (YUV444 / YUV420 / YUV422)
        w.WriteBit(scaled);                                  // bScaledArith
        w.WriteBits((uint)bands, 4);                         // SB_ALL / SB_NO_FLEXBITS
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

    private static (JxrInternalColorFormat clrFmt, int qpDc, int qpLp, int qpHp, bool scaled, JxrBandsPresent bands) ReadPlaneHeader(ref BitReader r, JxrOutputBitDepth bd)
    {
        var clrFmt = (JxrInternalColorFormat)r.ReadBits(3);
        if (clrFmt is not (JxrInternalColorFormat.YUV444 or JxrInternalColorFormat.YUV420 or JxrInternalColorFormat.YUV422))
            throw new NotSupportedException($"JxrCodestream decodes YUV444 / YUV420 / YUV422 internal formats (got {clrFmt}).");
        bool scaled = r.ReadBit();
        var bands = (JxrBandsPresent)r.ReadBits(4);
        if (bands is not (JxrBandsPresent.AllBands or JxrBandsPresent.NoFlexbits))
            throw new NotSupportedException($"JxrCodestream decodes all-bands / no-flexbits codestreams only (got {bands}).");
        // 8 bits of colour params: 444 = RESERVED_F(4)+RESERVED_H(4); 422/420 pack CHROMA_CENTERING_X/Y
        // into the same 8 bits — the encoder writes zeros for all, so we just skip them.
        r.SkipBits(8);
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
        return (clrFmt, qpDc, qpLp, qpHp, scaled, bands);
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
                                             int lenMantissaOrShift = 0, int expBias = 0,
                                             JxrBandsPresent bands = JxrBandsPresent.AllBands)
    {
        w.WriteBits((uint)JxrInternalColorFormat.YOnly, 3); // internal color format = 0
        w.WriteBit(scaled);                                 // bScaledArith
        w.WriteBits((uint)bands, 4);                        // SB_ALL / SB_NO_FLEXBITS
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

    private static (int qpDc, int qpLp, int qpHp, bool scaled, int lenMantissa, int expBias, JxrBandsPresent bands) ReadPlaneHeaderGray(ref BitReader r, JxrOutputBitDepth bd)
    {
        var clrFmt = (JxrInternalColorFormat)r.ReadBits(3);
        if (clrFmt != JxrInternalColorFormat.YOnly)
            throw new NotSupportedException($"JxrCodestream.DecodeGray expects YONLY internal format (got {clrFmt}).");
        bool scaled = r.ReadBit();
        var bands = (JxrBandsPresent)r.ReadBits(4);
        if (bands is not (JxrBandsPresent.AllBands or JxrBandsPresent.NoFlexbits))
            throw new NotSupportedException($"JxrCodestream.DecodeGray decodes all-bands / no-flexbits codestreams only (got {bands}).");
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
        return (qpDc, qpLp, qpHp, scaled, lenMantissa, expBias, bands);
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

    private static void WritePacketHeader(BitWriter w, int trimFlexBits = 0)
    {
        // writePacketHeader(pIO, ptPacketType=0 spatial, pID=0): 00 00 01 00.
        w.WriteBits(0, 8); w.WriteBits(0, 8); w.WriteBits(1, 8); w.WriteBits(0, 8);
        // jxrlib encodeMB: a 4-bit TRIM_FLEXBITS follows the SPATIAL packet header when the
        // image-header TRIM_FLEXBITS_FLAG is set (before the — here empty — tile band headers).
        if (trimFlexBits > 0) w.WriteBits((uint)trimFlexBits, 4);
    }

    // Returns the per-tile TRIM_FLEXBITS (0 when the image-header flag is clear).
    private static int ReadPacketHeader(ref BitReader r, bool trimFlexBitsFlag = false)
    {
        uint b0 = r.ReadBits(8), b1 = r.ReadBits(8), b2 = r.ReadBits(8), b3 = r.ReadBits(8);
        if (b0 != 0 || b1 != 0 || b2 != 1)
            throw new InvalidDataException($"Bad spatial packet header {b0:X2} {b1:X2} {b2:X2} {b3:X2} (expected 00 00 01 xx).");
        return trimFlexBitsFlag ? (int)r.ReadBits(4) : 0;
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

    // jxrlib supports arbitrary dimensions: WIDTH_MINUS1 / HEIGHT_MINUS1 carry the real
    // (unpadded) size, the coded grid is ceil(dim/16) macroblocks, the encoder edge-
    // replicates the partial right/bottom macroblocks (strenc.c padHorizontally replicates
    // the last column; inputMBRow replicates the last row by not advancing the source past
    // it), and the decoder crops the grid back to the real size on output (strdec.c
    // outputNChannel loops iColumn<cWidth / iRow<cHeight). There is NO WINDOWING_FLAG — the
    // flag (cExtraPixels*) is only emitted for compressed-domain transcoding (bTranscode),
    // which this codec never does. So sub-MB images are a pad-then-crop, not a windowed crop.
    private static void RequirePositiveDims(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("JxrCodestream requires positive width and height.");
    }

    // Number of macroblocks spanning a dimension: ceil(dim / 16).
    private static int MbCount(int dim) => (dim + 15) / 16;

    private static void ExtractMb(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b, int width, int height,
                                  int mbR, int mbC, int[] mr, int[] mg, int[] mb)
    {
        for (var row = 0; row < 16; row++)
        {
            int sy = Math.Min(mbR * 16 + row, height - 1); // edge-replicate the last row past the bottom
            int rowBase = sy * width;
            int dst = row * 16;
            for (var col = 0; col < 16; col++)
            {
                int sx = Math.Min(mbC * 16 + col, width - 1); // edge-replicate the last column past the right
                mr[dst + col] = r[rowBase + sx];
                mg[dst + col] = g[rowBase + sx];
                mb[dst + col] = b[rowBase + sx];
            }
        }
    }

    private static void StoreMb(Span<int> r, Span<int> g, Span<int> b, int width, int height,
                                int mbR, int mbC, int[] mr, int[] mg, int[] mb)
    {
        for (var row = 0; row < 16; row++)
        {
            int dy = mbR * 16 + row;
            if (dy >= height) break;             // drop the padded rows below the image
            int rowBase = dy * width;
            int src = row * 16;
            for (var col = 0; col < 16; col++)
            {
                int dx = mbC * 16 + col;
                if (dx >= width) break;          // drop the padded columns past the right edge
                r[rowBase + dx] = mr[src + col];
                g[rowBase + dx] = mg[src + col];
                b[rowBase + dx] = mb[src + col];
            }
        }
    }

    private static void ExtractMbGray(ReadOnlySpan<int> y, int width, int height, int mbR, int mbC, int[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int rowBase = Math.Min(mbR * 16 + row, height - 1) * width;
            int dst = row * 16;
            for (var col = 0; col < 16; col++)
                my[dst + col] = y[rowBase + Math.Min(mbC * 16 + col, width - 1)];
        }
    }

    private static void StoreMbGray(Span<int> y, int width, int height, int mbR, int mbC, int[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int dy = mbR * 16 + row;
            if (dy >= height) break;
            int rowBase = dy * width;
            int src = row * 16;
            for (var col = 0; col < 16; col++)
            {
                int dx = mbC * 16 + col;
                if (dx >= width) break;
                y[rowBase + dx] = my[src + col];
            }
        }
    }

    private static void ExtractMbGrayF(ReadOnlySpan<float> y, int width, int height, int mbR, int mbC, float[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int rowBase = Math.Min(mbR * 16 + row, height - 1) * width;
            int dst = row * 16;
            for (var col = 0; col < 16; col++)
                my[dst + col] = y[rowBase + Math.Min(mbC * 16 + col, width - 1)];
        }
    }

    private static void StoreMbGrayF(Span<float> y, int width, int height, int mbR, int mbC, float[] my)
    {
        for (var row = 0; row < 16; row++)
        {
            int dy = mbR * 16 + row;
            if (dy >= height) break;
            int rowBase = dy * width;
            int src = row * 16;
            for (var col = 0; col < 16; col++)
            {
                int dx = mbC * 16 + col;
                if (dx >= width) break;
                y[rowBase + dx] = my[src + col];
            }
        }
    }

    // Generic single-channel macroblock copy (used by the half-float gray/RGB paths).
    // Encode side edge-replicates the partial right/bottom MB (Math.Min clamp).
    private static void ExtractMb1<T>(ReadOnlySpan<T> src, int width, int height, int mbR, int mbC, Span<T> mb)
    {
        for (var row = 0; row < 16; row++)
        {
            int rowBase = Math.Min(mbR * 16 + row, height - 1) * width;
            int d = row * 16;
            for (var col = 0; col < 16; col++)
                mb[d + col] = src[rowBase + Math.Min(mbC * 16 + col, width - 1)];
        }
    }

    // Decode side crops the padded MB grid back to the real dimensions.
    private static void StoreMb1<T>(Span<T> dst, int width, int height, int mbR, int mbC, ReadOnlySpan<T> mb)
    {
        for (var row = 0; row < 16; row++)
        {
            int dy = mbR * 16 + row;
            if (dy >= height) break;
            int rowBase = dy * width;
            int s = row * 16;
            for (var col = 0; col < 16; col++)
            {
                int dx = mbC * 16 + col;
                if (dx >= width) break;
                dst[rowBase + dx] = mb[s + col];
            }
        }
    }
}
