namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL ANS (asymmetric numeral system) distribution and coder (ISO/IEC 18181-1 §C.2.5).
/// The frequency table (summing to 2^12) is read in one of four forms, then spread into an
/// alias table for O(1) symbol decoding. Faithful port of jxl-coding ans.rs; the encoder is
/// the exact rANS inverse (reverse-order state machine, 16-bit renormalisation), validated by
/// self round-trip. The compressed-distribution parse form is additionally validated against
/// real libjxl bytes at Rung 4.
/// </summary>
internal sealed class JxlAnsHistogram
{
    private const int TotalLog = 12;
    private const int Total = 1 << TotalLog; // 4096
    internal const uint InitialState = 0x130000;

    private struct Bucket
    {
        public ushort Dist;
        public byte AliasSymbol;
        public byte AliasCutoff;
        public ushort AliasOffset;   // already adjusted: original offset - cutoff
        public ushort AliasDistXor;
    }

    private readonly Bucket[] _buckets;
    private readonly int _logBucketSize;
    private readonly uint _bucketMask;
    private readonly uint? _singleSymbol;
    private readonly ushort[] _dist; // per-symbol frequency, length = table_size

    // Encoder tables (built lazily): for each symbol, the slots assigned to it indexed by offset.
    private int[][]? _inverseAlias;

    private JxlAnsHistogram(Bucket[] buckets, int logBucketSize, uint? singleSymbol, ushort[] dist)
    {
        _buckets = buckets;
        _logBucketSize = logBucketSize;
        _bucketMask = (1u << logBucketSize) - 1;
        _singleSymbol = singleSymbol;
        _dist = dist;
    }

    public uint? SingleSymbol => _singleSymbol;

    public static JxlAnsHistogram Parse(ref JxlBitReader br, int logAlphabetSize)
    {
        int tableSize = 1 << logAlphabetSize;
        int logBucketSize = TotalLog - logAlphabetSize; // 4..7
        int bucketSize = 1 << logBucketSize;

        var dist = new ushort[tableSize];
        int alphabetSize;

        if (br.ReadBit())
        {
            if (br.ReadBit())
            {
                // binary: two symbols
                int v0 = ReadU8(ref br);
                int v1 = ReadU8(ref br);
                if (v0 == v1)
                    throw new InvalidDataException("JPEG XL invalid ANS histogram (binary v0 == v1).");
                alphabetSize = Math.Max(v0, v1) + 1;
                CheckAlphabet(alphabetSize, tableSize);
                ushort prob = (ushort)br.ReadBits(12);
                dist[v0] = prob;
                dist[v1] = (ushort)(Total - prob);
            }
            else
            {
                // unary: single symbol
                int val = ReadU8(ref br);
                alphabetSize = val + 1;
                CheckAlphabet(alphabetSize, tableSize);
                dist[val] = Total;
            }
        }
        else if (br.ReadBit())
        {
            // evenly distributed
            alphabetSize = ReadU8(ref br) + 1;
            CheckAlphabet(alphabetSize, tableSize);
            int baseFreq = Total / alphabetSize;
            int leftover = Total % alphabetSize;
            for (int i = 0; i < alphabetSize; i++)
                dist[i] = (ushort)(baseFreq + (i < leftover ? 1 : 0));
        }
        else
        {
            alphabetSize = ParseCompressed(ref br, dist, tableSize);
        }

        return Build(dist, alphabetSize, tableSize, logBucketSize, bucketSize);
    }

    private static int ParseCompressed(ref JxlBitReader br, ushort[] dist, int tableSize)
    {
        int len = 0;
        while (len < 3 && br.ReadBit())
            len++;
        int shift = (int)(br.ReadBits(len) + (1u << len) - 1);
        if (shift > 13)
            throw new InvalidDataException("JPEG XL invalid ANS histogram (shift > 13).");

        int alphabetSize = ReadU8(ref br) + 3;
        CheckAlphabet(alphabetSize, tableSize);

        var repeatRanges = new List<(int Start, int End)>();
        int? omitPos = null;
        int omitLog = 0;
        int idx = 0;
        while (idx < alphabetSize)
        {
            dist[idx] = ReadPrefix(ref br);
            if (dist[idx] == 13)
            {
                int repeatCount = ReadU8(ref br) + 4;
                if (idx + repeatCount > alphabetSize)
                    throw new InvalidDataException("JPEG XL invalid ANS histogram (repeat out of range).");
                repeatRanges.Add((idx, idx + repeatCount));
                idx += repeatCount;
                continue;
            }
            if (omitPos is null || dist[idx] > omitLog)
            {
                omitLog = dist[idx];
                omitPos = idx;
            }
            idx++;
        }

        if (omitPos is not { } omit)
            throw new InvalidDataException("JPEG XL invalid ANS histogram (no omit symbol).");
        if (omit + 1 < dist.Length && dist[omit + 1] == 13)
            throw new InvalidDataException("JPEG XL invalid ANS histogram (omit followed by repeat).");

        int repeatRangeIdx = 0;
        int acc = 0;
        ushort prevDist = 0;
        for (int i = 0; i < dist.Length; i++)
        {
            if (repeatRangeIdx < repeatRanges.Count && repeatRanges[repeatRangeIdx].Start <= i)
            {
                if (repeatRanges[repeatRangeIdx].End == i)
                {
                    repeatRangeIdx++;
                }
                else
                {
                    dist[i] = prevDist;
                    acc += dist[i];
                    if (acc > Total)
                        throw new InvalidDataException("JPEG XL invalid ANS histogram (sum overflow).");
                    continue;
                }
            }

            if (dist[i] == 0)
            {
                prevDist = 0;
                continue;
            }
            if (i == omit)
            {
                prevDist = 0;
                continue;
            }
            if (dist[i] > 1)
            {
                int zeros = dist[i] - 1;
                int bitcount = Math.Clamp(shift - ((12 - zeros) >> 1), 0, zeros);
                dist[i] = (ushort)((1 << zeros) + (br.ReadBits(bitcount) << (zeros - bitcount)));
            }
            prevDist = dist[i];
            acc += dist[i];
            if (acc > Total)
                throw new InvalidDataException("JPEG XL invalid ANS histogram (sum overflow).");
        }

        dist[omit] = (ushort)(Total - acc);
        return alphabetSize;
    }

    private static JxlAnsHistogram Build(ushort[] dist, int alphabetSize, int tableSize, int logBucketSize, int bucketSize)
    {
        // Single-symbol fast path: one symbol owns the entire 2^12 mass.
        for (int s = 0; s < dist.Length; s++)
        {
            if (dist[s] != Total)
                continue;
            var single = new Bucket[tableSize];
            for (int i = 0; i < tableSize; i++)
                single[i] = new Bucket
                {
                    Dist = dist[i],
                    AliasSymbol = (byte)s,
                    AliasOffset = (ushort)(bucketSize * i),
                    AliasCutoff = 0,
                    AliasDistXor = (ushort)(dist[i] ^ Total),
                };
            return new JxlAnsHistogram(single, logBucketSize, (uint)s, dist);
        }

        // Vose/alias spreading: balance over- and under-full buckets.
        var work = new WorkingBucket[tableSize];
        for (int i = 0; i < tableSize; i++)
            work[i] = new WorkingBucket
            {
                Dist = dist[i],
                AliasSymbol = i < alphabetSize ? i : 0,
                AliasOffset = 0,
                AliasCutoff = dist[i],
            };

        var underfull = new List<int>();
        var overfull = new List<int>();
        for (int i = 0; i < tableSize; i++)
        {
            if (work[i].Dist < bucketSize) underfull.Add(i);
            else if (work[i].Dist > bucketSize) overfull.Add(i);
        }

        while (overfull.Count > 0 && underfull.Count > 0)
        {
            int o = Pop(overfull);
            int u = Pop(underfull);
            int by = bucketSize - work[u].AliasCutoff;
            work[o].AliasCutoff -= by;
            work[u].AliasSymbol = o;
            work[u].AliasOffset = work[o].AliasCutoff;
            if (work[o].AliasCutoff < bucketSize) underfull.Add(o);
            else if (work[o].AliasCutoff > bucketSize) overfull.Add(o);
        }

        var buckets = new Bucket[tableSize];
        for (int i = 0; i < tableSize; i++)
        {
            WorkingBucket w = work[i];
            if (w.AliasCutoff == bucketSize)
                buckets[i] = new Bucket
                {
                    Dist = w.Dist,
                    AliasSymbol = (byte)i,
                    AliasOffset = 0,
                    AliasCutoff = 0,
                    AliasDistXor = 0,
                };
            else
                buckets[i] = new Bucket
                {
                    Dist = w.Dist,
                    AliasSymbol = (byte)w.AliasSymbol,
                    AliasOffset = (ushort)(w.AliasOffset - w.AliasCutoff),
                    AliasCutoff = (byte)w.AliasCutoff,
                    AliasDistXor = (ushort)(w.Dist ^ work[w.AliasSymbol].Dist),
                };
        }

        return new JxlAnsHistogram(buckets, logBucketSize, null, dist);
    }

    private struct WorkingBucket
    {
        public ushort Dist;
        public int AliasSymbol;
        public int AliasOffset;
        public int AliasCutoff;
    }

    private static int Pop(List<int> list)
    {
        int last = list[^1];
        list.RemoveAt(list.Count - 1);
        return last;
    }

    public uint ReadSymbol(ref JxlBitReader br, ref uint state)
    {
        uint idx = state & 0xfff;
        int i = (int)(idx >> _logBucketSize);
        uint pos = idx & _bucketMask;
        Bucket bucket = _buckets[i];

        bool mapToAlias = pos >= bucket.AliasCutoff;
        uint offset = mapToAlias ? bucket.AliasOffset : 0u;
        uint distXor = mapToAlias ? bucket.AliasDistXor : 0u;
        uint dist = bucket.Dist ^ distXor;
        uint symbol = mapToAlias ? bucket.AliasSymbol : (uint)i;
        offset += pos;

        uint nextState = (state >> 12) * dist + offset;
        bool selectAppended = nextState < (1u << 16);
        if (selectAppended)
        {
            uint refill = br.PeekBits(16);
            br.ConsumeBits(16);
            state = (nextState << 16) | refill;
        }
        else
        {
            state = nextState;
        }
        return symbol;
    }

    // ---- encoder support (reverse-order rANS) ----

    /// <summary>Frequency (out of 2^12) of <paramref name="symbol"/> in this distribution.</summary>
    public int Frequency(int symbol) => _dist[symbol];

    /// <summary>
    /// Encodes one symbol in reverse order: renormalises by emitting 16-bit words (collected in
    /// <paramref name="words"/>) then applies the rANS state transform — the exact inverse of
    /// <see cref="ReadSymbol"/>.
    /// </summary>
    public void EncodeStep(ref uint state, int symbol, List<ushort> words)
    {
        _inverseAlias ??= BuildInverseAlias();
        int freq = _dist[symbol];
        if (freq == 0)
            throw new InvalidOperationException($"JPEG XL ANS encode of zero-frequency symbol {symbol}.");

        ulong xMax = (ulong)freq << 20; // state must stay below freq * 2^20 before the transform
        while (state >= xMax)
        {
            words.Add((ushort)(state & 0xffff));
            state >>= 16;
        }

        uint q = state / (uint)freq;
        uint offset = state % (uint)freq;
        uint slot = (uint)_inverseAlias[symbol][offset];
        state = (q << 12) | slot;
    }

    private int[][] BuildInverseAlias()
    {
        var inverse = new int[_dist.Length][];
        for (int s = 0; s < _dist.Length; s++)
            inverse[s] = _dist[s] > 0 ? new int[_dist[s]] : [];

        for (uint slot = 0; slot < Total; slot++)
        {
            int i = (int)(slot >> _logBucketSize);
            uint pos = slot & _bucketMask;
            Bucket bucket = _buckets[i];
            bool mapToAlias = pos >= bucket.AliasCutoff;
            int symbol = mapToAlias ? bucket.AliasSymbol : i;
            uint offset = (mapToAlias ? bucket.AliasOffset : 0u) + pos;
            inverse[symbol][offset] = (int)slot;
        }
        return inverse;
    }

    private static void CheckAlphabet(int alphabetSize, int tableSize)
    {
        if (alphabetSize > tableSize)
            throw new InvalidDataException($"JPEG XL ANS alphabet size {alphabetSize} exceeds table size {tableSize}.");
    }

    private static int ReadU8(ref JxlBitReader br)
    {
        if (!br.ReadBit())
            return 0;
        int n = (int)br.ReadBits(3);
        return (1 << n) + (int)br.ReadBits(n);
    }

    private static ushort ReadPrefix(ref JxlBitReader br)
    {
        switch (br.ReadBits(3))
        {
            case 0: return 10;
            case 1:
                foreach (ushort val in (ushort[])[4, 0, 11, 13])
                    if (br.ReadBit())
                        return val;
                return 12;
            case 2: return 7;
            case 3: return br.ReadBit() ? (ushort)1 : (ushort)3;
            case 4: return 6;
            case 5: return 8;
            case 6: return 9;
            default: return br.ReadBit() ? (ushort)2 : (ushort)5;
        }
    }
}
