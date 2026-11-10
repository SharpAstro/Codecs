using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.GainMap;
using SharpAstro.Jpeg;

namespace SharpAstro.Heif;

/// <summary>
/// A gain-map HEIF reconstructed to HDR: linear RGBA floats on the scale of
/// <see cref="HeifImageDecoder.TryDecodeRgbaFloat"/> (<c>1.0</c> = SDR white; for a PQ or HLG base 203 cd/m²), the
/// primaries of the base image and alpha 1.
/// </summary>
public sealed class HeifGainMapImage
{
    internal HeifGainMapImage(int width, int height, float[] rgba, ColorEncoding encoding, byte[]? icc,
        GainMapMetadata metadata, double displayHeadroom, double weight, HeifGainMapInfo info)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        ColorEncoding = encoding;
        IccProfile = icc;
        Metadata = metadata;
        DisplayHeadroom = displayHeadroom;
        Weight = weight;
        Info = info;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>R, G, B, A per pixel, row-major, top-down: linear light, <c>1.0</c> = SDR white. Values above 1 are the HDR boost.</summary>
    public float[] Rgba { get; }

    /// <summary>Linear transfer, the base image's primaries, <see cref="FloatSemantics.DisplayReferred"/>.</summary>
    public ColorEncoding ColorEncoding { get; }

    /// <summary>The base image's ICC profile when the file carries one, which describes its primaries.</summary>
    public byte[]? IccProfile { get; }

    /// <summary>The ISO/TS 21496-1 metadata the reconstruction used.</summary>
    public GainMapMetadata Metadata { get; }

    /// <summary>The display headroom (a linear ratio to SDR white) the gain was applied for.</summary>
    public double DisplayHeadroom { get; }

    /// <summary>
    /// The weight <c>W</c> in [0, 1] that headroom gave the gain: 0 is the linearised base, 1 the full authored boost. For
    /// the ISO form it is the log-space position of the display headroom between the metadata's two capacities; for Apple's
    /// it is <c>(H' - 1) / (H - 1)</c>, the share of the authored boost applied, <c>H' = clamp(display, 1, H)</c>.
    /// </summary>
    public double Weight { get; }

    /// <summary>The gain map as found: the <c>tmap</c> item and its inputs, or Apple's auxiliary item (<see cref="HeifGainMapInfo.Dialect"/>).</summary>
    public HeifGainMapInfo Info { get; }
}

/// <summary>
/// Reconstructs the HDR rendition of a HEIF that carries an ISO/TS 21496-1 gain map in a <c>tmap</c> derived image
/// item (see <see cref="HeifFile.GainMapOf"/> for the layouts). The base and the map are decoded by
/// <see cref="HeifDecoder"/>; the maths is the shared <see cref="GainMapReconstructor"/> of SharpAstro.GainMap.
/// <para>
/// <b>What happens to each input.</b> The base is converted to R'G'B' as any HEIF image is and then linearised by
/// its own colour: the <c>nclx</c> transfer (sRGB, the BT.709 family, PQ, HLG), the sRGB curve when the file states no
/// transfer (a gain map is authored against an SDR base, so "unspecified" is sRGB; an ICC profile is not applied,
/// its primaries are reported as the base's <c>nclx</c> or BT.709 states them). The map is not light: its codes
/// are divided by the maximum of <i>its</i> bit depth (255 for 8 bits, 1023 for 10), whatever transfer its own
/// <c>nclx</c> claims, and bilinearly upsampled (centre aligned) to the base's size by the core. A monochrome
/// (4:0:0) map, or any map whose metadata is single-channel, drives all three colour channels from one channel
/// (a colour map's G' for a single-channel one: equal to Y' for the neutral chroma such maps have).
/// </para>
/// <para>
/// <b>Headroom.</b> <c>displayHeadroom</c> is the display's peak as a linear ratio to SDR white (4 = two stops).
/// The default is the full authored headroom, <see cref="GainMapMetadata.HdrCapacityMax"/>, so the whole gain is
/// applied; the SDR headroom 1 gives the linearised base exactly (no <c>+offset - offset</c> noise).
/// </para>
/// <para>
/// <b>Apple's dialect</b> (<see cref="HeifGainMapDialect.AppleAux"/>, an <c>auxl</c> gain-map item with no <c>tmap</c>) goes
/// through <see cref="GainMapReconstructor.ReconstructAppleFromLinear"/>: the map's 8-bit codes are divided by 255,
/// upsampled, linearised by the Rec.709 curve and applied as <c>sdr * (1 + (H' - 1) * g)</c>, with <c>H'</c> the display
/// headroom clamped to [1, authored headroom] (the default display headroom is the authored one). Best-effort, from
/// Apple's public article; see <see cref="HeifAppleGainMap"/>. There is no <c>tmap</c> to carry transformative properties, so
/// none is applied beyond the inputs' own.
/// </para>
/// <para>
/// <b>Refused</b>, before anything is decoded: a base over <see cref="HeifLimits.MaxGainMapBasePixels"/>, a map over
/// <see cref="HeifLimits.MaxImagePixels"/> or with more pixels than its base (<see cref="InvalidDataException"/>), and a
/// <c>tmap</c> in the backward direction, whose base is the HDR rendition (<see cref="NotSupportedException"/>).
/// The <c>tmap</c> item's own <c>clap</c>, <c>irot</c> and <c>imir</c> are applied to the result, after the inputs' own
/// (which <see cref="HeifDecoder.Decode"/> applied to each).
/// </para>
/// </summary>
public static class HeifGainMap
{
    /// <summary>
    /// The HDR rendition of <paramref name="item"/> (the primary item when null: the base, or the <c>tmap</c> itself).
    /// </summary>
    /// <param name="file">The whole HEIF file.</param>
    /// <param name="item">The base image or the <c>tmap</c> item.</param>
    /// <param name="displayHeadroom">The display headroom as a linear ratio to SDR white, or null for the full authored one.</param>
    /// <exception cref="InvalidDataException">The item has no gain map, the structure is malformed, or a size is over the limits.</exception>
    /// <exception cref="NotSupportedException">Something in the files or the metadata this reader does not handle (named in the message).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayHeadroom"/> is not a positive finite number.</exception>
    public static HeifGainMapImage Decode(ReadOnlySpan<byte> file, HeifItem? item = null, double? displayHeadroom = null)
    {
        CheckHeadroom(displayHeadroom);
        var heif = HeifReader.Read(file);
        var target = item ?? heif.PrimaryItem;
        var info = heif.GainMapOf(file, target)
            ?? throw new InvalidDataException($"{target} has no gain map: no 'tmap' item derives from it, it is not one, and it has no Apple gain-map auxiliary image with a usable headroom.");
        return Decode(file, heif, info, displayHeadroom);
    }

    /// <summary>The same, for a gain map found by <see cref="HeifFile.GainMapOf"/> in <paramref name="heif"/>, read from <paramref name="file"/>.</summary>
    public static HeifGainMapImage Decode(ReadOnlySpan<byte> file, HeifFile heif, HeifGainMapInfo info, double? displayHeadroom = null)
    {
        ArgumentNullException.ThrowIfNull(heif);
        ArgumentNullException.ThrowIfNull(info);
        CheckHeadroom(displayHeadroom);
        var metadata = info.Metadata;
        if (metadata.BaseRenditionIsHdr)
            throw new NotSupportedException($"The 'tmap' item {info.Tmap?.Id} is in the backward direction (its base is the HDR rendition); only the SDR-base form is reconstructed.");

        // Sizes first, from the declared headers, before a plane is allocated or a slice read.
        var baseImage = heif.Describe(file, info.Base);
        var mapImage = heif.Describe(file, info.Map);
        long basePixels = (long)baseImage.Width * baseImage.Height, mapPixels = (long)mapImage.Width * mapImage.Height;
        if (basePixels > HeifLimits.MaxGainMapBasePixels)
            throw new InvalidDataException($"The gain map's base image {info.Base.Id} declares {baseImage.Width}x{baseImage.Height}, over the {HeifLimits.MaxGainMapBasePixels}-pixel limit for a reconstruction.");
        if (mapPixels > basePixels)
            throw new InvalidDataException($"The gain map {info.Map.Id} ({mapImage.Width}x{mapImage.Height}) is larger than its base image {info.Base.Id} ({baseImage.Width}x{baseImage.Height}).");

        var headroom = displayHeadroom ?? metadata.HdrCapacityMax;
        var apple = info.Dialect == HeifGainMapDialect.AppleAux;
        // The Apple form's weight is the share of the authored boost the display takes (its headroom H' = clamp(display, 1, H)
        // gives 1 + (H' - 1) g: linear, not the ISO form's log-space ramp).
        var weight = apple && info.Apple is { } details
            ? (Math.Clamp(headroom, 1, details.Headroom) - 1) / (details.Headroom - 1)
            : metadata.Weight(headroom);

        var baseRgb = HeifDecoder.Decode(file, info.Base);
        if (baseRgb.ColorEncoding.Transfer == TransferFunction.Unspecified)
            baseRgb = new HeifRgbImage(baseRgb.Width, baseRgb.Height, baseRgb.BitDepth, baseRgb.Samples,
                baseRgb.ColorEncoding with { Transfer = TransferFunction.Srgb }, baseRgb.IccProfile);
        var baseLinear = baseRgb.ToLinearRgbaFloat(out var encoding);

        var mapRgb = HeifDecoder.Decode(file, info.Map);
        var mapChannels = apple || mapImage.ChromaFormat == HevcChromaFormat.Monochrome || metadata.ChannelCount == 1 ? 1 : 3;
        var mapCodes = NormaliseMap(mapRgb, mapChannels);

        var reconstructed = apple
            ? GainMapReconstructor.ReconstructAppleFromLinear(
                baseLinear, baseRgb.Width, baseRgb.Height, 4,
                mapCodes, mapRgb.Width, mapRgb.Height, mapChannels,
                info.Apple!.Headroom, headroom)
            : GainMapReconstructor.ReconstructFromLinear(
                baseLinear, baseRgb.Width, baseRgb.Height, 4,
                mapCodes, mapRgb.Width, mapRgb.Height, mapChannels,
                metadata, headroom);

        // RGB floats from the core into RGBA (alpha 1), in the base's own array where it fits (it does: the same pixel count).
        var rgb = MemoryMarshal.Cast<byte, float>(reconstructed.Pixels);
        var rgba = baseLinear;
        for (var p = 0; p < baseRgb.Width * baseRgb.Height; p++)
        {
            rgba[4 * p] = rgb[3 * p];
            rgba[4 * p + 1] = rgb[3 * p + 1];
            rgba[4 * p + 2] = rgb[3 * p + 2];
            rgba[4 * p + 3] = 1f;
        }

        // The tmap's own transformative properties, in the order it lists them, after the inputs' own (which the
        // decodes above applied).
        int width = baseRgb.Width, height = baseRgb.Height;
        foreach (var association in info.Tmap?.Properties ?? [])
        {
            switch (association.Property)
            {
                case HeifCleanAperture clap:
                    (rgba, width, height) = Transforms.Crop(rgba, width, height, clap, 4);
                    break;
                case HeifRotation { Degrees: not 0 } rotation:
                    (rgba, width, height) = Transforms.Rotate(rgba, width, height, rotation.Degrees, 4);
                    break;
                case HeifMirror mirror:
                    Transforms.Mirror(rgba, width, height, mirror.Axis, 4);
                    break;
            }
        }

        return new HeifGainMapImage(width, height, rgba, encoding, baseRgb.IccProfile, metadata, headroom, weight, info);
    }

    /// <summary>
    /// The map's codes as <c>code / (2^bitDepth - 1)</c> in [0, 1], one channel (the G' of a colour map) or three.
    /// The divisor is the map's own bit depth: a 10-bit map's 1023 is full, not 255.
    /// </summary>
    private static float[] NormaliseMap(HeifRgbImage map, int channels)
    {
        var max = (1 << map.BitDepth) - 1;
        var pixels = map.Width * map.Height;
        var codes = new float[(long)pixels * channels];
        var samples = map.Samples;
        if (channels == 1)
        {
            for (var p = 0; p < pixels; p++)
                codes[p] = (float)samples[3 * p + 1] / max;
        }
        else
        {
            for (var i = 0; i < codes.Length; i++)
                codes[i] = (float)samples[i] / max;
        }
        return codes;
    }

    private static void CheckHeadroom(double? headroom)
    {
        if (headroom is { } h && !(h > 0 && double.IsFinite(h)))
            throw new ArgumentOutOfRangeException("displayHeadroom", h, "The display headroom is a positive finite ratio to SDR white.");
    }
}
