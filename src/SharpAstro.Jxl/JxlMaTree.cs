namespace SharpAstro.Jxl;

/// <summary>Supplies Modular sample properties (by index) to an MA-tree walk (ISO/IEC 18181-1 §H.4).</summary>
internal interface IJxlProperties
{
    int Get(int index);
}

/// <summary>A node of the meta-adaptive decision tree.</summary>
internal abstract class JxlMaNode;

internal sealed class JxlMaDecision : JxlMaNode
{
    public required int Property { get; init; }
    public required int Value { get; init; }
    public required JxlMaNode Left { get; init; }  // taken when property > value
    public required JxlMaNode Right { get; init; } // taken when property <= value
}

internal sealed class JxlMaLeaf : JxlMaNode
{
    public required byte Cluster { get; init; }
    public required JxlPredictor Predictor { get; init; }
    public required int Offset { get; init; }
    public required uint Multiplier { get; init; }
}

/// <summary>
/// JPEG XL meta-adaptive (MA) tree config (ISO/IEC 18181-1 §H.4, jxl-modular ma.rs). The tree is
/// read with a dedicated 6-distribution entropy decoder; the sample decoder (one distribution per
/// leaf) then drives residual decoding. For each sample the tree is walked from the root comparing
/// properties against split values, yielding (cluster, predictor, offset, multiplier).
/// The flat-tree perf optimisation is skipped — the recursive walk is bit-identical.
/// </summary>
internal sealed class JxlMaConfig
{
    private const int MaxNodes = 1 << 26;

    public JxlMaNode Tree { get; }
    public JxlEntropyDecoder SampleDecoder { get; }
    public int NumLeaves { get; }

    private JxlMaConfig(JxlMaNode tree, JxlEntropyDecoder sampleDecoder, int numLeaves)
    {
        Tree = tree;
        SampleDecoder = sampleDecoder;
        NumLeaves = numLeaves;
    }

    private abstract record RawNode;
    private sealed record RawDecision(int Property, int Value) : RawNode;
    private sealed record RawLeaf(int Ctx, JxlPredictor Predictor, int Offset, uint Multiplier) : RawNode;

    public static JxlMaConfig Parse(ref JxlBitReader br, int nodeLimit)
    {
        // Phase 1: the 6-distribution tree-structure decoder.
        var treeDecoder = JxlEntropyDecoder.Parse(ref br, 6);
        treeDecoder.Begin(ref br);

        // Phase 2: read tree nodes pre-order; nodes_left tracks the branching frontier.
        var nodes = new List<RawNode>();
        int ctx = 0;
        int nodesLeft = 1;
        while (nodesLeft > 0)
        {
            if (nodes.Count >= MaxNodes || nodes.Count > nodeLimit)
                throw new InvalidDataException("JPEG XL MA tree too large.");

            nodesLeft--;
            uint property = treeDecoder.ReadVarint(ref br, 1);
            if (property == 0)
            {
                uint predictorRaw = treeDecoder.ReadVarint(ref br, 2);
                if (predictorRaw > 13)
                    throw new InvalidDataException($"JPEG XL invalid MA tree predictor {predictorRaw}.");
                int offset = JxlModular.UnpackSigned(treeDecoder.ReadVarint(ref br, 3));
                uint mulLog = treeDecoder.ReadVarint(ref br, 4);
                if (mulLog > 30)
                    throw new InvalidDataException("JPEG XL invalid MA tree multiplier exponent.");
                uint mulBits = treeDecoder.ReadVarint(ref br, 5);
                if (mulBits > (1u << (31 - (int)mulLog)) - 2)
                    throw new InvalidDataException("JPEG XL invalid MA tree multiplier mantissa.");
                uint multiplier = (mulBits + 1) << (int)mulLog;
                nodes.Add(new RawLeaf(ctx, (JxlPredictor)predictorRaw, offset, multiplier));
                ctx++;
            }
            else
            {
                int prop = (int)property - 1;
                int value = JxlModular.UnpackSigned(treeDecoder.ReadVarint(ref br, 0));
                nodes.Add(new RawDecision(prop, value));
                nodesLeft += 2;
            }
        }
        treeDecoder.Finish();

        int numLeaves = ctx;

        // Phase 3: the sample decoder — one distribution per leaf.
        var sampleDecoder = JxlEntropyDecoder.Parse(ref br, (uint)numLeaves);
        byte[] clusterMap = sampleDecoder.ClusterMap;

        // Reconstruct the recursive tree (reverse pass; right child popped before left).
        var stack = new LinkedList<JxlMaNode>();
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i] is RawDecision d)
            {
                JxlMaNode right = PopFront(stack);
                JxlMaNode left = PopFront(stack);
                stack.AddLast(new JxlMaDecision { Property = d.Property, Value = d.Value, Left = left, Right = right });
            }
            else
            {
                var leaf = (RawLeaf)nodes[i];
                stack.AddLast(new JxlMaLeaf
                {
                    Cluster = clusterMap[leaf.Ctx],
                    Predictor = leaf.Predictor,
                    Offset = leaf.Offset,
                    Multiplier = leaf.Multiplier,
                });
            }
        }
        if (stack.Count != 1)
            throw new InvalidDataException("JPEG XL malformed MA tree.");

        return new JxlMaConfig(stack.First!.Value, sampleDecoder, numLeaves);
    }

    /// <summary>Walks the tree against the supplied properties to the matching leaf.</summary>
    public JxlMaLeaf GetLeaf(IJxlProperties properties)
    {
        JxlMaNode node = Tree;
        while (node is JxlMaDecision d)
            node = properties.Get(d.Property) > d.Value ? d.Left : d.Right;
        return (JxlMaLeaf)node;
    }

    private static JxlMaNode PopFront(LinkedList<JxlMaNode> stack)
    {
        JxlMaNode value = stack.First!.Value;
        stack.RemoveFirst();
        return value;
    }
}
