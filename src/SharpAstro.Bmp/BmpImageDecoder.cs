using System.Diagnostics.CodeAnalysis;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Bmp;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for BMP, bridging <see cref="BmpDecoder"/> into the
/// <c>SharpAstro.Codecs</c> facade.
/// <para>
/// Images with an alpha channel decode as 8-bit RGBA, the rest as 8-bit RGB; a V5 embedded ICC profile is
/// passed through, and the colour encoding is left unstated (BMP carries no signalling the facade could
/// use). Malformed or unsupported data (<c>BI_PNG</c>, <c>BI_JPEG</c>, ...) makes the <c>Try</c> methods
/// return false rather than throw.
/// </para>
/// </summary>
public sealed class BmpImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 26;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> data) => BmpDecoder.HasBmpSignature(data);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (!BmpDecoder.TryReadInfo(data, out BmpInfo bmp))
        {
            info = default;
            return false;
        }

        info = bmp.ToImageInfo();
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        if (!BmpDecoder.TryDecode(data, out BmpImage decoded))
        {
            image = null;
            return false;
        }

        image = decoded.ToRasterImage();
        return true;
    }

    /// <summary>
    /// Decodes into a caller-owned, tightly packed RGBA8 buffer (opaque images get alpha 255). Returns false,
    /// without throwing, for malformed or unsupported data or a destination shorter than width × height × 4.
    /// </summary>
    /// <param name="data">The complete BMP file.</param>
    /// <param name="destination">At least width × height × 4 bytes.</param>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (!BmpDecoder.TryReadInfo(data, out BmpInfo info))
        {
            return false;
        }

        long required = (long)info.Width * info.Height * 4;
        if (destination.Length < required || !BmpDecoder.TryDecode(data, out BmpImage decoded))
        {
            return false;
        }

        byte[] pixels = decoded.Pixels;
        Span<byte> target = destination[..(int)required];
        if (decoded.Channels == 4)
        {
            pixels.CopyTo(target);
            return true;
        }

        for (int source = 0, i = 0; source < pixels.Length; source += 3, i += 4)
        {
            target[i] = pixels[source];
            target[i + 1] = pixels[source + 1];
            target[i + 2] = pixels[source + 2];
            target[i + 3] = 255;
        }

        return true;
    }
}
