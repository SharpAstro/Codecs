using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Radiance;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for Radiance RGBE (<c>.hdr</c> / <c>.pic</c>), bridging
/// <see cref="RadianceFile"/> into the <c>SharpAstro.Codecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>): three-channel <see cref="SampleFormat.Float32"/>, the values
/// <see cref="RadianceFile.Read"/> returns. <see cref="IDecodedImage.ColorEncoding"/> reports
/// <see cref="TransferFunction.Linear"/> + <see cref="FloatSemantics.SceneReferred"/>: Radiance stores scene
/// radiance with no fixed white point. An <c>XYZE</c> file is tagged <see cref="ColorPrimaries.SMPTE428"/> (CIE XYZ,
/// H.273 value 10) so its X,Y,Z samples are never mistaken for RGB; they are not converted. An RGBE file keeps the
/// default BT.709 primaries, which is what Radiance's own tools assume absent a <c>PRIMARIES=</c> line (and which this
/// codec does not read). The <c>EXPOSURE=</c> product is not applied and is not part of the facade contract: read it
/// through <see cref="RadianceFile.Read"/> when needed.
/// </para>
/// <para>
/// <see cref="TryDecodeIntoRgba8"/> is always false: projecting scene-referred float to 8 bits needs a
/// consumer-chosen tone/stretch policy.
/// </para>
/// <para>
/// The adapter refuses a picture over 2^26 pixels (<see cref="MaximumPixels"/>), tighter than the reader's own default,
/// because the facade's raster is a byte array (an extra copy of the floats) and tops out below 2 GiB. Use
/// <see cref="RadianceFile.Read"/> directly for larger pictures.
/// </para>
/// </summary>
public sealed class RadianceImageDecoder : IImageDecoder
{
    /// <summary>Largest <c>width * height</c> the adapter decodes (2^26: 768 MiB of floats).</summary>
    public const long MaximumPixels = 1L << 26;

    /// <inheritdoc />
    /// <remarks><c>"#?RADIANCE"</c> is the longer magic, plus its line terminator.</remarks>
    public static int SignatureLength => 11;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => RadianceFile.HasMagic(header);

    /// <inheritdoc />
    /// <remarks>Header only: no scanline is decoded.</remarks>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        RadianceImage header;
        try
        {
            header = RadianceFile.ReadInfo(data, MaximumPixels);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        info = new ImageInfo(header.Width, header.Height, 3, SampleFormat.Float32);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        RadianceImage img;
        try
        {
            img = RadianceFile.Read(data, MaximumPixels);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var color = new ColorEncoding
        {
            Primaries = img.Format == RadianceFormat.Xyze ? ColorPrimaries.SMPTE428 : ColorPrimaries.BT709,
            Transfer = TransferFunction.Linear,
            Float = FloatSemantics.SceneReferred,
        };
        image = new RasterImage(img.Width, img.Height, 3, SampleFormat.Float32,
            MemoryMarshal.AsBytes(img.Pixels.AsSpan()).ToArray(), iccProfile: null, color);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>Always false — see the class remarks.</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) => false;
}
