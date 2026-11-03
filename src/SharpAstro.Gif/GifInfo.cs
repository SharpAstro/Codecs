namespace SharpAstro.Gif;

/// <summary>
/// Everything a frame's headers say, read without decoding its pixels. Delay is the Graphic Control
/// Extension's raw value in 1/100 s: no "0 means 100 ms" browser policy is applied here, that is the
/// caller's.
/// </summary>
/// <param name="X">Left edge of the frame on the logical screen.</param>
/// <param name="Y">Top edge of the frame on the logical screen.</param>
/// <param name="Width">Frame width in pixels (at least 1, inside the logical screen).</param>
/// <param name="Height">Frame height in pixels (at least 1, inside the logical screen).</param>
/// <param name="DelayCentiseconds">GCE delay in 1/100 s, 0 when the frame has no GCE.</param>
/// <param name="DisposalMethod">GCE disposal method, 0-7 as written (0 none, 1 leave, 2 background, 3 previous).</param>
/// <param name="TransparentIndex">The GCE transparent colour index, or -1 when the frame has none.</param>
/// <param name="Interlaced">True when the rows are stored in the four-pass interlaced order.</param>
/// <param name="HasLocalColorTable">True when the frame carries its own palette.</param>
/// <param name="ColorTableSize">Entries in the palette the frame is drawn with (local, else global, else 0).</param>
/// <param name="MinCodeSize">The LZW minimum code size byte (2-8).</param>
/// <param name="IsTruncated">True when the file ended inside this frame's data sub-blocks.</param>
public readonly record struct GifFrameInfo(
    int X,
    int Y,
    int Width,
    int Height,
    int DelayCentiseconds,
    int DisposalMethod,
    int TransparentIndex,
    bool Interlaced,
    bool HasLocalColorTable,
    int ColorTableSize,
    int MinCodeSize,
    bool IsTruncated);

/// <summary>Dimensions and structure read from a GIF's headers and block structure.</summary>
/// <param name="Width">Logical screen width.</param>
/// <param name="Height">Logical screen height.</param>
/// <param name="BackgroundColorIndex">The logical screen descriptor's background colour index (meaningful only with a global table).</param>
/// <param name="PixelAspectRatio">The raw pixel aspect ratio byte; 0 means "not given", otherwise (n + 15) / 64.</param>
/// <param name="HasGlobalColorTable">True when the logical screen descriptor announces a global palette.</param>
/// <param name="GlobalColorTableSize">Entries in the global palette, 0 when there is none.</param>
/// <param name="LoopCount">The NETSCAPE2.0 / ANIMEXTS1.0 loop count (0 = forever), or null when the file has no such extension.</param>
/// <param name="Frames">Every image the file holds, in order.</param>
/// <param name="HasTrailer">True when the 0x3B trailer was reached; false for a file that ended early or hit an unrecognised block.</param>
/// <param name="UnparsedByteCount">Bytes after the last block that was understood: whatever follows the trailer, or the rest of the file from an unrecognised block introducer.</param>
public sealed record GifInfo(
    int Width,
    int Height,
    int BackgroundColorIndex,
    int PixelAspectRatio,
    bool HasGlobalColorTable,
    int GlobalColorTableSize,
    int? LoopCount,
    IReadOnlyList<GifFrameInfo> Frames,
    bool HasTrailer,
    int UnparsedByteCount)
{
    /// <summary>Number of images in the file.</summary>
    public int FrameCount => Frames.Count;
}

internal static class GifLimits
{
    /// <summary>
    /// Largest logical screen the decoder will size a raster for, 2^26 pixels (256 MiB of RGBA), the
    /// same ceiling WebP puts on a canvas. The screen is declared by 4 header bytes, so without this a
    /// 13-byte file could ask for 16 GiB. Checked while parsing the header, before anything is allocated.
    /// </summary>
    public const long MaximumScreenPixels = 1L << 26;
}
