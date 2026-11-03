namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 only. No libwebp, libvpx or
// RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are black-box oracles.
// The length limiting below is the textbook package-merge algorithm (Larmore and Hirschberg).

/// <summary>Length-limited Huffman code construction for VP8L prefix codes.</summary>
internal static class Vp8LHuffman
{
    /// <summary>RFC 9649 section 3.7.2.1: a prefix code length is at most 15 bits.</summary>
    public const int MaxCodeLength = 15;

    private sealed class Node
    {
        public readonly long Weight;
        public readonly int Symbol;
        public readonly Node? Left;
        public readonly Node? Right;

        public Node(long weight, int symbol)
        {
            Weight = weight;
            Symbol = symbol;
        }

        public Node(Node left, Node right)
        {
            Weight = left.Weight + right.Weight;
            Symbol = -1;
            Left = left;
            Right = right;
        }
    }

    /// <summary>
    /// Fills <paramref name="lengths"/> with an optimal prefix code length per symbol, none longer
    /// than <paramref name="maxLength"/>. Unused symbols get 0. A single used symbol gets length 1;
    /// the caller decides whether that is written as a zero-bit code.
    /// </summary>
    public static void BuildLengths(ReadOnlySpan<int> histogram, int maxLength, Span<int> lengths)
    {
        if (lengths.Length < histogram.Length)
        {
            throw new ArgumentException("The lengths span is shorter than the histogram.", nameof(lengths));
        }

        lengths[..histogram.Length].Clear();
        var leaves = new List<Node>();
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > 0)
            {
                leaves.Add(new Node(histogram[i], i));
            }
        }

        int n = leaves.Count;
        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            lengths[leaves[0].Symbol] = 1;
            return;
        }

        if (maxLength < 1 || n > 1L << maxLength)
        {
            throw new ArgumentException($"{n} symbols cannot be coded in {maxLength} bits.");
        }

        leaves.Sort(static (a, b) => a.Weight != b.Weight ? a.Weight.CompareTo(b.Weight) : a.Symbol.CompareTo(b.Symbol));

        // Package-merge: level 1 is the leaves; each further level merges the leaves with the
        // pairwise packages of the level before. The cheapest 2n-2 items of the last level are
        // the code, and a symbol's length is the number of selected items that contain it.
        List<Node> current = leaves;
        for (int level = 1; level < maxLength; level++)
        {
            var packages = new List<Node>(current.Count / 2);
            for (int i = 0; i + 1 < current.Count; i += 2)
            {
                packages.Add(new Node(current[i], current[i + 1]));
            }

            var merged = new List<Node>(leaves.Count + packages.Count);
            int a = 0;
            int b = 0;
            while (a < leaves.Count || b < packages.Count)
            {
                if (b >= packages.Count || (a < leaves.Count && leaves[a].Weight <= packages[b].Weight))
                {
                    merged.Add(leaves[a++]);
                }
                else
                {
                    merged.Add(packages[b++]);
                }
            }

            current = merged;
        }

        for (int i = 0; i < 2 * n - 2; i++)
        {
            Accumulate(current[i], lengths);
        }
    }

    private static void Accumulate(Node node, Span<int> lengths)
    {
        if (node.Left is null)
        {
            lengths[node.Symbol]++;
            return;
        }

        Accumulate(node.Left, lengths);
        Accumulate(node.Right!, lengths);
    }

    /// <summary>
    /// Assigns canonical codes (RFC 9649 section 3.7.2.1: shorter codes first, ties by symbol) and
    /// stores each one bit-reversed, ready for the LSB-first writer: a prefix code is read most
    /// significant bit first, one bit at a time.
    /// </summary>
    public static void AssignCodes(ReadOnlySpan<int> lengths, Span<uint> codes)
    {
        Span<int> counts = stackalloc int[MaxCodeLength + 2];
        counts.Clear();
        for (int i = 0; i < lengths.Length; i++)
        {
            counts[lengths[i]]++;
        }

        counts[0] = 0;
        Span<uint> next = stackalloc uint[MaxCodeLength + 2];
        uint code = 0;
        for (int bits = 1; bits <= MaxCodeLength; bits++)
        {
            code = (code + (uint)counts[bits - 1]) << 1;
            next[bits] = code;
        }

        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            if (length == 0)
            {
                codes[i] = 0;
                continue;
            }

            uint value = next[length]++;
            uint reversed = 0;
            for (int bit = 0; bit < length; bit++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }

            codes[i] = reversed;
        }
    }
}
