using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Tests for the TILE_HEADER_{DC,LOWPASS,HIGHPASS} trio (T.832 §8.6.2–4)
/// and the <see cref="TileBandHeaders"/> composite that enforces
/// conditional emission based on <see cref="JxrBandsPresent"/>.
/// </summary>
public sealed class JxrTileBandHeadersTests
{
    [Fact]
    public void DcUniform_SingleBit()
    {
        var h = new TileHeaderDc { DcUniformFlag = true };
        var w = new BitWriter();
        h.Write(w);
        w.BitPosition.ShouldBe(1);
        var r = new BitReader(w.AsSpan());
        var read = TileHeaderDc.Read(ref r);
        read.DcUniformFlag.ShouldBeTrue();
    }

    [Fact]
    public void LpUniform_SingleBit()
    {
        var h = new TileHeaderLowpass { LpUniformFlag = true };
        var w = new BitWriter();
        h.Write(w);
        w.BitPosition.ShouldBe(1);
        var r = new BitReader(w.AsSpan());
        var read = TileHeaderLowpass.Read(ref r);
        read.LpUniformFlag.ShouldBeTrue();
    }

    [Fact]
    public void HpUniform_WithoutTrim_SingleBit()
    {
        var h = new TileHeaderHighpass { HpUniformFlag = true };
        var w = new BitWriter();
        h.Write(w, trimFlexBitsFlag: false);
        w.BitPosition.ShouldBe(1);
        var r = new BitReader(w.AsSpan());
        var read = TileHeaderHighpass.Read(ref r, trimFlexBitsFlag: false);
        read.HpUniformFlag.ShouldBeTrue();
        read.TrimFlexBits.ShouldBe(0);
    }

    [Fact]
    public void HpUniform_WithTrim_RoundTrips()
    {
        var h = new TileHeaderHighpass { TrimFlexBits = 7, HpUniformFlag = true };
        var w = new BitWriter();
        h.Write(w, trimFlexBitsFlag: true);
        w.BitPosition.ShouldBe(5, "4 bits trim + 1 bit uniform flag");
        var r = new BitReader(w.AsSpan());
        var read = TileHeaderHighpass.Read(ref r, trimFlexBitsFlag: true);
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
        var h = new TileHeaderHighpass { TrimFlexBits = value, HpUniformFlag = true };
        var w = new BitWriter();
        h.Write(w, trimFlexBitsFlag: true);
        var r = new BitReader(w.AsSpan());
        var read = TileHeaderHighpass.Read(ref r, trimFlexBitsFlag: true);
        read.TrimFlexBits.ShouldBe(value);
    }

    [Fact]
    public void TrimFlexBits_OutOfRange_Throws()
    {
        var h = new TileHeaderHighpass { TrimFlexBits = 16, HpUniformFlag = true };
        var w = new BitWriter();
        var threw = false;
        try { h.Write(w, trimFlexBitsFlag: true); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Theory]
    [InlineData(JxrBandsPresent.AllBands,   1 + 1 + 1)]
    [InlineData(JxrBandsPresent.NoFlexbits, 1 + 1 + 1)]
    [InlineData(JxrBandsPresent.NoHighpass, 1 + 1)]
    [InlineData(JxrBandsPresent.DcOnly,     1)]
    public void TileBandHeaders_ConditionalEmission(JxrBandsPresent bands, int expectedBits)
    {
        // Verifies which sub-headers actually appear in the bitstream.
        // trimFlexBitsFlag is false here so HP header contributes exactly 1 bit.
        var trio = TileBandHeaders.Uniform(bands);
        var w = new BitWriter();
        trio.Write(w, bands, trimFlexBitsFlag: false);
        w.BitPosition.ShouldBe(expectedBits);

        var r = new BitReader(w.AsSpan());
        var read = TileBandHeaders.Read(ref r, bands, trimFlexBitsFlag: false);
        read.Dc.DcUniformFlag.ShouldBeTrue();
        (read.Lowpass is not null).ShouldBe(bands != JxrBandsPresent.DcOnly);
        (read.Highpass is not null).ShouldBe(bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass);
    }

    [Fact]
    public void TileBandHeaders_AllBands_WithTrimFlexbits_RoundTrips()
    {
        var trio = new TileBandHeaders
        {
            Dc = new TileHeaderDc(),
            Lowpass = new TileHeaderLowpass(),
            Highpass = new TileHeaderHighpass { TrimFlexBits = 3 },
        };
        var w = new BitWriter();
        trio.Write(w, JxrBandsPresent.AllBands, trimFlexBitsFlag: true);
        w.BitPosition.ShouldBe(1 + 1 + 4 + 1, "DC + LP + TRIM + HP uniform flag");

        var r = new BitReader(w.AsSpan());
        var read = TileBandHeaders.Read(ref r, JxrBandsPresent.AllBands, trimFlexBitsFlag: true);
        read.Highpass.ShouldNotBeNull();
        read.Highpass!.TrimFlexBits.ShouldBe(3);
    }

    [Fact]
    public void TileBandHeaders_NoHighpass_NoTrimFlexbits()
    {
        // When BandsPresent is NoHighpass, the HP header is skipped entirely —
        // TRIM_FLEXBITS is therefore not in the bitstream even if the IMAGE_HEADER
        // had TRIM_FLEX_BITS_FLAG = true.
        var trio = TileBandHeaders.Uniform(JxrBandsPresent.NoHighpass);
        var w = new BitWriter();
        trio.Write(w, JxrBandsPresent.NoHighpass, trimFlexBitsFlag: true);
        w.BitPosition.ShouldBe(2, "DC + LP uniform flags only");
    }

    [Fact]
    public void Uniform_FactoryMatchesBandsPresent()
    {
        var dcOnly = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        dcOnly.Lowpass.ShouldBeNull();
        dcOnly.Highpass.ShouldBeNull();

        var allBands = TileBandHeaders.Uniform(JxrBandsPresent.AllBands);
        allBands.Lowpass.ShouldNotBeNull();
        allBands.Highpass.ShouldNotBeNull();

        var noHp = TileBandHeaders.Uniform(JxrBandsPresent.NoHighpass);
        noHp.Lowpass.ShouldNotBeNull();
        noHp.Highpass.ShouldBeNull();
    }

    [Fact]
    public void TileBandHeaders_MissingSubHeader_Throws()
    {
        // If a caller declares AllBands but forgets to provide LP/HP,
        // the writer must fail loudly rather than silently emitting a
        // malformed bitstream.
        var trio = new TileBandHeaders { Dc = new TileHeaderDc() };
        var w = new BitWriter();
        var threw = false;
        try { trio.Write(w, JxrBandsPresent.AllBands, trimFlexBitsFlag: false); }
        catch (InvalidOperationException) { threw = true; }
        threw.ShouldBeTrue();
    }
}
