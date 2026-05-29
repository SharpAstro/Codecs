namespace SharpAstro.Jxr;

/// <summary>
/// Encode / decode the absolute coefficient level — T.832 §8.7.13
/// (DECODE_ABS_LEVEL) plus the symmetric forward direction.
/// </summary>
/// <remarks>
/// A coefficient level is split into a VLC-coded <c>ABS_LEVEL_INDEX</c>
/// (Table 52, 2 adaptive tables, values 0..6 where 6 is the escape
/// marker) plus a fixed-length <c>LEVEL_REF</c> refinement whose width
/// is determined by the index. Indices 0..5 cover levels 2..17 via a
/// small lookup; index 6 enters escape mode with up to three extra
/// FIXED_NUM widths to reach levels up into the billions.
/// <para>
/// The caller manages the adaptive VLC state (table switching across
/// MB boundaries via <see cref="AdaptiveVlc.AdaptTable1"/>); this code
/// just accumulates the deltaDisc per value coded.
/// </para>
/// </remarks>
public static class AbsLevel
{
    // T.832 Table 50 lookups. iRemap[i] is the base level for ABS_LEVEL_INDEX
    // = i; iFixedLen[i] is the LEVEL_REF width in bits for that index.
    private static ReadOnlySpan<byte> Remap     => [2, 3, 4, 6, 10, 14];
    private static ReadOnlySpan<byte> FixedLen  => [0, 0, 1,  2,  2,  2];

    /// <summary>
    /// Decode the next absolute-level coefficient from <paramref name="reader"/>
    /// using the <paramref name="state"/>'s currently selected ABS_LEVEL_INDEX
    /// code table. Updates the discriminant per Table 86. Returns the
    /// decoded positive level (the sign is signalled separately via
    /// <c>SIGN_FLAG</c>).
    /// </summary>
    public static int Decode(ref BitReader reader, ref AdaptiveVlcState state)
    {
        var table = VlcTables.AbsLevelIndex[state.TableIndex];
        var index = table.Decode(ref reader);
        DeltaDiscTables.AccumulateTable1(ref state, DeltaDiscTables.AbsLevelIndex, index);

        if (index < 6)
        {
            // Common path — short-coded levels 2..17.
            var iFixed = FixedLen[index];
            var iLevel = (int)Remap[index];
            if (iFixed > 0)
                iLevel += (int)reader.ReadBits(iFixed);
            return iLevel;
        }

        // Escape mode (index == 6): iFixed determines the width of LEVEL_REF.
        var fixedNum = (int)reader.ReadBits(4);
        var iFixed2 = fixedNum + 4;
        if (iFixed2 == 19)
        {
            iFixed2 += (int)reader.ReadBits(2);
            if (iFixed2 == 22)
                iFixed2 += (int)reader.ReadBits(3);
        }
        var levelRef = (int)reader.ReadBits(iFixed2);
        return 2 + (1 << iFixed2) + levelRef;
    }

    /// <summary>
    /// Encode a positive absolute-level coefficient. The reverse of
    /// <see cref="Decode"/>: pick the smallest ABS_LEVEL_INDEX whose
    /// range covers <paramref name="absLevel"/>, emit it, then emit
    /// LEVEL_REF (and any FIXED_NUM extensions for escape mode).
    /// </summary>
    public static void Encode(BitWriter writer, ref AdaptiveVlcState state, int absLevel)
    {
        if (absLevel < 2)
            throw new ArgumentOutOfRangeException(nameof(absLevel),
                "absLevel must be >= 2; the coefficient encoding path is only entered for non-zero coefficients with magnitude > 1");

        var table = VlcTables.AbsLevelIndex[state.TableIndex];

        // Short codes: try the 6 small ranges first.
        for (var i = 0; i < 6; i++)
        {
            var baseLevel = Remap[i];
            var width = FixedLen[i];
            var max = baseLevel + (width == 0 ? 0 : (1 << width) - 1);
            if (absLevel >= baseLevel && absLevel <= max)
            {
                table.Encode(writer, i);
                DeltaDiscTables.AccumulateTable1(ref state, DeltaDiscTables.AbsLevelIndex, i);
                if (width > 0)
                    writer.WriteBits((uint)(absLevel - baseLevel), width);
                return;
            }
        }

        // Escape mode (index 6): emit the 5-bit-or-wider iFixed extension.
        table.Encode(writer, 6);
        DeltaDiscTables.AccumulateTable1(ref state, DeltaDiscTables.AbsLevelIndex, 6);

        // Compute iFixed such that absLevel - 2 has its MSB at position iFixed.
        var x = absLevel - 2;
        var iFixed = 0;
        while ((x >> (iFixed + 1)) > 0) iFixed++;

        // FIXED_NUM is the low 4 bits of (iFixed - 4); if iFixed >= 19 we
        // emit FIXED_NUM = 15 then FIXED_NUM_EXT (u(2)); if iFixed >= 22
        // we additionally emit FIXED_NUM_EXT2 (u(3)). Max iFixed is 29.
        if (iFixed < 19)
        {
            writer.WriteBits((uint)(iFixed - 4), 4);
        }
        else if (iFixed < 22)
        {
            writer.WriteBits(15, 4);
            writer.WriteBits((uint)(iFixed - 19), 2);
        }
        else if (iFixed <= 29)
        {
            writer.WriteBits(15, 4);
            writer.WriteBits(3, 2);
            writer.WriteBits((uint)(iFixed - 22), 3);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(absLevel),
                $"absLevel {absLevel} would require iFixed > 29, exceeding the FIXED_NUM_EXT2 range");
        }

        var levelRef = x - (1 << iFixed);
        writer.WriteBits((uint)levelRef, iFixed);
    }
}
