using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Jxl;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG XL, bridging
/// <see cref="JxlImageCodec.Decode(ReadOnlySpan{byte})"/> (auto-detects Modular vs
/// VarDCT) into the <c>SharpAstro.Codecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>): integer images land as
/// <see cref="SampleFormat.UInt8"/> (≤ 8 bits) or <see cref="SampleFormat.UInt16"/>
/// (9–16 bits); float images (F16/F32, values verbatim) as
/// <see cref="SampleFormat.Float32"/> with halves widened losslessly —
/// <see cref="IDecodedImage.ColorEncoding"/> then reports
/// <see cref="TransferFunction.Linear"/> + <see cref="FloatSemantics.SceneReferred"/>.
/// Constructs outside the codec's scope (alpha, multi-group Modular, …) return
/// false.
/// </para>
/// </summary>
public sealed class JxlImageDecoder : IImageDecoder
{
    // ISOBMFF container signature (the bare codestream form is just FF 0A).
    private static ReadOnlySpan<byte> ContainerSignature =>
        [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    /// <inheritdoc />
    public static int SignatureLength => 12;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        (header.Length >= 2 && header[0] == 0xFF && header[1] == 0x0A) ||
        (header.Length >= 12 && header[..12].SequenceEqual(ContainerSignature));

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        JxlImageInfo ji;
        try
        {
            ji = JxlFile.ReadInfo(data);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (ji.HasAlpha) return false; // alpha is outside the codec's decode scope
        var channels = ji.IsGrayscale ? 1 : 3;
        SampleFormat format;
        if (ji.IsFloat) format = SampleFormat.Float32;
        else if (ji.BitsPerSample <= 8) format = SampleFormat.UInt8;
        else if (ji.BitsPerSample <= 16) format = SampleFormat.UInt16;
        else return false;

        info = new ImageInfo(ji.Width, ji.Height, channels, format);
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
    /// <remarks>False for float images — verbatim HDR float has no canonical 8-bit
    /// projection; use <see cref="TryDecode"/> + <see cref="IDecodedImage.ToFloats"/>.</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        if (!TryDecodeCore(data, out var image)) return false;
        if (image.SampleFormat == SampleFormat.Float32) return false;
        if (rgbaDestination.Length < (long)image.Width * image.Height * 4) return false;
        image.ExpandToRgba8(rgbaDestination);
        return true;
    }

    private static bool TryDecodeCore(ReadOnlySpan<byte> data, [NotNullWhen(true)] out RasterImage? image)
    {
        image = null;
        JxlImage img;
        try
        {
            img = JxlImageCodec.Decode(data);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        var channels = img.ColorChannels; // 1 (grey) or 3 (RGB)
        var n = img.Width * img.Height;

        if (img.FloatingPoint)
        {
            // Channels hold IEEE bit patterns (F32 = the 32-bit pattern, F16 = the
            // half pattern sign-extended); reconstruct and widen to Float32.
            var interleaved = new float[checked(n * channels)];
            for (var c = 0; c < channels; c++)
            {
                var src = img.Channels[c];
                if (img.BitsPerSample == 32)
                    for (var i = 0; i < n; i++) interleaved[i * channels + c] = BitConverter.Int32BitsToSingle(src[i]);
                else if (img.BitsPerSample == 16)
                    for (var i = 0; i < n; i++) interleaved[i * channels + c] = (float)BitConverter.Int16BitsToHalf((short)src[i]);
                else
                    return false;
            }

            // Float samples are verbatim linear values with no fixed white point
            // (the astro HDR-master shape) - scene-referred.
            var color = new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.SceneReferred };
            image = new RasterImage(img.Width, img.Height, channels, SampleFormat.Float32,
                MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray(), iccProfile: null, color);
            return true;
        }

        if (img.BitsPerSample <= 8)
        {
            var dst = new byte[checked(n * channels)];
            for (var c = 0; c < channels; c++)
            {
                var src = img.Channels[c];
                for (var i = 0; i < n; i++) dst[i * channels + c] = (byte)src[i];
            }
            image = new RasterImage(img.Width, img.Height, channels, SampleFormat.UInt8, dst);
            return true;
        }

        if (img.BitsPerSample <= 16)
        {
            var dst = new ushort[checked(n * channels)];
            for (var c = 0; c < channels; c++)
            {
                var src = img.Channels[c];
                for (var i = 0; i < n; i++) dst[i * channels + c] = (ushort)src[i];
            }
            image = new RasterImage(img.Width, img.Height, channels, SampleFormat.UInt16,
                MemoryMarshal.AsBytes<ushort>(dst).ToArray());
            return true;
        }

        return false;
    }
}
