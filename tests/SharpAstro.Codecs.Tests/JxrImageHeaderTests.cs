using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Codestream-framing tests for the IMAGE_HEADER (T.832 §8.3). Verifies
/// the bitstream layout of the GDI_SIGNATURE and flag fields, plus the
/// dimension-encoding split between SHORT_HEADER and long-header modes.
/// </summary>
public sealed class JxrImageHeaderTests
{
    [Fact]
    public void GdiSignature_IsWmphotoNull()
    {
        // T.832 8.3.2 — 64-bit signature 0x574D50484F544F00 == "WMPHOTO\0".
        var h = new ImageHeader
        {
            OutputClrFmt = JxrOutputColorFormat.YOnly,
            OutputBitDepth = JxrOutputBitDepth.Bd8,
            ShortHeaderFlag = true,
            WidthMinus1 = 15,
            HeightMinus1 = 15,
        };
        var w = new BitWriter();
        h.Write(w);
        var bytes = w.AsSpan();

        bytes[0].ShouldBe((byte)'W');
        bytes[1].ShouldBe((byte)'M');
        bytes[2].ShouldBe((byte)'P');
        bytes[3].ShouldBe((byte)'H');
        bytes[4].ShouldBe((byte)'O');
        bytes[5].ShouldBe((byte)'T');
        bytes[6].ShouldBe((byte)'O');
        bytes[7].ShouldBe((byte)0);
    }

    [Fact]
    public void Minimal_ShortHeader_RoundTrips()
    {
        var h = new ImageHeader
        {
            OutputClrFmt = JxrOutputColorFormat.YOnly,
            OutputBitDepth = JxrOutputBitDepth.Bd8,
            ShortHeaderFlag = true,
            WidthMinus1 = 15,
            HeightMinus1 = 15,
        };
        var w = new BitWriter();
        h.Write(w);
        var r = new BitReader(w.AsSpan());
        var read = ImageHeader.Read(ref r);

        read.OutputClrFmt.ShouldBe(h.OutputClrFmt);
        read.OutputBitDepth.ShouldBe(h.OutputBitDepth);
        read.ShortHeaderFlag.ShouldBe(h.ShortHeaderFlag);
        read.WidthMinus1.ShouldBe(h.WidthMinus1);
        read.HeightMinus1.ShouldBe(h.HeightMinus1);
    }

    [Fact]
    public void Rgb_BD32F_LongHeader_RoundTrips()
    {
        // The HDR target format — float 32 per channel.
        var h = new ImageHeader
        {
            OutputClrFmt = JxrOutputColorFormat.Rgb,
            OutputBitDepth = JxrOutputBitDepth.Bd32F,
            ShortHeaderFlag = false,
            LongWordFlag = true,
            WidthMinus1 = 2963 - 1,
            HeightMinus1 = 2991 - 1,
        };
        var w = new BitWriter();
        h.Write(w);
        var r = new BitReader(w.AsSpan());
        var read = ImageHeader.Read(ref r);
        read.OutputClrFmt.ShouldBe(JxrOutputColorFormat.Rgb);
        read.OutputBitDepth.ShouldBe(JxrOutputBitDepth.Bd32F);
        read.LongWordFlag.ShouldBeTrue();
        read.WidthMinus1.ShouldBe(2962u);
        read.HeightMinus1.ShouldBe(2990u);
    }

    [Theory]
    [InlineData(JxrOutputColorFormat.YOnly, JxrOutputBitDepth.Bd8)]
    [InlineData(JxrOutputColorFormat.Rgb, JxrOutputBitDepth.Bd16)]
    [InlineData(JxrOutputColorFormat.Rgb, JxrOutputBitDepth.Bd16F)]
    [InlineData(JxrOutputColorFormat.Rgb, JxrOutputBitDepth.Bd32F)]
    [InlineData(JxrOutputColorFormat.YUV444, JxrOutputBitDepth.Bd16)]
    [InlineData(JxrOutputColorFormat.NComponent, JxrOutputBitDepth.Bd8)]
    public void FormatAndBitDepth_Combinations_RoundTrip(
        JxrOutputColorFormat fmt, JxrOutputBitDepth bd)
    {
        var h = new ImageHeader
        {
            OutputClrFmt = fmt,
            OutputBitDepth = bd,
            ShortHeaderFlag = true,
            WidthMinus1 = 1023,
            HeightMinus1 = 1023,
        };
        var w = new BitWriter();
        h.Write(w);
        var r = new BitReader(w.AsSpan());
        var read = ImageHeader.Read(ref r);
        read.OutputClrFmt.ShouldBe(fmt);
        read.OutputBitDepth.ShouldBe(bd);
    }

    [Fact]
    public void AllFlags_RoundTrip()
    {
        var h = new ImageHeader
        {
            HardTilingFlag = true,
            FrequencyModeCodestreamFlag = true,
            SpatialXfrmSubordinate = 5,
            // IndexTablePresentFlag stays false — not yet supported
            OverlapMode = 2,
            ShortHeaderFlag = true,
            LongWordFlag = true,
            TrimFlexBitsFlag = true,
            RedBlueNotSwappedFlag = true,
            PremultipliedAlphaFlag = true,
            AlphaImagePlaneFlag = true,
            OutputClrFmt = JxrOutputColorFormat.Rgb,
            OutputBitDepth = JxrOutputBitDepth.Bd8,
            WidthMinus1 = 100,
            HeightMinus1 = 200,
        };
        var w = new BitWriter();
        h.Write(w);
        var r = new BitReader(w.AsSpan());
        var read = ImageHeader.Read(ref r);

        read.HardTilingFlag.ShouldBe(h.HardTilingFlag);
        read.FrequencyModeCodestreamFlag.ShouldBe(h.FrequencyModeCodestreamFlag);
        read.SpatialXfrmSubordinate.ShouldBe(h.SpatialXfrmSubordinate);
        read.OverlapMode.ShouldBe(h.OverlapMode);
        read.LongWordFlag.ShouldBe(h.LongWordFlag);
        read.TrimFlexBitsFlag.ShouldBe(h.TrimFlexBitsFlag);
        read.RedBlueNotSwappedFlag.ShouldBe(h.RedBlueNotSwappedFlag);
        read.PremultipliedAlphaFlag.ShouldBe(h.PremultipliedAlphaFlag);
        read.AlphaImagePlaneFlag.ShouldBe(h.AlphaImagePlaneFlag);
    }

    [Fact]
    public void WrongSignature_Throws()
    {
        var bytes = new byte[16];
        bytes[0] = (byte)'X'; // not WMPHOTO

        var threw = false;
        try
        {
            var r = new BitReader(bytes);
            ImageHeader.Read(ref r);
        }
        catch (InvalidDataException ex)
        {
            ex.Message.ShouldContain("GDI_SIGNATURE");
            threw = true;
        }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void Tiled_2x2_RoundTrips()
    {
        // 2×2 tile grid: 1 explicit width + 1 implicit (last column), same for rows.
        var h = new ImageHeader
        {
            OutputClrFmt = JxrOutputColorFormat.Rgb,
            OutputBitDepth = JxrOutputBitDepth.Bd8,
            ShortHeaderFlag = true,
            WidthMinus1 = 127,
            HeightMinus1 = 127,
            TilingFlag = true,
            NumVerTilesMinus1 = 1,
            NumHorTilesMinus1 = 1,
            TileWidthInMb = [4],
            TileHeightInMb = [4],
        };
        var w = new BitWriter();
        h.Write(w);
        var r = new BitReader(w.AsSpan());
        var read = ImageHeader.Read(ref r);

        read.TilingFlag.ShouldBeTrue();
        read.NumVerTilesMinus1.ShouldBe(1);
        read.NumHorTilesMinus1.ShouldBe(1);
        read.TileWidthInMb.Length.ShouldBe(1);
        read.TileWidthInMb[0].ShouldBe(4);
        read.TileHeightInMb[0].ShouldBe(4);
    }

    [Fact]
    public void Tiled_3x4_LongHeader_RoundTrips()
    {
        // 3 tile columns × 4 tile rows = 12 tiles, using u(16) tile widths.
        var h = new ImageHeader
        {
            OutputClrFmt = JxrOutputColorFormat.Rgb,
            OutputBitDepth = JxrOutputBitDepth.Bd16,
            ShortHeaderFlag = false,
            LongWordFlag = true,
            WidthMinus1 = 2962,
            HeightMinus1 = 2990,
            TilingFlag = true,
            NumVerTilesMinus1 = 2,
            NumHorTilesMinus1 = 3,
            TileWidthInMb = [64, 64],
            TileHeightInMb = [48, 48, 48],
        };
        var w = new BitWriter();
        h.Write(w);
        var r = new BitReader(w.AsSpan());
        var read = ImageHeader.Read(ref r);

        read.TilingFlag.ShouldBeTrue();
        read.NumVerTilesMinus1.ShouldBe(2);
        read.NumHorTilesMinus1.ShouldBe(3);
        read.TileWidthInMb.ShouldBe(new[] { 64, 64 });
        read.TileHeightInMb.ShouldBe(new[] { 48, 48, 48 });
    }

    [Fact]
    public void Tiled_InconsistentArrays_Throws()
    {
        var h = new ImageHeader
        {
            OutputClrFmt = JxrOutputColorFormat.YOnly,
            OutputBitDepth = JxrOutputBitDepth.Bd8,
            ShortHeaderFlag = true,
            WidthMinus1 = 63,
            HeightMinus1 = 63,
            TilingFlag = true,
            NumVerTilesMinus1 = 2,
            NumHorTilesMinus1 = 0,
            TileWidthInMb = [3], // wrong length — should be 2
        };

        var w = new BitWriter();
        var threw = false;
        try { h.Write(w); }
        catch (InvalidOperationException) { threw = true; }
        threw.ShouldBeTrue();
    }
}
