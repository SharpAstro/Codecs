using System.Diagnostics.CodeAnalysis;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Gif;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for GIF, bridging <see cref="GifDecoder"/> into the
/// <c>SharpAstro.Codecs</c> facade.
/// <para>
/// Presents the first frame composited onto the logical screen as 8-bit RGBA, always four channels:
/// whether a pixel is transparent depends on the frame and on the area it leaves uncovered, so the
/// header alone cannot promise an opaque image. A truncated file decodes what is there. Malformed
/// data makes the <c>Try</c> methods return false rather than throw.
/// </para>
/// </summary>
public sealed class GifImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 6;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= SignatureLength &&
        (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8));

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (!GifDecoder.TryReadInfo(data, out var gif))
        {
            info = default;
            return false;
        }

        info = new ImageInfo(gif.Width, gif.Height, 4, SampleFormat.UInt8);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        try
        {
            var decoded = GifDecoder.Decode(data);
            image = new RasterImage(decoded.Width, decoded.Height, 4, SampleFormat.UInt8, decoded.Rgba,
                iccProfile: null, ColorEncoding.AssumedSrgb);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Decodes into a caller-owned, tightly packed RGBA8 buffer. Returns false, without throwing, for
    /// malformed data or a destination shorter than width × height × 4 bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        try
        {
            GifDecoder.DecodeTo(data, rgbaDestination);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}
