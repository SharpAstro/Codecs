using SharpAstro.Codecs.Abstractions;
using SharpAstro.GainMap;

namespace SharpAstro.Jpeg;

/// <summary>
/// A decoded gain-map JPEG: the authored SDR base rendition, the (typically
/// quarter-scale) gain map, and the scalar reconstruction parameters.
/// <see cref="ReconstructHdr"/> turns the pair into HDR linear floats for any
/// display headroom — the display-adaptive dial that makes the format useful.
/// </summary>
public sealed class GainMapImage
{
    /// <summary>The SDR base rendition — ordinary display-referred sRGB, 8-bit.</summary>
    public RasterImage Base { get; }

    /// <summary>The gain map raster, 8-bit; single-channel maps decoded through a JPEG
    /// codec surface as RGB with identical channels, which the math handles uniformly.</summary>
    public RasterImage GainMap { get; }

    /// <summary>The scalar hdrgm parameters describing how to undo the tone mapping.</summary>
    public GainMapMetadata Metadata { get; }

    public GainMapImage(RasterImage baseImage, RasterImage gainMap, GainMapMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        ArgumentNullException.ThrowIfNull(gainMap);
        ArgumentNullException.ThrowIfNull(metadata);
        if (baseImage.SampleFormat != SampleFormat.UInt8 || gainMap.SampleFormat != SampleFormat.UInt8)
            throw new ArgumentException("Gain-map reconstruction is defined on 8-bit base and map rasters.");
        metadata.Validate();
        Base = baseImage;
        GainMap = gainMap;
        Metadata = metadata;
    }

    /// <summary>
    /// Reconstructs HDR linear RGB (<see cref="SampleFormat.Float32"/>, 3 channels,
    /// <see cref="ColorEncoding"/> = Linear + DisplayReferred, 1.0 = SDR white) for a
    /// display with <paramref name="displayHeadroom"/> × SDR-white peak luminance.
    /// Headroom 1.0 (or anything ≤ <see cref="GainMapMetadata.HdrCapacityMin"/>)
    /// reproduces the linearized base exactly; ≥ <see cref="GainMapMetadata.HdrCapacityMax"/>
    /// applies the full authored gain; in between interpolates in log space.
    /// The gain map is bilinearly upsampled to the base's resolution.
    /// </summary>
    public RasterImage ReconstructHdr(double displayHeadroom) =>
        // The math lives in SharpAstro.GainMap (shared with the HEIF carrier); this is the 8-bit sRGB form.
        GainMapReconstructor.ReconstructFromSrgb8(
            Base.Pixels, Base.Width, Base.Height, Base.Channels,
            GainMap.Pixels, GainMap.Width, GainMap.Height, GainMap.Channels,
            Metadata, displayHeadroom);

    /// <summary>The gain-application weight W ∈ [0,1] for a display headroom — the
    /// log-space position between <see cref="GainMapMetadata.HdrCapacityMin"/> and
    /// <see cref="GainMapMetadata.HdrCapacityMax"/>.</summary>
    public double Weight(double displayHeadroom) => Metadata.Weight(displayHeadroom);
}
