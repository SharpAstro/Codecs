namespace SharpAstro.Jxl;

/// <summary>JPEG XL colour space (ISO/IEC 18181-1 §D.3).</summary>
internal enum JxlColorSpace : uint
{
    Rgb = 0,
    Gray = 1,
    Xyb = 2,
    Unknown = 3,
}

/// <summary>JPEG XL white point (§D.3); the values are the CICP numbers where one exists.</summary>
internal enum JxlWhitePoint : uint
{
    D65 = 1,
    Custom = 2,
    E = 10,
    Dci = 11,
}

/// <summary>JPEG XL primaries (§D.3); the values are the CICP numbers where one exists.</summary>
internal enum JxlPrimaries : uint
{
    Srgb = 1,
    Custom = 2,
    Bt2100 = 9,
    P3 = 11,
}

/// <summary>JPEG XL transfer function (§D.3); the values are the CICP numbers. <see cref="Gamma"/> is ours.</summary>
internal enum JxlTransferFunction : uint
{
    Bt709 = 1,
    Unknown = 2,
    Linear = 8,
    Srgb = 13,
    Pq = 16,
    Dci = 17,
    Hlg = 18,
    /// <summary>Not a bitstream value: the encoding carries an explicit gamma (<see cref="JxlColorEncoding.Gamma"/>).</summary>
    Gamma = 0x10000,
}

/// <summary>
/// JPEG XL ColorEncoding (ISO/IEC 18181-1 §D.3, jxl-oxide <c>ColourEncoding</c> in jxl-image color.rs): the colour
/// space, white point, primaries, transfer function and rendering intent. Custom chromaticities are read (so the
/// fields after them stay aligned) but not kept, so a decoder that needs them refuses; an embedded ICC profile is
/// refused here, since its compressed bytes follow the image header and no reader takes them.
/// </summary>
internal readonly struct JxlColorEncoding
{
    public bool WantIcc { get; init; }
    public JxlColorSpace ColorSpace { get; init; }
    public JxlWhitePoint WhitePoint { get; init; }
    public JxlPrimaries Primaries { get; init; }
    public JxlTransferFunction Transfer { get; init; }

    /// <summary>The explicit gamma when <see cref="Transfer"/> is <see cref="JxlTransferFunction.Gamma"/> (0 to 1).</summary>
    public float Gamma { get; init; }

    public uint RenderingIntent { get; init; }

    /// <summary>The default: sRGB (D65, sRGB primaries, sRGB transfer, relative intent).</summary>
    public static JxlColorEncoding Srgb => new()
    {
        ColorSpace = JxlColorSpace.Rgb,
        WhitePoint = JxlWhitePoint.D65,
        Primaries = JxlPrimaries.Srgb,
        Transfer = JxlTransferFunction.Srgb,
        RenderingIntent = 1,
    };

    public static JxlColorEncoding Read(ref JxlBitReader br)
    {
        if (br.ReadBit()) // all_default -> sRGB
            return Srgb;

        bool wantIcc = br.ReadBit();
        var colorSpace = (JxlColorSpace)br.ReadEnum();
        if (wantIcc)
            throw new NotSupportedException("JPEG XL embedded ICC colour encoding is not yet supported.");

        var whitePoint = JxlWhitePoint.D65;
        var primaries = JxlPrimaries.Srgb;
        if (colorSpace != JxlColorSpace.Xyb)
        {
            whitePoint = (JxlWhitePoint)br.ReadEnum();
            if (whitePoint == JxlWhitePoint.Custom)
                ReadCustomXy(ref br);
            if (colorSpace != JxlColorSpace.Gray)
            {
                primaries = (JxlPrimaries)br.ReadEnum();
                if (primaries == JxlPrimaries.Custom)
                {
                    ReadCustomXy(ref br); // red
                    ReadCustomXy(ref br); // green
                    ReadCustomXy(ref br); // blue
                }
            }
        }

        JxlTransferFunction transfer;
        float gamma = 0;
        if (br.ReadBit()) // have_gamma
        {
            gamma = br.ReadBits(24) / 10_000_000f;
            transfer = JxlTransferFunction.Gamma;
        }
        else
        {
            transfer = (JxlTransferFunction)br.ReadEnum();
        }

        uint renderingIntent = br.ReadEnum();

        return new JxlColorEncoding
        {
            WantIcc = false,
            ColorSpace = colorSpace,
            WhitePoint = whitePoint,
            Primaries = primaries,
            Transfer = transfer,
            Gamma = gamma,
            RenderingIntent = renderingIntent,
        };
    }

    /// <summary>A Customxy (§D.3): two signed fixed-point coordinates, read past.</summary>
    private static void ReadCustomXy(ref JxlBitReader br)
    {
        for (int i = 0; i < 2; i++)
            br.ReadU32((0, 19), (524288, 19), (1048576, 20), (2097152, 21));
    }
}
