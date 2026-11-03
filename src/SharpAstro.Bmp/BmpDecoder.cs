using System.Buffers.Binary;

namespace SharpAstro.Bmp;

/// <summary>
/// Reads BMP (Windows and OS/2 device-independent bitmap) files, written from the Microsoft
/// BITMAPCOREHEADER / BITMAPINFOHEADER / V4 / V5 documentation and the OS/2 bitmap notes; the output is
/// validated pixel by pixel against ImageMagick (see the test project).
/// <para>
/// Supported: BITMAPFILEHEADER + a 12-, 16-, 40-, 52-, 56-, 64-, 108- or 124-byte header; 1-, 4- and 8-bit
/// indexed, 16-bit (5-5-5 or any <c>BI_BITFIELDS</c> masks), 24-bit and 32-bit (<c>BI_RGB</c> or masks)
/// pixels; <c>BI_RLE8</c> and <c>BI_RLE4</c>; bottom-up and top-down row order; the V5 embedded ICC profile
/// (reported, never applied). Masks of any width are scaled to 8 bits by rounding to nearest.
/// </para>
/// <para>
/// Output is 8-bit, top row first, RGB, or RGBA when the file has an alpha channel: a non-zero alpha mask
/// under <c>BI_BITFIELDS</c>, or a 32-bit <c>BI_RGB</c> file with a 40-byte-class header, whose fourth
/// byte is alpha (as in ImageMagick and stb_image) unless it is zero in every pixel, in which case the
/// image is opaque, because that is how X8R8G8B8 writers leave it.
/// </para>
/// <para>
/// Malformed or truncated input throws <see cref="InvalidDataException"/>. <c>BI_JPEG</c>, <c>BI_PNG</c>,
/// <c>BI_CMYK*</c>, OS/2 Huffman 1D and OS/2 RLE24 throw <see cref="NotSupportedException"/> naming the
/// feature. A header declaring more than 2^28 pixels is refused before anything is allocated.
/// </para>
/// </summary>
public static class BmpDecoder
{
    /// <summary>The most pixels a BMP header may declare (2^28).</summary>
    public const long MaximumPixels = BmpLimits.MaximumPixels;

    /// <summary>
    /// Cheap sniff: the <c>BM</c> signature followed by a DIB header size this decoder knows. Two
    /// letters are weak magic, so this also checks the size field and the zeroed reserved words.
    /// </summary>
    public static bool HasBmpSignature(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M') return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[6..]) != 0) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(data[14..]) is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    /// <summary>
    /// Reads dimensions, layout and the embedded ICC profile from the headers without decoding pixels.
    /// Returns false instead of throwing for malformed or unsupported data.
    /// </summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out BmpInfo info)
    {
        try
        {
            info = BmpHeader.Parse(data).Info;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            info = null!;
            return false;
        }
    }

    /// <summary>Reads dimensions, layout and the embedded ICC profile from the headers.</summary>
    /// <exception cref="InvalidDataException">The headers are malformed, or the file is shorter than they declare.</exception>
    /// <exception cref="NotSupportedException">The file uses a compression this decoder does not read.</exception>
    public static BmpInfo ReadInfo(ReadOnlySpan<byte> data) => BmpHeader.Parse(data).Info;

    /// <summary>Decodes a BMP, returning false instead of throwing for malformed or unsupported data.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out BmpImage image)
    {
        try
        {
            image = Decode(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            image = null!;
            return false;
        }
    }

    /// <summary>Decodes a BMP into 8-bit RGB or RGBA, top row first.</summary>
    /// <param name="data">The complete file.</param>
    /// <exception cref="InvalidDataException">The data is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The file uses a compression this decoder does not read.</exception>
    public static BmpImage Decode(ReadOnlySpan<byte> data)
    {
        var header = BmpHeader.Parse(data);
        var info = header.Info;
        var pixels = new byte[checked(info.Width * info.Height * info.Channels)];

        switch (info.Compression)
        {
            case BmpCompression.Rle8:
            case BmpCompression.Rle4:
                BmpRle.Decode(data, header, pixels);
                break;
            default:
                BmpRaster.Decode(data, header, pixels);
                break;
        }

        return new BmpImage(info, pixels);
    }
}
