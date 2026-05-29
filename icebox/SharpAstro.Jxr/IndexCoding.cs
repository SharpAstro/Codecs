namespace SharpAstro.Jxr;

/// <summary>
/// Encode / decode the INDEX syntax element for non-first coefficients in
/// a block — T.832 §8.7.18.7 (DECODE_INDEX) and its forward dual.
/// </summary>
/// <remarks>
/// INDEX is positional: which VLC table is in play depends on
/// <paramref name="iLocation"/> (the position currently being filled in the block):
/// <list type="bullet">
///   <item><c>iLocation &lt; 15</c>: INDEX_A — adaptive 4-table VLC (Table 80,
///     values 0..5) with discriminant accumulation per Table 88.</item>
///   <item><c>iLocation == 15</c>: INDEX_B — fixed single-table VLC (Table 81,
///     values 0..3). No adaptive state involved.</item>
///   <item><c>iLocation == 16</c>: INDEX_C_FLAG — a 1-bit FLC signalling
///     presence of further run/level pairs.</item>
/// </list>
/// </remarks>
public static class IndexCoding
{
    /// <summary>
    /// Decode the next INDEX value, dispatching on <paramref name="iLocation"/>
    /// to the matching VLC table. The <paramref name="state"/> is only
    /// touched for <c>iLocation &lt; 15</c> (the INDEX_A path).
    /// </summary>
    public static int Decode(ref BitReader reader, int iLocation, ref AdaptiveVlcState state)
    {
        if (iLocation < 15)
        {
            var table = VlcTables.IndexA[state.TableIndex];
            var idx = table.Decode(ref reader);
            DeltaDiscTables.AccumulateTable2(ref state, DeltaDiscTables.IndexA, idx);
            return idx;
        }
        if (iLocation == 15)
        {
            return VlcTables.IndexB.Decode(ref reader);
        }
        // iLocation == 16: INDEX_C_FLAG
        return (int)reader.ReadBits(1);
    }

    /// <summary>Encode <paramref name="iIndex"/> with the same iLocation-dependent table dispatch.</summary>
    public static void Encode(BitWriter writer, int iLocation, ref AdaptiveVlcState state, int iIndex)
    {
        if (iLocation < 15)
        {
            if ((uint)iIndex > 5)
                throw new ArgumentOutOfRangeException(nameof(iIndex), "INDEX_A must be in [0, 5]");
            var table = VlcTables.IndexA[state.TableIndex];
            table.Encode(writer, iIndex);
            DeltaDiscTables.AccumulateTable2(ref state, DeltaDiscTables.IndexA, iIndex);
        }
        else if (iLocation == 15)
        {
            if ((uint)iIndex > 3)
                throw new ArgumentOutOfRangeException(nameof(iIndex), "INDEX_B must be in [0, 3]");
            VlcTables.IndexB.Encode(writer, iIndex);
        }
        else
        {
            // INDEX_C_FLAG — 1-bit FLC
            writer.WriteBits((uint)(iIndex & 1), 1);
        }
    }
}
