namespace SharpAstro.Exr;

/// <summary>
/// Per-block compression dispatch. OpenEXR stores a block uncompressed whenever the
/// compressor fails to shrink it (compressed size ≥ raw size); a reader detects that
/// by comparing the stored size to the expected uncompressed size. Each scheme is a
/// lossless, reversible transform of the gathered scanline bytes.
/// </summary>
internal static class ExrCompressor
{
    /// <summary>Compress one block's raw bytes; returns the bytes to store (raw if compression didn't help).</summary>
    public static byte[] Compress(ExrCompression compression, byte[] raw, ExrBlockInfo info)
    {
        if (compression == ExrCompression.None)
            return raw;

        byte[] packed = compression switch
        {
            _ => throw new NotSupportedException($"EXR compression {compression} is not yet implemented."),
        };

        // OpenEXR keeps whichever is smaller; ties go to the raw bytes.
        return packed.Length < raw.Length ? packed : raw;
    }

    /// <summary>Decompress one block back to <paramref name="uncompressedSize"/> raw bytes.</summary>
    public static byte[] Decompress(ExrCompression compression, ReadOnlySpan<byte> src, int uncompressedSize, ExrBlockInfo info)
    {
        // A block stored uncompressed (because compression didn't help, or NONE).
        if (src.Length == uncompressedSize)
            return src.ToArray();

        return compression switch
        {
            ExrCompression.None => src.ToArray(),
            _ => throw new NotSupportedException($"EXR compression {compression} is not yet implemented."),
        };
    }
}
