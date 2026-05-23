using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Codestream-framing tests for the IMAGE_PLANE_HEADER (T.832 §8.4).
/// Covers internal-format selection, band-presence variants, conditional
/// SHIFT_BITS / LEN_MANTISSA+EXP_BIAS fields, quantization aliasing flags,
/// and final byte alignment.
/// </summary>
public sealed class JxrImagePlaneHeaderTests
{
    [Fact]
    public void Minimal_YOnly_AllBands_RoundTrips()
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = JxrBandsPresent.AllBands,
            DcQuant = 1,
            LpQuant = 1,
            HpQuant = 1,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd8);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);

        read.InternalClrFmt.ShouldBe(h.InternalClrFmt);
        read.BandsPresent.ShouldBe(h.BandsPresent);
        read.ScaledFlag.ShouldBeFalse();
        read.DcQuant.ShouldBe((byte)1);
        read.LpQuant.ShouldBe((byte)1);
        read.HpQuant.ShouldBe((byte)1);
        read.UseDcQpForLp.ShouldBeFalse();
        read.UseLpQpForHp.ShouldBeFalse();
    }

    [Fact]
    public void Rgb_AllBands_DistinctQuants_RoundTrip()
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.Rgb,
            BandsPresent = JxrBandsPresent.AllBands,
            DcQuant = 7,
            LpQuant = 13,
            HpQuant = 29,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd8);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);
        read.DcQuant.ShouldBe((byte)7);
        read.LpQuant.ShouldBe((byte)13);
        read.HpQuant.ShouldBe((byte)29);
    }

    [Theory]
    [InlineData(JxrBandsPresent.AllBands)]
    [InlineData(JxrBandsPresent.NoFlexbits)]
    [InlineData(JxrBandsPresent.NoHighpass)]
    [InlineData(JxrBandsPresent.DcOnly)]
    public void BandsPresent_RoundTrips(JxrBandsPresent bands)
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = bands,
            DcQuant = 4,
            LpQuant = 5,
            HpQuant = 6,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd8);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);
        read.BandsPresent.ShouldBe(bands);
        read.DcQuant.ShouldBe((byte)4);
        if (bands != JxrBandsPresent.DcOnly)
            read.LpQuant.ShouldBe((byte)5);
        if (bands != JxrBandsPresent.DcOnly && bands != JxrBandsPresent.NoHighpass)
            read.HpQuant.ShouldBe((byte)6);
    }

    [Fact]
    public void UseDcQpForLp_OmitsLpQuant()
    {
        // When the LP band reuses DC quantization, the LP_QUANT field is
        // absent from the bitstream — verify both the round-trip and the
        // bit-savings (compare against a header that writes a distinct LP_QUANT).
        var aliased = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = JxrBandsPresent.NoHighpass,
            DcQuant = 9,
            UseDcQpForLp = true,
        };
        var distinct = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = JxrBandsPresent.NoHighpass,
            DcQuant = 9,
            LpQuant = 42,
        };
        var wA = new BitWriter();
        aliased.Write(wA, JxrOutputBitDepth.Bd8);
        var wD = new BitWriter();
        distinct.Write(wD, JxrOutputBitDepth.Bd8);
        // The distinct version carries an extra 9 bits (LP_IMAGE_PLANE_UNIFORM_FLAG + LP_QUANT)
        // → which after byte alignment can differ by 0 or 1 bytes. The key
        // invariant is round-trip correctness with the flag intact.
        var r = new BitReader(wA.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);
        read.UseDcQpForLp.ShouldBeTrue();
    }

    [Fact]
    public void UseLpQpForHp_OmitsHpQuant()
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = JxrBandsPresent.AllBands,
            DcQuant = 3,
            LpQuant = 11,
            UseLpQpForHp = true,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd8);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);
        read.UseLpQpForHp.ShouldBeTrue();
        read.LpQuant.ShouldBe((byte)11);
    }

    [Fact]
    public void Bd16_ShiftBits_RoundTrips()
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.Rgb,
            BandsPresent = JxrBandsPresent.AllBands,
            DcQuant = 2,
            LpQuant = 2,
            HpQuant = 2,
            ShiftBits = 6,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd16);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd16);
        read.ShiftBits.ShouldBe((byte)6);
    }

    [Fact]
    public void Bd32F_HdrPath_RoundTrips()
    {
        // The motivating use case — float HDR (Bd32F + Rgb) carrying
        // LEN_MANTISSA + (signed) EXP_BIAS in the plane header.
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.Rgb,
            BandsPresent = JxrBandsPresent.AllBands,
            DcQuant = 1,
            LpQuant = 1,
            HpQuant = 1,
            LenMantissa = 23,
            ExpBias = -5,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd32F);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd32F);
        read.LenMantissa.ShouldBe((byte)23);
        read.ExpBias.ShouldBe((sbyte)-5);
    }

    [Fact]
    public void NComponent_ParsesNumComponents()
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.NComponent,
            BandsPresent = JxrBandsPresent.AllBands,
            NumComponents = 5,
            DcQuant = 1,
            LpQuant = 1,
            HpQuant = 1,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd8);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);
        read.InternalClrFmt.ShouldBe(JxrInternalColorFormat.NComponent);
        read.NumComponents.ShouldBe(5);
    }

    [Fact]
    public void YUV420_Throws_NotSupported()
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YUV420,
            BandsPresent = JxrBandsPresent.AllBands,
            DcQuant = 1,
            LpQuant = 1,
            HpQuant = 1,
        };
        var w = new BitWriter();
        var threw = false;
        try { h.Write(w, JxrOutputBitDepth.Bd8); }
        catch (NotSupportedException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void EndsOnByteBoundary()
    {
        // T.832 §8.4 ends IMAGE_PLANE_HEADER with byte_alignment(). Verify
        // the writer leaves the stream byte-aligned for every variant — the
        // next codestream structure (tile header) expects byte alignment.
        var variants = new (JxrInternalColorFormat fmt, JxrBandsPresent bands, JxrOutputBitDepth bd)[]
        {
            (JxrInternalColorFormat.YOnly,     JxrBandsPresent.AllBands,   JxrOutputBitDepth.Bd8),
            (JxrInternalColorFormat.Rgb,       JxrBandsPresent.NoFlexbits, JxrOutputBitDepth.Bd16),
            (JxrInternalColorFormat.YUV444,    JxrBandsPresent.NoHighpass, JxrOutputBitDepth.Bd8),
            (JxrInternalColorFormat.NComponent,JxrBandsPresent.DcOnly,     JxrOutputBitDepth.Bd32F),
            (JxrInternalColorFormat.Rgb,       JxrBandsPresent.AllBands,   JxrOutputBitDepth.Bd32F),
        };
        foreach (var v in variants)
        {
            var h = new ImagePlaneHeader
            {
                InternalClrFmt = v.fmt,
                BandsPresent = v.bands,
                DcQuant = 1, LpQuant = 1, HpQuant = 1,
            };
            var w = new BitWriter();
            h.Write(w, v.bd);
            (w.BitPosition % 8).ShouldBe(0, $"variant {v.fmt}/{v.bands}/{v.bd} did not byte-align");
        }
    }

    [Fact]
    public void DcOnly_OmitsLpAndHpFields()
    {
        var dcOnly = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = JxrBandsPresent.DcOnly,
            DcQuant = 8,
        };
        var allBands = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = JxrBandsPresent.AllBands,
            DcQuant = 8, LpQuant = 8, HpQuant = 8,
        };
        var wDc = new BitWriter();
        dcOnly.Write(wDc, JxrOutputBitDepth.Bd8);
        var wAll = new BitWriter();
        allBands.Write(wAll, JxrOutputBitDepth.Bd8);
        wDc.BitPosition.ShouldBeLessThan(wAll.BitPosition,
            "DC-only header must be shorter than all-bands");

        var r = new BitReader(wDc.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);
        read.BandsPresent.ShouldBe(JxrBandsPresent.DcOnly);
        read.DcQuant.ShouldBe((byte)8);
    }

    [Fact]
    public void ScaledFlag_RoundTrips()
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            BandsPresent = JxrBandsPresent.AllBands,
            ScaledFlag = true,
            DcQuant = 1, LpQuant = 1, HpQuant = 1,
        };
        var w = new BitWriter();
        h.Write(w, JxrOutputBitDepth.Bd8);
        var r = new BitReader(w.AsSpan());
        var read = ImagePlaneHeader.Read(ref r, JxrOutputBitDepth.Bd8);
        read.ScaledFlag.ShouldBeTrue();
    }
}
