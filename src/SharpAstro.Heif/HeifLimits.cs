namespace SharpAstro.Heif;

/// <summary>
/// Ceilings on what a HEIF file may DECLARE, checked when an image is described, before anything is
/// sized from it. Here from the first rung, as for <c>SharpAstro.Jpeg2000</c>, rather than added after
/// shipping as JBIG2's were.
/// <para>
/// The container itself needs no ceiling of its own: every item, extent, reference and property the
/// reader records costs at least a few bytes of input, so what the reader holds is proportional to the
/// file. The amplification is in what the boxes declare: an <c>ispe</c> or a grid's output size is a pair
/// of 32-bit numbers, eight bytes that can name 2^64 pixels. That is what is bounded here.
/// </para>
/// </summary>
public static class HeifLimits
{
    /// <summary>
    /// The most pixels an image (a grid's output, or one coded image) may declare: 2^28, the ceiling the
    /// JBIG2 and JPEG 2000 decoders here also use. A 1200 dpi A4 scan is about 139 Mpixels and the largest
    /// current camera stills about 100 Mpixels (the R7's 6960x4640 is 32 Mpixels), so this admits every
    /// real still with room to spare and keeps a 4:4:4 16-bit plane set well inside what an array can hold.
    /// </summary>
    public const long MaxImagePixels = 1L << 28;

    /// <summary>
    /// The largest width or height a coded HEVC picture may declare: 16888, the most any level allows.
    /// H.265 A.4.1 bounds each side by Sqrt(MaxLumaPs * 8), and level 6.x's MaxLumaPs of 35,651,584 gives
    /// 16888. HEIF splits anything bigger into a grid.
    /// </summary>
    public const int MaxCodedDimension = 16888;
}
