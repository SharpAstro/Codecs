namespace SharpAstro.Jxr;

/// <summary>
/// Per-syntax-element adaptive VLC state — T.832 §8.8.
/// </summary>
/// <remarks>
/// The codec switches between several predefined VLC code tables per
/// syntax element based on observed coding statistics. The state tracks:
/// <list type="bullet">
///   <item><see cref="TableIndex"/> — which code table is currently active</item>
///   <item><see cref="DeltaTableIndex"/> / <see cref="Delta2TableIndex"/> —
///         which <c>deltaDisc</c> table indexes the per-symbol weight contribution to
///         the two discriminants (only Delta2 used when there are &gt; 2 code tables)</item>
///   <item><see cref="DiscrimVal1"/> / <see cref="DiscrimVal2"/> — running
///         discriminant accumulators clipped to ±64</item>
/// </list>
/// Mutable struct chosen over a class because (a) AOT-friendly: no heap
/// allocation per syntax-element instance, (b) matches the spec's value-
/// semantics pseudocode literally, (c) callers hold one of these per
/// distinct VLC syntax element (~12 of them for an HP-heavy decoder).
/// </remarks>
public struct AdaptiveVlcState
{
    public int TableIndex;
    public int DeltaTableIndex;
    public int Delta2TableIndex;
    public int DiscrimVal1;
    public int DiscrimVal2;
}

/// <summary>
/// Static helpers for initialising and adapting <see cref="AdaptiveVlcState"/>
/// — T.832 §8.8.3 / §8.8.4. Two flavours: <c>Table1</c> for syntax elements
/// with exactly two code tables (only DiscrimVal1 in play), <c>Table2</c>
/// for syntax elements with three or more code tables (both discriminants).
/// </summary>
public static class AdaptiveVlc
{
    /// <summary>
    /// Initial state for a 2-table syntax element — T.832 8.8.3.5 / Table 96.
    /// </summary>
    public static AdaptiveVlcState InitializeTable1() => new()
    {
        TableIndex = 0,
        DeltaTableIndex = 0,
        DiscrimVal1 = 0,
    };

    /// <summary>
    /// Initial state for a 3+-table syntax element — T.832 8.8.3.6 / Table 97.
    /// </summary>
    public static AdaptiveVlcState InitializeTable2() => new()
    {
        TableIndex = 1,
        DeltaTableIndex = 0,
        Delta2TableIndex = 1,
        DiscrimVal1 = 0,
        DiscrimVal2 = 0,
    };

    /// <summary>
    /// Adapt a 2-table state — T.832 8.8.4.4 / Table 101. If
    /// <c>DiscrimVal1 &lt; -8</c> and we're not already on table 0, drop down
    /// one table; if <c>&gt; 8</c> and we're not already at the maximum, bump
    /// up. On a transition, reset DiscrimVal1; on no transition, clip to ±64.
    /// </summary>
    public static void AdaptTable1(ref AdaptiveVlcState s)
    {
        const int iMaxTableIndex = 1; // Only two code tables
        const int cLowerBound = -8;
        const int cUpperBound = 8;

        if (s.DiscrimVal1 < cLowerBound && s.TableIndex != 0)
        {
            s.TableIndex--;
            s.DiscrimVal1 = 0;
        }
        else if (s.DiscrimVal1 > cUpperBound && s.TableIndex != iMaxTableIndex)
        {
            s.TableIndex++;
            s.DiscrimVal1 = 0;
        }
        else
        {
            // No transition — clip the accumulator.
            if (s.DiscrimVal1 < -64) s.DiscrimVal1 = -64;
            if (s.DiscrimVal1 > 64) s.DiscrimVal1 = 64;
        }
    }

    /// <summary>
    /// Adapt a multi-table state — T.832 8.8.4.5 / Table 102. DiscrimVal1
    /// decides "drop down a table", DiscrimVal2 decides "bump up a table".
    /// After a transition, the DeltaTableIndex / Delta2TableIndex pointers
    /// are re-aimed at the appropriate deltaDisc tables for the new
    /// neighbour pair, with special handling at the table-range endpoints.
    /// </summary>
    /// <param name="iMaxTableIndex">Highest valid <see cref="AdaptiveVlcState.TableIndex"/> value (i.e. <c>N - 1</c> for N code tables).</param>
    public static void AdaptTable2(ref AdaptiveVlcState s, int iMaxTableIndex)
    {
        const int cLowerBound = -8;
        const int cUpperBound = 8;

        var bChange = false;
        if (s.DiscrimVal1 < cLowerBound && s.TableIndex != 0)
        {
            s.TableIndex--;
            bChange = true;
        }
        else if (s.DiscrimVal2 > cUpperBound && s.TableIndex != iMaxTableIndex)
        {
            s.TableIndex++;
            bChange = true;
        }

        if (bChange)
        {
            s.DiscrimVal1 = 0;
            s.DiscrimVal2 = 0;
            if (s.TableIndex == iMaxTableIndex)
            {
                // At the top: only a "decrease" deltaDisc table is meaningful;
                // both Delta pointers reference the one comparing against the
                // table below us.
                s.DeltaTableIndex = s.TableIndex - 1;
                s.Delta2TableIndex = s.TableIndex - 1;
            }
            else if (s.TableIndex == 0)
            {
                // At the bottom: only an "increase" deltaDisc table is meaningful.
                s.DeltaTableIndex = s.TableIndex;
                s.Delta2TableIndex = s.TableIndex;
            }
            else
            {
                // Middle: Delta points down, Delta2 points up.
                s.DeltaTableIndex = s.TableIndex - 1;
                s.Delta2TableIndex = s.TableIndex;
            }
        }
        else
        {
            // No transition — clip both accumulators.
            if (s.DiscrimVal1 < -64) s.DiscrimVal1 = -64;
            if (s.DiscrimVal1 > 64) s.DiscrimVal1 = 64;
            if (s.DiscrimVal2 < -64) s.DiscrimVal2 = -64;
            if (s.DiscrimVal2 > 64) s.DiscrimVal2 = 64;
        }
    }
}
