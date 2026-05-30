namespace SharpAstro.Jxl;

/// <summary>JPEG XL colour space (ISO/IEC 18181-1 §D.3).</summary>
internal enum JxlColorSpace : uint
{
    Rgb = 0,
    Gray = 1,
    Xyb = 2,
    Unknown = 3,
}

/// <summary>
/// JPEG XL ColorEncoding (ISO/IEC 18181-1 §D.3). Rung 1 parses the structural fields needed
/// to advance the bit position and expose the colour space. ICC blobs and custom
/// chromaticities are not yet handled (no Rung 1 oracle input uses them); they throw so a
/// future input that needs them fails loudly rather than silently misaligning.
/// </summary>
internal readonly struct JxlColorEncoding
{
    public bool WantIcc { get; init; }
    public JxlColorSpace ColorSpace { get; init; }

    public static JxlColorEncoding Read(ref JxlBitReader br)
    {
        if (br.ReadBit()) // all_default -> sRGB
            return new JxlColorEncoding { WantIcc = false, ColorSpace = JxlColorSpace.Rgb };

        bool wantIcc = br.ReadBit();
        var colorSpace = (JxlColorSpace)br.ReadEnum();
        if (wantIcc)
            throw new NotSupportedException("JPEG XL embedded ICC colour encoding is not yet supported.");

        if (colorSpace != JxlColorSpace.Xyb)
        {
            uint whitePoint = br.ReadEnum();
            if (whitePoint == 2) // Custom
                throw new NotSupportedException("JPEG XL custom white point is not yet supported.");

            if (colorSpace != JxlColorSpace.Gray)
            {
                uint primaries = br.ReadEnum();
                if (primaries == 2) // Custom
                    throw new NotSupportedException("JPEG XL custom primaries are not yet supported.");
            }
        }

        bool haveGamma = br.ReadBit();
        if (haveGamma)
            br.ReadBits(24);  // gamma (24-bit fixed point)
        else
            br.ReadEnum();    // transfer_function

        br.ReadEnum();        // rendering_intent

        return new JxlColorEncoding { WantIcc = false, ColorSpace = colorSpace };
    }
}
