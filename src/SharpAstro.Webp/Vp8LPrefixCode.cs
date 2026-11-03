namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 section 3.7.2 only. No libwebp,
// libvpx or RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are black-box
// test oracles.

/// <summary>
/// One VP8L prefix code built from a symbol histogram: the code lengths, the codes the writer
/// emits, and the RFC 9649 section 3.7.2.1 header that transmits them (simple form for up to two
/// symbols below 256, otherwise the normal form with a code-length code).
/// </summary>
internal sealed class Vp8LPrefixCode
{
    // RFC 9649 section 3.7.2.1.2: the order the 19 code-length code lengths are stored in.
    private static readonly int[] CodeLengthCodeOrder =
        { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    // The code-length code is itself length-limited, to what its 3-bit lengths can store.
    private const int CodeLengthCodeMaxLength = 7;

    private readonly int _alphabetSize;
    private readonly int _usedSymbols;
    private readonly int[] _lengths;
    private readonly uint[] _codes;
    private readonly int[] _emitBits;

    private Vp8LPrefixCode(int alphabetSize, int[] lengths, uint[] codes, int usedSymbols)
    {
        _alphabetSize = alphabetSize;
        _lengths = lengths;
        _codes = codes;
        _usedSymbols = usedSymbols;

        // A code with one symbol is a zero-bit code: every use of it writes nothing.
        _emitBits = usedSymbols <= 1 ? new int[alphabetSize] : lengths;
    }

    /// <summary>The code length per symbol (0 for symbols that never occur), at most 15.</summary>
    public ReadOnlySpan<int> Lengths => _lengths;

    /// <summary>How many symbols occur.</summary>
    public int UsedSymbols => _usedSymbols;

    /// <summary>Builds the code for <paramref name="histogram"/>, whose length is the alphabet size.</summary>
    public static Vp8LPrefixCode Build(ReadOnlySpan<int> histogram)
    {
        int size = histogram.Length;
        int[] lengths = new int[size];
        Vp8LHuffman.BuildLengths(histogram, Vp8LHuffman.MaxCodeLength, lengths);
        uint[] codes = new uint[size];
        Vp8LHuffman.AssignCodes(lengths, codes);
        int used = 0;
        for (int i = 0; i < size; i++)
        {
            if (lengths[i] != 0)
            {
                used++;
            }
        }

        return new Vp8LPrefixCode(size, lengths, codes, used);
    }

    /// <summary>The bits the symbols of <paramref name="histogram"/> cost with this code (extra bits not included).</summary>
    public long DataBits(ReadOnlySpan<int> histogram)
    {
        long bits = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            bits += (long)histogram[i] * _emitBits[i];
        }

        return bits;
    }

    /// <summary>The bits <see cref="WriteHeader"/> writes.</summary>
    public long HeaderBits()
    {
        var scratch = new Vp8LBitWriter(64);
        WriteHeader(scratch);
        return scratch.BitCount;
    }

    /// <summary>Writes one occurrence of <paramref name="symbol"/>.</summary>
    public void WriteSymbol(Vp8LBitWriter writer, int symbol) => writer.WriteBits(_codes[symbol], _emitBits[symbol]);

    /// <summary>Writes the code's description (RFC 9649 section 3.7.2.1).</summary>
    public void WriteHeader(Vp8LBitWriter writer)
    {
        if (_usedSymbols <= 2 && LastUsedSymbol() < 256)
        {
            WriteSimple(writer);
            return;
        }

        WriteNormal(writer);
    }

    private int LastUsedSymbol()
    {
        for (int i = _alphabetSize - 1; i >= 0; i--)
        {
            if (_lengths[i] != 0)
            {
                return i;
            }
        }

        return 0;
    }

    private void WriteSimple(Vp8LBitWriter writer)
    {
        // An empty histogram (a code nothing uses, like the distance code here) is coded as the
        // one symbol 0, which costs no bits per use.
        int first = -1;
        int second = -1;
        for (int i = 0; i < _alphabetSize; i++)
        {
            if (_lengths[i] == 0)
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }
            else
            {
                second = i;
            }
        }

        if (first < 0)
        {
            first = 0;
        }

        writer.WriteBits(1, 1); // simple_code_or_not
        writer.WriteBits(second >= 0 ? 1u : 0u, 1); // num_symbols - 1
        bool wide = first > 1;
        writer.WriteBits(wide ? 1u : 0u, 1); // is_first_8bits
        writer.WriteBits((uint)first, wide ? 8 : 1);
        if (second >= 0)
        {
            writer.WriteBits((uint)second, 8);
        }
    }

    private readonly record struct Token(int Symbol, int Extra, int ExtraBits);

    private sealed class NormalPlan
    {
        public required List<Token> Tokens;
        public required int[] CodeLengthLengths;
        public required uint[] CodeLengthCodes;
        public required bool UseMaxSymbol;
        public required long Cost;
    }

    private void WriteNormal(Vp8LBitWriter writer)
    {
        int trimmedCount = LastUsedSymbol() + 1;
        NormalPlan full = Plan(_alphabetSize, useMaxSymbol: false);
        NormalPlan trimmed = Plan(trimmedCount, useMaxSymbol: true);
        NormalPlan plan = trimmed.Cost < full.Cost ? trimmed : full;

        writer.WriteBits(0, 1); // simple_code_or_not
        int stored = 4;
        for (int i = 0; i < CodeLengthCodeOrder.Length; i++)
        {
            if (plan.CodeLengthLengths[CodeLengthCodeOrder[i]] != 0)
            {
                stored = Math.Max(stored, i + 1);
            }
        }

        writer.WriteBits((uint)(stored - 4), 4);
        for (int i = 0; i < stored; i++)
        {
            writer.WriteBits((uint)plan.CodeLengthLengths[CodeLengthCodeOrder[i]], 3);
        }

        if (plan.UseMaxSymbol)
        {
            int nbits = MaxSymbolBits(plan.Tokens.Count);
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)((nbits - 2) / 2), 3);
            writer.WriteBits((uint)(plan.Tokens.Count - 2), nbits);
        }
        else
        {
            writer.WriteBits(0, 1);
        }

        foreach (Token token in plan.Tokens)
        {
            writer.WriteBits(plan.CodeLengthCodes[token.Symbol], plan.CodeLengthLengths[token.Symbol]);
            writer.WriteBits((uint)token.Extra, token.ExtraBits);
        }
    }

    // max_symbol is 2 + a (2 + 2k)-bit value, k in 0..7.
    private static int MaxSymbolBits(int tokenCount)
    {
        int nbits = 2;
        while (nbits < 16 && (tokenCount - 2) >= 1 << nbits)
        {
            nbits += 2;
        }

        return nbits;
    }

    private NormalPlan Plan(int count, bool useMaxSymbol)
    {
        List<Token> tokens = Tokenize(count);

        int[] histogram = new int[19];
        foreach (Token token in tokens)
        {
            histogram[token.Symbol]++;
        }

        // A one-symbol code-length code would be a zero-bit code; give it a second symbol so it
        // is an ordinary complete code every decoder reads the same way.
        int distinct = histogram.Count(static h => h > 0);
        if (distinct == 1)
        {
            int other = histogram[0] > 0 ? 1 : 0;
            histogram[other] = 1;
        }

        int[] lengths = new int[19];
        Vp8LHuffman.BuildLengths(histogram, CodeLengthCodeMaxLength, lengths);
        uint[] codes = new uint[19];
        Vp8LHuffman.AssignCodes(lengths, codes);

        long cost = 1 + 4; // simple_code_or_not, number_of_code_lengths
        int stored = 4;
        for (int i = 0; i < CodeLengthCodeOrder.Length; i++)
        {
            if (lengths[CodeLengthCodeOrder[i]] != 0)
            {
                stored = Math.Max(stored, i + 1);
            }
        }

        cost += 3L * stored;
        cost += useMaxSymbol ? 4 + MaxSymbolBits(tokens.Count) : 1;
        foreach (Token token in tokens)
        {
            cost += lengths[token.Symbol] + token.ExtraBits;
        }

        return new NormalPlan
        {
            Tokens = tokens,
            CodeLengthLengths = lengths,
            CodeLengthCodes = codes,
            UseMaxSymbol = useMaxSymbol,
            Cost = cost,
        };
    }

    // Turns the first `count` code lengths into code-length symbols: literal lengths 0..15, 16
    // (repeat the previous non-zero length 3-6 times, which starts as 8), 17 (3-10 zeros) and 18
    // (11-138 zeros).
    private List<Token> Tokenize(int count)
    {
        var tokens = new List<Token>();
        int previous = 8;
        int i = 0;
        while (i < count)
        {
            int value = _lengths[i];
            int run = 1;
            while (i + run < count && _lengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run > 0)
                {
                    if (run >= 11)
                    {
                        int r = Math.Min(run, 138);
                        tokens.Add(new Token(18, r - 11, 7));
                        run -= r;
                    }
                    else if (run >= 3)
                    {
                        tokens.Add(new Token(17, run - 3, 3));
                        run = 0;
                    }
                    else
                    {
                        tokens.Add(new Token(0, 0, 0));
                        run--;
                    }
                }

                continue;
            }

            if (value != previous)
            {
                tokens.Add(new Token(value, 0, 0));
                previous = value;
                run--;
            }

            while (run >= 3)
            {
                int r = Math.Min(run, 6);
                tokens.Add(new Token(16, r - 3, 2));
                run -= r;
            }

            for (; run > 0; run--)
            {
                tokens.Add(new Token(value, 0, 0));
            }
        }

        return tokens;
    }
}
