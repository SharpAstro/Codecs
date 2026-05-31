using System.Linq;
using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// VarDCT bitstream-section tests (Rung 5, the phase after the math foundation). These validate the
/// header readers/writers by self round-trip; full-image validation against libjxl/Magick comes once
/// the LfGroup / HfGlobal / PassGroup sections compose into a decodable frame.
/// </summary>
public sealed class JxlVarDctBitstreamTests
{
    [Theory]
    [InlineData(1, 16)]
    [InlineData(4096, 32)]
    [InlineData(8193, 1)]
    [InlineData(65535, 65536)]
    public void LfGlobalVarDct_Header_RoundTrips(int globalScale, int quantLf)
    {
        var src = new JxlLfGlobalVarDct { GlobalScale = globalScale, QuantLf = quantLf };

        var bw = new JxlBitWriter();
        src.Write(bw);
        byte[] bytes = bw.ToArray();

        var br = new JxlBitReader(bytes);
        JxlLfGlobalVarDct rt = JxlLfGlobalVarDct.Read(ref br);

        rt.GlobalScale.ShouldBe(globalScale);
        rt.QuantLf.ShouldBe(quantLf);
        // Default HfBlockContext + LfChannelCorrelation.
        rt.NumBlockClusters.ShouldBe(15);
        rt.BlockCtxMap.Length.ShouldBe(39);
        rt.QfThresholds.ShouldBeEmpty();
        rt.ColourFactor.ShouldBe(84);
        rt.BaseCorrelationX.ShouldBe(0f);
        rt.BaseCorrelationB.ShouldBe(1f);
    }

    [Theory]
    [InlineData(0, 8, 8)]   // Dct8: 64 single-cell blocks
    [InlineData(4, 4, 4)]   // Dct16: 4 blocks, 2x2 cells each
    [InlineData(4, 8, 8)]   // Dct16: 16 blocks
    [InlineData(5, 4, 4)]   // Dct32: 1 block, 4x4 cells
    public void BlockLayout_Uniform_RoundTrips(int transformOrdinal, int bw, int bh)
    {
        var t = (JxlVarDctTransform)transformOrdinal;
        (int dw, int dh) = t.DctSelectSize();
        int count = (bw / dw) * (bh / dh);
        var dctRaw = new int[count];
        var mulRaw = new int[count];
        for (int i = 0; i < count; i++) { dctRaw[i] = (int)t; mulRaw[i] = i % 5; }

        JxlBlockInfo[] grid = JxlBlockLayout.Decode(dctRaw, mulRaw, bw, bh);

        grid.Count(g => g.State == JxlBlockInfo.BlockState.Data).ShouldBe(count);
        grid.All(g => g.IsOccupied).ShouldBeTrue(); // fully tiled, no gaps

        (int[] dct2, int[] mul2) = JxlBlockLayout.Encode(grid, bw, bh);
        dct2.ShouldBe(dctRaw);
        mul2.ShouldBe(mulRaw);
    }

    [Fact]
    public void BlockLayout_Mixed_RoundTrips()
    {
        // One Dct16 at (0,0) then Dct8 filling the rest of a 4x4 LF-group grid: 1 + 12 = 13 blocks.
        const int bw = 4, bh = 4;
        var dctRaw = new List<int> { (int)JxlVarDctTransform.Dct16 };
        dctRaw.AddRange(Enumerable.Repeat((int)JxlVarDctTransform.Dct8, 12));
        int[] mulRaw = Enumerable.Range(0, 13).Select(i => i % 7).ToArray();

        JxlBlockInfo[] grid = JxlBlockLayout.Decode(dctRaw.ToArray(), mulRaw, bw, bh);

        grid.All(g => g.IsOccupied).ShouldBeTrue();
        grid.Count(g => g.State == JxlBlockInfo.BlockState.Data).ShouldBe(13);

        (int[] dct2, int[] mul2) = JxlBlockLayout.Encode(grid, bw, bh);
        dct2.ShouldBe(dctRaw.ToArray());
        mul2.ShouldBe(mulRaw);
    }

    [Fact]
    public void BlockLayout_VarblockExceedingGrid_Throws()
    {
        // A Dct16 (2x2 cells) at the only cell of a 1x1 grid cannot fit.
        Should.Throw<InvalidDataException>(() =>
            JxlBlockLayout.Decode(new[] { (int)JxlVarDctTransform.Dct16 }, new[] { 0 }, 1, 1));
    }
}
