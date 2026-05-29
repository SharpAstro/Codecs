namespace SharpAstro.Jxr;

/// <summary>
/// Encode / decode the run-length between non-zero coefficients within a
/// 4×4 block — T.832 §8.7.18.6 (DECODE_RUN) and its forward dual.
/// </summary>
/// <remarks>
/// The "run" here is the count of zero coefficients between two non-zero
/// ones in scan order. iMaxRun is the remaining scan length, which caps
/// how long a run can be and selects the coding strategy:
/// <list type="bullet">
///   <item><c>iMaxRun &lt; 2</c>: no syntax needed — only run=1 is reachable.</item>
///   <item><c>2 ≤ iMaxRun ≤ 4</c>: a short, dedicated RUN_VALUE Huffman
///     table per iMaxRun (Tables 76, 77, 78).</item>
///   <item><c>iMaxRun ≥ 5</c>: RUN_INDEX (Table 79, 5 values) selects one
///     of 15 (base, fixed-width-refinement) bins, then RUN_REF supplies
///     the refinement bits. The iRunBin[] array partitions iMaxRun into
///     three regimes (≤7, ≤11, ≤14) that share the same RUN_INDEX
///     alphabet but map to different bins.</item>
/// </list>
/// No adaptive VLC state — the RUN_INDEX / RUN_VALUE tables are fixed.
/// </remarks>
public static class RunCoding
{
    // T.832 Table 73 constants.
    private static ReadOnlySpan<byte> RemapRun =>
        [1, 2, 3, 5, 7, 1, 2, 3, 5, 7, 1, 2, 3, 4, 5];

    private static ReadOnlySpan<sbyte> RunBin =>
        [-1, -1, -1, -1, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0];

    private static ReadOnlySpan<byte> RunFixedLength =>
        [0, 0, 1, 1, 3, 0, 0, 1, 1, 2, 0, 0, 0, 0, 1];

    /// <summary>
    /// Decode the next run-length given the remaining scan length
    /// <paramref name="iMaxRun"/> (in [1, 14]). Returns the run length.
    /// </summary>
    public static int Decode(ref BitReader reader, int iMaxRun)
    {
        if (iMaxRun < 1 || iMaxRun > 14)
            throw new ArgumentOutOfRangeException(nameof(iMaxRun), "iMaxRun must be in [1, 14]");

        if (iMaxRun == 1) return 1;
        if (iMaxRun < 5)
            return VlcTables.RunValue(iMaxRun).Decode(ref reader);

        var runIndex = VlcTables.RunIndex.Decode(ref reader);
        var idx = runIndex + 5 * RunBin[iMaxRun];
        var iFixed = RunFixedLength[idx];
        var iRun = (int)RemapRun[idx];
        if (iFixed > 0)
            iRun += (int)reader.ReadBits(iFixed);
        return iRun;
    }

    /// <summary>
    /// Encode a run-length <paramref name="iRun"/> given the remaining scan
    /// length <paramref name="iMaxRun"/>.
    /// </summary>
    public static void Encode(BitWriter writer, int iMaxRun, int iRun)
    {
        if (iMaxRun < 1 || iMaxRun > 14)
            throw new ArgumentOutOfRangeException(nameof(iMaxRun), "iMaxRun must be in [1, 14]");
        if (iRun < 1 || iRun > iMaxRun)
            throw new ArgumentOutOfRangeException(nameof(iRun), $"iRun {iRun} out of range [1, {iMaxRun}]");

        if (iMaxRun == 1)
            return; // No bits emitted — decoder infers iRun = 1.

        if (iMaxRun < 5)
        {
            VlcTables.RunValue(iMaxRun).Encode(writer, iRun);
            return;
        }

        // Long-run encoding: find which (base, fixed-width) bin covers iRun
        // within the iIndex range allowed by iMaxRun's iRunBin.
        var bin = RunBin[iMaxRun];
        var baseIdx = 5 * bin;
        for (var j = 0; j < 5; j++)
        {
            var idx = baseIdx + j;
            var baseRun = (int)RemapRun[idx];
            var width = RunFixedLength[idx];
            var maxForBin = baseRun + (width == 0 ? 0 : (1 << width) - 1);
            if (iRun >= baseRun && iRun <= maxForBin)
            {
                VlcTables.RunIndex.Encode(writer, j);
                if (width > 0)
                    writer.WriteBits((uint)(iRun - baseRun), width);
                return;
            }
        }
        throw new InvalidOperationException(
            $"RunCoding.Encode: no bin covers iRun={iRun} for iMaxRun={iMaxRun} (bin={bin}) — spec invariant violated");
    }
}
