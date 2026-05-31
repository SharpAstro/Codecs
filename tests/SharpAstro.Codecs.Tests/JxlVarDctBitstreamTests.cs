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
}
