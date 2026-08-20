using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace SharpAstro.Tiff;

/// <summary>
/// Pure-managed TIFF reader — the dual of <see cref="TiffWriter"/>. Reads
/// every IFD in chain order and decodes its strips into a single contiguous
/// byte buffer per page. <see cref="TiffPage.Pixels"/> is normalised to the
/// host's byte order: file-byte-order is detected from the "II" / "MM"
/// header and a final per-sample swap runs when (file order != host order)
/// so callers can re-interpret the bytes as <c>ushort</c> / <c>float</c>
/// with <c>MemoryMarshal.Cast</c> without further endian gymnastics.
///
/// Scope (v1):
/// <list type="bullet">
/// <item>Both byte orders: "II" (little-endian) and "MM" (big-endian). Most
///       astronomy TIFFs are II; some scanner / Photoshop output is MM.</item>
/// <item>Strip layout (no tile decoding yet — tiled TIFFs throw).</item>
/// <item>Bit depths 8, 16, 32 — uniform across all samples (per TIFF norm).</item>
/// <item>Compression: <see cref="TiffCompression.Uncompressed"/>, <see cref="TiffCompression.Lzw"/>,
///       <see cref="TiffCompression.Deflate"/>, <see cref="TiffCompression.ZlibPkzip"/>.</item>
/// <item>Sample formats: <see cref="TiffSampleFormat.Uint"/>, <see cref="TiffSampleFormat.IeeeFloat"/>.</item>
/// <item>Predictors: <see cref="TiffPredictor.None"/> and
///       <see cref="TiffPredictor.HorizontalDifferencing"/> -- the default for
///       every writer that emits ZIP compression. <see cref="TiffPredictor.FloatingPoint"/>
///       throws rather than decoding past it.</item>
/// <item>Contiguous planar config only (one sample per pixel position; chunky).</item>
/// </list>
/// JPEG / Tile / BigTIFF / planar-separate are out of scope — when
/// TianWen needs them, add them here (the existing code only loops over strips
/// and dispatches on compression).
/// </summary>
public static class TiffReader
{
    /// <summary>Decode every page from a TIFF in-memory.</summary>
    public static TiffDocument Read(ReadOnlySpan<byte> tiff)
    {
        var fileIsLE = ReadHeader(tiff);

        var sink = new BufferingSink();
        return ReadCore(tiff, fileIsLE, ref sink, attachPixels: true);
    }

    /// <summary>
    /// Decode a TIFF, handing each page's pixels to <paramref name="sink"/> strip by strip instead of
    /// assembling a raster. Returns the pages' METADATA, with
    /// <see cref="TiffPage.Pixels"/> empty on every one.
    /// </summary>
    /// <remarks>
    /// <para>This is the same machinery <see cref="Read(ReadOnlySpan{byte})"/> runs -- that overload is
    /// simply this one with a sink that concatenates. One path, so the streaming form cannot drift from
    /// the buffering form in predictor handling, endianness or strip geometry, and the existing
    /// round-trip tests cover both.</para>
    ///
    /// <para>Pair it with a memory-mapped file for the case it exists for: an uncompressed page then
    /// materialises neither the file bytes nor a raster, because the reader hands the sink a slice of
    /// the mapping. See <see cref="ITiffStripSink"/> for when that applies and when a scratch buffer is
    /// used instead.</para>
    /// </remarks>
    public static TiffDocument ReadInto<TSink>(ReadOnlySpan<byte> tiff, ref TSink sink)
        where TSink : ITiffStripSink
    {
        var fileIsLE = ReadHeader(tiff);
        return ReadCore(tiff, fileIsLE, ref sink, attachPixels: false);
    }

    private static TiffDocument ReadCore<TSink>(ReadOnlySpan<byte> tiff, bool fileIsLE, ref TSink sink,
        bool attachPixels)
        where TSink : ITiffStripSink
    {
        var pages = new List<TiffPage>();
        var ifdOffset = (int)ReadUInt32(tiff.Slice(4, 4), fileIsLE);
        var pageIndex = 0;
        while (ifdOffset != 0)
        {
            var (page, nextOffset) = ReadPage(tiff, ifdOffset, fileIsLE, pageIndex, ref sink);
            // Only the buffering path re-attaches pixels, and it is the sink that holds them: a
            // streaming caller has already consumed them and gets metadata alone.
            pages.Add(attachPixels && sink is BufferingSink buffered
                ? page with { Pixels = buffered.TakePixels() }
                : page);
            ifdOffset = nextOffset;
            pageIndex++;
        }
        return new TiffDocument(pages);
    }

    /// <summary>
    /// Validates the 8-byte header and reports the file's byte order. Split out so
    /// <see cref="ReadInto{TSink}"/> and <see cref="Read(ReadOnlySpan{byte})"/> cannot come to disagree
    /// about what a valid TIFF is.
    /// </summary>
    private static bool ReadHeader(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) throw new InvalidDataException("TIFF too small for header");

        // Detect byte order from header bytes 0-1. "II" = little-endian,
        // "MM" = big-endian (TIFF 6.0 §2). All multi-byte values in the file
        // — including the magic, the IFD offsets, every IFD entry, every
        // pixel sample of width > 8 bits — follow this same order.
        bool fileIsLE;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') fileIsLE = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') fileIsLE = false;
        else throw new InvalidDataException($"Unknown TIFF byte order tag {tiff[0]:X2}{tiff[1]:X2}");

        if (ReadUInt16(tiff.Slice(2, 2), fileIsLE) != 42)
            throw new InvalidDataException("TIFF magic mismatch");

        return fileIsLE;
    }

    /// <summary>
    /// The sink behind <see cref="Read(ReadOnlySpan{byte})"/>: concatenates a page's strips into the
    /// one contiguous buffer that overload has always returned.
    /// </summary>
    private sealed class BufferingSink : ITiffStripSink
    {
        private byte[] _pixels = [];
        private int _position;

        public bool BeginPage(int pageIndex, TiffPage description)
        {
            _pixels = new byte[checked(description.Width * description.Height
                * description.SamplesPerPixel * (description.BitsPerSample / 8))];
            _position = 0;
            return true;
        }

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
        {
            // Clamped, not trusted: a truncated file can present more bytes than the page's declared
            // dimensions hold, and this overload's contract is a buffer of exactly that size.
            var copy = Math.Min(samples.Length, _pixels.Length - _position);
            if (copy <= 0) return;
            samples[..copy].CopyTo(_pixels.AsSpan(_position, copy));
            _position += copy;
        }

        /// <summary>Hands the assembled buffer over and forgets it, so the next page starts clean.</summary>
        public byte[] TakePixels()
        {
            var pixels = _pixels;
            _pixels = [];
            _position = 0;
            return pixels;
        }
    }

    /// <summary>Decode every page from a TIFF stream (slurped to a byte array).</summary>
    public static TiffDocument Read(Stream stream)
    {
        // TIFF readers need random access via the IFD chain — slurp the stream
        // and operate on the in-memory span. For very large TIFFs the caller
        // should map the file (MemoryMappedFile) and pass that span instead.
        // Pre-size when the length is known: MemoryStream grows by doubling, so slurping a
        // 100 MB TIFF into a default-capacity stream reallocates a dozen times and copies
        // roughly twice the file before the first byte is ever decoded.
        var capacity = stream.CanSeek ? (int)Math.Min(int.MaxValue, stream.Length - stream.Position) : 0;
        using var ms = new MemoryStream(capacity);
        stream.CopyTo(ms);
        return Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    private static (TiffPage Page, int NextIfdOffset) ReadPage<TSink>(ReadOnlySpan<byte> tiff, int ifdOffset,
        bool fileIsLE, int pageIndex, ref TSink sink)
        where TSink : ITiffStripSink
    {
        // ---- Parse the IFD into a tag dictionary ---------------------------
        if (ifdOffset + 2 > tiff.Length) throw new InvalidDataException("IFD offset out of bounds");
        var entryCount = ReadUInt16(tiff.Slice(ifdOffset, 2), fileIsLE);
        const int entrySize = 12;

        var directoryEnd = ifdOffset + 2 + entryCount * entrySize;
        if (directoryEnd + 4 > tiff.Length) throw new InvalidDataException("IFD truncated");

        // ---- Required tags ------------------------------------------------
        var width = 0;
        var height = 0;
        var samplesPerPixel = 1;
        var bitsPerSample = 1;
        var compression = TiffCompression.Uncompressed;
        var photometric = TiffPhotometric.MinIsBlack;
        var sampleFormat = TiffSampleFormat.Uint;
        var planarConfig = TiffPlanarConfig.Contig;
        var predictor = TiffPredictor.None;
        var rowsPerStrip = 0;
        uint[]? stripOffsets = null;
        uint[]? stripByteCounts = null;
        float? sMin = null;
        float? sMax = null;
        byte[]? icc = null;
        int? exifIfdOffset = null;
        int? gpsIfdOffset = null;

        for (var i = 0; i < entryCount; i++)
        {
            var entryStart = ifdOffset + 2 + i * entrySize;
            var tag = ReadUInt16(tiff.Slice(entryStart, 2), fileIsLE);
            var type = (TiffFieldType)ReadUInt16(tiff.Slice(entryStart + 2, 2), fileIsLE);
            var count = (int)ReadUInt32(tiff.Slice(entryStart + 4, 4), fileIsLE);
            var valueSpan = tiff.Slice(entryStart + 8, 4);

            switch (tag)
            {
                case TiffTag.ImageWidth:
                    width = (int)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.ImageLength:
                    height = (int)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.SamplesPerPixel:
                    samplesPerPixel = (int)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.BitsPerSample:
                    bitsPerSample = (int)ReadShortArray(tiff, type, count, valueSpan, fileIsLE)[0];
                    break;
                case TiffTag.Compression:
                    compression = (TiffCompression)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.PhotometricInterp:
                    photometric = (TiffPhotometric)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.Predictor:
                    predictor = (TiffPredictor)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.PlanarConfig:
                    planarConfig = (TiffPlanarConfig)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.RowsPerStrip:
                    rowsPerStrip = (int)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.StripOffsets:
                    stripOffsets = ReadLongOrShortArray(tiff, type, count, valueSpan, fileIsLE);
                    break;
                case TiffTag.StripByteCounts:
                    stripByteCounts = ReadLongOrShortArray(tiff, type, count, valueSpan, fileIsLE);
                    break;
                case TiffTag.SampleFormat:
                    sampleFormat = (TiffSampleFormat)ReadShortArray(tiff, type, count, valueSpan, fileIsLE)[0];
                    break;
                case TiffTag.SMinSampleValue:
                    sMin = ReadFloatArray(tiff, type, count, valueSpan, fileIsLE)[0];
                    break;
                case TiffTag.SMaxSampleValue:
                    sMax = ReadFloatArray(tiff, type, count, valueSpan, fileIsLE)[0];
                    break;
                case TiffTag.IccProfile:
                    icc = ReadByteArray(tiff, type, count, valueSpan, fileIsLE);
                    break;
                case TiffTag.ExifIfd:
                    // Sub-IFD pointer — capture the offset so a caller can pass
                    // it to SharpAstro.Exif.ExifReader.FromIfd without re-walking.
                    exifIfdOffset = (int)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.GpsInfoIfd:
                    gpsIfdOffset = (int)ReadScalar(type, valueSpan, fileIsLE);
                    break;
                default:
                    // Unrecognised tag — TIFF spec says skip unknown tags.
                    break;
            }
        }

        var nextIfdOffset = (int)ReadUInt32(tiff.Slice(directoryEnd, 4), fileIsLE);

        // ---- Validate the layout we're willing to handle ------------------
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("ImageWidth/ImageLength missing or invalid");
        if (planarConfig != TiffPlanarConfig.Contig)
            throw new NotSupportedException("Only PlanarConfig=Contig is supported");
        if (stripOffsets is null || stripByteCounts is null)
            throw new NotSupportedException("Tiled or stripless TIFFs are not supported in this reader");
        if (stripOffsets.Length != stripByteCounts.Length)
            throw new InvalidDataException("StripOffsets/StripByteCounts length mismatch");
        if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 32)
            throw new NotSupportedException($"BitsPerSample={bitsPerSample} not supported (expected 8/16/32)");
        if (sampleFormat is not (TiffSampleFormat.Uint or TiffSampleFormat.Int or TiffSampleFormat.IeeeFloat))
            throw new NotSupportedException($"SampleFormat={sampleFormat} not supported");
        // Refuse an unhandled predictor rather than decoding past it. An un-inverted predictor
        // yields a full-size, entirely plausible buffer of WRONG pixels -- no exception, no
        // short read, correct dimensions -- so staying quiet here is indistinguishable from
        // success to every caller downstream.
        if (predictor is not (TiffPredictor.None or TiffPredictor.HorizontalDifferencing))
            throw new NotSupportedException($"Predictor={predictor} not supported in this reader");

        // ---- Decode strips, normalise each, hand it to the sink -----------
        var bytesPerPixel = samplesPerPixel * (bitsPerSample / 8);
        var bytesPerRow = width * bytesPerPixel;

        // The page is fully described before a single pixel is decoded, which is the whole point of the
        // sink: a caller sizes its own destination from this and never needs a raster. Pixels stay empty
        // here; the buffering sink re-attaches its concatenated buffer in ReadCore.
        var page = new TiffPage(
            Width: width,
            Height: height,
            SamplesPerPixel: samplesPerPixel,
            BitsPerSample: bitsPerSample,
            Photometric: photometric,
            SampleFormat: sampleFormat,
            Compression: compression,
            RowsPerStrip: rowsPerStrip,
            Pixels: [],
            SMinSampleValue: sMin,
            SMaxSampleValue: sMax,
            IccProfile: icc,
            ExifIfdOffset: exifIfdOffset,
            GpsInfoIfdOffset: gpsIfdOffset,
            FileIsLittleEndian: fileIsLE);

        if (!sink.BeginPage(pageIndex, page))
        {
            // Declined: none of this page strips are touched. That is what makes reading page 0 of a
            // multi-page file cost only page 0.
            return (page, nextIfdOffset);
        }

        // RowsPerStrip defaults to the whole image in one strip (2^32-1 per TIFF 6.0), and a file may
        // omit it, so a missing or absurd value becomes the image height rather than a zero step.
        var rowsPerStripEffective = rowsPerStrip > 0 && rowsPerStrip <= height ? rowsPerStrip : height;

        // 8-bit samples have no byte order, and neither has a file that already matches the host --
        // which in practice is nearly every file, since writers emit II and hosts are little-endian.
        var needsSwap = fileIsLE != BitConverter.IsLittleEndian && bitsPerSample > 8;
        var needsPredictor = predictor == TiffPredictor.HorizontalDifferencing;

        // THE FAST CASE, and the reason ReadInto exists: nothing to decompress and nothing to rewrite,
        // so the sink gets a slice of the INPUT and no scratch is allocated at all. Reading a
        // memory-mapped uncompressed TIFF this way materialises neither the file bytes nor a raster.
        // Both guards are about mutation -- normalising would have to write, and a mapped view is
        // read-only.
        var passFileBytesThrough = compression is TiffCompression.Uncompressed && !needsSwap && !needsPredictor;

        // ONE scratch buffer for every strip, plus ONE MemoryStream for the inflate path -- not one of
        // each per strip. A writer emitting ZIP almost always emits RowsPerStrip=1, so a real file is
        // thousands of strips: measured over a corpus of 16-bit RGB frames, 2,009 to 5,529 of them.
        // That was thousands of short-lived arrays plus thousands of streams per decode.
        byte[]? scratch = passFileBytesThrough
            ? null
            : ArrayPool<byte>.Shared.Rent(checked(rowsPerStripEffective * bytesPerRow));
        byte[]? inflateSource = null;
        MemoryStream? scratchStream = null;
        if (compression is TiffCompression.Deflate or TiffCompression.ZlibPkzip)
        {
            var maxStrip = 0;
            foreach (var byteCount in stripByteCounts)
                maxStrip = Math.Max(maxStrip, (int)byteCount);
            inflateSource = ArrayPool<byte>.Shared.Rent(maxStrip);
            // writable + publiclyVisible so each strip is presented by moving Length/Position
            // rather than by constructing a new stream over a new array.
            scratchStream = new MemoryStream(inflateSource, 0, inflateSource.Length, writable: true,
                publiclyVisible: true);
        }

        try
        {
            var rowCursor = 0;
            for (var i = 0; i < stripOffsets.Length && rowCursor < height; i++)
            {
                var stripStart = (int)stripOffsets[i];
                var stripLen = (int)stripByteCounts[i];
                if (stripStart < 0 || stripLen < 0 || stripStart + stripLen > tiff.Length)
                    throw new InvalidDataException($"Strip {i} extents out of bounds");
                var stripSpan = tiff.Slice(stripStart, stripLen);

                var rowCount = Math.Min(rowsPerStripEffective, height - rowCursor);
                var capacity = rowCount * bytesPerRow;
                int decoded;
                ReadOnlySpan<byte> samples;

                switch (compression)
                {
                    case TiffCompression.Uncompressed when passFileBytesThrough:
                        decoded = Math.Min(stripSpan.Length, capacity);
                        samples = stripSpan[..decoded];
                        break;
                    case TiffCompression.Uncompressed:
                        decoded = Math.Min(stripSpan.Length, capacity);
                        stripSpan[..decoded].CopyTo(scratch!.AsSpan());
                        samples = scratch.AsSpan(0, decoded);
                        break;
                    case TiffCompression.Lzw:
                        // Decoded straight from the file span -- LZW needs no zlib stream, so it needs
                        // neither the inflate source buffer nor the MemoryStream.
                        decoded = TiffLzw.Decode(stripSpan, scratch!.AsSpan(0, capacity));
                        samples = scratch.AsSpan(0, decoded);
                        break;
                    case TiffCompression.Deflate:
                    case TiffCompression.ZlibPkzip:
                        // SetLength BEFORE the copy, never after: MemoryStream ZERO-FILLS when it
                        // grows, so setting the length second wipes the tail of any strip longer
                        // than the previous one. That is silent and data-dependent -- strips of a
                        // smooth image all deflate to near-identical sizes and never grow, so it
                        // only bites once the content compresses unevenly.
                        scratchStream!.SetLength(stripLen);
                        stripSpan.CopyTo(inflateSource!.AsSpan());
                        scratchStream.Position = 0;
                        decoded = InflateInto(scratchStream, scratch!.AsSpan(0, capacity));
                        samples = scratch.AsSpan(0, decoded);
                        break;
                    default:
                        throw new NotSupportedException($"Compression {compression} not supported in this reader");
                }

                // Normalise this strip, not the assembled image. Both passes are row-local -- the swap
                // is per sample, and differencing restarts at every row (TIFF 6.0 section 14) -- and
                // strips hold whole rows, so a strip seam is never a special case and the result is
                // identical to running them once over a whole raster.
                if (needsSwap)
                    SwapPixelsToHostOrder(scratch!.AsSpan(0, decoded), bitsPerSample);
                if (needsPredictor)
                    UndoHorizontalDifferencing(scratch!.AsSpan(0, decoded), width, samplesPerPixel, bitsPerSample);

                sink.Strip(pageIndex, rowCursor, rowCount, samples);
                rowCursor += rowCount;
            }
        }
        finally
        {
            scratchStream?.Dispose();
            if (inflateSource is not null)
                ArrayPool<byte>.Shared.Return(inflateSource);
            if (scratch is not null)
                ArrayPool<byte>.Shared.Return(scratch);
        }

        return (page, nextIfdOffset);
    }

    /// <summary>
    /// Invert TIFF Predictor 2 (horizontal differencing, TIFF 6.0 section 14): each stored sample
    /// is the difference from the sample one pixel to its left in the same row and the same
    /// channel, so recovering the image is a running sum along each row, per channel.
    ///
    /// Differencing restarts at every row, which is what lets this run over the assembled buffer
    /// instead of per strip: strips hold whole rows, so the row boundaries are identical either
    /// way and a strip seam is never a special case.
    ///
    /// The addition WRAPS at the sample width on purpose -- the encoder computed the differences
    /// with the same wrap, so unchecked addition is exactly what reverses it. A trailing partial
    /// row (truncated file) is left alone rather than half-summed.
    /// </summary>
    private static void UndoHorizontalDifferencing(Span<byte> pixels, int width, int samplesPerPixel, int bitsPerSample)
    {
        var stride = width * samplesPerPixel;   // samples per row, every channel interleaved
        if (stride <= 0 || samplesPerPixel <= 0) return;

        switch (bitsPerSample)
        {
            case 8:
                AccumulateBytes(pixels, stride, samplesPerPixel);
                break;
            case 16:
                AccumulateUInt16(pixels, stride, samplesPerPixel);
                break;
            case 32:
                AccumulateUInt32(pixels, stride, samplesPerPixel);
                break;
        }
    }

    private static void AccumulateBytes(Span<byte> pixels, int stride, int samplesPerPixel)
    {
        var rows = pixels.Length / stride;
        for (var y = 0; y < rows; y++)
        {
            var row = pixels.Slice(y * stride, stride);
            for (var i = samplesPerPixel; i < row.Length; i++)
                row[i] = (byte)(row[i] + row[i - samplesPerPixel]);
        }
    }

    private static void AccumulateUInt16(Span<byte> pixels, int stride, int samplesPerPixel)
    {
        var samples = MemoryMarshal.Cast<byte, ushort>(pixels);
        var rows = samples.Length / stride;
        for (var y = 0; y < rows; y++)
        {
            var row = samples.Slice(y * stride, stride);
            for (var i = samplesPerPixel; i < row.Length; i++)
                row[i] = (ushort)(row[i] + row[i - samplesPerPixel]);
        }
    }

    /// <summary>
    /// 32-bit samples are summed as uint even when the page is IEEE float: Predictor 2 is defined
    /// on integer samples, and float data is what <see cref="TiffPredictor.FloatingPoint"/>
    /// exists for. An encoder that pairs float samples with Predictor 2 therefore differenced the
    /// raw bit patterns as integers, and this reverses exactly that.
    /// </summary>
    private static void AccumulateUInt32(Span<byte> pixels, int stride, int samplesPerPixel)
    {
        var samples = MemoryMarshal.Cast<byte, uint>(pixels);
        var rows = samples.Length / stride;
        for (var y = 0; y < rows; y++)
        {
            var row = samples.Slice(y * stride, stride);
            for (var i = samplesPerPixel; i < row.Length; i++)
                row[i] += row[i - samplesPerPixel];
        }
    }

    /// <summary>
    /// In-place per-sample byte-reverse. Float32 is treated as a 32-bit blob
    /// because reversing the 4 raw bytes of an IEEE-754 number gives back the
    /// other-endian IEEE-754 representation of the same value.
    /// </summary>
    private static void SwapPixelsToHostOrder(Span<byte> pixels, int bitsPerSample)
    {
        switch (bitsPerSample)
        {
            case 16:
                var asU16 = MemoryMarshal.Cast<byte, ushort>(pixels);
                for (var i = 0; i < asU16.Length; i++)
                    asU16[i] = BinaryPrimitives.ReverseEndianness(asU16[i]);
                break;
            case 32:
                var asU32 = MemoryMarshal.Cast<byte, uint>(pixels);
                for (var i = 0; i < asU32.Length; i++)
                    asU32[i] = BinaryPrimitives.ReverseEndianness(asU32[i]);
                break;
            // 8-bit: byte order is irrelevant.
        }
    }

    /// <summary>
    /// Inflate one zlib-wrapped strip from <paramref name="src"/> into the destination span,
    /// returning the number of bytes written. ZLibStream is forgiving of trailing zero
    /// padding the writer may have left after the deflate trailer (none for
    /// our writer, but some encoders pad strips up to the row boundary).
    /// </summary>
    private static int InflateInto(Stream src, Span<byte> dst)
    {
        // leaveOpen is required: the source is the caller's reused scratch stream. Each strip
        // IS an independent zlib stream and so needs its own ZLibStream -- that one remaining
        // per-strip allocation is inherent to the format, not to this code -- but disposing it
        // must not take the shared source with it.
        using var z = new ZLibStream(src, CompressionMode.Decompress, leaveOpen: true);
        var written = 0;
        while (written < dst.Length)
        {
            var n = z.Read(dst.Slice(written));
            if (n == 0) break;
            written += n;
        }
        return written;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static float ReadSingle(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
        : BinaryPrimitives.ReadSingleBigEndian(bytes);

    private static uint ReadScalar(TiffFieldType type, ReadOnlySpan<byte> valueOrOffset, bool fileIsLE) => type switch
    {
        TiffFieldType.Byte  => valueOrOffset[0],
        TiffFieldType.Short => ReadUInt16(valueOrOffset.Slice(0, 2), fileIsLE),
        TiffFieldType.Long  => ReadUInt32(valueOrOffset.Slice(0, 4), fileIsLE),
        _ => throw new NotSupportedException($"Scalar tag has unexpected type {type}"),
    };

    private static ushort[] ReadShortArray(ReadOnlySpan<byte> tiff, TiffFieldType type, int count, ReadOnlySpan<byte> valueSpan, bool fileIsLE)
    {
        if (type != TiffFieldType.Short)
            throw new NotSupportedException($"Expected SHORT, got {type}");
        var totalBytes = count * 2;
        var data = totalBytes <= 4
            ? valueSpan.Slice(0, totalBytes)
            : tiff.Slice((int)ReadUInt32(valueSpan, fileIsLE), totalBytes);
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadUInt16(data.Slice(i * 2, 2), fileIsLE);
        return result;
    }

    private static uint[] ReadLongOrShortArray(ReadOnlySpan<byte> tiff, TiffFieldType type, int count, ReadOnlySpan<byte> valueSpan, bool fileIsLE)
    {
        var elemSize = type switch
        {
            TiffFieldType.Short => 2,
            TiffFieldType.Long  => 4,
            _ => throw new NotSupportedException($"Expected SHORT/LONG, got {type}"),
        };
        var totalBytes = count * elemSize;
        var data = totalBytes <= 4
            ? valueSpan.Slice(0, totalBytes)
            : tiff.Slice((int)ReadUInt32(valueSpan, fileIsLE), totalBytes);
        var result = new uint[count];
        for (var i = 0; i < count; i++)
            result[i] = elemSize == 2
                ? ReadUInt16(data.Slice(i * 2, 2), fileIsLE)
                : ReadUInt32(data.Slice(i * 4, 4), fileIsLE);
        return result;
    }

    private static float[] ReadFloatArray(ReadOnlySpan<byte> tiff, TiffFieldType type, int count, ReadOnlySpan<byte> valueSpan, bool fileIsLE)
    {
        if (type != TiffFieldType.Float)
            throw new NotSupportedException($"Expected FLOAT, got {type}");
        var totalBytes = count * 4;
        var data = totalBytes <= 4
            ? valueSpan.Slice(0, totalBytes)
            : tiff.Slice((int)ReadUInt32(valueSpan, fileIsLE), totalBytes);
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadSingle(data.Slice(i * 4, 4), fileIsLE);
        return result;
    }

    private static byte[] ReadByteArray(ReadOnlySpan<byte> tiff, TiffFieldType type, int count, ReadOnlySpan<byte> valueSpan, bool fileIsLE)
    {
        if (type != TiffFieldType.Undefined && type != TiffFieldType.Byte)
            throw new NotSupportedException($"Expected UNDEFINED/BYTE, got {type}");
        var data = count <= 4
            ? valueSpan.Slice(0, count)
            : tiff.Slice((int)ReadUInt32(valueSpan, fileIsLE), count);
        return data.ToArray();
    }
}

/// <summary>
/// Top-level result of a TIFF decode: every IFD in chain order, each as a
/// <see cref="TiffPage"/>. For single-page TIFFs (typical) this list has
/// length 1.
/// </summary>
public sealed record TiffDocument(IReadOnlyList<TiffPage> Pages);

/// <summary>
/// One decoded IFD plus its strip-concatenated pixel buffer.
/// <see cref="Pixels"/> is laid out in row-major contiguous order with each
/// sample in the *host's* byte order — the reader already swapped any
/// file-byte-order mismatch (e.g. MM file on an LE host) — so callers on
/// x64/arm64 can reinterpret-cast it as <c>ushort[]</c> / <c>float[]</c>
/// with <c>System.Runtime.InteropServices.MemoryMarshal.Cast</c> for
/// zero-copy access.
/// </summary>
public sealed record TiffPage(
    int Width,
    int Height,
    int SamplesPerPixel,
    int BitsPerSample,
    TiffPhotometric Photometric,
    TiffSampleFormat SampleFormat,
    TiffCompression Compression,
    int RowsPerStrip,
    byte[] Pixels,
    float? SMinSampleValue,
    float? SMaxSampleValue,
    byte[]? IccProfile,
    int? ExifIfdOffset,
    int? GpsInfoIfdOffset,
    bool FileIsLittleEndian);
