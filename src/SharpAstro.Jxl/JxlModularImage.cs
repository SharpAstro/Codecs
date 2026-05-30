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
/// Decodes Modular channels (ISO/IEC 18181-1 §H): per channel, per pixel, walk the MA tree to a
/// leaf, decode the residual with that leaf's cluster, add the predictor output, and record the
/// sample. Currently supports the still-image lossless path (single group, predictors incl. the
/// weighted predictor, RCT). Squeeze/Palette and cross-channel properties are not yet wired.
/// </summary>
internal static class JxlModularImage
{
    public static void Decode(
        ref JxlBitReader br, JxlMaConfig tree, JxlModularChannel[] channels, in JxlWpHeader wp, uint streamIndex)
    {
        JxlEntropyDecoder decoder = tree.SampleDecoder;
        decoder.Begin(ref br);

        int distMultiplier = 0;
        foreach (JxlModularChannel ch in channels)
            distMultiplier = Math.Max(distMultiplier, ch.Width);

        bool needsWp = TreeNeedsWeightedPredictor(tree.Tree);
        bool singleLeaf = tree.Tree is JxlMaLeaf;
        var props = new ModularProperties();

        for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
        {
            JxlModularChannel grid = channels[channelIndex];
            int width = grid.Width, height = grid.Height;
            JxlWeightedPredictor? predictor = needsWp ? new JxlWeightedPredictor(wp) : null;
            predictor?.Reset(width);

            for (int y = 0; y < height; y++)
            {
                int prevGrad = 0;
                for (int x = 0; x < width; x++)
                {
                    int n = y > 0 ? grid.Get(x, y - 1) : (x > 0 ? grid.Get(x - 1, 0) : 0);
                    int w = x > 0 ? grid.Get(x - 1, y) : (y > 0 ? grid.Get(0, y - 1) : 0);
                    int nw = y > 0 ? (x > 0 ? grid.Get(x - 1, y - 1) : grid.Get(0, y - 1))
                                   : (x > 0 ? grid.Get(x - 1, 0) : 0);
                    int ne = (y == 0 || x + 1 >= width) ? n : grid.Get(x + 1, y - 1);
                    int nee = (y == 0 || x + 2 >= width) ? ne : grid.Get(x + 2, y - 1);
                    int nn = y >= 2 ? grid.Get(x, y - 2) : 0;
                    int ww = x >= 2 ? grid.Get(x - 2, y) : w;

                    JxlWeightedPredictor.Prediction wpPred = default;
                    long wpValue = 0;
                    int maxError = 0;
                    if (predictor is not null)
                    {
                        wpPred = predictor.Predict(n, nw, ne, w, nn);
                        wpValue = wpPred.Value;
                        maxError = wpPred.MaxError;
                    }

                    JxlMaLeaf leaf;
                    if (singleLeaf)
                    {
                        leaf = (JxlMaLeaf)tree.Tree;
                    }
                    else
                    {
                        props.Update(channelIndex, streamIndex, x, y, n, w, nw, ne, nn, ww, maxError, prevGrad);
                        leaf = tree.GetLeaf(props);
                    }

                    uint token = decoder.ReadVarintClustered(ref br, leaf.Cluster, (uint)distMultiplier);
                    int diff = JxlModular.UnpackSigned(token);
                    int residual = unchecked(diff * (int)leaf.Multiplier + leaf.Offset);
                    var neighbors = new JxlNeighbors { N = n, W = w, NW = nw, NE = ne, NN = nn, WW = ww, NEE = nee };
                    int pred = JxlModularPredictor.Predict(leaf.Predictor, neighbors, wpValue);
                    int sample = unchecked(residual + pred);

                    grid.Set(x, y, sample);
                    predictor?.Record(wpPred, sample);
                    prevGrad = unchecked(w - nw + n);
                }
            }
        }

        decoder.Finish();
    }

    private static bool TreeNeedsWeightedPredictor(JxlMaNode node) => node switch
    {
        JxlMaLeaf leaf => leaf.Predictor == JxlPredictor.SelfCorrecting,
        JxlMaDecision d => d.Property == 15 || TreeNeedsWeightedPredictor(d.Left) || TreeNeedsWeightedPredictor(d.Right),
        _ => false,
    };

    private sealed class ModularProperties : IJxlProperties
    {
        private int _channelIndex, _streamIndex, _x, _y, _n, _w, _nw, _ne, _nn, _ww, _maxError, _prevGrad;

        public void Update(int channelIndex, uint streamIndex, int x, int y, int n, int w, int nw, int ne, int nn, int ww, int maxError, int prevGrad)
        {
            _channelIndex = channelIndex;
            _streamIndex = (int)streamIndex;
            _x = x; _y = y; _n = n; _w = w; _nw = nw; _ne = ne; _nn = nn; _ww = ww;
            _maxError = maxError; _prevGrad = prevGrad;
        }

        public int Get(int index) => index switch
        {
            0 => _channelIndex,
            1 => _streamIndex,
            2 => _y,
            3 => _x,
            4 => (int)Math.Abs((long)_n),
            5 => (int)Math.Abs((long)_w),
            6 => _n,
            7 => _w,
            8 => unchecked(_w - _prevGrad),
            9 => unchecked(_w - _nw + _n),
            10 => unchecked(_w - _nw),
            11 => unchecked(_nw - _n),
            12 => unchecked(_n - _ne),
            13 => unchecked(_n - _nn),
            14 => unchecked(_w - _ww),
            15 => _maxError,
            _ => throw new NotSupportedException($"JPEG XL Modular cross-channel property {index} is not yet supported."),
        };
    }
}
