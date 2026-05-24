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
                dcGrid[sbRow * 4 + sbCol] = subBlock[0];
            }
            Transforms.FCT4x4(dcGrid);
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
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
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
        JxrTileLayout? tiling = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height}");

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

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
                LoadSubBlock(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbRow * 4 + sbCol;
                dcGrid[blkIdx] = subBlock[0];
                // Stash positions 1..15 of this sub-block as HP coefficients.
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, 0, blkIdx, p] = subBlock[p];
            }
            Transforms.FCT4x4(dcGrid);
            // Position 0 of dcGrid = super-DC; positions 1..15 = LP coefficients.
            mbDc[mbx, mby, 0] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, 0, p] = dcGrid[p];
        }

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
            ImageHeader = BuildImageHeader(width, height, JxrOutputColorFormat.YOnly, JxrOutputBitDepth.Bd8, tiling),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.YOnly,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = 1,
                DcQuant = 1,
                LpQuant = 1,
                HpQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    /// <summary>
    /// Build an ImageHeader prefilled from the common per-encoder parameters,
    /// optionally populating the tile-grid fields if a layout is supplied.
    /// </summary>
    internal static ImageHeader BuildImageHeader(int width, int height,
        JxrOutputColorFormat outFmt, JxrOutputBitDepth outBd, JxrTileLayout? tiling)
    {
        var h = new ImageHeader
        {
            OutputClrFmt = outFmt,
            OutputBitDepth = outBd,
            ShortHeaderFlag = true,
            WidthMinus1 = (uint)(width - 1),
            HeightMinus1 = (uint)(height - 1),
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
    /// Encode an 8-bit RGB image (interleaved R, G, B in row-major order)
    /// with the NoFlexbits band configuration. Lossless for arbitrary
    /// pixel content at OverlapMode = 0.
    /// </summary>
    /// <param name="pixels">
    /// <c>width × height × 3</c> bytes, interleaved as <c>R, G, B, R, G, B, …</c>.
    /// </param>
    public static byte[] EncodeBd8RgbNoFlexbits(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height * 3)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height * 3}");

        const int numComponents = 3;
        const JxrInternalColorFormat format = JxrInternalColorFormat.Rgb;

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

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
                LoadSubBlockRgb(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbRow * 4 + sbCol;
                dcGrid[blkIdx] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blkIdx, p] = subBlock[p];
            }
            Transforms.FCT4x4(dcGrid);
            mbDc[mbx, mby, comp] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, comp, p] = dcGrid[p];
        }

        // Prediction cascade — DC, LP, HP.
        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, mbDcMode: mbDcMode);

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
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.Rgb,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = (uint)(width - 1),
                HeightMinus1 = (uint)(height - 1),
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = numComponents,
                DcQuant = 1,
                LpQuant = 1,
                HpQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    /// <summary>
    /// Encode a 16-bit grayscale image with the NoFlexbits band configuration.
    /// Lossless for arbitrary content at OverlapMode = 0. This is the HDR-master
    /// target path for monochrome.
    /// </summary>
    public static byte[] EncodeBd16GrayscaleNoFlexbits(ushort[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height}");

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

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
                LoadSubBlock16(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbRow * 4 + sbCol;
                dcGrid[blkIdx] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, 0, blkIdx, p] = subBlock[p];
            }
            Transforms.FCT4x4(dcGrid);
            mbDc[mbx, mby, 0] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, 0, p] = dcGrid[p];
        }

        const JxrInternalColorFormat format = JxrInternalColorFormat.YOnly;
        var predDc = new int[mbW, mbH, 1];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, mbDcMode: mbDcMode);

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
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd16,
                ShortHeaderFlag = true,
                WidthMinus1 = (uint)(width - 1),
                HeightMinus1 = (uint)(height - 1),
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = 1,
                ShiftBits = 0, // no extra bit-depth shift for plain 16-bit unsigned
                DcQuant = 1,
                LpQuant = 1,
                HpQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.L1),
            Macroblocks = mbs,
        };
        return img.Encode();
    }

    /// <summary>
    /// Encode a 16-bit RGB image (interleaved R, G, B in row-major order) — the
    /// primary HDR-master deliverable shape for the SharpAstro pipeline.
    /// </summary>
    public static byte[] EncodeBd16RgbNoFlexbits(ushort[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height * 3)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height * 3}");

        const int numComponents = 3;
        const JxrInternalColorFormat format = JxrInternalColorFormat.Rgb;

        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

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
                LoadSubBlock16Rgb(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbRow * 4 + sbCol;
                dcGrid[blkIdx] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blkIdx, p] = subBlock[p];
            }
            Transforms.FCT4x4(dcGrid);
            mbDc[mbx, mby, comp] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, comp, p] = dcGrid[p];
        }

        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, mbDcMode: mbDcMode);

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
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.Rgb,
                OutputBitDepth = JxrOutputBitDepth.Bd16,
                ShortHeaderFlag = true,
                WidthMinus1 = (uint)(width - 1),
                HeightMinus1 = (uint)(height - 1),
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = numComponents,
                ShiftBits = 0,
                DcQuant = 1,
                LpQuant = 1,
                HpQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.L1),
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
    public static byte[] EncodeBd16FGrayscaleNoFlexbits(ushort[] halfBits, int width, int height)
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
            expBias: 15 - 128,
            loadSubBlock: (src, w, h, x0, y0, _, dst) =>
            {
                for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                {
                    var y = y0 + r; if (y >= h) y = h - 1;
                    var x = x0 + c; if (x >= w) x = w - 1;
                    dst[r * 4 + c] = src[y * w + x] - Bd16Bias;
                }
            });
        return bytes;
    }

    /// <summary>
    /// Encode a 16-bit half-float RGB image. The full HDR-master deliverable: float
    /// dynamic range in a JPEG XR codestream.
    /// </summary>
    public static byte[] EncodeBd16FRgbNoFlexbits(ushort[] halfBits, int width, int height)
    {
        ValidateBd16F(halfBits, width, height, expectedComponents: 3);

        return EncodeBd16PipelineCore(
            halfBits,
            width,
            height,
            numComponents: 3,
            format: JxrInternalColorFormat.Rgb,
            outputClrFmt: JxrOutputColorFormat.Rgb,
            outputBitDepth: JxrOutputBitDepth.Bd16F,
            lenMantissa: 10,
            expBias: 15 - 128,
            loadSubBlock: (src, w, h, x0, y0, comp, dst) =>
            {
                for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                {
                    var y = y0 + r; if (y >= h) y = h - 1;
                    var x = x0 + c; if (x >= w) x = w - 1;
                    dst[r * 4 + c] = src[(y * w + x) * 3 + comp] - Bd16Bias;
                }
            });
    }

    private static void ValidateBd16F(ushort[] halfBits, int width, int height, int expectedComponents)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (halfBits.Length < width * height * expectedComponents)
            throw new ArgumentException($"halfBits has length {halfBits.Length}, expected ≥ {width * height * expectedComponents}");
    }

    private delegate void LoadSubBlockUshort(ushort[] src, int width, int height, int x0, int y0, int comp, Span<int> dst);

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
        LoadSubBlockUshort loadSubBlock)
    {
        var mbW = (width + 15) >> 4;
        var mbH = (height + 15) >> 4;

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
                loadSubBlock(src, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, subBlock);
                Transforms.FCT4x4(subBlock);
                var blkIdx = sbRow * 4 + sbCol;
                dcGrid[blkIdx] = subBlock[0];
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blkIdx, p] = subBlock[p];
            }
            Transforms.FCT4x4(dcGrid);
            mbDc[mbx, mby, comp] = dcGrid[0];
            for (var p = 0; p < 16; p++)
                mbDcLp[mbx, mby, comp, p] = dcGrid[p];
        }

        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, format, mbDcMode: mbDcMode);

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
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = outputClrFmt,
                OutputBitDepth = outputBitDepth,
                ShortHeaderFlag = true,
                WidthMinus1 = (uint)(width - 1),
                HeightMinus1 = (uint)(height - 1),
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = format,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = numComponents,
                LenMantissa = lenMantissa,
                ExpBias = (sbyte)expBias,
                DcQuant = 1,
                LpQuant = 1,
                HpQuant = 1,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.L1),
            Macroblocks = mbs,
        };
        return img.Encode();
    }
}
