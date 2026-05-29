using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Validates the adaptive-VLC symbol codec and its tables (transcribed verbatim
/// from jxrlib's adapthuff.c). Every symbol of every table is round-tripped
/// encode→decode (which would break on any code-or-decode-table transcription
/// typo that loses consistency), the consumed bit-length is checked against the
/// independent <c>g_Index*Table</c> length column, and the decode tables are
/// checked for the exact jxrlib sizes.
/// </summary>
public sealed class VlcSymbolCodecTests
{
    // (alphabet size, number of table blocks) — matches jxrlib gMaxTables data layout.
    public static IEnumerable<object[]> Alphabets() => new[]
    {
        new object[] { 4, 1 }, new object[] { 5, 2 }, new object[] { 6, 4 },
        new object[] { 7, 2 }, new object[] { 8, 2 }, new object[] { 9, 2 },
        new object[] { 12, 5 },
    };

    [Theory]
    [MemberData(nameof(Alphabets))]
    public void EverySymbol_RoundTrips_AndLengthMatchesIndexTable(int nSym, int tableCount)
    {
        var decodeTables = VlcTables.DecodeTables(nSym);
        var lengths = VlcTables.LengthTable(nSym);

        // Code table block count is implied by its flat length / stride.
        (VlcTables.CodeTable(nSym).Length / (nSym * 2 + 1)).ShouldBe(tableCount, "code table block count");
        decodeTables.Length.ShouldBe(tableCount, "decode table count");

        for (var t = 0; t < tableCount; t++)
        for (var s = 0; s < nSym; s++)
        {
            var (code, length) = VlcTables.GetCode(nSym, t, s);

            // Cross-check the length against the independent g_Index*Table column.
            length.ShouldBe(lengths[t * nSym + s], $"length nSym={nSym} t={t} s={s}");
            code.ShouldBeLessThan(1 << length, $"code fits in length nSym={nSym} t={t} s={s}");

            // Encode the symbol, pad so the 5-bit root peek always has data.
            var w = new BitWriter();
            VlcSymbolCodec.Encode(w, nSym, t, s);
            w.WriteBits(0, 16);

            var r = new BitReader(w.AsSpan());
            int before = r.BitPosition;
            int decoded = VlcSymbolCodec.Decode(decodeTables[t], ref r);
            int consumed = r.BitPosition - before;

            decoded.ShouldBe(s, $"round-trip symbol nSym={nSym} t={t} s={s}");
            consumed.ShouldBe(length, $"consumed bits nSym={nSym} t={t} s={s}");
        }
    }

    [Theory]
    [MemberData(nameof(Alphabets))]
    public void AdaptiveHuffman_SelectsTable_AndRoundTrips(int nSym, int tableCount)
    {
        // After init, AdaptDiscriminant selects the table; encode/decode through
        // the AdaptiveHuffman instance must round-trip every symbol.
        var h = new AdaptiveHuffman(nSym);
        h.AdaptDiscriminant();

        for (var s = 0; s < nSym; s++)
        {
            var w = new BitWriter();
            h.Encode(w, s);
            w.WriteBits(0, 16);
            var r = new BitReader(w.AsSpan());
            h.Decode(ref r).ShouldBe(s, $"nSym={nSym} s={s}");
        }
        _ = tableCount;
    }

    [Theory]
    [MemberData(nameof(Alphabets))]
    public void DecodeTables_HaveExactJxrlibSize(int nSym, int tableCount)
    {
        // Root (32 = 2^5) + per-alphabet extension nodes, exactly as declared in adapthuff.c.
        int expected = nSym switch
        {
            4 => 40, 5 => 42, 6 => 44, 7 => 46, 8 => 48, 9 => 50, 12 => 56, _ => -1,
        };
        foreach (var table in VlcTables.DecodeTables(nSym))
            table.Length.ShouldBe(expected, $"decode table size nSym={nSym}");
        _ = tableCount;
    }
}
