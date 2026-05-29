namespace SharpAstro.Jxr;

/// <summary>
/// Encode / decode the FIRST_INDEX syntax element — T.832 §8.7.18.8.
/// FIRST_INDEX is the joint code at the head of a block's coefficient
/// emission that encapsulates three events at once:
/// </summary>
/// <remarks>
/// Bit-encoding of the value (from the spec):
/// <list type="bullet">
///   <item>bit 0 — whether the run BEFORE the first non-zero coefficient is non-zero (0) or zero (1)</item>
///   <item>bit 1 — whether the magnitude of the first non-zero coefficient is 1 (0) or &gt; 1 (1)</item>
///   <item>bits 2-3 — ternary trailing-coefficient signal: 0 = this is the last coefficient, 1 = next-run is zero, 2 = next-run is non-zero</item>
/// </list>
/// Alphabet size 2 × 2 × 3 = 12 (values 0..11).
/// <para>
/// The caller selects the correct <see cref="AdaptiveVlcState"/> instance
/// from the four band×chroma states (DecFirstIndLPLum / LPChr / HPLum /
/// HPChr) and passes it; this code updates both discriminants via the
/// FirstIndexDelta table (T.832 Table 87).
/// </para>
/// </remarks>
public static class FirstIndexCoding
{
    /// <summary>
    /// Decode the next FIRST_INDEX from <paramref name="reader"/> using
    /// the table selected by <paramref name="state"/>.TableIndex (0..4).
    /// </summary>
    public static int Decode(ref BitReader reader, ref AdaptiveVlcState state)
    {
        var table = VlcTables.FirstIndex[state.TableIndex];
        var firstIndex = table.Decode(ref reader);
        DeltaDiscTables.AccumulateTable2(ref state, DeltaDiscTables.FirstIndex, firstIndex);
        return firstIndex;
    }

    /// <summary>Encode <paramref name="firstIndex"/> (0..11) into <paramref name="writer"/>.</summary>
    public static void Encode(BitWriter writer, ref AdaptiveVlcState state, int firstIndex)
    {
        if ((uint)firstIndex > 11)
            throw new ArgumentOutOfRangeException(nameof(firstIndex), "FIRST_INDEX must be in [0, 11]");
        var table = VlcTables.FirstIndex[state.TableIndex];
        table.Encode(writer, firstIndex);
        DeltaDiscTables.AccumulateTable2(ref state, DeltaDiscTables.FirstIndex, firstIndex);
    }
}
