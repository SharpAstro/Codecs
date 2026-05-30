namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL BitDepth (ISO/IEC 18181-1 §D.2) — integer or IEEE-float sample precision.
/// </summary>
internal readonly struct JxlBitDepth
{
    public bool FloatingPoint { get; init; }
    public int BitsPerSample { get; init; }
    public int ExponentBits { get; init; }

    public static JxlBitDepth Default => new() { FloatingPoint = false, BitsPerSample = 8, ExponentBits = 0 };

    public static JxlBitDepth Read(ref JxlBitReader br)
    {
        bool floatingPoint = br.ReadBit();
        if (!floatingPoint)
        {
            return new JxlBitDepth
            {
                FloatingPoint = false,
                BitsPerSample = (int)br.ReadU32((8, 0), (10, 0), (12, 0), (1, 6)),
                ExponentBits = 0,
            };
        }

        int bits = (int)br.ReadU32((32, 0), (16, 0), (24, 0), (1, 6));
        // TODO(jxl/float): confirm exponent_bits_per_sample encoding when the float path is
        // first exercised — Rung 1 oracle inputs are all integer (Magick Q16-HDRI).
        int exponentBits = 1 + (int)br.ReadBits(4);
        return new JxlBitDepth { FloatingPoint = true, BitsPerSample = bits, ExponentBits = exponentBits };
    }
}
