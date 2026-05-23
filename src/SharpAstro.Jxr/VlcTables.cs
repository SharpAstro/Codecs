namespace SharpAstro.Jxr;

/// <summary>
/// Static VLC code tables from T.832 §8.7 — one per syntax element. Each
/// table is constructed once at type-init time and reused for every
/// macroblock that needs to encode or decode the corresponding element.
/// </summary>
/// <remarks>
/// Bit patterns are transcribed verbatim from the spec's "Code | Value"
/// tables. Binary literals (<c>0b...</c>) match the spec's left-to-right
/// MSB-first ordering so the reviewer can scan the table side-by-side with
/// the spec page.
/// </remarks>
public static class VlcTables
{
    // T.832 Table 51 — VAL_DC_YUV (8.7.14.2). 3-bit value (Y, U, V zero/non-zero
    // status) encoded with a fixed 2-to-5 bit Huffman code. Single code table
    // (not adaptive).
    public static readonly VlcCodeTable ValDcYuv = new(
    [
        new(Value: 0, Code: 0b10,    Length: 2),
        new(Value: 1, Code: 0b001,   Length: 3),
        new(Value: 2, Code: 0b00001, Length: 5),
        new(Value: 3, Code: 0b0001,  Length: 4),
        new(Value: 4, Code: 0b11,    Length: 2),
        new(Value: 5, Code: 0b010,   Length: 3),
        new(Value: 6, Code: 0b00000, Length: 5),
        new(Value: 7, Code: 0b011,   Length: 3),
    ]);

    // T.832 Table 52 — ABS_LEVEL_INDEX (8.7.14.5). 7 values; two code tables
    // selected adaptively (see AdaptiveVlc.AdaptTable1 driving TableIndex 0/1).
    public static readonly VlcCodeTable AbsLevelIndex0 = new(
    [
        new(Value: 0, Code: 0b01,    Length: 2),
        new(Value: 1, Code: 0b10,    Length: 2),
        new(Value: 2, Code: 0b11,    Length: 2),
        new(Value: 3, Code: 0b001,   Length: 3),
        new(Value: 4, Code: 0b0001,  Length: 4),
        new(Value: 5, Code: 0b00000, Length: 5),
        new(Value: 6, Code: 0b00001, Length: 5),
    ]);

    public static readonly VlcCodeTable AbsLevelIndex1 = new(
    [
        new(Value: 0, Code: 0b1,      Length: 1),
        new(Value: 1, Code: 0b01,     Length: 2),
        new(Value: 2, Code: 0b001,    Length: 3),
        new(Value: 3, Code: 0b0001,   Length: 4),
        new(Value: 4, Code: 0b00001,  Length: 5),
        new(Value: 5, Code: 0b000000, Length: 6),
        new(Value: 6, Code: 0b000001, Length: 6),
    ]);

    /// <summary>
    /// Pick the right ABS_LEVEL_INDEX table by the current adaptive
    /// TableIndex (0 or 1). Convenience wrapper so callers don't have to
    /// keep the two table references straight.
    /// </summary>
    public static VlcCodeTable AbsLevelIndex(int tableIndex) => tableIndex switch
    {
        0 => AbsLevelIndex0,
        1 => AbsLevelIndex1,
        _ => throw new ArgumentOutOfRangeException(nameof(tableIndex), "ABS_LEVEL_INDEX has only tables 0 and 1"),
    };
}
