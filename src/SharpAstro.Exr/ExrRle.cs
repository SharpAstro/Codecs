namespace SharpAstro.Exr;

/// <summary>
/// RLE compression: the shared <see cref="ExrByteTransform"/> followed by OpenEXR's
/// byte run-length coding (ImfRle.cpp). Stream format: a signed control byte <c>c</c>
/// — if <c>c &lt; 0</c>, <c>-c</c> literal bytes follow; if <c>c &gt;= 0</c>, the next
/// single byte repeats <c>c + 1</c> times. Our encoder need only emit a valid stream
/// (it isn't required to match OpenEXR's exact run/literal choices), and our decoder
/// reads any conformant stream — together giving a lossless, interoperable round-trip.
/// </summary>
internal static class ExrRle
{
    private const int MaxRun = 128;       // a run encodes 1..128 copies
    private const int MaxLiteral = 127;   // a literal segment encodes 1..127 bytes
    private const int MinRun = 3;         // shorter equal-runs aren't worth a run code

    public static byte[] Compress(byte[] raw)
    {
        byte[] t = ExrByteTransform.Encode(raw);
        int n = t.Length;
        var outp = new List<byte>(n / 2 + 16);

        int pos = 0;
        while (pos < n)
        {
            int runLen = RunLength(t, pos, n);
            if (runLen >= MinRun)
            {
                outp.Add((byte)(sbyte)(runLen - 1)); // 2..127
                outp.Add(t[pos]);
                pos += runLen;
            }
            else
            {
                int litStart = pos;
                int litLen = 0;
                while (pos < n && litLen < MaxLiteral && RunLength(t, pos, n) < MinRun)
                {
                    pos++;
                    litLen++;
                }
                outp.Add((byte)(sbyte)(-litLen));    // -1..-127
                for (var k = 0; k < litLen; k++) outp.Add(t[litStart + k]);
            }
        }
        return [.. outp];
    }

    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        var t = new byte[uncompressedSize];
        int o = 0, i = 0;
        while (i < src.Length && o < uncompressedSize)
        {
            sbyte c = (sbyte)src[i++];
            if (c < 0)
            {
                int count = -c;
                if (i + count > src.Length || o + count > uncompressedSize)
                    throw new InvalidDataException("Corrupt EXR RLE literal run.");
                src.Slice(i, count).CopyTo(t.AsSpan(o));
                i += count; o += count;
            }
            else
            {
                int count = c + 1;
                if (i >= src.Length || o + count > uncompressedSize)
                    throw new InvalidDataException("Corrupt EXR RLE repeat run.");
                byte v = src[i++];
                t.AsSpan(o, count).Fill(v);
                o += count;
            }
        }
        if (o != uncompressedSize)
            throw new InvalidDataException($"EXR RLE decoded {o} bytes, expected {uncompressedSize}.");
        return ExrByteTransform.Decode(t);
    }

    // Length of the run of bytes equal to data[pos], capped at MaxRun.
    private static int RunLength(byte[] data, int pos, int n)
    {
        int len = 1;
        while (pos + len < n && data[pos + len] == data[pos] && len < MaxRun) len++;
        return len;
    }
}
