using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// End-to-end round-trip tests for the spatial-mode tile orchestrator
/// (<see cref="TileSpatial"/>). This is the first JXR test that wires
/// multiple per-MB encoders into a single bitstream and confirms that
/// the band-header + raster-loop + byte-alignment composition round-trips.
/// </summary>
public sealed class JxrTileSpatialTests
{
    [Fact]
    public void SingleMb_YOnly_DcOnly_ZeroDc_RoundTrips()
    {
        // Simplest possible tile: 1×1 MB, YOnly, DcOnly, DC=0.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var mb = new Macroblock { Dc = [0] };
        var mbs = new[] { mb };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, numComponents: 1,
            widthInMb: 1, heightInMb: 1,
            mbs);

        (w.BitPosition % 8).ShouldBe(0, "TILE_SPATIAL must end on a byte boundary");

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, numComponents: 1,
            widthInMb: 1, heightInMb: 1,
            out var readHeaders);

        readHeaders.Dc.DcUniformFlag.ShouldBeTrue();
        decoded.Length.ShouldBe(1);
        decoded[0].Dc[0].ShouldBe(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(-5)]
    [InlineData(-1000)]
    [InlineData(32767)]
    [InlineData(-32768)]
    public void SingleMb_YOnly_DcOnly_VariousDc_RoundTrips(int dc)
    {
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var mbs = new[] { new Macroblock { Dc = [dc] } };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, out _);

        decoded[0].Dc[0].ShouldBe(dc);
    }

    [Fact]
    public void HorizontalStrip_4x1_RoundTrips()
    {
        // 4 macroblocks in a row, distinct DC values per MB — exercises
        // adaptive state evolution across MBs.
        var dcValues = new[] { 10, -20, 300, -4000 };
        var mbs = new Macroblock[dcValues.Length];
        for (var i = 0; i < dcValues.Length; i++)
            mbs[i] = new Macroblock { Dc = [dcValues[i]] };

        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 4, heightInMb: 1, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 4, heightInMb: 1, out _);

        for (var i = 0; i < dcValues.Length; i++)
            decoded[i].Dc[0].ShouldBe(dcValues[i], $"mb {i}");
    }

    [Fact]
    public void VerticalStrip_1x4_RoundTrips()
    {
        var dcValues = new[] { 1, 100, -50, 12345 };
        var mbs = new Macroblock[dcValues.Length];
        for (var i = 0; i < dcValues.Length; i++)
            mbs[i] = new Macroblock { Dc = [dcValues[i]] };

        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 1, heightInMb: 4, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 1, heightInMb: 4, out _);

        for (var i = 0; i < dcValues.Length; i++)
            decoded[i].Dc[0].ShouldBe(dcValues[i]);
    }

    [Fact]
    public void Grid_3x3_RoundTrips()
    {
        // Raster-order verification: the value at (row, col) is uniquely
        // encoded so a transposition bug would surface immediately.
        var mbs = new Macroblock[9];
        for (var i = 0; i < 9; i++)
            mbs[i] = new Macroblock { Dc = [(i + 1) * 100] };

        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 3, heightInMb: 3, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 3, heightInMb: 3, out _);

        for (var i = 0; i < 9; i++)
            decoded[i].Dc[0].ShouldBe((i + 1) * 100, $"mb {i}");
    }

    [Fact]
    public void Grid_8x8_RandomDc_RoundTrips()
    {
        var rng = new Random(unchecked((int)0xDEADBEEF));
        var n = 64;
        var mbs = new Macroblock[n];
        var expected = new int[n];
        for (var i = 0; i < n; i++)
        {
            expected[i] = rng.Next(-50_000, 50_000);
            mbs[i] = new Macroblock { Dc = [expected[i]] };
        }

        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 8, heightInMb: 8, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1,
            widthInMb: 8, heightInMb: 8, out _);

        for (var i = 0; i < n; i++)
            decoded[i].Dc[0].ShouldBe(expected[i], $"mb {i}");
    }

    [Fact]
    public void EndsOnByteBoundary()
    {
        // Crucial: any structure that follows the tile expects byte alignment.
        var mbs = new[] { new Macroblock { Dc = [42] } };
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, mbs);
        (w.BitPosition % 8).ShouldBe(0);
    }

    [Fact]
    public void MismatchedMbsLength_Throws()
    {
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.DcOnly);
        var w = new BitWriter();
        var threw = false;
        try
        {
            TileSpatial.Write(w, headers, JxrBandsPresent.DcOnly,
                trimFlexBitsFlag: false,
                JxrInternalColorFormat.YOnly, 1,
                widthInMb: 2, heightInMb: 2,
                mbs: new[] { new Macroblock { Dc = [0] } }); // wrong size — needs 4
        }
        catch (ArgumentException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void AllBands_YOnly_RoundTrips_LosslessAtIModelBitsZero()
    {
        // AllBands with iModelBits = 0 (initial model state) emits no
        // FlexBits — identical bitstream content to NoFlexbits, but the
        // BandsPresent dispatch path is exercised.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.AllBands);
        var plane = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = JxrBandsPresent.AllBands,
        };
        var mb = new Macroblock
        {
            Dc = [42],
            Lp = new int[16],
            Hp = new int[256],
            MbHpMode = 2,
        };
        // Populate a couple of HP positions to exercise the VLC pass.
        mb.Hp[1 * 16 + 3] = 7;
        mb.Hp[5 * 16 + 9] = -11;

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.AllBands,
            trimFlexBitsFlag: false, plane, 1, 1, new[] { mb });

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.AllBands,
            trimFlexBitsFlag: false, plane, 1, 1, out var _);

        decoded.Length.ShouldBe(1);
        decoded[0].Dc[0].ShouldBe(42);
        for (var p = 0; p < 256; p++)
            decoded[0].Hp[p].ShouldBe(mb.Hp[p], $"pos {p}");
    }

    // ----------------------------------------------------------------------
    // BANDS_PRESENT = NoHighpass (DC + LP) tests
    // ----------------------------------------------------------------------

    [Fact]
    public void SingleMb_YOnly_NoHighpass_ZeroLp_RoundTrips()
    {
        // Simplest LP case: all LP coefficients zero. Exercises the
        // CBPLP_CH_BIT = 0 path and confirms MB_LP slots in after MB_DC.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoHighpass);
        var mb = new Macroblock { Dc = [50], Lp = new int[16] };
        var mbs = new[] { mb };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, mbs);

        (w.BitPosition % 8).ShouldBe(0);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, out _);

        decoded[0].Dc[0].ShouldBe(50);
        for (var p = 1; p < 16; p++) decoded[0].Lp[p].ShouldBe(0, $"lp[{p}]");
    }

    [Fact]
    public void SingleMb_YOnly_NoHighpass_NonZeroLp_RoundTrips()
    {
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoHighpass);
        // LP coefficients at positions 1..15; position 0 is the DC slot (ignored).
        var lp = new int[16];
        for (var p = 1; p < 16; p++) lp[p] = p * 3 - 20;
        var mb = new Macroblock { Dc = [100], Lp = lp };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, new[] { mb });

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, out _);

        decoded[0].Dc[0].ShouldBe(100);
        for (var p = 1; p < 16; p++)
            decoded[0].Lp[p].ShouldBe(p * 3 - 20, $"lp[{p}]");
    }

    [Fact]
    public void Grid_2x2_YOnly_NoHighpass_RoundTrips()
    {
        // 4 macroblocks each with distinct DC and LP — exercises both DC and
        // LP adaptive state evolution across MBs.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoHighpass);
        var mbs = new Macroblock[4];
        for (var i = 0; i < 4; i++)
        {
            var lp = new int[16];
            for (var p = 1; p < 16; p++) lp[p] = (i + 1) * (p - 8);
            mbs[i] = new Macroblock { Dc = [(i + 1) * 100], Lp = lp };
        }

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 2, 2, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 2, 2, out _);

        for (var i = 0; i < 4; i++)
        {
            decoded[i].Dc[0].ShouldBe((i + 1) * 100);
            for (var p = 1; p < 16; p++)
                decoded[i].Lp[p].ShouldBe((i + 1) * (p - 8), $"mb {i} lp[{p}]");
        }
    }

    [Fact]
    public void SingleMb_Rgb_NoHighpass_RoundTrips()
    {
        // 3-component RGB exercises the per-component CBPLP_CH_BIT path
        // with all three components active.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoHighpass);
        var lp = new int[3 * 16];
        for (var c = 0; c < 3; c++)
        for (var p = 1; p < 16; p++)
            lp[c * 16 + p] = (c + 1) * p;
        var mb = new Macroblock { Dc = new[] { 10, 20, 30 }, Lp = lp };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.Rgb, 3, 1, 1, new[] { mb });

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoHighpass,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.Rgb, 3, 1, 1, out _);

        decoded[0].Dc[0].ShouldBe(10);
        decoded[0].Dc[1].ShouldBe(20);
        decoded[0].Dc[2].ShouldBe(30);
        for (var c = 0; c < 3; c++)
        for (var p = 1; p < 16; p++)
            decoded[0].Lp[c * 16 + p].ShouldBe((c + 1) * p, $"c={c} p={p}");
    }

    [Fact]
    public void NoHighpass_MbsLpMissing_Throws()
    {
        // If caller asks for NoHighpass but forgets to populate mb.Lp,
        // validation must catch it loudly.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoHighpass);
        var mb = new Macroblock { Dc = [0] }; // Lp left empty
        var w = new BitWriter();
        var threw = false;
        try
        {
            TileSpatial.Write(w, headers, JxrBandsPresent.NoHighpass,
                trimFlexBitsFlag: false,
                JxrInternalColorFormat.YOnly, 1, 1, 1, new[] { mb });
        }
        catch (ArgumentException) { threw = true; }
        threw.ShouldBeTrue();
    }

    // ----------------------------------------------------------------------
    // BANDS_PRESENT = NoFlexbits (DC + LP + CBPHP + HP) tests
    //
    // All tests pin MbHpMode = 0 (horizontal scan). Per-MB derivation of
    // MbHpMode from LP coefficients is a separate piece of work.
    // ----------------------------------------------------------------------

    [Fact]
    public void SingleMb_YOnly_NoFlexbits_ZeroHp_RoundTrips()
    {
        // All HP blocks zero — exercises the CBPHP=0 path for every block.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoFlexbits);
        var mb = new Macroblock
        {
            Dc = [25],
            Lp = new int[16],
            Hp = new int[256], // 16 4×4 blocks × 16 coeffs
            MbHpMode = 0,
        };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, new[] { mb });

        (w.BitPosition % 8).ShouldBe(0);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, out _);

        decoded[0].Dc[0].ShouldBe(25);
        decoded[0].Hp.Length.ShouldBe(256);
        for (var i = 0; i < 256; i++) decoded[0].Hp[i].ShouldBe(0, $"hp[{i}]");
    }

    [Fact]
    public void SingleMb_YOnly_NoFlexbits_OneNonZeroBlock_RoundTrips()
    {
        // Exactly one 4×4 sub-block is non-zero — CBPHP bitmap has exactly
        // one bit set, exercising the NUM_CBPHP=1 / REF_CBPHP=u(2) path.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoFlexbits);
        var hp = new int[256];
        // Put a non-zero coefficient at block 0, position 5.
        hp[0 * 16 + 5] = 42;

        var mb = new Macroblock { Dc = [0], Lp = new int[16], Hp = hp, MbHpMode = 0 };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, new[] { mb });

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, out _);

        decoded[0].Hp[0 * 16 + 5].ShouldBe(42);
        // Everything else stays zero.
        for (var i = 0; i < 256; i++)
            if (i != 5) decoded[0].Hp[i].ShouldBe(0, $"hp[{i}]");
    }

    [Fact]
    public void SingleMb_YOnly_NoFlexbits_AllBlocksNonZero_RoundTrips()
    {
        // Every 4×4 sub-block has at least one non-zero coefficient —
        // CBPHP bitmap = 0xFFFF, exercising the NUM_CBPHP=4 (all groups set) path.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoFlexbits);
        var hp = new int[256];
        for (var blk = 0; blk < 16; blk++)
            hp[blk * 16 + 1] = blk + 1; // one non-zero coeff per block

        var mb = new Macroblock { Dc = [0], Lp = new int[16], Hp = hp, MbHpMode = 0 };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, new[] { mb });

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 1, 1, out _);

        for (var blk = 0; blk < 16; blk++)
            decoded[0].Hp[blk * 16 + 1].ShouldBe(blk + 1, $"block {blk} coeff 1");
    }

    [Fact]
    public void Grid_2x2_YOnly_NoFlexbits_RoundTrips()
    {
        // 4 MBs each with distinct DC, LP, and HP content — exercises all
        // four adaptive state machines (DC/LP/CBPHP/HP) crossing MB boundaries.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoFlexbits);
        var mbs = new Macroblock[4];
        for (var i = 0; i < 4; i++)
        {
            var lp = new int[16];
            for (var p = 1; p < 16; p++) lp[p] = (i + 1) * 2;
            var hp = new int[256];
            // Vary which blocks are non-zero per MB.
            hp[(i * 3) * 16 + 1] = (i + 1) * 10;
            mbs[i] = new Macroblock { Dc = [(i + 1) * 50], Lp = lp, Hp = hp, MbHpMode = 0 };
        }

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 2, 2, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.YOnly, 1, 2, 2, out _);

        for (var i = 0; i < 4; i++)
        {
            decoded[i].Dc[0].ShouldBe((i + 1) * 50, $"mb {i} dc");
            decoded[i].Hp[(i * 3) * 16 + 1].ShouldBe((i + 1) * 10, $"mb {i} hp");
        }
    }

    [Fact]
    public void SingleMb_Rgb_NoFlexbits_RoundTrips()
    {
        // 3-component RGB through the full DC + LP + CBPHP + HP chain.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoFlexbits);
        var lp = new int[3 * 16];
        var hp = new int[3 * 256];
        for (var c = 0; c < 3; c++)
        {
            for (var p = 1; p < 16; p++) lp[c * 16 + p] = (c + 1) * p;
            // Drop one non-zero HP coefficient per component.
            hp[c * 256 + (c * 4) * 16 + 1] = (c + 1) * 7;
        }
        var mb = new Macroblock { Dc = new[] { 5, 15, 25 }, Lp = lp, Hp = hp, MbHpMode = 0 };

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.Rgb, 3, 1, 1, new[] { mb });

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.NoFlexbits,
            trimFlexBitsFlag: false,
            JxrInternalColorFormat.Rgb, 3, 1, 1, out _);

        for (var c = 0; c < 3; c++)
        {
            decoded[0].Dc[c].ShouldBe((c + 1) * 10 - 5);
            decoded[0].Hp[c * 256 + (c * 4) * 16 + 1].ShouldBe((c + 1) * 7, $"c={c}");
        }
    }

    [Fact]
    public void NoFlexbits_MbsHpMissing_Throws()
    {
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.NoFlexbits);
        var mb = new Macroblock { Dc = [0], Lp = new int[16] }; // Hp left empty
        var w = new BitWriter();
        var threw = false;
        try
        {
            TileSpatial.Write(w, headers, JxrBandsPresent.NoFlexbits,
                trimFlexBitsFlag: false,
                JxrInternalColorFormat.YOnly, 1, 1, 1, new[] { mb });
        }
        catch (ArgumentException) { threw = true; }
        threw.ShouldBeTrue();
    }
}
