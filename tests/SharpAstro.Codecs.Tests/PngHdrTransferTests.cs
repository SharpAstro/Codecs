using SharpAstro.Codecs.Abstractions;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The transfer and range maths behind the PNG float projection (<see cref="PngHdr"/>): PQ, HLG
/// (inverse OETF + OOTF), sRGB / BT.709 linearisation and H.273 narrow-range expansion.
/// </summary>
/// <remarks>
/// <para>Two kinds of check, and they are kept apart. <b>Anchor points</b> are numbers the
/// standards (or BT.2408) state outright - PQ 1.0 = 10 000 cd/m², HLG signal ½ = 1/12 scene light,
/// the sRGB knee at 0.04045, narrow range 16..235 - so a wrong constant cannot hide behind a
/// self-consistent implementation. <b>Round trips</b> run each decoder against the test's own
/// <i>forward</i> transform written from the same formulas the other way round; they catch an
/// algebra slip in the inverse, and nothing about a constant both directions share, which is why the
/// anchors exist.</para>
/// <para>The curves are checked as monotone over a dense sweep because a transposed term in
/// these formulas tends to produce a curve that is right at the anchors and wrong - often
/// non-monotone - between them.</para>
/// </remarks>
public sealed class PngHdrTransferTests
{
    // ---- independent forward transforms (the encoders the decoders must invert) ------------

    // ST 2084 inverse EOTF, nits -> signal. N = ((c1 + c2 Y^m1) / (1 + c3 Y^m1))^m2, Y = nits / 10000.
    private static double PqForward(double nits)
    {
        const double m1 = 2610.0 / 16384.0, m2 = 2523.0 / 4096.0 * 128.0;
        const double c1 = 3424.0 / 4096.0, c2 = 2413.0 / 4096.0 * 32.0, c3 = 2392.0 / 4096.0 * 32.0;
        double y = Math.Pow(nits / 10000.0, m1);
        return Math.Pow((c1 + c2 * y) / (1.0 + c3 * y), m2);
    }

    // BT.2100 HLG OETF, scene light E -> signal.
    private static double HlgForward(double e)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        return e <= 1.0 / 12.0 ? Math.Sqrt(3.0 * e) : a * Math.Log(12.0 * e - b) + c;
    }

    private static double SrgbForward(double l) =>
        l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;

    private static double Bt709Forward(double l)
    {
        const double alpha = 1.0992968268094429, beta = 0.018053968510807;
        return l < beta ? 4.5 * l : alpha * Math.Pow(l, 0.45) - (alpha - 1.0);
    }

    // ---- PQ -------------------------------------------------------------------------------

    [Fact]
    public void Pq_EndpointsAreExact()
    {
        PngHdr.PqInverseEotf(0f).ShouldBe(0f);
        PngHdr.PqInverseEotf(1f).ShouldBe(10000f);
    }

    [Fact]
    public void Pq_100Nits_IsCode0_5081()
    {
        // ST 2084's published code value for 100 cd/m2 is 0.5081 (4 decimals).
        PngHdr.PqInverseEotf(0.5081f).ShouldBe(100f, 0.1);
        PqForward(100.0).ShouldBe(0.5081, 5e-5);
    }

    [Fact]
    public void Pq_ReferenceWhite203Nits_IsCode0_5807()
    {
        // BT.2408 puts HDR reference white at 203 cd/m2, ~58% PQ.
        PqForward(203.0).ShouldBe(0.5807, 5e-5);
        PngHdr.PqInverseEotf(0.5807f).ShouldBe(203f, 0.5);
    }

    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.01)]
    [InlineData(1.0)]
    [InlineData(100.0)]
    [InlineData(203.0)]
    [InlineData(1000.0)]
    [InlineData(4000.0)]
    [InlineData(10000.0)]
    public void Pq_RoundTripsThroughTheForwardTransform(double nits)
    {
        var code = (float)PqForward(nits);
        // Float code precision (6e-8 relative) is amplified by the curve's steepness at the top
        // and bottom; a 0.1% relative bound is far tighter than any display step and still ample.
        PngHdr.PqInverseEotf(code).ShouldBe((float)nits, nits * 1e-3 + 1e-6);
    }

    [Fact]
    public void Pq_IsStrictlyIncreasingAndBounded()
    {
        float previous = -1f;
        for (int code = 0; code <= 4095; code++)
        {
            float nits = PngHdr.PqInverseEotf(code / 4095f);
            nits.ShouldBeGreaterThan(previous, $"code {code}");
            nits.ShouldBeInRange(0f, 10000f);
            previous = nits;
        }
    }

    [Fact]
    public void Pq_ClampsItsDomain()
    {
        PngHdr.PqInverseEotf(-0.25f).ShouldBe(0f);
        PngHdr.PqInverseEotf(1.0913f).ShouldBe(10000f);
    }

    // ---- HLG ------------------------------------------------------------------------------

    [Fact]
    public void Hlg_InverseOetf_HalfSignalIsOneTwelfthSceneLight()
    {
        // BT.2100 Table 5: the quadratic segment ends at E' = 1/2, where E = (1/2)^2 / 3 = 1/12.
        PngHdr.HlgInverseOetf(0.5f).ShouldBe(1f / 12f, 1e-7);
        PngHdr.HlgInverseOetf(0f).ShouldBe(0f);
    }

    [Fact]
    public void Hlg_InverseOetf_FullSignalIsFullSceneLight()
    {
        // The printed constants (8 digits) make OETF(1) = 1 only to ~2e-4.
        PngHdr.HlgInverseOetf(1f).ShouldBe(1f, 5e-4);
    }

    [Fact]
    public void Hlg_InverseOetf_IsContinuousAtTheKnee()
    {
        // The exp branch evaluated just above 1/2 must meet the quadratic branch at 1/12.
        PngHdr.HlgInverseOetf(0.5000001f).ShouldBe(1f / 12f, 1e-5);
    }

    [Fact]
    public void Hlg_InverseOetf_InvertsTheForwardOetf()
    {
        // Constants are printed to 8 digits, so the two directions agree to about 1e-6 of scene light.
        for (double e = 0.0; e <= 1.0; e += 1.0 / 256)
            PngHdr.HlgInverseOetf((float)HlgForward(e)).ShouldBe((float)e, 1e-5, $"E = {e}");
    }

    [Fact]
    public void Hlg_InverseOetf_IsStrictlyIncreasing()
    {
        float previous = -1f;
        for (int code = 0; code <= 1023; code++)
        {
            float e = PngHdr.HlgInverseOetf(code / 1023f);
            e.ShouldBeGreaterThan(previous, $"code {code}");
            previous = e;
        }
    }

    [Fact]
    public void Hlg_Ootf_ReferenceWhiteSignal_IsAbout203NitsOnTheNominalDisplay()
    {
        // BT.2408: 75% HLG is the 203 cd/m2 reference white on a 1000 cd/m2 display. Grey, so Ys = E.
        float scene = PngHdr.HlgInverseOetf(0.75f);
        scene.ShouldBe(0.265f, 1e-3);
        PngHdr.HlgOotfGrey(scene).ShouldBe(203f, 1.0);

        // ...and the RGB form agrees for R = G = B (the weights sum to 1).
        var (r, g, b) = PngHdr.HlgOotf(scene, scene, scene, LumaWeights.Bt2020);
        r.ShouldBe(203f, 1.0);
        g.ShouldBe(r, 1e-3);
        b.ShouldBe(r, 1e-3);
    }

    [Fact]
    public void Hlg_Ootf_FullWhiteIsThePeakLuminance()
    {
        // Ys = 1 so the gain is just L_W.
        var (r, g, b) = PngHdr.HlgOotf(1f, 1f, 1f, LumaWeights.Bt2020);
        r.ShouldBe(1000f, 0.1);
        g.ShouldBe(1000f, 0.1);
        b.ShouldBe(1000f, 0.1);
        PngHdr.HlgOotfGrey(1f).ShouldBe(1000f);
        PngHdr.HlgOotfGrey(0f).ShouldBe(0f);
    }

    [Fact]
    public void Hlg_Ootf_AppliesOneLuminanceGainToAllThreeChannels()
    {
        // F_D = L_W * Ys^(gamma-1) * E_S per channel, Ys from the primaries' Y weights.
        const float rs = 0.20f, gs = 0.10f, bs = 0.05f;
        var w = LumaWeights.Bt2020;
        double ys = 0.2627 * rs + 0.6780 * gs + 0.0593 * bs;
        double gain = 1000.0 * Math.Pow(ys, 0.2);

        var (r, g, b) = PngHdr.HlgOotf(rs, gs, bs, w);

        r.ShouldBe((float)(gain * rs), 1e-3);
        g.ShouldBe((float)(gain * gs), 1e-3);
        b.ShouldBe((float)(gain * bs), 1e-3);
        // One gain for all channels, so the ratios between them are those of the scene light.
        (r / g).ShouldBe(rs / gs, 1e-4);
        (g / b).ShouldBe(gs / bs, 1e-4);
    }

    [Fact]
    public void Hlg_Ootf_LuminanceComesFromThePrimariesWeights()
    {
        // Pure red: Ys is 0.2126 under BT.709 and 0.2627 under BT.2020, and the gain is Ys^0.2.
        var (r709, _, _) = PngHdr.HlgOotf(1f, 0f, 0f, LumaWeights.Bt709);
        var (r2020, _, _) = PngHdr.HlgOotf(1f, 0f, 0f, LumaWeights.Bt2020);

        r709.ShouldBe((float)(1000.0 * Math.Pow(0.2126, 0.2)), 1e-2);
        r2020.ShouldBe((float)(1000.0 * Math.Pow(0.2627, 0.2)), 1e-2);
        r2020.ShouldBeGreaterThan(r709);
    }

    [Fact]
    public void Hlg_Ootf_BlackStaysBlackWithoutNaN()
    {
        var (r, g, b) = PngHdr.HlgOotf(0f, 0f, 0f, LumaWeights.Bt709);
        (r, g, b).ShouldBe((0f, 0f, 0f));
    }

    [Fact]
    public void Hlg_Ootf_IsMonotoneInSceneLight()
    {
        float previous = -1f;
        for (int i = 0; i <= 1000; i++)
        {
            float e = i / 1000f;
            var (r, _, _) = PngHdr.HlgOotf(e, 0.5f * e, 0.25f * e, LumaWeights.Bt2020);
            r.ShouldBeGreaterThanOrEqualTo(previous, $"E = {e}");
            previous = r;
        }
    }

    // ---- luminance weights ----------------------------------------------------------------

    // Y weights of an RGB primaries set, derived from its H.273 §8.1 chromaticities: with each
    // primary's column (x/y, 1, z/y), the scale S_i that makes the three columns sum to the white
    // point's XYZ is that primary's Y contribution. Cramer's rule on the 3x3 system.
    private static (double R, double G, double B) DeriveLumaWeights(
        (double x, double y) r, (double x, double y) g, (double x, double y) b, (double x, double y) w)
    {
        double[] cr = [r.x / r.y, 1.0, (1.0 - r.x - r.y) / r.y];
        double[] cg = [g.x / g.y, 1.0, (1.0 - g.x - g.y) / g.y];
        double[] cb = [b.x / b.y, 1.0, (1.0 - b.x - b.y) / b.y];
        double[] white = [w.x / w.y, 1.0, (1.0 - w.x - w.y) / w.y];

        static double Det(double[] c0, double[] c1, double[] c2) =>
            c0[0] * (c1[1] * c2[2] - c2[1] * c1[2])
            - c1[0] * (c0[1] * c2[2] - c2[1] * c0[2])
            + c2[0] * (c0[1] * c1[2] - c1[1] * c0[2]);

        double d = Det(cr, cg, cb);
        return (Det(white, cg, cb) / d, Det(cr, white, cb) / d, Det(cr, cg, white) / d);
    }

    [Fact]
    public void LumaWeights_MatchTheH273ChromaticitiesTheyAreDerivedFrom()
    {
        var d65 = (0.3127, 0.3290);

        var bt709 = DeriveLumaWeights((0.640, 0.330), (0.300, 0.600), (0.150, 0.060), d65);
        var bt2020 = DeriveLumaWeights((0.708, 0.292), (0.170, 0.797), (0.131, 0.046), d65);
        var p3 = DeriveLumaWeights((0.680, 0.320), (0.265, 0.690), (0.150, 0.060), d65);

        AssertWeights(LumaWeights.Bt709, bt709);
        AssertWeights(LumaWeights.Bt2020, bt2020);
        AssertWeights(LumaWeights.DisplayP3, p3);

        // The two the task names are the standards' own numbers.
        LumaWeights.Bt2020.ShouldBe(new LumaWeights(0.2627f, 0.6780f, 0.0593f));
        LumaWeights.Bt709.ShouldBe(new LumaWeights(0.2126f, 0.7152f, 0.0722f));

        static void AssertWeights(LumaWeights actual, (double R, double G, double B) derived)
        {
            // The standards print 4 decimals; the derivation from 3-4 decimal chromaticities agrees to ~1e-4.
            actual.R.ShouldBe((float)derived.R, 2e-4);
            actual.G.ShouldBe((float)derived.G, 2e-4);
            actual.B.ShouldBe((float)derived.B, 2e-4);
            (actual.R + actual.G + actual.B).ShouldBe(1f, 1e-6);
        }
    }

    [Theory]
    [InlineData(ColorPrimaries.BT2020, 0.2627f)]
    [InlineData(ColorPrimaries.BT709, 0.2126f)]
    [InlineData(ColorPrimaries.DisplayP3, 0.2290f)]
    [InlineData(ColorPrimaries.Unspecified, 0.2126f)]   // decoders read Unspecified as BT.709
    [InlineData(ColorPrimaries.SMPTE170M, 0.2126f)]     // no table of its own: BT.709 fallback
    public void LumaWeights_ForPrimaries_PicksTheRedWeight(ColorPrimaries primaries, float expectedRed) =>
        LumaWeights.For(primaries).R.ShouldBe(expectedRed);

    // ---- sRGB / BT.709 --------------------------------------------------------------------

    [Fact]
    public void Srgb_KneeIsAt0_04045_AndTheTwoSegmentsMeet()
    {
        const double knee = 0.04045;
        double linearSegment = knee / 12.92;
        double powerSegment = Math.Pow((knee + 0.055) / 1.055, 2.4);

        // The segments are continuous to ~1e-6 at the knee (that is how the constants were chosen)...
        linearSegment.ShouldBe(powerSegment, 1e-6);
        // ...and the function is the linear one at and below the knee, the power one above it.
        PngHdr.SrgbToLinear((float)knee).ShouldBe((float)linearSegment, 1e-7);
        PngHdr.SrgbToLinear(0.02f).ShouldBe(0.02f / 12.92f, 1e-8);
        PngHdr.SrgbToLinear(0.05f).ShouldBe((float)Math.Pow((0.05 + 0.055) / 1.055, 2.4), 1e-7);
        PngHdr.SrgbToLinear(0.0405f).ShouldBe((float)Math.Pow((0.0405 + 0.055) / 1.055, 2.4), 1e-7);
    }

    [Fact]
    public void Srgb_EndpointsAndMidpoint()
    {
        PngHdr.SrgbToLinear(0f).ShouldBe(0f);
        PngHdr.SrgbToLinear(1f).ShouldBe(1f, 1e-7);
        PngHdr.SrgbToLinear(0.5f).ShouldBe(0.21404f, 1e-5);   // the familiar 188-in-8-bit-sRGB = 50% grey figure
        PngHdr.SrgbToLinear(-0.1f).ShouldBe(0f);
        PngHdr.SrgbToLinear(1.2f).ShouldBe(1f, 1e-7);
    }

    [Fact]
    public void Srgb_InvertsTheForwardTransformAndIsMonotone()
    {
        float previous = -1f;
        for (int code = 0; code <= 255; code++)
        {
            float v = code / 255f;
            float linear = PngHdr.SrgbToLinear(v);
            linear.ShouldBeGreaterThan(previous, $"code {code}");
            SrgbForward(linear).ShouldBe(v, 1e-6, $"code {code}");
            previous = linear;
        }
    }

    [Fact]
    public void Bt709_EndpointsKneeAndGreyCard()
    {
        PngHdr.Bt709ToLinear(0f).ShouldBe(0f);
        PngHdr.Bt709ToLinear(1f).ShouldBe(1f, 1e-6);

        // Encoded knee 4.5 * beta: below it the curve is v / 4.5, above it the power segment, and
        // the two meet (value and slope are what fix alpha and beta).
        const double knee = 4.5 * 0.018053968510807;
        PngHdr.Bt709ToLinear((float)(knee - 1e-4)).ShouldBe((float)((knee - 1e-4) / 4.5), 1e-7);
        PngHdr.Bt709ToLinear((float)(knee + 1e-4)).ShouldBe(0.018053968510807f, 1e-4);
        PngHdr.Bt709ToLinear((float)knee).ShouldBe(0.018053968510807f, 1e-6);

        // The 18% grey card's well-known BT.709 signal level is ~0.409.
        PngHdr.Bt709ToLinear(0.4089f).ShouldBe(0.18f, 5e-4);
    }

    [Fact]
    public void Bt709_InvertsTheForwardTransformAndIsMonotone()
    {
        float previous = -1f;
        for (int code = 0; code <= 1023; code++)
        {
            float v = code / 1023f;
            float linear = PngHdr.Bt709ToLinear(v);
            linear.ShouldBeGreaterThan(previous, $"code {code}");
            Bt709Forward(linear).ShouldBe(v, 1e-6, $"code {code}");
            previous = linear;
        }
    }

    // ---- narrow range ---------------------------------------------------------------------

    [Fact]
    public void NarrowRange_8Bit_16Is0_And235Is1()
    {
        PngHdr.CodeToSignal(16, 8, fullRange: false).ShouldBe(0f);
        PngHdr.CodeToSignal(235, 8, fullRange: false).ShouldBe(1f);
        PngHdr.CodeToSignal(126, 8, fullRange: false).ShouldBe(110f / 219f, 1e-7);
    }

    [Fact]
    public void NarrowRange_10Bit_64Is0_And940Is1()
    {
        PngHdr.CodeToSignal(64, 10, fullRange: false).ShouldBe(0f);
        PngHdr.CodeToSignal(940, 10, fullRange: false).ShouldBe(1f);
    }

    [Fact]
    public void NarrowRange_16Bit_4096Is0_And60160Is1_TheTenBitCodesShiftedLeftBySix()
    {
        PngHdr.CodeToSignal(4096, 16, fullRange: false).ShouldBe(0f);
        PngHdr.CodeToSignal(60160, 16, fullRange: false).ShouldBe(1f);

        // 16 << 8 = 64 << 6 and 235 << 8 = 940 << 6: the scaling-to-depth is a plain shift, so a
        // 16-bit file that carries 10-bit video-range content lands on the same signal.
        4096.ShouldBe(64 << 6);
        60160.ShouldBe(940 << 6);
        for (int code10 = 0; code10 <= 1023; code10 += 17)
            PngHdr.CodeToSignal(code10 << 6, 16, fullRange: false)
                .ShouldBe(PngHdr.CodeToSignal(code10, 10, fullRange: false), 1e-6);
    }

    [Fact]
    public void NarrowRange_FootroomAndHeadroomAreNotClamped()
    {
        PngHdr.CodeToSignal(0, 8, fullRange: false).ShouldBe(-16f / 219f, 1e-7);
        PngHdr.CodeToSignal(255, 8, fullRange: false).ShouldBe(239f / 219f, 1e-7);
        PngHdr.CodeToSignal(0, 8, fullRange: false).ShouldBeLessThan(0f);
        PngHdr.CodeToSignal(255, 8, fullRange: false).ShouldBeGreaterThan(1f);
    }

    [Theory]
    [InlineData(8, 255)]
    [InlineData(10, 1023)]
    [InlineData(12, 4095)]
    [InlineData(16, 65535)]
    public void FullRange_SpansZeroToOne(int bitDepth, int maxCode)
    {
        PngHdr.CodeToSignal(0, bitDepth, fullRange: true).ShouldBe(0f);
        PngHdr.CodeToSignal(maxCode, bitDepth, fullRange: true).ShouldBe(1f);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(17, true)]
    [InlineData(7, false)]     // H.273's narrow-range equations start at 8 bits
    [InlineData(17, false)]
    public void CodeToSignal_RejectsBitDepthsOutsideTheDefinedRange(int bitDepth, bool fullRange) =>
        Should.Throw<ArgumentOutOfRangeException>(() => PngHdr.CodeToSignal(0, bitDepth, fullRange));

    [Fact]
    public void ExpandCodes_AgreesWithTheScalarForm()
    {
        byte[] bytes = [0, 16, 100, 235, 255];
        ushort[] words = [0, 4096, 30000, 60160, 65535];
        var signal = new float[5];

        PngHdr.ExpandCodes(bytes, fullRange: false, signal);
        for (int i = 0; i < bytes.Length; i++)
            signal[i].ShouldBe(PngHdr.CodeToSignal(bytes[i], 8, fullRange: false));

        PngHdr.ExpandCodes(words, 16, fullRange: true, signal);
        for (int i = 0; i < words.Length; i++)
            signal[i].ShouldBe(PngHdr.CodeToSignal(words[i], 16, fullRange: true));

        PngHdr.ExpandCodes(words, 16, fullRange: false, signal);
        signal[1].ShouldBe(0f);
        signal[3].ShouldBe(1f);

        Should.Throw<ArgumentOutOfRangeException>(() => PngHdr.ExpandCodes(bytes, true, new float[4]));
        Should.Throw<ArgumentOutOfRangeException>(() => PngHdr.ExpandCodes(words, 4, false, new float[5]));
    }

    // ---- ToLinear: the pipeline over pixels -----------------------------------------------

    [Fact]
    public void ToLinear_Pq_ScalesAbsoluteLightToReferenceWhite()
    {
        // grey, one channel per pixel: PQ(203 nits) -> 1.0, PQ(1.0) -> 10000 / 203.
        float[] px = [(float)PqForward(203.0), 1f, 0f];
        PngHdr.ToLinear(TransferFunction.Pq, ColorPrimaries.BT2020, px, colorChannels: 1, stride: 1);

        px[0].ShouldBe(1f, 1e-4);
        px[1].ShouldBe(10000f / 203f, 1e-4);
        px[2].ShouldBe(0f);
    }

    [Fact]
    public void ToLinear_Pq_LeavesAlphaAloneAndClampsHeadroom()
    {
        // RGBA, stride 4: alpha (index 3) is coverage, not light, and must not be curved.
        float[] px = [(float)PqForward(100.0), 1.0913f, -0.1f, 0.5f];
        PngHdr.ToLinear(TransferFunction.Pq, ColorPrimaries.BT2020, px, colorChannels: 3, stride: 4);

        px[0].ShouldBe(100f / 203f, 1e-3);
        px[1].ShouldBe(10000f / 203f, 1e-4);   // narrow-range super-white clamps to peak
        px[2].ShouldBe(0f);                    // footroom clamps to black
        px[3].ShouldBe(0.5f);                  // alpha untouched
    }

    [Fact]
    public void ToLinear_Hlg_75PercentGreyIsReferenceWhite()
    {
        // 75% signal on the nominal display is 203 cd/m2 = 1.0 on this scale (203.1 / 203).
        float[] rgb = [0.75f, 0.75f, 0.75f];
        PngHdr.ToLinear(TransferFunction.Hlg, ColorPrimaries.BT2020, rgb, colorChannels: 3, stride: 3);

        foreach (var v in rgb) v.ShouldBe(1f, 0.01);
    }

    [Fact]
    public void ToLinear_Hlg_FullWhiteIsPeakOverReferenceWhite()
    {
        float[] rgb = [1f, 1f, 1f];
        PngHdr.ToLinear(TransferFunction.Hlg, ColorPrimaries.BT2020, rgb, colorChannels: 3, stride: 3);

        foreach (var v in rgb) v.ShouldBe(1000f / 203f, 0.01);
    }

    [Fact]
    public void ToLinear_Hlg_GreyAndRgbPathsAgree_AndUsePrimariesOnlyForRgb()
    {
        float[] grey = [0.6f, 0.9f];
        PngHdr.ToLinear(TransferFunction.Hlg, ColorPrimaries.BT2020, grey, colorChannels: 1, stride: 1);

        foreach (var primaries in new[] { ColorPrimaries.BT2020, ColorPrimaries.BT709, ColorPrimaries.DisplayP3 })
        {
            float[] rgb = [0.6f, 0.6f, 0.6f, 0.9f, 0.9f, 0.9f];
            PngHdr.ToLinear(TransferFunction.Hlg, primaries, rgb, colorChannels: 3, stride: 3);

            rgb[0].ShouldBe(grey[0], 1e-4);
            rgb[2].ShouldBe(grey[0], 1e-4);
            rgb[3].ShouldBe(grey[1], 1e-3);
            rgb[5].ShouldBe(grey[1], 1e-3);
        }
    }

    [Fact]
    public void ToLinear_Hlg_SaturatedColourUsesThePrimariesLuminance()
    {
        float[] viaBt2020 = [1f, 0f, 0f];
        float[] viaBt709 = [1f, 0f, 0f];
        PngHdr.ToLinear(TransferFunction.Hlg, ColorPrimaries.BT2020, viaBt2020, 3, 3);
        PngHdr.ToLinear(TransferFunction.Hlg, ColorPrimaries.BT709, viaBt709, 3, 3);

        // E_S = 1 for the red channel; display red is L_W * Ys^0.2 / 203.
        viaBt2020[0].ShouldBe((float)(1000.0 * Math.Pow(0.2627, 0.2) / 203.0), 1e-2);
        viaBt709[0].ShouldBe((float)(1000.0 * Math.Pow(0.2126, 0.2) / 203.0), 1e-2);
        viaBt2020[1].ShouldBe(0f);
        viaBt2020[2].ShouldBe(0f);
    }

    [Fact]
    public void ToLinear_Hlg_LeavesAlphaAlone()
    {
        float[] rgba = [0.75f, 0.75f, 0.75f, 0.25f, 0.5f, 0.5f, 0.5f, 1f];
        PngHdr.ToLinear(TransferFunction.Hlg, ColorPrimaries.BT2020, rgba, colorChannels: 3, stride: 4);

        rgba[3].ShouldBe(0.25f);
        rgba[7].ShouldBe(1f);
    }

    [Theory]
    [InlineData(TransferFunction.Srgb)]
    [InlineData(TransferFunction.BT709)]
    [InlineData(TransferFunction.SMPTE170M)]
    [InlineData(TransferFunction.BT2020_10)]
    [InlineData(TransferFunction.BT2020_12)]
    public void ToLinear_SdrTransfers_StayOnTheSdrWhiteScale(TransferFunction transfer)
    {
        float[] px = [0f, 0.3f, 1f];
        PngHdr.ToLinear(transfer, ColorPrimaries.BT709, px, colorChannels: 1, stride: 1);

        float expectedMid = transfer == TransferFunction.Srgb
            ? PngHdr.SrgbToLinear(0.3f)
            : PngHdr.Bt709ToLinear(0.3f);

        px[0].ShouldBe(0f);
        px[1].ShouldBe(expectedMid);
        px[2].ShouldBe(1f, 1e-6);   // SDR white stays 1.0, it is NOT rescaled to 203
    }

    [Fact]
    public void ToLinear_Linear_PassesThroughUnclamped()
    {
        float[] px = [-0.1f, 0.5f, 1f, 3.5f];
        PngHdr.ToLinear(TransferFunction.Linear, ColorPrimaries.BT709, px, colorChannels: 1, stride: 1);

        px.ShouldBe(new[] { -0.1f, 0.5f, 1f, 3.5f });
    }

    [Fact]
    public void ToLinear_FeedsFromExpandedNarrowRangeCodes()
    {
        // End to end for a narrow-range 10-bit PQ greyscale: code 64 is black, code 940 is 10 000 cd/m2.
        ushort[] codes = [64, 940];
        var px = new float[2];
        PngHdr.ExpandCodes(codes, 10, fullRange: false, px);
        PngHdr.ToLinear(TransferFunction.Pq, ColorPrimaries.BT2020, px, colorChannels: 1, stride: 1);

        px[0].ShouldBe(0f);
        px[1].ShouldBe(10000f / 203f, 1e-4);
    }

    [Theory]
    [InlineData(TransferFunction.Pq, true)]
    [InlineData(TransferFunction.Hlg, true)]
    [InlineData(TransferFunction.Srgb, true)]
    [InlineData(TransferFunction.BT709, true)]
    [InlineData(TransferFunction.SMPTE170M, true)]
    [InlineData(TransferFunction.BT2020_10, true)]
    [InlineData(TransferFunction.BT2020_12, true)]
    [InlineData(TransferFunction.Linear, true)]
    [InlineData(TransferFunction.Unspecified, false)]
    [InlineData(TransferFunction.BT470M, false)]
    [InlineData(TransferFunction.BT470BG, false)]
    [InlineData(TransferFunction.SMPTE240M, false)]
    [InlineData(TransferFunction.Log100, false)]
    [InlineData(TransferFunction.SMPTE428, false)]
    public void IsSupportedTransfer_CoversExactlyTheCurvesImplemented(TransferFunction transfer, bool expected)
    {
        PngHdr.IsSupportedTransfer(transfer).ShouldBe(expected);

        float[] px = [0.5f];
        if (expected)
            Should.NotThrow(() => PngHdr.ToLinear(transfer, ColorPrimaries.BT709, px, 1, 1));
        else
            Should.Throw<NotSupportedException>(() => PngHdr.ToLinear(transfer, ColorPrimaries.BT709, px, 1, 1));
    }

    [Fact]
    public void ToLinear_RejectsBadShapes()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            PngHdr.ToLinear(TransferFunction.Pq, ColorPrimaries.BT2020, new float[4], colorChannels: 2, stride: 2));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            PngHdr.ToLinear(TransferFunction.Pq, ColorPrimaries.BT2020, new float[6], colorChannels: 3, stride: 2));
        Should.Throw<ArgumentException>(() =>
            PngHdr.ToLinear(TransferFunction.Pq, ColorPrimaries.BT2020, new float[7], colorChannels: 3, stride: 4));
    }
}
