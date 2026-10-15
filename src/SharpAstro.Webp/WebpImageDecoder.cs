using System.Diagnostics.CodeAnalysis;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Webp;

public sealed class WebpImageDecoder : IImageDecoder
{
    public static int SignatureLength => 12;

    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= SignatureLength &&
        data[..4].SequenceEqual("RIFF"u8) &&
        data[8..12].SequenceEqual("WEBP"u8);

    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (!WebpDecoder.TryReadInfo(data, out WebpInfo webpInfo))
        {
            info = default;
            return false;
        }

        info = new ImageInfo(webpInfo.Width, webpInfo.Height, webpInfo.HasAlpha ? 4 : 3, SampleFormat.UInt8);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        try
        {
            WebpImage decoded = WebpDecoder.Decode(data);
            int channels = decoded.HasAlpha ? 4 : 3;
            byte[] pixels = channels == 4
                ? decoded.Rgba
                : ToRgb24(decoded.Rgba, decoded.Width, decoded.Height);
            image = new RasterImage(
                decoded.Width,
                decoded.Height,
                channels,
                SampleFormat.UInt8,
                pixels,
                decoded.IccProfile.ToArray());
            return true;
        }
        catch (Exception)
        {
            image = null;
            return false;
        }
    }

    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (!WebpDecoder.TryReadInfo(data, out WebpInfo info))
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

            WebpDecoder.DecodeTo(data, destination[..requiredLength]);
            return true;
        }
        catch (Exception)
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
