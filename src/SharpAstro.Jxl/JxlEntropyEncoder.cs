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
    private const int Las = 8; // log_alphabet_size -> ANS table size 256

    private readonly byte[] _contextMap; // context -> cluster
    private readonly int _numClusters;
    private readonly JxlIntegerConfig[] _configs; // per cluster

    public JxlEntropyEncoder(byte[] contextMap, JxlIntegerConfig[] configsPerCluster)
    {
        _contextMap = contextMap;
        _configs = configsPerCluster;
        _numClusters = configsPerCluster.Length;
    }

    public void Encode(JxlBitWriter bw, IReadOnlyList<(int Ctx, uint Value)> stream)
    {
        int n = stream.Count;
        var clusterOf = new int[n];
        var token = new uint[n];
        var extra = new uint[n];
        var extraCount = new int[n];
        var maxToken = new int[_numClusters];

        for (int k = 0; k < n; k++)
        {
            int cluster = _contextMap[stream[k].Ctx];
            clusterOf[k] = cluster;
            token[k] = _configs[cluster].EncodeToken(stream[k].Value, out extra[k], out extraCount[k]);
            maxToken[cluster] = Math.Max(maxToken[cluster], (int)token[k]);
        }

        var alphabet = new int[_numClusters];
        var hist = new JxlAnsHistogram[_numClusters];
        for (int c = 0; c < _numClusters; c++)
        {
            alphabet[c] = maxToken[c] + 1; // tokens 0..maxToken occur
            if (alphabet[c] > (1 << Las))
                throw new InvalidOperationException(
                    $"JPEG XL encoder: token alphabet {alphabet[c]} exceeds ANS table size {1 << Las}.");
            var hw = new JxlBitWriter();
            WriteEvenHeader(hw, alphabet[c]);
            var hr = new JxlBitReader(hw.ToArray());
            hist[c] = JxlAnsHistogram.Parse(ref hr, Las);
        }

        // Reverse rANS pass: collect each symbol's renorm word (0 or 1).
        uint state = JxlAnsHistogram.InitialState;
        var scratch = new List<ushort>();
        var word = new ushort?[n];
        for (int k = n - 1; k >= 0; k--)
        {
            int before = scratch.Count;
            hist[clusterOf[k]].EncodeStep(ref state, (int)token[k], scratch);
            if (scratch.Count > before)
                word[k] = scratch[before];
        }

        WriteHeader(bw, alphabet);
        bw.WriteBits(state, 32);
        for (int k = 0; k < n; k++)
        {
            if (word[k] is ushort wd)
                bw.WriteBits(wd, 16);
            bw.WriteBits(extra[k], extraCount[k]);
        }
    }

    private void WriteHeader(JxlBitWriter bw, int[] alphabet)
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

        bw.WriteBit(false);               // use_prefix_code = false (ANS)
        bw.WriteBits((uint)(Las - 5), 2); // log_alphabet_size
        for (int c = 0; c < _numClusters; c++)
            _configs[c].Write(bw, Las);
        for (int c = 0; c < _numClusters; c++)
            WriteEvenHeader(bw, alphabet[c]);
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
