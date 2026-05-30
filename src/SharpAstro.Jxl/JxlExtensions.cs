namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL extension mechanism (ISO/IEC 18181-1 §C.3): a U64 bitmask of present extensions
/// followed by per-extension bit lengths. No defined extensions exist yet, so a non-zero
/// mask is unsupported; the common (empty) case is just the 2-bit zero U64.
/// </summary>
internal static class JxlExtensions
{
    public static void Skip(ref JxlBitReader br)
    {
        ulong extensionBits = br.ReadU64();
        if (extensionBits != 0)
            throw new NotSupportedException("JPEG XL bitstream extensions are not yet supported.");
    }
}
