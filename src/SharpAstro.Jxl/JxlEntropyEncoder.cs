using System.Numerics;

namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL entropy stream encoder (ISO/IEC 18181-1 §C.2) — the inverse of
/// <see cref="JxlEntropyDecoder"/>. Emits an LZ77-disabled, ANS-coded stream: a simple context
/// map, evenly-distributed histograms (correctness over compression for now), then the 32-bit
/// initial rANS state followed by per-symbol [renorm word?][hybrid-uint extra bits], interleaved
/// in forward order to match the decoder's read order (read_symbol then read_uint per varint).
/// The rANS pass runs in reverse; each symbol emits at most one 16-bit word.
/// </summary>
internal sealed class JxlEntropyEncoder
{
    private readonly byte[] _contextMap; // context -> cluster
    private readonly int _numClusters;
    private readonly JxlIntegerConfig[] _configs; // per cluster

    public JxlEntropyEncoder(byte[] contextMap, JxlIntegerConfig[] configsPerCluster)
    {
        _contextMap = contextMap;
        _configs = configsPerCluster;
        _numClusters = configsPerCluster.Length;
    }

    /// <summary>
    /// A prepared entropy plan: the per-cluster alphabet sizes, the shared log_alphabet_size, and the
    /// parsed even histograms — computed by analysing one or more symbol streams. The histogram
    /// <em>config</em> is written once via <see cref="WriteConfig"/>; each stream's coded <em>data</em>
    /// (initial rANS state + symbols) is written via <see cref="WriteData"/>. This split mirrors the
    /// decoder's <c>Parse</c> (config) vs <c>Begin</c>/<c>ReadVarint</c> (data), and is what lets VarDCT
    /// put the entropy config in HfGlobal while the coded symbols live in each PassGroup.
    /// </summary>
    public sealed class Plan
    {
        internal required int Las { get; init; }
        internal required int[] Alphabet { get; init; }
        internal required JxlAnsHistogram[] Hist { get; init; }
    }

    /// <summary>
    /// Analyse one or more symbol streams (sharing this encoder's context map and configs) and build
    /// the shared histogram plan. All streams that will later be passed to <see cref="WriteData"/>
    /// must be included here so the per-cluster alphabets cover every token that gets coded.
    /// </summary>
    public Plan Prepare(params IReadOnlyList<(int Ctx, uint Value)>[] streams)
    {
        var maxToken = new int[_numClusters];
        foreach (IReadOnlyList<(int Ctx, uint Value)> stream in streams)
            for (int k = 0; k < stream.Count; k++)
            {
                int cluster = _contextMap[stream[k].Ctx];
                uint tok = _configs[cluster].EncodeToken(stream[k].Value, out _, out _);
                maxToken[cluster] = Math.Max(maxToken[cluster], (int)tok);
            }

        var alphabet = new int[_numClusters];
        int maxAlphabet = 1;
        for (int c = 0; c < _numClusters; c++)
        {
            alphabet[c] = maxToken[c] + 1; // tokens 0..maxToken occur
            maxAlphabet = Math.Max(maxAlphabet, alphabet[c]);
        }

        // log_alphabet_size is shared by all clusters; pick the smallest in [5,8] that fits the
        // widest alphabet. Smaller tables leave fewer zero-frequency buckets, which keeps the
        // alias spreading aligned with libjxl's decoder.
        int las = 5;
        while (las < 8 && (1 << las) < maxAlphabet)
            las++;
        if (maxAlphabet > (1 << las))
            throw new InvalidOperationException(
                $"JPEG XL encoder: token alphabet {maxAlphabet} exceeds ANS table size {1 << las}.");

        var hist = new JxlAnsHistogram[_numClusters];
        for (int c = 0; c < _numClusters; c++)
        {
            var hw = new JxlBitWriter();
            WriteEvenHeader(hw, alphabet[c]);
            var hr = new JxlBitReader(hw.ToArray());
            hist[c] = JxlAnsHistogram.Parse(ref hr, las);
        }

        return new Plan { Las = las, Alphabet = alphabet, Hist = hist };
    }

    /// <summary>
    /// Write the entropy <em>config</em> (lz77 flag, context map, integer configs, even histograms) —
    /// everything the decoder's <c>Parse</c> consumes, and nothing the data section needs. For VarDCT
    /// this is the HfGlobal slice; the matching <see cref="WriteData"/> calls fill the PassGroups.
    /// </summary>
    public void WriteConfig(JxlBitWriter bw, Plan plan)
    {
        bw.WriteBit(false); // lz77 disabled

        // Context map is read only when num_dist > 1.
        if (_contextMap.Length > 1)
        {
            int maxCluster = _numClusters - 1;
            int nbits = maxCluster == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)maxCluster);
            bw.WriteBit(true); // simple context map
            bw.WriteBits((uint)nbits, 2);
            foreach (byte c in _contextMap)
                bw.WriteBits(c, nbits);
        }

        bw.WriteBit(false);                    // use_prefix_code = false (ANS)
        bw.WriteBits((uint)(plan.Las - 5), 2); // log_alphabet_size
        for (int c = 0; c < _numClusters; c++)
            _configs[c].Write(bw, plan.Las);
        for (int c = 0; c < _numClusters; c++)
            WriteEvenHeader(bw, plan.Alphabet[c]);
    }

    /// <summary>
    /// Write one stream's coded <em>data</em>: the 32-bit initial rANS state followed by the
    /// interleaved per-symbol renorm words and hybrid-uint extra bits, in forward (decoder) order.
    /// The histograms come from <paramref name="plan"/>; every symbol's token must fit the alphabet
    /// the plan was prepared with (i.e. this stream was one of <see cref="Prepare"/>'s inputs).
    /// </summary>
    public void WriteData(JxlBitWriter bw, Plan plan, IReadOnlyList<(int Ctx, uint Value)> stream)
    {
        int n = stream.Count;
        var clusterOf = new int[n];
        var token = new uint[n];
        var extra = new uint[n];
        var extraCount = new int[n];

        for (int k = 0; k < n; k++)
        {
            int cluster = _contextMap[stream[k].Ctx];
            clusterOf[k] = cluster;
            token[k] = _configs[cluster].EncodeToken(stream[k].Value, out extra[k], out extraCount[k]);
            if (token[k] >= plan.Alphabet[cluster])
                throw new InvalidOperationException(
                    "JPEG XL encoder: a coded token exceeds the prepared alphabet; was this stream passed to Prepare?");
        }

        // Reverse rANS pass: collect each symbol's renorm word (0 or 1).
        uint state = JxlAnsHistogram.InitialState;
        var scratch = new List<ushort>();
        var word = new ushort?[n];
        for (int k = n - 1; k >= 0; k--)
        {
            int before = scratch.Count;
            plan.Hist[clusterOf[k]].EncodeStep(ref state, (int)token[k], scratch);
            if (scratch.Count > before)
                word[k] = scratch[before];
        }

        bw.WriteBits(state, 32);
        for (int k = 0; k < n; k++)
        {
            if (word[k] is ushort wd)
                bw.WriteBits(wd, 16);
            bw.WriteBits(extra[k], extraCount[k]);
        }
    }

    /// <summary>
    /// Convenience for the contiguous (Modular) case: prepare, then write the config and the single
    /// stream's data back-to-back into one writer — exactly what the decoder reads with
    /// <c>Parse</c> immediately followed by <c>Begin</c>/<c>ReadVarint</c>.
    /// </summary>
    public void Encode(JxlBitWriter bw, IReadOnlyList<(int Ctx, uint Value)> stream)
    {
        Plan plan = Prepare(stream);
        WriteConfig(bw, plan);
        WriteData(bw, plan, stream);
    }

    private static void WriteEvenHeader(JxlBitWriter bw, int alphabetSize)
    {
        bw.WriteBit(false); // not binary/unary
        bw.WriteBit(true);  // evenly distributed
        WriteU8(bw, alphabetSize - 1);
    }

    private static void WriteU8(JxlBitWriter bw, int value)
    {
        if (value == 0)
        {
            bw.WriteBit(false);
            return;
        }
        bw.WriteBit(true);
        int n = 31 - BitOperations.LeadingZeroCount((uint)value);
        bw.WriteBits((uint)n, 3);
        bw.WriteBits((uint)(value - (1 << n)), n);
    }
}
