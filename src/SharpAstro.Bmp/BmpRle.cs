namespace SharpAstro.Bmp;

/// <summary>
/// BI_RLE8 / BI_RLE4. A stream is pairs of bytes: a non-zero count then a value (a run), or a zero
/// followed by an escape: 0 end of line, 1 end of bitmap, 2 delta (two more bytes: right, down), and 3 or
/// more an absolute run of that many pixels, padded to a 16-bit boundary.
/// <para>
/// Pixels the stream never writes (a delta skips them, or the bitmap ends early) take palette entry 0,
/// which is what ImageMagick leaves. Every iteration consumes at least two bytes, so a stream cannot loop
/// for ever, and every write is range-checked: a row past the last, or a run past the end of its row's
/// padded storage, is an <see cref="InvalidDataException"/>, not a silent wrap.
/// </para>
/// <para>
/// A run may overshoot the width into the row's padding (up to the 4-byte stride): ImageMagick's RLE writer
/// encodes the padding bytes of each scanline as pixels, so its files do this, and the overshoot is dropped.
/// </para>
/// </summary>
internal static class BmpRle
{
    internal static void Decode(ReadOnlySpan<byte> d, BmpHeader h, byte[] dst)
    {
        int width = h.Width, height = h.Height;
        bool rle4 = h.Info.Compression == BmpCompression.Rle4;
        bool topDown = h.Info.TopDown;
        var palette = BmpRaster.ReadPalette(d, h);
        // Columns the stored row has room for: the width rounded up to the 4-byte stride.
        int rowLimit = checked((int)h.Stride) * (rle4 ? 2 : 1);

        for (int i = 0; i < dst.Length; i += 3)
        {
            dst[i] = palette[0];
            dst[i + 1] = palette[1];
            dst[i + 2] = palette[2];
        }

        int p = h.PixelOffset;
        int end = d.Length;
        int x = 0, y = 0; // y counts rows in file order, which is bottom-up unless the file is top-down
        bool endOfBitmap = false;

        while (p < end)
        {
            if (end - p < 2) throw Truncated();
            int count = d[p];
            int value = d[p + 1];
            p += 2;

            if (count > 0)
            {
                CheckRoom(x, y, count, rowLimit, height);
                int row = RowOffset(y, height, width, topDown);
                int visible = Math.Max(0, Math.Min(count, width - x));
                if (!rle4)
                {
                    for (int i = 0; i < visible; i++)
                        Put(dst, row + (x + i) * 3, palette, value);
                }
                else
                {
                    for (int i = 0; i < visible; i++)
                        Put(dst, row + (x + i) * 3, palette, (i & 1) == 0 ? value >> 4 : value & 15);
                }
                x += count;
                continue;
            }

            if (value == 0) // end of line
            {
                x = 0;
                y++;
                if (y > height) throw BmpHeader.Invalid("RLE stream has more rows than the image.");
            }
            else if (value == 1) // end of bitmap
            {
                endOfBitmap = true;
                break;
            }
            else if (value == 2) // delta
            {
                if (end - p < 2) throw Truncated();
                x += d[p];
                y += d[p + 1];
                p += 2;
                if (x > rowLimit || y > height) throw BmpHeader.Invalid("RLE delta leaves the image.");
            }
            else // absolute run of 'value' pixels
            {
                int dataBytes = rle4 ? (value + 1) / 2 : value;
                if (end - p < dataBytes) throw Truncated();
                CheckRoom(x, y, value, rowLimit, height);
                int row = RowOffset(y, height, width, topDown);
                int visible = Math.Max(0, Math.Min(value, width - x));
                if (!rle4)
                {
                    for (int i = 0; i < visible; i++)
                        Put(dst, row + (x + i) * 3, palette, d[p + i]);
                }
                else
                {
                    for (int i = 0; i < visible; i++)
                    {
                        int b = d[p + (i >> 1)];
                        Put(dst, row + (x + i) * 3, palette, (i & 1) == 0 ? b >> 4 : b & 15);
                    }
                }
                x += value;
                // Runs are padded to a word; a stream may omit the pad when the data simply ends.
                p += Math.Min(dataBytes + (dataBytes & 1), end - p);
            }
        }

        // Without an end-of-bitmap marker the data must at least have reached the last pixel.
        if (!endOfBitmap && !(y >= height || (y == height - 1 && x >= width)))
            throw Truncated();
    }

    private static void CheckRoom(int x, int y, int count, int rowLimit, int height)
    {
        if (y >= height || x + count > rowLimit)
            throw BmpHeader.Invalid("RLE run leaves the image.");
    }

    private static int RowOffset(int fileRow, int height, int width, bool topDown) =>
        (topDown ? fileRow : height - 1 - fileRow) * width * 3;

    private static void Put(byte[] dst, int offset, byte[] palette, int index)
    {
        int q = index * 3;
        dst[offset] = palette[q];
        dst[offset + 1] = palette[q + 1];
        dst[offset + 2] = palette[q + 2];
    }

    private static InvalidDataException Truncated() =>
        BmpHeader.Invalid("truncated: the RLE data ends before the image is complete.");
}
