namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL container handling (ISO/IEC 18181-2). A .jxl file is either a bare codestream
/// (starting with the 0xFF 0x0A signature) or an ISOBMFF container whose codestream lives
/// in a single <c>jxlc</c> box, or is split across ordered <c>jxlp</c> boxes.
/// </summary>
internal static class JxlContainer
{
    // ISOBMFF signature box: size = 12, type "JXL ", payload 0D 0A 87 0A.
    private static ReadOnlySpan<byte> ContainerSignature =>
        [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    // Bare codestream signature.
    private static ReadOnlySpan<byte> CodestreamSignature => [0xFF, 0x0A];

    /// <summary>
    /// Returns the raw codestream bytes (including the leading 0xFF 0x0A signature),
    /// unwrapping the ISOBMFF container when present.
    /// </summary>
    public static byte[] ExtractCodestream(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(CodestreamSignature))
            return data.ToArray();

        if (!data.StartsWith(ContainerSignature))
            throw new InvalidDataException(
                "Not a JPEG XL file: missing codestream (FF 0A) or container (JXL box) signature.");

        using var codestream = new MemoryStream();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            ulong boxSize = ReadU32BE(data, pos);
            ReadOnlySpan<byte> type = data.Slice(pos + 4, 4);
            int headerLen = 8;
            if (boxSize == 1) // 64-bit extended size
            {
                if (pos + 16 > data.Length)
                    break;
                boxSize = ReadU64BE(data, pos + 8);
                headerLen = 16;
            }

            // boxSize == 0 means "to end of file".
            long boxEnd = boxSize == 0 ? data.Length : pos + (long)boxSize;
            if (boxEnd <= pos || boxEnd > data.Length)
                boxEnd = data.Length;

            ReadOnlySpan<byte> payload = data[(pos + headerLen)..(int)boxEnd];
            if (type.SequenceEqual("jxlc"u8))
                codestream.Write(payload);
            else if (type.SequenceEqual("jxlp"u8) && payload.Length >= 4)
                codestream.Write(payload[4..]); // skip the 4-byte partial index

            pos = (int)boxEnd;
        }

        if (codestream.Length == 0)
            throw new InvalidDataException("JPEG XL container has no jxlc/jxlp codestream box.");
        return codestream.ToArray();
    }

    private static uint ReadU32BE(ReadOnlySpan<byte> d, int p) =>
        (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);

    private static ulong ReadU64BE(ReadOnlySpan<byte> d, int p) =>
        ((ulong)ReadU32BE(d, p) << 32) | ReadU32BE(d, p + 4);
}
