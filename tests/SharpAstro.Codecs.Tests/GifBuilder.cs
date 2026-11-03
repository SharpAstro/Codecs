namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Test-only GIF writer for the files ImageMagick will not produce on request: a first frame with its
/// own (local) palette, an index the palette is too small for, a frame outside the screen, a screen
/// declared far bigger than its data, unknown extensions, trailing garbage, truncation.
/// <para>
/// Its LZW is deliberately the dumbest valid encoding: a clear code before every run of
/// <c>2^root - 2</c> literals, so the table never fills far enough to widen the code. It cannot share
/// a mistake with the decoder's width schedule, and ImageMagick's real compressing encoder covers
/// the table-growth path in <see cref="GifStillTests"/>.
/// </para>
/// </summary>
internal sealed class GifBuilder
{
    private readonly List<byte> _bytes = [];

    public byte[] ToArray() => [.. _bytes];

    public GifBuilder Header(int width, int height, byte[]? globalRgb = null, int background = 0, bool gif89a = true)
    {
        _bytes.AddRange(gif89a ? "GIF89a"u8.ToArray() : "GIF87a"u8.ToArray());
        U16(width);
        U16(height);
        int packed = 0x70; // colour resolution 8 bits, unsorted
        if (globalRgb is not null)
        {
            packed |= 0x80 | TableBits(globalRgb.Length / 3);
        }
        _bytes.Add((byte)packed);
        _bytes.Add((byte)background);
        _bytes.Add(0); // pixel aspect ratio
        if (globalRgb is not null)
        {
            _bytes.AddRange(Pad(globalRgb));
        }
        return this;
    }

    public GifBuilder GraphicControl(int delay = 0, int disposal = 0, int transparentIndex = -1)
    {
        _bytes.AddRange([0x21, 0xF9, 4, (byte)((disposal << 2) | (transparentIndex >= 0 ? 1 : 0)), (byte)delay, (byte)(delay >> 8),
            (byte)Math.Max(transparentIndex, 0), 0]);
        return this;
    }

    public GifBuilder Netscape(int loops)
    {
        _bytes.AddRange([0x21, 0xFF, 11]);
        _bytes.AddRange("NETSCAPE2.0"u8.ToArray());
        _bytes.AddRange([3, 1, (byte)loops, (byte)(loops >> 8), 0]);
        return this;
    }

    /// <summary>An extension block of any label with the given payload split into sub-blocks of at most 255.</summary>
    public GifBuilder Extension(byte label, params byte[] payload)
    {
        _bytes.Add(0x21);
        _bytes.Add(label);
        SubBlocks(payload);
        return this;
    }

    public GifBuilder Image(int left, int top, int width, int height, byte[] indices, int minCodeSize,
        byte[]? localRgb = null, bool interlaced = false, bool terminate = true)
    {
        _bytes.Add(0x2C);
        U16(left);
        U16(top);
        U16(width);
        U16(height);
        int packed = interlaced ? 0x40 : 0;
        if (localRgb is not null)
        {
            packed |= 0x80 | TableBits(localRgb.Length / 3);
        }
        _bytes.Add((byte)packed);
        if (localRgb is not null)
        {
            _bytes.AddRange(Pad(localRgb));
        }
        _bytes.Add((byte)minCodeSize);
        SubBlocks(Lzw(interlaced ? Interlace(indices, width, height) : indices, minCodeSize), terminate);
        return this;
    }

    /// <summary>An image whose LZW payload is exactly <paramref name="lzw"/>, already encoded.</summary>
    public GifBuilder RawImage(int left, int top, int width, int height, byte[] lzw, int minCodeSize, bool terminate = true)
    {
        _bytes.Add(0x2C);
        U16(left);
        U16(top);
        U16(width);
        U16(height);
        _bytes.Add(0);
        _bytes.Add((byte)minCodeSize);
        SubBlocks(lzw, terminate);
        return this;
    }

    public GifBuilder Trailer()
    {
        _bytes.Add(0x3B);
        return this;
    }

    public GifBuilder Append(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    /// <summary>Rows reordered into the four-pass file order (GIF89a Appendix E).</summary>
    public static byte[] Interlace(byte[] indices, int width, int height)
    {
        var result = new byte[indices.Length];
        int fileRow = 0;
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
        {
            for (int y = start; y < height; y += step, fileRow++)
            {
                Array.Copy(indices, y * width, result, fileRow * width, width);
            }
        }
        return result;
    }

    /// <summary>The stream of <see cref="Lzw"/> for <paramref name="indices"/>, without sub-block framing.</summary>
    public static byte[] Lzw(byte[] indices, int minCodeSize)
    {
        int clear = 1 << minCodeSize;
        int eoi = clear + 1;
        int width = minCodeSize + 1;
        int run = clear - 2;
        var output = new List<byte>();
        int acc = 0, count = 0;

        void Put(int code)
        {
            acc |= code << count;
            count += width;
            while (count >= 8)
            {
                output.Add((byte)acc);
                acc >>= 8;
                count -= 8;
            }
        }

        for (int i = 0; i < indices.Length; i++)
        {
            if (i % run == 0)
            {
                Put(clear);
            }
            Put(indices[i]);
        }
        Put(eoi);
        if (count > 0)
        {
            output.Add((byte)acc);
        }
        return [.. output];
    }

    private void SubBlocks(byte[] payload, bool terminate = true)
    {
        for (int i = 0; i < payload.Length; i += 255)
        {
            int n = Math.Min(255, payload.Length - i);
            _bytes.Add((byte)n);
            _bytes.AddRange(payload.AsSpan(i, n).ToArray());
        }
        if (terminate)
        {
            _bytes.Add(0);
        }
    }

    private void U16(int v)
    {
        _bytes.Add((byte)v);
        _bytes.Add((byte)(v >> 8));
    }

    // Table sizes are powers of two from 2 to 256; the packed field holds log2(size) - 1.
    private static int TableBits(int entries)
    {
        int bits = 0;
        while ((2 << bits) < entries)
        {
            bits++;
        }
        return bits;
    }

    private static byte[] Pad(byte[] rgb)
    {
        int entries = 2 << TableBits(rgb.Length / 3);
        var padded = new byte[entries * 3];
        rgb.CopyTo(padded, 0);
        return padded;
    }
}
