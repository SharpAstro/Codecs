using System.Buffers.Binary;

namespace SharpAstro.Bmp;

/// <summary>
/// Writes BMP files: 24-bit BGR with a 40-byte <c>BITMAPINFOHEADER</c>, and 32-bit BGRA with a 108-byte
/// <c>BITMAPV4HEADER</c> whose <c>BI_BITFIELDS</c> masks name the alpha byte (<c>0xFF000000</c>), so alpha
/// survives in every reader that honours the masks (ImageMagick, stb_image, WIC, this package's decoder).
/// <para>
/// Both write uncompressed, bottom-up rows padded to a multiple of four bytes, which is what every BMP
/// reader accepts. The input is tightly packed or strided, top row first, non-premultiplied; the encoder
/// never changes a sample.
/// </para>
/// <para>
/// Arguments are validated before anything is allocated: <c>width</c> and <c>height</c> must be positive,
/// the image at most <see cref="BmpDecoder.MaximumPixels"/> pixels (what this package will read back), the
/// stride at least one row and the input long enough for the last row. A violation is an
/// <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public static class BmpEncoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;
    private const uint LcsSrgb = 0x73524742; // 'sRGB'

    /// <summary>Encodes tightly packed 8-bit RGB (R, G, B per pixel, top row first) as a 24-bit BMP.</summary>
    public static byte[] EncodeRgb24(ReadOnlySpan<byte> rgb, int width, int height) =>
        EncodeRgb24(rgb, width, height, 0);

    /// <summary>Encodes 8-bit RGB (R, G, B per pixel, top row first) as a 24-bit BMP.</summary>
    /// <param name="rgb">The pixels.</param>
    /// <param name="width">Pixel width, at least 1.</param>
    /// <param name="height">Pixel height, at least 1.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed (<c>width * 3</c>).</param>
    public static byte[] EncodeRgb24(ReadOnlySpan<byte> rgb, int width, int height, int stride)
    {
        int rowBytes = Validate(rgb.Length, width, height, 3, ref stride);
        int outStride = (rowBytes + 3) & ~3;
        byte[] file = Allocate(InfoHeaderSize, outStride, height, out int pixelOffset);
        WriteFileHeader(file, pixelOffset);
        WriteInfoHeader(file, InfoHeaderSize, width, height, 24, BiRgb, outStride * height);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> src = rgb.Slice(y * stride, rowBytes);
            Span<byte> dst = file.AsSpan(pixelOffset + (height - 1 - y) * outStride, rowBytes);
            for (int x = 0; x < src.Length; x += 3)
            {
                dst[x] = src[x + 2];
                dst[x + 1] = src[x + 1];
                dst[x + 2] = src[x];
            }
        }

        return file;
    }

    /// <summary>Encodes tightly packed 8-bit RGBA (R, G, B, A per pixel, top row first) as a 32-bit BMP.</summary>
    public static byte[] EncodeRgba32(ReadOnlySpan<byte> rgba, int width, int height) =>
        EncodeRgba32(rgba, width, height, 0);

    /// <summary>Encodes 8-bit non-premultiplied RGBA (R, G, B, A per pixel, top row first) as a 32-bit BMP with alpha.</summary>
    /// <param name="rgba">The pixels.</param>
    /// <param name="width">Pixel width, at least 1.</param>
    /// <param name="height">Pixel height, at least 1.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed (<c>width * 4</c>).</param>
    public static byte[] EncodeRgba32(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        int rowBytes = Validate(rgba.Length, width, height, 4, ref stride);
        int outStride = rowBytes; // 32-bit rows are already a multiple of four
        byte[] file = Allocate(V4HeaderSize, outStride, height, out int pixelOffset);
        WriteFileHeader(file, pixelOffset);
        WriteInfoHeader(file, V4HeaderSize, width, height, 32, BiBitfields, outStride * height);

        // The V4 additions: colour masks (R, G, B, A), then the colour space type; the endpoints and
        // gamma that follow are ignored for LCS_sRGB and stay zero.
        Span<byte> h = file.AsSpan(FileHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(h[44..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(h[48..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(h[52..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(h[56..], LcsSrgb);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> src = rgba.Slice(y * stride, rowBytes);
            Span<byte> dst = file.AsSpan(pixelOffset + (height - 1 - y) * outStride, rowBytes);
            for (int x = 0; x < src.Length; x += 4)
            {
                dst[x] = src[x + 2];
                dst[x + 1] = src[x + 1];
                dst[x + 2] = src[x];
                dst[x + 3] = src[x + 3];
            }
        }

        return file;
    }

    /// <summary>Checks the arguments and returns the bytes of one tightly packed input row; resolves a 0 stride.</summary>
    private static int Validate(int length, int width, int height, int channels, ref int stride)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width), width, "width must be at least 1.");
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height), height, "height must be at least 1.");
        if ((long)width * height > BmpDecoder.MaximumPixels)
            throw new ArgumentException($"{width} x {height} exceeds the {BmpDecoder.MaximumPixels}-pixel limit of this package.");

        // width * height <= 2^28 bounds width * channels by 2^30, so the row fits an int with room for padding.
        int rowBytes = width * channels;
        if (stride == 0) stride = rowBytes;
        if (stride < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"stride must be at least {rowBytes} (width * {channels}), or 0 for tightly packed.");

        long required = (long)(height - 1) * stride + rowBytes;
        if (length < required)
            throw new ArgumentException($"the input holds {length} bytes but {width} x {height} with stride {stride} needs {required}.");

        return rowBytes;
    }

    private static byte[] Allocate(int dibHeaderSize, int outStride, int height, out int pixelOffset)
    {
        pixelOffset = FileHeaderSize + dibHeaderSize;
        long total = pixelOffset + (long)outStride * height;
        if (total > int.MaxValue)
            throw new ArgumentException("the encoded BMP would exceed 2 GiB.");
        return new byte[(int)total];
    }

    private static void WriteFileHeader(byte[] file, int pixelOffset)
    {
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)pixelOffset);
    }

    /// <summary>The first 40 bytes of the DIB header; a positive height means bottom-up rows.</summary>
    private static void WriteInfoHeader(byte[] file, int headerSize, int width, int height, int bitsPerPixel,
        uint compression, int imageSize)
    {
        Span<byte> h = file.AsSpan(FileHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], (ushort)bitsPerPixel);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], compression);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)imageSize);
        // Resolution (2835 px/m = 72 dpi); palette counts stay 0.
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], 2835);
    }
}
