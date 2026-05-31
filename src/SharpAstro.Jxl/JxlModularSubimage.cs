namespace SharpAstro.Jxl;

/// <summary>
/// Encodes Modular <em>sub-images</em> that share a single global MA tree — the shape VarDCT uses for
/// its LfCoeff (LF-DC) and HfMetadata channels (ISO/IEC 18181-1 §H, jxl-frame lf_group.rs /
/// hf_metadata.rs). The MA tree and the sample-decoder <em>config</em> (histograms) are written once
/// in GlobalModular; each sub-image then contributes only its sample <em>data</em> (initial rANS
/// state + residual symbols) in its own frame section, decoded by <see cref="JxlModularImage.Decode"/>
/// with that sub-image's stream id. This is the Modular counterpart of the entropy config/data split
/// (<see cref="JxlEntropyEncoder.WriteConfig"/> / <see cref="JxlEntropyEncoder.WriteData"/>).
///
/// <para>
/// Targets the minimal-encoder shape: one global tree consisting of a single Gradient-predicted leaf
/// (cluster 0, offset 0, multiplier 1), which <see cref="JxlMaConfig.Parse"/> accepts and our
/// libjxl-validated Modular decoder reads back losslessly. Richer trees (per-stream contexts via the
/// stream-index property) are a later optimisation; correctness, not ratio, is the goal here.
/// </para>
/// </summary>
internal static class JxlModularSubimage
{
    // Both the tree-structure decoder and the sample decoder use the proven Modular hybrid-uint config.
    private static readonly JxlIntegerConfig TreeConfig = JxlIntegerConfig.Create(4, 0, 0);
    public static readonly JxlIntegerConfig SampleConfig = JxlIntegerConfig.Create(4, 0, 0);

    /// <summary>
    /// Build the Gradient-predicted residual symbol stream for a sub-image's channels (single leaf ⇒
    /// every residual is coded under cluster 0). Neighbour selection matches
    /// <see cref="JxlModularImage.Decode"/> exactly so the lossless reconstruction agrees.
    /// </summary>
    public static List<(int Ctx, uint Value)> BuildSampleStream(JxlModularChannel[] channels)
    {
        var stream = new List<(int Ctx, uint Value)>();
        foreach (JxlModularChannel grid in channels)
        {
            int width = grid.Width, height = grid.Height;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int n = y > 0 ? grid.Get(x, y - 1) : (x > 0 ? grid.Get(x - 1, 0) : 0);
                    int w = x > 0 ? grid.Get(x - 1, y) : (y > 0 ? grid.Get(0, y - 1) : 0);
                    int nw = y > 0
                        ? (x > 0 ? grid.Get(x - 1, y - 1) : grid.Get(0, y - 1))
                        : (x > 0 ? grid.Get(x - 1, 0) : 0);
                    int pred = JxlModular.GradClamped(n, w, nw); // Gradient predictor
                    stream.Add((0, JxlModular.PackSigned(unchecked(grid.Get(x, y) - pred))));
                }
        }
        return stream;
    }

    /// <summary>
    /// Write the shared global MA tree (a single Gradient leaf) followed by the sample-decoder config
    /// derived from <paramref name="samplePlan"/>. This is what <see cref="JxlMaConfig.Parse"/> reads:
    /// the 6-distribution tree-structure stream (its own config+data) and then the sample-decoder
    /// config (parsed but not begun — each sub-image begins it over its own data).
    /// </summary>
    public static void WriteSharedTree(JxlBitWriter bw, JxlEntropyEncoder sampleEnc, JxlEntropyEncoder.Plan samplePlan)
    {
        // Tree structure: property == 0 marks a leaf, then predictor / offset / mul_log / mul_bits.
        // Contexts: 0=value, 1=property, 2=predictor, 3=offset, 4=mul_log, 5=mul_bits.
        var treeStream = new (int Ctx, uint Value)[]
        {
            (1, 0),                            // property = 0 -> leaf
            (2, (uint)JxlPredictor.Gradient),  // predictor
            (3, 0),                            // offset (packed) = 0
            (4, 0),                            // mul_log = 0
            (5, 0),                            // mul_bits = 0 -> multiplier (0+1)<<0 = 1
        };
        new JxlEntropyEncoder([0, 0, 0, 0, 0, 0], [TreeConfig]).Encode(bw, treeStream);

        // Sample-decoder config (one distribution, since the tree has one leaf).
        sampleEnc.WriteConfig(bw, samplePlan);
    }

    /// <summary>A sample encoder for the single-leaf tree: one cluster, the proven sample config.</summary>
    public static JxlEntropyEncoder NewSampleEncoder() => new([0], [SampleConfig]);
}
