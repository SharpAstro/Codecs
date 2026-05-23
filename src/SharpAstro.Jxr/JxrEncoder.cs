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

    private static void LoadSubBlock(byte[] pixels, int width, int x0, int y0, Span<int> dst)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
            dst[r * 4 + c] = pixels[(y0 + r) * width + (x0 + c)] - Bd8Bias;
    }
}
