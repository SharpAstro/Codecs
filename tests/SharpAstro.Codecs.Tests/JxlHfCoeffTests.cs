using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// VarDCT HF-coefficient entropy codec (Rung 5k) — JxlHfCoeff, the PassGroup body. Validated by
/// self round-trip through the real entropy layer with the config/data split (5i): the encoder
/// produces a (ctx, value) symbol stream, the entropy config goes in one blob (the HfGlobal slice)
/// and the coded data in another (the PassGroup slice), and the decoder walks the block grid
/// identically — recomputing every context — to reconstruct the coefficient planes. End-to-end
/// validation against libjxl/Magick happens once the full VarDct frame assembles (5m).
/// </summary>
public sealed class JxlHfCoeffTests
{
    // Default HfBlockContext: 15 clusters, the fixed 39-entry block_ctx_map, empty thresholds.
    private const int NumBlockClusters = 15;
    private const int CtxSize = 495 * NumBlockClusters; // 7425 hf_dist contexts (num_hf_presets = 1)

    private static JxlHfCoeff.Params MakeParams(int bw, int bh)
    {
        var grid = new JxlBlockInfo[bw * bh];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = JxlBlockInfo.Data(JxlVarDctTransform.Dct8, hfMul: 1);

        return new JxlHfCoeff.Params
        {
            Bw = bw,
            Bh = bh,
            BlockGrid = grid,
            NumBlockClusters = NumBlockClusters,
            BlockCtxMap = JxlLfGlobalVarDct.DefaultBlockCtxMap,
            CoeffShift = 0,
        };
    }

    /// <summary>Sparse, signed, deterministic AC coefficients (DC left at zero — it is the LF path's job).</summary>
    private static int[][] MakeCoeffPlanes(int bw, int bh)
    {
        int gridW = bw * 8, gridH = bh * 8;
        var planes = new int[3][];
        for (int ch = 0; ch < 3; ch++)
        {
            var plane = new int[gridW * gridH];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                    for (int py = 0; py < 8; py++)
                        for (int px = 0; px < 8; px++)
                        {
                            if (px == 0 && py == 0)
                                continue; // DC
                            int seed = ch * 131 + (by * bw + bx) * 17 + py * 8 + px;
                            int v = seed % 7 == 0 ? seed % 11 - 5 : 0; // ~1/7 nonzero, range -5..5
                            plane[(by * 8 + py) * gridW + bx * 8 + px] = v;
                        }
            planes[ch] = plane;
        }
        return planes;
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    public void HfCoeff_Dct8_RoundTripsThroughEntropyLayer(int bw, int bh)
    {
        JxlHfCoeff.Params p = MakeParams(bw, bh);
        int[][] planes = MakeCoeffPlanes(bw, bh);

        // Encode -> symbol stream.
        List<(int Ctx, uint Value)> stream = JxlHfCoeff.Encode(p, planes);
        stream.ShouldNotBeEmpty();

        // Entropy: all 7425 contexts map to a single cluster/histogram (correct, suboptimal).
        byte[] contextMap = new byte[CtxSize]; // all zeros -> 1 cluster
        JxlIntegerConfig[] configs = [JxlIntegerConfig.Create(splitExponent: 4, msbInToken: 0, lsbInToken: 0)];
        var enc = new JxlEntropyEncoder(contextMap, configs);
        JxlEntropyEncoder.Plan plan = enc.Prepare(stream);

        var configBw = new JxlBitWriter();
        enc.WriteConfig(configBw, plan);    // the HfGlobal slice
        var dataBw = new JxlBitWriter();
        enc.WriteData(dataBw, plan, stream); // the PassGroup slice (num_hf_presets == 1 -> no hfp bits)

        // Decode: parse the config from its blob, then walk the grid over the data blob.
        var configBr = new JxlBitReader(configBw.ToArray());
        JxlEntropyDecoder dist = JxlEntropyDecoder.Parse(ref configBr, (uint)CtxSize);

        var dataBr = new JxlBitReader(dataBw.ToArray());
        var outPlanes = new int[3][];
        for (int c = 0; c < 3; c++)
            outPlanes[c] = new int[bw * 8 * bh * 8];

        JxlHfCoeff.Decode(ref dataBr, dist, dist.ClusterMap, numHfPresets: 1, p, outPlanes);

        for (int c = 0; c < 3; c++)
            outPlanes[c].ShouldBe(planes[c], $"channel {c}");
    }

    [Fact]
    public void HfCoeff_DenseBlock_RoundTrips()
    {
        // A fully-dense block (every AC coefficient non-zero) drives non_zeros to its max and walks
        // the whole scan — the path where the remaining-non-zero / freq context sum is tightest.
        JxlHfCoeff.Params p = MakeParams(2, 2);
        int gridW = 16;
        var planes = new int[3][];
        for (int ch = 0; ch < 3; ch++)
        {
            var plane = new int[gridW * gridW];
            for (int by = 0; by < 2; by++)
                for (int bx = 0; bx < 2; bx++)
                    for (int py = 0; py < 8; py++)
                        for (int px = 0; px < 8; px++)
                            if (px != 0 || py != 0)
                                plane[(by * 8 + py) * gridW + bx * 8 + px] = (px + py) % 2 == 0 ? 3 : -2;
            planes[ch] = plane;
        }

        List<(int Ctx, uint Value)> stream = JxlHfCoeff.Encode(p, planes);

        byte[] contextMap = new byte[CtxSize];
        JxlIntegerConfig[] configs = [JxlIntegerConfig.Create(4, 0, 0)];
        var enc = new JxlEntropyEncoder(contextMap, configs);
        JxlEntropyEncoder.Plan plan = enc.Prepare(stream);
        var configBw = new JxlBitWriter();
        enc.WriteConfig(configBw, plan);
        var dataBw = new JxlBitWriter();
        enc.WriteData(dataBw, plan, stream);

        var configBr = new JxlBitReader(configBw.ToArray());
        JxlEntropyDecoder dist = JxlEntropyDecoder.Parse(ref configBr, (uint)CtxSize);
        var dataBr = new JxlBitReader(dataBw.ToArray());
        var outPlanes = new int[3][];
        for (int c = 0; c < 3; c++)
            outPlanes[c] = new int[gridW * gridW];
        JxlHfCoeff.Decode(ref dataBr, dist, dist.ClusterMap, 1, p, outPlanes);

        for (int c = 0; c < 3; c++)
            outPlanes[c].ShouldBe(planes[c], $"channel {c}");
    }
}
