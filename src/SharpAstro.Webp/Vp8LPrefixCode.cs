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
    // Assigned when the code is first written: the encoder builds a code for every candidate it costs, and costing
    // needs only the lengths (Codecs#92).
    private uint[]? _codes;
    private readonly int[] _emitBits;

    // The cheaper of the two normal-form headers, planned once: HeaderBits costs every code the encoder considers,
    // and WriteHeader writes the few it keeps.
    private NormalPlan? _normal;

    private Vp8LPrefixCode(int alphabetSize, int[] lengths, int usedSymbols)
    {
        _alphabetSize = alphabetSize;
        _lengths = lengths;
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
        int used = 0;
        for (int i = 0; i < size; i++)
        {
            if (lengths[i] != 0)
            {
                used++;
            }
        }

        return new Vp8LPrefixCode(size, lengths, used);
    }

    /// <summary>
    /// What <see cref="DataBits"/> plus <see cref="HeaderBits"/> of the code <see cref="Build"/> makes for
    /// <paramref name="histogram"/> would be, without making it: the lengths live in scratch and nothing is kept.
    /// The encoder prices every candidate stream this way and builds codes only for the one it writes (Codecs#100).
    /// </summary>
    public static long Cost(ReadOnlySpan<int> histogram)
    {
        int size = histogram.Length;
        int[]? rented = null;
        Span<int> lengths = size <= 3072 ? stackalloc int[size] : (rented = WebpBufferPool<int>.Rent(size)).AsSpan(0, size);
        try
        {
            lengths.Clear();
            Vp8LHuffman.BuildLengths(histogram, Vp8LHuffman.MaxCodeLength, lengths);
            int used = 0;
            for (int i = 0; i < size; i++)
            {
                if (lengths[i] != 0)
                {
                    used++;
                }
            }

            // A code with one symbol is a zero-bit code: every use of it writes nothing.
            long bits = 0;
            if (used > 1)
            {
                for (int i = 0; i < size; i++)
                {
                    bits += (long)histogram[i] * lengths[i];
                }
            }

            return bits + HeaderBitsOf(lengths, used);
        }
        finally
        {
            if (rented is not null)
            {
                WebpBufferPool<int>.Return(rented);
            }
        }
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

    /// <summary>
    /// The bits <see cref="WriteHeader"/> writes, counted without writing them: the simple form's fields, or the
    /// normal form's plan cost, which counts the same fields <see cref="WriteNormal"/> writes. It used to write the
    /// header into a scratch writer for every code the encoder costed (Codecs#92).
    /// </summary>
    public long HeaderBits() => HeaderBitsOf(_lengths, _usedSymbols);

    private static long HeaderBitsOf(ReadOnlySpan<int> lengths, int usedSymbols)
    {
        if (IsSimpleCode(lengths, usedSymbols))
        {
            var (first, second) = SimpleSymbols(lengths);
            // simple_code_or_not, num_symbols - 1, is_first_8bits, the first symbol, the second.
            return 3 + (first > 1 ? 8 : 1) + (second >= 0 ? 8 : 0);
        }

        // The cheaper of the two normal-form headers (the trimmed one only when strictly cheaper, as NormalHeader).
        Span<int> scratch = stackalloc int[19];
        long full = PlanCore(lengths, lengths.Length, useMaxSymbol: false, scratch, out _);
        long trimmed = PlanCore(lengths, LastUsedSymbol(lengths) + 1, useMaxSymbol: true, scratch, out _);
        return trimmed < full ? trimmed : full;
    }

    private bool IsSimple => IsSimpleCode(_lengths, _usedSymbols);

    private static bool IsSimpleCode(ReadOnlySpan<int> lengths, int usedSymbols) =>
        usedSymbols <= 2 && LastUsedSymbol(lengths) < 256;

    private NormalPlan NormalHeader()
    {
        if (_normal is { } planned)
        {
            return planned;
        }

        NormalPlan full = Plan(_alphabetSize, useMaxSymbol: false);
        NormalPlan trimmed = Plan(LastUsedSymbol(_lengths) + 1, useMaxSymbol: true);
        return _normal = trimmed.Cost < full.Cost ? trimmed : full;
    }

    /// <summary>Writes one occurrence of <paramref name="symbol"/>.</summary>
    public void WriteSymbol(Vp8LBitWriter writer, int symbol) => writer.WriteBits(Codes[symbol], _emitBits[symbol]);

    /// <summary>The canonical codes, bit-reversed for the writer (<see cref="Vp8LHuffman.AssignCodes"/>), made on first use.</summary>
    private uint[] Codes
    {
        get
        {
            if (_codes is { } assigned)
            {
                return assigned;
            }

            var codes = new uint[_alphabetSize];
            Vp8LHuffman.AssignCodes(_lengths, codes);
            return _codes = codes;
        }
    }

    /// <summary>Writes the code's description (RFC 9649 section 3.7.2.1).</summary>
    public void WriteHeader(Vp8LBitWriter writer)
    {
        if (IsSimple)
        {
            WriteSimple(writer);
            return;
        }

        WriteNormal(writer);
    }

    private static int LastUsedSymbol(ReadOnlySpan<int> lengths)
    {
        for (int i = lengths.Length - 1; i >= 0; i--)
        {
            if (lengths[i] != 0)
            {
                return i;
            }
        }

        return 0;
    }

    private void WriteSimple(Vp8LBitWriter writer)
    {
        var (first, second) = SimpleSymbols(_lengths);
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

    /// <summary>The simple form's one or two symbols; the second is -1 for one.</summary>
    private static (int First, int Second) SimpleSymbols(ReadOnlySpan<int> lengths)
    {
        // An empty histogram (a code nothing uses, like the distance code here) is coded as the
        // one symbol 0, which costs no bits per use.
        int first = -1;
        int second = -1;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] == 0)
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

        return (first, second);
    }

    /// <summary>Where <see cref="Tokenize"/> sends each code-length symbol, with its extra bits.</summary>
    private interface ITokenSink : allows ref struct
    {
        void Add(int symbol, int extra, int extraBits);
    }

    /// <summary>Counts the symbols and their extra bits: all a plan's cost needs.</summary>
    private ref struct CountingSink(Span<int> histogram) : ITokenSink
    {
        public readonly Span<int> Histogram = histogram;
        public int Tokens;
        public long ExtraBits;

        public void Add(int symbol, int extra, int extraBits)
        {
            Histogram[symbol]++;
            Tokens++;
            ExtraBits += extraBits;
        }
    }

    /// <summary>Writes each symbol with the code-length code, then its extra bits.</summary>
    private readonly struct WritingSink(Vp8LBitWriter writer, int[] lengths, uint[] codes) : ITokenSink
    {
        public void Add(int symbol, int extra, int extraBits)
        {
            writer.WriteBits(codes[symbol], lengths[symbol]);
            writer.WriteBits((uint)extra, extraBits);
        }
    }

    /// <summary>
    /// A normal-form header, planned: how many lengths it codes and how many tokens that takes, its code-length code,
    /// and its cost. The tokens themselves are not kept: costing every code the encoder considers used to hold two
    /// lists of them a code, sized to the alphabet (Codecs#92), and the writer walks the lengths again instead.
    /// </summary>
    private sealed class NormalPlan
    {
        public required int Count;
        public required int TokenCount;
        public required int[] CodeLengthLengths;
        public required uint[] CodeLengthCodes;
        public required bool UseMaxSymbol;
        public required long Cost;
    }

    private void WriteNormal(Vp8LBitWriter writer)
    {
        NormalPlan plan = NormalHeader();

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
            int nbits = MaxSymbolBits(plan.TokenCount);
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)((nbits - 2) / 2), 3);
            writer.WriteBits((uint)(plan.TokenCount - 2), nbits);
        }
        else
        {
            writer.WriteBits(0, 1);
        }

        var sink = new WritingSink(writer, plan.CodeLengthLengths, plan.CodeLengthCodes);
        Tokenize(_lengths, plan.Count, ref sink);
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
        int[] lengths = new int[19];
        long cost = PlanCore(_lengths, count, useMaxSymbol, lengths, out int tokens);
        uint[] codes = new uint[19];
        Vp8LHuffman.AssignCodes(lengths, codes);

        return new NormalPlan
        {
            Count = count,
            TokenCount = tokens,
            CodeLengthLengths = lengths,
            CodeLengthCodes = codes,
            UseMaxSymbol = useMaxSymbol,
            Cost = cost,
        };
    }

    // Costs a normal-form header for the first `count` lengths and leaves its code-length code's lengths in
    // `codeLengthLengths` (19 entries). All on the stack: costing every candidate the encoder considers used to
    // allocate two arrays and a plan for each (Codecs#100).
    private static long PlanCore(
        ReadOnlySpan<int> lengthsOfCode,
        int count,
        bool useMaxSymbol,
        Span<int> codeLengthLengths,
        out int tokens)
    {
        Span<int> histogram = stackalloc int[19];
        histogram.Clear();
        var counting = new CountingSink(histogram);
        Tokenize(lengthsOfCode, count, ref counting);
        // What the tokens cost is counted on the real histogram, before the second symbol below is added.
        Span<int> occurrences = stackalloc int[19];
        histogram.CopyTo(occurrences);

        // A one-symbol code-length code would be a zero-bit code; give it a second symbol so it
        // is an ordinary complete code every decoder reads the same way.
        int distinct = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > 0)
            {
                distinct++;
            }
        }

        if (distinct == 1)
        {
            int other = histogram[0] > 0 ? 1 : 0;
            histogram[other] = 1;
        }

        codeLengthLengths.Clear();
        Vp8LHuffman.BuildLengths(histogram, CodeLengthCodeMaxLength, codeLengthLengths);

        long cost = 1 + 4; // simple_code_or_not, number_of_code_lengths
        int stored = 4;
        for (int i = 0; i < CodeLengthCodeOrder.Length; i++)
        {
            if (codeLengthLengths[CodeLengthCodeOrder[i]] != 0)
            {
                stored = Math.Max(stored, i + 1);
            }
        }

        cost += 3L * stored;
        cost += useMaxSymbol ? 4 + MaxSymbolBits(counting.Tokens) : 1;
        for (int symbol = 0; symbol < occurrences.Length; symbol++)
        {
            cost += (long)occurrences[symbol] * codeLengthLengths[symbol];
        }

        cost += counting.ExtraBits;
        tokens = counting.Tokens;
        return cost;
    }

    // Turns the first `count` code lengths into code-length symbols: literal lengths 0..15, 16
    // (repeat the previous non-zero length 3-6 times, which starts as 8), 17 (3-10 zeros) and 18
    // (11-138 zeros).
    private static void Tokenize<TSink>(ReadOnlySpan<int> lengths, int count, ref TSink sink)
        where TSink : struct, ITokenSink, allows ref struct
    {
        int previous = 8;
        int i = 0;
        while (i < count)
        {
            int value = lengths[i];
            int run = 1;
            while (i + run < count && lengths[i + run] == value)
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
                        sink.Add(18, r - 11, 7);
                        run -= r;
                    }
                    else if (run >= 3)
                    {
                        sink.Add(17, run - 3, 3);
                        run = 0;
                    }
                    else
                    {
                        sink.Add(0, 0, 0);
                        run--;
                    }
                }

                continue;
            }

            if (value != previous)
            {
                sink.Add(value, 0, 0);
                previous = value;
                run--;
            }

            while (run >= 3)
            {
                int r = Math.Min(run, 6);
                sink.Add(16, r - 3, 2);
                run -= r;
            }

            for (; run > 0; run--)
            {
                sink.Add(value, 0, 0);
            }
        }
    }
}
