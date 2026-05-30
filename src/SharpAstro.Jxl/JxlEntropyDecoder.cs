namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL entropy decoder (ISO/IEC 18181-1 §C.2) — the top-level driver tying the hybrid
/// integer config, prefix/ANS coders, the distribution-clustering context map, and the optional
/// LZ77 layer into the <c>read_varint(context)</c> API the rest of the codec consumes. Faithful
/// port of jxl-coding lib.rs (<c>Decoder</c> / <c>DecoderInner</c> / <c>read_clusters</c>).
/// </summary>
internal sealed class JxlEntropyDecoder
{
    private readonly byte[] _clusters;          // num_dist entries -> cluster index
    private readonly JxlIntegerConfig[] _configs; // one per cluster
    private readonly bool _usePrefix;
    private readonly JxlPrefixCode[]? _prefix;
    private readonly JxlAnsHistogram[]? _ans;
    private uint _ansState;
    private bool _ansInitial;

    private readonly bool _lz77Enabled;
    private readonly uint _lzMinSymbol;
    private readonly uint _lzMinLength;
    private readonly JxlIntegerConfig _lzLenConfig;
    private uint[] _lzWindow = [];
    private uint _lzNumToCopy;
    private uint _lzCopyPos;
    private uint _lzNumDecoded;

    private JxlEntropyDecoder(
        byte[] clusters, JxlIntegerConfig[] configs, bool usePrefix,
        JxlPrefixCode[]? prefix, JxlAnsHistogram[]? ans,
        bool lz77Enabled, uint lzMinSymbol, uint lzMinLength, JxlIntegerConfig lzLenConfig)
    {
        _clusters = clusters;
        _configs = configs;
        _usePrefix = usePrefix;
        _prefix = prefix;
        _ans = ans;
        _ansInitial = !usePrefix;
        _lz77Enabled = lz77Enabled;
        _lzMinSymbol = lzMinSymbol;
        _lzMinLength = lzMinLength;
        _lzLenConfig = lzLenConfig;
    }

    public byte[] ClusterMap => _clusters;

    // Diagnostics.
    public bool UsesLz77 => _lz77Enabled;
    public bool UsesPrefix => _usePrefix;

    public static JxlEntropyDecoder Parse(ref JxlBitReader br, uint numDist)
    {
        bool lz77Enabled = br.ReadBit();
        uint lzMinSymbol = 0, lzMinLength = 0;
        JxlIntegerConfig lzLenConfig = default;
        uint innerNumDist = numDist;
        if (lz77Enabled)
        {
            lzMinSymbol = br.ReadU32((224, 0), (512, 0), (4096, 0), (8, 15));
            lzMinLength = br.ReadU32((3, 0), (4, 0), (5, 2), (9, 8));
            lzLenConfig = JxlIntegerConfig.Parse(ref br, 8);
            innerNumDist = numDist + 1;
        }
        return ParseInner(ref br, innerNumDist, lz77Enabled, lzMinSymbol, lzMinLength, lzLenConfig);
    }

    private static JxlEntropyDecoder ParseAssumeNoLz77(ref JxlBitReader br, uint numDist)
    {
        if (br.ReadBit())
            throw new InvalidDataException("JPEG XL LZ77 not allowed in this context.");
        return ParseInner(ref br, numDist, false, 0, 0, default);
    }

    private static JxlEntropyDecoder ParseInner(
        ref JxlBitReader br, uint numDist, bool lz77Enabled,
        uint lzMinSymbol, uint lzMinLength, JxlIntegerConfig lzLenConfig)
    {
        (uint numClusters, byte[] clusters) = ReadClusters(ref br, numDist);

        bool usePrefix = br.ReadBit();
        int logAlphabetSize = usePrefix ? 15 : (int)br.ReadBits(2) + 5;

        var configs = new JxlIntegerConfig[numClusters];
        for (int i = 0; i < numClusters; i++)
            configs[i] = JxlIntegerConfig.Parse(ref br, logAlphabetSize);

        JxlPrefixCode[]? prefix = null;
        JxlAnsHistogram[]? ans = null;
        if (usePrefix)
        {
            var counts = new uint[numClusters];
            for (int i = 0; i < numClusters; i++)
            {
                if (br.ReadBit())
                {
                    int n = (int)br.ReadBits(4);
                    counts[i] = 1u + (1u << n) + br.ReadBits(n);
                }
                else
                {
                    counts[i] = 1;
                }
                if (counts[i] > 1 << 15)
                    throw new InvalidDataException("JPEG XL invalid prefix histogram count.");
            }
            prefix = new JxlPrefixCode[numClusters];
            for (int i = 0; i < numClusters; i++)
                prefix[i] = JxlPrefixCode.Parse(ref br, counts[i]);
        }
        else
        {
            ans = new JxlAnsHistogram[numClusters];
            for (int i = 0; i < numClusters; i++)
                ans[i] = JxlAnsHistogram.Parse(ref br, logAlphabetSize);
        }

        return new JxlEntropyDecoder(
            clusters, configs, usePrefix, prefix, ans,
            lz77Enabled, lzMinSymbol, lzMinLength, lzLenConfig);
    }

    private static (uint NumClusters, byte[] Clusters) ReadClusters(ref JxlBitReader br, uint numDist)
    {
        if (numDist == 1)
            return (1, [0]);

        byte[] clusters;
        if (br.ReadBit())
        {
            // simple distribution: each context's cluster in a fixed bit width
            int nbits = (int)br.ReadBits(2);
            clusters = new byte[numDist];
            for (int i = 0; i < numDist; i++)
                clusters[i] = (byte)br.ReadBits(nbits);
        }
        else
        {
            bool useMtf = br.ReadBit();
            JxlEntropyDecoder decoder = numDist <= 2
                ? ParseAssumeNoLz77(ref br, 1)
                : Parse(ref br, 1);
            decoder.Begin(ref br);
            clusters = new byte[numDist];
            for (int i = 0; i < numDist; i++)
            {
                uint b = decoder.ReadVarint(ref br, 0);
                if (b > 255)
                    throw new InvalidDataException($"JPEG XL invalid cluster index {b}.");
                clusters[i] = (byte)b;
            }
            decoder.Finish();

            if (useMtf)
            {
                var mtf = new byte[256];
                for (int i = 0; i < 256; i++)
                    mtf[i] = (byte)i;
                for (int i = 0; i < clusters.Length; i++)
                {
                    int idx = clusters[i];
                    byte val = mtf[idx];
                    clusters[i] = val;
                    Array.Copy(mtf, 0, mtf, 1, idx); // move-to-front shift
                    mtf[0] = val;
                }
            }
        }

        uint numClusters = 0;
        foreach (byte c in clusters)
            numClusters = Math.Max(numClusters, (uint)c + 1);
        var seen = new HashSet<byte>(clusters);
        if (seen.Count != numClusters)
            throw new InvalidDataException("JPEG XL distribution clustering has a hole.");
        return (numClusters, clusters);
    }

    /// <summary>Reads the 32-bit initial ANS state (no-op for prefix codes). Optional — done lazily otherwise.</summary>
    public void Begin(ref JxlBitReader br)
    {
        if (!_usePrefix)
        {
            _ansState = br.ReadBits(32);
            _ansInitial = false;
        }
    }

    /// <summary>For ANS streams, verifies the final state matches the spec signature.</summary>
    public void Finish()
    {
        if (!_usePrefix && _ansState != JxlAnsHistogram.InitialState)
            throw new InvalidDataException("JPEG XL ANS stream did not finalise to the expected state.");
    }

    public uint ReadVarint(ref JxlBitReader br, uint ctx) => ReadVarintWithMultiplier(ref br, ctx, 0);

    public uint ReadVarintWithMultiplier(ref JxlBitReader br, uint ctx, uint distMultiplier)
        => ReadVarintClustered(ref br, _clusters[ctx], distMultiplier);

    /// <summary>Reads an integer for an explicit cluster (used by the Modular decode loop, which holds the leaf's cluster).</summary>
    public uint ReadVarintClustered(ref JxlBitReader br, byte cluster, uint distMultiplier)
    {
        if (_lz77Enabled)
            return ReadVarintLz77(ref br, cluster, distMultiplier);
        uint token = ReadSymbol(ref br, cluster);
        return _configs[cluster].ReadUint(ref br, token);
    }

    private uint ReadSymbol(ref JxlBitReader br, byte cluster)
    {
        if (_usePrefix)
            return _prefix![cluster].ReadSymbol(ref br);

        if (_ansInitial)
        {
            _ansState = br.ReadBits(32);
            _ansInitial = false;
        }
        return _ans![cluster].ReadSymbol(ref br, ref _ansState);
    }

    private uint ReadVarintLz77(ref JxlBitReader br, byte cluster, uint distMultiplier)
    {
        uint r;
        if (_lzNumToCopy > 0)
        {
            r = _lzWindow[(int)(_lzCopyPos & 0xfffff)];
            _lzCopyPos++;
            _lzNumToCopy--;
        }
        else
        {
            uint token = ReadSymbol(ref br, cluster);
            if (token >= _lzMinSymbol)
            {
                if (_lzNumDecoded == 0)
                    throw new InvalidDataException("JPEG XL LZ77 repeat before any symbol.");

                byte lzDistCluster = _clusters[^1];
                uint numToCopy = _lzLenConfig.ReadUint(ref br, token - _lzMinSymbol);
                _lzNumToCopy = numToCopy + _lzMinLength;

                uint distToken = ReadSymbol(ref br, lzDistCluster);
                uint distance = _configs[lzDistCluster].ReadUint(ref br, distToken);
                distance = ResolveLzDistance(distance, distMultiplier);
                distance = Math.Min(Math.Min((1u << 20) - 1, distance) + 1, _lzNumDecoded);
                _lzCopyPos = _lzNumDecoded - distance;

                r = _lzWindow[(int)(_lzCopyPos & 0xfffff)];
                _lzCopyPos++;
                _lzNumToCopy--;
            }
            else
            {
                r = _configs[cluster].ReadUint(ref br, token);
            }
        }

        int offset = (int)(_lzNumDecoded & 0xfffff);
        if (_lzWindow.Length <= offset)
        {
            Array.Resize(ref _lzWindow, offset + 1);
        }
        _lzWindow[offset] = r;
        _lzNumDecoded++;
        return r;
    }

    private static uint ResolveLzDistance(uint distance, uint distMultiplier)
    {
        if (distMultiplier == 0)
            return distance;
        if (distance < 120)
        {
            (sbyte ox, sbyte dy) = SpecialDistances[distance];
            int dist = ox + (int)distMultiplier * dy;
            return (uint)Math.Max(dist - 1, 0);
        }
        return distance - 120;
    }

    /// <summary>Reads a permutation (Lehmer code) from the entropy stream (jxl-coding permutation.rs).</summary>
    public static int[] ReadPermutation(ref JxlBitReader br, JxlEntropyDecoder decoder, uint size, uint skip)
    {
        uint end = decoder.ReadVarint(ref br, PermutationContext(size));
        if (end > size - skip)
            throw new InvalidDataException("JPEG XL invalid permutation.");

        var lehmer = new uint[end];
        uint prev = 0;
        for (uint idx = 0; idx < end; idx++)
        {
            lehmer[idx] = decoder.ReadVarint(ref br, PermutationContext(prev));
            if (lehmer[idx] >= size - skip - idx)
                throw new InvalidDataException("JPEG XL invalid permutation.");
            prev = lehmer[idx];
        }

        var temp = new List<int>((int)(size - skip));
        for (int i = (int)skip; i < size; i++)
            temp.Add(i);
        var permutation = new int[size];
        int p = 0;
        for (int idx = 0; idx < skip; idx++)
            permutation[p++] = idx;
        foreach (uint l in lehmer)
        {
            permutation[p++] = temp[(int)l];
            temp.RemoveAt((int)l);
        }
        foreach (int t in temp)
            permutation[p++] = t;
        return permutation;
    }

    private static uint PermutationContext(uint x) => Math.Min((uint)JxlIntegerConfig.AddLog2Ceil(x), 7);

    private static readonly (sbyte, sbyte)[] SpecialDistances =
    [
        (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0), (1, 2), (-1, 2), (2, 1), (-2, 1),
        (2, 2), (-2, 2), (0, 3), (3, 0), (1, 3), (-1, 3), (3, 1), (-3, 1), (2, 3), (-2, 3),
        (3, 2), (-3, 2), (0, 4), (4, 0), (1, 4), (-1, 4), (4, 1), (-4, 1), (3, 3), (-3, 3),
        (2, 4), (-2, 4), (4, 2), (-4, 2), (0, 5), (3, 4), (-3, 4), (4, 3), (-4, 3), (5, 0),
        (1, 5), (-1, 5), (5, 1), (-5, 1), (2, 5), (-2, 5), (5, 2), (-5, 2), (4, 4), (-4, 4),
        (3, 5), (-3, 5), (5, 3), (-5, 3), (0, 6), (6, 0), (1, 6), (-1, 6), (6, 1), (-6, 1),
        (2, 6), (-2, 6), (6, 2), (-6, 2), (4, 5), (-4, 5), (5, 4), (-5, 4), (3, 6), (-3, 6),
        (6, 3), (-6, 3), (0, 7), (7, 0), (1, 7), (-1, 7), (5, 5), (-5, 5), (7, 1), (-7, 1),
        (4, 6), (-4, 6), (6, 4), (-6, 4), (2, 7), (-2, 7), (7, 2), (-7, 2), (3, 7), (-3, 7),
        (7, 3), (-7, 3), (5, 6), (-5, 6), (6, 5), (-6, 5), (8, 0), (4, 7), (-4, 7), (7, 4),
        (-7, 4), (8, 1), (8, 2), (6, 6), (-6, 6), (8, 3), (5, 7), (-5, 7), (7, 5), (-7, 5),
        (8, 4), (6, 7), (-6, 7), (7, 6), (-7, 6), (8, 5), (7, 7), (-7, 7), (8, 6), (8, 7),
    ];
}
