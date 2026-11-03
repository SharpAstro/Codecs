namespace SharpAstro.Webp;

// Provenance: a plain agglomerative clustering of histograms by estimated entropy cost, written for
// this encoder. The bitstream rule it serves (RFC 9649 section 3.7.2.2, meta prefix codes: one prefix
// code group per cluster, picked per tile by an entropy image) is from the RFC only; no libwebp
// source was read or transcribed.

/// <summary>
/// Where the five histograms of one prefix-code group live in one flat <c>int[]</c>: green (with the
/// length symbols and any colour cache indices), red, blue, alpha, then the distance symbols.
/// </summary>
internal sealed class Vp8LHistogramLayout
{
    public const int ChannelSize = 256;
    public const int DistanceSize = 40;

    private readonly int _greenSize;

    public Vp8LHistogramLayout(int greenSize)
    {
        _greenSize = greenSize;
        Segments =
        [
            (0, greenSize),
            (greenSize, ChannelSize),
            (greenSize + ChannelSize, ChannelSize),
            (greenSize + 2 * ChannelSize, ChannelSize),
            (greenSize + 3 * ChannelSize, DistanceSize),
        ];
    }

    public int GreenSize => _greenSize;

    public int Red => _greenSize;

    public int Blue => _greenSize + ChannelSize;

    public int Alpha => _greenSize + 2 * ChannelSize;

    public int Distance => _greenSize + 3 * ChannelSize;

    public int Total => _greenSize + 3 * ChannelSize + DistanceSize;

    /// <summary>The start and length of each of the five histograms, in stream order.</summary>
    public (int Start, int Length)[] Segments { get; }
}

/// <summary>
/// Groups tiles whose symbol statistics are alike, so each group gets one prefix code. The cost of a
/// group is the entropy estimate of its five histograms plus a rough header cost, and the clustering
/// merges where that estimate falls.
/// </summary>
internal static class Vp8LHistogramClusterer
{
    private sealed class Cluster(int[] histogram, double cost, List<int> tiles)
    {
        public int[] Histogram = histogram;
        public double Cost = cost;
        public List<int> Tiles = tiles;
    }

    /// <summary>Estimated bits of one histogram segment's prefix code header, by how many symbols it uses.</summary>
    private static double HeaderEstimate(int used) => used == 0 ? 4 : used <= 2 ? 8 : 30 + 4.0 * used;

    /// <summary>The estimated bits of a group whose histograms are <paramref name="histogram"/>: symbols plus headers.</summary>
    public static double Cost(Vp8LHistogramLayout layout, ReadOnlySpan<int> histogram)
    {
        double cost = 0;
        foreach ((int start, int length) in layout.Segments)
        {
            ReadOnlySpan<int> segment = histogram.Slice(start, length);
            cost += Vp8LEntropy.Bits(segment) + HeaderEstimate(Vp8LEntropy.Used(segment));
        }

        return cost;
    }

    private static double MergedCost(Vp8LHistogramLayout layout, int[] a, int[] b)
    {
        double cost = 0;
        foreach ((int start, int length) in layout.Segments)
        {
            long total = 0;
            double sum = 0;
            int used = 0;
            for (int k = start; k < start + length; k++)
            {
                int c = a[k] + b[k];
                if (c > 0)
                {
                    used++;
                    total += c;
                    sum += Vp8LEntropy.NLog2N(c);
                }
            }

            cost += (total <= 1 ? 0 : total * Math.Log2(total) - sum) + HeaderEstimate(used);
        }

        return cost;
    }

    private static int[] Merge(int[] a, int[] b)
    {
        int[] merged = new int[a.Length];
        for (int k = 0; k < merged.Length; k++)
        {
            merged[k] = a[k] + b[k];
        }

        return merged;
    }

    /// <summary>
    /// Clusters the tiles. <paramref name="tileHistograms"/> holds one flat histogram per tile (an
    /// all-zero one is a tile no symbol starts in; it joins the group of the tile before it, which
    /// costs the entropy image least). Returns the group of each tile, numbered from 0 in order of
    /// first appearance, and at most <paramref name="maxGroups"/> of them.
    /// </summary>
    /// <param name="tileHistograms">One flat histogram of <paramref name="layout"/> per tile.</param>
    /// <param name="layout">The histogram layout.</param>
    /// <param name="maxGroups">The most groups to return (at least 1).</param>
    /// <param name="candidatesPerCluster">How many partners each cluster samples per round.</param>
    /// <param name="refine">Whether to reassign tiles to their cheapest cluster afterwards.</param>
    /// <param name="groupCount">The number of groups returned.</param>
    public static int[] ClusterTiles(
        int[][] tileHistograms,
        Vp8LHistogramLayout layout,
        int maxGroups,
        int candidatesPerCluster,
        bool refine,
        out int groupCount)
    {
        int tileCount = tileHistograms.Length;
        var clusters = new List<Cluster>();
        for (int tile = 0; tile < tileCount; tile++)
        {
            int[] histogram = tileHistograms[tile];
            if (histogram.AsSpan().ContainsAnyExcept(0))
            {
                clusters.Add(new Cluster(histogram, Cost(layout, histogram), [tile]));
            }
        }

        if (clusters.Count == 0)
        {
            groupCount = 1;
            return new int[tileCount];
        }

        uint random = 0x2545f491u;
        for (int round = 0; round < 64 && clusters.Count > 1; round++)
        {
            int n = clusters.Count;
            int excess = n - maxGroups;
            var pairs = new List<(double Delta, int I, int J)>();
            for (int i = 0; i < n; i++)
            {
                for (int step = 1; step <= candidatesPerCluster && i + step < n; step++)
                {
                    pairs.Add((Delta(layout, clusters, i, i + step), i, i + step));
                }

                for (int r = 0; r < candidatesPerCluster / 2; r++)
                {
                    random ^= random << 13;
                    random ^= random >> 17;
                    random ^= random << 5;
                    int j = (int)(random % (uint)n);
                    if (j != i)
                    {
                        pairs.Add((Delta(layout, clusters, Math.Min(i, j), Math.Max(i, j)), Math.Min(i, j), Math.Max(i, j)));
                    }
                }
            }

            pairs.Sort(static (x, y) => x.Delta.CompareTo(y.Delta));
            var consumed = new bool[n];
            var replacement = new Cluster?[n];
            int applied = 0;
            foreach ((double delta, int i, int j) in pairs)
            {
                if (delta >= 0 && applied >= excess)
                {
                    break;
                }

                if (consumed[i] || consumed[j])
                {
                    continue;
                }

                consumed[i] = consumed[j] = true;
                int[] merged = Merge(clusters[i].Histogram, clusters[j].Histogram);
                var tiles = new List<int>(clusters[i].Tiles);
                tiles.AddRange(clusters[j].Tiles);
                replacement[i] = new Cluster(merged, clusters[i].Cost + clusters[j].Cost + delta, tiles);
                applied++;
            }

            if (applied == 0)
            {
                break;
            }

            var next = new List<Cluster>(n - applied);
            for (int i = 0; i < n; i++)
            {
                if (replacement[i] is { } merged)
                {
                    next.Add(merged);
                }
                else if (!consumed[i])
                {
                    next.Add(clusters[i]);
                }
            }

            clusters = next;
        }

        // The sampled rounds can stall above the cap; pair neighbours until it holds.
        while (clusters.Count > maxGroups)
        {
            int excess = clusters.Count - maxGroups;
            var next = new List<Cluster>(clusters.Count);
            for (int i = 0; i < clusters.Count; i++)
            {
                if (excess > 0 && i + 1 < clusters.Count)
                {
                    int[] merged = Merge(clusters[i].Histogram, clusters[i + 1].Histogram);
                    var tiles = new List<int>(clusters[i].Tiles);
                    tiles.AddRange(clusters[i + 1].Tiles);
                    next.Add(new Cluster(merged, Cost(layout, merged), tiles));
                    i++;
                    excess--;
                }
                else
                {
                    next.Add(clusters[i]);
                }
            }

            clusters = next;
        }

        if (refine && clusters.Count > 1)
        {
            clusters = Refine(tileHistograms, layout, clusters);
        }

        // Number the groups in order of first appearance; tiles no symbol starts in follow the tile before.
        int[] tileToCluster = new int[tileCount];
        Array.Fill(tileToCluster, -1);
        for (int c = 0; c < clusters.Count; c++)
        {
            foreach (int tile in clusters[c].Tiles)
            {
                tileToCluster[tile] = c;
            }
        }

        int[] renumber = new int[clusters.Count];
        Array.Fill(renumber, -1);
        int groups = 0;
        int[] groupOfTile = new int[tileCount];
        int previous = 0;
        for (int tile = 0; tile < tileCount; tile++)
        {
            int c = tileToCluster[tile];
            if (c < 0)
            {
                groupOfTile[tile] = previous;
                continue;
            }

            if (renumber[c] < 0)
            {
                renumber[c] = groups++;
            }

            previous = groupOfTile[tile] = renumber[c];
        }

        groupCount = Math.Max(groups, 1);
        return groupOfTile;
    }

    private static double Delta(Vp8LHistogramLayout layout, List<Cluster> clusters, int i, int j) =>
        MergedCost(layout, clusters[i].Histogram, clusters[j].Histogram) - clusters[i].Cost - clusters[j].Cost;

    // Two k-means-like passes: price each tile's symbols under each cluster's own code (an absent
    // symbol is priced as if it were rare), move it to the cheapest, rebuild the clusters.
    private static List<Cluster> Refine(int[][] tileHistograms, Vp8LHistogramLayout layout, List<Cluster> clusters)
    {
        int total = layout.Total;
        for (int pass = 0; pass < 2; pass++)
        {
            float[][] price = new float[clusters.Count][];
            for (int c = 0; c < clusters.Count; c++)
            {
                price[c] = new float[total];
                foreach ((int start, int length) in layout.Segments)
                {
                    long segmentTotal = 0;
                    for (int k = start; k < start + length; k++)
                    {
                        segmentTotal += clusters[c].Histogram[k];
                    }

                    double log = Math.Log2(Math.Max(1, segmentTotal));
                    for (int k = start; k < start + length; k++)
                    {
                        int count = clusters[c].Histogram[k];
                        price[c][k] = (float)(count > 0 ? log - Math.Log2(count) : log + 6);
                    }
                }
            }

            var rebuilt = new int[clusters.Count][];
            var members = new List<int>[clusters.Count];
            for (int c = 0; c < clusters.Count; c++)
            {
                rebuilt[c] = new int[total];
                members[c] = [];
            }

            foreach (Cluster cluster in clusters)
            {
                foreach (int tile in cluster.Tiles)
                {
                    int[] histogram = tileHistograms[tile];
                    int best = 0;
                    double bestCost = double.MaxValue;
                    for (int c = 0; c < clusters.Count; c++)
                    {
                        double cost = 0;
                        float[] p = price[c];
                        for (int k = 0; k < total; k++)
                        {
                            if (histogram[k] != 0)
                            {
                                cost += histogram[k] * p[k];
                            }
                        }

                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = c;
                        }
                    }

                    members[best].Add(tile);
                    for (int k = 0; k < total; k++)
                    {
                        rebuilt[best][k] += histogram[k];
                    }
                }
            }

            var next = new List<Cluster>(clusters.Count);
            for (int c = 0; c < clusters.Count; c++)
            {
                if (members[c].Count > 0)
                {
                    members[c].Sort();
                    next.Add(new Cluster(rebuilt[c], Cost(layout, rebuilt[c]), members[c]));
                }
            }

            clusters = next;
            if (clusters.Count == 1)
            {
                break;
            }
        }

        return clusters;
    }
}
