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
/// <para>
/// What a decode may still allocate, measured: before reading a slice byte, the decoder allocates the output
/// canvas (6 bytes a pixel) and each picture's planes (about 3.4 bytes a pixel in 4:2:0, 6.4 in 4:4:4), so a
/// file of a few KB that declares sizes at these limits gets memory in proportion to what it declares, not to
/// its size; this is the floor for any decoder that accepts images this large. What is checked first, from
/// the parameter sets alone: each coded picture holds the size the file declares for it, and is at most
/// twice the area of the output it covers (plus one 64x64 CTB), and a grid keeps at most 1 GiB of decoded
/// tile pictures alive at once (at least one), however many cores decode it. So the ceiling is the canvas
/// plus about one picture: a 36 KB 1x2 grid whose tiles' SPS declare 16384x16384 4:2:2 and whose output is
/// 32768x8192 peaks at 2.6 GiB before its slice data runs out (it was the canvas plus one picture per core);
/// a 4:4:4 grid at the limit, about 3.2 GiB. A caller wanting less should check <c>HeifFile.Describe</c>'s
/// sizes before decoding.
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
