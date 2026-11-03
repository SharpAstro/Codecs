namespace SharpAstro.Tga;

/// <summary>Options for <see cref="TgaWriter"/>.</summary>
public sealed record TgaWriteOptions
{
    /// <summary>
    /// Write run-length coded pixels (image type 10) instead of raw ones (type 2). Default false. RLE output is
    /// smaller for flat images and up to 1/128 larger (one header byte per 128 pixels) for noise.
    /// </summary>
    public bool Rle { get; init; }

    /// <summary>
    /// True (the default) stores rows top to bottom and sets descriptor bit 5; false stores them bottom to top
    /// (the original Truevision layout, bit 5 clear). The decoded image is the same either way.
    /// </summary>
    public bool TopOrigin { get; init; } = true;
}

/// <summary>
/// Writer for Truevision TGA 2.0 true-colour images: 24-bit BGR and 32-bit BGRA, raw (type 2) or run-length
/// coded (type 10). Written from the Truevision TGA File Format Specification, version 2.0.
/// </summary>
/// <remarks>
/// <para>32-bit output declares 8 attribute (alpha) bits in the descriptor and stores alpha as given
/// (non-premultiplied). The image ID is empty and the colour map absent.</para>
/// <para>Every file ends with the 26-byte TGA 2.0 footer (extension and developer area offsets both 0, then
/// <c>TRUEVISION-XFILE.\0</c>), so a reader can tell it from a bare 1.0 file; no extension or developer area is written.</para>
/// <para>RLE packets never cross a scanline: the specification says a packet should not, so that a reader can
/// decode a row at a time. Every row therefore starts a fresh packet, and a run continuing from the end of one
/// row into the next is split in two. Runs of 2 to 128 identical pixels become run packets, everything between
/// them raw packets of 1 to 128 pixels; a run longer than 128 is a run packet of 128 followed by the rest.</para>
/// </remarks>
public static class TgaWriter
{
    private const int HeaderSize = 18;

    private static ReadOnlySpan<byte> Footer =>
        [0, 0, 0, 0, 0, 0, 0, 0, (byte)'T', (byte)'R', (byte)'U', (byte)'E', (byte)'V', (byte)'I', (byte)'S', (byte)'I',
         (byte)'O', (byte)'N', (byte)'-', (byte)'X', (byte)'F', (byte)'I', (byte)'L', (byte)'E', (byte)'.', 0];

    /// <summary>Encodes tightly packed, top-down RGB8 (3 bytes per pixel) as a 24-bit TGA.</summary>
    /// <exception cref="ArgumentException">A dimension is outside 1..65535, or <paramref name="rgb"/> is not exactly <c>width * height * 3</c> bytes.</exception>
    public static byte[] EncodeRgb8(ReadOnlySpan<byte> rgb, int width, int height, TgaWriteOptions? options = null) =>
        Encode(rgb, width, height, 3, options);

    /// <summary>Encodes tightly packed, top-down, non-premultiplied RGBA8 (4 bytes per pixel) as a 32-bit TGA.</summary>
    /// <exception cref="ArgumentException">A dimension is outside 1..65535, or <paramref name="rgba"/> is not exactly <c>width * height * 4</c> bytes.</exception>
    public static byte[] EncodeRgba8(ReadOnlySpan<byte> rgba, int width, int height, TgaWriteOptions? options = null) =>
        Encode(rgba, width, height, 4, options);

    private static byte[] Encode(ReadOnlySpan<byte> src, int width, int height, int bpp, TgaWriteOptions? options)
    {
        options ??= new TgaWriteOptions();
        if (width is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException($"TGA width {width} is outside 1..65535.", nameof(width));
        }

        if (height is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException($"TGA height {height} is outside 1..65535.", nameof(height));
        }

        long expected = (long)width * height * bpp;
        if (src.Length != expected)
        {
            throw new ArgumentException(
                $"Pixel data is {src.Length} bytes; {width}x{height} at {bpp} bytes per pixel needs {expected}.", nameof(src));
        }

        int stride = width * bpp;
        // Worst case for RLE is one header byte per 128 pixels of a row on top of the raw bytes (a row of all
        // different pixels), so this bound holds for both modes.
        long capacity = HeaderSize + expected + Footer.Length
            + (options.Rle ? (long)height * ((width + 127) / 128) : 0);
        if (capacity > Array.MaxLength)
        {
            throw new ArgumentException($"A {width}x{height} TGA would not fit in one array.", nameof(src));
        }

        var output = new byte[capacity];
        output[2] = options.Rle ? (byte)10 : (byte)2;
        output[12] = (byte)width;
        output[13] = (byte)(width >> 8);
        output[14] = (byte)height;
        output[15] = (byte)(height >> 8);
        output[16] = (byte)(bpp * 8);
        output[17] = (byte)((bpp == 4 ? 8 : 0) | (options.TopOrigin ? 0x20 : 0));

        int pos = HeaderSize;
        for (int y = 0; y < height; y++)
        {
            // Stored order: top origin writes row 0 first, bottom origin writes the last row first.
            ReadOnlySpan<byte> row = src.Slice((options.TopOrigin ? y : height - 1 - y) * stride, stride);
            pos = options.Rle ? WriteRleRow(row, width, bpp, output, pos) : WriteRawRow(row, width, bpp, output, pos);
        }

        Footer.CopyTo(output.AsSpan(pos));
        pos += Footer.Length;
        return pos == output.Length ? output : output.AsSpan(0, pos).ToArray();
    }

    // RGB(A) in, BGR(A) out.
    private static int WriteRawRow(ReadOnlySpan<byte> row, int width, int bpp, byte[] output, int pos)
    {
        for (int x = 0; x < width; x++)
        {
            pos = WritePixel(row.Slice(x * bpp, bpp), bpp, output, pos);
        }

        return pos;
    }

    private static int WriteRleRow(ReadOnlySpan<byte> row, int width, int bpp, byte[] output, int pos)
    {
        int x = 0;
        while (x < width)
        {
            ReadOnlySpan<byte> px = row.Slice(x * bpp, bpp);
            int run = 1;
            while (x + run < width && run < 128 && row.Slice((x + run) * bpp, bpp).SequenceEqual(px))
            {
                run++;
            }

            if (run >= 2)
            {
                output[pos++] = (byte)(0x80 | (run - 1));
                pos = WritePixel(px, bpp, output, pos);
                x += run;
                continue;
            }

            // Raw packet: extend until the next pixel starts a run (equal to its successor), the row ends or 128.
            int count = 1;
            while (x + count < width && count < 128
                && !(x + count + 1 < width
                    && row.Slice((x + count) * bpp, bpp).SequenceEqual(row.Slice((x + count + 1) * bpp, bpp))))
            {
                count++;
            }

            output[pos++] = (byte)(count - 1);
            for (int i = 0; i < count; i++)
            {
                pos = WritePixel(row.Slice((x + i) * bpp, bpp), bpp, output, pos);
            }

            x += count;
        }

        return pos;
    }

    private static int WritePixel(ReadOnlySpan<byte> px, int bpp, byte[] output, int pos)
    {
        output[pos++] = px[2];
        output[pos++] = px[1];
        output[pos++] = px[0];
        if (bpp == 4)
        {
            output[pos++] = px[3];
        }

        return pos;
    }
}
