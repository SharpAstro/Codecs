using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="PngImageDecoder.TryDecodeRgbaFloat(ReadOnlySpan{byte}, out float[], out int, out int, out ColorEncoding)"/>:
/// a PNG to linear-light RGBA floats, PQ and HLG included.
/// </summary>
/// <remarks>
/// <para>The reference for every number here is computed in this file, in double precision, straight
/// from the standards (ST 2084, BT.2100 Table 5, IEC 61966-2-1, BT.709 / BT.2020 as printed, H.273
/// §8.3) - not by calling <c>PngHdr</c>, whose own anchors are pinned in
/// <see cref="PngHdrTransferTests"/>. What these tests add is that the <i>decoder</i> wires the right
/// samples, range, curve and alpha together for each colour type and depth.</para>
/// <para>The PQ / HLG fixtures are Mike Pedersen's cICP conformance files (<c>Fixtures/Cicp</c>). They
/// are checked two ways, kept apart: every sample against the double reference (1e-4 relative), and a
/// handful of BT.2111 / BT.2408 patches against the <i>nominal</i> levels those documents print, so a
/// curve that is self-consistent but wrong cannot pass.</para>
/// </remarks>
public sealed class PngTryDecodeRgbaFloatTests
{
    private static readonly string FixturesRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cicp");

    private const string PqFr = "PNG-PQ-BT.2111-ColorBars-16bit-cICP-FR.png";
    private const string PqMdcv1K = "PNG-PQ-BT.2111-ColorBars-16bit-cICP-mDCV-max1Knit-min.0005nit-cLLI-FR.png";
    private const string PqMdcv4K = "PNG-PQ-BT.2111-ColorBars-16bit-cICP-mDCV-max4Knit-min.0005nit-cLLI-FR.png";
    private const string HlgFr = "PNG-HLG-FancyColorBars-16bit-cICP-FR.png";
    private const string HlgNr = "PNG-HLG-FancyColorBars-16bit-cICP-NR.png";
    private const string SdrFr = "PNG-SDR-BT.709-ColorBars-Tent-Valley-Grayscale-16bit-cICP-FR.png";
    private const string SdrNr = "PNG-SDR-BT.709-ColorBars-Tent-Valley-Grayscale-16bit-cICP-NR.png";
    private const string Sdr2020 = "SDR-Color-Bars-BT.709-16bit-1080-FR-BT.2020-12bitTest.png";

    // ---- the reference, in double, from the standards ---------------------------------------

    // BT.2408 reference white: the divisor that puts PQ / HLG display light on the 1.0 = reference-white scale.
    private const double RefWhiteNits = 203.0;

    // ST 2084 EOTF: signal -> cd/m2.
    private static double RefPqNits(double n)
    {
        const double m1 = 2610.0 / 16384.0, m2 = 2523.0 / 4096.0 * 128.0;
        const double c1 = 3424.0 / 4096.0, c2 = 2413.0 / 4096.0 * 32.0, c3 = 2392.0 / 4096.0 * 32.0;
        var p = Math.Pow(n, 1.0 / m2);
        return 10000.0 * Math.Pow(Math.Max(p - c1, 0.0) / (c2 - c3 * p), 1.0 / m1);
    }

    // ST 2084 inverse EOTF (the encoder): cd/m2 -> signal. Used to read a patch back to its code.
    private static double RefPqSignal(double nits)
    {
        const double m1 = 2610.0 / 16384.0, m2 = 2523.0 / 4096.0 * 128.0;
        const double c1 = 3424.0 / 4096.0, c2 = 2413.0 / 4096.0 * 32.0, c3 = 2392.0 / 4096.0 * 32.0;
        var y = Math.Pow(nits / 10000.0, m1);
        return Math.Pow((c1 + c2 * y) / (1.0 + c3 * y), m2);
    }

    // BT.2100-2 Table 5 HLG inverse OETF: signal -> scene light.
    private static double RefHlgScene(double s)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        return s <= 0.5 ? s * s / 3.0 : (Math.Exp((s - c) / a) + b) / 12.0;
    }

    // BT.2100-2 Table 5 OOTF on scene light (r, g, b) with the BT.2020 Y weights: display cd/m2 per channel.
    private static (double R, double G, double B) RefHlgDisplayNits(double r, double g, double b)
    {
        var ys = 0.2627 * r + 0.6780 * g + 0.0593 * b;
        var gain = 1000.0 * Math.Pow(ys, 0.2);
        return (gain * r, gain * g, gain * b);
    }

    private static double RefSrgb(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    // BT.709 / BT.2020 OETF inverse with the constants the recommendation prints (alpha, beta).
    private static double RefBt709(double v, double alpha = 1.099, double beta = 0.018) =>
        v < 4.5 * beta ? v / 4.5 : Math.Pow((v + (alpha - 1.0)) / alpha, 1.0 / 0.45);

    // H.273 §8.3 RGB code -> normalised signal, unclamped.
    private static double RefSignal(int code, int bitDepth, bool fullRange) =>
        fullRange
            ? code / (double)((1 << bitDepth) - 1)
            : (code - 16.0 * (1 << (bitDepth - 8))) / (219.0 * (1 << (bitDepth - 8)));

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    private static void Close(float actual, double expected, double relative, string what)
    {
        // A zero reference must come out exactly zero; otherwise a relative bound.
        Math.Abs(actual - expected).ShouldBeLessThanOrEqualTo(relative * Math.Abs(expected), what + $" (actual {actual:R}, expected {expected:R})");
    }

    // ---- fixtures: helpers ------------------------------------------------------------------

    private static byte[] Fixture(string name)
    {
        var path = Path.Combine(FixturesRoot, name);
        File.Exists(path).ShouldBeTrue($"fixture missing: {path}");
        return File.ReadAllBytes(path);
    }

    private static (float[] Rgba, int Width, int Height, ColorEncoding Encoding) DecodeFloat(byte[] file)
    {
        PngImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out var w, out var h, out var enc).ShouldBeTrue();
        return (rgba, w, h, enc);
    }

    private static (float R, float G, float B, float A) PixelAt(float[] rgba, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    // The three 16-bit codes the file holds at (x, y): documents which patch a spot check is about.
    private static (int R, int G, int B) CodesAt(PngImage png, int x, int y)
    {
        var s = png.AsUInt16Samples();
        var i = (y * png.Width + x) * 3;
        return (s[i], s[i + 1], s[i + 2]);
    }

    // ---- PQ / HLG fixtures: every sample against the double reference ------------------------

    [Theory]
    [InlineData(PqFr)]
    [InlineData(PqMdcv1K)]
    [InlineData(PqMdcv4K)]
    [InlineData(HlgFr)]
    [InlineData(HlgNr)]
    public void HdrFixture_EverySampleMatchesTheDoublePrecisionReference(string name)
    {
        var file = Fixture(name);
        var (rgba, w, h, enc) = DecodeFloat(file);
        var png = PngReader.Decode(file);
        var cicp = png.Cicp.ShouldNotBeNull();
        var samples = png.AsUInt16Samples();

        (w, h).ShouldBe((png.Width, png.Height));
        rgba.Length.ShouldBe(w * h * 4);

        // Per 16-bit code, once: the range-expanded, clamped signal, and the curve's per-channel step.
        // PQ is fully per channel (-> 1.0 = 203 cd/m2); HLG's per-channel step is the inverse OETF and
        // its OOTF then couples the three channels through the scene luminance.
        var perChannel = new double[65536];
        for (var code = 0; code < 65536; code++)
        {
            var signal = Clamp01(RefSignal(code, 16, cicp.VideoFullRangeFlag));
            perChannel[code] = cicp.TransferFunction == TransferFunction.Pq
                ? RefPqNits(signal) / RefWhiteNits
                : RefHlgScene(signal);
        }

        var bad = 0;
        var firstBad = -1;
        var worst = 0.0;
        void Check(int index, float actual, double expected)
        {
            var error = expected == 0.0 ? Math.Abs(actual) : Math.Abs(actual - expected) / Math.Abs(expected);
            worst = Math.Max(worst, error);
            if (error > 1e-4 && bad++ == 0) firstBad = index;
        }

        for (var p = 0; p < w * h; p++)
        {
            var r = perChannel[samples[p * 3]];
            var g = perChannel[samples[p * 3 + 1]];
            var b = perChannel[samples[p * 3 + 2]];
            if (cicp.TransferFunction == TransferFunction.Hlg)
            {
                var d = RefHlgDisplayNits(r, g, b);
                (r, g, b) = (d.R / RefWhiteNits, d.G / RefWhiteNits, d.B / RefWhiteNits);
            }
            Check(p * 4, rgba[p * 4], r);
            Check(p * 4 + 1, rgba[p * 4 + 1], g);
            Check(p * 4 + 2, rgba[p * 4 + 2], b);
            rgba[p * 4 + 3].ShouldBe(1f);
        }

        bad.ShouldBe(0, $"{bad} samples differ from the reference by more than 1e-4 relative; worst {worst:E2}, first at sample {firstBad}");

        enc.Primaries.ShouldBe(ColorPrimaries.BT2020);
        enc.Transfer.ShouldBe(TransferFunction.Linear);
        enc.FullRange.ShouldBeTrue();
        enc.Float.ShouldBe(FloatSemantics.DisplayReferred);
    }

    // ---- PQ / HLG fixtures: the patches BT.2111 / BT.2408 give nominal levels for -------------

    [Fact]
    public void PqBars_NominalLevels()
    {
        var file = Fixture(PqFr);
        var (rgba, w, _, _) = DecodeFloat(file);
        var png = PngReader.Decode(file);

        // Black.
        CodesAt(png, 109, 29).ShouldBe((0, 0, 0));
        PixelAt(rgba, w, 109, 29).ShouldBe((0f, 0f, 0f, 1f));

        // Peak white, code 1.0: ST 2084's 10 000 cd/m2, 49.26 of a 203 cd/m2 reference white.
        CodesAt(png, 241, 0).ShouldBe((65535, 65535, 65535));
        var white = PixelAt(rgba, w, 241, 0);
        Close(white.R, 10000.0 / 203.0, 1e-5, "100 % PQ white R");
        Close(white.G, 10000.0 / 203.0, 1e-5, "100 % PQ white G");
        Close(white.B, 10000.0 / 203.0, 1e-5, "100 % PQ white B");

        // The 58 % grey is BT.2408's HDR reference white. 58 % is a rounded code for 203 cd/m2 (PQ
        // 0.5807), so it lands on 201.7 cd/m2 - within 1 % of the nominal.
        CodesAt(png, 244, 94).ShouldBe((38010, 38010, 38010));
        var refWhite = PixelAt(rgba, w, 244, 94);
        refWhite.R.ShouldBe(1f, 0.01f);
        refWhite.G.ShouldBe(refWhite.R);
        refWhite.B.ShouldBe(refWhite.R);
        // ...and it is a PQ 58 % signal again once re-encoded (the encoder, not the decoder, formula).
        RefPqSignal(refWhite.R * RefWhiteNits).ShouldBe(38010 / 65535.0, 1e-5);

        // A 58 % primary: its own channel at the reference-white level, the others at black.
        CodesAt(png, 1269, 94).ShouldBe((38010, 0, 0));
        var red = PixelAt(rgba, w, 1269, 94);
        red.R.ShouldBe(refWhite.R);
        (red.G, red.B).ShouldBe((0f, 0f));

        // The grey ramp 0.1 .. 0.9: each patch re-encodes (ST 2084 inverse EOTF) to the code in the file.
        foreach (var (x, code) in new[] { (553, 6553), (656, 13107), (759, 19660), (964, 32767), (1066, 39321), (1169, 45874), (1272, 52428), (1375, 58981) })
        {
            CodesAt(png, x, 634).ShouldBe((code, code, code));
            var grey = PixelAt(rgba, w, x, 634);
            RefPqSignal(grey.R * RefWhiteNits).ShouldBe(code / 65535.0, 1e-5, $"ramp patch at x={x}");
        }
    }

    [Fact]
    public void HlgBars_NominalLevels()
    {
        var file = Fixture(HlgFr);
        var (rgba, w, _, _) = DecodeFloat(file);
        var png = PngReader.Decode(file);

        // 75 % HLG is BT.2408's reference white: 203 cd/m2 on the nominal 1000 cd/m2 display (203.2
        // from the OOTF), so 1.0 within half a percent.
        CodesAt(png, 244, 93).ShouldBe((49151, 49151, 49151));
        var refWhite = PixelAt(rgba, w, 244, 93);
        refWhite.R.ShouldBe(1f, 0.005f);
        (refWhite.G, refWhite.B).ShouldBe((refWhite.R, refWhite.R));

        // 100 % HLG white is the display's 1000 cd/m2 peak: 4.926 of the 203 cd/m2 reference white
        // (to the 1e-4 that the printed constants leave at E = 1).
        CodesAt(png, 242, 0).ShouldBe((65535, 65535, 65535));
        var peak = PixelAt(rgba, w, 242, 0);
        Close(peak.R, 1000.0 / 203.0, 5e-4, "100 % HLG white");
        (peak.G, peak.B).ShouldBe((peak.R, peak.R));

        // The 38 % grey background is BT.2408's 18 % grey card: 26 cd/m2.
        CodesAt(png, 0, 0).ShouldBe((24837, 24837, 24837));
        var card = PixelAt(rgba, w, 0, 0);
        (card.R * RefWhiteNits).ShouldBe(26.0, 0.2);

        // 50 % signal is scene light 1/12, i.e. 1000 / 12^1.2 = 50.7 cd/m2.
        CodesAt(png, 81, 599).ShouldBe((32767, 32767, 32767));
        (PixelAt(rgba, w, 81, 599).R * RefWhiteNits).ShouldBe(50.7, 0.1);

        CodesAt(png, 15, 27).ShouldBe((0, 0, 0));
        PixelAt(rgba, w, 15, 27).ShouldBe((0f, 0f, 0f, 1f));
    }

    [Fact]
    public void HlgNarrowRange_IsTheSamePictureAsFullRange()
    {
        var (fr, w, _, _) = DecodeFloat(Fixture(HlgFr));
        var nrFile = Fixture(HlgNr);
        var (nr, _, _, _) = DecodeFloat(nrFile);
        var nrPng = PngReader.Decode(nrFile);

        // The same bars at the same places: 38 % background, 75 % reference-white bar, 100 % white. The two
        // files are separate renders, so codes differ by a dither step or two; 1 % of the light covers that.
        foreach (var (x, y) in new[] { (0, 0), (244, 93), (60, 596), (1373, 633) })
        {
            var a = PixelAt(fr, w, x, y);
            var b = PixelAt(nr, w, x, y);
            b.R.ShouldBe(a.R, Math.Abs(a.R) * 0.01 + 1e-6, $"R at ({x},{y})");
            b.G.ShouldBe(a.G, Math.Abs(a.G) * 0.01 + 1e-6, $"G at ({x},{y})");
            b.B.ShouldBe(a.B, Math.Abs(a.B) * 0.01 + 1e-6, $"B at ({x},{y})");
        }

        // Footroom: code 4096 is narrow-range black, exactly; sub-black codes clamp to it.
        CodesAt(nrPng, 195, 27).ShouldBe((4096, 4096, 4096));
        PixelAt(nr, w, 195, 27).ShouldBe((0f, 0f, 0f, 1f));
        CodesAt(nrPng, 244, 633).ShouldBe((252, 252, 252));
        PixelAt(nr, w, 244, 633).ShouldBe((0f, 0f, 0f, 1f));

        // Headroom: code 60214 is above the nominal 60160, 65275 far above it. Both clamp to signal 1.0,
        // which is full-range 65535's output.
        var white = PixelAt(fr, w, 242, 0);
        CodesAt(nrPng, 244, 0).ShouldBe((60214, 60214, 60214));
        PixelAt(nr, w, 244, 0).ShouldBe(white);
        CodesAt(nrPng, 1579, 633).ShouldBe((65275, 65275, 65275));
        PixelAt(nr, w, 1579, 633).ShouldBe(white);
    }

    // ---- SDR fixtures: against their own 8-bit output, linearised ------------------------------

    [Theory]
    [InlineData(SdrFr, true, 1.099, 0.018)]
    [InlineData(SdrNr, false, 1.099, 0.018)]
    // BT.2020's 12-bit constants (transfer 15).
    [InlineData(Sdr2020, true, 1.0993, 0.0181)]
    public void SdrFixture_MatchesTheRgba8OutputLinearisedWithinEightBitQuantisation(string name, bool fullRange, double alpha, double beta)
    {
        var file = Fixture(name);
        var (rgba, w, h, enc) = DecodeFloat(file);
        PngImageDecoder.TryDecodeRgba8(file, out var rgba8, out var w8, out var h8).ShouldBeTrue();
        (w8, h8).ShouldBe((w, h));
        rgba8.Length.ShouldBe(rgba.Length);

        // TryDecodeRgba8 keeps a 16-bit sample's high byte as it is (no range expansion, truncated), so
        // each of its codes c stands for 16-bit codes in [256 c, 256 c + 255]. +/- one 8-bit code is the
        // quantisation band; the linearised band is the curve at its ends (the curve is monotone).
        // The reference curve uses the constants the recommendation prints (rounded), the decoder the
        // full-precision solution of the same curve: they differ by up to ~0.3 % relative (at the knee),
        // and a code at the very edge of its band shows it, so the band is widened by 0.5 % of its value.
        var curve = new double[258];
        for (var c = -1; c <= 256; c++)
            curve[c + 1] = RefBt709(Clamp01(RefSignal(c, 8, fullRange)), alpha, beta);

        var bad = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var code = rgba8[i + c];
                var lo = curve[code - 1 + 1] * (1 - 5e-3) - 1e-4;
                var hi = curve[code + 1 + 1] * (1 + 5e-3) + 1e-4;
                if (rgba[i + c] < lo || rgba[i + c] > hi) bad++;
            }
            rgba[i + 3].ShouldBe(rgba8[i + 3] / 255f);
        }
        bad.ShouldBe(0, $"{bad} colour samples are outside the +/- 1 code band of the RGBA8 output");

        enc.Transfer.ShouldBe(TransferFunction.Linear);
        enc.Float.ShouldBe(FloatSemantics.DisplayReferred);
    }

    [Theory]
    [InlineData(SdrFr)]
    [InlineData(Sdr2020)]
    public void SdrFixture_WhiteIsOneAndBlackIsZero(string name)
    {
        var file = Fixture(name);
        var (rgba, _, _, _) = DecodeFloat(file);
        // 1.0 is SDR white: not rescaled to 203 like PQ / HLG.
        rgba.Max().ShouldBe(1f, 1e-6f);
        rgba.Min().ShouldBeGreaterThanOrEqualTo(0f);
    }

    // ---- metadata ---------------------------------------------------------------------------

    [Theory]
    [InlineData(PqMdcv1K)]
    [InlineData(PqMdcv4K)]
    public void MdcvAndClli_AreSurfacedAsTheReaderParsedThem(string name)
    {
        var file = Fixture(name);
        PngImageDecoder.TryDecodeRgbaFloat(file, out _, out _, out _, out _, out var mdcv, out var clli).ShouldBeTrue();

        var png = PngReader.Decode(file);
        mdcv.ShouldNotBeNull().ShouldBe(png.Mdcv);
        clli.ShouldNotBeNull().ShouldBe(png.Clli);
        clli.MaxCllUnits.ShouldBeGreaterThan(0u);
    }

    [Fact]
    public void MdcvAndClli_AreNullWhenTheFileHasNone()
    {
        PngImageDecoder.TryDecodeRgbaFloat(Fixture(PqFr), out _, out _, out _, out _, out var mdcv, out var clli).ShouldBeTrue();

        mdcv.ShouldBeNull();
        clli.ShouldBeNull();
    }

    [Fact]
    public void MdcvAndClli_RoundTripThroughTheWriter()
    {
        var mdcv = new MdcvChunk(34000, 16000, 13250, 34500, 7500, 3000, 15635, 16450, 10_000_000, 50);
        var clli = new ClliChunk(10_000_000, 2_500_000);
        var png = PngWriter.EncodeRgb16([0, 0, 0, 65535, 65535, 65535], 2, 1,
            new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq, Mdcv = mdcv, Clli = clli });

        PngImageDecoder.TryDecodeRgbaFloat(png, out _, out _, out _, out _, out var m, out var c).ShouldBeTrue();

        m.ShouldBe(mdcv);
        c.ShouldBe(clli);
    }

    [Fact]
    public void FiveArgumentOverload_DecodesTheSame()
    {
        var file = Fixture(PqFr);
        PngImageDecoder.TryDecodeRgbaFloat(file, out var a, out var wa, out var ha, out var ea).ShouldBeTrue();
        PngImageDecoder.TryDecodeRgbaFloat(file, out var b, out var wb, out var hb, out var eb, out _, out _).ShouldBeTrue();

        (wa, ha).ShouldBe((wb, hb));
        ea.ShouldBe(eb);
        a.ShouldBe(b);
    }

    // ---- the RGBA8 paths keep refusing what this one decodes --------------------------------

    [Theory]
    [InlineData(PqFr)]
    [InlineData(HlgFr)]
    [InlineData(HlgNr)]
    public void PqAndHlg_AreRefusedByTheRgba8PathsButDecodedAsFloat(string name)
    {
        var file = Fixture(name);

        PngImageDecoder.TryDecodeRgba8(file, out _, out _, out _).ShouldBeFalse();
        PngImageDecoder.TryDecodeIntoRgba8(file, new byte[1920 * 1080 * 4]).ShouldBeFalse();
        PngImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out _, out _, out _).ShouldBeTrue();
        rgba.ShouldNotBeNull();
    }

    // ---- synthetic images: each colour type, depth and tag --------------------------------

    private static readonly byte[] RgbaBytes =
    [
        0, 0, 0, 255,
        255, 255, 255, 255,
        128, 64, 32, 128,
        10, 200, 255, 0,
    ];

    private static void ShouldBeSrgbRgba8(float[] rgba, byte[] expectedRgba8)
    {
        rgba.Length.ShouldBe(expectedRgba8.Length);
        for (var i = 0; i < expectedRgba8.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
                Close(rgba[i + c], RefSrgb(expectedRgba8[i + c] / 255.0), 1e-6, $"sample {i + c}");
            rgba[i + 3].ShouldBe(expectedRgba8[i + 3] / 255f);
        }
    }

    [Fact]
    public void Rgba8Srgb_TaggedAndUntagged_LineariseWithTheSrgbCurve()
    {
        var untagged = PngWriter.Encode(RgbaBytes, 4, 1);
        var tagged = PngWriter.Encode(RgbaBytes, 4, 1, new PngWriteOptions { Cicp = CicpChunk.Srgb });
        var srgbChunk = PngWriter.Encode(RgbaBytes, 4, 1, new PngWriteOptions { SrgbRenderingIntent = 0 });

        foreach (var file in new[] { untagged, tagged, srgbChunk })
        {
            PngImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out var w, out var h, out var enc, out var mdcv, out var clli).ShouldBeTrue();

            (w, h).ShouldBe((4, 1));
            ShouldBeSrgbRgba8(rgba, RgbaBytes);
            enc.ShouldBe(new ColorEncoding { Primaries = ColorPrimaries.BT709, Transfer = TransferFunction.Linear, FullRange = true, Float = FloatSemantics.DisplayReferred });
            mdcv.ShouldBeNull();
            clli.ShouldBeNull();
        }
    }

    [Fact]
    public void Rgba8_AlphaIsLinearAndStraight_NotLinearisedOrPremultiplied()
    {
        PngImageDecoder.TryDecodeRgbaFloat(PngWriter.Encode(RgbaBytes, 4, 1), out var rgba, out _, out _, out _).ShouldBeTrue();

        // Pixel 2: alpha 128 -> 128/255 as it is, colour untouched by it.
        rgba[2 * 4 + 3].ShouldBe(128f / 255f);
        Close(rgba[2 * 4], RefSrgb(128 / 255.0), 1e-6, "R of the half-transparent pixel");
        // Pixel 3: fully transparent keeps its colour.
        rgba[3 * 4 + 3].ShouldBe(0f);
        Close(rgba[3 * 4 + 2], 1.0, 1e-6, "B of the transparent pixel");
    }

    [Fact]
    public void Result_IsAFreshArrayEveryTime()
    {
        var png = PngWriter.Encode(RgbaBytes, 4, 1);
        PngImageDecoder.TryDecodeRgbaFloat(png, out var a, out _, out _, out _).ShouldBeTrue();
        PngImageDecoder.TryDecodeRgbaFloat(png, out var b, out _, out _, out _).ShouldBeTrue();

        a.ShouldNotBeSameAs(b);
        a[0] = 123f;
        b[0].ShouldBe(0f);
    }

    [Fact]
    public void GammaOne_ReadsAsLinear_SoCodesPassThrough()
    {
        var png = PngWriter.Encode(RgbaBytes, 4, 1, new PngWriteOptions { Gamma = 1.0 });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out var enc).ShouldBeTrue();

        for (var i = 0; i < RgbaBytes.Length; i++)
            rgba[i].ShouldBe(RgbaBytes[i] / 255f);
        enc.Transfer.ShouldBe(TransferFunction.Linear);
        enc.Primaries.ShouldBe(ColorPrimaries.BT709);
    }

    [Fact]
    public void ExplicitLinearCicp_PassesCodesThrough()
    {
        var cicp = new CicpChunk(ColorPrimaries.BT709, TransferFunction.Linear, MatrixCoefficients.Identity, true);
        var png = PngWriter.EncodeGray16([0, 16384, 65535], 3, 1, new PngWriteOptions { Cicp = cicp });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeTrue();

        PixelAt(rgba, 3, 1, 0).ShouldBe((16384 / 65535f, 16384 / 65535f, 16384 / 65535f, 1f));
        PixelAt(rgba, 3, 2, 0).ShouldBe((1f, 1f, 1f, 1f));
    }

    [Fact]
    public void Gray16Hlg_ReplicatesIntoRgbThroughTheGreyOotf()
    {
        ushort[] codes = [0, 24837, 49151, 65535];
        var png = PngWriter.EncodeGray16(codes, 4, 1, new PngWriteOptions { Cicp = CicpChunk.Bt2020Hlg });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out var enc).ShouldBeTrue();

        for (var x = 0; x < codes.Length; x++)
        {
            var e = RefHlgScene(codes[x] / 65535.0);
            var expected = RefHlgDisplayNits(e, e, e).R / RefWhiteNits;
            var px = PixelAt(rgba, 4, x, 0);
            Close(px.R, expected, 1e-4, $"grey {codes[x]}");
            px.G.ShouldBe(px.R);
            px.B.ShouldBe(px.R);
            px.A.ShouldBe(1f);
        }
        enc.Primaries.ShouldBe(ColorPrimaries.BT2020);
    }

    [Fact]
    public void Rgb16Hlg_CouplesTheChannelsThroughLuminance()
    {
        // A saturated red is not R * (grey gain): the OOTF's gain is a function of the pixel's luminance.
        ushort[] rgb = [65535, 0, 0, 49151, 32767, 16384, 0, 0, 65535];
        var png = PngWriter.EncodeRgb16(rgb, 3, 1, new PngWriteOptions { Cicp = CicpChunk.Bt2020Hlg });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeTrue();

        for (var x = 0; x < 3; x++)
        {
            var e = (R: RefHlgScene(rgb[x * 3] / 65535.0), G: RefHlgScene(rgb[x * 3 + 1] / 65535.0), B: RefHlgScene(rgb[x * 3 + 2] / 65535.0));
            var d = RefHlgDisplayNits(e.R, e.G, e.B);
            var px = PixelAt(rgba, 3, x, 0);
            Close(px.R, d.R / RefWhiteNits, 1e-4, $"R of pixel {x}");
            Close(px.G, d.G / RefWhiteNits, 1e-4, $"G of pixel {x}");
            Close(px.B, d.B / RefWhiteNits, 1e-4, $"B of pixel {x}");
        }
        // The red pixel's luminance is 0.2627 of its scene light, so its gain is 0.2627^0.2 (about 0.77x) of the white gain.
        var red = PixelAt(rgba, 3, 0, 0);
        var white = 1000.0 * Math.Pow(RefHlgScene(1.0), 1.2) / RefWhiteNits;
        red.R.ShouldBeLessThan((float)white);
    }

    [Fact]
    public void Rgba16Pq_NarrowRange_ExpandsColourButNotAlpha()
    {
        // Colour at the narrow-range footroom, mid and nominal peak; alpha spanning its own full range.
        ushort[] rgba16 =
        [
            4096, 4096, 4096, 0,
            32128, 32128, 32128, 32768,
            60160, 60160, 60160, 65535,
            0, 65535, 4095, 65535,        // below footroom, far above headroom, one below footroom
        ];
        var cicp = new CicpChunk(ColorPrimaries.BT2020, TransferFunction.Pq, MatrixCoefficients.Identity, false);
        var png = PngWriter.EncodeRgba16(rgba16, 4, 1, new PngWriteOptions { Cicp = cicp });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeTrue();

        PixelAt(rgba, 4, 0, 0).ShouldBe((0f, 0f, 0f, 0f));
        var mid = PixelAt(rgba, 4, 1, 0);
        // (32128 - 4096) / 56064 = 0.5 exactly: PQ 50 % is 92.245 cd/m2.
        Close(mid.R, RefPqNits(0.5) / RefWhiteNits, 1e-4, "mid");
        mid.A.ShouldBe(32768 / 65535f);
        var peak = PixelAt(rgba, 4, 2, 0);
        Close(peak.R, 10000.0 / 203.0, 1e-5, "nominal peak");
        peak.A.ShouldBe(1f);
        // Clamped at both ends before the curve.
        var clamped = PixelAt(rgba, 4, 3, 0);
        clamped.R.ShouldBe(0f);
        Close(clamped.G, 10000.0 / 203.0, 1e-5, "headroom clamps to peak");
        clamped.B.ShouldBe(0f);
    }

    [Fact]
    public void Gray8_NarrowRange_Bt709()
    {
        var cicp = new CicpChunk(ColorPrimaries.BT709, TransferFunction.BT709, MatrixCoefficients.Identity, false);
        byte[] gray = [0, 15, 16, 126, 235, 255];
        var png = PngWriter.EncodeGray8(gray, gray.Length, 1, new PngWriteOptions { Cicp = cicp });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeTrue();

        // The reference uses the constants BT.709 prints (1.099, 0.018); the decoder the full-precision
        // solution of the same curve. Away from the knee they agree to well under 1e-3 relative.
        var expected = gray.Select(g => RefBt709(Clamp01(RefSignal(g, 8, false)))).ToArray();
        for (var x = 0; x < gray.Length; x++)
        {
            var px = PixelAt(rgba, gray.Length, x, 0);
            Close(px.R, expected[x], 1e-3, $"code {gray[x]}");
            (px.G, px.B, px.A).ShouldBe((px.R, px.R, 1f));
        }
        // Footroom and below are 0; 235 and above are SDR white.
        (rgba[0], rgba[4], rgba[8]).ShouldBe((0f, 0f, 0f));
        (rgba[16], rgba[20]).ShouldBe((1f, 1f));
    }

    [Fact]
    public void LinearNarrowRange_IsNotClamped()
    {
        // Linear is the one curve that passes values outside [0, 1] through: below footroom is negative,
        // above headroom exceeds 1 (a consumer that wants them clamped can).
        var cicp = new CicpChunk(ColorPrimaries.BT709, TransferFunction.Linear, MatrixCoefficients.Identity, false);
        byte[] gray = [0, 16, 235, 255];
        var png = PngWriter.EncodeGray8(gray, 4, 1, new PngWriteOptions { Cicp = cicp });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeTrue();

        rgba[0].ShouldBe(-16f / 219f, 1e-6f);
        rgba[4].ShouldBe(0f);
        rgba[8].ShouldBe(1f, 1e-6f);
        rgba[12].ShouldBe(239f / 219f, 1e-6f);
    }

    [Fact]
    public void GrayAlpha8_And_GrayAlpha16_Decode()
    {
        // 8-bit: (grey 128, alpha 64) and (grey 255, alpha 255).
        var png8 = BuildPng(2, 1, 8, 4, [128, 64, 255, 255]);
        PngImageDecoder.TryDecodeRgbaFloat(png8, out var rgba8, out _, out _, out _).ShouldBeTrue();
        var g = RefSrgb(128 / 255.0);
        Close(rgba8[0], g, 1e-6, "R");
        (rgba8[1], rgba8[2]).ShouldBe((rgba8[0], rgba8[0]));
        rgba8[3].ShouldBe(64 / 255f);
        PixelAt(rgba8, 2, 1, 0).ShouldBe((1f, 1f, 1f, 1f));

        // 16-bit PQ: (grey 38010, alpha 1000).
        var png16 = BuildPng(1, 1, 16, 4, [0x94, 0x7A, 0x03, 0xE8],
            chunks: [("cICP", [(byte)ColorPrimaries.BT2020, (byte)TransferFunction.Pq, 0, 1])]);
        PngImageDecoder.TryDecodeRgbaFloat(png16, out var rgba16, out _, out _, out _).ShouldBeTrue();
        Close(rgba16[0], RefPqNits(38010 / 65535.0) / RefWhiteNits, 1e-4, "PQ grey");
        (rgba16[1], rgba16[2]).ShouldBe((rgba16[0], rgba16[0]));
        rgba16[3].ShouldBe(1000 / 65535f);
    }

    [Fact]
    public void Rgb8_HasOpaqueAlpha()
    {
        PngImageDecoder.TryDecodeRgbaFloat(PngWriter.EncodeRgb8([255, 0, 0, 0, 128, 255], 2, 1), out var rgba, out _, out _, out _).ShouldBeTrue();

        PixelAt(rgba, 2, 0, 0).ShouldBe((1f, 0f, 0f, 1f));
        var px = PixelAt(rgba, 2, 1, 0);
        px.R.ShouldBe(0f);
        Close(px.G, RefSrgb(128 / 255.0), 1e-6, "G");
        (px.B, px.A).ShouldBe((1f, 1f));
    }

    [Fact]
    public void Indexed_ExpandsThroughThePaletteWithTrnsAlpha()
    {
        byte[] palette = [0, 0, 0, 255, 255, 255, 128, 64, 32];
        byte[] trns = [255, 128];   // entry 2 is past its end: opaque
        var png = BuildPng(4, 1, 8, 3, [0, 1, 2, 1], palette, trns);

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out var w, out var h, out var enc).ShouldBeTrue();

        (w, h).ShouldBe((4, 1));
        PixelAt(rgba, 4, 0, 0).ShouldBe((0f, 0f, 0f, 1f));
        PixelAt(rgba, 4, 1, 0).ShouldBe((1f, 1f, 1f, 128 / 255f));
        var third = PixelAt(rgba, 4, 2, 0);
        Close(third.R, RefSrgb(128 / 255.0), 1e-6, "R");
        Close(third.G, RefSrgb(64 / 255.0), 1e-6, "G");
        Close(third.B, RefSrgb(32 / 255.0), 1e-6, "B");
        third.A.ShouldBe(1f);
        PixelAt(rgba, 4, 3, 0).ShouldBe(PixelAt(rgba, 4, 1, 0));
        enc.Transfer.ShouldBe(TransferFunction.Linear);
    }

    [Fact]
    public void Indexed_HlgPaletteIsLinearisedPerEntry()
    {
        // 16-bit PNG cannot be indexed; the palette is 8-bit and goes through the 8-bit expansion.
        byte[] palette = [255, 0, 0, 191, 128, 64];
        var png = BuildPng(2, 1, 8, 3, [0, 1], palette,
            chunks: [("cICP", [(byte)ColorPrimaries.BT2020, (byte)TransferFunction.Hlg, 0, 1])]);

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeTrue();

        for (var x = 0; x < 2; x++)
        {
            var d = RefHlgDisplayNits(RefHlgScene(palette[x * 3] / 255.0), RefHlgScene(palette[x * 3 + 1] / 255.0), RefHlgScene(palette[x * 3 + 2] / 255.0));
            var px = PixelAt(rgba, 2, x, 0);
            Close(px.R, d.R / RefWhiteNits, 1e-4, $"R {x}");
            Close(px.G, d.G / RefWhiteNits, 1e-4, $"G {x}");
            Close(px.B, d.B / RefWhiteNits, 1e-4, $"B {x}");
        }
    }

    [Fact]
    public void SubByteGrey_IsScaledByTheReaderAndThenLinearised()
    {
        // 4-bit grey, 3 pixels: 0, 5, 15 -> packed 0x05 0xF0. The reader unpacks to 8-bit 0, 85, 255.
        var png = BuildPng(3, 1, 4, 0, [0x05, 0xF0]);

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeTrue();

        rgba[0].ShouldBe(0f);
        Close(rgba[4], RefSrgb(85 / 255.0), 1e-6, "level 5 of 15");
        rgba[8].ShouldBe(1f);
    }

    [Fact]
    public void SubByteGrey_NarrowRange_IsRefused()
    {
        // H.273's narrow-range equations start at 8 bits; a 4-bit grey scaled to 0-255 is not such a code.
        var png = BuildPng(3, 1, 4, 0, [0x05, 0xF0],
            chunks: [("cICP", [(byte)ColorPrimaries.BT709, (byte)TransferFunction.BT709, 0, 0])]);

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out var w, out var h, out var enc).ShouldBeFalse();

        (rgba, w, h, enc).ShouldBe((null, 0, 0, null));
    }

    // ---- refusals ---------------------------------------------------------------------------

    [Theory]
    [InlineData((byte)TransferFunction.Unspecified)]
    [InlineData((byte)TransferFunction.BT470M)]
    [InlineData((byte)TransferFunction.SMPTE240M)]
    [InlineData((byte)TransferFunction.Log100)]
    [InlineData((byte)TransferFunction.Iec6196624)]
    [InlineData((byte)TransferFunction.BT1361)]
    [InlineData((byte)TransferFunction.SMPTE428)]
    [InlineData((byte)200)]
    public void AnUnsupportedTransfer_IsRefused(byte transfer)
    {
        var cicp = new CicpChunk(ColorPrimaries.BT709, (TransferFunction)transfer, MatrixCoefficients.Identity, true);
        var png = PngWriter.EncodeRgb8([1, 2, 3], 1, 1, new PngWriteOptions { Cicp = cicp });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out var w, out var h, out var enc, out var mdcv, out var clli).ShouldBeFalse();

        (rgba, w, h, enc, mdcv, clli).ShouldBe((null, 0, 0, null, null, null));
    }

    [Theory]
    [InlineData(TransferFunction.Srgb)]
    [InlineData(TransferFunction.Pq)]
    [InlineData(TransferFunction.Hlg)]
    [InlineData(TransferFunction.BT709)]
    [InlineData(TransferFunction.SMPTE170M)]
    [InlineData(TransferFunction.BT2020_10)]
    [InlineData(TransferFunction.BT2020_12)]
    [InlineData(TransferFunction.Linear)]
    public void EverySupportedTransfer_Decodes(TransferFunction transfer)
    {
        var cicp = new CicpChunk(ColorPrimaries.BT2020, transfer, MatrixCoefficients.Identity, true);
        var png = PngWriter.EncodeRgb8([1, 2, 3], 1, 1, new PngWriteOptions { Cicp = cicp });

        PngImageDecoder.TryDecodeRgbaFloat(png, out _, out _, out _, out var enc).ShouldBeTrue();

        enc.Transfer.ShouldBe(TransferFunction.Linear);
    }

    [Fact]
    public void ANonIdentityMatrix_IsRefused()
    {
        // PNG carries RGB; a cICP that says its samples are YCbCr is not something to read as RGB.
        var cicp = new CicpChunk(ColorPrimaries.BT709, TransferFunction.Srgb, MatrixCoefficients.BT709, true);
        var png = PngWriter.EncodeRgb8([1, 2, 3], 1, 1, new PngWriteOptions { Cicp = cicp });

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out _, out _, out _).ShouldBeFalse();
        rgba.ShouldBeNull();
    }

    [Fact]
    public void AnIndexedPixelPastThePalette_IsRefused()
    {
        var png = BuildPng(2, 1, 8, 3, [0, 3], [0, 0, 0, 255, 255, 255]);

        PngImageDecoder.TryDecodeRgbaFloat(png, out var rgba, out var w, out var h, out var enc).ShouldBeFalse();

        (rgba, w, h, enc).ShouldBe((null, 0, 0, null));
    }

    [Fact]
    public void AnIndexedImageWithNoPalette_IsRefused()
    {
        PngImageDecoder.TryDecodeRgbaFloat(BuildPng(2, 1, 8, 3, [0, 0]), out var rgba, out _, out _, out _).ShouldBeFalse();
        rgba.ShouldBeNull();
    }

    [Fact]
    public void NotAPng_ResetsEveryOutput()
    {
        PngImageDecoder.TryDecodeRgbaFloat("not a png at all"u8, out var rgba, out var w, out var h, out var enc, out var mdcv, out var clli).ShouldBeFalse();

        (rgba, w, h, enc, mdcv, clli).ShouldBe((null, 0, 0, null, null, null));
    }

    [Fact]
    public void ATruncatedPng_IsFalseRatherThanThrowing()
    {
        var png = PngWriter.Encode(RgbaBytes, 4, 1);

        // Cut mid-IDAT, so a chunk runs past the end of the file.
        PngImageDecoder.TryDecodeRgbaFloat(png.AsSpan(0, png.Length - 20), out var rgba, out _, out _, out _).ShouldBeFalse();
        rgba.ShouldBeNull();
    }

    [Fact]
    public void Adam7_DecodesToTheSameFloatsAsTheNonInterlacedFile()
    {
        // The reader de-interlaces since 3.25, so the float projection no longer refuses an Adam7 file:
        // it must read exactly what the non-interlaced encoding of the same raster gives.
        var rgb = new byte[16 * 16 * 3];
        new Random(7).NextBytes(rgb);
        byte[] Encode(Interlace interlace)
        {
            using var img = new MagickImage();
            img.ReadPixels(rgb, new PixelReadSettings(16, 16, StorageType.Char, "RGB"));
            img.Settings.Interlace = interlace;
            return img.ToByteArray(MagickFormat.Png);
        }

        PngImageDecoder.TryDecodeRgbaFloat(Encode(Interlace.NoInterlace), out var plain, out var pw, out var ph, out _).ShouldBeTrue();
        PngImageDecoder.TryDecodeRgbaFloat(Encode(Interlace.Png), out var adam7, out var aw, out var ah, out _).ShouldBeTrue();

        (aw, ah).ShouldBe((pw, ph));
        adam7.ShouldBe(plain);
    }

    // ---- a minimal PNG builder (the writer has no grey+alpha, indexed or sub-byte output) ---------

    // Signature + IHDR + any extra chunks + PLTE + tRNS + IDAT (filter 0 on every row) + IEND, every chunk
    // with a correct CRC. rows is the raw sample bytes with each row already packed to a whole number of
    // bytes, and no filter byte.
    private static byte[] BuildPng(int w, int h, int bitDepth, int colorType, byte[] rows, byte[]? palette = null, byte[]? trns = null,
        (string Type, byte[] Data)[]? chunks = null)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        WriteChunk(ms, "IHDR"u8, ihdr);

        foreach (var (type, data) in chunks ?? [])
            WriteChunk(ms, System.Text.Encoding.ASCII.GetBytes(type), data);
        if (palette is not null) WriteChunk(ms, "PLTE"u8, palette);
        if (trns is not null) WriteChunk(ms, "tRNS"u8, trns);

        var rowBytes = rows.Length / h;
        var raw = new byte[(rowBytes + 1) * h];
        for (var y = 0; y < h; y++)
            Array.Copy(rows, y * rowBytes, raw, y * (rowBytes + 1) + 1, rowBytes);
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        WriteChunk(ms, "IDAT"u8, deflated.ToArray());
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        s.Write(word);
        s.Write(type);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFFu);
        s.Write(word);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
