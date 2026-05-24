using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase 20b: per-MB LP_QP_INDEX / HP_QP_INDEX round-trip tests.
/// </summary>
public sealed class JxrQpIndexTests
{
    [Theory]
    [InlineData(2)]   // 1-bit width
    [InlineData(3)]   // 1-bit width too (per Table 47)
    [InlineData(4)]   // 2-bit
    [InlineData(8)]   // 3-bit
    [InlineData(16)]  // 4-bit
    public void QpIndex_AllValuesRoundTrip(int numQPs)
    {
        // Exhaustively verify every value in [0, numQPs) round-trips via Write+Read.
        for (var idx = 0; idx < numQPs; idx++)
        {
            var w = new BitWriter();
            QpIndex.Write(w, numQPs, idx);
            var r = new BitReader(w.AsSpan());
            QpIndex.Read(ref r, numQPs).ShouldBe(idx, $"numQPs={numQPs} idx={idx}");
        }
    }

    [Fact]
    public void QpIndex_NumQPsOne_EmitsNoBits()
    {
        var w = new BitWriter();
        QpIndex.Write(w, numQPs: 1, qpIndex: 0);
        w.BitPosition.ShouldBe(0, "NumQPs=1 has no QP_INDEX bits");

        // Read side also reads nothing — no bits to consume.
        var r = new BitReader(new byte[1]);
        QpIndex.Read(ref r, numQPs: 1).ShouldBe(0);
        r.BitPosition.ShouldBe(0);
    }

    [Fact]
    public void QpIndex_NonZeroFlagThenOptionalRef()
    {
        // Value 0 emits exactly 1 bit (the IS_QPINDEX_NONZERO_FLAG=0). Higher
        // values: 1 bit flag + iBits.
        var w0 = new BitWriter();
        QpIndex.Write(w0, numQPs: 4, qpIndex: 0);
        w0.BitPosition.ShouldBe(1);

        var w1 = new BitWriter();
        QpIndex.Write(w1, numQPs: 4, qpIndex: 1);
        // numQPs=4 → iBits=2 (Table 47), so 1 (flag) + 2 (ref) = 3 bits.
        w1.BitPosition.ShouldBe(3);
    }

    [Fact]
    public void TileSpatial_PerMbLpQpIndex_RoundTrips()
    {
        // Build a 1×1-MB tile with 3 LP QP rows so each MB carries an LP_QP_INDEX.
        var lpQp = new QpTable
        {
            NumQPs = 3,
            NumComponents = 1,
            ComponentModes = [QpComponentMode.Uniform, QpComponentMode.Uniform, QpComponentMode.Uniform],
            Qps = new byte[3, 1] { { 5 }, { 11 }, { 23 } },
        };
        var headers = new TileBandHeaders
        {
            Dc = new TileHeaderDc(),
            Lowpass = new TileHeaderLowpass { LpQp = lpQp },
        };
        var plane = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = JxrBandsPresent.NoHighpass,
            DcImagePlaneUniformFlag = false,    // forces tile-level DC_QP() emission
            LpImagePlaneUniformFlag = false,    // forces tile-level LP_QP block
        };
        // DC needs a per-tile DC_QP too (since plane non-uniform):
        headers.Dc.DcQp = QpTable.Uniform(numComponents: 1, qp: 1);

        // Single MB picks LP_QP_INDEX = 2 (the third row → QP=23).
        var mb = new Macroblock
        {
            Dc = [0],
            Lp = new int[16],
            LpQpIndex = 2,
        };
        var mbs = new[] { mb };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false, plane,
            widthInMb: 1, heightInMb: 1, mbs);

        var r = new BitReader(w.AsSpan());
        var decodedMbs = TileSpatial.Read(ref r, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false, plane,
            widthInMb: 1, heightInMb: 1, out var decodedHeaders);

        decodedHeaders.Lowpass.ShouldNotBeNull();
        decodedHeaders.Lowpass!.LpQp.ShouldNotBeNull();
        decodedHeaders.Lowpass.LpQp!.NumQPs.ShouldBe(3);
        decodedMbs.Length.ShouldBe(1);
        decodedMbs[0].LpQpIndex.ShouldBe(2);
    }
}
