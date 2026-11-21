// Portions of this file are ported from jxl-oxide (https://github.com/tirr-c/jxl-oxide), Copyright (c)
// Wonwoo Choi and the jxl-oxide contributors, used under the MIT licence; see THIRD-PARTY-NOTICES.md.

namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL frame Table of Contents (ISO/IEC 18181-1 §E.3): the byte size of each frame section/group and
/// where it starts. A permuted TOC (an encoder's own section order: cjxl writes chunk by chunk, each LfGroup
/// followed by its PassGroups, and libjxl can also write groups centre-first) codes a
/// permutation as a Lehmer code over the entries, with 8 distributions, before the sizes; the sizes then
/// follow in codestream order and logical entry <c>i</c> is the section at codestream position
/// <c>permutation[i]</c> (libjxl ReadGroupOffsets, jxl-oxide Toc::parse). <see cref="Sizes"/> and
/// <see cref="Offsets"/> are given in logical order either way, so a frame reader indexes them by section
/// (LfGlobal, LfGroup..., HfGlobal, PassGroup...) without knowing whether the TOC was permuted.
/// </summary>
internal readonly struct JxlToc
{
    /// <summary>Total byte size of all frame sections.</summary>
    public long TotalSize { get; init; }

    public int EntryCount { get; init; }

    /// <summary>Per-entry byte sizes, in logical order. Length == <see cref="EntryCount"/>.</summary>
    public long[] Sizes { get; init; }

    /// <summary>
    /// Per-entry start, in bytes from the first section (the byte after the TOC), in logical order.
    /// Length == <see cref="EntryCount"/>.
    /// </summary>
    public long[] Offsets { get; init; }

    /// <summary>True when the TOC carried a permutation.</summary>
    public bool Permuted { get; init; }

    public static JxlToc Read(ref JxlBitReader br, in JxlFrameHeader frame)
    {
        int entryCount = frame.NumGroups == 1 && frame.NumPasses == 1
            ? 1
            : 1 + frame.NumLfGroups + 1 + frame.NumGroups * frame.NumPasses;

        int[]? permutation = null;
        if (br.ReadBit()) // permuted
        {
            var decoder = JxlEntropyDecoder.Parse(ref br, 8);
            decoder.Begin(ref br);
            permutation = JxlEntropyDecoder.ReadPermutation(ref br, decoder, (uint)entryCount, 0);
            decoder.Finish();
        }

        br.ZeroPadToByte();
        long total = 0;
        var streamSizes = new long[entryCount];
        var streamOffsets = new long[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            streamOffsets[i] = total;
            streamSizes[i] = br.ReadU32((0, 10), (1024, 14), (17408, 22), (4211712, 30));
            total += streamSizes[i];
        }
        br.ZeroPadToByte();

        if (permutation is null)
            return new JxlToc { TotalSize = total, EntryCount = entryCount, Sizes = streamSizes, Offsets = streamOffsets };

        var sizes = new long[entryCount];
        var offsets = new long[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            sizes[i] = streamSizes[permutation[i]];
            offsets[i] = streamOffsets[permutation[i]];
        }
        return new JxlToc { TotalSize = total, EntryCount = entryCount, Sizes = sizes, Offsets = offsets, Permuted = true };
    }
}
