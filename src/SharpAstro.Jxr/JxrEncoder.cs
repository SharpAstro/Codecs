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

    /// <summary>
    /// Encode an 8-bit grayscale image with the DcOnly band configuration.
    /// Returns the raw JXR codestream (not yet wrapped in an Annex A container).
    /// </summary>
    /// <param name="pixels"><c>width × height</c> sample bytes in row-major order.</param>
    public static byte[] EncodeBd8GrayscaleDcOnly(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "image dimensions must be positive");
        if ((width & 15) != 0 || (height & 15) != 0)
            throw new ArgumentException($"width and height must be multiples of 16 (got {width}×{height}); edge-padding lands in a follow-on commit");
        if (pixels.Length < width * height)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height}");

        var mbW = width >> 4;
        var mbH = height >> 4;
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
                LoadSubBlock(pixels, width, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
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
    public static byte[] EncodeBd8GrayscaleNoFlexbits(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if ((width & 15) != 0 || (height & 15) != 0)
            throw new ArgumentException($"width and height must be multiples of 16 (got {width}×{height})");
        if (pixels.Length < width * height)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height}");

        var mbW = width >> 4;
        var mbH = height >> 4;

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
                LoadSubBlock(pixels, width, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
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

        // Prediction cascade in spec order: DC → LP → HP.
        var predDc = new int[mbW, mbH, 1];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Encode(mbDc, predDc, JxrInternalColorFormat.YOnly, mbDcMode: mbDcMode);

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
        if ((width & 15) != 0 || (height & 15) != 0)
            throw new ArgumentException($"width and height must be multiples of 16 (got {width}×{height})");
        if (pixels.Length < width * height * 3)
            throw new ArgumentException($"pixels has length {pixels.Length}, expected ≥ {width * height * 3}");

        const int numComponents = 3;
        const JxrInternalColorFormat format = JxrInternalColorFormat.Rgb;

        var mbW = width >> 4;
        var mbH = height >> 4;

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
                LoadSubBlockRgb(pixels, width, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, subBlock);
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

    private static void LoadSubBlock(byte[] pixels, int width, int x0, int y0, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
            dst[r * 4 + c] = pixels[(y0 + r) * width + (x0 + c)] - Bd8Bias;
    }

    private static void LoadSubBlockRgb(byte[] pixels, int width, int x0, int y0, int comp, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
            dst[r * 4 + c] = pixels[((y0 + r) * width + (x0 + c)) * 3 + comp] - Bd8Bias;
    }
}
