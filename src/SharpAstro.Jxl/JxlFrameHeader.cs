namespace SharpAstro.Jxl;

/// <summary>JPEG XL frame pixel-data encoding (ISO/IEC 18181-1 §E.2).</summary>
internal enum JxlFrameEncoding
{
    VarDct = 0,
    Modular = 1,
}

/// <summary>JPEG XL frame type (ISO/IEC 18181-1 §E.2).</summary>
internal enum JxlFrameType
{
    RegularFrame = 0,
    LfFrame = 1,
    ReferenceOnly = 2,
    SkipProgressive = 3,
}

/// <summary>JPEG XL frame blend mode (ISO/IEC 18181-1 §E.2).</summary>
internal enum JxlBlendMode
{
    Replace = 0,
    Add = 1,
    Blend = 2,
    MulAdd = 3,
    Mul = 4,
}

/// <summary>
/// JPEG XL FrameHeader (ISO/IEC 18181-1 §E.2). Implements the full field chain for the
/// still-image case: RegularFrame, single pass, no crop, normal blending. Exotic frames
/// (LF frame, reference-only, crop, animation) throw rather than guess, since they aren't
/// in the oracle set. Validated structurally via TOC byte accounting (see JxlToc).
/// </summary>
internal readonly struct JxlFrameHeader
{
    public JxlFrameEncoding Encoding { get; init; }
    public int GroupDim { get; init; }
    public int NumGroups { get; init; }
    public int NumLfGroups { get; init; }
    public int NumPasses { get; init; }

    private const ulong FlagUseLfFrame = 0x20;

    public static JxlFrameHeader Read(ref JxlBitReader br, in JxlImageMetadata meta, int width, int height)
    {
        bool allDefault = br.ReadBit();

        var encoding = JxlFrameEncoding.VarDct;
        int groupSizeShift = 1; // VarDCT default => group dim 256
        int numPasses = 1;
        int upsampling = 1;

        if (!allDefault)
        {
            var frameType = (JxlFrameType)br.ReadBits(2);
            if (frameType is JxlFrameType.LfFrame or JxlFrameType.ReferenceOnly)
                throw new NotSupportedException($"JPEG XL {frameType} frames are not yet supported.");

            encoding = (JxlFrameEncoding)br.ReadBits(1);
            ulong flags = br.ReadU64();

            bool doYcbcr = !meta.XybEncoded && br.ReadBit();
            bool useLfFrame = (flags & FlagUseLfFrame) != 0;

            if (doYcbcr && !useLfFrame)
                for (int i = 0; i < 3; i++) br.ReadBits(2); // jpeg_upsampling

            if (!useLfFrame)
            {
                upsampling = (int)br.ReadU32((1, 0), (2, 0), (4, 0), (8, 0));
                for (int i = 0; i < meta.NumExtraChannels; i++)
                    br.ReadU32((1, 0), (2, 0), (4, 0), (8, 0)); // ec_upsampling
            }

            if (encoding == JxlFrameEncoding.Modular)
                groupSizeShift = (int)br.ReadBits(2);

            if (meta.XybEncoded && encoding == JxlFrameEncoding.VarDct)
            {
                br.ReadBits(3); // x_qm_scale
                br.ReadBits(3); // b_qm_scale
            }

            numPasses = ReadPasses(ref br);

            bool haveCrop = br.ReadBit();
            if (haveCrop)
                throw new NotSupportedException("JPEG XL cropped frames are not yet supported.");

            // frame_type is normal (RegularFrame / SkipProgressive) here.
            JxlBlendMode blendMode = ReadBlendingInfo(ref br, meta.NumExtraChannels > 0, colorMode: null);
            for (int i = 0; i < meta.NumExtraChannels; i++)
                ReadBlendingInfo(ref br, meta.NumExtraChannels > 0, colorMode: blendMode);

            // No animation (extra_fields is unsupported upstream), so no duration/timecode.
            bool isLast = br.ReadBit();
            if (!isLast)
                br.ReadBits(2); // save_as_reference

            // resets_canvas: no crop => full image, so == (blendMode is Replace).
            bool resetsCanvas = blendMode == JxlBlendMode.Replace;
            if (resetsCanvas && !isLast)
                br.ReadBit(); // save_before_ct (duration == 0 here)

            ReadName(ref br);
            ReadRestorationFilter(ref br, encoding);
            JxlExtensions.Skip(ref br);
        }

        int groupDim = 128 << groupSizeShift;
        int lfGroupDim = groupDim * 8;
        int colorW = DivCeil(width, upsampling);
        int colorH = DivCeil(height, upsampling);

        return new JxlFrameHeader
        {
            Encoding = encoding,
            GroupDim = groupDim,
            NumGroups = DivCeil(colorW, groupDim) * DivCeil(colorH, groupDim),
            NumLfGroups = DivCeil(colorW, lfGroupDim) * DivCeil(colorH, lfGroupDim),
            NumPasses = numPasses,
        };
    }

    private static int ReadPasses(ref JxlBitReader br)
    {
        int numPasses = (int)br.ReadU32((1, 0), (2, 0), (3, 0), (4, 3));
        if (numPasses != 1)
        {
            int numDs = (int)br.ReadU32((0, 0), (1, 0), (2, 0), (3, 1));
            for (int i = 0; i < numPasses - 1; i++) br.ReadBits(2);                  // shift
            for (int i = 0; i < numDs; i++) br.ReadU32((1, 0), (2, 0), (4, 0), (8, 0)); // downsample
            for (int i = 0; i < numDs; i++) br.ReadU32((0, 0), (1, 0), (2, 0), (0, 3)); // last_pass
        }
        return numPasses;
    }

    private static JxlBlendMode ReadBlendingInfo(ref JxlBitReader br, bool extraChannelsPresent, JxlBlendMode? colorMode)
    {
        var mode = (JxlBlendMode)br.ReadU32((0, 0), (1, 0), (2, 0), (3, 2));
        bool usesAlpha = mode is JxlBlendMode.Blend or JxlBlendMode.MulAdd;

        if (extraChannelsPresent && usesAlpha)
            br.ReadU32((0, 0), (1, 0), (2, 0), (3, 3)); // alpha_channel
        if ((extraChannelsPresent && usesAlpha) || mode == JxlBlendMode.Mul)
            br.ReadBit();                               // clamp

        // resets_canvas with no crop => full image.
        bool resets = (colorMode ?? mode) == JxlBlendMode.Replace;
        if (!resets)
            br.ReadBits(2);                             // source

        return mode;
    }

    private static void ReadName(ref JxlBitReader br)
    {
        uint length = br.ReadU32((0, 0), (0, 4), (16, 5), (48, 10));
        for (uint i = 0; i < length; i++)
            br.ReadBits(8);
    }

    private static void ReadRestorationFilter(ref JxlBitReader br, JxlFrameEncoding encoding)
    {
        bool allDefault = br.ReadBit();
        if (allDefault)
            return;

        ReadGabor(ref br);
        ReadEdgePreservingFilter(ref br, encoding);
        JxlExtensions.Skip(ref br);
    }

    private static void ReadGabor(ref JxlBitReader br)
    {
        if (!br.ReadBit()) return; // gab_enabled
        if (!br.ReadBit()) return; // custom
        for (int i = 0; i < 6; i++) br.ReadBits(16); // 3 channels x 2 f16 weights
    }

    private static void ReadEdgePreservingFilter(ref JxlBitReader br, JxlFrameEncoding encoding)
    {
        uint iters = br.ReadBits(2);
        if (iters == 0)
            return;

        bool sharpCustom = encoding == JxlFrameEncoding.VarDct && br.ReadBit();
        if (sharpCustom)
            for (int i = 0; i < 8; i++) br.ReadBits(16); // sharp_lut f16

        bool weightCustom = br.ReadBit();
        if (weightCustom)
        {
            for (int i = 0; i < 3; i++) br.ReadBits(16); // channel_scale f16
            br.ReadBits(32);                              // ignored
        }

        bool sigmaCustom = br.ReadBit();
        if (sigmaCustom)
        {
            if (encoding == JxlFrameEncoding.VarDct) br.ReadBits(16); // quant_mul
            br.ReadBits(16);                                          // pass0_sigma_scale
            br.ReadBits(16);                                          // pass2_sigma_scale
            br.ReadBits(16);                                          // border_sad_mul
        }

        if (encoding == JxlFrameEncoding.Modular)
            br.ReadBits(16); // sigma_for_modular
    }

    private static int DivCeil(int a, int b) => (a + b - 1) / b;
}
