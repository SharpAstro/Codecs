using System.Buffers.Binary;
using SharpAstro.Tiff;

namespace SharpAstro.Jxr;

/// <summary>
/// Read/write the JPEG XR tag-based file format (T.832 Annex A). The container
/// is little-endian throughout — no MM byte order — with the fixed 8-byte
/// header <c>49 49 BC 01 &lt;FIRST_IFD_OFFSET:le32&gt;</c> followed by one or more
/// IFDs whose entries describe the image metadata and point at the primary
/// codestream (and optionally a separate alpha codestream).
/// </summary>
/// <remarks>
/// This type treats the codestream itself as an opaque blob. Phase 1 of
/// SharpAstro.Jxr only round-trips the container — the T.832 bitstream
/// encoder/decoder land in later phases.
///
/// The IFD ELEMENT_TYPE enum is numerically identical to TIFF 6.0's
/// <see cref="TiffFieldType"/>, so the same type is reused for parsing.
/// </remarks>
public static class JxrContainer
{
    private const ushort SignatureBytes = 0x4949; // "II"
    private const byte HeaderByte0xBC = 0xBC;
    private const byte FileVersionId = 0x01;

    // -----------------------------------------------------------------------
    // Reader
    // -----------------------------------------------------------------------

    public static JxrFile Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8) throw new InvalidDataException("JXR too small for FILE_HEADER");
        if (BinaryPrimitives.ReadUInt16LittleEndian(file) != SignatureBytes)
            throw new InvalidDataException("JXR signature ('II') mismatch");
        if (file[2] != HeaderByte0xBC)
            throw new InvalidDataException($"JXR fixed header byte mismatch: expected 0xBC, got 0x{file[2]:X2}");
        if (file[3] != FileVersionId)
            throw new InvalidDataException($"Unsupported JXR FILE_VERSION_ID: {file[3]} (expected 1)");

        var firstIfdOffset = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(4, 4));
        var ifd = ParseIfd(file, firstIfdOffset);

        // ---- Required tags -------------------------------------------------
        if (!TryGetScalarUInt32(file, ifd, JxrTag.ImageWidth, out var width))
            throw new InvalidDataException("Required IMAGE_WIDTH tag missing");
        if (!TryGetScalarUInt32(file, ifd, JxrTag.ImageHeight, out var height))
            throw new InvalidDataException("Required IMAGE_HEIGHT tag missing");
        if (!TryGetScalarUInt32(file, ifd, JxrTag.ImageOffset, out var imageOffset))
            throw new InvalidDataException("Required IMAGE_OFFSET tag missing");
        if (!TryGetScalarUInt32(file, ifd, JxrTag.ImageByteCount, out var imageByteCount))
            throw new InvalidDataException("Required IMAGE_BYTE_COUNT tag missing");

        var pixelFormatBytes = GetBytes(file, ifd, JxrTag.PixelFormat, expectedLength: 16)
            ?? throw new InvalidDataException("Required PIXEL_FORMAT tag missing");
        var pixelFormat = new JxrPixelFormat(pixelFormatBytes);

        if (imageOffset + (long)imageByteCount > file.Length)
            throw new InvalidDataException("Primary codestream extents out of file bounds");
        var codestream = file.Slice((int)imageOffset, (int)imageByteCount).ToArray();

        byte[]? alphaCodestream = null;
        if (TryGetScalarUInt32(file, ifd, JxrTag.AlphaOffset, out var alphaOffset) &&
            TryGetScalarUInt32(file, ifd, JxrTag.AlphaByteCount, out var alphaByteCount))
        {
            if (alphaOffset + (long)alphaByteCount > file.Length)
                throw new InvalidDataException("Alpha codestream extents out of file bounds");
            alphaCodestream = file.Slice((int)alphaOffset, (int)alphaByteCount).ToArray();
        }

        // ---- Optional tags -------------------------------------------------
        uint? spatialXfrm = TryGetScalarUInt32(file, ifd, JxrTag.SpatialXfrmPrimary, out var s) ? s : null;
        float? widthRes = TryGetScalarFloat(file, ifd, JxrTag.WidthResolution, out var wr) ? wr : null;
        float? heightRes = TryGetScalarFloat(file, ifd, JxrTag.HeightResolution, out var hr) ? hr : null;

        var icc = GetBytes(file, ifd, JxrTag.IccProfile, expectedLength: null);
        var xmp = GetBytes(file, ifd, JxrTag.XmpMetadata, expectedLength: null);

        return new JxrFile(
            Width: width,
            Height: height,
            PixelFormat: pixelFormat,
            Codestream: codestream,
            AlphaCodestream: alphaCodestream,
            SpatialXfrmPrimary: spatialXfrm,
            WidthResolution: widthRes,
            HeightResolution: heightRes,
            IccProfile: icc,
            XmpMetadata: xmp);
    }

    public static JxrFile Read(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    // -----------------------------------------------------------------------
    // Writer
    // -----------------------------------------------------------------------

    /// <summary>
    /// Serialise <paramref name="file"/> as a complete .jxr byte stream.
    /// The IFD lists tags in ascending order (T.832 A.7.2) and the codestream(s)
    /// follow the IFD and any out-of-line metadata blobs.
    /// </summary>
    public static byte[] Write(JxrFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(file.Codestream);

        // ---- Plan IFD entries in tag-ascending order ----------------------
        // Each entry is either inline (value fits in 4 bytes) or refers to a
        // blob we'll emit after the IFD. PixelFormat is always out-of-line
        // (16 bytes). Codestream offsets are computed once the layout is final.
        var entries = new List<PlannedEntry>();

        // 0x02BC XMP metadata
        if (file.XmpMetadata is { Length: > 0 } xmp)
            entries.Add(PlannedEntry.OutOfLine(JxrTag.XmpMetadata, TiffFieldType.Byte, xmp.Length, xmp));

        // 0x8773 ICC profile
        if (file.IccProfile is { Length: > 0 } icc)
            entries.Add(PlannedEntry.OutOfLine(JxrTag.IccProfile, TiffFieldType.Undefined, icc.Length, icc));

        // 0xBC01 PixelFormat (always 16 bytes → out-of-line)
        var pixelFormatBytes = file.PixelFormat.ToArray();
        entries.Add(PlannedEntry.OutOfLine(JxrTag.PixelFormat, TiffFieldType.Byte, 16, pixelFormatBytes));

        // 0xBC02 SpatialXfrmPrimary (inline ULONG, range 0..7). Emit 0 by
        // default — Microsoft's WIC WMPhoto decoder treats this tag as
        // required for frame instantiation; without it BitmapDecoder.Frames
        // returns an empty collection even though the file otherwise parses.
        entries.Add(PlannedEntry.InlineUInt32(JxrTag.SpatialXfrmPrimary, file.SpatialXfrmPrimary ?? 0u));

        // 0xBC04 ImageType (inline ULONG). Per T.832 §A.7.4 the bit layout is
        // 0=normal/1=preview in bit 0 and profile/level info in higher bits;
        // value 0 means "normal full image". WIC's own JXR encoder writes
        // this tag with value 0 and the decoder appears to refuse to expose
        // a frame if it's missing.
        entries.Add(PlannedEntry.InlineUInt32(JxrTag.ImageType, 0u));

        // 0xBC80 ImageWidth, 0xBC81 ImageHeight (inline ULONG)
        entries.Add(PlannedEntry.InlineUInt32(JxrTag.ImageWidth, file.Width));
        entries.Add(PlannedEntry.InlineUInt32(JxrTag.ImageHeight, file.Height));

        // 0xBC82 / 0xBC83 resolution (inline FLOAT). Default to 96 dpi
        // (Windows convention) when not supplied — WIC reportedly fails to
        // instantiate a frame without these. Callers can override via
        // JxrFile.WidthResolution / HeightResolution.
        entries.Add(PlannedEntry.InlineFloat(JxrTag.WidthResolution,  file.WidthResolution  ?? 96f));
        entries.Add(PlannedEntry.InlineFloat(JxrTag.HeightResolution, file.HeightResolution ?? 96f));

        // 0xBCC0 IMAGE_OFFSET, 0xBCC1 IMAGE_BYTE_COUNT — offset patched after layout
        var imageOffsetEntryIndex = entries.Count;
        entries.Add(PlannedEntry.InlineUInt32(JxrTag.ImageOffset, 0));
        entries.Add(PlannedEntry.InlineUInt32(JxrTag.ImageByteCount, (uint)file.Codestream.Length));

        // 0xBCC2 ALPHA_OFFSET, 0xBCC3 ALPHA_BYTE_COUNT — offset patched after layout
        int alphaOffsetEntryIndex = -1;
        if (file.AlphaCodestream is { Length: > 0 } alpha)
        {
            alphaOffsetEntryIndex = entries.Count;
            entries.Add(PlannedEntry.InlineUInt32(JxrTag.AlphaOffset, 0));
            entries.Add(PlannedEntry.InlineUInt32(JxrTag.AlphaByteCount, (uint)alpha.Length));
        }

        // ---- Compute file layout ------------------------------------------
        // Layout: [header 8] [IFD: 2 + N*12 + 4] [out-of-line metadata blobs aligned to 2]
        //         [primary codestream aligned to 2] [alpha codestream aligned to 2]
        var ifdSize = 2 + entries.Count * 12 + 4;
        var cursor = AlignUp(8 + ifdSize);

        foreach (var e in entries.Where(e => e.OutOfLineData is not null))
        {
            e.OutOfLineOffset = cursor;
            cursor = AlignUp(cursor + e.OutOfLineData!.Length);
        }

        var primaryCodestreamOffset = cursor;
        cursor = AlignUp(cursor + file.Codestream.Length);

        var alphaCodestreamOffset = -1;
        if (file.AlphaCodestream is { Length: > 0 } alphaCs)
        {
            alphaCodestreamOffset = cursor;
            cursor = AlignUp(cursor + alphaCs.Length);
        }

        // Patch the IMAGE_OFFSET / ALPHA_OFFSET entries now that we know their values.
        entries[imageOffsetEntryIndex] =
            PlannedEntry.InlineUInt32(JxrTag.ImageOffset, (uint)primaryCodestreamOffset);
        if (alphaOffsetEntryIndex >= 0)
            entries[alphaOffsetEntryIndex] =
                PlannedEntry.InlineUInt32(JxrTag.AlphaOffset, (uint)alphaCodestreamOffset);

        // Verify ascending tag order (planned in order; double-check the spec invariant).
        for (var i = 1; i < entries.Count; i++)
            if (entries[i].Tag <= entries[i - 1].Tag)
                throw new InvalidOperationException($"IFD entries not in ascending FIELD_TAG order: {entries[i - 1].Tag:X4} >= {entries[i].Tag:X4}");

        // ---- Emit the bytes ------------------------------------------------
        var output = new byte[cursor];
        var span = output.AsSpan();

        // File header
        BinaryPrimitives.WriteUInt16LittleEndian(span, SignatureBytes);
        span[2] = HeaderByte0xBC;
        span[3] = FileVersionId;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), 8);

        // IFD at offset 8
        var ifdSpan = span.Slice(8);
        BinaryPrimitives.WriteUInt16LittleEndian(ifdSpan, (ushort)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entrySpan = ifdSpan.Slice(2 + i * 12, 12);
            var e = entries[i];
            BinaryPrimitives.WriteUInt16LittleEndian(entrySpan, e.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entrySpan.Slice(2, 2), (ushort)e.ElementType);
            BinaryPrimitives.WriteUInt32LittleEndian(entrySpan.Slice(4, 4), (uint)e.NumElements);
            if (e.OutOfLineData is not null)
                BinaryPrimitives.WriteUInt32LittleEndian(entrySpan.Slice(8, 4), (uint)e.OutOfLineOffset);
            else
                e.InlineBytes.AsSpan(0, 4).CopyTo(entrySpan.Slice(8, 4));
        }
        // ZERO_OR_NEXT_IFD_OFFSET = 0 (single IFD)
        BinaryPrimitives.WriteUInt32LittleEndian(ifdSpan.Slice(2 + entries.Count * 12, 4), 0);

        // Out-of-line metadata blobs
        foreach (var e in entries)
        {
            if (e.OutOfLineData is null) continue;
            e.OutOfLineData.CopyTo(span.Slice(e.OutOfLineOffset, e.OutOfLineData.Length));
        }

        // Codestreams
        file.Codestream.CopyTo(span.Slice(primaryCodestreamOffset, file.Codestream.Length));
        if (file.AlphaCodestream is { Length: > 0 } alphaCs2)
            alphaCs2.CopyTo(span.Slice(alphaCodestreamOffset, alphaCs2.Length));

        return output;
    }

    public static void Write(JxrFile file, Stream output)
    {
        var bytes = Write(file);
        output.Write(bytes, 0, bytes.Length);
    }

    // -----------------------------------------------------------------------
    // IFD parsing helpers
    // -----------------------------------------------------------------------

    private readonly record struct RawIfdEntry(ushort Tag, TiffFieldType Type, uint NumElements, uint ValueOrOffset);

    private static List<RawIfdEntry> ParseIfd(ReadOnlySpan<byte> file, uint ifdOffset)
    {
        if (ifdOffset + 2 > file.Length) throw new InvalidDataException("IFD offset out of bounds");
        var numEntries = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice((int)ifdOffset, 2));
        if (numEntries == 0) throw new InvalidDataException("IFD has NUM_ENTRIES = 0 (reserved)");

        var entriesEnd = (int)ifdOffset + 2 + numEntries * 12;
        if (entriesEnd + 4 > file.Length) throw new InvalidDataException("IFD truncated");

        var entries = new List<RawIfdEntry>(numEntries);
        ushort prevTag = 0;
        for (var i = 0; i < numEntries; i++)
        {
            var entrySpan = file.Slice((int)ifdOffset + 2 + i * 12, 12);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(entrySpan);
            if (i > 0 && tag <= prevTag)
                throw new InvalidDataException($"IFD entries not in ascending order: {prevTag:X4} then {tag:X4}");
            prevTag = tag;
            var type = (TiffFieldType)BinaryPrimitives.ReadUInt16LittleEndian(entrySpan.Slice(2, 2));
            var num = BinaryPrimitives.ReadUInt32LittleEndian(entrySpan.Slice(4, 4));
            var valOrOff = BinaryPrimitives.ReadUInt32LittleEndian(entrySpan.Slice(8, 4));
            entries.Add(new RawIfdEntry(tag, type, num, valOrOff));
        }
        return entries;
    }

    private static bool TryGetScalarUInt32(ReadOnlySpan<byte> file, List<RawIfdEntry> ifd, ushort tag, out uint value)
    {
        foreach (var e in ifd)
        {
            if (e.Tag != tag) continue;
            if (e.NumElements != 1)
                throw new InvalidDataException($"Tag 0x{tag:X4}: expected scalar, got NUM_ELEMENTS={e.NumElements}");
            value = e.Type switch
            {
                TiffFieldType.Byte => (byte)(e.ValueOrOffset & 0xFF),
                TiffFieldType.Short => (ushort)(e.ValueOrOffset & 0xFFFF),
                TiffFieldType.Long => e.ValueOrOffset,
                _ => throw new InvalidDataException($"Tag 0x{tag:X4}: unsupported ELEMENT_TYPE {e.Type} for unsigned scalar"),
            };
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryGetScalarFloat(ReadOnlySpan<byte> file, List<RawIfdEntry> ifd, ushort tag, out float value)
    {
        foreach (var e in ifd)
        {
            if (e.Tag != tag) continue;
            if (e.Type != TiffFieldType.Float || e.NumElements != 1)
                throw new InvalidDataException($"Tag 0x{tag:X4}: expected FLOAT scalar, got {e.Type} x{e.NumElements}");
            // FLOAT is inline (4 bytes ≤ 4) — value is stored in VALUES_OR_OFFSET little-endian.
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, e.ValueOrOffset);
            value = BinaryPrimitives.ReadSingleLittleEndian(tmp);
            return true;
        }
        value = 0f;
        return false;
    }

    private static byte[]? GetBytes(ReadOnlySpan<byte> file, List<RawIfdEntry> ifd, ushort tag, int? expectedLength)
    {
        foreach (var e in ifd)
        {
            if (e.Tag != tag) continue;
            if (expectedLength is int exp && e.NumElements != exp)
                throw new InvalidDataException($"Tag 0x{tag:X4}: expected {exp} elements, got {e.NumElements}");
            // BYTE/UNDEFINED — 1 byte per element. Inline if total ≤ 4, else file offset.
            var total = (int)e.NumElements;
            if (total <= 4)
            {
                Span<byte> tmp = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(tmp, e.ValueOrOffset);
                return tmp.Slice(0, total).ToArray();
            }
            if (e.ValueOrOffset + (long)total > file.Length)
                throw new InvalidDataException($"Tag 0x{tag:X4}: payload extents out of file");
            return file.Slice((int)e.ValueOrOffset, total).ToArray();
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Writer helpers
    // -----------------------------------------------------------------------

    /// <summary>Round <paramref name="x"/> up to the next multiple of 2 (the JXR spec
    /// requires every offset to be a multiple of 2).</summary>
    private static int AlignUp(int x) => (x + 1) & ~1;

    private sealed class PlannedEntry
    {
        public ushort Tag;
        public TiffFieldType ElementType;
        public int NumElements;
        public byte[] InlineBytes = new byte[4]; // padded with zeros when value < 4 bytes
        public byte[]? OutOfLineData;
        public int OutOfLineOffset;

        public static PlannedEntry InlineUInt32(ushort tag, uint value)
        {
            var e = new PlannedEntry { Tag = tag, ElementType = TiffFieldType.Long, NumElements = 1 };
            BinaryPrimitives.WriteUInt32LittleEndian(e.InlineBytes, value);
            return e;
        }

        public static PlannedEntry InlineFloat(ushort tag, float value)
        {
            var e = new PlannedEntry { Tag = tag, ElementType = TiffFieldType.Float, NumElements = 1 };
            BinaryPrimitives.WriteSingleLittleEndian(e.InlineBytes, value);
            return e;
        }

        public static PlannedEntry OutOfLine(ushort tag, TiffFieldType type, int numElements, byte[] data)
        {
            // T.832 A.7.5: if total ≤ 4 bytes, VALUES_OR_OFFSET holds the data inline,
            // not a file offset. Caller passes BYTE/UNDEFINED payloads (1 byte each) so
            // numElements == data.Length here; we assume that for the inline path.
            if (data.Length != numElements)
                throw new ArgumentException($"OutOfLine data length {data.Length} != numElements {numElements}");
            if (data.Length <= 4)
            {
                var e = new PlannedEntry { Tag = tag, ElementType = type, NumElements = numElements };
                data.AsSpan().CopyTo(e.InlineBytes);
                return e;
            }
            return new PlannedEntry { Tag = tag, ElementType = type, NumElements = numElements, OutOfLineData = data };
        }
    }
}

/// <summary>
/// Decoded result of a JXR container parse, or the payload to be serialised
/// by <see cref="JxrContainer.Write(JxrFile)"/>. <see cref="Codestream"/>
/// is the opaque T.832 codestream bytes — Phase 1 of SharpAstro.Jxr does not
/// inspect them.
/// </summary>
public sealed record JxrFile(
    uint Width,
    uint Height,
    JxrPixelFormat PixelFormat,
    byte[] Codestream,
    byte[]? AlphaCodestream = null,
    uint? SpatialXfrmPrimary = null,
    float? WidthResolution = null,
    float? HeightResolution = null,
    byte[]? IccProfile = null,
    byte[]? XmpMetadata = null);
