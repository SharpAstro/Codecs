namespace SharpAstro.Exr;

/// <summary>
/// Per-channel sample type (OpenEXR <c>PixelType</c>). The on-disk enum values are
/// fixed by the format and written verbatim into the channel list.
/// </summary>
public enum ExrPixelType
{
    /// <summary>32-bit unsigned integer.</summary>
    Uint = 0,
    /// <summary>16-bit IEEE half-float (<see cref="System.Half"/>).</summary>
    Half = 1,
    /// <summary>32-bit IEEE single-precision float.</summary>
    Float = 2,
}

/// <summary>
/// Pixel-data compression scheme (OpenEXR <c>Compression</c>). Values are the
/// fixed on-disk codes stored in the <c>compression</c> header attribute.
/// </summary>
public enum ExrCompression : byte
{
    /// <summary>No compression — raw little-endian samples.</summary>
    None = 0,
    /// <summary>Run-length encoding (lossless), one scanline per block.</summary>
    Rle = 1,
    /// <summary>zlib deflate (lossless), one scanline per block.</summary>
    Zips = 2,
    /// <summary>zlib deflate (lossless), 16 scanlines per block.</summary>
    Zip = 3,
    /// <summary>Wavelet + Huffman (lossless), 32 scanlines per block — OpenEXR's default.</summary>
    Piz = 4,
    /// <summary>Lossy 24-bit float (not yet implemented).</summary>
    Pxr24 = 5,
    /// <summary>Lossy 4×4 block (not yet implemented).</summary>
    B44 = 6,
    /// <summary>Lossy 4×4 block, flat-region optimized (not yet implemented).</summary>
    B44a = 7,
    /// <summary>Lossy DCT, 32 scanlines per block (not yet implemented).</summary>
    Dwaa = 8,
    /// <summary>Lossy DCT, 256 scanlines per block (not yet implemented).</summary>
    Dwab = 9,
}

/// <summary>Scanline storage order (OpenEXR <c>LineOrder</c>).</summary>
public enum ExrLineOrder
{
    /// <summary>Scanlines stored top-to-bottom (y increasing).</summary>
    IncreasingY = 0,
    /// <summary>Scanlines stored bottom-to-top (y decreasing).</summary>
    DecreasingY = 1,
    /// <summary>Scanlines stored in arbitrary order (tiled/deep only).</summary>
    RandomY = 2,
}

/// <summary>Fixed format constants and per-compression geometry.</summary>
public static class ExrFormat
{
    /// <summary>Magic number — the little-endian int <c>20000630</c> (bytes 76 2F 31 01).</summary>
    public const int MagicNumber = 20000630;

    /// <summary>Version number occupying the low byte of the 4-byte version field.</summary>
    public const int Version = 2;

    // Version-field flag bits (high 24 bits of the version field).
    public const int TiledFlag = 0x200;        // bit 9
    public const int LongNamesFlag = 0x400;     // bit 10
    public const int NonImageFlag = 0x800;      // bit 11 (deep data)
    public const int MultiPartFlag = 0x1000;    // bit 12

    /// <summary>Number of scanlines coded together in one compression block.</summary>
    public static int ScanLinesPerBlock(ExrCompression c) => c switch
    {
        ExrCompression.None => 1,
        ExrCompression.Rle => 1,
        ExrCompression.Zips => 1,
        ExrCompression.Zip => 16,
        ExrCompression.Piz => 32,
        ExrCompression.Pxr24 => 16,
        ExrCompression.B44 => 32,
        ExrCompression.B44a => 32,
        ExrCompression.Dwaa => 32,
        ExrCompression.Dwab => 256,
        _ => 1,
    };

    /// <summary>Bytes per sample for a given pixel type.</summary>
    public static int BytesPerSample(ExrPixelType t) => t == ExrPixelType.Half ? 2 : 4;
}
