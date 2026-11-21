namespace SharpAstro.Jxl;

/// <summary>
/// A Modular channel: a row-major integer grid plus its shifts (ISO/IEC 18181-1 §H). Mutable so
/// transforms (Palette/Squeeze) can reshape the channel list and rewrite grids.
/// </summary>
internal sealed class JxlModularChannel
{
    public int[] Data { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Hshift { get; set; }
    public int Vshift { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }

    public JxlModularChannel(int width, int height, int hshift = 0, int vshift = 0)
    {
        Width = width;
        Height = height;
        Hshift = hshift;
        Vshift = vshift;
        OriginalWidth = width;
        OriginalHeight = height;
        Data = new int[width * height];
    }

    public int Get(int x, int y) => Data[y * Width + x];
    public void Set(int x, int y, int value) => Data[y * Width + x] = value;
}

/// <summary>
/// Decodes Modular channels (ISO/IEC 18181-1 §H), ported from jxl-oxide's
/// <c>TransformedModularSubimage::decode_inner</c> (jxl-modular image.rs) and its <c>PredictorState</c> /
/// <c>Properties</c> (predictor.rs): per channel, per pixel, walk the MA tree to a leaf, decode the residual with
/// that leaf's cluster, add the predictor output, and record the sample.
///
/// <para>
/// As there, the tree is first specialised per channel: the channel-index and stream-index properties (0 and 1)
/// are constants for a channel, and a property of a previous channel that does not exist reads as 0, so those
/// decisions are resolved once (jxl-oxide <c>MaTreeNode::next_decision_node</c>). Properties 16 and up read the
/// previous channels of the same size and shift, nearest first, four properties each (|c|, c, |c - g|, c - g with
/// g that channel's clamped gradient). A channel of zero width or height is skipped. Every stream decodes from
/// its own clone of the tree's sample decoder, so ANS and LZ77 state never carry between streams.
/// </para>
/// </summary>
internal static class JxlModularImage
{
    public static void Decode(
        ref JxlBitReader br, JxlMaConfig tree, JxlModularChannel[] channels, in JxlWpHeader wp, uint streamIndex)
    {
        JxlEntropyDecoder decoder = tree.SampleDecoder.Clone();
        decoder.Begin(ref br);

        int distMultiplier = 0;
        foreach (JxlModularChannel ch in channels)
            distMultiplier = Math.Max(distMultiplier, ch.Width);

        var prev = new List<JxlModularChannel>();
        for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
        {
            JxlModularChannel grid = channels[channelIndex];
            if (grid.Width == 0 || grid.Height == 0)
                continue;

            // Previous decoded channels with the same size and shifts, nearest first.
            prev.Clear();
            for (int i = channelIndex - 1; i >= 0; i--)
            {
                JxlModularChannel p = channels[i];
                if (p.Width == grid.Width && p.Height == grid.Height && p.Hshift == grid.Hshift && p.Vshift == grid.Vshift)
                    prev.Add(p);
            }

            JxlMaNode specialised = Specialise(tree.Tree, channelIndex, (int)streamIndex, prev.Count);
            DecodeChannel(ref br, decoder, (uint)distMultiplier, specialised, wp, grid, prev.ToArray());
        }

        decoder.Finish();
    }

    /// <summary>
    /// Resolves the decisions that are constant for one channel of one stream (jxl-oxide
    /// <c>next_decision_node</c>): property 0 is the channel index, 1 the stream index, and a property of a
    /// previous channel beyond <paramref name="prevChannels"/> reads as 0.
    /// </summary>
    internal static JxlMaNode Specialise(JxlMaNode node, int channel, int stream, int prevChannels)
    {
        while (node is JxlMaDecision d)
        {
            if (d.Property == 0 || d.Property == 1)
            {
                int target = d.Property == 0 ? channel : stream;
                node = target > d.Value ? d.Left : d.Right;
                continue;
            }
            if (d.Property >= 16 && (d.Property - 16) / 4 >= prevChannels)
            {
                node = 0 > d.Value ? d.Left : d.Right;
                continue;
            }
            JxlMaNode left = Specialise(d.Left, channel, stream, prevChannels);
            JxlMaNode right = Specialise(d.Right, channel, stream, prevChannels);
            if (ReferenceEquals(left, d.Left) && ReferenceEquals(right, d.Right))
                return d;
            return new JxlMaDecision { Property = d.Property, Value = d.Value, Left = left, Right = right };
        }
        return node;
    }

    private static bool NeedsWeightedPredictor(JxlMaNode node) => node switch
    {
        JxlMaLeaf leaf => leaf.Predictor == JxlPredictor.SelfCorrecting,
        JxlMaDecision d => d.Property == 15 || NeedsWeightedPredictor(d.Left) || NeedsWeightedPredictor(d.Right),
        _ => false,
    };

    private static void DecodeChannel(
        ref JxlBitReader br, JxlEntropyDecoder decoder, uint distMultiplier, JxlMaNode tree, in JxlWpHeader wp,
        JxlModularChannel grid, JxlModularChannel[] prev)
    {
        int width = grid.Width, height = grid.Height;
        int[] d = grid.Data;
        bool needsWp = NeedsWeightedPredictor(tree);

        // A single leaf with a stateless predictor needs no properties at all (the common LF / HfMetadata case).
        if (tree is JxlMaLeaf only && !needsWp)
        {
            DecodeSingleLeaf(ref br, decoder, distMultiplier, only, d, width, height);
            return;
        }

        JxlWeightedPredictor? predictor = null;
        if (needsWp)
        {
            predictor = new JxlWeightedPredictor(wp);
            predictor.Reset(width);
        }

        var props = new Properties { Prev = prev };
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            props.PrevGrad = 0;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                int n, w, nw;
                if (y == 0)
                {
                    n = w = nw = x > 0 ? d[idx - 1] : 0;
                }
                else if (x == 0)
                {
                    n = w = nw = d[idx - width];
                }
                else
                {
                    n = d[idx - width];
                    w = d[idx - 1];
                    nw = d[idx - width - 1];
                }
                int ne = (y == 0 || x + 1 >= width) ? n : d[idx - width + 1];
                int nee = (y == 0 || x + 2 >= width) ? ne : d[idx - width + 2];
                int nn = y >= 2 ? d[idx - 2 * width] : n;
                int ww = x >= 2 ? d[idx - 2] : w;

                JxlWeightedPredictor.Prediction wpPred = default;
                if (predictor is not null)
                    wpPred = predictor.Predict(n, nw, ne, w, nn);

                props.X = x; props.Y = y;
                props.N = n; props.W = w; props.NW = nw; props.NE = ne; props.NN = nn; props.WW = ww;
                props.MaxError = wpPred.MaxError;

                JxlMaNode node = tree;
                while (node is JxlMaDecision dec)
                    node = props.Get(dec.Property) > dec.Value ? dec.Left : dec.Right;
                var leaf = (JxlMaLeaf)node;

                uint token = decoder.ReadVarintClustered(ref br, leaf.Cluster, distMultiplier);
                int residual = unchecked(JxlModular.UnpackSigned(token) * (int)leaf.Multiplier + leaf.Offset);
                var neighbors = new JxlNeighbors { N = n, W = w, NW = nw, NE = ne, NN = nn, WW = ww, NEE = nee };
                int sample = unchecked(residual + JxlModularPredictor.Predict(leaf.Predictor, neighbors, wpPred.Value));

                d[idx] = sample;
                predictor?.Record(wpPred, sample);
                props.PrevGrad = unchecked(w - nw + n);
            }
        }
    }

    private static void DecodeSingleLeaf(
        ref JxlBitReader br, JxlEntropyDecoder decoder, uint distMultiplier, JxlMaLeaf leaf, int[] d, int width, int height)
    {
        byte cluster = leaf.Cluster;
        int mul = (int)leaf.Multiplier, offset = leaf.Offset;
        JxlPredictor predictorKind = leaf.Predictor;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                uint token = decoder.ReadVarintClustered(ref br, cluster, distMultiplier);
                int residual = unchecked(JxlModular.UnpackSigned(token) * mul + offset);
                int pred;
                if (predictorKind == JxlPredictor.Zero)
                {
                    pred = 0;
                }
                else
                {
                    int n, w, nw;
                    if (y == 0)
                        n = w = nw = x > 0 ? d[idx - 1] : 0;
                    else if (x == 0)
                        n = w = nw = d[idx - width];
                    else
                    {
                        n = d[idx - width];
                        w = d[idx - 1];
                        nw = d[idx - width - 1];
                    }
                    if (predictorKind == JxlPredictor.Gradient)
                    {
                        pred = JxlModular.GradClamped(n, w, nw);
                    }
                    else
                    {
                        int ne = (y == 0 || x + 1 >= width) ? n : d[idx - width + 1];
                        int nee = (y == 0 || x + 2 >= width) ? ne : d[idx - width + 2];
                        int nn = y >= 2 ? d[idx - 2 * width] : n;
                        int ww = x >= 2 ? d[idx - 2] : w;
                        var neighbors = new JxlNeighbors { N = n, W = w, NW = nw, NE = ne, NN = nn, WW = ww, NEE = nee };
                        pred = JxlModularPredictor.Predict(predictorKind, neighbors);
                    }
                }
                d[idx] = unchecked(residual + pred);
            }
        }
    }

    /// <summary>The MA-tree properties of the current sample (jxl-oxide <c>Properties</c>).</summary>
    private sealed class Properties
    {
        public required JxlModularChannel[] Prev { get; init; }
        public int X, Y, N, W, NW, NE, NN, WW, MaxError, PrevGrad;

        public int Get(int index)
        {
            switch (index)
            {
                case 0:
                case 1:
                    return 0; // resolved by Specialise
                case 2: return Y;
                case 3: return X;
                case 4: return (int)(uint)Math.Abs((long)N);
                case 5: return (int)(uint)Math.Abs((long)W);
                case 6: return N;
                case 7: return W;
                case 8: return unchecked(W - PrevGrad);
                case 9: return unchecked(W - NW + N);
                case 10: return unchecked(W - NW);
                case 11: return unchecked(NW - N);
                case 12: return unchecked(N - NE);
                case 13: return unchecked(N - NN);
                case 14: return unchecked(W - WW);
                case 15: return MaxError;
                default:
                    return GetExtra(index - 16);
            }
        }

        private int GetExtra(int extra)
        {
            int k = extra / 4, kind = extra % 4;
            if (k >= Prev.Length)
                return 0;
            JxlModularChannel ch = Prev[k];
            int[] d = ch.Data;
            int width = ch.Width;
            int c = d[Y * width + X];
            if (kind == 0)
                return (int)(uint)Math.Abs((long)c);
            if (kind == 1)
                return c;
            int g;
            if (X == 0 && Y == 0)
                g = 0;
            else if (X == 0)
                g = d[(Y - 1) * width];
            else if (Y == 0)
                g = d[X - 1];
            else
                g = JxlModular.GradClamped(d[(Y - 1) * width + X], d[Y * width + X - 1], d[(Y - 1) * width + X - 1]);
            return kind == 2 ? (int)(uint)Math.Abs((long)c - g) : unchecked(c - g);
        }
    }
}

/// <summary>
/// One complete Modular sub-bitstream (ISO/IEC 18181-1 §H.2): the local header, the forward transforms on the
/// channel list, a local MA tree unless the header asks for the global one, the channel data, and the inverse
/// transforms. Ported from jxl-oxide's <c>read_and_validate_local_modular_header</c> plus
/// <c>prepare_subimage</c> / <c>decode</c> / <c>finish</c>. A request with no channels reads nothing, as
/// <c>Modular::parse</c> does.
/// </summary>
internal static class JxlModularStream
{
    public static JxlModularChannel[] Read(
        ref JxlBitReader br, JxlMaConfig? globalTree, JxlModularChannel[] channels, uint streamIndex, int bitDepth)
    {
        if (channels.Length == 0)
            return channels;

        JxlModularHeader header = JxlModularHeader.Parse(ref br);
        var list = new List<JxlModularChannel>(channels);
        int nbMeta = 0;
        foreach (JxlTransform t in header.Transforms)
            JxlModularTransforms.ApplyForward(list, t, ref nbMeta);

        JxlMaConfig tree;
        if (header.UseGlobalTree)
        {
            tree = globalTree ?? throw new InvalidDataException("JPEG XL: a Modular stream asks for the global tree, but the frame has none.");
        }
        else
        {
            long samples = 0;
            foreach (JxlModularChannel ch in list)
                samples += (long)ch.Width * ch.Height;
            tree = JxlMaConfig.Parse(ref br, (int)Math.Min(1 << 20, 1024 + samples));
        }

        JxlModularImage.Decode(ref br, tree, list.ToArray(), header.Wp, streamIndex);

        for (int i = header.Transforms.Length - 1; i >= 0; i--)
            JxlModularTransforms.ApplyInverse(list, header.Transforms[i], bitDepth);

        return list.ToArray();
    }
}
