using System.Diagnostics.CodeAnalysis;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Tga;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for TGA, bridging <see cref="TgaReader"/> into the
/// <c>SharpAstro.Codecs</c> facade.
/// <para>
/// A TGA has no magic number at the start, so a header guess would claim arbitrary bytes. This adapter therefore
/// recognises a file by its <b>TGA 2.0 footer only</b>: the last 18 bytes must be <c>TRUEVISION-XFILE.\0</c>
/// (the signature that ends the 26-byte footer), and the header must also parse as a TGA <see cref="TgaReader"/>
/// accepts. A TGA 1.0 file, which has no footer, is not recognised by the facade; it still decodes through
/// <see cref="TgaReader"/> directly. The facade passes the whole stream to <see cref="CanDecode"/>, which is what
/// lets it look at the end.
/// </para>
/// <para>
/// Images with alpha (<see cref="TgaInfo.HasAlpha"/>) decode as 8-bit RGBA, the rest as 8-bit RGB; the colour
/// encoding is assumed sRGB. Malformed or unsupported data makes the <c>Try</c> methods return false rather than throw.
/// </para>
/// </summary>
public sealed class TgaImageDecoder : IImageDecoder
{
    private const int HeaderSize = 18;
    private const int FooterSize = 26;

    private static ReadOnlySpan<byte> FooterSignature => "TRUEVISION-XFILE.\0"u8;

    /// <summary>
    /// The smallest file <see cref="CanDecode"/> can accept: an 18-byte header and the 26-byte footer. Unlike the
    /// other codecs the signature it checks is at the end, so it needs the whole stream, which the facade passes.
    /// </summary>
    public static int SignatureLength => HeaderSize + FooterSize;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= SignatureLength &&
        data[^FooterSignature.Length..].SequenceEqual(FooterSignature) &&
        TgaReader.TryReadInfo(data, out _);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (!TgaReader.TryReadInfo(data, out TgaInfo tga))
        {
            info = default;
            return false;
        }

        info = new ImageInfo(tga.Width, tga.Height, tga.HasAlpha ? 4 : 3, SampleFormat.UInt8);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        try
        {
            TgaImage decoded = TgaReader.Decode(data);
            int channels = decoded.HasAlpha ? 4 : 3;
            byte[] pixels = channels == 4 ? decoded.Rgba : ToRgb24(decoded.Rgba, decoded.Width, decoded.Height);
            image = new RasterImage(
                decoded.Width,
                decoded.Height,
                channels,
                SampleFormat.UInt8,
                pixels,
                null,
                ColorEncoding.AssumedSrgb);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Decodes into a caller-owned, tightly packed RGBA8 buffer (alpha 255 when the file has none). Returns false,
    /// without throwing, for malformed or unsupported data or a destination shorter than width × height × 4 bytes.
    /// </summary>
    /// <param name="data">The complete TGA file.</param>
    /// <param name="destination">At least width × height × 4 bytes.</param>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (!TgaReader.TryReadInfo(data, out TgaInfo info))
        {
            return false;
        }

        try
        {
            int requiredLength = checked(info.Width * info.Height * 4);
            if (destination.Length < requiredLength)
            {
                return false;
            }

            TgaReader.Decode(data).Rgba.CopyTo(destination);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

    private static byte[] ToRgb24(byte[] rgba, int width, int height)
    {
        byte[] rgb = new byte[checked(width * height * 3)];
        for (int source = 0, target = 0; source < rgba.Length; source += 4)
        {
            rgb[target++] = rgba[source];
            rgb[target++] = rgba[source + 1];
            rgb[target++] = rgba[source + 2];
        }

        return rgb;
    }
}
