using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// JPEG XL entropy coding (ISO/IEC 18181-1 §C.2) — validated by self round-trip, since the
/// entropy layer has no isolated Magick oracle (Magick only round-trips whole files). Our
/// encoder and decoder are faithful inverses of jxl-coding; encode→decode identity over a
/// spread of inputs is the regression guard. The decoder is re-validated end-to-end against
/// real libjxl bytes once Modular decode (Rung 4) lands.
///   Rung 3a: hybrid integer (token + trailing bits) config + value round-trip.
/// </summary>
public sealed class JxlEntropyTests
{
    [Fact]
    public void IntegerConfig_SerializesAndParsesBack()
    {
        foreach (int las in (int[])[5, 6, 7, 8, 15])
        {
            foreach (JxlIntegerConfig cfg in ConfigsFor(las))
            {
                var bw = new JxlBitWriter();
                cfg.Write(bw, las);
                var br = new JxlBitReader(bw.ToArray());
                JxlIntegerConfig back = JxlIntegerConfig.Parse(ref br, las);

                string label = $"las={las} se={cfg.SplitExponent} msb={cfg.MsbInToken} lsb={cfg.LsbInToken}";
                back.SplitExponent.ShouldBe(cfg.SplitExponent, label);
                back.MsbInToken.ShouldBe(cfg.MsbInToken, label);
                back.LsbInToken.ShouldBe(cfg.LsbInToken, label);
                back.Split.ShouldBe(cfg.Split, label);
            }
        }
    }

    [Fact]
    public void HybridUint_TokenAndTrailingBits_RoundTrip()
    {
        uint[] values = [0, 1, 2, 3, 7, 8, 15, 16, 31, 32, 63, 100, 255, 256, 1000, 65535, 65536, 1_000_000, 16_777_215];

        // The token itself rides the entropy coder (tested at later rungs); here we drive it
        // directly and only round-trip the config's trailing-bit expansion.
        foreach (int las in (int[])[5, 8, 15])
        {
            foreach (JxlIntegerConfig cfg in ConfigsFor(las))
            {
                foreach (uint value in values)
                {
                    uint token = cfg.EncodeToken(value, out uint restBits, out int restBitCount);

                    var bw = new JxlBitWriter();
                    bw.WriteBits(restBits, restBitCount);
                    var br = new JxlBitReader(bw.ToArray());
                    uint decoded = cfg.ReadUint(ref br, token);

                    decoded.ShouldBe(value, $"las={las} se={cfg.SplitExponent} msb={cfg.MsbInToken} lsb={cfg.LsbInToken} v={value}");
                    if (value < cfg.Split)
                        token.ShouldBe(value); // literal token range
                }
            }
        }
    }

    [Fact]
    public void PrefixCode_SimpleTrees_RoundTrip()
    {
        // The four "simple" prefix-tree shapes (ISO §C.2.4): nsym 1..4 with fixed length sets.
        // Exercises the toplevel-table build + read_symbol for code lengths up to 3. The complex
        // form and the nested second-level table (codes longer than 10 bits) are validated
        // end-to-end against real libjxl bytes at Rung 4.
        uint alphabet = 32;
        (uint[] syms, byte[] lens)[] trees =
        [
            ([7], [0]),                         // nsym=1: single 0-bit symbol
            ([3, 20], [1, 1]),                  // nsym=2: two length-1 codes
            ([1, 9, 30], [1, 2, 2]),            // nsym=3
            ([2, 5, 11, 19], [1, 2, 3, 3]),     // nsym=4, tree_selector = true
            ([4, 8, 12, 16], [2, 2, 2, 2]),     // nsym=4, tree_selector = false
        ];

        foreach ((uint[] syms, byte[] lens) in trees)
        {
            var bw = new JxlBitWriter();
            WriteSimpleHeader(bw, alphabet, syms, lens);

            // Encode a stream cycling through the alphabet's symbols.
            var codeLengths = new byte[alphabet];
            for (int i = 0; i < syms.Length; i++)
                codeLengths[syms[i]] = lens[i];
            uint[] codes = CanonicalCodes(codeLengths);

            uint[] stream = [.. Enumerable.Range(0, 25).Select(i => syms[i % syms.Length])];
            foreach (uint s in stream)
                bw.WriteBits(JxlPrefixCode.ReverseLowBits(codes[s], codeLengths[s]), codeLengths[s]);

            var br = new JxlBitReader(bw.ToArray());
            JxlPrefixCode code = JxlPrefixCode.Parse(ref br, alphabet);
            foreach (uint expected in stream)
                code.ReadSymbol(ref br).ShouldBe(expected, $"nsym={syms.Length}");

            if (syms.Length == 1)
                code.SingleSymbol().ShouldBe(syms[0]);
        }
    }

    [Fact]
    public void Ans_EvenDistribution_RoundTrips()
    {
        // The evenly-distributed form spreads frequencies across many symbols, exercising the
        // alias-table over/under-full balancing and the rANS renorm/word-ordering most heavily.
        foreach (int las in (int[])[5, 6, 7, 8])
        {
            int tableSize = 1 << las;
            foreach (int alphabet in (int[])[2, 3, 5, 8, 17])
            {
                if (alphabet > tableSize)
                    continue;
                uint[] stream = [.. Enumerable.Range(0, 300).Select(i => (uint)((i * 7 + i / 5) % alphabet))];
                RoundTrip(las, hw => WriteEvenHeader(hw, alphabet), stream, $"even las={las} A={alphabet}");
            }
        }
    }

    [Fact]
    public void Ans_BinaryAndUnary_RoundTrip()
    {
        // Binary (two skewed symbols) and unary (single symbol) distribution forms.
        const int las = 8;
        uint[] binStream = [.. Enumerable.Range(0, 400).Select(i => (uint)(i % 5 == 0 ? 1 : 0))];
        RoundTrip(las, hw => WriteBinaryHeader(hw, v0: 0, v1: 1, prob: 3200), binStream, "binary");

        uint[] unaryStream = [.. Enumerable.Repeat(42u, 50)];
        RoundTrip(las, hw => WriteUnaryHeader(hw, symbol: 42), unaryStream, "unary");
    }

    private static void RoundTrip(int las, Action<JxlBitWriter> writeHeader, uint[] stream, string label)
    {
        // Build the histogram from its own serialized header so the encoder and decoder agree.
        var hw = new JxlBitWriter();
        writeHeader(hw);
        var hr = new JxlBitReader(hw.ToArray());
        JxlAnsHistogram hist = JxlAnsHistogram.Parse(ref hr, las);

        // Encode in reverse, collect 16-bit renorm words, reverse to stream order.
        uint state = JxlAnsHistogram.InitialState;
        var words = new List<ushort>();
        for (int k = stream.Length - 1; k >= 0; k--)
            hist.EncodeStep(ref state, (int)stream[k], words);
        words.Reverse();

        // Assemble [header][32-bit initial state][16-bit words], all one continuous bitstream.
        var bw = new JxlBitWriter();
        writeHeader(bw);
        bw.WriteBits(state, 32);
        foreach (ushort w in words)
            bw.WriteBits(w, 16);

        var br = new JxlBitReader(bw.ToArray());
        JxlAnsHistogram dec = JxlAnsHistogram.Parse(ref br, las);
        uint dstate = br.ReadBits(32);
        foreach (uint expected in stream)
            dec.ReadSymbol(ref br, ref dstate).ShouldBe(expected, label);
        dstate.ShouldBe(JxlAnsHistogram.InitialState, $"{label}: final ANS state");
    }

    private static void WriteEvenHeader(JxlBitWriter bw, int alphabetSize)
    {
        bw.WriteBit(false); // not binary/unary
        bw.WriteBit(true);  // evenly distributed
        WriteU8(bw, alphabetSize - 1);
    }

    private static void WriteBinaryHeader(JxlBitWriter bw, int v0, int v1, ushort prob)
    {
        bw.WriteBit(true);  // first branch
        bw.WriteBit(true);  // binary
        WriteU8(bw, v0);
        WriteU8(bw, v1);
        bw.WriteBits(prob, 12);
    }

    private static void WriteUnaryHeader(JxlBitWriter bw, int symbol)
    {
        bw.WriteBit(true);  // first branch
        bw.WriteBit(false); // unary
        WriteU8(bw, symbol);
    }

    private static void WriteU8(JxlBitWriter bw, int value)
    {
        if (value == 0)
        {
            bw.WriteBit(false);
            return;
        }
        bw.WriteBit(true);
        int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);
        bw.WriteBits((uint)n, 3);
        bw.WriteBits((uint)(value - (1 << n)), n);
    }

    private static void WriteSimpleHeader(JxlBitWriter bw, uint alphabet, uint[] syms, byte[] lens)
    {
        int alphabetBits = JxlIntegerConfig.AddLog2Ceil(alphabet - 1);
        bw.WriteBits(1, 2);                       // hskip = 1 -> simple
        bw.WriteBits((uint)syms.Length - 1, 2);   // nsym - 1
        foreach (uint s in syms)
            bw.WriteBits(s, alphabetBits);
        if (syms.Length == 4)
            bw.WriteBit(lens is [1, 2, 3, 3]);    // tree_selector
    }

    // Deflate-style canonical code assignment: shortest lengths first, increasing symbol order.
    private static uint[] CanonicalCodes(byte[] codeLengths)
    {
        int maxLen = codeLengths.Length == 0 ? 0 : codeLengths.Max();
        var blCount = new int[maxLen + 1];
        foreach (byte len in codeLengths)
            if (len > 0)
                blCount[len]++;

        var nextCode = new uint[maxLen + 2];
        uint code = 0;
        for (int len = 1; len <= maxLen; len++)
        {
            code = (code + (uint)blCount[len - 1]) << 1;
            nextCode[len] = code;
        }

        var codes = new uint[codeLengths.Length];
        for (int sym = 0; sym < codeLengths.Length; sym++)
        {
            int len = codeLengths[sym];
            if (len > 0)
                codes[sym] = nextCode[len]++;
        }
        return codes;
    }

    // A representative spread of valid (split_exponent, msb, lsb) triples for the alphabet size.
    private static IEnumerable<JxlIntegerConfig> ConfigsFor(int logAlphabetSize)
    {
        int[] splits = logAlphabetSize <= 8
            ? Enumerable.Range(0, logAlphabetSize + 1).ToArray()
            : [0, 1, 4, 8, 12, 15];
        foreach (int se in splits)
        {
            // When split_exponent == log_alphabet_size the msb/lsb fields aren't transmitted
            // (forced to 0), so only that triple is representable for that split.
            if (se == logAlphabetSize)
            {
                yield return JxlIntegerConfig.Create(se, 0, 0);
                continue;
            }
            for (int msb = 0; msb <= se; msb++)
                for (int lsb = 0; lsb + msb <= se; lsb++)
                    yield return JxlIntegerConfig.Create(se, msb, lsb);
        }
    }
}
