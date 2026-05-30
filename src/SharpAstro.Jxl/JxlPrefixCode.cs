using System.Numerics;

namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL prefix (Huffman) code, Brotli-derived (ISO/IEC 18181-1 §C.2.4). Code lengths are
/// read from the bitstream via a "simple" form (≤4 explicit symbols) or a "complex" form (a
/// meta prefix code over code-length codes, with run-length repeats), then a two-level lookup
/// table is built. Symbols are read LSB-first, so the table is indexed by bit-reversed codes.
/// Faithful port of jxl-coding prefix.rs.
/// </summary>
internal sealed class JxlPrefixCode
{
    private const int MaxPrefixBits = 15;
    private const int MaxToplevelBits = 10;

    private readonly int _toplevelBits;
    private readonly uint _toplevelMask;
    private readonly Entry[] _toplevel;
    private readonly Entry[] _secondLevel;

    private struct Entry
    {
        public bool Nested;
        public byte BitsOrMask;
        public ushort SymbolOrOffset;
    }

    private JxlPrefixCode(int toplevelBits, uint toplevelMask, Entry[] toplevel, Entry[] secondLevel)
    {
        _toplevelBits = toplevelBits;
        _toplevelMask = toplevelMask;
        _toplevel = toplevel;
        _secondLevel = secondLevel;
    }

    public static JxlPrefixCode Parse(ref JxlBitReader br, uint alphabetSize)
    {
        if (alphabetSize == 1)
            return WithSingleSymbol(0);
        if (alphabetSize > (1u << MaxPrefixBits))
            throw new InvalidDataException($"JPEG XL prefix alphabet size {alphabetSize} too large.");

        uint hskip = br.ReadBits(2);
        return hskip == 1
            ? ParseSimple(ref br, alphabetSize)
            : ParseComplex(ref br, alphabetSize, hskip);
    }

    public uint ReadSymbol(ref JxlBitReader br)
    {
        uint peeked = br.PeekBits(MaxPrefixBits);
        Entry top = _toplevel[(int)(peeked & _toplevelMask)];
        if (top.Nested)
        {
            uint chunkOffset = (peeked >> _toplevelBits) & top.BitsOrMask;
            Entry second = _secondLevel[top.SymbolOrOffset + chunkOffset];
            br.ConsumeBits(second.BitsOrMask);
            return second.SymbolOrOffset;
        }
        br.ConsumeBits(top.BitsOrMask);
        return top.SymbolOrOffset;
    }

    /// <summary>If the code always decodes to the same symbol (a 0-bit code), returns it.</summary>
    public uint? SingleSymbol()
        => _toplevel is [{ Nested: false, BitsOrMask: 0, SymbolOrOffset: var s }] ? s : null;

    private static JxlPrefixCode WithSingleSymbol(ushort symbol)
    {
        var entry = new Entry { Nested = false, BitsOrMask = 0, SymbolOrOffset = symbol };
        return new JxlPrefixCode(0, 0, [entry], []);
    }

    private static JxlPrefixCode ParseSimple(ref JxlBitReader br, uint alphabetSize)
    {
        int alphabetBits = JxlIntegerConfig.AddLog2Ceil(alphabetSize - 1);
        uint nsym = br.ReadBits(2) + 1;

        if (nsym == 1)
        {
            uint sym = br.ReadBits(alphabetBits);
            if (sym >= alphabetSize)
                throw new InvalidDataException("JPEG XL invalid simple prefix histogram.");
            return WithSingleSymbol((ushort)sym);
        }

        // (symbol, code-length) pairs for the fixed simple-tree shapes.
        Span<uint> syms = stackalloc uint[4];
        Span<byte> lens = stackalloc byte[4];
        int count;
        switch (nsym)
        {
            case 2:
                syms[0] = br.ReadBits(alphabetBits);
                syms[1] = br.ReadBits(alphabetBits);
                lens[0] = 1; lens[1] = 1;
                count = 2;
                break;
            case 3:
                syms[0] = br.ReadBits(alphabetBits);
                syms[1] = br.ReadBits(alphabetBits);
                syms[2] = br.ReadBits(alphabetBits);
                lens[0] = 1; lens[1] = 2; lens[2] = 2;
                count = 3;
                break;
            default: // 4
                syms[0] = br.ReadBits(alphabetBits);
                syms[1] = br.ReadBits(alphabetBits);
                syms[2] = br.ReadBits(alphabetBits);
                syms[3] = br.ReadBits(alphabetBits);
                bool treeSelector = br.ReadBit();
                if (treeSelector) { lens[0] = 1; lens[1] = 2; lens[2] = 3; lens[3] = 3; }
                else { lens[0] = 2; lens[1] = 2; lens[2] = 2; lens[3] = 2; }
                count = 4;
                break;
        }

        var codeLengths = new byte[alphabetSize];
        for (int i = 0; i < count; i++)
        {
            if (syms[i] >= alphabetSize)
                throw new InvalidDataException("JPEG XL invalid simple prefix histogram.");
            codeLengths[syms[i]] = lens[i];
        }
        return WithCodeLengths(codeLengths);
    }

    private static readonly int[] CodeLengthOrder = [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static JxlPrefixCode ParseComplex(ref JxlBitReader br, uint alphabetSize, uint hskip)
    {
        var clcl = new byte[18]; // code-length-code lengths
        int bitacc = 0;
        int nonzeroCount = 0;
        int nonzeroSym = 0;
        foreach (int idx in CodeLengthOrder.AsSpan((int)hskip))
        {
            uint baseLen = br.ReadU32((0, 0), (4, 0), (3, 0), (8, 0));
            byte len;
            if (baseLen == 8)
                len = br.ReadBit() ? (br.ReadBit() ? (byte)5 : (byte)1) : (byte)2;
            else
                len = (byte)baseLen;

            clcl[idx] = len;
            if (len != 0)
            {
                nonzeroCount++;
                nonzeroSym = idx;
                bitacc += 32 >> len;
                if (bitacc == 32)
                    break;
                if (bitacc > 32)
                    throw new InvalidDataException("JPEG XL invalid prefix histogram (code-length codes overflow).");
            }
        }

        JxlPrefixCode clHistogram = nonzeroCount == 1
            ? WithSingleSymbol((ushort)nonzeroSym)
            : bitacc != 32
                ? throw new InvalidDataException("JPEG XL invalid prefix histogram (code-length codes underflow).")
                : WithCodeLengths(clcl);

        var codeLengths = new byte[alphabetSize];
        long acc = 0;
        byte prevSym = 8;
        byte lastNonzeroSym = 8;
        long lastRepeatCount = 0;
        int repeatCount = 0;
        byte repeatSym = 0;

        for (int i = 0; i < codeLengths.Length; i++)
        {
            byte len;
            if (repeatCount > 0)
            {
                len = repeatSym;
                repeatCount--;
            }
            else
            {
                byte sym = (byte)clHistogram.ReadSymbol(ref br);
                switch (sym)
                {
                    case 0:
                        len = 0;
                        break;
                    case >= 1 and <= 15:
                        len = sym;
                        lastNonzeroSym = sym;
                        break;
                    case 16:
                        repeatCount = (int)br.ReadBits(2) + 3;
                        if (prevSym == 16)
                        {
                            repeatCount += (int)(lastRepeatCount * 3 - 8);
                            lastRepeatCount += repeatCount;
                        }
                        else
                        {
                            lastRepeatCount = repeatCount;
                        }
                        repeatSym = lastNonzeroSym;
                        len = repeatSym;
                        repeatCount--;
                        break;
                    default: // 17
                        repeatCount = (int)br.ReadBits(3) + 3;
                        if (prevSym == 17)
                        {
                            repeatCount += (int)(lastRepeatCount * 7 - 16);
                            lastRepeatCount += repeatCount;
                        }
                        else
                        {
                            lastRepeatCount = repeatCount;
                        }
                        repeatSym = 0;
                        len = repeatSym;
                        repeatCount--;
                        break;
                }
                prevSym = sym;
            }

            codeLengths[i] = len;
            if (len != 0)
            {
                acc += 1L << (MaxPrefixBits - len);
                if (acc > 1L << MaxPrefixBits)
                    throw new InvalidDataException("JPEG XL invalid prefix histogram (lengths overflow).");
                if (acc == 1L << MaxPrefixBits && repeatCount == 0)
                    break;
            }
        }

        if (acc != 1L << MaxPrefixBits || repeatCount > 0)
            throw new InvalidDataException("JPEG XL invalid prefix histogram (lengths underflow).");
        return WithCodeLengths(codeLengths);
    }

    private static JxlPrefixCode WithCodeLengths(byte[] codeLengths)
    {
        // Group symbols by code length (symbols within a length stay in increasing order).
        var symsForLength = new List<List<ushort>>();
        for (int sym = 0; sym < codeLengths.Length; sym++)
        {
            int len = codeLengths[sym];
            if (len == 0)
                continue;
            while (symsForLength.Count < len)
                symsForLength.Add([]);
            symsForLength[len - 1].Add((ushort)sym);
        }

        int toplevelBits = Math.Min(symsForLength.Count, MaxToplevelBits);
        var entries = new Entry[1 << toplevelBits];
        uint currentBits = 0;
        for (int idx = 0; idx < toplevelBits; idx++)
        {
            int shifts = toplevelBits - 1 - idx;
            foreach (ushort sym in symsForLength[idx])
            {
                var entry = new Entry { Nested = false, BitsOrMask = (byte)(idx + 1), SymbolOrOffset = sym };
                int span = 1 << shifts;
                for (int j = 0; j < span; j++)
                    entries[currentBits + j] = entry;
                currentBits += (uint)span;
            }
        }

        var secondLevel = new List<Entry>();
        if (toplevelBits < symsForLength.Count)
        {
            var remaining = new List<Entry>();
            int remainingEntryBits = 0;
            for (int idx = toplevelBits; idx < symsForLength.Count; idx++)
            {
                List<ushort> syms = symsForLength[idx];
                if (syms.Count == 0)
                    continue;

                int chunkSizeBits = idx + 1 - toplevelBits;
                int chunkSize = 1 << chunkSizeBits;
                var chunk = new List<Entry>(chunkSize);
                if (remaining.Count > 0)
                {
                    int mult = 1 << (chunkSizeBits - remainingEntryBits);
                    foreach (Entry e in remaining)
                        for (int m = 0; m < mult; m++)
                            chunk.Add(e);
                }
                foreach (ushort sym in syms)
                {
                    chunk.Add(new Entry { Nested = false, BitsOrMask = (byte)(idx + 1), SymbolOrOffset = sym });
                    if (chunk.Count == chunkSize)
                    {
                        entries[currentBits] = new Entry
                        {
                            Nested = true,
                            BitsOrMask = (byte)(chunkSize - 1),
                            SymbolOrOffset = (ushort)secondLevel.Count,
                        };
                        ReverseBitsInto(chunk, secondLevel);
                        currentBits++;
                        chunk = new List<Entry>(chunkSize);
                    }
                }
                remaining = chunk;
                remainingEntryBits = chunkSizeBits;
            }

            if (remaining.Count > 0)
                throw new InvalidDataException("JPEG XL invalid prefix histogram (incomplete second level).");
        }

        if (currentBits != 1u << toplevelBits)
            throw new InvalidDataException("JPEG XL invalid prefix histogram (toplevel table not filled).");

        var toplevel = new List<Entry>(entries.Length);
        ReverseBitsInto(entries, toplevel);
        return new JxlPrefixCode(toplevelBits, (1u << toplevelBits) - 1, toplevel.ToArray(), secondLevel.ToArray());
    }

    // Append v's entries to dst in bit-reversed index order (v.Count must be a power of two).
    private static void ReverseBitsInto(IReadOnlyList<Entry> v, List<Entry> dst)
    {
        int len = v.Count;
        int bits = BitOperations.TrailingZeroCount((uint)len);
        for (int idx = 0; idx < len; idx++)
            dst.Add(v[(int)ReverseLowBits((uint)idx, bits)]);
    }

    internal static uint ReverseLowBits(uint value, int bits)
    {
        uint result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}
