using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// VarDCT 5l foundation — Modular sub-images that share one global MA tree, the shape used by
/// LfCoeff (LF-DC) and HfMetadata. Validates the Modular config/data split: the tree + sample-decoder
/// config are written once, then each sub-image contributes only its own rANS state + residual
/// symbols, read back by the (libjxl-validated, Rung 4) JxlModularImage.Decode with its stream id.
/// </summary>
public sealed class JxlModularSubimageTests
{
    private static JxlModularChannel[] MakeChannels((int W, int H)[] dims, uint seed)
    {
        uint s = seed;
        var chans = new JxlModularChannel[dims.Length];
        for (int c = 0; c < dims.Length; c++)
        {
            var ch = new JxlModularChannel(dims[c].W, dims[c].H);
            for (int i = 0; i < ch.Data.Length; i++)
            {
                s = s * 1664525 + 1013904223;
                ch.Data[i] = (int)(s % 201) - 100; // -100..100
            }
            chans[c] = ch;
        }
        return chans;
    }

    private static void AssertSame(JxlModularChannel[] expected, JxlModularChannel[] actual)
    {
        actual.Length.ShouldBe(expected.Length);
        for (int c = 0; c < expected.Length; c++)
        {
            actual[c].Width.ShouldBe(expected[c].Width);
            actual[c].Height.ShouldBe(expected[c].Height);
            actual[c].Data.ShouldBe(expected[c].Data, $"channel {c}");
        }
    }

    [Fact]
    public void SharedTree_TwoSubimages_RoundTrip()
    {
        // Sub-image A: 2 channels (LfCoeff-like); sub-image B: 3 channels of mixed sizes
        // (HfMetadata-like). Both share one tree + one sample config; each has its own data blob.
        (int, int)[] dimsA = [(5, 4), (5, 4)];
        (int, int)[] dimsB = [(3, 3), (7, 2), (4, 4)];
        JxlModularChannel[] subA = MakeChannels(dimsA, 0xA11CE);
        JxlModularChannel[] subB = MakeChannels(dimsB, 0xB0B);

        List<(int Ctx, uint Value)> streamA = JxlModularSubimage.BuildSampleStream(subA);
        List<(int Ctx, uint Value)> streamB = JxlModularSubimage.BuildSampleStream(subB);

        JxlEntropyEncoder sampleEnc = JxlModularSubimage.NewSampleEncoder();
        JxlEntropyEncoder.Plan plan = sampleEnc.Prepare(streamA, streamB);

        var treeBw = new JxlBitWriter();
        JxlModularSubimage.WriteSharedTree(treeBw, sampleEnc, plan);
        var dataABw = new JxlBitWriter();
        sampleEnc.WriteData(dataABw, plan, streamA);
        var dataBBw = new JxlBitWriter();
        sampleEnc.WriteData(dataBBw, plan, streamB);

        // Decode: parse the shared tree once, then each sub-image over its own data with its stream id.
        var treeBr = new JxlBitReader(treeBw.ToArray());
        JxlMaConfig tree = JxlMaConfig.Parse(ref treeBr, nodeLimit: 1024);
        tree.NumLeaves.ShouldBe(1);

        var outA = MakeChannels(dimsA, 0); // dims only; data overwritten by decode
        var brA = new JxlBitReader(dataABw.ToArray());
        JxlModularImage.Decode(ref brA, tree, outA, JxlWpHeader.Default, streamIndex: 1);
        AssertSame(subA, outA);

        var outB = MakeChannels(dimsB, 0);
        var brB = new JxlBitReader(dataBBw.ToArray());
        JxlModularImage.Decode(ref brB, tree, outB, JxlWpHeader.Default, streamIndex: 2);
        AssertSame(subB, outB);
    }

    [Fact]
    public void SharedTree_HfMetadataShape_AllZeros_RoundTrips()
    {
        // The minimal HfMetadata channels for an all-DCT8 image: x_from_y / b_from_y (lf/64),
        // block_info_raw (nb_blocks x 2), sharpness (bw x bh) — all zero (default CfL, Dct8, hf_mul 1).
        const int bw = 4, bh = 4;
        int nbBlocks = bw * bh; // every cell is a Dct8 data block
        (int, int)[] dims = [(1, 1), (1, 1), (nbBlocks, 2), (bw, bh)];
        JxlModularChannel[] sub = MakeChannels(dims, 0); // all zero data
        foreach (JxlModularChannel ch in sub)
            Array.Clear(ch.Data);

        List<(int Ctx, uint Value)> stream = JxlModularSubimage.BuildSampleStream(sub);
        JxlEntropyEncoder sampleEnc = JxlModularSubimage.NewSampleEncoder();
        JxlEntropyEncoder.Plan plan = sampleEnc.Prepare(stream);

        var treeBw = new JxlBitWriter();
        JxlModularSubimage.WriteSharedTree(treeBw, sampleEnc, plan);
        var dataBw = new JxlBitWriter();
        sampleEnc.WriteData(dataBw, plan, stream);

        var treeBr = new JxlBitReader(treeBw.ToArray());
        JxlMaConfig tree = JxlMaConfig.Parse(ref treeBr, 1024);
        var outSub = MakeChannels(dims, 0);
        var br = new JxlBitReader(dataBw.ToArray());
        JxlModularImage.Decode(ref br, tree, outSub, JxlWpHeader.Default, streamIndex: 3);

        foreach (JxlModularChannel ch in outSub)
            ch.Data.ShouldAllBe(v => v == 0);
    }
}
