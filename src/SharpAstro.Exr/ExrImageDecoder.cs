using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Exr;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for OpenEXR, bridging <see cref="ExrFile"/>
/// into the <c>SharpAstro.Codecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>): everything lands as
/// <see cref="SampleFormat.Float32"/> — FLOAT channels verbatim, HALF channels
/// widened losslessly. <c>R</c>/<c>G</c>/<c>B</c> (+ optional <c>A</c>) map to
/// RGB(A); a single channel (any name, e.g. <c>Y</c>) maps to grayscale. UINT
/// channels and other channel sets return false.
/// <see cref="IDecodedImage.ColorEncoding"/> reports
/// <see cref="TransferFunction.Linear"/> + <see cref="FloatSemantics.SceneReferred"/>
/// — EXR is scene-linear light with no fixed white point.
/// </para>
/// <para>
/// <see cref="TryDecodeIntoRgba8"/> is always false: projecting scene-referred
/// float to 8-bit needs a consumer-chosen tone/stretch policy. Decode via
/// <see cref="TryDecode"/> and apply your own transfer over
/// <see cref="IDecodedImage.ToFloats"/> / <see cref="IDecodedImage.Pixels"/>.
/// </para>
/// </summary>
public sealed class ExrImageDecoder : IImageDecoder
{
    // 0x762F3101: the little-endian OpenEXR magic (20000630).
    private static ReadOnlySpan<byte> Signature => [0x76, 0x2F, 0x31, 0x01];

    /// <inheritdoc />
    public static int SignatureLength => 4;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4].SequenceEqual(Signature);

    /// <inheritdoc />
    /// <remarks>The channel list lives in the variable-length attribute header, so
    /// this runs the full reader rather than peeking a fixed-size prefix.</remarks>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (!TryDecodeCore(data, out var image)) return false;
        info = new ImageInfo(image.Width, image.Height, image.Channels, image.SampleFormat);
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
    /// <remarks>Always false — see the class remarks: scene-referred linear float
    /// has no canonical 8-bit projection.</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) => false;

    private static bool TryDecodeCore(ReadOnlySpan<byte> data, [NotNullWhen(true)] out RasterImage? image)
    {
        image = null;
        ExrImage img;
        try
        {
            img = ExrFile.Read(data);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        // Channel selection: R/G/B (+A) as colour, else any single channel as gray.
        int[] indices;
        if (img.HasChannel("R") && img.HasChannel("G") && img.HasChannel("B"))
            indices = img.HasChannel("A")
                ? [img.IndexOf("R"), img.IndexOf("G"), img.IndexOf("B"), img.IndexOf("A")]
                : [img.IndexOf("R"), img.IndexOf("G"), img.IndexOf("B")];
        else if (img.Channels.Count == 1)
            indices = [0];
        else if (img.HasChannel("Y"))
            indices = [img.IndexOf("Y")];
        else
            return false;

        var channels = indices.Length;
        var n = img.Width * img.Height;
        var interleaved = new float[checked(n * channels)];
        for (var c = 0; c < channels; c++)
        {
            var raw = img.GetData(indices[c]);
            switch (img.Channels[indices[c]].Type)
            {
                case ExrPixelType.Float:
                {
                    // Channel data is little-endian; on LE hosts (all supported
                    // .NET targets) the reinterpret is exact.
                    var src = MemoryMarshal.Cast<byte, float>(raw);
                    for (var i = 0; i < n; i++) interleaved[i * channels + c] = src[i];
                    break;
                }
                case ExrPixelType.Half:
                {
                    var src = MemoryMarshal.Cast<byte, Half>(raw);
                    for (var i = 0; i < n; i++) interleaved[i * channels + c] = (float)src[i];
                    break;
                }
                default:
                    return false; // UINT: not a light-linear sample; no honest Float32 mapping
            }
        }

        // Scene-linear light, open-ended range. Primaries default to BT.709 (what
        // readers assume when the chromaticities attribute is absent); a
        // chromaticities-aware mapping is a follow-up.
        var color = new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.SceneReferred };
        image = new RasterImage(img.Width, img.Height, channels, SampleFormat.Float32,
            MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray(), iccProfile: null, color);
        return true;
    }
}
