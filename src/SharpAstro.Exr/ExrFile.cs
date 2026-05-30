namespace SharpAstro.Exr;

/// <summary>
/// Reads and writes single-part, scanline OpenEXR files: magic + version, header
/// attributes (<see cref="ExrHeader"/>), the scanline-offset table, and the pixel
/// blocks. Pixel data within a block is laid out scanline-by-scanline, and within
/// each scanline channel-by-channel in ascending name order (OpenEXR's storage
/// convention). Per-block compression is dispatched to <see cref="ExrCompressor"/>.
/// </summary>
public static class ExrFile
{
    /// <summary>Serialize an <see cref="ExrImage"/> to OpenEXR bytes.</summary>
    public static byte[] Write(ExrImage image)
    {
        if (image.LineOrder != ExrLineOrder.IncreasingY)
            throw new NotSupportedException("ExrFile.Write emits INCREASING_Y line order only.");

        var sorted = SortChannels(image);
        int w = image.Width, h = image.Height;
        int linesPerBlock = ExrFormat.ScanLinesPerBlock(image.Compression);
        int numBlocks = (h + linesPerBlock - 1) / linesPerBlock;

        var header = new ExrHeader
        {
            Channels = [.. sorted.Select(s => s.ch)],
            Compression = image.Compression,
            LineOrder = ExrLineOrder.IncreasingY,
            DataXMin = 0, DataYMin = 0, DataXMax = w - 1, DataYMax = h - 1,
            DispXMin = 0, DispYMin = 0, DispXMax = w - 1, DispYMax = h - 1,
            Chromaticities = image.Chromaticities,
        };

        var writer = new ExrWriter(1024 + w * h * 2);
        writer.WriteInt32(ExrFormat.MagicNumber);
        int version = ExrFormat.Version | (header.RequiresLongNames ? ExrFormat.LongNamesFlag : 0);
        writer.WriteInt32(version);
        header.WriteAttributes(writer);

        // Reserve the offset table; remember where so we can backfill absolute offsets.
        int offsetTablePos = writer.Length;
        for (var i = 0; i < numBlocks; i++) writer.WriteUInt64(0);

        var blockInfo = new ExrBlockInfo(w, sorted.Select(s => s.ch).ToArray());
        for (var b = 0; b < numBlocks; b++)
        {
            int firstScan = b * linesPerBlock;
            int count = Math.Min(linesPerBlock, h - firstScan);
            byte[] raw = GatherBlock(sorted, w, firstScan, count);
            byte[] payload = ExrCompressor.Compress(image.Compression, raw, blockInfo with { ScanlineCount = count });

            ulong blockOffset = (ulong)writer.Length;
            writer.PatchUInt64At(offsetTablePos + b * 8, blockOffset);
            writer.WriteInt32(firstScan);           // y of the block's first scanline (DataYMin == 0)
            writer.WriteInt32(payload.Length);
            writer.WriteBytes(payload);
        }

        return writer.ToArray();
    }

    /// <summary>Parse OpenEXR bytes into an <see cref="ExrImage"/>.</summary>
    public static ExrImage Read(ReadOnlySpan<byte> bytes)
    {
        var r = new ExrReader(bytes);
        if (r.ReadInt32() != ExrFormat.MagicNumber)
            throw new InvalidDataException("Not an OpenEXR file (bad magic number).");
        int version = r.ReadInt32();
        int flags = version & ~0xFF;
        if ((flags & ExrFormat.TiledFlag) != 0) throw new NotSupportedException("Tiled EXR is not supported (scanline only).");
        if ((flags & ExrFormat.MultiPartFlag) != 0) throw new NotSupportedException("Multi-part EXR is not supported.");
        if ((flags & ExrFormat.NonImageFlag) != 0) throw new NotSupportedException("Deep/non-image EXR is not supported.");

        var header = ExrHeader.ReadAttributes(ref r);
        int w = header.Width, h = header.Height;
        if (w <= 0 || h <= 0) throw new InvalidDataException($"Invalid EXR data window ({w}x{h}).");

        var channels = header.SortedChannels();
        int linesPerBlock = ExrFormat.ScanLinesPerBlock(header.Compression);
        int numBlocks = (h + linesPerBlock - 1) / linesPerBlock;

        // Allocate per-channel output (full-image, row-major).
        var data = new byte[channels.Count][];
        for (var c = 0; c < channels.Count; c++)
            data[c] = new byte[w * h * channels[c].BytesPerSample];

        // Read the offset table, then each block via its absolute offset (handles any order).
        var offsets = new ulong[numBlocks];
        for (var i = 0; i < numBlocks; i++) offsets[i] = r.ReadUInt64();

        int rowBytes = 0;
        foreach (var c in channels) rowBytes += w * c.BytesPerSample;
        var blockInfo = new ExrBlockInfo(w, [.. channels]);

        for (var i = 0; i < numBlocks; i++)
        {
            r.Seek((int)offsets[i]);
            int y = r.ReadInt32();
            int dataSize = r.ReadInt32();
            var src = r.ReadBytes(dataSize);

            int firstScan = y - header.DataYMin;
            int count = Math.Min(linesPerBlock, h - firstScan);
            int uncompressedSize = rowBytes * count;
            byte[] raw = ExrCompressor.Decompress(header.Compression, src, uncompressedSize, blockInfo with { ScanlineCount = count });

            // Scatter: per scanline, per channel (sorted order), copy this row into the channel buffer.
            int p = 0;
            for (var s = 0; s < count; s++)
            {
                int row = firstScan + s;
                for (var c = 0; c < channels.Count; c++)
                {
                    int n = w * channels[c].BytesPerSample;
                    raw.AsSpan(p, n).CopyTo(data[c].AsSpan(row * n, n));
                    p += n;
                }
            }
        }

        var image = new ExrImage { Width = w, Height = h, Compression = header.Compression, LineOrder = header.LineOrder, Chromaticities = header.Chromaticities };
        for (var c = 0; c < channels.Count; c++)
            image.AddChannel(channels[c], data[c]);
        return image;
    }

    // Channels paired with their pixel data, in ascending name order.
    private static (ExrChannel ch, byte[] data)[] SortChannels(ExrImage image)
    {
        var pairs = new (ExrChannel ch, byte[] data)[image.Channels.Count];
        for (var i = 0; i < image.Channels.Count; i++)
            pairs[i] = (image.Channels[i], image.GetData(i));
        Array.Sort(pairs, static (a, b) => string.CompareOrdinal(a.ch.Name, b.ch.Name));
        return pairs;
    }

    // Build one block's uncompressed payload: scanline-by-scanline, channel-by-channel.
    private static byte[] GatherBlock((ExrChannel ch, byte[] data)[] sorted, int width, int firstScan, int count)
    {
        int rowBytes = 0;
        foreach (var (ch, _) in sorted) rowBytes += width * ch.BytesPerSample;
        var raw = new byte[rowBytes * count];
        int p = 0;
        for (var s = 0; s < count; s++)
        {
            int row = firstScan + s;
            foreach (var (ch, chData) in sorted)
            {
                int n = width * ch.BytesPerSample;
                chData.AsSpan(row * n, n).CopyTo(raw.AsSpan(p, n));
                p += n;
            }
        }
        return raw;
    }
}

/// <summary>
/// Structure of one scanline block, needed by the structured compressors (PIZ
/// operates per channel × scanline rather than on opaque bytes).
/// </summary>
internal readonly record struct ExrBlockInfo(int Width, ExrChannel[] Channels)
{
    public int ScanlineCount { get; init; }
}
