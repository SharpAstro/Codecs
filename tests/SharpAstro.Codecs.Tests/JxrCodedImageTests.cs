using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// End-to-end codestream round-trip tests for <see cref="CodedImage"/> —
/// the top-level T.832 §8.2 CODED_IMAGE wrapper. Each test builds a
/// CodedImage from scratch, encodes it to bytes, decodes those bytes
/// back, and verifies every field plus every macroblock survives.
/// </summary>
public sealed class JxrCodedImageTests
{
    private static ImageHeader BuildImageHeader(int width, int height,
        JxrOutputColorFormat outFmt = JxrOutputColorFormat.YOnly,
        JxrOutputBitDepth outBd = JxrOutputBitDepth.Bd8) => new()
    {
        OutputClrFmt = outFmt,
        OutputBitDepth = outBd,
        ShortHeaderFlag = true,
        WidthMinus1 = (uint)(width - 1),
        HeightMinus1 = (uint)(height - 1),
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
    public void Single_16x16_YOnly_DcOnly_RoundTrips()
    {
        // The simplest possible JXR image: 1 MB, YOnly, DcOnly.
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(16, 16),
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [42] }],
        };

        var bytes = img.Encode();
        var decoded = CodedImage.Decode(bytes);

        decoded.Width.ShouldBe(16);
        decoded.Height.ShouldBe(16);
        decoded.WidthInMb.ShouldBe(1);
        decoded.HeightInMb.ShouldBe(1);
        decoded.Macroblocks.Length.ShouldBe(1);
        decoded.Macroblocks[0].Dc[0].ShouldBe(42);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(16, 48)]
    [InlineData(48, 16)]
    [InlineData(64, 64)]
    public void VariousSizes_YOnly_DcOnly_RoundTrip(int width, int height)
    {
        var widthInMb = (width + 15) >> 4;
        var heightInMb = (height + 15) >> 4;
        var n = widthInMb * heightInMb;

        var mbs = new Macroblock[n];
        for (var i = 0; i < n; i++)
            mbs[i] = new Macroblock { Dc = [i * 7 - 100] };

        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(width, height),
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = mbs,
        };

        var decoded = CodedImage.Decode(img.Encode());

        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.WidthInMb.ShouldBe(widthInMb);
        decoded.HeightInMb.ShouldBe(heightInMb);
        for (var i = 0; i < n; i++)
            decoded.Macroblocks[i].Dc[0].ShouldBe(i * 7 - 100, $"mb {i}");
    }

    [Fact]
    public void NonAlignedSize_PadsTo16Multiples()
    {
        // 17×33 image needs a 2×3 MB grid (= 6 MBs). Bottom-right MBs cover
        // padded pixels.
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(17, 33),
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = Enumerable.Range(0, 6)
                .Select(i => new Macroblock { Dc = [i] })
                .ToArray(),
        };

        var decoded = CodedImage.Decode(img.Encode());

        decoded.Width.ShouldBe(17);
        decoded.Height.ShouldBe(33);
        decoded.WidthInMb.ShouldBe(2);
        decoded.HeightInMb.ShouldBe(3);
        decoded.Macroblocks.Length.ShouldBe(6);
    }

    [Fact]
    public void Rgb_NoHighpass_RoundTrips()
    {
        // RGB output through the DC + LP chain.
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(16, 16, JxrOutputColorFormat.Rgb),
            PlaneHeader = BuildPlaneHeader(JxrInternalColorFormat.Rgb, JxrBandsPresent.NoHighpass, 3),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = [new Macroblock
            {
                Dc = [100, 200, 300],
                Lp = Enumerable.Range(0, 48).Select(i => i % 16 == 0 ? 0 : i).ToArray(),
            }],
        };

        var decoded = CodedImage.Decode(img.Encode());
        decoded.Macroblocks[0].Dc.ShouldBe(new[] { 100, 200, 300 });
        for (var i = 0; i < 48; i++)
            if (i % 16 != 0) // position 0 of each component is super-DC, ignored
                decoded.Macroblocks[0].Lp[i].ShouldBe(i, $"lp[{i}]");
    }

    [Fact]
    public void Rgb_NoFlexbits_HdrPath_RoundTrips()
    {
        // The HDR-master path: Bd32F + Rgb + NoFlexbits, the full DC+LP+CBPHP+HP chain.
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(16, 16, JxrOutputColorFormat.Rgb, JxrOutputBitDepth.Bd32F),
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.Rgb,
                BandsPresent = JxrBandsPresent.NoFlexbits,
                NumComponents = 3,
                DcQuant = 1,
                LpQuant = 1,
                HpQuant = 1,
                LenMantissa = 23,
                ExpBias = 127 - 128, // typical float bias
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.L1),
            Macroblocks = [new Macroblock
            {
                Dc = [10, 20, 30],
                Lp = new int[3 * 16],
                Hp = new int[3 * 256],
                MbHpMode = 0,
            }],
        };

        var decoded = CodedImage.Decode(img.Encode());

        decoded.ImageHeader.OutputBitDepth.ShouldBe(JxrOutputBitDepth.Bd32F);
        decoded.PlaneHeader.LenMantissa.ShouldBe((byte)23);
        decoded.PlaneHeader.BandsPresent.ShouldBe(JxrBandsPresent.NoFlexbits);
        decoded.Macroblocks[0].Dc.ShouldBe(new[] { 10, 20, 30 });
        decoded.Macroblocks[0].Hp.Length.ShouldBe(3 * 256);
    }

    [Fact]
    public void HeaderFieldsSurvive_RoundTrip()
    {
        // Verify the codestream prologue is reassembled faithfully.
        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.Rgb,
                OutputBitDepth = JxrOutputBitDepth.Bd16,
                ShortHeaderFlag = true,
                LongWordFlag = true,
                TrimFlexBitsFlag = true,
                RedBlueNotSwappedFlag = true,
                WidthMinus1 = 31,
                HeightMinus1 = 47,
            },
            PlaneHeader = new ImagePlaneHeader
            {
                InternalClrFmt = JxrInternalColorFormat.Rgb,
                BandsPresent = JxrBandsPresent.DcOnly,
                NumComponents = 3,
                DcQuant = 12,
                ShiftBits = 4,
            },
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L4),
            Macroblocks = Enumerable.Range(0, 6)
                .Select(_ => new Macroblock { Dc = new[] { 0, 0, 0 } })
                .ToArray(),
        };

        var decoded = CodedImage.Decode(img.Encode());

        decoded.ImageHeader.OutputClrFmt.ShouldBe(JxrOutputColorFormat.Rgb);
        decoded.ImageHeader.OutputBitDepth.ShouldBe(JxrOutputBitDepth.Bd16);
        decoded.ImageHeader.TrimFlexBitsFlag.ShouldBeTrue();
        decoded.ImageHeader.RedBlueNotSwappedFlag.ShouldBeTrue();
        decoded.PlaneHeader.DcQuant.ShouldBe((byte)12);
        decoded.PlaneHeader.ShiftBits.ShouldBe((byte)4);
        decoded.ProfileLevelInfo.Entries.Count.ShouldBe(1);
        decoded.ProfileLevelInfo.Entries[0].ProfileIdc.ShouldBe(JxrProfile.Main);
        decoded.ProfileLevelInfo.Entries[0].LevelIdc.ShouldBe(JxrLevel.L4);
    }

    [Fact]
    public void Alpha_Throws_NotSupported()
    {
        var img = new CodedImage
        {
            ImageHeader = new ImageHeader
            {
                OutputClrFmt = JxrOutputColorFormat.Rgb,
                OutputBitDepth = JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                AlphaImagePlaneFlag = true,
                WidthMinus1 = 15,
                HeightMinus1 = 15,
            },
            PlaneHeader = BuildPlaneHeader(JxrInternalColorFormat.Rgb, JxrBandsPresent.DcOnly, 3),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [0, 0, 0] }],
        };

        var threw = false;
        try { img.Encode(); }
        catch (NotSupportedException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void MismatchedMacroblockCount_Throws()
    {
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(32, 32), // expects 2×2 = 4 MBs
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [0] }], // only 1
        };

        var threw = false;
        try { img.Encode(); }
        catch (InvalidOperationException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void DerivedDimensions_ComputeCorrectly()
    {
        // Spot-check the ceil(width/16) computation on edge cases.
        var img = new CodedImage
        {
            ImageHeader = BuildImageHeader(1, 1), // smallest possible
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [0] }],
        };
        img.WidthInMb.ShouldBe(1);
        img.HeightInMb.ShouldBe(1);

        var img2 = new CodedImage
        {
            ImageHeader = BuildImageHeader(16, 16),
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = [new Macroblock { Dc = [0] }],
        };
        img2.WidthInMb.ShouldBe(1);

        var img3 = new CodedImage
        {
            ImageHeader = BuildImageHeader(17, 17),
            PlaneHeader = BuildPlaneHeader(),
            ProfileLevelInfo = ProfileLevelInfo.Single(JxrProfile.SubBaseline, JxrLevel.L1),
            Macroblocks = Enumerable.Range(0, 4).Select(_ => new Macroblock { Dc = [0] }).ToArray(),
        };
        img3.WidthInMb.ShouldBe(2);
        img3.HeightInMb.ShouldBe(2);
    }
}
