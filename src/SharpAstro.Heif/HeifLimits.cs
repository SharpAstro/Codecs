namespace SharpAstro.Heif;

/// <summary>
/// Ceilings on what a HEIF file may DECLARE, checked when an image is described, before anything is
/// sized from it. Here from the first rung, as for <c>SharpAstro.Jpeg2000</c>, rather than added after
/// shipping as JBIG2's were.
/// <para>
/// The container itself needs no ceiling of its own: every item, extent, reference and property the
/// reader records costs at least a few bytes of input, so what the reader holds is proportional to the
/// file. (That had one hole, closed at the PR #69 review: iloc extent fields may be zero bytes wide, so the
/// reader now allows one such extent per item and checks every extent count against the bytes left; an
/// item's data larger than its file, from overlapping extents, is refused before it is copied.) The amplification is in what the boxes declare: an <c>ispe</c> or a grid's output size is a pair
/// of 32-bit numbers, eight bytes that can name 2^64 pixels. That is what is bounded here.
/// </para>
/// <para>
/// What a decode may still allocate, measured: before reading a slice byte, the decoder allocates the output
/// canvas (6 bytes a pixel) and each picture's planes (about 3.4 bytes a pixel in 4:2:0, 6.4 in 4:4:4), so a
/// file of a few KB that declares sizes at these limits gets memory in proportion to what it declares, not to
/// its size; this is the floor for any decoder that accepts images this large. What is checked first, from
/// the parameter sets alone: each coded picture holds the size the file declares for it; a single picture is
/// coded at most twice the area it shows and a grid's tiles together at most four times its output (each
/// plus one 64x64 CTB: a legal grid's last row and column overhang by anything short of a tile, which stays
/// under twice on an axis of two or more tiles); and a grid keeps at most 1 GiB of decoded
/// tile pictures alive at once (at least one), however many cores decode it. So the ceiling is the canvas
/// plus about one picture: a 36 KB 1x2 grid whose tiles' SPS declare 16384x16384 4:2:2 and whose output is
/// 32768x8192 peaks at 2.6 GiB before its slice data runs out (it was the canvas plus one picture per core);
/// a 4:4:4 grid at the limit, about 3.2 GiB. Those are before the loop filters: a picture that reaches SAO
/// also holds a copy of its luma plane while it is filtered (2 bytes a sample, about 0.5 GiB more for a
/// 16384x16384 picture), which the live-picture budget counts. A caller wanting less should check
/// <c>HeifFile.Describe</c>'s sizes before decoding.
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

    /// <summary>
    /// The most pixels the base image of a gain map may declare for <see cref="HeifGainMap"/> to reconstruct it: 2^26
    /// (67 Mpixels: the R7's 32 Mpixels, 50 and 48 Mpixel phones, not a 200 Mpixel one). The reconstruction holds the base's
    /// R'G'B' codes (6 bytes a pixel), then its linear floats (16) beside the reconstructed RGB (12) and the RGBA
    /// result (16), so a base at this limit peaks near 3 GiB and one at the image limit would be 4 times that and
    /// overflow the core's 32-bit size arithmetic. The gain map itself is held to <see cref="MaxImagePixels"/> and
    /// to the base's own area (a map larger than its image is not a map of it).
    /// </summary>
    public const long MaxGainMapBasePixels = 1L << 26;
}
