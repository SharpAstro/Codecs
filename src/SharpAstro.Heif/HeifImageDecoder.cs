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
            var image = heif.DescribePrimary(data);
            var (width, height) = DisplaySize(image);
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
        if (!TryDecodeRgb(data, out var rgb)) return false;
        var max = (1 << rgb.BitDepth) - 1;
        var pixels = new byte[rgb.Samples.Length * 2];
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
        if (rgb.ColorEncoding.Transfer is TransferFunction.Pq or TransferFunction.Hlg) return false;
        if (rgbaDestination.Length < (long)rgb.Width * rgb.Height * 4) return false;
        var max = (1 << rgb.BitDepth) - 1;
        var pixels = rgb.Width * rgb.Height;
        for (var p = 0; p < pixels; p++)
        {
            rgbaDestination[4 * p] = (byte)((rgb.Samples[3 * p] * 255 + max / 2) / max);
            rgbaDestination[4 * p + 1] = (byte)((rgb.Samples[3 * p + 1] * 255 + max / 2) / max);
            rgbaDestination[4 * p + 2] = (byte)((rgb.Samples[3 * p + 2] * 255 + max / 2) / max);
            rgbaDestination[4 * p + 3] = 255;
        }
        return true;
    }

    /// <summary>
    /// The primary image as linear-light RGBA floats (see <see cref="HeifRgbImage.ToLinearRgbaFloat"/>):
    /// for PQ and HLG <c>1.0 = 203 cd/m²</c>, primaries reported in <paramref name="encoding"/>. False for a
    /// file this reader cannot decode or a transfer it cannot linearise.
    /// </summary>
    public static bool TryDecodeRgbaFloat(ReadOnlySpan<byte> data, [NotNullWhen(true)] out float[]? rgba, out int width, out int height,
        [NotNullWhen(true)] out ColorEncoding? encoding)
    {
        rgba = null;
        encoding = null;
        width = height = 0;
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
            rgb = HeifDecoder.Decode(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The size after the item's <c>clap</c> and <c>irot</c>, in its order, as <see cref="HeifDecoder.Decode"/> produces it.</summary>
    private static (int Width, int Height) DisplaySize(HeifImage image)
    {
        int width = (int)image.Width, height = (int)image.Height;
        foreach (var association in image.Item.Properties)
        {
            switch (association.Property)
            {
                case HeifCleanAperture { WidthD: > 0, HeightD: > 0 } clap:
                    (width, height) = ((int)Math.Round((double)clap.WidthN / clap.WidthD), (int)Math.Round((double)clap.HeightN / clap.HeightD));
                    break;
                case HeifRotation { Degrees: 90 or 270 }:
                    (width, height) = (height, width);
                    break;
            }
        }
        return (width, height);
    }
}
