using System.IO.Compression;

namespace SharpAstro.Exr;

/// <summary>
/// ZIP / ZIPS compression: the shared <see cref="ExrByteTransform"/> followed by zlib
/// deflate. OpenEXR uses zlib's framed format (2-byte header + deflate + Adler-32),
/// which <see cref="ZLibStream"/> produces and consumes interchangeably — so our
/// output inflates in OpenEXR and we inflate OpenEXR's. (Byte-for-byte parity with
/// OpenEXR's zlib is neither expected nor required: .NET's deflate differs, but the
/// decoded pixels are identical.) ZIP and ZIPS differ only in scanlines-per-block.
/// </summary>
internal static class ExrZip
{
    public static byte[] Compress(byte[] raw)
    {
        byte[] transformed = ExrByteTransform.Encode(raw);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(transformed, 0, transformed.Length);
        return ms.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        var transformed = new byte[uncompressedSize];
        using (var ms = new MemoryStream(src.ToArray(), writable: false))
        using (var z = new ZLibStream(ms, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < uncompressedSize)
            {
                int n = z.Read(transformed, read, uncompressedSize - read);
                if (n == 0) break;
                read += n;
            }
            if (read != uncompressedSize)
                throw new InvalidDataException($"ZIP block inflated to {read} bytes, expected {uncompressedSize}.");
        }
        return ExrByteTransform.Decode(transformed);
    }
}
