namespace SharpAstro.Gif;

/// <summary>Where one frame's palette and LZW data sit in the file, beside its public description.</summary>
/// <param name="Info">The frame's public description.</param>
/// <param name="PaletteOffset">File offset of the first palette entry (3 bytes each); valid when <c>Info.ColorTableSize</c> is above 0.</param>
/// <param name="DataOffset">File offset of the first data sub-block's length byte.</param>
/// <param name="DataLength">Total payload bytes over all of the frame's sub-blocks.</param>
internal readonly record struct GifFrameLayout(GifFrameInfo Info, int PaletteOffset, int DataOffset, int DataLength);

/// <summary>
/// The GIF block structure (GIF89a section "Syntax"), parsed once into <see cref="GifInfo"/> plus the
/// file offsets the decoder needs. Nothing here touches LZW: a frame's data is only walked, sub-block by
/// sub-block, to find its end and total length.
/// <para>
/// Policy, as the issue asks: decode what is there and say so. A file that ends early, or a block
/// introducer that is none of 0x21 / 0x2C / 0x3B, ends the parse with <see cref="GifInfo.HasTrailer"/>
/// false and the rest counted in <see cref="GifInfo.UnparsedByteCount"/>; whatever frames were read
/// before that stand. Only a file that yields no usable header, or no frame at all, throws.
/// </para>
/// <para>Every loop advances by at least one byte or ends: each block consumes its introducer, each
/// sub-block its length byte.</para>
/// </summary>
internal sealed class GifContainer
{
    /// <summary>File offset of the global colour table: right after the 13-byte header and logical screen descriptor.</summary>
    internal const int GlobalTableOffset = 13;

    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationLabel = 0xFF;

    private GifContainer(GifInfo info, GifFrameLayout[] frames)
    {
        Info = info;
        Frames = frames;
    }

    public GifInfo Info { get; }

    public GifFrameLayout[] Frames { get; }

    /// <summary>Parses the whole block structure, throwing <see cref="InvalidDataException"/> when it is not a usable GIF.</summary>
    public static GifContainer Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13 || !(data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            throw new InvalidDataException("Not a GIF: missing GIF87a/GIF89a signature or logical screen descriptor.");
        }

        int width = data[6] | (data[7] << 8);
        int height = data[8] | (data[9] << 8);
        int packed = data[10];
        int background = data[11];
        int aspect = data[12];

        // Charged here, from 4 header bytes, before any caller can size a raster from it (summed in long).
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("GIF logical screen has a zero dimension.");
        }
        if ((long)width * height > GifLimits.MaximumScreenPixels)
        {
            throw new InvalidDataException($"GIF logical screen {width}x{height} exceeds the {GifLimits.MaximumScreenPixels}-pixel limit.");
        }

        int pos = GlobalTableOffset;
        bool hasGlobal = (packed & 0x80) != 0;
        int globalSize = hasGlobal ? 1 << ((packed & 7) + 1) : 0;
        int globalOffset = pos;
        if (hasGlobal)
        {
            pos += globalSize * 3;
            if (pos > data.Length)
            {
                throw new InvalidDataException("GIF global colour table is truncated.");
            }
        }

        var frames = new List<GifFrameLayout>();
        int? loopCount = null;
        bool hasTrailer = false;

        // Pending Graphic Control Extension state, consumed by the next image descriptor.
        int delay = 0, disposal = 0, transparent = -1;

        // Each pass consumes at least the block introducer byte (pos grows) or breaks.
        while (pos < data.Length)
        {
            byte introducer = data[pos];
            if (introducer == Trailer)
            {
                hasTrailer = true;
                pos++;
                break;
            }

            if (introducer == ExtensionIntroducer)
            {
                if (pos + 2 > data.Length)
                {
                    pos = data.Length;
                    break;
                }

                byte label = data[pos + 1];
                int blockStart = pos + 2;
                int next = blockStart;
                SkipSubBlocks(data, ref next, out _, out bool terminated);

                if (label == GraphicControlLabel && next > blockStart && data[blockStart] >= 4 && blockStart + 5 <= data.Length)
                {
                    int flags = data[blockStart + 1];
                    disposal = (flags >> 2) & 7;
                    delay = data[blockStart + 2] | (data[blockStart + 3] << 8);
                    transparent = (flags & 1) != 0 ? data[blockStart + 4] : -1;
                }
                else if (label == ApplicationLabel && terminated)
                {
                    ReadApplicationExtension(data, blockStart, ref loopCount);
                }

                pos = next;
                if (!terminated)
                {
                    break;
                }
                continue;
            }

            if (introducer != ImageSeparator)
            {
                // Not a block this format defines: stop, keep the frames already read.
                break;
            }

            // Image descriptor: separator + 9 bytes, optional local table, min code size, sub-blocks.
            int descriptor = pos + 1;
            if (descriptor + 9 > data.Length)
            {
                pos = data.Length;
                break;
            }

            int left = data[descriptor] | (data[descriptor + 1] << 8);
            int top = data[descriptor + 2] | (data[descriptor + 3] << 8);
            int fw = data[descriptor + 4] | (data[descriptor + 5] << 8);
            int fh = data[descriptor + 6] | (data[descriptor + 7] << 8);
            int imagePacked = data[descriptor + 8];

            // A frame must lie inside the logical screen; the sums are in long so no pair of
            // 16-bit fields can wrap.
            if (fw == 0 || fh == 0 || (long)left + fw > width || (long)top + fh > height)
            {
                throw new InvalidDataException(
                    $"GIF frame {frames.Count} ({fw}x{fh} at {left},{top}) lies outside the {width}x{height} logical screen.");
            }

            int cursor = descriptor + 9;
            bool hasLocal = (imagePacked & 0x80) != 0;
            int paletteOffset = globalOffset;
            int tableSize = globalSize;
            if (hasLocal)
            {
                tableSize = 1 << ((imagePacked & 7) + 1);
                paletteOffset = cursor;
                cursor += tableSize * 3;
                if (cursor > data.Length)
                {
                    pos = data.Length;
                    break;
                }
            }

            if (cursor >= data.Length)
            {
                pos = data.Length;
                break;
            }

            int minCodeSize = data[cursor];
            if (minCodeSize is < 2 or > 8)
            {
                throw new InvalidDataException($"GIF frame {frames.Count} has LZW minimum code size {minCodeSize}; only 2-8 is valid.");
            }

            int dataOffset = cursor + 1;
            int end = dataOffset;
            SkipSubBlocks(data, ref end, out int dataLength, out bool dataTerminated);

            frames.Add(new GifFrameLayout(
                new GifFrameInfo(left, top, fw, fh, delay, disposal, transparent,
                    (imagePacked & 0x40) != 0, hasLocal, tableSize, minCodeSize, !dataTerminated),
                paletteOffset, dataOffset, dataLength));

            // The GCE applies to one image only.
            delay = 0;
            disposal = 0;
            transparent = -1;

            pos = end;
            if (!dataTerminated)
            {
                break;
            }
        }

        if (frames.Count == 0)
        {
            throw new InvalidDataException("GIF holds no image.");
        }

        var layouts = frames.ToArray();
        var infos = new GifFrameInfo[layouts.Length];
        for (int i = 0; i < layouts.Length; i++)
        {
            infos[i] = layouts[i].Info;
        }

        var info = new GifInfo(width, height, background, aspect, hasGlobal, globalSize, loopCount,
            infos, hasTrailer, data.Length - pos);
        return new GifContainer(info, layouts);
    }

    /// <summary>
    /// Walks a chain of data sub-blocks from <paramref name="pos"/> to its zero terminator without
    /// copying anything, summing the payload. <paramref name="terminated"/> is false when the input
    /// ended first, with <paramref name="pos"/> then at the end of the input.
    /// </summary>
    internal static void SkipSubBlocks(ReadOnlySpan<byte> data, ref int pos, out int total, out bool terminated)
    {
        long sum = 0;
        terminated = false;
        // Every pass reads one length byte, so pos strictly grows.
        while (pos < data.Length)
        {
            int n = data[pos++];
            if (n == 0)
            {
                terminated = true;
                break;
            }
            if (pos + n > data.Length)
            {
                sum += data.Length - pos;
                pos = data.Length;
                break;
            }
            sum += n;
            pos += n;
        }
        total = (int)sum;
    }

    /// <summary>
    /// Concatenates a frame's sub-block payloads into one array, sized exactly once from the length the
    /// parse already summed, so a frame of thousands of 255-byte sub-blocks costs one allocation.
    /// </summary>
    internal static byte[] GatherSubBlocks(ReadOnlySpan<byte> data, int offset, int total)
    {
        var result = new byte[total];
        int written = 0;
        int pos = offset;
        // Same advance guarantee as SkipSubBlocks; the sums agree because both walk the same bytes.
        while (pos < data.Length && written < total)
        {
            int n = data[pos++];
            if (n == 0)
            {
                break;
            }
            n = Math.Min(n, data.Length - pos);
            data.Slice(pos, n).CopyTo(result.AsSpan(written));
            written += n;
            pos += n;
        }
        return result;
    }

    private static void ReadApplicationExtension(ReadOnlySpan<byte> data, int start, ref int? loopCount)
    {
        // Sub-block 1: length 11, 8-byte identifier + 3-byte authentication code.
        if (start >= data.Length || data[start] != 11 || start + 12 > data.Length)
        {
            return;
        }

        var id = data.Slice(start + 1, 11);
        if (!id.SequenceEqual("NETSCAPE2.0"u8) && !id.SequenceEqual("ANIMEXTS1.0"u8))
        {
            return;
        }

        // Sub-block 2: length 3 (at least), sub-id 1, loop count LE16. Only the first one counts.
        int second = start + 12;
        if (second + 4 <= data.Length && data[second] >= 3 && data[second + 1] == 1 && loopCount is null)
        {
            loopCount = data[second + 2] | (data[second + 3] << 8);
        }
    }
}
