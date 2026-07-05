using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Jxr;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG XR, bridging
/// <see cref="JxrImageCodec"/> into the <c>SharpAstro.Codecs</c> facade. Reads the
/// container's PIXEL_FORMAT GUID and dispatches over the byte-validated decode
/// paths.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>): BD8 (Gray8 / Rgb24 / Bgra32) →
/// <see cref="SampleFormat.UInt8"/>; BD16 (Gray16 / Rgb48) →
/// <see cref="SampleFormat.UInt16"/>; float (BD32F gray, BD16F gray/RGB) →
/// <see cref="SampleFormat.Float32"/> (halves widen losslessly). Formats
/// <see cref="IDecodedImage"/> cannot represent — signed fixed-point
/// (BD16S/BD32S), BGR-ordered, CMYK, YCC, packed 555/565/101010 — return false.
/// </para>
/// <para>
/// Colour meaning: JXR float pixel formats are scRGB — linear light, sRGB/BT.709
/// primaries, <c>1.0f</c> = diffuse white — so
/// <see cref="IDecodedImage.ColorEncoding"/> reports
/// <see cref="TransferFunction.Linear"/> + <see cref="FloatSemantics.DisplayReferred"/>;
/// integer formats keep the sRGB assumption.
/// </para>
/// </summary>
public sealed class JxrImageDecoder : IImageDecoder
{
    // T.833 FILE_HEADER: "II" 0xBC, then the file-version byte (0x01).
    private static ReadOnlySpan<byte> Signature => [0x49, 0x49, 0xBC];

    /// <inheritdoc />
    public static int SignatureLength => 3;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[..3].SequenceEqual(Signature);

    /// <inheritdoc />
    /// <remarks>Parses the container IFD only — the codestream is not decoded.</remarks>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        JxrFile file;
        try
        {
            file = JxrContainer.Read(data);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (!TryMapLayout(file.PixelFormat, out var channels, out var format)) return false;
        info = new ImageInfo((int)file.Width, (int)file.Height, channels, format);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        var ok = TryDecodeCore(data, out var raster);
        image = raster;
        return ok;
    }

    /// <inheritdoc />
    /// <remarks>False for float formats: scRGB linear float needs a colour-managed
    /// projection (sRGB OETF + a highlight policy), not the code-value expansion.
    /// Use <see cref="TryDecode"/> + <see cref="IDecodedImage.ToFloats"/> instead.</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        if (!TryDecodeCore(data, out var image)) return false;
        if (image.SampleFormat == SampleFormat.Float32) return false;
        if (rgbaDestination.Length < (long)image.Width * image.Height * 4) return false;
        image.ExpandToRgba8(rgbaDestination);
        return true;
    }

    // The PIXEL_FORMAT GUIDs this adapter decodes - exactly the set the codec's
    // typed Encode*/Decode* pairs are byte-validated for against jxrlib.
    private static bool TryMapLayout(JxrPixelFormat pf, out int channels, out SampleFormat format)
    {
        (channels, format) = (0, SampleFormat.UInt8);
        if (pf == JxrPixelFormat.Gray8Bpp) (channels, format) = (1, SampleFormat.UInt8);
        else if (pf == JxrPixelFormat.Rgb24Bpp) (channels, format) = (3, SampleFormat.UInt8);
        else if (pf == JxrPixelFormat.Bgra32Bpp) (channels, format) = (4, SampleFormat.UInt8);
        else if (pf == JxrPixelFormat.Gray16Bpp) (channels, format) = (1, SampleFormat.UInt16);
        else if (pf == JxrPixelFormat.Rgb48Bpp) (channels, format) = (3, SampleFormat.UInt16);
        else if (pf == JxrPixelFormat.GrayFloat32Bpp) (channels, format) = (1, SampleFormat.Float32);
        else if (pf == JxrPixelFormat.GrayHalf16Bpp) (channels, format) = (1, SampleFormat.Float32);
        else if (pf == JxrPixelFormat.RgbHalf48Bpp) (channels, format) = (3, SampleFormat.Float32);
        else return false;
        return true;
    }

    private static bool TryDecodeCore(ReadOnlySpan<byte> data, [NotNullWhen(true)] out RasterImage? image)
    {
        image = null;
        try
        {
            var pf = JxrContainer.Read(data).PixelFormat;
            if (!TryMapLayout(pf, out var channels, out var format)) return false;

            // scRGB for the float formats: linear light, 1.0 = diffuse white.
            var color = format == SampleFormat.Float32
                ? new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.DisplayReferred }
                : ColorEncoding.AssumedSrgb;

            if (pf == JxrPixelFormat.Gray8Bpp)
            {
                var (w, h, y) = JxrImageCodec.DecodeGray8(data);
                image = new RasterImage(w, h, 1, format, ToBytes(y), null, color);
            }
            else if (pf == JxrPixelFormat.Rgb24Bpp)
            {
                var (w, h, r, g, b) = JxrImageCodec.DecodeRgb24(data);
                image = new RasterImage(w, h, 3, format, InterleaveBytes(r, g, b), null, color);
            }
            else if (pf == JxrPixelFormat.Bgra32Bpp)
            {
                var (w, h, r, g, b, a) = JxrImageCodec.DecodeRgba32(data);
                image = new RasterImage(w, h, 4, format, InterleaveBytes(r, g, b, a), null, color);
            }
            else if (pf == JxrPixelFormat.Gray16Bpp)
            {
                var (w, h, y) = JxrImageCodec.DecodeGray16(data);
                image = new RasterImage(w, h, 1, format, ToUInt16Bytes(y), null, color);
            }
            else if (pf == JxrPixelFormat.Rgb48Bpp)
            {
                var (w, h, r, g, b) = JxrImageCodec.DecodeRgb48(data);
                image = new RasterImage(w, h, 3, format, InterleaveUInt16Bytes(r, g, b), null, color);
            }
            else if (pf == JxrPixelFormat.GrayFloat32Bpp)
            {
                var (w, h, y) = JxrImageCodec.DecodeGrayF32(data);
                image = new RasterImage(w, h, 1, format, MemoryMarshal.AsBytes<float>(y).ToArray(), null, color);
            }
            else if (pf == JxrPixelFormat.GrayHalf16Bpp)
            {
                var (w, h, y) = JxrImageCodec.DecodeGrayF16(data);
                image = new RasterImage(w, h, 1, format, WidenHalves(y), null, color);
            }
            else // RgbHalf48Bpp (TryMapLayout filtered everything else)
            {
                var (w, h, rgb) = JxrImageCodec.DecodeRgbF16(data);
                image = new RasterImage(w, h, 3, format, WidenHalves(rgb), null, color);
            }

            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[] ToBytes(int[] y)
    {
        var dst = new byte[y.Length];
        for (var i = 0; i < y.Length; i++) dst[i] = (byte)y[i];
        return dst;
    }

    private static byte[] InterleaveBytes(int[] r, int[] g, int[] b)
    {
        var dst = new byte[checked(r.Length * 3)];
        for (int i = 0, d = 0; i < r.Length; i++, d += 3)
        {
            dst[d] = (byte)r[i];
            dst[d + 1] = (byte)g[i];
            dst[d + 2] = (byte)b[i];
        }
        return dst;
    }

    private static byte[] InterleaveBytes(int[] r, int[] g, int[] b, int[] a)
    {
        var dst = new byte[checked(r.Length * 4)];
        for (int i = 0, d = 0; i < r.Length; i++, d += 4)
        {
            dst[d] = (byte)r[i];
            dst[d + 1] = (byte)g[i];
            dst[d + 2] = (byte)b[i];
            dst[d + 3] = (byte)a[i];
        }
        return dst;
    }

    private static byte[] ToUInt16Bytes(int[] y)
    {
        var dst = new ushort[y.Length];
        for (var i = 0; i < y.Length; i++) dst[i] = (ushort)y[i];
        return MemoryMarshal.AsBytes<ushort>(dst).ToArray();
    }

    private static byte[] InterleaveUInt16Bytes(int[] r, int[] g, int[] b)
    {
        var dst = new ushort[checked(r.Length * 3)];
        for (int i = 0, d = 0; i < r.Length; i++, d += 3)
        {
            dst[d] = (ushort)r[i];
            dst[d + 1] = (ushort)g[i];
            dst[d + 2] = (ushort)b[i];
        }
        return MemoryMarshal.AsBytes<ushort>(dst).ToArray();
    }

    private static byte[] WidenHalves(Half[] src)
    {
        var dst = new float[src.Length];
        for (var i = 0; i < src.Length; i++) dst[i] = (float)src[i];
        return MemoryMarshal.AsBytes<float>(dst).ToArray();
    }
}
