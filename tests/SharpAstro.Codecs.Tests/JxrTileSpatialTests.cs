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
    public void UnsupportedBandsPresent_Throws_AllBands()
    {
        // Until LP/HP wiring lands, BANDS_PRESENT != DcOnly is rejected.
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.AllBands);
        var w = new BitWriter();
        var threw = false;
        try
        {
            TileSpatial.Write(w, headers, JxrBandsPresent.AllBands,
                trimFlexBitsFlag: false,
                JxrInternalColorFormat.YOnly, 1, 1, 1,
                new[] { new Macroblock { Dc = [0] } });
        }
        catch (NotSupportedException) { threw = true; }
        threw.ShouldBeTrue();
    }
}
