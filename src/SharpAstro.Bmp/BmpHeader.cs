using System.Buffers.Binary;

namespace SharpAstro.Bmp;

/// <summary>
/// Everything the headers say, validated against the data they sit in: offsets, palette size, colour
/// masks, and that the pixel data is as long as the dimensions need. Parsing is shared by
/// <see cref="BmpDecoder.ReadInfo"/> and <see cref="BmpDecoder.Decode"/> so both refuse the same files.
/// </summary>
internal sealed class BmpHeader
{
    private const int FileHeaderSize = 14;
    private const uint ProfileEmbedded = 0x4D424544; // 'MBED'

    public required BmpInfo Info { get; init; }
    public required int PixelOffset { get; init; }

    /// <summary>Palette entries actually present (0 for true-colour files).</summary>
    public required int PaletteCount { get; init; }
    public required int PaletteOffset { get; init; }

    /// <summary>3 for OS/2 core headers (BGR), 4 otherwise (BGRx).</summary>
    public required int PaletteEntrySize { get; init; }

    public required uint RedMask { get; init; }
    public required uint GreenMask { get; init; }
    public required uint BlueMask { get; init; }
    public required uint AlphaMask { get; init; }

    /// <summary>True when 16/32-bit pixels are cut apart by masks; false for the fixed BI_RGB layouts.</summary>
    public required bool UsesMasks { get; init; }

    /// <summary>A 32-bit BI_RGB file whose fourth byte is alpha (see <see cref="Parse"/>).</summary>
    public required bool Rgb32HasAlpha { get; init; }

    public int Width => Info.Width;
    public int Height => Info.Height;
    public int BitsPerPixel => Info.BitsPerPixel;

    /// <summary>Bytes per stored row: each row is padded to a multiple of four bytes.</summary>
    public long Stride => ((long)Width * BitsPerPixel + 31) / 32 * 4;

    public static BmpHeader Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < FileHeaderSize + 4 || d[0] != (byte)'B' || d[1] != (byte)'M')
            throw Invalid("not a BMP: the 'BM' signature is missing.");

        uint offBits = U32(d, 10);
        uint hsz = U32(d, 14);

        BmpHeaderKind kind;
        switch (hsz)
        {
            case 12: kind = BmpHeaderKind.Os2Core; break;
            case 16:
            case 64: kind = BmpHeaderKind.Os2Version2; break;
            case 40:
            case 52:
            case 56: kind = BmpHeaderKind.Info; break;
            case 108: kind = BmpHeaderKind.V4; break;
            case 124: kind = BmpHeaderKind.V5; break;
            default: throw Invalid($"unsupported DIB header size {hsz}.");
        }

        if (d.Length - FileHeaderSize < hsz)
            throw Invalid("truncated: the file ends inside the DIB header.");

        int dib = FileHeaderSize;
        int header = (int)hsz;
        int width, rawHeight, bpp;
        uint compressionField = 0, clrUsed = 0;

        if (kind == BmpHeaderKind.Os2Core)
        {
            width = U16(d, dib + 4);
            rawHeight = U16(d, dib + 6);
            bpp = U16(d, dib + 10);
        }
        else
        {
            width = I32(d, dib + 4);
            rawHeight = I32(d, dib + 8);
            bpp = U16(d, dib + 14);
            if (header >= 20) compressionField = U32(d, dib + 16);
            if (header >= 36) clrUsed = U32(d, dib + 32);
        }

        var compression = ReadCompression(kind, header, compressionField, bpp);

        if (width <= 0)
            throw Invalid($"invalid width {width}.");
        if (rawHeight == 0 || rawHeight == int.MinValue)
            throw Invalid($"invalid height {rawHeight}.");
        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);

        bool validDepth = compression switch
        {
            BmpCompression.Rle8 => bpp == 8,
            BmpCompression.Rle4 => bpp == 4,
            BmpCompression.Bitfields or BmpCompression.AlphaBitfields => bpp is 16 or 32,
            _ => kind == BmpHeaderKind.Os2Core ? bpp is 1 or 4 or 8 or 24 : bpp is 1 or 4 or 8 or 16 or 24 or 32,
        };
        if (!validDepth)
            throw Invalid($"{bpp} bits per pixel is not valid with {compression}.");

        // The declared size gates every allocation, before any data is looked at.
        if ((long)width * height > BmpLimits.MaximumPixels)
            throw Invalid($"the declared size {width}x{height} exceeds the {BmpLimits.MaximumPixels}-pixel limit.");

        // Colour masks: in the header from the 52-byte variant on, otherwise after a 40-byte header.
        bool usesMasks = compression is BmpCompression.Bitfields or BmpCompression.AlphaBitfields;
        uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
        int maskBytesAfterHeader = 0;
        if (usesMasks)
        {
            if (kind == BmpHeaderKind.Os2Version2)
                throw Invalid("colour masks are not defined for an OS/2 2.x header.");
            if (header >= 52)
            {
                rMask = U32(d, dib + 40);
                gMask = U32(d, dib + 44);
                bMask = U32(d, dib + 48);
                if (header >= 56) aMask = U32(d, dib + 52);
            }
            else
            {
                maskBytesAfterHeader = compression == BmpCompression.AlphaBitfields ? 16 : 12;
                if (d.Length - (dib + header) < maskBytesAfterHeader)
                    throw Invalid("truncated: the file ends inside the colour masks.");
                rMask = U32(d, dib + header);
                gMask = U32(d, dib + header + 4);
                bMask = U32(d, dib + header + 8);
                if (maskBytesAfterHeader == 16) aMask = U32(d, dib + header + 12);
            }

            if ((rMask | gMask | bMask) == 0)
                throw Invalid("the colour masks are all zero.");
            if (bpp == 16 && ((rMask | gMask | bMask | aMask) > 0xFFFF))
                throw Invalid("a colour mask of a 16-bit pixel reaches above bit 15.");
        }
        else if (bpp == 16)
        {
            // Without masks a 16-bit pixel is 5-5-5, whatever a V4/V5 header's mask fields hold.
            rMask = 0x7C00; gMask = 0x03E0; bMask = 0x001F;
        }

        // 32-bit BI_RGB: with the 40-byte-class headers the fourth byte is alpha, which is how
        // ImageMagick and stb_image read it. A V4/V5 header's masks only apply under BI_BITFIELDS,
        // so a V4/V5 BI_RGB file is plain opaque BGRx.
        bool rgb32Alpha = !usesMasks && bpp == 32 && header < 108;

        int channels = (usesMasks && aMask != 0) || rgb32Alpha ? 4 : 3;

        int paletteOffset = dib + header + maskBytesAfterHeader;
        if (offBits < paletteOffset)
            throw Invalid("the pixel data starts inside the headers.");
        if (offBits > d.Length)
            throw Invalid("truncated: the pixel data offset lies beyond the end of the file.");

        int paletteEntry = kind == BmpHeaderKind.Os2Core ? 3 : 4;
        int paletteCount = 0;
        if (bpp <= 8)
        {
            long maxEntries = 1L << bpp;
            long wanted = clrUsed == 0 ? maxEntries : Math.Min(clrUsed, maxEntries);
            // Writers that store fewer entries than the depth allows leave the pixel data right after
            // them, so what the offset leaves room for bounds the table. Indices beyond it read black.
            long room = (offBits - paletteOffset) / paletteEntry;
            paletteCount = (int)Math.Min(wanted, room);
        }

        if (compression is BmpCompression.Rgb or BmpCompression.Bitfields or BmpCompression.AlphaBitfields)
        {
            long stride = ((long)width * bpp + 31) / 32 * 4;
            long need = stride * height;
            if (need > d.Length - offBits)
                throw Invalid($"truncated: {need} bytes of pixel data are declared but {d.Length - offBits} are present.");
        }

        byte[]? icc = null;
        if (kind == BmpHeaderKind.V5 && U32(d, dib + 56) == ProfileEmbedded)
        {
            uint profileOffset = U32(d, dib + 112);
            uint profileSize = U32(d, dib + 116);
            if (profileSize != 0)
            {
                // bV5ProfileData counts from the start of the V5 header, not of the file.
                if (profileOffset < hsz || (long)dib + profileOffset + profileSize > d.Length)
                    throw Invalid("the embedded ICC profile lies outside the file.");
                icc = d.Slice(dib + (int)profileOffset, (int)profileSize).ToArray();
            }
        }

        var info = new BmpInfo(width, height, channels, kind, bpp, compression, topDown, icc);
        return new BmpHeader
        {
            Info = info,
            PixelOffset = (int)offBits,
            PaletteCount = paletteCount,
            PaletteOffset = paletteOffset,
            PaletteEntrySize = paletteEntry,
            RedMask = rMask,
            GreenMask = gMask,
            BlueMask = bMask,
            AlphaMask = aMask,
            UsesMasks = usesMasks,
            Rgb32HasAlpha = rgb32Alpha,
        };
    }

    private static BmpCompression ReadCompression(BmpHeaderKind kind, int header, uint field, int bpp)
    {
        if (kind == BmpHeaderKind.Os2Core) return BmpCompression.Rgb;

        switch (field)
        {
            case 0: return BmpCompression.Rgb;
            case 1: return BmpCompression.Rle8;
            case 2: return BmpCompression.Rle4;
            case 3:
                // The same code is OS/2's Huffman 1D for a 1-bit image, and BI_BITFIELDS needs 16/32 bits.
                if (bpp == 1)
                    throw new NotSupportedException("BMP compression OS/2 Huffman 1D is not supported.");
                return BmpCompression.Bitfields;
            case 4:
                if (header == 64 && bpp == 24)
                    throw new NotSupportedException("BMP compression OS/2 RLE24 is not supported.");
                throw new NotSupportedException("BMP compression BI_JPEG (an embedded JPEG stream) is not supported.");
            case 5:
                throw new NotSupportedException("BMP compression BI_PNG (an embedded PNG stream) is not supported.");
            case 6: return BmpCompression.AlphaBitfields;
            case 11 or 12 or 13:
                throw new NotSupportedException("BMP compression BI_CMYK (CMYK pixels) is not supported.");
            default:
                throw Invalid($"unknown compression code {field}.");
        }
    }

    internal static InvalidDataException Invalid(string message) => new($"BMP: {message}");

    private static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);
}
