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
/// the spec page. Adaptive tables (where the spec gives Code 0..N-1
/// columns) are exposed as arrays indexed by <c>AdaptiveVlcState.TableIndex</c>.
/// </remarks>
public static class VlcTables
{
    // ======================================================================
    // Table 51 — VAL_DC_YUV (T.832 8.7.14.2). Single fixed table.
    // ======================================================================
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

    // ======================================================================
    // Table 52 — ABS_LEVEL_INDEX (T.832 8.7.14.5). 2 adaptive tables.
    // ======================================================================
    public static readonly VlcCodeTable[] AbsLevelIndex =
    [
        // Code 0
        new(
        [
            new(Value: 0, Code: 0b01,    Length: 2),
            new(Value: 1, Code: 0b10,    Length: 2),
            new(Value: 2, Code: 0b11,    Length: 2),
            new(Value: 3, Code: 0b001,   Length: 3),
            new(Value: 4, Code: 0b0001,  Length: 4),
            new(Value: 5, Code: 0b00000, Length: 5),
            new(Value: 6, Code: 0b00001, Length: 5),
        ]),
        // Code 1
        new(
        [
            new(Value: 0, Code: 0b1,      Length: 1),
            new(Value: 1, Code: 0b01,     Length: 2),
            new(Value: 2, Code: 0b001,    Length: 3),
            new(Value: 3, Code: 0b0001,   Length: 4),
            new(Value: 4, Code: 0b00001,  Length: 5),
            new(Value: 5, Code: 0b000000, Length: 6),
            new(Value: 6, Code: 0b000001, Length: 6),
        ]),
    ];

    // ======================================================================
    // Table 55 — CBPLP_YUV1 for YUV444 (T.832 8.7.16.3.1). Single table.
    // ======================================================================
    public static readonly VlcCodeTable CbpLpYuv444 = new(
    [
        new(Value: 0, Code: 0b0,    Length: 1),
        new(Value: 1, Code: 0b100,  Length: 3),
        new(Value: 2, Code: 0b1010, Length: 4),
        new(Value: 3, Code: 0b1011, Length: 4),
        new(Value: 4, Code: 0b1100, Length: 4),
        new(Value: 5, Code: 0b1101, Length: 4),
        new(Value: 6, Code: 0b1110, Length: 4),
        new(Value: 7, Code: 0b1111, Length: 4),
    ]);

    // ======================================================================
    // Table 56 — CBPLP_YUV1 for YUV420 / YUV422. Single table.
    // ======================================================================
    public static readonly VlcCodeTable CbpLpYuv420Or422 = new(
    [
        new(Value: 0, Code: 0b0,   Length: 1),
        new(Value: 1, Code: 0b10,  Length: 2),
        new(Value: 2, Code: 0b110, Length: 3),
        new(Value: 3, Code: 0b111, Length: 3),
    ]);

    // ======================================================================
    // Table 59 — NUM_CBPHP (T.832 8.7.17.4.1). 2 adaptive tables.
    // ======================================================================
    public static readonly VlcCodeTable[] NumCbphp =
    [
        new(
        [
            new(Value: 0, Code: 0b1,    Length: 1),
            new(Value: 1, Code: 0b01,   Length: 2),
            new(Value: 2, Code: 0b001,  Length: 3),
            new(Value: 3, Code: 0b0000, Length: 4),
            new(Value: 4, Code: 0b0001, Length: 4),
        ]),
        new(
        [
            new(Value: 0, Code: 0b1,   Length: 1),
            new(Value: 1, Code: 0b000, Length: 3),
            new(Value: 2, Code: 0b001, Length: 3),
            new(Value: 3, Code: 0b010, Length: 3),
            new(Value: 4, Code: 0b011, Length: 3),
        ]),
    ];

    // ======================================================================
    // Table 60 — NUM_BLKCBPHP for YONLY / NCOMPONENT / YUVK. 2 adaptive tables.
    // (Identical bit patterns to Table 59; kept separate per spec for clarity.)
    // ======================================================================
    public static readonly VlcCodeTable[] NumBlkCbphpYuvk = NumCbphp;

    // ======================================================================
    // Table 61 — NUM_BLKCBPHP for other formats. 2 adaptive tables, 9 values each.
    // ======================================================================
    public static readonly VlcCodeTable[] NumBlkCbphpColour =
    [
        new(
        [
            new(Value: 0, Code: 0b010,    Length: 3),
            new(Value: 1, Code: 0b00000,  Length: 5),
            new(Value: 2, Code: 0b0010,   Length: 4),
            new(Value: 3, Code: 0b00001,  Length: 5),
            new(Value: 4, Code: 0b00010,  Length: 5),
            new(Value: 5, Code: 0b1,      Length: 1),
            new(Value: 6, Code: 0b011,    Length: 3),
            new(Value: 7, Code: 0b00011,  Length: 5),
            new(Value: 8, Code: 0b0011,   Length: 4),
        ]),
        new(
        [
            new(Value: 0, Code: 0b1,       Length: 1),
            new(Value: 1, Code: 0b001,     Length: 3),
            new(Value: 2, Code: 0b010,     Length: 3),
            new(Value: 3, Code: 0b0001,    Length: 4),
            new(Value: 4, Code: 0b000001,  Length: 6),
            new(Value: 5, Code: 0b011,     Length: 3),
            new(Value: 6, Code: 0b00001,   Length: 5),
            new(Value: 7, Code: 0b0000000, Length: 7),
            new(Value: 8, Code: 0b0000001, Length: 7),
        ]),
    ];

    // ======================================================================
    // Table 62 — CHR_CBPHP, VAL_INC, CBPHP_CH_BLK (T.832 8.7.17.4.3). Single table.
    // ======================================================================
    public static readonly VlcCodeTable ChrCbphp = new(
    [
        new(Value: 0, Code: 0b1,  Length: 1),
        new(Value: 1, Code: 0b01, Length: 2),
        new(Value: 2, Code: 0b00, Length: 2),
    ]);

    // ======================================================================
    // Table 63 — NUM_CH_BLK (T.832 8.7.17.4.6). Single table.
    // ======================================================================
    public static readonly VlcCodeTable NumChBlk = new(
    [
        new(Value: 0, Code: 0b1,   Length: 1),
        new(Value: 1, Code: 0b01,  Length: 2),
        new(Value: 2, Code: 0b000, Length: 3),
        new(Value: 3, Code: 0b001, Length: 3),
    ]);

    // ======================================================================
    // Table 64 — REF_CBPHP1 (T.832 8.7.17.4.8). Single table.
    // ======================================================================
    public static readonly VlcCodeTable RefCbphp1 = new(
    [
        new(Value: 3,  Code: 0b00,  Length: 2),
        new(Value: 5,  Code: 0b01,  Length: 2),
        new(Value: 6,  Code: 0b100, Length: 3),
        new(Value: 9,  Code: 0b101, Length: 3),
        new(Value: 10, Code: 0b110, Length: 3),
        new(Value: 12, Code: 0b111, Length: 3),
    ]);

    // ======================================================================
    // Tables 76 / 77 / 78 — RUN_VALUE for iMaxRun in {2, 3, 4}.
    // ======================================================================
    public static readonly VlcCodeTable RunValueMax2 = new(
    [
        new(Value: 1, Code: 0b1, Length: 1),
        new(Value: 2, Code: 0b0, Length: 1),
    ]);

    public static readonly VlcCodeTable RunValueMax3 = new(
    [
        new(Value: 1, Code: 0b1,  Length: 1),
        new(Value: 2, Code: 0b01, Length: 2),
        new(Value: 3, Code: 0b00, Length: 2),
    ]);

    public static readonly VlcCodeTable RunValueMax4 = new(
    [
        new(Value: 1, Code: 0b1,   Length: 1),
        new(Value: 2, Code: 0b01,  Length: 2),
        new(Value: 3, Code: 0b001, Length: 3),
        new(Value: 4, Code: 0b000, Length: 3),
    ]);

    /// <summary>Pick the right RUN_VALUE table from <paramref name="iMaxRun"/> in {2,3,4}.</summary>
    public static VlcCodeTable RunValue(int iMaxRun) => iMaxRun switch
    {
        2 => RunValueMax2,
        3 => RunValueMax3,
        4 => RunValueMax4,
        _ => throw new ArgumentOutOfRangeException(nameof(iMaxRun), "RUN_VALUE is defined only for iMaxRun in {2,3,4}"),
    };

    // ======================================================================
    // Table 79 — RUN_INDEX. Single table.
    // ======================================================================
    public static readonly VlcCodeTable RunIndex = new(
    [
        new(Value: 0, Code: 0b1,    Length: 1),
        new(Value: 1, Code: 0b01,   Length: 2),
        new(Value: 2, Code: 0b001,  Length: 3),
        new(Value: 3, Code: 0b0000, Length: 4),
        new(Value: 4, Code: 0b0001, Length: 4),
    ]);

    // ======================================================================
    // Table 80 — INDEX_A (T.832 8.7.18.9.4). 4 adaptive tables, 6 values each.
    // ======================================================================
    public static readonly VlcCodeTable[] IndexA =
    [
        new(
        [
            new(Value: 0, Code: 0b1,     Length: 1),
            new(Value: 1, Code: 0b00000, Length: 5),
            new(Value: 2, Code: 0b001,   Length: 3),
            new(Value: 3, Code: 0b00001, Length: 5),
            new(Value: 4, Code: 0b01,    Length: 2),
            new(Value: 5, Code: 0b0001,  Length: 4),
        ]),
        new(
        [
            new(Value: 0, Code: 0b01,   Length: 2),
            new(Value: 1, Code: 0b0000, Length: 4),
            new(Value: 2, Code: 0b10,   Length: 2),
            new(Value: 3, Code: 0b0001, Length: 4),
            new(Value: 4, Code: 0b11,   Length: 2),
            new(Value: 5, Code: 0b001,  Length: 3),
        ]),
        new(
        [
            new(Value: 0, Code: 0b0000, Length: 4),
            new(Value: 1, Code: 0b0001, Length: 4),
            new(Value: 2, Code: 0b01,   Length: 2),
            new(Value: 3, Code: 0b10,   Length: 2),
            new(Value: 4, Code: 0b11,   Length: 2),
            new(Value: 5, Code: 0b001,  Length: 3),
        ]),
        new(
        [
            new(Value: 0, Code: 0b00000, Length: 5),
            new(Value: 1, Code: 0b00001, Length: 5),
            new(Value: 2, Code: 0b01,    Length: 2),
            new(Value: 3, Code: 0b1,     Length: 1),
            new(Value: 4, Code: 0b0001,  Length: 4),
            new(Value: 5, Code: 0b001,   Length: 3),
        ]),
    ];

    // ======================================================================
    // Table 81 — INDEX_B (T.832 8.7.18.9.5). Single table.
    // ======================================================================
    public static readonly VlcCodeTable IndexB = new(
    [
        new(Value: 0, Code: 0b0,   Length: 1),
        new(Value: 1, Code: 0b110, Length: 3),
        new(Value: 2, Code: 0b10,  Length: 2),
        new(Value: 3, Code: 0b111, Length: 3),
    ]);

    // ======================================================================
    // Table 82 — FIRST_INDEX (T.832 8.7.18.9.7). 5 adaptive tables, 12 values each.
    // ======================================================================
    public static readonly VlcCodeTable[] FirstIndex =
    [
        // Code 0
        new(
        [
            new(Value: 0,  Code: 0b00001,    Length: 5),
            new(Value: 1,  Code: 0b000001,   Length: 6),
            new(Value: 2,  Code: 0b0000000,  Length: 7),
            new(Value: 3,  Code: 0b0000001,  Length: 7),
            new(Value: 4,  Code: 0b00100,    Length: 5),
            new(Value: 5,  Code: 0b010,      Length: 3),
            new(Value: 6,  Code: 0b00101,    Length: 5),
            new(Value: 7,  Code: 0b1,        Length: 1),
            new(Value: 8,  Code: 0b00110,    Length: 5),
            new(Value: 9,  Code: 0b0001,     Length: 4),
            new(Value: 10, Code: 0b00111,    Length: 5),
            new(Value: 11, Code: 0b011,      Length: 3),
        ]),
        // Code 1
        new(
        [
            new(Value: 0,  Code: 0b0010,     Length: 4),
            new(Value: 1,  Code: 0b00010,    Length: 5),
            new(Value: 2,  Code: 0b000000,   Length: 6),
            new(Value: 3,  Code: 0b000001,   Length: 6),
            new(Value: 4,  Code: 0b0011,     Length: 4),
            new(Value: 5,  Code: 0b010,      Length: 3),
            new(Value: 6,  Code: 0b00011,    Length: 5),
            new(Value: 7,  Code: 0b11,       Length: 2),
            new(Value: 8,  Code: 0b011,      Length: 3),
            new(Value: 9,  Code: 0b100,      Length: 3),
            new(Value: 10, Code: 0b00001,    Length: 5),
            new(Value: 11, Code: 0b101,      Length: 3),
        ]),
        // Code 2
        new(
        [
            new(Value: 0,  Code: 0b11,       Length: 2),
            new(Value: 1,  Code: 0b001,      Length: 3),
            new(Value: 2,  Code: 0b0000000,  Length: 7),
            new(Value: 3,  Code: 0b0000001,  Length: 7),
            new(Value: 4,  Code: 0b00001,    Length: 5),
            new(Value: 5,  Code: 0b010,      Length: 3),
            new(Value: 6,  Code: 0b0000010,  Length: 7),
            new(Value: 7,  Code: 0b011,      Length: 3),
            new(Value: 8,  Code: 0b100,      Length: 3),
            new(Value: 9,  Code: 0b101,      Length: 3),
            new(Value: 10, Code: 0b0000011,  Length: 7),
            new(Value: 11, Code: 0b0001,     Length: 4),
        ]),
        // Code 3
        new(
        [
            new(Value: 0,  Code: 0b001,      Length: 3),
            new(Value: 1,  Code: 0b11,       Length: 2),
            new(Value: 2,  Code: 0b0000000,  Length: 7),
            new(Value: 3,  Code: 0b00001,    Length: 5),
            new(Value: 4,  Code: 0b00010,    Length: 5),
            new(Value: 5,  Code: 0b010,      Length: 3),
            new(Value: 6,  Code: 0b0000001,  Length: 7),
            new(Value: 7,  Code: 0b011,      Length: 3),
            new(Value: 8,  Code: 0b00011,    Length: 5),
            new(Value: 9,  Code: 0b100,      Length: 3),
            new(Value: 10, Code: 0b000001,   Length: 6),
            new(Value: 11, Code: 0b101,      Length: 3),
        ]),
        // Code 4
        new(
        [
            new(Value: 0,  Code: 0b010,      Length: 3),
            new(Value: 1,  Code: 0b1,        Length: 1),
            new(Value: 2,  Code: 0b0000001,  Length: 7),
            new(Value: 3,  Code: 0b0001,     Length: 4),
            new(Value: 4,  Code: 0b0000010,  Length: 7),
            new(Value: 5,  Code: 0b011,      Length: 3),
            new(Value: 6,  Code: 0b00000000, Length: 8),
            new(Value: 7,  Code: 0b0010,     Length: 4),
            new(Value: 8,  Code: 0b0000011,  Length: 7),
            new(Value: 9,  Code: 0b0011,     Length: 4),
            new(Value: 10, Code: 0b00000001, Length: 8),
            new(Value: 11, Code: 0b00001,    Length: 5),
        ]),
    ];
}
