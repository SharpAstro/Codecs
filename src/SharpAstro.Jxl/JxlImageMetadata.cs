namespace SharpAstro.Jxl;

/// <summary>JPEG XL extra-channel type (ISO/IEC 18181-1 §D.2).</summary>
internal enum JxlExtraChannelType : uint
{
    Alpha = 0,
    Depth = 1,
    SpotColor = 2,
    SelectionMask = 3,
    Black = 4,
    Cfa = 5,
    Thermal = 6,
    Unknown = 15,
    Optional = 16,
}

/// <summary>
/// JPEG XL ImageMetadata (ISO/IEC 18181-1 §D.2) — the codestream header that follows the
/// SizeHeader: bit depth, extra channels (alpha), the XYB-encoded flag, and colour encoding.
/// </summary>
internal readonly struct JxlImageMetadata
{
    public JxlBitDepth BitDepth { get; init; }
    public bool XybEncoded { get; init; }
    public int NumExtraChannels { get; init; }
    public bool HasAlpha { get; init; }
    public JxlColorSpace ColorSpace { get; init; }

    /// <summary>EXIF-style orientation 1 to 8 (1 = as stored), from <c>extra_fields</c>; 1 without them.</summary>
    public int Orientation { get; init; }

    /// <summary>The intrinsic display size, when <c>extra_fields</c> carries one; null otherwise.</summary>
    public (int Width, int Height)? IntrinsicSize { get; init; }

    /// <summary>
    /// <c>tone_mapping.intensity_target</c>: the nits that sample value 1.0 is meant to reach (255 by default,
    /// 10000 for a PQ image from cjxl).
    /// </summary>
    public float IntensityTarget { get; init; }

    /// <summary>True when a preview frame precedes the main frame in the codestream.</summary>
    public bool HasPreview { get; init; }

    public static JxlImageMetadata Read(ref JxlBitReader br)
    {
        bool allDefault = br.ReadBit();
        if (allDefault)
        {
            // default_m is read unconditionally (it is NOT gated by all_default — see jxl-image
            // ImageMetadata); when false it is followed by the opsin inverse matrix / upsampling
            // weights, which we don't support. Omitting this read desyncs everything after it.
            if (!br.ReadBit()) // default_m
                throw new NotSupportedException(
                    "JPEG XL custom opsin matrix / upsampling weights (default_m = false) are not yet supported.");
            return new JxlImageMetadata
            {
                BitDepth = JxlBitDepth.Default,
                XybEncoded = true,
                NumExtraChannels = 0,
                HasAlpha = false,
                ColorSpace = JxlColorSpace.Rgb,
                Orientation = 1,
                IntensityTarget = DefaultIntensityTarget,
            };
        }

        // extra_fields (§D.2): orientation, an intrinsic size, a preview and an animation header before the
        // bit depth, and the tone mapping after the colour encoding. cjxl sets it for an HDR image (its
        // intensity target), so a PQ file cannot be read without it.
        bool extraFields = br.ReadBit();
        int orientation = 1;
        (int Width, int Height)? intrinsicSize = null;
        bool hasPreview = false;
        if (extraFields)
        {
            orientation = 1 + (int)br.ReadBits(3);
            if (br.ReadBit()) // have_intr_size
                intrinsicSize = JxlSizeHeader.Read(ref br);
            hasPreview = br.ReadBit();
            if (hasPreview)
                SkipPreviewHeader(ref br);
            if (br.ReadBit()) // have_animation
            {
                // An animation also changes every FrameHeader (durations, timecodes), which the frame
                // decoder does not read, so refuse here with the reason rather than misread a frame.
                throw new NotSupportedException("Animated JPEG XL is not supported.");
            }
        }

        JxlBitDepth bitDepth = JxlBitDepth.Read(ref br);
        _ = br.ReadBit(); // modular_16bit_buffers

        uint numExtraChannels = br.ReadU32((0, 0), (1, 0), (2, 4), (1, 12));
        bool hasAlpha = false;
        for (uint i = 0; i < numExtraChannels; i++)
        {
            if (ReadExtraChannelInfo(ref br) == JxlExtraChannelType.Alpha)
                hasAlpha = true;
        }

        bool xybEncoded = br.ReadBit();
        JxlColorEncoding color = JxlColorEncoding.Read(ref br);

        float intensityTarget = DefaultIntensityTarget;
        if (extraFields)
            intensityTarget = ReadToneMapping(ref br);

        JxlExtensions.Skip(ref br);

        // default_m is read unconditionally; when false it is followed by the opsin inverse
        // matrix (if xyb) and custom upsampling-weight tables — not yet exercised by any oracle
        // input, so unsupported for now. Omitting this bit was a latent Rung-1 misalignment.
        bool defaultModular = br.ReadBit();
        if (!defaultModular)
        {
            throw new NotSupportedException(
                "JPEG XL custom opsin matrix / upsampling weights (default_m = false) are not yet supported.");
        }

        return new JxlImageMetadata
        {
            BitDepth = bitDepth,
            XybEncoded = xybEncoded,
            NumExtraChannels = (int)numExtraChannels,
            HasAlpha = hasAlpha,
            ColorSpace = color.ColorSpace,
            Orientation = orientation,
            IntrinsicSize = intrinsicSize,
            IntensityTarget = intensityTarget,
            HasPreview = hasPreview,
        };
    }

    private const float DefaultIntensityTarget = 255f;

    /// <summary>
    /// ToneMapping (§D.2): all_default, else intensity_target, min_nits, relative_to_max_display and
    /// linear_below. Only the intensity target is kept; the rest are display hints.
    /// </summary>
    private static float ReadToneMapping(ref JxlBitReader br)
    {
        if (br.ReadBit()) // all_default
            return DefaultIntensityTarget;
        float intensityTarget = br.ReadF16();
        _ = br.ReadF16(); // min_nits
        _ = br.ReadBit(); // relative_to_max_display
        _ = br.ReadF16(); // linear_below
        if (!(intensityTarget > 0))
            throw new InvalidDataException("JPEG XL tone_mapping.intensity_target must be positive.");
        return intensityTarget;
    }

    /// <summary>
    /// PreviewHeader (§D.2): the preview frame's size, read past. The preview frame itself comes before the
    /// main frame in the codestream.
    /// </summary>
    private static void SkipPreviewHeader(ref JxlBitReader br)
    {
        bool div8 = br.ReadBit();
        ReadPreviewDimension(ref br, div8); // ysize
        if (br.ReadBits(3) == 0)             // ratio
            ReadPreviewDimension(ref br, div8); // xsize

        static void ReadPreviewDimension(ref JxlBitReader br, bool div8)
        {
            if (div8)
                _ = br.ReadU32((16, 0), (32, 0), (1, 5), (33, 9));
            else
                _ = br.ReadU32((1, 6), (65, 8), (321, 10), (1345, 12));
        }
    }

    private static JxlExtraChannelType ReadExtraChannelInfo(ref JxlBitReader br)
    {
        if (br.ReadBit()) // all_default -> an alpha channel
            return JxlExtraChannelType.Alpha;

        var type = (JxlExtraChannelType)br.ReadEnum();
        _ = JxlBitDepth.Read(ref br);                        // bit_depth
        _ = br.ReadU32((0, 0), (3, 0), (4, 0), (1, 3));      // dim_shift
        uint nameLength = br.ReadU32((0, 0), (0, 4), (16, 5), (48, 10)); // name length (bytes)
        for (uint i = 0; i < nameLength; i++)
            _ = br.ReadBits(8);

        if (type == JxlExtraChannelType.Alpha)
            _ = br.ReadBit(); // alpha_associated
        else if (type == JxlExtraChannelType.SpotColor || type == JxlExtraChannelType.Cfa)
            throw new NotSupportedException($"JPEG XL extra channel type {type} is not yet supported.");

        return type;
    }
}
