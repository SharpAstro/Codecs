using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Png;

/// <summary>
/// Y (luminance) weights of an RGB primaries set: <c>Y = R * wR + G * wG + B * wB</c> on linear
/// light. The HLG OOTF needs them because its gain is a function of scene luminance, not of the
/// individual channel (BT.2100-2 Table 5, <c>Y_S</c>).
/// </summary>
internal readonly record struct LumaWeights(float R, float G, float B)
{
    /// <summary>BT.709 / sRGB primaries, D65 (BT.709-6 / BT.2100-2 Table 5 note on Y).</summary>
    public static LumaWeights Bt709 { get; } = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>BT.2020 / BT.2100 primaries, D65 (BT.2100-2 Table 5: <c>Y_S = 0.2627 R + 0.6780 G + 0.0593 B</c>).</summary>
    public static LumaWeights Bt2020 { get; } = new(0.2627f, 0.6780f, 0.0593f);

    /// <summary>
    /// Display P3 primaries (H.273 §8.1 value 12) with the D65 white point. Not a number a
    /// standard prints: it is the Y row of the RGB-to-XYZ matrix those chromaticities give (the
    /// test suite re-derives it, and the two others, from the H.273 chromaticity table).
    /// </summary>
    public static LumaWeights DisplayP3 { get; } = new(0.2290f, 0.6917f, 0.0793f);

    /// <summary>
    /// The weights for the primaries a cICP chunk names. BT.2020 and Display P3 have their own;
    /// every other value - BT.709 itself, <see cref="ColorPrimaries.Unspecified"/> (which decoders
    /// read as BT.709, see its documentation), and the legacy SD / film / DCI sets - gets the BT.709
    /// weights. That is an approximation for the sets that are not BT.709, and a cheap one: the
    /// weights only enter the OOTF as <c>Y_S^0.2</c>, so a few percent of error in Y becomes a
    /// fraction of a percent in the gain.
    /// </summary>
    public static LumaWeights For(ColorPrimaries primaries) => primaries switch
    {
        ColorPrimaries.BT2020 => Bt2020,
        ColorPrimaries.DisplayP3 => DisplayP3,
        _ => Bt709,
    };
}

/// <summary>
/// The transfer-function and range maths that projecting a cICP-tagged PNG onto floats needs,
/// written from the formulas in SMPTE ST 2084, ITU-R BT.2100, IEC 61966-2-1 and ITU-T H.273 (no
/// third-party source was consulted or transcribed).
/// <para>
/// <b>Output scale.</b> <see cref="ToLinear"/> and everything built on it produce linear light
/// normalised so that <c>1.0</c> is the reference white of the signal's own system:
/// </para>
/// <list type="bullet">
/// <item><description><b>PQ, HLG</b> - <c>1.0 = 203 cd/m²</c>, the HDR reference white of
/// ITU-R BT.2408 (<see cref="ReferenceWhiteNits"/>). Absolute PQ light is divided by 203, so a
/// 10 000 cd/m² code reads 49.26. HLG is rendered through the BT.2100 OOTF for the nominal
/// 1000 cd/m² display (system gamma 1.2) and then divided by 203 as well, so HLG signal 75 %
/// (BT.2408's reference white level) lands on 1.0, and a 100 % HLG white on 4.93.</description></item>
/// <item><description><b>SDR transfers</b> (BT.709 family 1/6/14/15, sRGB 13) and <b>Linear</b> (8) -
/// <c>1.0 = SDR white</c>, the code value 1.0 after linearisation. Nothing is rescaled to 203:
/// SDR content has no absolute luminance, and a consumer that mixes it with HDR picks that
/// mapping itself.</description></item>
/// </list>
/// <para>
/// <b>Domain.</b> Encoded signals are clamped to <c>[0, 1]</c> before a curve is applied, which is
/// what a display does with narrow-range footroom and headroom (BT.2100 defines the PQ and HLG
/// curves on <c>[0, 1]</c> only; <c>pow</c> of a negative would be NaN). Linear is the exception
/// and passes through unclamped: it is the one curve for which a value above 1.0 is meaningful
/// as it stands.
/// </para>
/// <para>
/// Everything here is span-based and allocation-free; the scalar curves compute in double and
/// return float so that exact anchor points (PQ 1.0 = 10 000 cd/m²) stay exact.
/// </para>
/// </summary>
internal static class PngHdr
{
    /// <summary>
    /// ITU-R BT.2408 HDR reference white, in cd/m². The divisor that turns absolute PQ light, or
    /// HLG display light on the nominal display, into the <c>1.0 = reference white</c> scale.
    /// </summary>
    public const float ReferenceWhiteNits = 203f;

    /// <summary>Peak of the PQ signal range: code value 1.0 is 10 000 cd/m² (ST 2084).</summary>
    public const float PqPeakNits = 10000f;

    /// <summary>Nominal peak display luminance the HLG OOTF is evaluated for (BT.2100-2 Table 5, <c>L_W</c>).</summary>
    public const float HlgNominalPeakNits = 1000f;

    /// <summary>HLG system gamma at the nominal 1000 cd/m² peak (BT.2100-2 Table 5, <c>γ = 1.2</c>).</summary>
    public const float HlgSystemGamma = 1.2f;

    // ---------------------------------------------------------------------------------------
    // PQ - SMPTE ST 2084 (also BT.2100-2 Table 4, H.273 §8.2 Table 3 value 16).
    // All six constants are dyadic rationals, so they are exact in double.
    // ---------------------------------------------------------------------------------------
    private const double PqM1 = 2610.0 / 16384.0;          // 0.1593017578125
    private const double PqM2 = 2523.0 / 4096.0 * 128.0;   // 78.84375
    private const double PqC1 = 3424.0 / 4096.0;           // 0.8359375  (= c3 - c2 + 1)
    private const double PqC2 = 2413.0 / 4096.0 * 32.0;    // 18.8515625
    private const double PqC3 = 2392.0 / 4096.0 * 32.0;    // 18.6875

    // ---------------------------------------------------------------------------------------
    // HLG - BT.2100-2 Table 5 (ARIB STD-B67), constants as the spec prints them.
    // ---------------------------------------------------------------------------------------
    private const double HlgA = 0.17883277;
    private const double HlgB = 0.28466892;   // 1 - 4a
    private const double HlgC = 0.55991073;   // 0.5 - a * ln(4a)

    // ---------------------------------------------------------------------------------------
    // BT.709 / BT.2020 OETF - H.273 §8.2 Table 3 values 1, 6, 14, 15. The knee is the point
    // where the linear segment (4.5 L) meets the power segment (alpha L^0.45 - (alpha - 1))
    // with equal slope; these are the full-precision solutions of that condition. The rounded
    // 1.099 / 0.018 (10-bit) and 1.0993 / 0.0181 (12-bit) that BT.2020 prints differ from them by
    // far less than one 8-bit code step, so one curve serves 1, 6, 14 and 15.
    // ---------------------------------------------------------------------------------------
    private const double Bt709Alpha = 1.0992968268094429;
    private const double Bt709Beta = 0.018053968510807;                  // linear-light knee
    private const double Bt709EncodedKnee = 4.5 * Bt709Beta;             // 0.0812428...

    /// <summary>
    /// SMPTE ST 2084 inverse of the reference OETF, i.e. the PQ EOTF: encoded signal
    /// <paramref name="signal"/> in <c>[0, 1]</c> to absolute display light in cd/m²
    /// (<c>0 → 0</c>, <c>1 → 10 000</c>). <c>Y = 10000 · (max(N^(1/m2) − c1, 0) / (c2 − c3 · N^(1/m2)))^(1/m1)</c>.
    /// </summary>
    public static float PqInverseEotf(float signal)
    {
        double n = Math.Clamp((double)signal, 0.0, 1.0);
        double p = Math.Pow(n, 1.0 / PqM2);
        double numerator = Math.Max(p - PqC1, 0.0);
        double denominator = PqC2 - PqC3 * p;
        return (float)(PqPeakNits * Math.Pow(numerator / denominator, 1.0 / PqM1));
    }

    /// <summary>
    /// BT.2100-2 Table 5, HLG inverse OETF: encoded signal <paramref name="signal"/> in
    /// <c>[0, 1]</c> to normalised scene light <c>E</c> in <c>[0, 1]</c>:
    /// <c>E = E'² / 3</c> for <c>E' ≤ ½</c>, else <c>(exp((E' − c) / a) + b) / 12</c>.
    /// The ½ knee lands on <c>1/12</c> and signal 1 on 1.0 (to the rounding of the printed constants).
    /// This is scene light, before <see cref="HlgOotf"/>.
    /// </summary>
    public static float HlgInverseOetf(float signal)
    {
        double s = Math.Clamp((double)signal, 0.0, 1.0);
        double e = s <= 0.5
            ? s * s / 3.0
            : (Math.Exp((s - HlgC) / HlgA) + HlgB) / 12.0;
        return (float)e;
    }

    /// <summary>
    /// BT.2100-2 Table 5, the HLG reference OOTF: scene-linear <c>(R_S, G_S, B_S)</c> in
    /// <c>[0, 1]</c> to display light in cd/m² for the nominal 1000 cd/m² display,
    /// <c>F_D = L_W · Y_S^(γ−1) · E_S</c> per channel, with <c>Y_S</c> the scene luminance from
    /// <paramref name="weights"/> (the Y weights of the image's primaries), <c>γ = 1.2</c> and
    /// <c>L_W = 1000</c>. The black level <c>L_B</c> is taken as 0.
    /// </summary>
    public static (float R, float G, float B) HlgOotf(float r, float g, float b, LumaWeights weights)
    {
        float ys = weights.R * r + weights.G * g + weights.B * b;
        // Pow(0, 0.2) is 0, and Ys == 0 only when r = g = b = 0 (the weights are positive), so
        // there is no 0 * infinity to guard against.
        float gain = HlgNominalPeakNits * MathF.Pow(ys, HlgSystemGamma - 1f);
        return (gain * r, gain * g, gain * b);
    }

    /// <summary>
    /// The HLG OOTF for a grey (<c>R = G = B = E</c>) pixel: <c>Y_S = E</c>, so
    /// <c>F_D = L_W · E^γ</c>. Same clause as <see cref="HlgOotf"/>; it avoids needing the weights
    /// to sum to exactly 1 in float.
    /// </summary>
    public static float HlgOotfGrey(float e) => HlgNominalPeakNits * MathF.Pow(e, HlgSystemGamma);

    /// <summary>
    /// IEC 61966-2-1 (sRGB) decoding, H.273 §8.2 Table 3 value 13: encoded <paramref name="v"/>
    /// in <c>[0, 1]</c> to linear light in <c>[0, 1]</c>. Linear segment <c>v / 12.92</c> up to the
    /// encoded knee 0.04045, then <c>((v + 0.055) / 1.055)^2.4</c>.
    /// </summary>
    public static float SrgbToLinear(float v)
    {
        double s = Math.Clamp((double)v, 0.0, 1.0);
        return (float)(s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4));
    }

    /// <summary>
    /// BT.709 / BT.2020 OETF inverse, H.273 §8.2 Table 3 values 1 (BT.709), 6 (SMPTE 170M),
    /// 14 and 15 (BT.2020 10- and 12-bit): encoded <paramref name="v"/> in <c>[0, 1]</c> to linear
    /// light in <c>[0, 1]</c>. Linear segment <c>v / 4.5</c> below the encoded knee
    /// (<c>4.5 · β ≈ 0.0812</c>), then <c>((v + (α − 1)) / α)^(1/0.45)</c>.
    /// </summary>
    public static float Bt709ToLinear(float v)
    {
        double s = Math.Clamp((double)v, 0.0, 1.0);
        return (float)(s < Bt709EncodedKnee
            ? s / 4.5
            : Math.Pow((s + (Bt709Alpha - 1.0)) / Bt709Alpha, 1.0 / 0.45));
    }

    /// <summary>
    /// H.273 §8.3 code expansion for an RGB (MatrixCoefficients 0) sample: an integer code at
    /// <paramref name="bitDepth"/> bits to the normalised signal the transfer curves take.
    /// Full range (<c>VideoFullRangeFlag = 1</c>): <c>code / (2^n − 1)</c>. Narrow range:
    /// <c>(code − 16 · 2^(n−8)) / (219 · 2^(n−8))</c>, i.e. 16..235 at 8 bits, 64..940 at 10,
    /// 4096..60160 at 16. The result is <b>not</b> clamped: a narrow-range code below the footroom
    /// comes out negative and one above the headroom above 1; the curves clamp (see the class
    /// remarks), and <see cref="ToLinear"/> leaves Linear alone.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitDepth"/> is outside 1..16, or
    /// outside 8..16 for narrow range (the H.273 equations start at 8 bits).</exception>
    public static float CodeToSignal(int code, int bitDepth, bool fullRange)
    {
        ValidateBitDepth(bitDepth, fullRange);
        return CodeToSignalUnchecked(code, bitDepth, fullRange);
    }

    /// <summary>
    /// <see cref="CodeToSignal"/> over a run of 8-bit samples, written to <paramref name="signal"/>
    /// (at least as long as <paramref name="codes"/>). Validates once per run.
    /// </summary>
    public static void ExpandCodes(ReadOnlySpan<byte> codes, bool fullRange, Span<float> signal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(signal.Length, codes.Length);
        for (int i = 0; i < codes.Length; i++)
            signal[i] = CodeToSignalUnchecked(codes[i], 8, fullRange);
    }

    /// <summary>
    /// <see cref="CodeToSignal"/> over a run of samples at <paramref name="bitDepth"/> bits held in
    /// host-order <see cref="ushort"/>s (a 16-bit PNG's samples after the big-endian swap), written
    /// to <paramref name="signal"/> (at least as long as <paramref name="codes"/>). Validates once per run.
    /// </summary>
    public static void ExpandCodes(ReadOnlySpan<ushort> codes, int bitDepth, bool fullRange, Span<float> signal)
    {
        ValidateBitDepth(bitDepth, fullRange);
        ArgumentOutOfRangeException.ThrowIfLessThan(signal.Length, codes.Length);
        for (int i = 0; i < codes.Length; i++)
            signal[i] = CodeToSignalUnchecked(codes[i], bitDepth, fullRange);
    }

    /// <summary>
    /// Whether <see cref="ToLinear"/> can linearise <paramref name="transfer"/>: PQ (16), HLG (18),
    /// sRGB (13), the BT.709 family (1, 6, 14, 15) and Linear (8). The rest of the H.273 table
    /// (BT.470 gammas, SMPTE 240M, the log curves, xvYCC, BT.1361, ST 428) is not covered.
    /// </summary>
    public static bool IsSupportedTransfer(TransferFunction transfer) => transfer is
        TransferFunction.Pq or TransferFunction.Hlg or TransferFunction.Srgb or
        TransferFunction.BT709 or TransferFunction.SMPTE170M or
        TransferFunction.BT2020_10 or TransferFunction.BT2020_12 or
        TransferFunction.Linear;

    /// <summary>
    /// Linearises encoded pixels in place onto the scale the class remarks define (1.0 = 203 cd/m²
    /// reference white for PQ/HLG, 1.0 = SDR white otherwise). <paramref name="pixels"/> holds
    /// <see cref="CodeToSignal">normalised signals</see>, <paramref name="stride"/> floats per
    /// pixel of which the first <paramref name="colorChannels"/> are colour (1 = grey, 3 = RGB);
    /// anything after them (alpha) is left untouched, since alpha is not light.
    /// For HLG the colour channels go through <see cref="HlgInverseOetf"/> and then the OOTF, which
    /// couples the three of an RGB pixel through its luminance - hence <paramref name="primaries"/>.
    /// For every other transfer the primaries do not matter.
    /// </summary>
    /// <exception cref="NotSupportedException"><paramref name="transfer"/> is not
    /// <see cref="IsSupportedTransfer">supported</see>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorChannels"/> is not 1 or 3, or
    /// <paramref name="stride"/> is smaller than it.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is not a whole number of pixels.</exception>
    public static void ToLinear(TransferFunction transfer, ColorPrimaries primaries, Span<float> pixels, int colorChannels, int stride)
    {
        if (colorChannels is not (1 or 3))
            throw new ArgumentOutOfRangeException(nameof(colorChannels), colorChannels, "Colour channels must be 1 (grey) or 3 (RGB).");
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, colorChannels);
        if (pixels.Length % stride != 0)
            throw new ArgumentException($"Span length {pixels.Length} is not a multiple of the stride {stride}.", nameof(pixels));

        switch (transfer)
        {
            case TransferFunction.Linear:
                return;
            case TransferFunction.Pq:
                Map<PqToReferenceWhite>(pixels, colorChannels, stride);
                return;
            case TransferFunction.Srgb:
                Map<SrgbCurve>(pixels, colorChannels, stride);
                return;
            case TransferFunction.BT709 or TransferFunction.SMPTE170M or
                 TransferFunction.BT2020_10 or TransferFunction.BT2020_12:
                Map<Bt709Curve>(pixels, colorChannels, stride);
                return;
            case TransferFunction.Hlg:
                HlgToLinear(pixels, colorChannels, stride, LumaWeights.For(primaries));
                return;
            default:
                throw new NotSupportedException($"Transfer characteristics {(byte)transfer} ({transfer}) has no float linearisation.");
        }
    }

    // ---- code tables (the float fill's fast path) ------------------------------------------

    /// <summary>
    /// The whole code-to-linear map as a table: <c>table[c] = ToLinear(CodeToSignal(c))</c> for every
    /// code <c>c</c> in <c>0 .. 2^bitDepth - 1</c>, on a single-channel (grey) scale. Because each
    /// entry is produced by the very same <see cref="CodeToSignal"/> and <see cref="ToLinear"/> a
    /// per-sample caller would use, looking a code up is <b>bit-identical</b> to computing it; the
    /// table also folds in the narrow-range expansion and the clamp. For every transfer but HLG this
    /// is the colour channel's final value whatever the pixel's other channels are; for HLG it is the
    /// final value of a <i>grey</i> pixel only, and an RGB pixel needs <see cref="BuildHlgSceneLightTable"/>
    /// and <see cref="HlgSceneToLinear"/> (the OOTF couples its channels).
    /// </summary>
    /// <param name="table">Exactly <c>2^bitDepth</c> floats.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bit depth is invalid for the range (see
    /// <see cref="CodeToSignal"/>), or <paramref name="table"/> is not <c>2^bitDepth</c> long.</exception>
    /// <exception cref="NotSupportedException"><paramref name="transfer"/> is not
    /// <see cref="IsSupportedTransfer">supported</see>.</exception>
    public static void BuildLinearTable(TransferFunction transfer, ColorPrimaries primaries, int bitDepth, bool fullRange, Span<float> table)
    {
        FillSignalTable(bitDepth, fullRange, table);
        ToLinear(transfer, primaries, table, 1, 1);
    }

    /// <summary>
    /// <see cref="BuildLinearTable"/>'s HLG sibling for RGB pixels: <c>table[c] = HlgInverseOetf(CodeToSignal(c))</c>,
    /// the normalised <i>scene</i> light that <see cref="HlgSceneToLinear"/> then takes through the OOTF.
    /// </summary>
    public static void BuildHlgSceneLightTable(int bitDepth, bool fullRange, Span<float> table)
    {
        FillSignalTable(bitDepth, fullRange, table);
        for (int i = 0; i < table.Length; i++)
            table[i] = HlgInverseOetf(table[i]);
    }

    /// <summary>
    /// The per-pixel half of an HLG RGB pixel: scene light <c>(er, eg, eb)</c> (from
    /// <see cref="BuildHlgSceneLightTable"/>) to linear light on the <c>1.0 = 203 cd/m²</c> scale,
    /// the same <see cref="HlgOotf"/> and the same division by <see cref="ReferenceWhiteNits"/>
    /// <see cref="ToLinear"/> performs, so the result is bit-identical to it.
    /// </summary>
    public static void HlgSceneToLinear(float er, float eg, float eb, LumaWeights weights, out float r, out float g, out float b)
    {
        var (fr, fg, fb) = HlgOotf(er, eg, eb, weights);
        r = fr / ReferenceWhiteNits;
        g = fg / ReferenceWhiteNits;
        b = fb / ReferenceWhiteNits;
    }

    private static void FillSignalTable(int bitDepth, bool fullRange, Span<float> table)
    {
        ValidateBitDepth(bitDepth, fullRange);
        if (table.Length != 1 << bitDepth)
            throw new ArgumentOutOfRangeException(nameof(table), table.Length, $"A {bitDepth}-bit code table has {1 << bitDepth} entries.");
        for (int c = 0; c < table.Length; c++)
            table[c] = CodeToSignalUnchecked(c, bitDepth, fullRange);
    }

    // ---- internals -----------------------------------------------------------------------

    private static void ValidateBitDepth(int bitDepth, bool fullRange)
    {
        int min = fullRange ? 1 : 8;
        if (bitDepth < min || bitDepth > 16)
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth,
                fullRange ? "Bit depth must be 1..16." : "Narrow-range expansion is defined for 8..16 bits.");
    }

    private static float CodeToSignalUnchecked(int code, int bitDepth, bool fullRange)
    {
        if (fullRange)
            return code / (float)((1 << bitDepth) - 1);

        int shift = bitDepth - 8;
        return (code - (16 << shift)) / (float)(219 << shift);
    }

    private static void HlgToLinear(Span<float> pixels, int colorChannels, int stride, LumaWeights weights)
    {
        for (int p = 0; p < pixels.Length; p += stride)
        {
            if (colorChannels == 1)
            {
                float e = HlgInverseOetf(pixels[p]);
                pixels[p] = HlgOotfGrey(e) / ReferenceWhiteNits;
            }
            else
            {
                var (r, g, b) = HlgOotf(
                    HlgInverseOetf(pixels[p]),
                    HlgInverseOetf(pixels[p + 1]),
                    HlgInverseOetf(pixels[p + 2]),
                    weights);
                pixels[p] = r / ReferenceWhiteNits;
                pixels[p + 1] = g / ReferenceWhiteNits;
                pixels[p + 2] = b / ReferenceWhiteNits;
            }
        }
    }

    // One struct per elementwise curve so Map<> specialises per curve: no delegate, no branch on
    // the curve inside the loop.
    private interface ICurve
    {
        static abstract float Apply(float signal);
    }

    private readonly struct PqToReferenceWhite : ICurve
    {
        public static float Apply(float signal) => PqInverseEotf(signal) / ReferenceWhiteNits;
    }

    private readonly struct SrgbCurve : ICurve
    {
        public static float Apply(float signal) => SrgbToLinear(signal);
    }

    private readonly struct Bt709Curve : ICurve
    {
        public static float Apply(float signal) => Bt709ToLinear(signal);
    }

    private static void Map<TCurve>(Span<float> pixels, int colorChannels, int stride) where TCurve : struct, ICurve
    {
        for (int p = 0; p < pixels.Length; p += stride)
            for (int c = 0; c < colorChannels; c++)
                pixels[p + c] = TCurve.Apply(pixels[p + c]);
    }
}
