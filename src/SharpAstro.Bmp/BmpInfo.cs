using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Bmp;

/// <summary>Which DIB header a BMP file carries, by its size field.</summary>
public enum BmpHeaderKind
{
    /// <summary>12-byte <c>BITMAPCOREHEADER</c> (OS/2 1.x, also written by ImageMagick's BMP2): 3-byte palette entries, no compression.</summary>
    Os2Core,

    /// <summary>40-byte <c>BITMAPINFOHEADER</c>, or the 52/56-byte Adobe variants that add colour masks to it.</summary>
    Info,

    /// <summary>16- or 64-byte OS/2 2.x header.</summary>
    Os2Version2,

    /// <summary>108-byte <c>BITMAPV4HEADER</c>: colour masks and colour-space endpoints.</summary>
    V4,

    /// <summary>124-byte <c>BITMAPV5HEADER</c>: V4 plus rendering intent and an embedded or linked ICC profile.</summary>
    V5,
}

/// <summary>The pixel encodings this decoder reads (the DIB <c>biCompression</c> field).</summary>
public enum BmpCompression
{
    /// <summary><c>BI_RGB</c>: uncompressed, indexed or true colour with the default layout.</summary>
    Rgb = 0,

    /// <summary><c>BI_RLE8</c>: 8-bit run-length encoding.</summary>
    Rle8 = 1,

    /// <summary><c>BI_RLE4</c>: 4-bit run-length encoding.</summary>
    Rle4 = 2,

    /// <summary><c>BI_BITFIELDS</c>: 16- or 32-bit pixels whose channels are cut out by colour masks.</summary>
    Bitfields = 3,

    /// <summary><c>BI_ALPHABITFIELDS</c> (Windows CE): <see cref="Bitfields"/> with a fourth, alpha, mask.</summary>
    AlphaBitfields = 6,
}

/// <summary>Dimensions and encoding properties read from a BMP file's headers, without decoding pixels.</summary>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height (always positive; see <paramref name="TopDown"/> for the row order).</param>
/// <param name="Channels">3 for RGB, 4 for RGBA: the layout <see cref="BmpDecoder.Decode"/> produces.</param>
/// <param name="HeaderKind">The DIB header the file carries.</param>
/// <param name="BitsPerPixel">1, 4, 8, 16, 24 or 32.</param>
/// <param name="Compression">How the pixel data is encoded.</param>
/// <param name="TopDown">True when the file stores its first row at the top (a negative height in the header).</param>
/// <param name="IccProfile">The embedded ICC profile of a V5 file whose colour space is <c>PROFILE_EMBEDDED</c>; otherwise null.</param>
public sealed record BmpInfo(
    int Width,
    int Height,
    int Channels,
    BmpHeaderKind HeaderKind,
    int BitsPerPixel,
    BmpCompression Compression,
    bool TopDown,
    byte[]? IccProfile)
{
    /// <summary>The dimension/layout summary the codec facade works in: 8-bit samples, <see cref="Channels"/> per pixel.</summary>
    public ImageInfo ToImageInfo() => new(Width, Height, Channels, SampleFormat.UInt8);
}

/// <summary>A decoded BMP: tightly packed, row-major, top row first, 8-bit, non-premultiplied RGB or RGBA.</summary>
public sealed class BmpImage
{
    internal BmpImage(BmpInfo info, byte[] pixels)
    {
        Info = info;
        Pixels = pixels;
    }

    /// <summary>The header information the pixels were decoded under.</summary>
    public BmpInfo Info { get; }

    /// <summary>Pixel width.</summary>
    public int Width => Info.Width;

    /// <summary>Pixel height.</summary>
    public int Height => Info.Height;

    /// <summary>3 (R, G, B) or 4 (R, G, B, A) bytes per pixel.</summary>
    public int Channels => Info.Channels;

    /// <summary><c>Width * Height * Channels</c> bytes, always top row first whatever the file's own row order.</summary>
    public byte[] Pixels { get; }

    /// <summary>The embedded ICC profile, or null.</summary>
    public byte[]? IccProfile => Info.IccProfile;

    /// <summary>Wraps the pixels for the codec facade. The raster takes ownership of <see cref="Pixels"/>.</summary>
    public RasterImage ToRasterImage() =>
        new(Width, Height, Channels, SampleFormat.UInt8, Pixels, Info.IccProfile);
}

internal static class BmpLimits
{
    /// <summary>
    /// The most pixels a header may declare, 2^28: a 16384x16384 image, and the output buffer stays
    /// below 2^30 bytes so every size computation fits an <see cref="int"/>.
    /// </summary>
    public const long MaximumPixels = 1L << 28;
}
