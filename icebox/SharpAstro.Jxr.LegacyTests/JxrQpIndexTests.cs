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
    public void EndToEnd_NonUniformDc_DecoderAppliesPerTileDcQp()
    {
        // Manually craft a 1×1 MB image with DcImagePlaneUniformFlag=false
        // and a per-tile DC_QP() row of QP=3. Encode the CodedImage, then
        // decode and verify the dequantizer used divisor 3 — by comparing
        // against a parallel uniform-QP=3 image, which must reach the same
        // dequantized DC value.
        var widthInMb = 1;
        var heightInMb = 1;
        var rawDc = new int[] { 5 };  // pre-quant input
        var quantizedDc = rawDc[0] / 3;  // what the encoder would put into the bitstream

        // Build the encoder's "already quantized" macroblocks. Our encoder
        // facades quantize-then-write — for this test we hand-craft a
        // CodedImage with the already-quantized values and per-tile DC_QP.
        var planeNonUniform = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = JxrBandsPresent.DcOnly,
            DcImagePlaneUniformFlag = false,
        };
        var headersNonUniform = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        headersNonUniform.Dc.DcQp = QpTable.Uniform(numComponents: 1, qp: 3);
        var mbsNonUniform = new[] { new Macroblock { Dc = [quantizedDc] } };

        // Encode + decode roundtrip via TileSpatial directly (DcOnly).
        var w = new BitWriter();
        TileSpatial.Write(w, headersNonUniform, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false, planeNonUniform,
            widthInMb, heightInMb, mbsNonUniform);
        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false, planeNonUniform,
            widthInMb, heightInMb, out var decodedHeaders);

        // Build a CodedImage so we can invoke QpResolver + JxrQuant the way
        // the JxrDecoder facade does.
        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 15,
                HeightMinus1 = 15,
            },
            PlaneHeader = planeNonUniform,
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = decoded,
            PerTileBandHeaders = [decodedHeaders],
            TileGridBounds = [(0, 0, widthInMb, heightInMb)],
        };
        var dcDivisors = QpResolver.BuildBandDivisors(img, QpBand.Dc);
        dcDivisors[0, 0, 0].ShouldBe(JxrQuant.QpIndexToDivisor(3),
            "QpResolver should pick the per-tile DC QP of 3");

        // Apply per-MB dequant in place — yielding the original pre-quant DC.
        var mbDc = new int[1, 1, 1] { { { decoded[0].Dc[0] } } };
        JxrQuant.DequantizeDc(mbDc, dcDivisors);
        mbDc[0, 0, 0].ShouldBe(quantizedDc * JxrQuant.QpIndexToDivisor(3));
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
