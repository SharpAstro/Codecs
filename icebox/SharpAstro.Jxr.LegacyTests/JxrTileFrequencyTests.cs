using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// End-to-end round-trip tests for the frequency-mode tile orchestrator
/// (T.832 §8.7). Each test builds a CodedImage with
/// <c>FrequencyModeCodestreamFlag = true</c>, encodes it, decodes the
/// bytes, and verifies the macroblock data survives. Where applicable a
/// parallel spatial-mode encoding is also done and the two decoders are
/// cross-checked to ensure they reach the same MBs (the codestream bytes
/// differ, but the decoded MBs should match).
/// </summary>
public sealed class JxrTileFrequencyTests
{
    private static ImageHeader BuildImageHeader(int width, int height, bool frequencyMode,
        JxrOutputColorFormat outFmt = JxrOutputColorFormat.YOnly,
        JxrOutputBitDepth outBd = JxrOutputBitDepth.Bd8) => new()
    {
        OutputClrFmt = outFmt,
        OutputBitDepth = outBd,
        ShortHeaderFlag = true,
        WidthMinus1 = (uint)(width - 1),
        HeightMinus1 = (uint)(height - 1),
        FrequencyModeCodestreamFlag = frequencyMode,
    };

    private static ImagePlaneHeader BuildPlaneHeader(
        JxrInternalColorFormat fmt = JxrInternalColorFormat.YOnly,
        JxrBandsPresent bands = JxrBandsPresent.DcOnly,
        int numComponents = 1) => new()
    {
        InternalClrFmt = fmt,
        BandsPresent = bands,
        NumComponents = numComponents,
        DcQuant = 1,
        LpQuant = 1,
        HpQuant = 1,
    };

    [Fact]
    public void Frequency_Single_16x16_YOnly_DcOnly_RoundTrips()
    {
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(16, 16, frequencyMode: true),
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [42] }],
        };

        var decoded = CodedImage.Decode(img.Encode());

        decoded.ImageHeader.FrequencyModeCodestreamFlag.ShouldBeTrue();
        decoded.Macroblocks[0].Dc[0].ShouldBe(42);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(16, 48)]
    [InlineData(48, 16)]
    [InlineData(64, 64)]
    public void Frequency_VariousSizes_DcOnly_RoundTrip(int width, int height)
    {
        var widthInMb = (width + 15) >> 4;
        var heightInMb = (height + 15) >> 4;
        var n = widthInMb * heightInMb;

        var mbs = new Macroblock[n];
        for (var i = 0; i < n; i++)
            mbs[i] = new Macroblock { Dc = [i * 7 - 100] };

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height, frequencyMode: true),
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs,
        };

        var decoded = CodedImage.Decode(img.Encode());

        for (var i = 0; i < n; i++)
            decoded.Macroblocks[i].Dc[0].ShouldBe(i * 7 - 100, $"mb {i}");
    }

    [Fact]
    public void Frequency_Rgb_NoHighpass_RoundTrips()
    {
        var lp = Enumerable.Range(0, 48).Select(i => i % 16 == 0 ? 0 : i).ToArray();
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(16, 16, frequencyMode: true, JxrOutputColorFormat.Rgb),
            PlaneHeader = BuildPlaneHeader(JxrInternalColorFormat.Rgb, JxrBandsPresent.NoHighpass, 3),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [100, 200, 300], Lp = lp }],
        };

        var decoded = CodedImage.Decode(img.Encode());
        decoded.Macroblocks[0].Dc.ShouldBe(new[] { 100, 200, 300 });
        for (var i = 0; i < 48; i++)
            if (i % 16 != 0)
                decoded.Macroblocks[0].Lp[i].ShouldBe(i, $"lp[{i}]");
    }

    [Fact]
    public void Frequency_Rgb_NoFlexbits_RoundTrips()
    {
        // Full DC+LP+CBPHP+HP chain in frequency mode, BD8 RGB.
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(16, 16, frequencyMode: true, JxrOutputColorFormat.Rgb),
            PlaneHeader = BuildPlaneHeader(JxrInternalColorFormat.Rgb, JxrBandsPresent.NoFlexbits, 3),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = [new Macroblock
            {
                Dc = [10, 20, 30],
                Lp = new int[3 * 16],
                Hp = new int[3 * 256],
                MbHpMode = 0,
            }],
        };

        var decoded = CodedImage.Decode(img.Encode());
        decoded.PlaneHeader.BandsPresent.ShouldBe(JxrBandsPresent.NoFlexbits);
        decoded.Macroblocks[0].Dc.ShouldBe(new[] { 10, 20, 30 });
        decoded.Macroblocks[0].Lp.Length.ShouldBe(3 * 16);
        decoded.Macroblocks[0].Hp.Length.ShouldBe(3 * 256);
    }

    [Fact]
    public void Frequency_Tiled_2x2_DcOnly_RoundTrips()
    {
        // Frequency mode + multi-tile. Each tile's DC sub-stream is emitted
        // before its LP/HP sub-streams; with DcOnly that means 4 sub-streams
        // total (one per tile, one band each).
        var mbs = new Macroblock[4];
        for (var i = 0; i < 4; i++)
            mbs[i] = new Macroblock { Dc = [(i + 1) * 100] };

        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 31,
                HeightMinus1 = 31,
                TilingFlag = true,
                FrequencyModeCodestreamFlag = true,
                NumVerTilesMinus1 = 1,
                NumHorTilesMinus1 = 1,
                TileWidthInMb = [1],
                TileHeightInMb = [1],
            },
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs,
        };

        var decoded = CodedImage.Decode(img.Encode());

        for (var i = 0; i < 4; i++)
            decoded.Macroblocks[i].Dc[0].ShouldBe((i + 1) * 100, $"mb {i}");
    }

    [Fact]
    public void Frequency_Tiled_3x3_NoHighpass_RoundTrips()
    {
        var mbs = new Macroblock[9];
        for (var i = 0; i < 9; i++)
            mbs[i] = new Macroblock
            {
                Dc = [i * 50 - 200],
                Lp = Enumerable.Range(0, 16).Select(k => k == 0 ? 0 : (k * 3) - (i * 5)).ToArray(),
            };

        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 47,
                HeightMinus1 = 47,
                TilingFlag = true,
                FrequencyModeCodestreamFlag = true,
                NumVerTilesMinus1 = 2,
                NumHorTilesMinus1 = 2,
                TileWidthInMb = [1, 1],
                TileHeightInMb = [1, 1],
            },
            PlaneHeader = BuildPlaneHeader(bands: JxrBandsPresent.NoHighpass),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs,
        };

        var decoded = CodedImage.Decode(img.Encode());

        for (var i = 0; i < 9; i++)
        {
            decoded.Macroblocks[i].Dc[0].ShouldBe(i * 50 - 200, $"dc mb {i}");
            for (var k = 1; k < 16; k++) // position 0 is reserved
                decoded.Macroblocks[i].Lp[k].ShouldBe((k * 3) - (i * 5), $"lp mb {i} pos {k}");
        }
    }

    [Fact]
    public void Frequency_Tiled_WithIndexTable_RoundTrips()
    {
        // Frequency mode + INDEX_TABLE_TILES with multiple bands per tile.
        // For NoHighpass at 2×2 tiles → 8 entries (4 tiles × 2 bands).
        var mbs = new Macroblock[4];
        for (var i = 0; i < 4; i++)
            mbs[i] = new Macroblock
            {
                Dc = [(i + 1) * 100],
                Lp = Enumerable.Range(0, 16).Select(k => k == 0 ? 0 : k + i * 7).ToArray(),
            };

        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.YOnly,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                WidthMinus1 = 31,
                HeightMinus1 = 31,
                TilingFlag = true,
                FrequencyModeCodestreamFlag = true,
                IndexTablePresentFlag = true,
                NumVerTilesMinus1 = 1,
                NumHorTilesMinus1 = 1,
                TileWidthInMb = [1],
                TileHeightInMb = [1],
            },
            PlaneHeader = BuildPlaneHeader(bands: JxrBandsPresent.NoHighpass),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs,
        };

        var decoded = CodedImage.Decode(img.Encode());

        decoded.ImageHeader.FrequencyModeCodestreamFlag.ShouldBeTrue();
        decoded.ImageHeader.IndexTablePresentFlag.ShouldBeTrue();
        for (var i = 0; i < 4; i++)
            decoded.Macroblocks[i].Dc[0].ShouldBe((i + 1) * 100, $"mb {i}");
    }

    [Fact]
    public void Frequency_And_Spatial_DecodeToSameMbs()
    {
        // Cross-check: encode the same MBs once in spatial mode and once in
        // frequency mode. The byte streams differ, but the decoded
        // macroblocks must match — the per-band syntax elements are the
        // same, only their layout in the bitstream changes.
        var mbs = new Macroblock[4];
        for (var i = 0; i < 4; i++)
            mbs[i] = new Macroblock
            {
                Dc = [(i + 1) * 100],
                Lp = Enumerable.Range(0, 16).Select(k => k == 0 ? 0 : (k - 8) * (i + 1)).ToArray(),
                Hp = new int[256],
                MbHpMode = 2,
            };

        ImageHeader MakeHeader(bool freq) => new()
        {
            OutputClrFmt = JxrOutputColorFormat.YOnly,
            OutputBitDepth = JxrOutputBitDepth.Bd8,
            ShortHeaderFlag = true,
            WidthMinus1 = 31,
            HeightMinus1 = 31,
            FrequencyModeCodestreamFlag = freq,
        };

        var spatial = new CodedImage
        {
            ImageHeader = MakeHeader(false),
            PlaneHeader = BuildPlaneHeader(bands: JxrBandsPresent.NoFlexbits),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs.Select(m => new Macroblock
            {
                Dc = (int[])m.Dc.Clone(),
                Lp = (int[])m.Lp.Clone(),
                Hp = (int[])m.Hp.Clone(),
                MbHpMode = m.MbHpMode,
            }).ToArray(),
        };
        var frequency = new CodedImage
        {
            ImageHeader = MakeHeader(true),
            PlaneHeader = BuildPlaneHeader(bands: JxrBandsPresent.NoFlexbits),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = mbs.Select(m => new Macroblock
            {
                Dc = (int[])m.Dc.Clone(),
                Lp = (int[])m.Lp.Clone(),
                Hp = (int[])m.Hp.Clone(),
                MbHpMode = m.MbHpMode,
            }).ToArray(),
        };

        var ds = CodedImage.Decode(spatial.Encode());
        var df = CodedImage.Decode(frequency.Encode());

        for (var i = 0; i < 4; i++)
        {
            df.Macroblocks[i].Dc[0].ShouldBe(ds.Macroblocks[i].Dc[0], $"dc mb {i}");
            for (var k = 1; k < 16; k++)
                df.Macroblocks[i].Lp[k].ShouldBe(ds.Macroblocks[i].Lp[k], $"lp mb {i} pos {k}");
        }
    }

    [Fact]
    public void TileFrequency_BandCount_MatchesSpec()
    {
        TileFrequency.BandCount(JxrBandsPresent.DcOnly).ShouldBe(1);
        TileFrequency.BandCount(JxrBandsPresent.NoHighpass).ShouldBe(2);
        TileFrequency.BandCount(JxrBandsPresent.NoFlexbits).ShouldBe(3);
        TileFrequency.BandCount(JxrBandsPresent.AllBands).ShouldBe(4);
    }
}
