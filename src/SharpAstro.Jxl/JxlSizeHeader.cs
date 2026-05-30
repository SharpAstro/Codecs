namespace SharpAstro.Jxl;

/// <summary>
/// The JPEG XL codestream SizeHeader (ISO/IEC 18181-1 §C.2). A single shared <c>div8</c>
/// flag selects the 8-aligned shortcut for both dimensions; the height is always coded,
/// while the width is either coded explicitly (ratio 0) or derived from one of seven fixed
/// width:height aspect ratios.
/// </summary>
internal static class JxlSizeHeader
{
    // Width:height ratios for the 3-bit `ratio` field. Index 0 = explicit width (unused here).
    private static readonly (int Num, int Den)[] AspectRatios =
    [
        (0, 0),   // 0: explicit
        (1, 1),   // 1
        (12, 10), // 2
        (4, 3),   // 3
        (3, 2),   // 4
        (16, 9),  // 5
        (5, 4),   // 6
        (2, 1),   // 7
    ];

    public static (int Width, int Height) Read(ref JxlBitReader br)
    {
        bool div8 = br.ReadBit();
        int height = ReadDimension(ref br, div8);

        uint ratio = br.ReadBits(3);
        int width = ratio == 0
            ? ReadDimension(ref br, div8)
            : (int)((long)height * AspectRatios[ratio].Num / AspectRatios[ratio].Den);

        return (width, height);
    }

    private static int ReadDimension(ref JxlBitReader br, bool div8) =>
        div8
            ? 8 * (1 + (int)br.ReadBits(5))
            : 1 + (int)br.ReadU32((0, 9), (0, 13), (0, 18), (0, 30));
}
