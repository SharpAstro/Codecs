using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Tiff;

/// <summary>
/// TIFF's LZW variant (TIFF 6.0 section 13), the other compression a real-world TIFF is likely to use.
///
/// <para><b>It is not GIF's LZW and not raw DEFLATE.</b> Codes are packed <b>MSB-first</b> (GIF packs
/// LSB-first), and the code width grows on the <i>early change</i> schedule: it steps up one code
/// BEFORE the table is actually full -- to 10 bits when the next free code is 511, not 512, and
/// likewise at 1023 and 2047. Both quirks decode plausibly for the first few hundred codes and then
/// diverge, so an implementation that gets either wrong produces a picture that starts correct and
/// turns to noise partway down. libtiff and Photoshop both write early-change, which is what makes it
/// the compatible reading rather than merely one option.</para>
///
/// <para>The decoder itself is the shared <see cref="Lzw"/> in SharpAstro.Codecs.Abstractions, which GIF
/// reads through too; this is its TIFF preset (<see cref="LzwFormat.Tiff"/>: MSB-first, early change,
/// 8-bit literals). The table is a prefix chain, so adding an entry is O(1) and needs no allocation.</para>
///
/// <para>A predictor, if the file declares one, is applied by the reader AFTER this returns. Nothing
/// here knows about predictors, and LZW plus horizontal differencing is Photoshop's own default pair,
/// so the two must compose rather than one implying the other.</para>
/// </summary>
internal static class TiffLzw
{
    /// <summary>
    /// Decode one LZW-compressed strip into <paramref name="dst"/>, returning the number of bytes
    /// written. Stops at end-of-information, at the end of the input, or when the destination is full
    /// -- a strip that decodes to more than its share is truncated rather than throwing, matching how
    /// the uncompressed path clamps.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst) => Lzw.Decode(LzwFormat.Tiff, src, dst);
}
