namespace SharpAstro.Jxr;

/// <summary>
/// Pixel-level JXR encoder facade — turns raw sample data into a JXR codestream.
/// Wraps the transform / prediction / per-MB coding pipeline beneath
/// <see cref="CodedImage"/> so callers don't have to assemble macroblocks
/// by hand.
/// </summary>
/// <remarks>
/// <para>This first cut covers an intentionally narrow slice — enough to
/// prove the full pipeline composes end-to-end. Supported configurations:</para>
/// <list type="bullet">
///   <item>8-bit unsigned grayscale (BD8 + YOnly), single component.</item>
///   <item>Image dimensions that are multiples of 16 (no edge padding).</item>
///   <item>Overlap mode 0 (no POT filtering) — keeps the per-MB pipeline
///         independent of neighbour pixels.</item>
///   <item><see cref="JxrBandsPresent.DcOnly"/> — lossless for constant-valued
///         macroblocks; lossy for anything with frequency content. The full
///         lossless LP/HP path is wired separately.</item>
/// </list>
/// </remarks>
public static class JxrEncoder
{
    /// <summary>
    /// Pre-scaling bias for BD8 (T.832 D.2) — samples are centred on zero
    /// before the transform pipeline so the coefficient ranges stay within
    /// the signed-integer safety margins of FCT/ICT.
    /// </summary>
    public const int Bd8Bias = 128;

    /// <summary>Pre-scaling bias for BD16 (T.832 D.2) — half the 16-bit range.</summary>
    public const int Bd16Bias = 32768;

    /// <summary>
    /// Encode an 8-bit grayscale image with the DcOnly band configuration.
    /// Returns the raw JXR codestream (not yet wrapped in an Annex A container).
    /// </summary>
    /// <param name="pixels"><c>width × height</c> sample bytes in row-major order.</param>
    public static byte[] EncodeBd8GrayscaleDcOnly(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "image dimensions must be positive");
        if (pixels.Length < width * height)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height}");

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;
        var mbs = new Macroblock[mbW * mbH];

        // Scratch buffers reused across MBs.
        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        var mbDc = new int[mbW, mbH, 1];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            // Forward transform pyramid for one 16×16 MB.
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                // Load 4×4 sub-block from the source image with pre-scaling.
                LoadSubBlock(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
                Transforms.FCT4x4(subBlock);
                // Column-major sub-block DC loading to match jxrlib's blkOffset
                // storage convention; see encoder no-flexbits path for details.
                dcGrid[sbCol * 4 + sbRow] = subBlock[0];
            }
            Transforms.FCT4x4Stage2(dcGrid);
            mbDc[mbx, mby, 0] = dcGrid[0];
        }

        // DC prediction across MBs — single-MB images skip prediction
        // entirely (left/top edge → mode 3 = no prediction).
        var predDc = new int[mbW, mbH, 1];
        DcPrediction.Encode(mbDc, predDc, JxrInternalColorFormat.YOnly);

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            mbs[mby * mbW + mbx] = new Macroblock { Dc = [mbDc[mbx, mby, 0]] };
        }

        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = (uint)(width - 1),
                HeightMinus1 = (uint)(height - 1),
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.DcOnly,
                NumComponents = 1,
                DcQuant = 1,
            },
            // LEVEL_IDC=255 (Unrestricted) — matches jxrlib's reference encoder
            // and WIC's WMPhotoDecoder. Hard-coding L1 caused WIC to reject any
            // image larger than 1920×1088 with FRAMES=0 even though JxrDecApp
            // accepts it. See Task #11 in the WIC oracle harness.
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.LUnrestricted),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    /// <summary>
    /// Encode an 8-bit grayscale image with the NoFlexbits band configuration
    /// (DC + LP + HP, no flexbits refinement). Lossless for arbitrary pixel
    /// content at OverlapMode = 0.
    /// </summary>
    public static byte[] EncodeBd8GrayscaleNoFlexbits(byte[] pixels, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height}");
        if (overlapMode is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(overlapMode), "supported values are 0 (no POT) and 1 (single-stage POT)");

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

        // Pre-scaled working buffer: image-sized signed ints. For OverlapMode=1
        // we apply POT in pixel space before the FCT cascade reads sub-blocks.
        var working = new int[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            working[y * width + x] = pixels[y * width + x] - Bd8Bias;

        if (overlapMode == 1)
            ApplyPreFilterPot(working, width, height);

        // Storage shaped for the prediction layers.
        var mbDc = new int[mbW, mbH, 1];
        var mbDcLp = new int[mbW, mbH, 1, 16];        // pos 0 = super-DC, pos 1..15 = LP
        var mbHp = new int[mbW, mbH, 1, 16, 16];      // pos 0 of each block unused

        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            // Forward pyramid: per-subblock FCT then DC-grid FCT.
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                LoadSubBlockFromWorking(working, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
                Transforms.FCT4x4(subBlock);
                // Col-major sub-block DC into the FCT4x4Stage2 grid; HP coefs
                // stay in row-major mbHp[blkIdx]. See no-flexbits BD16 path.
                dcGrid[sbCol * 4 + sbRow] = subBlock[0];
                var blkIdx = sbCol * 4 + sbRow; // col-major to match jxrlib blkOffset[]
                // Stash positions 1..15 of this sub-block as HP coefficients.
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, 0, blkIdx, p] = subBlock[p];
            }
            Transforms.FCT4x4Stage2(dcGrid);
            // Position 0 of dcGrid = super-DC; positions 1..15 = LP coefficients.
            mbDc[mbx, mby, 0] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, 0, p] = dcGrid[p];
        }

        // Quantize BEFORE prediction so both encoder and decoder operate on
        // the same quantized values — keeps cross-MB prediction consistent.
        // QP=1 is a no-op so the lossless paths skip work entirely.
        var dcDiv = JxrQuant.QpIndexToDivisor(dcQp);
        var lpDiv = JxrQuant.QpIndexToDivisor(lpQp);
        var hpDiv = JxrQuant.QpIndexToDivisor(hpQp);
        JxrQuant.QuantizeDc(mbDc, dcDiv);
        JxrQuant.QuantizeLp(mbDcLp, lpDiv);
        JxrQuant.QuantizeHp(mbHp, hpDiv);
        // Mirror super-DC into mbDcLp[..., 0] so the LP context sees the same value.
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbDcLp[mbx, mby, 0, 0] = mbDc[mbx, mby, 0];

        // Tile-aware DC prediction: masks force "no prediction" across tile
        // boundaries so encoder and decoder agree on neighbour availability.
        bool[,]? leftMask = null;
        bool[,]? topMask = null;
        if (tiling is not null)
            (leftMask, topMask) = tiling.BuildMasks(mbW, mbH);

        // Prediction cascade in spec order: DC → LP → HP.
        var predDc = new int[mbW, mbH, 1];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, JxrInternalColorFormat.YOnly, leftMask, topMask, mbDcMode);

        var predDcLp = new int[mbW, mbH, 1, 16];
        LpPrediction.Encode(mbDcLp, predDcLp, mbDcMode, JxrInternalColorFormat.YOnly);

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, JxrInternalColorFormat.YOnly, numComponents: 1);
        HpPrediction.Encode(mbHp, mbHpMode, JxrInternalColorFormat.YOnly);

        // Pack into Macroblock[].
        var mbs = new Macroblock[mbW * mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var lp = new int[16];
            // Position 0 of mb.Lp is the super-DC slot (ignored by MbLp), but copy
            // anyway to keep the layout symmetric with the decoder.
            for (var p = 0; p < 16; p++) lp[p] = mbDcLp[mbx, mby, 0, p];

            var hp = new int[256];
            for (var blk = 0; blk < 16; blk++)
            for (var p = 1; p < 16; p++)
                hp[blk * 16 + p] = mbHp[mbx, mby, 0, blk, p];

            mbs[mby * mbW + mbx] = new Macroblock
            {
                Dc = [mbDc[mbx, mby, 0]],
                Lp = lp,
                Hp = hp,
                MbHpMode = mbHpMode[mbx, mby],
            };
        }

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height, JxrOutputColorFormat.YOnly, JxrOutputBitDepth.Bd8, tiling, overlapMode, frequencyMode),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = 1,
                DcQuant = dcQp,
                LpQuant = lpQp,
                HpQuant = hpQp,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.LUnrestricted),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    /// <summary>
    /// Build an ImageHeader prefilled from the common per-encoder parameters,
    /// optionally populating the tile-grid fields if a layout is supplied.
    /// </summary>
    internal static ImageHeader BuildImageHeader(int width, int height,
        JxrOutputColorFormat outFmt, JxrOutputBitDepth outBd, JxrTileLayout? tiling,
        int overlapMode = 0,
        bool frequencyMode = false)
    {
        var h = new ImageHeader
        {
            OutputClrFmt = outFmt,
            OutputBitDepth = outBd,
            ShortHeaderFlag = true,
            WidthMinus1 = (uint)(width - 1),
            HeightMinus1 = (uint)(height - 1),
            OverlapMode = overlapMode,
            FrequencyModeCodestreamFlag = frequencyMode,
            // jxrlib's reference encoder unconditionally writes LONG_WORD_FLAG=1
            // (strenc.c) and its decoder unconditionally treats it as 1 even
            // when read as 0 (strdec.c). Matching that here matters because WIC
            // rejects codestreams with LongWordFlag=false on large images.
            LongWordFlag = true,
            // WIC's WMPhotoDecoder also rejects codestreams without an
            // INDEX_TABLE_TILES on large images, even single-tile ones — the
            // table is degenerate (one entry, offset 0) but its presence is
            // load-bearing for WIC. JxrDecApp tolerates its absence, so this
            // divergence surfaces only through the WIC oracle. See
            // CodedImage.Encode for the single-tile degenerate-table path.
            IndexTablePresentFlag = true,
        };
        if (tiling is not null)
        {
            h.TilingFlag = true;
            h.NumVerTilesMinus1 = tiling.NumVerTiles - 1;
            h.NumHorTilesMinus1 = tiling.NumHorTiles - 1;
            h.TileWidthInMb = tiling.TileWidthInMb;
            h.TileHeightInMb = tiling.TileHeightInMb;
        }
        return h;
    }

    /// <summary>
    /// OverlapMode=1 forward POT — apply <see cref="OverlapFilters.OverlapPreFilter4x4"/>
    /// to every 4×4 patch centred on a sub-block-grid junction whose full
    /// footprint lies inside the image. The working buffer is updated in place;
    /// subsequent sub-block extraction reads the POT-modified values.
    /// </summary>
    internal static void ApplyPreFilterPot(int[] working, int width, int height)
    {
        Span<int> patch = stackalloc int[16];
        // Sub-block grid lines are at multiples of 4. A POT patch centred at
        // (jx, jy) covers pixels (jx-2..jx+1, jy-2..jy+1) — only valid when
        // both jx and jy ≥ 2 and ≤ width-2 / height-2.
        for (var jy = 4; jy + 2 <= height; jy += 4)
        for (var jx = 4; jx + 2 <= width;  jx += 4)
        {
            // Extract 4×4 patch.
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                patch[r * 4 + c] = working[(jy - 2 + r) * width + (jx - 2 + c)];
            OverlapFilters.OverlapPreFilter4x4(patch);
            // Write back.
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                working[(jy - 2 + r) * width + (jx - 2 + c)] = patch[r * 4 + c];
        }
    }

    /// <summary>OverlapMode=1 inverse POT — applies <see cref="OverlapFilters.OverlapPostFilter4x4"/> at the same patch positions.</summary>
    internal static void ApplyPostFilterPot(int[] working, int width, int height)
    {
        Span<int> patch = stackalloc int[16];
        for (var jy = 4; jy + 2 <= height; jy += 4)
        for (var jx = 4; jx + 2 <= width;  jx += 4)
        {
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                patch[r * 4 + c] = working[(jy - 2 + r) * width + (jx - 2 + c)];
            OverlapFilters.OverlapPostFilter4x4(patch);
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                working[(jy - 2 + r) * width + (jx - 2 + c)] = patch[r * 4 + c];
        }
    }

    /// <summary>Load a 4×4 sub-block from a pre-scaled signed-int working buffer with edge clamping.</summary>
    internal static void LoadSubBlockFromWorking(int[] working, int width, int height, int x0, int y0, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; if (y >= height) y = height - 1;
            var x = x0 + c; if (x >= width)  x = width  - 1;
            dst[r * 4 + c] = working[y * width + x];
        }
    }

    /// <summary>Per-component variant of <see cref="LoadSubBlockFromWorking"/>; reads one channel from an interleaved working buffer.</summary>
    internal static void LoadSubBlockFromWorkingRgb(int[] working, int width, int height,
        int x0, int y0, int comp, int numComponents, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; if (y >= height) y = height - 1;
            var x = x0 + c; if (x >= width)  x = width  - 1;
            dst[r * 4 + c] = working[(y * width + x) * numComponents + comp];
        }
    }

    /// <summary>
    /// Per-component POT pre-filter for an interleaved RGB working buffer. Each
    /// of the <paramref name="numComponents"/> channels gets its own pass — POT
    /// is purely intra-channel, so the channels are independent.
    /// </summary>
    internal static void ApplyPreFilterPotRgb(int[] working, int width, int height, int numComponents)
    {
        Span<int> patch = stackalloc int[16];
        for (var comp = 0; comp < numComponents; comp++)
        for (var jy = 4; jy + 2 <= height; jy += 4)
        for (var jx = 4; jx + 2 <= width;  jx += 4)
        {
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                patch[r * 4 + c] = working[((jy - 2 + r) * width + (jx - 2 + c)) * numComponents + comp];
            OverlapFilters.OverlapPreFilter4x4(patch);
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                working[((jy - 2 + r) * width + (jx - 2 + c)) * numComponents + comp] = patch[r * 4 + c];
        }
    }

    /// <summary>Inverse of <see cref="ApplyPreFilterPotRgb"/> — per-component POT post-filter.</summary>
    internal static void ApplyPostFilterPotRgb(int[] working, int width, int height, int numComponents)
    {
        Span<int> patch = stackalloc int[16];
        for (var comp = 0; comp < numComponents; comp++)
        for (var jy = 4; jy + 2 <= height; jy += 4)
        for (var jx = 4; jx + 2 <= width;  jx += 4)
        {
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                patch[r * 4 + c] = working[((jy - 2 + r) * width + (jx - 2 + c)) * numComponents + comp];
            OverlapFilters.OverlapPostFilter4x4(patch);
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                working[((jy - 2 + r) * width + (jx - 2 + c)) * numComponents + comp] = patch[r * 4 + c];
        }
    }

    /// <summary>
    /// Encode an 8-bit RGB image (interleaved R, G, B in row-major order)
    /// with the NoFlexbits band configuration. Lossless for arbitrary
    /// pixel content at OverlapMode = 0.
    /// </summary>
    /// <param name="pixels">
    /// <c>width × height × 3</c> bytes, interleaved as <c>R, G, B, R, G, B, …</c>.
    /// </param>
    public static byte[] EncodeBd8RgbNoFlexbits(byte[] pixels, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false,
        bool useYUV444 = false)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height * 3)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height * 3}");
        if (overlapMode is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(overlapMode), "supported values are 0 and 1");

        const int numComponents = 3;
        // useYUV444=true: apply YCoCg-R color transform pre-FCT and tag the codestream
        // as InternalClrFmt=YUV444. WIC's WMPhoto decoder rejects InternalClrFmt=Rgb,
        // so this mode is required for Windows Photo interop. Note: OutputClrFmt is
        // set to NComponent (not YUV444) — Microsoft's WIC writes it that way and
        // WIC's decoder rejects OutputClrFmt=YUV444 even when InternalClrFmt=YUV444
        // is allowed. The container's PixelFormat GUID (Rgb24Bpp) tells consumers
        // the logical interpretation.
        var format = useYUV444 ? JxrInternalColorFormat.YUV444 : JxrInternalColorFormat.Rgb;
        var outputClrFmt = useYUV444 ? JxrOutputColorFormat.NComponent : JxrOutputColorFormat.Rgb;

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

        // Pre-scaled interleaved RGB working buffer.
        var working = new int[width * height * 3];
        for (var i = 0; i < width * height * 3; i++) working[i] = pixels[i] - Bd8Bias;
        if (useYUV444) YCoCgTransform.ForwardInPlace(working);
        if (overlapMode == 1) ApplyPreFilterPotRgb(working, width, height, numComponents);
        // BD8 lossless: jxrlib bScaledArith condition resolves to FALSE
        // (uQPMode uniform + QPIndex 0/1 + SB_ALL + no UV-resampling).
        const bool scaledArith = false;

        var mbDc = new int[mbW, mbH, numComponents];
        var mbDcLp = new int[mbW, mbH, numComponents, 16];
        var mbHp = new int[mbW, mbH, numComponents, 16, 16];

        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
        {
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                LoadSubBlockFromWorkingRgb(working, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, numComponents, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbCol * 4 + sbRow; // col-major to match jxrlib blkOffset[]
                dcGrid[sbCol * 4 + sbRow] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blkIdx, p] = subBlock[p];
            }
            // strNormalizeEnc: jxrlib strFwdTransform.c:724-728 halves the 16
            // sub-block DC slots for chroma channels before strDCT4x4SecondStage
            // when bScaledArith. Luma is a no-op. Mirrored on decode after
            // ICT4x4Stage2.
            if (scaledArith && comp > 0)
                for (var k = 0; k < 16; k++) dcGrid[k] >>= 1;
            Transforms.FCT4x4Stage2(dcGrid);
            mbDc[mbx, mby, comp] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, comp, p] = dcGrid[p];
        }

        // Quantize before prediction; QP=1 is a no-op.
        var dcDiv = JxrQuant.QpIndexToDivisor(dcQp);
        var lpDiv = JxrQuant.QpIndexToDivisor(lpQp);
        var hpDiv = JxrQuant.QpIndexToDivisor(hpQp);
        JxrQuant.QuantizeDc(mbDc, dcDiv);
        JxrQuant.QuantizeLp(mbDcLp, lpDiv);
        JxrQuant.QuantizeHp(mbHp, hpDiv);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < numComponents; c++)
            mbDcLp[mbx, mby, c, 0] = mbDc[mbx, mby, c];

        // Tile-aware DC prediction.
        bool[,]? leftMask = null;
        bool[,]? topMask = null;
        if (tiling is not null)
            (leftMask, topMask) = tiling.BuildMasks(mbW, mbH);

        // Spec order DC → LP → HP. HP CalcMode runs on the post-LpPrediction
        // RESIDUAL LP coefficients, matching the decoder which derives the
        // same mode from residual LP just read from the bitstream
        // (TileSpatial.Read → DeriveMbHpMode). Encoder and decoder must agree
        // bit-exactly on what input feeds CalcMode, otherwise the adaptive HP
        // scan direction diverges and the bitstream is unreadable.
        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, leftMask, topMask, mbDcMode);

        var predDcLp = new int[mbW, mbH, numComponents, 16];
        LpPrediction.Encode(mbDcLp, predDcLp, mbDcMode, format);

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, format, numComponents);
        HpPrediction.Encode(mbHp, mbHpMode, format);

        // Pack into Macroblock[].
        var mbs = new Macroblock[mbW * mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var dc = new int[numComponents];
            var lp = new int[numComponents * 16];
            var hp = new int[numComponents * 256];
            for (var comp = 0; comp < numComponents; comp++)
            {
                dc[comp] = mbDc[mbx, mby, comp];
                for (var p = 0; p < 16; p++)
                    lp[comp * 16 + p] = mbDcLp[mbx, mby, comp, p];
                for (var blk = 0; blk < 16; blk++)
                for (var p = 1; p < 16; p++)
                    hp[comp * 256 + blk * 16 + p] = mbHp[mbx, mby, comp, blk, p];
            }
            mbs[mby * mbW + mbx] = new Macroblock
            {
                Dc = dc,
                Lp = lp,
                Hp = hp,
                MbHpMode = mbHpMode[mbx, mby],
            };
        }

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height, outputClrFmt, JxrOutputBitDepth.Bd8, tiling, overlapMode, frequencyMode),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = numComponents,
                DcQuant = dcQp,
                LpQuant = lpQp,
                HpQuant = hpQp,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.LUnrestricted),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    /// <summary>
    /// Encode a 16-bit grayscale image with the NoFlexbits band configuration.
    /// Lossless for arbitrary content at OverlapMode = 0. This is the HDR-master
    /// target path for monochrome.
    /// </summary>
    public static byte[] EncodeBd16GrayscaleNoFlexbits(ushort[] pixels, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height}");
        if (overlapMode is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(overlapMode), "supported values are 0 and 1");

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

        var working = new int[width * height];
        for (var i = 0; i < width * height; i++) working[i] = pixels[i] - Bd16Bias;
        if (overlapMode == 1) ApplyPreFilterPot(working, width, height);

        var mbDc = new int[mbW, mbH, 1];
        var mbDcLp = new int[mbW, mbH, 1, 16];
        var mbHp = new int[mbW, mbH, 1, 16, 16];

        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                LoadSubBlockFromWorking(working, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbCol * 4 + sbRow; // col-major to match jxrlib blkOffset[]
                dcGrid[sbCol * 4 + sbRow] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, 0, blkIdx, p] = subBlock[p];
            }
            Transforms.FCT4x4Stage2(dcGrid);
            mbDc[mbx, mby, 0] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, 0, p] = dcGrid[p];
        }

        const JxrInternalColorFormat format = JxrInternalColorFormat.YOnly;

        var dcDiv = JxrQuant.QpIndexToDivisor(dcQp);
        var lpDiv = JxrQuant.QpIndexToDivisor(lpQp);
        var hpDiv = JxrQuant.QpIndexToDivisor(hpQp);
        JxrQuant.QuantizeDc(mbDc, dcDiv);
        JxrQuant.QuantizeLp(mbDcLp, lpDiv);
        JxrQuant.QuantizeHp(mbHp, hpDiv);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbDcLp[mbx, mby, 0, 0] = mbDc[mbx, mby, 0];

        bool[,]? leftMask = null;
        bool[,]? topMask = null;
        if (tiling is not null) (leftMask, topMask) = tiling.BuildMasks(mbW, mbH);

        var predDc = new int[mbW, mbH, 1];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, leftMask, topMask, mbDcMode);

        var predDcLp = new int[mbW, mbH, 1, 16];
        LpPrediction.Encode(mbDcLp, predDcLp, mbDcMode, format);

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, format, numComponents: 1);
        HpPrediction.Encode(mbHp, mbHpMode, format);

        var mbs = new Macroblock[mbW * mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var lp = new int[16];
            for (var p = 0; p < 16; p++) lp[p] = mbDcLp[mbx, mby, 0, p];
            var hp = new int[256];
            for (var blk = 0; blk < 16; blk++)
            for (var p = 1; p < 16; p++)
                hp[blk * 16 + p] = mbHp[mbx, mby, 0, blk, p];
            mbs[mby * mbW + mbx] = new Macroblock
            {
                Dc = [mbDc[mbx, mby, 0]],
                Lp = lp,
                Hp = hp,
                MbHpMode = mbHpMode[mbx, mby],
            };
        }

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height, JxrOutputColorFormat.YOnly, JxrOutputBitDepth.Bd16, tiling, overlapMode, frequencyMode),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = 1,
                ShiftBits = 0,
                DcQuant = dcQp,
                LpQuant = lpQp,
                HpQuant = hpQp,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.LUnrestricted),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    /// <summary>
    /// Encode a 16-bit RGB image (interleaved R, G, B in row-major order) — the
    /// primary HDR-master deliverable shape for the SharpAstro pipeline.
    /// </summary>
    public static byte[] EncodeBd16RgbNoFlexbits(ushort[] pixels, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false,
        bool useYUV444 = false)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height * 3)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height * 3}");
        if (overlapMode is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(overlapMode), "supported values are 0 and 1");

        const int numComponents = 3;
        // See EncodeBd8RgbNoFlexbits for why YUV444 pairs with OutputClrFmt=NComponent.
        var format = useYUV444 ? JxrInternalColorFormat.YUV444 : JxrInternalColorFormat.Rgb;
        var outputClrFmt = useYUV444 ? JxrOutputColorFormat.NComponent : JxrOutputColorFormat.Rgb;

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

        var working = new int[width * height * 3];
        for (var i = 0; i < width * height * 3; i++) working[i] = pixels[i] - Bd16Bias;
        if (useYUV444) YCoCgTransform.ForwardInPlace(working);
        // BD16 integer lossless: jxrlib bScaledArith condition resolves to FALSE.
        const bool scaledArith = false;
        if (overlapMode == 1) ApplyPreFilterPotRgb(working, width, height, numComponents);

        var mbDc = new int[mbW, mbH, numComponents];
        var mbDcLp = new int[mbW, mbH, numComponents, 16];
        var mbHp = new int[mbW, mbH, numComponents, 16, 16];

        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
        {
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                LoadSubBlockFromWorkingRgb(working, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, numComponents, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbCol * 4 + sbRow; // col-major to match jxrlib blkOffset[]
                dcGrid[sbCol * 4 + sbRow] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blkIdx, p] = subBlock[p];
            }
            // strNormalizeEnc: jxrlib strFwdTransform.c:724-728 halves the 16
            // sub-block DC slots for chroma channels before strDCT4x4SecondStage
            // when bScaledArith. Luma is a no-op. Mirrored on decode after
            // ICT4x4Stage2.
            if (scaledArith && comp > 0)
                for (var k = 0; k < 16; k++) dcGrid[k] >>= 1;
            Transforms.FCT4x4Stage2(dcGrid);
            mbDc[mbx, mby, comp] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, comp, p] = dcGrid[p];
        }

        var dcDiv = JxrQuant.QpIndexToDivisor(dcQp);
        var lpDiv = JxrQuant.QpIndexToDivisor(lpQp);
        var hpDiv = JxrQuant.QpIndexToDivisor(hpQp);
        JxrQuant.QuantizeDc(mbDc, dcDiv);
        JxrQuant.QuantizeLp(mbDcLp, lpDiv);
        JxrQuant.QuantizeHp(mbHp, hpDiv);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < numComponents; c++)
            mbDcLp[mbx, mby, c, 0] = mbDc[mbx, mby, c];

        bool[,]? leftMask = null;
        bool[,]? topMask = null;
        if (tiling is not null) (leftMask, topMask) = tiling.BuildMasks(mbW, mbH);

        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, leftMask, topMask, mbDcMode);

        var predDcLp = new int[mbW, mbH, numComponents, 16];
        LpPrediction.Encode(mbDcLp, predDcLp, mbDcMode, format);

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, format, numComponents);
        HpPrediction.Encode(mbHp, mbHpMode, format);

        var mbs = new Macroblock[mbW * mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var dc = new int[numComponents];
            var lp = new int[numComponents * 16];
            var hp = new int[numComponents * 256];
            for (var comp = 0; comp < numComponents; comp++)
            {
                dc[comp] = mbDc[mbx, mby, comp];
                for (var p = 0; p < 16; p++)
                    lp[comp * 16 + p] = mbDcLp[mbx, mby, comp, p];
                for (var blk = 0; blk < 16; blk++)
                for (var p = 1; p < 16; p++)
                    hp[comp * 256 + blk * 16 + p] = mbHp[mbx, mby, comp, blk, p];
            }
            mbs[mby * mbW + mbx] = new Macroblock
            {
                Dc = dc,
                Lp = lp,
                Hp = hp,
                MbHpMode = mbHpMode[mbx, mby],
            };
        }

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height, outputClrFmt, JxrOutputBitDepth.Bd16, tiling, overlapMode, frequencyMode),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = numComponents,
                ShiftBits = 0,
                DcQuant = dcQp,
                LpQuant = lpQp,
                HpQuant = hpQp,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.LUnrestricted),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    // Sub-block loaders. All clamp coordinates to the image bounds so that
    // edge MBs covering padded pixels read the nearest real sample (T.832 D.1.2
    // edge-extension by replication). Keeps the FCT happy without needing a
    // separate padded buffer allocation.

    private static void LoadSubBlock(byte[] pixels, int width, int height, int x0, int y0, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; if (y >= height) y = height - 1;
            var x = x0 + c; if (x >= width)  x = width  - 1;
            dst[r * 4 + c] = pixels[y * width + x] - Bd8Bias;
        }
    }

    private static void LoadSubBlockRgb(byte[] pixels, int width, int height, int x0, int y0, int comp, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; if (y >= height) y = height - 1;
            var x = x0 + c; if (x >= width)  x = width  - 1;
            dst[r * 4 + c] = pixels[(y * width + x) * 3 + comp] - Bd8Bias;
        }
    }

    private static void LoadSubBlock16(ushort[] pixels, int width, int height, int x0, int y0, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; if (y >= height) y = height - 1;
            var x = x0 + c; if (x >= width)  x = width  - 1;
            dst[r * 4 + c] = pixels[y * width + x] - Bd16Bias;
        }
    }

    private static void LoadSubBlock16Rgb(ushort[] pixels, int width, int height, int x0, int y0, int comp, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; if (y >= height) y = height - 1;
            var x = x0 + c; if (x >= width)  x = width  - 1;
            dst[r * 4 + c] = pixels[(y * width + x) * 3 + comp] - Bd16Bias;
        }
    }

    /// <summary>
    /// Encode a 16-bit half-float grayscale image (IEEE binary16 bit patterns) — the
    /// HDR-master target for radiance / linear-light pipelines.
    /// </summary>
    /// <param name="halfBits"><c>width × height</c> ushorts; each holds the IEEE 754
    /// binary16 bit pattern (1 sign + 5 exponent + 10 mantissa) — same memory layout
    /// as <c>System.Half</c>.</param>
    /// <remarks>
    /// The encoder treats the 16-bit half-float pattern as the sample to compress —
    /// equivalent to the same <c>bias-by-32768</c> integer pipeline used for BD16,
    /// just labelled with OutputBitDepth = Bd16F and the matching half-float
    /// LEN_MANTISSA / EXP_BIAS in the plane header. Lossless because the integer
    /// FCT pipeline preserves bit patterns exactly.
    /// </remarks>
    public static byte[] EncodeBd16FGrayscaleNoFlexbits(ushort[] halfBits, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false)
    {
        ValidateBd16F(halfBits, width, height, expectedComponents: 1);

        var bytes = EncodeBd16PipelineCore(
            halfBits,
            width,
            height,
            numComponents: 1,
            format: JxrInternalColorFormat.YOnly,
            outputClrFmt: JxrOutputColorFormat.YOnly,
            outputBitDepth: JxrOutputBitDepth.Bd16F,
            lenMantissa: 10,
            expBias: 15,
            tiling: tiling,
            dcQp: dcQp, lpQp: lpQp, hpQp: hpQp,
            overlapMode: overlapMode,
            frequencyMode: frequencyMode);
        return bytes;
    }

    /// <summary>
    /// Encode a 16-bit half-float RGB image. The full HDR-master deliverable: float
    /// dynamic range in a JPEG XR codestream.
    /// </summary>
    public static byte[] EncodeBd16FRgbNoFlexbits(ushort[] halfBits, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false,
        bool useYUV444 = false)
    {
        ValidateBd16F(halfBits, width, height, expectedComponents: 3);

        return EncodeBd16PipelineCore(
            halfBits,
            width,
            height,
            numComponents: 3,
            format: useYUV444 ? JxrInternalColorFormat.YUV444 : JxrInternalColorFormat.Rgb,
            outputClrFmt: useYUV444 ? JxrOutputColorFormat.NComponent : JxrOutputColorFormat.Rgb,
            outputBitDepth: JxrOutputBitDepth.Bd16F,
            lenMantissa: 10,
            expBias: 15,
            tiling: tiling,
            dcQp: dcQp, lpQp: lpQp, hpQp: hpQp,
            overlapMode: overlapMode,
            frequencyMode: frequencyMode);
    }

    private static void ValidateBd16F(ushort[] halfBits, int width, int height, int expectedComponents)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (halfBits.Length < width * height * expectedComponents)
            throw new ArgumentException($"halfBits has length {halfBits.Length}, expected ≥ {width * height * expectedComponents}");
    }

    private static byte[] EncodeBd16PipelineCore(
        ushort[] src,
        int width,
        int height,
        int numComponents,
        JxrInternalColorFormat format,
        JxrOutputColorFormat outputClrFmt,
        JxrOutputBitDepth outputBitDepth,
        byte lenMantissa,
        int expBias,
        JxrTileLayout? tiling,
        byte dcQp,
        byte lpQp,
        byte hpQp,
        int overlapMode = 0,
        bool frequencyMode = false)
    {
        if (overlapMode is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(overlapMode), "supported values are 0 and 1");

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

        // Pre-scaled signed-int working buffer. Layout: width × height × numComponents
        // interleaved (single-component case degenerates to a flat ints buffer).
        var working = new int[width * height * numComponents];
        const int Bd16FScaleShift = 3;
        // bScaledArith always enabled for BD16F: jxrlib's spec gate (strenc.c:
        // 957) turns it off for lossless QP=1, but our integer FCT/ICT
        // lifting steps lose precision on small inputs without the 3-bit
        // headroom (21 BD16F self-roundtrip tests fail when the gate is
        // applied). Keeping bScaledArith on unconditionally for BD16F so the
        // pre-shift gives the transform pipeline the headroom it needs.
        // Cross-codec impact: jxrlib lossless BD16F files will be decoded
        // with our >>3 post-shift erroneously applied — not a regression for
        // the current target which is to round-trip our OWN encoder's BD16F
        // output through jxrlib.
        var scaledArith = outputBitDepth == JxrOutputBitDepth.Bd16F;
        if (outputBitDepth == JxrOutputBitDepth.Bd16F)
        {
            // Half-float input: sign-magnitude conversion per jxrlib's forwardHalf
            // followed by the bScaledArith pre-shift (cShift = SHIFTZERO +
            // QPFRACBITS = 3 in jxrlib/common.h). Paired with ScaledFlag=true
            // in the plane header so the decode side knows to right-shift back.
            for (var i = 0; i < src.Length; i++)
            {
                var bits = src[i];
                var magnitude = bits & 0x7FFF;
                var signed = (bits & 0x8000) != 0 ? -magnitude : magnitude;
                working[i] = signed << Bd16FScaleShift;
            }
        }
        else
        {
            // Integer path: midpoint-bias subtraction to centre samples around 0.
            for (var i = 0; i < width * height * numComponents; i++) working[i] = src[i] - Bd16Bias;
        }
        // YCoCg-R lifting when the caller asks for InternalClrFmt=YUV444 with
        // 3 components. See Phase 22 — encodes RGB samples as Y/Co/Cg so the
        // codestream can claim YUV444 (the only 3-component internal format
        // WIC's WMPhoto decoder accepts).
        if (format == JxrInternalColorFormat.YUV444 && numComponents == 3)
            YCoCgTransform.ForwardInPlace(working);
        if (overlapMode == 1)
        {
            if (numComponents == 1) ApplyPreFilterPot(working, width, height);
            else ApplyPreFilterPotRgb(working, width, height, numComponents);
        }

        var mbDc = new int[mbW, mbH, numComponents];
        var mbDcLp = new int[mbW, mbH, numComponents, 16];
        var mbHp = new int[mbW, mbH, numComponents, 16, 16];

        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
        {
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                var x0 = mbx * 16 + sbCol * 4;
                var y0 = mby * 16 + sbRow * 4;
                if (numComponents == 1)
                    LoadSubBlockFromWorking(working, width, height, x0, y0, subBlock);
                else
                    LoadSubBlockFromWorkingRgb(working, width, height, x0, y0, comp, numComponents, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbCol * 4 + sbRow; // col-major to match jxrlib blkOffset[]
                dcGrid[sbCol * 4 + sbRow] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blkIdx, p] = subBlock[p];
            }
            // strNormalizeEnc: jxrlib strFwdTransform.c:724-728 halves the 16
            // sub-block DC slots for chroma channels before strDCT4x4SecondStage
            // when bScaledArith. Luma is a no-op. Mirrored on decode after
            // ICT4x4Stage2.
            if (scaledArith && comp > 0)
                for (var k = 0; k < 16; k++) dcGrid[k] >>= 1;
            Transforms.FCT4x4Stage2(dcGrid);
            mbDc[mbx, mby, comp] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, comp, p] = dcGrid[p];

            if (Environment.GetEnvironmentVariable("DIR_LIB_JXR_TRACE") == "1" && mbx <= 1 && mby == 0 && comp == 0)
            {
                System.Console.Error.Write($"OUR PCT mb=({mbx},{mby}) ch={comp} post-PCT DC-block:");
                for (var p = 0; p < 16; p++) System.Console.Error.Write($" {dcGrid[p]}");
                System.Console.Error.WriteLine();
            }
        }

        // jxrlib's formatQuantizer multiplies by 2^3 when bScaledArith
        // (strPredQuant.c:79-117). Thread scaledArith through so the
        // bitstream-stored coefficients match jxrlib's expectations.
        var dcDiv = JxrQuant.QpIndexToDivisor(dcQp, scaledArith);
        var lpDiv = JxrQuant.QpIndexToDivisor(lpQp, scaledArith);
        var hpDiv = JxrQuant.QpIndexToDivisor(hpQp, scaledArith);
        JxrQuant.QuantizeDc(mbDc, dcDiv);
        JxrQuant.QuantizeLp(mbDcLp, lpDiv);
        JxrQuant.QuantizeHp(mbHp, hpDiv);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < numComponents; c++)
            mbDcLp[mbx, mby, c, 0] = mbDc[mbx, mby, c];

        bool[,]? leftMask = null;
        bool[,]? topMask = null;
        if (tiling is not null) (leftMask, topMask) = tiling.BuildMasks(mbW, mbH);

        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, leftMask, topMask, mbDcMode);

        var predDcLp = new int[mbW, mbH, numComponents, 16];
        LpPrediction.Encode(mbDcLp, predDcLp, mbDcMode, format);

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, format, numComponents);
        HpPrediction.Encode(mbHp, mbHpMode, format);

        var mbs = new Macroblock[mbW * mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var dc = new int[numComponents];
            var lp = new int[numComponents * 16];
            var hp = new int[numComponents * 256];
            for (var comp = 0; comp < numComponents; comp++)
            {
                dc[comp] = mbDc[mbx, mby, comp];
                for (var p = 0; p < 16; p++)
                    lp[comp * 16 + p] = mbDcLp[mbx, mby, comp, p];
                for (var blk = 0; blk < 16; blk++)
                for (var p = 1; p < 16; p++)
                    hp[comp * 256 + blk * 16 + p] = mbHp[mbx, mby, comp, blk, p];
            }
            mbs[mby * mbW + mbx] = new Macroblock
            {
                Dc = dc,
                Lp = lp,
                Hp = hp,
                MbHpMode = mbHpMode[mbx, mby],
            };
        }

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height, outputClrFmt, outputBitDepth, tiling, overlapMode, frequencyMode),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = numComponents,
                LenMantissa = lenMantissa,
                ExpBias = (sbyte)expBias,
                DcQuant = dcQp,
                LpQuant = lpQp,
                HpQuant = hpQp,
                // ScaledFlag signals the bScaledArith pre-multiplication done
                // for BD16F (see Bd16FScaleShift above). The decoder pairs this
                // bit with a matching post-IDCT right-shift.
                ScaledFlag = scaledArith,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.LUnrestricted),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    // -------------------------------------------------------------------------
    // BD32F (32-bit float) — full single-precision floating point
    // -------------------------------------------------------------------------
    //
    // Float pixels can't ride the simple "ushort bias = 2^bitdepth-1" trick
    // BD16/BD16F use, because uint32 doesn't fit in int32 and the FCT cascade
    // amplifies a 31-bit input by ~6–8 bits, overflowing the signed pipeline.
    // T.832 D.2.5 instead reshapes each float into a finite-precision
    // sign-magnitude integer: (1 sign + 8 exp + LEN_MANTISSA mantissa bits).
    // With <c>lenMantissa = 8</c> (default), the encoded magnitude tops out
    // around ±2^16, so the FCT pyramid stays comfortably within int32. Larger
    // <c>lenMantissa</c> preserves more mantissa precision; <c>23</c>
    // preserves the full float mantissa but only round-trips losslessly for
    // small-magnitude floats where the FCT cascade doesn't push past int32.

    /// <summary>
    /// Encode a 32-bit float grayscale image. The float bit patterns are
    /// reshaped via T.832 D.2.5 into a sign-magnitude integer with
    /// <paramref name="lenMantissa"/> mantissa bits before the FCT pipeline.
    /// </summary>
    /// <param name="lenMantissa">Mantissa bits preserved in the encoded
    /// representation; 1..23. Defaults to 8 — keeps the FCT cascade safely
    /// inside int32 for any input float. At 23 the original 23-bit mantissa
    /// is preserved bit-exact, but only floats with modest dynamic range
    /// avoid FCT overflow.</param>
    public static byte[] EncodeBd32FGrayscaleNoFlexbits(float[] pixels, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false,
        byte lenMantissa = 8)
    {
        ValidateBd32F(pixels, width, height, expectedComponents: 1, lenMantissa);
        var asInts = Bd32FToIntArray(pixels, lenMantissa, numComponents: 1, width, height);
        return EncodeBd32FPipelineCore(asInts, width, height, numComponents: 1,
            format: JxrInternalColorFormat.YOnly, outputClrFmt: JxrOutputColorFormat.YOnly,
            lenMantissa: lenMantissa, tiling: tiling,
            dcQp: dcQp, lpQp: lpQp, hpQp: hpQp,
            overlapMode: overlapMode, frequencyMode: frequencyMode);
    }

    /// <summary>
    /// Encode a 32-bit float RGB image — single-precision HDR linear-light.
    /// </summary>
    public static byte[] EncodeBd32FRgbNoFlexbits(float[] pixels, int width, int height,
        JxrTileLayout? tiling = null,
        byte dcQp = 1, byte lpQp = 1, byte hpQp = 1,
        int overlapMode = 0,
        bool frequencyMode = false,
        byte lenMantissa = 8,
        bool useYUV444 = false)
    {
        ValidateBd32F(pixels, width, height, expectedComponents: 3, lenMantissa);
        var asInts = Bd32FToIntArray(pixels, lenMantissa, numComponents: 3, width, height);
        return EncodeBd32FPipelineCore(asInts, width, height, numComponents: 3,
            format: useYUV444 ? JxrInternalColorFormat.YUV444 : JxrInternalColorFormat.Rgb,
            outputClrFmt: useYUV444 ? JxrOutputColorFormat.NComponent : JxrOutputColorFormat.Rgb,
            lenMantissa: lenMantissa, tiling: tiling,
            dcQp: dcQp, lpQp: lpQp, hpQp: hpQp,
            overlapMode: overlapMode, frequencyMode: frequencyMode);
    }

    private static void ValidateBd32F(float[] pixels, int width, int height, int expectedComponents, byte lenMantissa)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height * expectedComponents)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height * expectedComponents}");
        if (lenMantissa is < 1 or > 23)
            throw new ArgumentOutOfRangeException(nameof(lenMantissa), "BD32F LEN_MANTISSA must be in 1..23");
    }

    /// <summary>
    /// T.832 D.2.5 float → sign-magnitude integer conversion. Each float is
    /// decomposed into (sign, biased-exp, mantissa) and packed as
    /// <c>±((exp &lt;&lt; lenMantissa) | (mantissa &gt;&gt; (23 - lenMantissa)))</c>.
    /// EXP_BIAS in the plane header is set to <c>0 - 128 = -128</c> so the
    /// decoder reads back the same encoded exponent we wrote.
    /// </summary>
    internal static int[] Bd32FToIntArray(float[] pixels, byte lenMantissa, int numComponents, int width, int height)
    {
        var n = width * height * numComponents;
        var result = new int[n];
        var mantissaShift = 23 - lenMantissa;
        for (var i = 0; i < n; i++)
        {
            var raw = BitConverter.SingleToUInt32Bits(pixels[i]);
            var sign = (raw >> 31) != 0;
            var exp = (int)((raw >> 23) & 0xFF);
            var mant = (int)(raw & 0x7FFFFF);
            // Sign-magnitude packing per D.2.5: (exp << lenMantissa) | (mant >> shift).
            // ±zero (exp == 0 && mant == 0) maps to 0; otherwise to ±(mag).
            var mag = (exp << lenMantissa) | (mant >> mantissaShift);
            result[i] = sign ? -mag : mag;
        }
        return result;
    }

    /// <summary>Inverse of <see cref="Bd32FToIntArray"/> — reconstruct float bit patterns from sign-magnitude ints.</summary>
    internal static float[] IntArrayToBd32F(int[] encoded, byte lenMantissa)
    {
        var result = new float[encoded.Length];
        var mantissaShift = 23 - lenMantissa;
        var mantissaMask = (1 << lenMantissa) - 1;
        for (var i = 0; i < encoded.Length; i++)
        {
            var v = encoded[i];
            var sign = v < 0;
            var mag = sign ? -v : v;
            var exp = (uint)((mag >> lenMantissa) & 0xFF);
            var mant = (uint)((mag & mantissaMask) << mantissaShift);
            var raw = (sign ? 0x80000000u : 0u) | (exp << 23) | mant;
            result[i] = BitConverter.UInt32BitsToSingle(raw);
        }
        return result;
    }

    /// <summary>
    /// BD32F variant of <see cref="EncodeBd16PipelineCore"/>. Input is a flat
    /// <c>width × height × numComponents</c> array of sign-magnitude ints
    /// produced by <see cref="Bd32FToIntArray"/>. EXP_BIAS is hard-coded to
    /// <c>-128</c> (= 0 in the codestream raw byte) so the decoder reads back
    /// the encoded exponent unmodified.
    /// </summary>
    private static byte[] EncodeBd32FPipelineCore(
        int[] src, int width, int height, int numComponents,
        JxrInternalColorFormat format, JxrOutputColorFormat outputClrFmt,
        byte lenMantissa, JxrTileLayout? tiling,
        byte dcQp, byte lpQp, byte hpQp,
        int overlapMode, bool frequencyMode)
    {
        if (overlapMode is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(overlapMode), "supported values are 0 and 1");

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

        // Pre-converted signed-int working buffer. No further biasing needed —
        // the float→int conversion already produces sign-magnitude values.
        var working = new int[width * height * numComponents];
        Array.Copy(src, working, src.Length);
        // jxrlib forces bScaledArith = FALSE for BD32 family (strenc.c:961-963),
        // so the BD32F path never applies the strNormalize chroma-DC halving.
        const bool scaledArith = false;
        // YCoCg-R for the WIC-interop YUV444 path — same logic as BD16Core.
        if (format == JxrInternalColorFormat.YUV444 && numComponents == 3)
            YCoCgTransform.ForwardInPlace(working);
        if (overlapMode == 1)
        {
            if (numComponents == 1) ApplyPreFilterPot(working, width, height);
            else ApplyPreFilterPotRgb(working, width, height, numComponents);
        }

        var mbDc = new int[mbW, mbH, numComponents];
        var mbDcLp = new int[mbW, mbH, numComponents, 16];
        var mbHp = new int[mbW, mbH, numComponents, 16, 16];

        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
        {
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                var x0 = mbx * 16 + sbCol * 4;
                var y0 = mby * 16 + sbRow * 4;
                if (numComponents == 1)
                    LoadSubBlockFromWorking(working, width, height, x0, y0, subBlock);
                else
                    LoadSubBlockFromWorkingRgb(working, width, height, x0, y0, comp, numComponents, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbCol * 4 + sbRow; // col-major to match jxrlib blkOffset[]
                dcGrid[sbCol * 4 + sbRow] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blkIdx, p] = subBlock[p];
            }
            // strNormalizeEnc: jxrlib strFwdTransform.c:724-728 halves the 16
            // sub-block DC slots for chroma channels before strDCT4x4SecondStage
            // when bScaledArith. Luma is a no-op. Mirrored on decode after
            // ICT4x4Stage2.
            if (scaledArith && comp > 0)
                for (var k = 0; k < 16; k++) dcGrid[k] >>= 1;
            Transforms.FCT4x4Stage2(dcGrid);
            mbDc[mbx, mby, comp] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, comp, p] = dcGrid[p];

            if (Environment.GetEnvironmentVariable("DIR_LIB_JXR_TRACE") == "1" && mbx <= 1 && mby == 0 && comp == 0)
            {
                System.Console.Error.Write($"OUR PCT mb=({mbx},{mby}) ch={comp} post-PCT DC-block:");
                for (var p = 0; p < 16; p++) System.Console.Error.Write($" {dcGrid[p]}");
                System.Console.Error.WriteLine();
            }
        }

        // jxrlib's formatQuantizer multiplies by 2^3 when bScaledArith
        // (strPredQuant.c:79-117). Thread scaledArith through so the
        // bitstream-stored coefficients match jxrlib's expectations.
        var dcDiv = JxrQuant.QpIndexToDivisor(dcQp, scaledArith);
        var lpDiv = JxrQuant.QpIndexToDivisor(lpQp, scaledArith);
        var hpDiv = JxrQuant.QpIndexToDivisor(hpQp, scaledArith);
        JxrQuant.QuantizeDc(mbDc, dcDiv);
        JxrQuant.QuantizeLp(mbDcLp, lpDiv);
        JxrQuant.QuantizeHp(mbHp, hpDiv);
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var c = 0; c < numComponents; c++)
            mbDcLp[mbx, mby, c, 0] = mbDc[mbx, mby, c];

        bool[,]? leftMask = null;
        bool[,]? topMask = null;
        if (tiling is not null) (leftMask, topMask) = tiling.BuildMasks(mbW, mbH);

        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, leftMask, topMask, mbDcMode);

        var predDcLp = new int[mbW, mbH, numComponents, 16];
        LpPrediction.Encode(mbDcLp, predDcLp, mbDcMode, format);

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, format, numComponents);
        HpPrediction.Encode(mbHp, mbHpMode, format);

        var mbs = new Macroblock[mbW * mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var dc = new int[numComponents];
            var lp = new int[numComponents * 16];
            var hp = new int[numComponents * 256];
            for (var comp = 0; comp < numComponents; comp++)
            {
                dc[comp] = mbDc[mbx, mby, comp];
                for (var p = 0; p < 16; p++)
                    lp[comp * 16 + p] = mbDcLp[mbx, mby, comp, p];
                for (var blk = 0; blk < 16; blk++)
                for (var p = 1; p < 16; p++)
                    hp[comp * 256 + blk * 16 + p] = mbHp[mbx, mby, comp, blk, p];
            }
            mbs[mby * mbW + mbx] = new Macroblock
            {
                Dc = dc,
                Lp = lp,
                Hp = hp,
                MbHpMode = mbHpMode[mbx, mby],
            };
        }

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height, outputClrFmt, JxrOutputBitDepth.Bd32F, tiling, overlapMode, frequencyMode),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = numComponents,
                LenMantissa = lenMantissa,
                ExpBias = unchecked((sbyte)-128), // T.832 stores as raw u(8); -128 = "no exponent adjustment".
                DcQuant = dcQp,
                LpQuant = lpQp,
                HpQuant = hpQp,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.LUnrestricted),
            Macroblocks = mbs,
        };
        return img.Encode();
    }
}
