using System.Diagnostics.CodeAnalysis;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.GainMap;
using SharpAstro.Jpeg;

namespace SharpAstro.Heif;

/// <summary>
/// The gain map of a HEIF file kept together with the bytes it was found in: the escape hatch behind
/// <see cref="HeifGainMapDecodedImage.Source"/>, for a display headroom other than the authored one
/// (<see cref="Reconstruct"/>) and for the SDR base (<see cref="DecodeBase"/>).
/// </summary>
public sealed class HeifGainMapSource
{
    private readonly byte[] _file;
    private readonly HeifFile _heif;

    private HeifGainMapSource(byte[] file, HeifFile heif, HeifGainMapInfo info, int width, int height)
    {
        _file = file;
        _heif = heif;
        Info = info;
        DisplayWidth = width;
        DisplayHeight = height;
    }

    /// <summary>The gain map as found (<see cref="HeifGainMapInfo.Dialect"/> says whether it is the <c>tmap</c> or Apple's).</summary>
    public HeifGainMapInfo Info { get; }

    /// <summary>The ISO/TS 21496-1 metadata (for Apple's dialect the single-channel form built from its headroom).</summary>
    public GainMapMetadata Metadata => Info.Metadata;

    /// <summary>The headroom the gain map was authored for, a linear ratio to SDR white: what the facade reconstructs at.</summary>
    public double AuthoredHeadroom => Metadata.HdrCapacityMax;

    /// <summary>The size of the reconstruction, and of the base after the same transformative properties.</summary>
    public int DisplayWidth { get; }

    /// <inheritdoc cref="DisplayWidth"/>
    public int DisplayHeight { get; }

    /// <summary>
    /// The HDR rendition for a display headroom (a linear ratio to SDR white), the authored one when null; see
    /// <see cref="HeifGainMap"/> for what is read and how the weight is applied.
    /// </summary>
    /// <exception cref="InvalidDataException">The structure or the HEVC data is malformed.</exception>
    /// <exception cref="NotSupportedException">Something this reader does not handle.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayHeadroom"/> is not a positive finite number.</exception>
    public HeifGainMapImage Reconstruct(double? displayHeadroom = null) => HeifGainMap.Decode(_file, _heif, Info, displayHeadroom);

    /// <summary>
    /// The SDR base as R'G'B' codes: the base item decoded as any HEIF image is, then the <c>tmap</c> item's own
    /// <c>clap</c>/<c>irot</c>/<c>imir</c> (when the <c>tmap</c> is the primary item they are what makes the base the displayed image).
    /// </summary>
    public HeifRgbImage DecodeBase() => HeifGainMap.DecodeBase(_file, Info);

    /// <summary>
    /// The gain map of the primary item that the facade can reconstruct: found, in the SDR-base direction, with a base
    /// inside <see cref="HeifLimits.MaxGainMapBasePixels"/> and a map no larger than it. Everything else (no gain map,
    /// a malformed or unsupported one, a limit) is false, and the file is then handled as a plain HEIF.
    /// </summary>
    internal static HeifGainMapInfo? Find(HeifFile heif, ReadOnlySpan<byte> file, bool reconstructable)
    {
        try
        {
            var info = heif.GainMapOf(file, heif.PrimaryItem);
            if (info is null || !reconstructable) return info;
            if (info.Metadata.BaseRenditionIsHdr) return null;
            var baseImage = heif.Describe(file, info.Base);
            var mapImage = heif.Describe(file, info.Map);
            long basePixels = (long)baseImage.Width * baseImage.Height, mapPixels = (long)mapImage.Width * mapImage.Height;
            return basePixels > HeifLimits.MaxGainMapBasePixels || mapPixels > basePixels || !HeifGainMap.MapMatchesBaseShape(baseImage, mapImage, info.Dialect) ? null : info;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The same, as a source that keeps its own copy of <paramref name="data"/>.</summary>
    public static bool TryCreate(ReadOnlySpan<byte> data, [NotNullWhen(true)] out HeifGainMapSource? source)
    {
        source = null;
        try
        {
            var heif = HeifReader.Read(data);
            if (Find(heif, data, true) is not { } info) return false;
            var (width, height) = HeifImageDecoder.DisplaySize(heif.Describe(data, info.Base), info.Tmap);
            source = new HeifGainMapSource(data.ToArray(), heif, info, width, height);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or OutOfMemoryException)
        {
            return false;
        }
    }
}

/// <summary>
/// The <see cref="IDecodedImage"/> a gain-map HEIF surfaces through the <c>SharpAstro.Codecs</c> facade, split by tier as
/// <c>GainMapDecodedImage</c> is for a gain-map JPEG:
/// <list type="bullet">
/// <item><b>The float path is HDR.</b> <see cref="Pixels"/> and <see cref="ToFloats"/> are the reconstruction at the
/// authored headroom (<see cref="HeifGainMapSource.AuthoredHeadroom"/>): linear, display-referred, <c>1.0</c> = SDR white
/// (203 cd/m² as for PQ), in the base's primaries, RGBA with alpha 1.</item>
/// <item><b>The int path is SDR.</b> <see cref="ToRgba8"/> (and the facade's <c>TryDecodeIntoRgba8</c>) return the base
/// image, the one a gain-map-unaware viewer shows, not a tone map of the floats.</item>
/// </list>
/// The reconstruction is made when the image is created, so that a file whose gain map cannot be decoded is handed back
/// by the facade as the plain base instead of failing on the first read; the base is decoded when <see cref="ToRgba8"/>
/// asks for it. For another headroom reach through <see cref="Source"/>.
/// </summary>
public sealed class HeifGainMapDecodedImage : IDecodedImage
{
    private readonly HeifGainMapImage _hdr;

    internal HeifGainMapDecodedImage(HeifGainMapSource source, HeifGainMapImage hdr)
    {
        Source = source;
        _hdr = hdr;
    }

    /// <summary>The gain map and the file it is in: the full API (another display headroom, the base).</summary>
    public HeifGainMapSource Source { get; }

    /// <summary>The reconstruction as the facade made it, at the authored headroom.</summary>
    public HeifGainMapImage Hdr => _hdr;

    /// <inheritdoc />
    public int Width => _hdr.Width;

    /// <inheritdoc />
    public int Height => _hdr.Height;

    /// <summary>4: RGBA floats, alpha 1 (as <c>HeifImageDecoder.TryDecodeRgbaFloat</c> gives them).</summary>
    public int Channels => 4;

    /// <inheritdoc />
    public SampleFormat SampleFormat => SampleFormat.Float32;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Pixels => System.Runtime.InteropServices.MemoryMarshal.AsBytes(_hdr.Rgba.AsSpan());

    /// <summary>Empty: the base's profile describes its encoded codes, not these linear floats; its primaries are in <see cref="ColorEncoding"/>.</summary>
    public ReadOnlySpan<byte> IccProfile => default;

    /// <summary>Linear, the base's primaries, display-referred.</summary>
    public ColorEncoding ColorEncoding => _hdr.ColorEncoding;

    /// <inheritdoc />
    public float[] ToFloats() => (float[])_hdr.Rgba.Clone();

    /// <summary>The SDR base as 8-bit RGBA, which is what the file's own primary image is for a gain-map-unaware reader.</summary>
    /// <exception cref="NotSupportedException">The base is PQ or HLG, which has no canonical 8-bit projection.</exception>
    public byte[] ToRgba8()
    {
        var rgb = Source.DecodeBase();
        var rgba = new byte[checked(rgb.Width * rgb.Height * 4)];
        if (!HeifImageDecoder.ToRgba8(rgb, rgba))
            throw new NotSupportedException("The base image is PQ or HLG, which has no canonical 8-bit RGBA projection.");
        return rgba;
    }
}
