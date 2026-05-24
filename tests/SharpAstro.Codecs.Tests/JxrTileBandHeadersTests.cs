using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Per-band tile-header round-trip tests. Phase 20 made the headers
/// spec-correct: T.832 says the per-band tile header is empty when the
/// enclosing IMAGE_PLANE_HEADER set the matching uniform flag. The
/// legacy 1-bit DcUniformFlag/LpUniformFlag/HpUniformFlag we used to emit
/// was non-conformant; the new headers emit zero bits in the uniform
/// case (the common one) and a DC_QP / LP_QP / HP_QP block otherwise.
/// </summary>
public sealed class JxrTileBandHeadersTests
{
    [Fact]
    public void DcUniform_EmitsZeroBits()
    {
        var h = new TileHeaderDc();
        var w = new BitWriter();
        h.Write(w, planeUniform: true, numComponents: 1);
        w.BitPosition.ShouldBe(0, "T.832 Table 41: tile-header empty when plane uniform");

        var r = new BitReader(w.AsSpan());
        var read = TileHeaderDc.Read(ref r, planeUniform: true, numComponents: 1);
        read.DcQp.ShouldBeNull();
        read.DcUniformFlag.ShouldBeTrue();
    }

    [Fact]
    public void LpUniform_EmitsZeroBits()
    {
        var h = new TileHeaderLowpass();
        var w = new BitWriter();
        h.Write(w, planeUniform: true, numComponents: 1);
        w.BitPosition.ShouldBe(0);

        var r = new BitReader(w.AsSpan());
        var read = TileHeaderLowpass.Read(ref r, planeUniform: true, numComponents: 1);
        read.LpQp.ShouldBeNull();
        read.LpUniformFlag.ShouldBeTrue();
    }

    [Fact]
    public void HpUniform_WithoutTrim_EmitsZeroBits()
    {
        var h = new TileHeaderHighpass();
        var w = new BitWriter();
        h.Write(w, trimFlexBitsFlag: false, planeUniform: true, numComponents: 1);
        w.BitPosition.ShouldBe(0);

        var r = new BitReader(w.AsSpan());
        var read = TileHeaderHighpass.Read(ref r, trimFlexBitsFlag: false, planeUniform: true, numComponents: 1);
        read.HpQp.ShouldBeNull();
        read.HpUniformFlag.ShouldBeTrue();
        read.TrimFlexBits.ShouldBe(0);
    }

    [Fact]
    public void HpUniform_WithTrim_RoundTrips()
    {
        var h = new TileHeaderHighpass { TrimFlexBits = 7 };
        var w = new BitWriter();
        h.Write(w, trimFlexBitsFlag: true, planeUniform: true, numComponents: 1);
        w.BitPosition.ShouldBe(4, "4 bits trim only — no plane-non-uniform bits");

        var r = new BitReader(w.AsSpan());
        var read = TileHeaderHighpass.Read(ref r, trimFlexBitsFlag: true, planeUniform: true, numComponents: 1);
        read.TrimFlexBits.ShouldBe(7);
        read.HpUniformFlag.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(15)]
    public void TrimFlexBits_AllValuesRoundTrip(int value)
    {
        var h = new TileHeaderHighpass { TrimFlexBits = value };
        var w = new BitWriter();
        h.Write(w, trimFlexBitsFlag: true, planeUniform: true, numComponents: 1);
        var r = new BitReader(w.AsSpan());
        var read = TileHeaderHighpass.Read(ref r, trimFlexBitsFlag: true, planeUniform: true, numComponents: 1);
        read.TrimFlexBits.ShouldBe(value);
    }

    [Fact]
    public void TrimFlexBits_OutOfRange_Throws()
    {
        var h = new TileHeaderHighpass { TrimFlexBits = 16 };
        var w = new BitWriter();
        var threw = false;
        try { h.Write(w, trimFlexBitsFlag: true, planeUniform: true, numComponents: 1); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Theory]
    // All bits-counts assume planeUniform=true (default): each per-band
    // tile-header contributes 0 bits per T.832 §8.6.x.
    [InlineData(JxrBandsPresent.AllBands,   0)]
    [InlineData(JxrBandsPresent.NoFlexbits, 0)]
    [InlineData(JxrBandsPresent.NoHighpass, 0)]
    [InlineData(JxrBandsPresent.DcOnly,     0)]
    public void TileBandHeaders_AllUniform_EmitZeroBits(JxrBandsPresent bands, int expectedBits)
    {
        var trio = TileBandHeaders.Uniform(bands);
        var plane = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = bands,
        };
        var w = new BitWriter();
        trio.Write(w, bands, trimFlexBitsFlag: false, plane);
        w.BitPosition.ShouldBe(expectedBits);

        var r = new BitReader(w.AsSpan());
        var read = TileBandHeaders.Read(ref r, bands, trimFlexBitsFlag: false, plane);
        read.Dc.DcUniformFlag.ShouldBeTrue();
        (read.Lowpass is not null).ShouldBe(bands != JxrBandsPresent.DcOnly);
        (read.Highpass is not null).ShouldBe(bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass);
    }

    [Fact]
    public void DcNonUniform_EmitsDcQpBlock()
    {
        // When the plane is non-uniform, the DC tile-header carries a single
        // QP row. For a 1-component image (YOnly) the COMPONENT_MODE bits are
        // suppressed — just 8 bits of QP.
        var h = new TileHeaderDc
        {
            DcQp = QpTable.Uniform(numComponents: 1, qp: 42),
        };
        var w = new BitWriter();
        h.Write(w, planeUniform: false, numComponents: 1);
        w.BitPosition.ShouldBe(8, "1-component: just 8 bits of QP");

        var r = new BitReader(w.AsSpan());
        var read = TileHeaderDc.Read(ref r, planeUniform: false, numComponents: 1);
        read.DcQp.ShouldNotBeNull();
        read.DcQp![0, 0].ShouldBe((byte)42);
    }

    [Fact]
    public void DcNonUniform_Rgb_EmitsComponentModeAndQp()
    {
        // 3-component: 2 bits COMPONENT_MODE + 8 bits QP (uniform mode).
        var h = new TileHeaderDc { DcQp = QpTable.Uniform(numComponents: 3, qp: 13) };
        var w = new BitWriter();
        h.Write(w, planeUniform: false, numComponents: 3);
        w.BitPosition.ShouldBe(2 + 8);

        var r = new BitReader(w.AsSpan());
        var read = TileHeaderDc.Read(ref r, planeUniform: false, numComponents: 3);
        read.DcQp.ShouldNotBeNull();
        for (var c = 0; c < 3; c++) read.DcQp![0, c].ShouldBe((byte)13);
    }
}
