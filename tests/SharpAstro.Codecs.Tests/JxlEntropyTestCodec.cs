using SharpAstro.Jxl;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Test-only helpers that emit JPEG XL entropy streams our <see cref="JxlEntropyDecoder"/> reads:
/// an LZ77-disabled, simple-context-map, ANS decoder using evenly-distributed histograms and
/// configs with split_exponent == log_alphabet_size (so every value is a literal token — no
/// trailing raw bits, sidestepping the extra-bit↔ANS-word interleaving). This lets Modular-layer
/// tests (MA tree, decode loop) build real entropy streams without a production encoder.
/// </summary>
internal static class JxlEntropyTestCodec
{
    public const int Las = 8; // log_alphabet_size; split == 256, so values < 256 are literal tokens

    /// <summary>Zigzag encode (inverse of <see cref="JxlModular.UnpackSigned"/>).</summary>
    public static uint PackSigned(int v) => (uint)((v << 1) ^ (v >> 31));

    /// <summary>
    /// Writes a full ANS Decoder (header + 32-bit state + renorm words) that decodes the given
    /// (context, value) stream. <paramref name="clusters"/> maps context→cluster;
    /// <paramref name="perClusterAlphabet"/> sizes each cluster's even histogram.
    /// </summary>
    public static void WriteDecoderWithData(
        JxlBitWriter bw, byte[] clusters, int[] perClusterAlphabet, IReadOnlyList<(int Ctx, int Value)> stream)
    {
        JxlAnsHistogram[] hist = BuildHistograms(perClusterAlphabet);
        uint state = JxlAnsHistogram.InitialState;
        var words = new List<ushort>();
        for (int k = stream.Count - 1; k >= 0; k--)
            hist[clusters[stream[k].Ctx]].EncodeStep(ref state, stream[k].Value, words);
        words.Reverse();

        WriteHeader(bw, clusters, perClusterAlphabet);
        bw.WriteBits(state, 32);
        foreach (ushort w in words)
            bw.WriteBits(w, 16);
    }

    /// <summary>Writes only the Decoder header (no ANS state/data) — for a decoder that is parsed but not read.</summary>
    public static void WriteDecoderHeaderOnly(JxlBitWriter bw, byte[] clusters, int[] perClusterAlphabet)
        => WriteHeader(bw, clusters, perClusterAlphabet);

    private static JxlAnsHistogram[] BuildHistograms(int[] perClusterAlphabet)
    {
        var hist = new JxlAnsHistogram[perClusterAlphabet.Length];
        for (int c = 0; c < hist.Length; c++)
        {
            var hw = new JxlBitWriter();
            WriteEvenHeader(hw, perClusterAlphabet[c]);
            var hr = new JxlBitReader(hw.ToArray());
            hist[c] = JxlAnsHistogram.Parse(ref hr, Las);
        }
        return hist;
    }

    private static void WriteHeader(JxlBitWriter bw, byte[] clusters, int[] perClusterAlphabet)
    {
        bw.WriteBit(false); // lz77 disabled

        // The decoder reads a context map only when num_dist > 1 (single-dist short-circuits).
        if (clusters.Length > 1)
        {
            int maxCluster = clusters.Max();
            int nbits = maxCluster == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)maxCluster);
            bw.WriteBit(true);            // simple context map
            bw.WriteBits((uint)nbits, 2);
            foreach (byte c in clusters)
                bw.WriteBits(c, nbits);
        }

        bw.WriteBit(false);                   // use_prefix_code = false (ANS)
        bw.WriteBits((uint)(Las - 5), 2);     // log_alphabet_size
        var config = JxlIntegerConfig.Create(Las, 0, 0);
        for (int c = 0; c < perClusterAlphabet.Length; c++)
            config.Write(bw, Las);
        foreach (int alphabet in perClusterAlphabet)
            WriteEvenHeader(bw, alphabet);
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
        int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);
        bw.WriteBits((uint)n, 3);
        bw.WriteBits((uint)(value - (1 << n)), n);
    }
}
