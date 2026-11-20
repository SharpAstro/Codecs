namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 only. No libwebp, libvpx or
// RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are black-box oracles.
// The length limiting below is the textbook package-merge algorithm (Larmore and Hirschberg).

/// <summary>Length-limited Huffman code construction for VP8L prefix codes.</summary>
internal static class Vp8LHuffman
{
    /// <summary>RFC 9649 section 3.7.2.1: a prefix code length is at most 15 bits.</summary>
    public const int MaxCodeLength = 15;

    /// <summary>
    /// Fills <paramref name="lengths"/> with an optimal prefix code length per symbol, none longer
    /// than <paramref name="maxLength"/>. Unused symbols get 0. A single used symbol gets length 1;
    /// the caller decides whether that is written as a zero-bit code.
    /// </summary>
    /// <remarks>
    /// The items of package-merge live in pooled arrays and point at each other by index: an encode costs every
    /// candidate exactly, which builds this code many times over, and a heap object per item and a list per level
    /// was 81% of what an encode allocated (157 MiB for a 31x7 image at effort 9, Codecs#92). The algorithm,
    /// its order and its tie-breaks are the ones it always had, so the lengths are the same.
    /// </remarks>
    public static void BuildLengths(ReadOnlySpan<int> histogram, int maxLength, Span<int> lengths)
    {
        if (lengths.Length < histogram.Length)
        {
            throw new ArgumentException("The lengths span is shorter than the histogram.", nameof(lengths));
        }

        lengths[..histogram.Length].Clear();
        int n = 0;
        int only = -1;
        foreach (int count in histogram)
        {
            if (count > 0)
            {
                n++;
            }
        }

        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            for (int i = 0; i < histogram.Length; i++)
            {
                if (histogram[i] > 0)
                {
                    only = i;
                }
            }

            lengths[only] = 1;
            return;
        }

        if (maxLength < 1 || n > 1L << maxLength)
        {
            throw new ArgumentException($"{n} symbols cannot be coded in {maxLength} bits.");
        }

        // Every level adds at most n - 1 packages to the n leaves, so maxLength levels hold at most maxLength * n items.
        int capacity = maxLength * n;
        long[] weight = WebpBufferPool<long>.Rent(capacity);
        int[] symbol = WebpBufferPool<int>.Rent(capacity);
        int[] left = WebpBufferPool<int>.Rent(capacity);
        int[] right = WebpBufferPool<int>.Rent(capacity);
        int[] current = WebpBufferPool<int>.Rent(2 * n);
        int[] merged = WebpBufferPool<int>.Rent(2 * n);
        int[] packages = WebpBufferPool<int>.Rent(n);
        try
        {
            // The leaves, ordered by weight and then symbol: as one key each, since a count fits 31 bits.
            long[] keys = WebpBufferPool<long>.Rent(n);
            try
            {
                int k = 0;
                for (int i = 0; i < histogram.Length; i++)
                {
                    if (histogram[i] > 0)
                    {
                        keys[k++] = ((long)histogram[i] << 32) | (uint)i;
                    }
                }

                keys.AsSpan(0, n).Sort();
                for (int i = 0; i < n; i++)
                {
                    weight[i] = keys[i] >> 32;
                    symbol[i] = (int)(keys[i] & 0xFFFFFFFF);
                    left[i] = -1;
                    current[i] = i;
                }
            }
            finally
            {
                WebpBufferPool<long>.Return(keys);
            }

            // Package-merge: level 1 is the leaves; each further level merges the leaves with the
            // pairwise packages of the level before. The cheapest 2n-2 items of the last level are
            // the code, and a symbol's length is the number of selected items that contain it.
            int items = n;
            int currentCount = n;
            for (int level = 1; level < maxLength; level++)
            {
                int packageCount = 0;
                for (int i = 0; i + 1 < currentCount; i += 2)
                {
                    int a = current[i];
                    int b = current[i + 1];
                    weight[items] = weight[a] + weight[b];
                    symbol[items] = -1;
                    left[items] = a;
                    right[items] = b;
                    packages[packageCount++] = items++;
                }

                int count = 0;
                int leaf = 0;
                int package = 0;
                while (leaf < n || package < packageCount)
                {
                    if (package >= packageCount || (leaf < n && weight[leaf] <= weight[packages[package]]))
                    {
                        merged[count++] = leaf++;
                    }
                    else
                    {
                        merged[count++] = packages[package++];
                    }
                }

                (current, merged) = (merged, current);
                currentCount = count;
            }

            // Each selected item adds one to every leaf under it. An item of level L is L - 1 deep, so the walk's stack
            // holds at most maxLength pending items.
            Span<int> stack = maxLength <= 64 ? stackalloc int[maxLength + 1] : new int[maxLength + 1];
            for (int i = 0; i < 2 * n - 2; i++)
            {
                int depth = 0;
                stack[depth++] = current[i];
                while (depth > 0)
                {
                    int item = stack[--depth];
                    if (left[item] < 0)
                    {
                        lengths[symbol[item]]++;
                        continue;
                    }

                    stack[depth++] = right[item];
                    stack[depth++] = left[item];
                }
            }
        }
        finally
        {
            WebpBufferPool<long>.Return(weight);
            WebpBufferPool<int>.Return(symbol);
            WebpBufferPool<int>.Return(left);
            WebpBufferPool<int>.Return(right);
            WebpBufferPool<int>.Return(current);
            WebpBufferPool<int>.Return(merged);
            WebpBufferPool<int>.Return(packages);
        }
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
