using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Jpeg;

namespace SharpAstro.GainMap;

/// <summary>
/// The reconstruction math, independent of the carrier format: a base rendition plus a gain map plus
/// <see cref="GainMapMetadata"/> give HDR linear floats for any display headroom.
/// <para>
/// Two entry points, one loop. <see cref="ReconstructFromSrgb8"/> is the JPEG form (8-bit sRGB base,
/// 8-bit map) and is byte-identical to what <c>GainMapImage.ReconstructHdr</c> always produced.
/// <see cref="ReconstructFromLinear"/> takes the base as <b>linear floats</b> (so a 10-bit PQ HEIF base,
/// once the caller has linearised it, works as well as an 8-bit sRGB one) and the map as codes
/// normalised to [0, 1] (so a map of any bit depth works: divide by its maximum code first).
/// </para>
/// <para>
/// Per pixel and per channel <c>c</c>, with that channel's parameters from
/// <see cref="GainMapMetadata.GetChannel"/>: <c>recovery = code / maxCode</c>,
/// <c>logGain = lerp(log2(Min), log2(Max), recovery^(1/Gamma))</c>,
/// <c>HDR = (base + OffsetSdr) · 2^(logGain · W) − OffsetHdr</c>. The map is bilinearly upsampled
/// (centre-aligned) to the base's size. A one-channel map is shared by all three output channels.
/// </para>
/// </summary>
public static class GainMapReconstructor
{
    /// <summary>
    /// Reconstructs from an 8-bit display-referred sRGB base (<paramref name="baseChannels"/> interleaved
    /// samples per pixel, 1 to 4) and an 8-bit gain map. Output: RGB <see cref="SampleFormat.Float32"/>,
    /// linear, display-referred, 1.0 = SDR white.
    /// </summary>
    public static RasterImage ReconstructFromSrgb8(
        ReadOnlySpan<byte> basePixels, int width, int height, int baseChannels,
        ReadOnlySpan<byte> mapPixels, int mapWidth, int mapHeight, int mapChannels,
        GainMapMetadata metadata, double displayHeadroom) =>
        Reconstruct(new Srgb8Base(basePixels), width, height, baseChannels,
            new Map8(mapPixels), mapWidth, mapHeight, mapChannels, metadata, displayHeadroom);

    /// <summary>
    /// Reconstructs from a base given as linear floats (1.0 = SDR white, <paramref name="baseChannels"/>
    /// interleaved samples per pixel) and a gain map given as codes normalised to [0, 1]. Output as
    /// <see cref="ReconstructFromSrgb8"/>.
    /// </summary>
    public static RasterImage ReconstructFromLinear(
        ReadOnlySpan<float> baseLinear, int width, int height, int baseChannels,
        ReadOnlySpan<float> mapNormalised, int mapWidth, int mapHeight, int mapChannels,
        GainMapMetadata metadata, double displayHeadroom) =>
        Reconstruct(new LinearBase(baseLinear), width, height, baseChannels,
            new MapNormalised(mapNormalised), mapWidth, mapHeight, mapChannels, metadata, displayHeadroom);

    /// <summary>
    /// Apple's HDR gain-map form (the one iPhone HEICs carry as an <c>auxl</c> auxiliary image, before ISO/TS 21496-1),
    /// from a linear-float base and a map given as codes normalised to [0, 1]. The base is linear light, 1.0 = SDR white.
    /// Per pixel and channel, with the map's codes bilinearly upsampled (centre aligned) to the base's size FIRST and
    /// linearised after (Apple's order: "resize ... then linearize"):
    /// <c>g = Rec709ToLinear(code)</c>, <c>H' = clamp(displayHeadroom, 1, headroom)</c>,
    /// <c>HDR = base · (1 + (H' − 1) · g)</c>.
    /// <para>
    /// The core of it is Apple's own (<c>hdr_rgb = sdr_rgb * (1.0 + (headroom - 1.0) * gainmap)</c>, with the map
    /// linearised by the Rec.709 curve; headroom from <c>HeifAppleGainMap.HeadroomFromStops</c>). It differs from
    /// <see cref="ReconstructFromLinear"/> in being <b>linear</b> in the map (not <c>2^(log-gain)</c>), with no offsets,
    /// and in how a display is accommodated, which Apple's page does not describe: this clamps the display headroom
    /// into [1, <paramref name="headroom"/>] and uses it as the headroom, so a display of 1 gives the base exactly
    /// (<c>base · 1</c>) and one at or above the authored headroom the whole effect. That clamp is this package's
    /// choice (the result never exceeds the display's peak, as Apple states for the authored one), not Apple's.
    /// </para>
    /// </summary>
    /// <param name="headroom">The authored headroom, a linear ratio to SDR white, above 1.</param>
    /// <param name="displayHeadroom">The display's headroom, a linear ratio to SDR white.</param>
    /// <returns>RGB <see cref="SampleFormat.Float32"/>, linear, display-referred, 1.0 = SDR white.</returns>
    public static RasterImage ReconstructAppleFromLinear(
        ReadOnlySpan<float> baseLinear, int width, int height, int baseChannels,
        ReadOnlySpan<float> mapNormalised, int mapWidth, int mapHeight, int mapChannels,
        double headroom, double displayHeadroom)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeadroom);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapChannels);
        if (!(headroom > 1) || !double.IsFinite(headroom))
            throw new ArgumentOutOfRangeException(nameof(headroom), headroom, "The authored headroom is a finite ratio above 1.");
        if (baseLinear.Length < (long)width * height * baseChannels)
            throw new ArgumentException("Base is smaller than width * height * channels.", nameof(baseLinear));
        if (mapNormalised.Length < (long)mapWidth * mapHeight * mapChannels)
            throw new ArgumentException("Gain map is smaller than width * height * channels.", nameof(mapNormalised));

        var applied = Math.Clamp(displayHeadroom, 1, headroom) - 1;
        var baseG = Math.Min(1, baseChannels - 1);
        var baseB = Math.Min(2, baseChannels - 1);
        var usableMapChannels = mapChannels >= 3 ? 3 : 1;

        var outBytes = new byte[width * height * 3 * sizeof(float)];
        var output = MemoryMarshal.Cast<byte, float>(outBytes.AsSpan());
        var encoding = new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.DisplayReferred };

        for (var y = 0; y < height; y++)
        {
            var v = (y + 0.5) * mapHeight / height - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(v), 0, mapHeight - 1);
            var y1 = Math.Min(y0 + 1, mapHeight - 1);
            var fy = Math.Clamp(v - y0, 0, 1);

            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5) * mapWidth / width - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(u), 0, mapWidth - 1);
                var x1 = Math.Min(x0 + 1, mapWidth - 1);
                var fx = Math.Clamp(u - x0, 0, 1);

                var row0 = y0 * mapWidth;
                var row1 = y1 * mapWidth;
                var baseIndex = (y * width + x) * baseChannels;
                var outIndex = (y * width + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var mc = c < usableMapChannels ? c : 0;
                    var code = (mapNormalised[(row0 + x0) * mapChannels + mc] * (1 - fx) + mapNormalised[(row0 + x1) * mapChannels + mc] * fx) * (1 - fy)
                             + (mapNormalised[(row1 + x0) * mapChannels + mc] * (1 - fx) + mapNormalised[(row1 + x1) * mapChannels + mc] * fx) * fy;
                    var gain = Rec709Transfer.ToLinear(Math.Clamp(code, 0, 1));
                    var sdr = baseLinear[baseIndex + (c == 0 ? 0 : c == 1 ? baseG : baseB)];
                    output[outIndex + c] = (float)(sdr * (1 + applied * gain));
                }
            }
        }

        return new RasterImage(width, height, 3, SampleFormat.Float32, outBytes, null, encoding);
    }

    private static RasterImage Reconstruct<TBase, TMap>(
        TBase source, int width, int height, int baseChannels,
        TMap map, int mapWidth, int mapHeight, int mapChannels,
        GainMapMetadata metadata, double displayHeadroom)
        where TBase : struct, IBaseSamples, allows ref struct
        where TMap : struct, IMapSamples, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeadroom);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapChannels);
        metadata.Validate();
        if (metadata.BaseRenditionIsHdr)
            throw new NotSupportedException("BaseRenditionIsHDR=true (HDR-base) files are not supported; only the SDR-base form is implemented.");
        if (source.Length < (long)width * height * baseChannels)
            throw new ArgumentException("Base is smaller than width * height * channels.", nameof(source));
        if (map.Length < (long)mapWidth * mapHeight * mapChannels)
            throw new ArgumentException("Gain map is smaller than width * height * channels.", nameof(map));

        var weight = metadata.Weight(displayHeadroom);
        var baseG = Math.Min(1, baseChannels - 1);
        var baseB = Math.Min(2, baseChannels - 1);

        var outBytes = new byte[width * height * 3 * sizeof(float)];
        var output = MemoryMarshal.Cast<byte, float>(outBytes.AsSpan());
        var encoding = new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.DisplayReferred };

        if (weight == 0)
        {
            // Fast path, and the format's exactness promise: at SDR headroom the
            // result IS the base, just linearized — no +offset−offset float noise.
            for (int i = 0, o = 0; o < output.Length; i += baseChannels, o += 3)
            {
                output[o] = (float)source.Linear(i);
                output[o + 1] = (float)source.Linear(i + baseG);
                output[o + 2] = (float)source.Linear(i + baseB);
            }
            return new RasterImage(width, height, 3, SampleFormat.Float32, outBytes, null, encoding);
        }

        // Per-channel constants, hoisted out of the pixel loop (identical arithmetic for the scalar case).
        Span<double> logMin = stackalloc double[3];
        Span<double> logMax = stackalloc double[3];
        Span<double> invGamma = stackalloc double[3];
        Span<double> offsetSdr = stackalloc double[3];
        Span<double> offsetHdr = stackalloc double[3];
        for (var c = 0; c < 3; c++)
        {
            var p = metadata.GetChannel(c);
            logMin[c] = Math.Log2(p.GainMapMin);
            logMax[c] = Math.Log2(p.GainMapMax);
            invGamma[c] = 1.0 / p.Gamma;
            offsetSdr[c] = p.OffsetSdr;
            offsetHdr[c] = p.OffsetHdr;
        }

        var maxCode = map.MaxCode;
        // Per-channel gains when the map carries RGB; a gray map (or gray-decoded-
        // to-RGB) collapses to the same value for all three.
        var usableMapChannels = mapChannels >= 3 ? 3 : 1;

        for (var y = 0; y < height; y++)
        {
            // Center-aligned bilinear map coordinates (the resampling convention
            // Skia/libultrahdr use when scaling the quarter-size map back up).
            var v = (y + 0.5) * mapHeight / height - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(v), 0, mapHeight - 1);
            var y1 = Math.Min(y0 + 1, mapHeight - 1);
            var fy = Math.Clamp(v - y0, 0, 1);

            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5) * mapWidth / width - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(u), 0, mapWidth - 1);
                var x1 = Math.Min(x0 + 1, mapWidth - 1);
                var fx = Math.Clamp(u - x0, 0, 1);

                var row0 = y0 * mapWidth;
                var row1 = y1 * mapWidth;
                var baseIndex = (y * width + x) * baseChannels;
                var outIndex = (y * width + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var mc = c < usableMapChannels ? c : 0;
                    var code = (map.Code((row0 + x0) * mapChannels + mc) * (1 - fx) + map.Code((row0 + x1) * mapChannels + mc) * fx) * (1 - fy)
                             + (map.Code((row1 + x0) * mapChannels + mc) * (1 - fx) + map.Code((row1 + x1) * mapChannels + mc) * fx) * fy;

                    var recovery = code / maxCode;
                    if (invGamma[c] != 1.0)
                        recovery = Math.Pow(recovery, invGamma[c]);
                    var gain = Math.Pow(2, (logMin[c] + (logMax[c] - logMin[c]) * recovery) * weight);

                    var sdrLinear = source.Linear(baseIndex + Math.Min(c, baseChannels - 1));
                    output[outIndex + c] = (float)((sdrLinear + offsetSdr[c]) * gain - offsetHdr[c]);
                }
            }
        }

        return new RasterImage(width, height, 3, SampleFormat.Float32, outBytes, null, encoding);
    }

    // The base and the map are read through these so one loop serves every sample representation.
    // Generic struct arguments are specialised by the JIT/AOT, so the abstraction costs nothing, and
    // the 8-bit form keeps its double arithmetic (a float detour would change the last bit).
    private interface IBaseSamples
    {
        int Length { get; }
        double Linear(int index);
    }

    private interface IMapSamples
    {
        int Length { get; }
        double MaxCode { get; }
        double Code(int index);
    }

    private readonly ref struct Srgb8Base(ReadOnlySpan<byte> pixels) : IBaseSamples
    {
        private readonly ReadOnlySpan<byte> _pixels = pixels;
        private static readonly double[] Lut = SrgbTransfer.EotfLut8;
        public int Length => _pixels.Length;
        public double Linear(int index) => Lut[_pixels[index]];
    }

    private readonly ref struct LinearBase(ReadOnlySpan<float> pixels) : IBaseSamples
    {
        private readonly ReadOnlySpan<float> _pixels = pixels;
        public int Length => _pixels.Length;
        public double Linear(int index) => _pixels[index];
    }

    private readonly ref struct Map8(ReadOnlySpan<byte> pixels) : IMapSamples
    {
        private readonly ReadOnlySpan<byte> _pixels = pixels;
        public int Length => _pixels.Length;
        public double MaxCode => 255.0;
        public double Code(int index) => _pixels[index];
    }

    private readonly ref struct MapNormalised(ReadOnlySpan<float> pixels) : IMapSamples
    {
        private readonly ReadOnlySpan<float> _pixels = pixels;
        public int Length => _pixels.Length;
        public double MaxCode => 1.0;
        public double Code(int index) => _pixels[index];
    }
}
