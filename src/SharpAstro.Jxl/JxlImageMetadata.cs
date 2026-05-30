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

    public static JxlImageMetadata Read(ref JxlBitReader br)
    {
        bool allDefault = br.ReadBit();
        if (allDefault)
        {
            return new JxlImageMetadata
            {
                BitDepth = JxlBitDepth.Default,
                XybEncoded = true,
                NumExtraChannels = 0,
                HasAlpha = false,
                ColorSpace = JxlColorSpace.Rgb,
            };
        }

        bool extraFields = br.ReadBit();
        if (extraFields)
        {
            // orientation / intrinsic size / preview / animation. No Rung 1 oracle input sets
            // this; implement the skip when first exercised rather than misalign silently.
            throw new NotSupportedException(
                "JPEG XL extended image fields (orientation/preview/animation) are not yet supported.");
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

        ulong extensions = br.ReadU64();
        if (extensions != 0)
            throw new NotSupportedException("JPEG XL ImageMetadata extensions are not yet supported.");

        return new JxlImageMetadata
        {
            BitDepth = bitDepth,
            XybEncoded = xybEncoded,
            NumExtraChannels = (int)numExtraChannels,
            HasAlpha = hasAlpha,
            ColorSpace = color.ColorSpace,
        };
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
