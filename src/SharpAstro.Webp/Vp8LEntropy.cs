namespace SharpAstro.Webp;

// Provenance: plain Shannon entropy arithmetic; nothing here comes from any WebP implementation.

/// <summary>
/// Shannon-entropy cost estimates the encoder's searches rank choices by (predictor modes, cross
/// colour multipliers, tile clusterings, whole pipelines). An estimate only orders candidates: what
/// is written is always costed exactly, from the real prefix codes, before it is chosen.
/// </summary>
internal static class Vp8LEntropy
{
    // n * log2(n) for the counts a tile histogram reaches; larger counts are computed.
    private static readonly double[] NLog2NTable = BuildTable(1 << 14);

    private static double[] BuildTable(int size)
    {
        var table = new double[size];
        for (int n = 2; n < size; n++)
        {
            table[n] = n * Math.Log2(n);
        }

        return table;
    }

    /// <summary><c>n * log2(n)</c>, 0 for 0 and 1.</summary>
    public static double NLog2N(int n) => (uint)n < (uint)NLog2NTable.Length ? NLog2NTable[n] : n * Math.Log2(n);

    /// <summary><c>total * log2(total) - sum(n * log2(n))</c>: the ideal bits to code the histogram's symbols.</summary>
    public static double Bits(ReadOnlySpan<int> histogram)
    {
        long total = 0;
        double sum = 0;
        foreach (int n in histogram)
        {
            if (n > 0)
            {
                total += n;
                sum += NLog2N(n);
            }
        }

        return total <= 1 ? 0 : total * Math.Log2(total) - sum;
    }

    /// <summary>Counts the non-zero bins.</summary>
    public static int Used(ReadOnlySpan<int> histogram)
    {
        int used = 0;
        foreach (int n in histogram)
        {
            if (n > 0)
            {
                used++;
            }
        }

        return used;
    }
}
