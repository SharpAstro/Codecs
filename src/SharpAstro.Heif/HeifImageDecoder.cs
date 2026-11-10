using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Heif;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for HEIF (HEVC-coded), bridging <see cref="HeifDecoder"/> into the
/// <c>SharpAstro.Codecs</c> facade. The primary image is decoded; thumbnails go through
/// <see cref="HeifDecoder.Decode"/> with an item from <see cref="HeifFile.ThumbnailsOf"/>.
/// <para>
/// <see cref="TryDecode"/> gives three-channel UInt16 R'G'B', the image's codes scaled to fill the format
/// (as the JPEG 2000 adapter does: the facade divides by 65535), with the file's primaries and transfer in
/// <see cref="IDecodedImage.ColorEncoding"/>. <see cref="TryDecodeIntoRgba8"/> refuses PQ and HLG, whose codes
/// are encoded for an HDR display and have no canonical 8-bit projection (PNG's adapter refuses them alike);
/// <see cref="TryDecodeRgbaFloat"/> is the HDR projection, on the scale PNG's float projection uses.
/// </para>
/// <para>
/// <b>A gain map.</b> When the primary image has one (an ISO/TS 21496-1 <c>tmap</c> or Apple's auxiliary gain map, see
/// <see cref="HeifFile.GainMapOf"/>) the float tier is the HDR reconstruction at the authored headroom through
/// <see cref="HeifGainMap"/> (<c>1.0</c> = SDR white, 203 cd/m² as for PQ): <see cref="TryDecodeRgbaFloat"/>, and
/// <see cref="TryDecode"/> as a <see cref="HeifGainMapDecodedImage"/> of four Float32 channels whose
/// <see cref="HeifGainMapDecodedImage.Source"/> reconstructs at other headrooms. The 8-bit tier,
/// <see cref="TryDecodeIntoRgba8"/>, stays the SDR base. A gain map that cannot be used (malformed, in the backward
/// direction, over a limit, its HEVC data damaged) is not an error: the file is handled as the plain HEIF it also is.
/// </para>
/// </summary>
public sealed class HeifImageDecoder : IImageDecoder
{
    /// <summary>The <c>ftyp</c> box, which names the brands, is the first box; 64 bytes hold any real one.</summary>
    public static int SignatureLength => 64;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => HeifReader.IsHeif(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        try
        {
            var heif = HeifReader.Read(data);
            if (HeifGainMapSource.Find(heif, data, reconstructable: true) is { } gainMap)
            {
                var (hdrWidth, hdrHeight) = DisplaySize(heif.Describe(data, gainMap.Base), gainMap.Tmap);
                info = new ImageInfo(hdrWidth, hdrHeight, 4, SampleFormat.Float32);
                return true;
            }
            var image = PlainImage(heif, data);
            var (width, height) = DisplaySize(image, null);
            info = new ImageInfo(width, height, 3, SampleFormat.UInt16);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        // A gain map is reconstructed here, not on first read, so that one that cannot be (damaged HEVC data) leaves the
        // plain base below, as it does for TryDecodeRgbaFloat.
        if (HeifGainMapSource.TryCreate(data, out var source))
        {
            try
            {
                image = new HeifGainMapDecodedImage(source, source.Reconstruct());
                return true;
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or OutOfMemoryException)
            {
            }
        }
        if (!TryDecodeRgb(data, out var rgb)) return false;
        var max = (1 << rgb.BitDepth) - 1;
        byte[] pixels;
        try
        {
            // Six more bytes a pixel on top of the decoded canvas: a picture that decodes but cannot be copied is
            // false here too, never an exception out of a Try method.
            pixels = new byte[rgb.Samples.Length * 2];
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
        var wide = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
        for (var i = 0; i < rgb.Samples.Length; i++)
            wide[i] = (ushort)((rgb.Samples[i] * 65535 + max / 2) / max);
        image = new RasterImage(rgb.Width, rgb.Height, 3, SampleFormat.UInt16, pixels, rgb.IccProfile, rgb.ColorEncoding);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>False for PQ and HLG images; see the class remarks.</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        if (!TryDecodeRgb(data, out var rgb)) return false;
        return rgbaDestination.Length >= (long)rgb.Width * rgb.Height * 4 && ToRgba8(rgb, rgbaDestination);
    }

    /// <summary>
    /// The codes of <paramref name="rgb"/> scaled to 8 bits, alpha 255, into <paramref name="destination"/> (long enough).
    /// False, writing nothing, for PQ and HLG.
    /// </summary>
    internal static bool ToRgba8(HeifRgbImage rgb, Span<byte> destination)
    {
        if (rgb.ColorEncoding.Transfer is TransferFunction.Pq or TransferFunction.Hlg) return false;
        var max = (1 << rgb.BitDepth) - 1;
        var pixels = rgb.Width * rgb.Height;
        for (var p = 0; p < pixels; p++)
        {
            destination[4 * p] = (byte)((rgb.Samples[3 * p] * 255 + max / 2) / max);
            destination[4 * p + 1] = (byte)((rgb.Samples[3 * p + 1] * 255 + max / 2) / max);
            destination[4 * p + 2] = (byte)((rgb.Samples[3 * p + 2] * 255 + max / 2) / max);
            destination[4 * p + 3] = 255;
        }
        return true;
    }

    /// <summary>
    /// The primary image as linear-light RGBA floats (see <see cref="HeifRgbImage.ToLinearRgbaFloat(out ColorEncoding)"/>):
    /// for PQ and HLG <c>1.0 = 203 cd/m²</c>, primaries reported in <paramref name="encoding"/>. False for a
    /// file this reader cannot decode or a transfer it cannot linearise.
    /// </summary>
    public static bool TryDecodeRgbaFloat(ReadOnlySpan<byte> data, [NotNullWhen(true)] out float[]? rgba, out int width, out int height,
        [NotNullWhen(true)] out ColorEncoding? encoding)
    {
        rgba = null;
        encoding = null;
        width = height = 0;
        try
        {
            var heif = HeifReader.Read(data);
            if (HeifGainMapSource.Find(heif, data, reconstructable: true) is { } gainMap)
            {
                var hdr = HeifGainMap.Decode(data, heif, gainMap);
                (rgba, width, height, encoding) = (hdr.Rgba, hdr.Width, hdr.Height, hdr.ColorEncoding);
                return true;
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or OutOfMemoryException)
        {
            // The plain image below.
        }
        if (!TryDecodeRgb(data, out var rgb)) return false;
        try
        {
            rgba = rgb.ToLinearRgbaFloat(out encoding);
        }
        catch (Exception e) when (e is NotSupportedException or OutOfMemoryException)
        {
            return false;
        }
        width = rgb.Width;
        height = rgb.Height;
        return true;
    }

    private static bool TryDecodeRgb(ReadOnlySpan<byte> data, [NotNullWhen(true)] out HeifRgbImage? rgb)
    {
        rgb = null;
        try
        {
            var heif = HeifReader.Read(data);
            // A tmap primary is the HDR rendition itself, which this decoder cannot decode as an image: the plain tiers
            // show its base, with the tmap's own transformative properties (what a gain-map-unaware reader shows).
            if (heif.PrimaryItem.Type == "tmap")
            {
                if (HeifGainMapSource.Find(heif, data, reconstructable: false) is not { } gainMap) return false;
                rgb = HeifGainMap.DecodeBase(data, gainMap);
                return true;
            }
            rgb = HeifDecoder.Decode(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The primary image, or for a <c>tmap</c> primary the base it derives from.</summary>
    private static HeifImage PlainImage(HeifFile heif, ReadOnlySpan<byte> data)
    {
        if (heif.PrimaryItem.Type != "tmap") return heif.DescribePrimary(data);
        var info = HeifGainMapSource.Find(heif, data, reconstructable: false)
            ?? throw new InvalidDataException("The primary 'tmap' item has no usable gain map.");
        return heif.Describe(data, info.Base);
    }

    /// <summary>
    /// The size after the item's <c>clap</c> and <c>irot</c>, in its order, as <see cref="HeifDecoder.Decode"/> produces it,
    /// then those of <paramref name="tmap"/> (a gain map's own, applied after the base's).
    /// </summary>
    internal static (int Width, int Height) DisplaySize(HeifImage image, HeifItem? tmap)
    {
        int width = (int)image.Width, height = (int)image.Height;
        foreach (var association in image.Item.Properties.Concat(tmap?.Properties ?? []))
        {
            switch (association.Property)
            {
                case HeifCleanAperture clap:
                    // The same rectangle Crop takes, with its checks (a bad clap is InvalidData here too).
                    (_, _, width, height) = Transforms.CleanAperture(width, height, clap);
                    break;
                case HeifRotation { Degrees: 90 or 270 }:
                    (width, height) = (height, width);
                    break;
            }
        }
        return (width, height);
    }
}
