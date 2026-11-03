using System.Buffers.Binary;

namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 only (the RIFF layout of section
// 2, "RIFF Container", and the extended file format). No libwebp, libvpx or RFC 6386 section 20
// source was read or transcribed; cwebp and dwebp are black-box test oracles.

/// <summary>Writes the RIFF/WEBP container around a VP8L bitstream.</summary>
internal static class WebpWriter
{
    private const byte IccFlag = 0x20;
    private const byte AlphaFlag = 0x10;
    private const byte ExifFlag = 0x08;
    private const byte XmpFlag = 0x04;

    /// <summary>
    /// Builds a complete file. With no metadata it is the simple layout, <c>RIFF WEBP VP8L</c>; with
    /// an ICC profile, EXIF or XMP it is the extended layout <c>VP8X [ICCP] VP8L [EXIF] [XMP]</c>,
    /// the order the format requires. Each chunk is padded to an even length.
    /// </summary>
    public static byte[] Write(
        ReadOnlySpan<byte> vp8l,
        int width,
        int height,
        bool hasAlpha,
        ReadOnlySpan<byte> icc,
        ReadOnlySpan<byte> exif,
        ReadOnlySpan<byte> xmp)
    {
        bool extended = !icc.IsEmpty || !exif.IsEmpty || !xmp.IsEmpty;

        long payload = 4 + ChunkSize(vp8l.Length);
        if (extended)
        {
            payload += 8 + 10 + ChunkSize(icc.Length) + ChunkSize(exif.Length) + ChunkSize(xmp.Length);
        }

        if (payload > uint.MaxValue)
        {
            throw new ArgumentException("The image and its metadata do not fit one RIFF container (4 GiB).");
        }

        byte[] file = new byte[8 + payload];
        var span = file.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)payload);
        "WEBP"u8.CopyTo(span[8..]);
        int offset = 12;

        if (extended)
        {
            byte flags = 0;
            if (!icc.IsEmpty) flags |= IccFlag;
            if (hasAlpha) flags |= AlphaFlag;
            if (!exif.IsEmpty) flags |= ExifFlag;
            if (!xmp.IsEmpty) flags |= XmpFlag;

            offset = WriteHeader(span, offset, "VP8X"u8, 10);
            span[offset] = flags;
            // Three reserved bytes stay zero, then the canvas size minus one in 24 bits each.
            WriteUInt24(span[(offset + 4)..], width - 1);
            WriteUInt24(span[(offset + 7)..], height - 1);
            offset += 10;
            offset = WriteChunk(span, offset, "ICCP"u8, icc);
        }

        offset = WriteChunk(span, offset, "VP8L"u8, vp8l);
        if (extended)
        {
            offset = WriteChunk(span, offset, "EXIF"u8, exif);
            offset = WriteChunk(span, offset, "XMP "u8, xmp);
        }

        return file;
    }

    // The bytes a chunk takes, or none for an empty payload that is not written at all.
    private static long ChunkSize(int length) => length == 0 ? 0 : 8L + length + (length & 1);

    private static int WriteHeader(Span<byte> span, int offset, ReadOnlySpan<byte> type, int length)
    {
        type.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], (uint)length);
        return offset + 8;
    }

    private static int WriteChunk(Span<byte> span, int offset, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return offset;
        }

        offset = WriteHeader(span, offset, type, data.Length);
        data.CopyTo(span[offset..]);
        offset += data.Length;

        // The pad byte is already zero in the fresh array.
        return offset + (data.Length & 1);
    }

    private static void WriteUInt24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }
}
