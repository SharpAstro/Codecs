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
    public ulong Flags { get; init; }
    public bool DoYcbcr { get; init; }
    public int ColorWidth { get; init; }
    public int ColorHeight { get; init; }

    /// <summary>Colour upsampling factor (1, 2, 4 or 8).</summary>
    public int Upsampling { get; init; }

    /// <summary>The <c>jpeg_upsampling</c> mode of each channel (all 0 unless <see cref="DoYcbcr"/>).</summary>
    public int[] JpegUpsampling { get; init; }

    /// <summary><c>x_qm_scale</c>: 3 by default for an XYB VarDCT frame, 2 otherwise.</summary>
    public int XQmScale { get; init; }

    /// <summary><c>b_qm_scale</c>: 2 by default.</summary>
    public int BQmScale { get; init; }

    /// <summary>The coefficient shift of each pass but the last (<c>passes.shift</c>).</summary>
    public int[] PassShifts { get; init; }

    /// <summary>The Gabor-like restoration filter's weights per channel, or null when disabled.</summary>
    public float[][]? GaborWeights { get; init; }

    /// <summary>The edge-preserving filter's parameters, or null when disabled.</summary>
    public JxlEpfParams? Epf { get; init; }

    public bool HasNoise => (Flags & 0x01) != 0;
    public bool HasPatches => (Flags & 0x02) != 0;
    public bool HasSplines => (Flags & 0x10) != 0;
    public bool UsesLfFrame => (Flags & FlagUseLfFrame) != 0;
    public bool SkipAdaptiveLfSmoothing => (Flags & 0x80) != 0;

    private const ulong FlagUseLfFrame = 0x20;

    public static JxlFrameHeader Read(ref JxlBitReader br, in JxlImageMetadata meta, int width, int height)
    {
        // A frame begins on a byte boundary: the codestream is zero-padded to the next byte
        // after the image header and before the FrameHeader (jxl-frame Frame::parse → zero_pad_to_byte).
        br.ZeroPadToByte();

        // A preview frame comes first and every reader here takes the first frame as the image, so a file
        // with one is refused rather than decoded as its preview.
        if (meta.HasPreview)
            throw new NotSupportedException("JPEG XL with a preview frame is not yet supported.");

        bool allDefault = br.ReadBit();

        var encoding = JxlFrameEncoding.VarDct;
        int groupSizeShift = 1; // VarDCT default => group dim 256
        int numPasses = 1;
        int[] passShifts = [];
        int upsampling = 1;
        ulong flags = 0;
        bool doYcbcr = false;
        var jpegUpsampling = new int[3];
        int xQmScale = meta.XybEncoded ? 3 : 2; // the VarDCT default (the frame is VarDCT unless it says otherwise)
        int bQmScale = 2;
        float[][]? gabor = DefaultGabor();
        JxlEpfParams? epf = JxlEpfParams.Default;

        if (!allDefault)
        {
            var frameType = (JxlFrameType)br.ReadBits(2);
            if (frameType is JxlFrameType.LfFrame or JxlFrameType.ReferenceOnly)
                throw new NotSupportedException($"JPEG XL {frameType} frames are not yet supported.");

            encoding = (JxlFrameEncoding)br.ReadBits(1);
            flags = br.ReadU64();

            doYcbcr = !meta.XybEncoded && br.ReadBit();
            bool useLfFrame = (flags & FlagUseLfFrame) != 0;

            if (doYcbcr && !useLfFrame)
                for (int i = 0; i < 3; i++) jpegUpsampling[i] = (int)br.ReadBits(2);

            if (!useLfFrame)
            {
                upsampling = (int)br.ReadU32((1, 0), (2, 0), (4, 0), (8, 0));
                for (int i = 0; i < meta.NumExtraChannels; i++)
                    br.ReadU32((1, 0), (2, 0), (4, 0), (8, 0)); // ec_upsampling
            }

            if (encoding == JxlFrameEncoding.Modular)
                groupSizeShift = (int)br.ReadBits(2);

            xQmScale = meta.XybEncoded && encoding == JxlFrameEncoding.VarDct ? 3 : 2;
            if (meta.XybEncoded && encoding == JxlFrameEncoding.VarDct)
            {
                xQmScale = (int)br.ReadBits(3);
                bQmScale = (int)br.ReadBits(3);
            }

            (numPasses, passShifts) = ReadPasses(ref br);

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
            (gabor, epf) = ReadRestorationFilter(ref br, encoding);
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
            Flags = flags,
            DoYcbcr = doYcbcr,
            ColorWidth = colorW,
            ColorHeight = colorH,
            Upsampling = upsampling,
            JpegUpsampling = jpegUpsampling,
            XQmScale = xQmScale,
            BQmScale = bQmScale,
            PassShifts = passShifts,
            GaborWeights = gabor,
            Epf = epf,
        };
    }

    private static (int NumPasses, int[] Shifts) ReadPasses(ref JxlBitReader br)
    {
        int numPasses = (int)br.ReadU32((1, 0), (2, 0), (3, 0), (4, 3));
        var shifts = new int[numPasses - 1];
        if (numPasses != 1)
        {
            int numDs = (int)br.ReadU32((0, 0), (1, 0), (2, 0), (3, 1));
            for (int i = 0; i < numPasses - 1; i++) shifts[i] = (int)br.ReadBits(2);  // shift
            for (int i = 0; i < numDs; i++) br.ReadU32((1, 0), (2, 0), (4, 0), (8, 0)); // downsample
            for (int i = 0; i < numDs; i++) br.ReadU32((0, 0), (1, 0), (2, 0), (0, 3)); // last_pass
        }
        return (numPasses, shifts);
    }

    /// <summary>The default Gabor-like weights (jxl-oxide <c>Gabor::default</c>), the same for every channel.</summary>
    private static float[][] DefaultGabor() =>
        [[0.115169525f, 0.061248592f], [0.115169525f, 0.061248592f], [0.115169525f, 0.061248592f]];

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

    /// <summary>
    /// RestorationFilter (§E.2, jxl-oxide <c>RestorationFilter</c> / <c>Gabor</c> / <c>EdgePreservingFilter</c> in
    /// jxl-frame filter.rs): the Gabor-like weights and the EPF parameters, defaulted when all_default.
    /// </summary>
    private static (float[][]? Gabor, JxlEpfParams? Epf) ReadRestorationFilter(ref JxlBitReader br, JxlFrameEncoding encoding)
    {
        if (br.ReadBit()) // all_default
            return (DefaultGabor(), JxlEpfParams.Default);

        float[][]? gabor = ReadGabor(ref br);
        JxlEpfParams? epf = ReadEdgePreservingFilter(ref br, encoding);
        JxlExtensions.Skip(ref br);
        return (gabor, epf);
    }

    private static float[][]? ReadGabor(ref JxlBitReader br)
    {
        if (!br.ReadBit()) return null;            // gab_enabled
        if (!br.ReadBit()) return DefaultGabor();  // custom
        var weights = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            weights[c] = [br.ReadF16(), br.ReadF16()];
            if (MathF.Abs(1f + (weights[c][0] + weights[c][1]) * 4f) < float.Epsilon)
                throw new InvalidDataException("JPEG XL Gabor weights give a near-zero kernel.");
        }
        return weights;
    }

    private static JxlEpfParams? ReadEdgePreservingFilter(ref JxlBitReader br, JxlFrameEncoding encoding)
    {
        int iters = (int)br.ReadBits(2);
        if (iters == 0)
            return null;

        float[] sharpLut = JxlEpfParams.DefaultSharpLut;
        if (encoding == JxlFrameEncoding.VarDct && br.ReadBit()) // sharp_custom
        {
            sharpLut = new float[8];
            for (int i = 0; i < 8; i++) sharpLut[i] = br.ReadF16();
        }

        float[] channelScale = JxlEpfParams.DefaultChannelScale;
        if (br.ReadBit()) // weight_custom
        {
            channelScale = [br.ReadF16(), br.ReadF16(), br.ReadF16()];
            br.ReadBits(32); // ignored
        }

        float quantMul = 0.46f, pass0 = 0.9f, pass2 = 6.5f, borderSadMul = 2f / 3f;
        if (br.ReadBit()) // sigma_custom
        {
            if (encoding == JxlFrameEncoding.VarDct) quantMul = br.ReadF16();
            pass0 = br.ReadF16();
            pass2 = br.ReadF16();
            borderSadMul = br.ReadF16();
        }

        float sigmaForModular = 1f;
        if (encoding == JxlFrameEncoding.Modular)
            sigmaForModular = br.ReadF16();

        return new JxlEpfParams
        {
            Iters = iters,
            SharpLut = sharpLut,
            ChannelScale = channelScale,
            QuantMul = quantMul,
            Pass0SigmaScale = pass0,
            Pass2SigmaScale = pass2,
            BorderSadMul = borderSadMul,
            SigmaForModular = sigmaForModular,
        };
    }

    private static int DivCeil(int a, int b) => (a + b - 1) / b;
}

/// <summary>Edge-preserving filter parameters (jxl-oxide <c>EpfParams</c> / <c>EpfSigma</c>, jxl-frame filter.rs).</summary>
internal sealed class JxlEpfParams
{
    public static readonly float[] DefaultSharpLut = [0f, 1f / 7, 2f / 7, 3f / 7, 4f / 7, 5f / 7, 6f / 7, 1f];
    public static readonly float[] DefaultChannelScale = [40f, 5f, 3.5f];

    /// <summary>The all_default parameters (jxl-oxide <c>EpfParams::default</c>: two iterations).</summary>
    public static JxlEpfParams Default => new()
    {
        Iters = 2,
        SharpLut = DefaultSharpLut,
        ChannelScale = DefaultChannelScale,
        QuantMul = 0.46f,
        Pass0SigmaScale = 0.9f,
        Pass2SigmaScale = 6.5f,
        BorderSadMul = 2f / 3f,
        SigmaForModular = 1f,
    };

    public required int Iters { get; init; }
    public required float[] SharpLut { get; init; }
    public required float[] ChannelScale { get; init; }
    public required float QuantMul { get; init; }
    public required float Pass0SigmaScale { get; init; }
    public required float Pass2SigmaScale { get; init; }
    public required float BorderSadMul { get; init; }
    public required float SigmaForModular { get; init; }
}
