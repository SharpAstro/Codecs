namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL frame Table of Contents (ISO/IEC 18181-1 §E.3): the byte size of each frame
/// section/group, in bitstream order. Rung 2 handles the non-permuted TOC; a permuted TOC
/// is entropy-coded and requires the Rung 3 decoder, so it throws for now.
/// </summary>
internal readonly struct JxlToc
{
    /// <summary>Total byte size of all frame sections.</summary>
    public long TotalSize { get; init; }

    public int EntryCount { get; init; }

    /// <summary>Per-entry byte sizes, in bitstream (non-permuted) order. Length == <see cref="EntryCount"/>.</summary>
    public long[] Sizes { get; init; }

    public static JxlToc Read(ref JxlBitReader br, in JxlFrameHeader frame)
    {
        int entryCount = frame.NumGroups == 1 && frame.NumPasses == 1
            ? 1
            : 1 + frame.NumLfGroups + 1 + frame.NumGroups * frame.NumPasses;

        bool permuted = br.ReadBit();
        if (permuted)
        {
            throw new NotSupportedException(
                "JPEG XL permuted TOC is not yet supported (requires the Rung 3 entropy decoder).");
        }

        br.ZeroPadToByte();
        long total = 0;
        var sizes = new long[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            sizes[i] = br.ReadU32((0, 10), (1024, 14), (17408, 22), (4211712, 30));
            total += sizes[i];
        }
        br.ZeroPadToByte();

        return new JxlToc { TotalSize = total, EntryCount = entryCount, Sizes = sizes };
    }
}
