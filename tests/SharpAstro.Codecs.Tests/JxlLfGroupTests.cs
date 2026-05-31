using System.Linq;
using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// VarDCT 5l — the LfGroup section (LfCoeff + HfMetadata) round-trip. Builds the quantized LF-DC
/// channels, a varblock grid, and the chroma-from-luma grids; writes the shared global tree once and
/// the section's two Modular sub-images (each its own data blob); then decodes the section back via
/// JxlLfGroup.Read and checks every part survives, including the block grid reconstructed by
/// JxlBlockLayout. End-to-end validation against libjxl is at 5m (full frame).
/// </summary>
public sealed class JxlLfGroupTests
{
    private static int[][] MakeLfQuant(int bw, int bh)
    {
        var lf = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            lf[c] = new int[bw * bh];
            for (int i = 0; i < lf[c].Length; i++)
                lf[c][i] = (c * 37 + i * 11) % 41 - 20; // -20..20
        }
        return lf;
    }

    private static JxlLfGroup RoundTrip(JxlLfGroup lf, uint lfStream, uint hfStream)
    {
        (List<(int Ctx, uint Value)> lfS, List<(int Ctx, uint Value)> hfS) = lf.BuildStreams();
        JxlEntropyEncoder enc = JxlModularSubimage.NewSampleEncoder();
        JxlEntropyEncoder.Plan plan = enc.Prepare(lfS, hfS);

        var treeBw = new JxlBitWriter();
        JxlModularSubimage.WriteSharedTree(treeBw, enc, plan);
        var secBw = new JxlBitWriter();
        lf.Write(secBw, enc, plan, lfS, hfS);

        var treeBr = new JxlBitReader(treeBw.ToArray());
        JxlMaConfig tree = JxlMaConfig.Parse(ref treeBr, 1024);
        var secBr = new JxlBitReader(secBw.ToArray());
        return JxlLfGroup.Read(ref secBr, tree, lf.Bw, lf.Bh, lf.CfW, lf.CfH, lfStream, hfStream);
    }

    [Fact]
    public void LfGroup_AllDct8_RoundTrips()
    {
        const int bw = 4, bh = 4, cfW = 1, cfH = 1;
        var grid = new JxlBlockInfo[bw * bh];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = JxlBlockInfo.Data(JxlVarDctTransform.Dct8, hfMul: i % 3 + 1);
        int[][] lfQuant = MakeLfQuant(bw, bh);

        var lf = new JxlLfGroup
        {
            Bw = bw, Bh = bh, ExtraPrecision = 2, LfQuant = lfQuant, BlockGrid = grid,
            CfW = cfW, CfH = cfH, XFromY = [3], BFromY = [-2],
        };

        JxlLfGroup rt = RoundTrip(lf, lfStream: 1, hfStream: 9);

        rt.ExtraPrecision.ShouldBe(2);
        for (int c = 0; c < 3; c++)
            rt.LfQuant[c].ShouldBe(lfQuant[c], $"lf channel {c}");
        rt.XFromY.ShouldBe([3]);
        rt.BFromY.ShouldBe([-2]);
        for (int i = 0; i < grid.Length; i++)
        {
            rt.BlockGrid[i].State.ShouldBe(JxlBlockInfo.BlockState.Data);
            rt.BlockGrid[i].DctSelect.ShouldBe(JxlVarDctTransform.Dct8);
            rt.BlockGrid[i].HfMul.ShouldBe(i % 3 + 1);
        }
    }

    [Fact]
    public void LfGroup_MixedBlocks_RoundTrips()
    {
        const int bw = 4, bh = 4, cfW = 1, cfH = 1;
        // One Dct16 at (0,0) then Dct8 filling the rest: 1 + 12 = 13 data blocks.
        var dctRaw = new List<int> { (int)JxlVarDctTransform.Dct16 };
        dctRaw.AddRange(Enumerable.Repeat((int)JxlVarDctTransform.Dct8, 12));
        int[] mulRaw = Enumerable.Range(0, 13).Select(i => i % 4).ToArray();
        JxlBlockInfo[] grid = JxlBlockLayout.Decode(dctRaw.ToArray(), mulRaw, bw, bh);

        var lf = new JxlLfGroup
        {
            Bw = bw, Bh = bh, ExtraPrecision = 1, LfQuant = MakeLfQuant(bw, bh), BlockGrid = grid,
            CfW = cfW, CfH = cfH, XFromY = [0], BFromY = [0],
        };

        JxlLfGroup rt = RoundTrip(lf, lfStream: 1, hfStream: 9);

        rt.ExtraPrecision.ShouldBe(1);
        // The reconstructed grid must re-encode to the same raw (dct_select, hf_mul-1) pairs.
        (int[] d2, int[] m2) = JxlBlockLayout.Encode(rt.BlockGrid, bw, bh);
        d2.ShouldBe(dctRaw.ToArray());
        m2.ShouldBe(mulRaw);
    }
}
