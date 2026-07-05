using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Png;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for PNG, bridging <see cref="PngReader"/>
/// into the <c>SharpAstro.Codecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>): greyscale / RGB / greyscale+alpha /
/// RGBA preserve their native channel count and bit depth (16-bit is converted from
/// PNG's big-endian to the host byte order required by <see cref="IDecodedImage"/>);
/// indexed images are palette-expanded to 8-bit RGBA (with tRNS alpha when present).
/// Colour signalling survives the adapter: cICP (and gAMA 1.0 → linear) maps onto
/// <see cref="IDecodedImage.ColorEncoding"/>, so PNG-3 HDR tagging isn't dropped
/// at the facade boundary.
/// </para>
/// </summary>
public sealed class PngImageDecoder : IImageDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <inheritdoc />
    public static int SignatureLength => 8;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(Signature);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        // 8 signature + 4 length + 4 "IHDR" + 13 IHDR payload = 33 bytes minimum.
        if (data.Length < 33 || !CanDecode(data)) return false;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;

        var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        int bitDepth = data[24];
        int colorType = data[25];
        if (width <= 0 || height <= 0) return false;
        if (!TryMapLayout(colorType, bitDepth, out var channels, out var format)) return false;

        info = new ImageInfo(width, height, channels, format);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        try
        {
            image = ToDecoded(PngReader.Decode(data));
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>False for cICP PQ/HLG-tagged files: their integer code values are
    /// encoded for an HDR EOTF, so the code-value expansion would render them
    /// crushed/wrong-gamut - the same "no canonical 8-bit projection" refusal the
    /// float codecs make. Use <see cref="TryDecode"/> + a colour-managed
    /// conversion instead. Untagged, sRGB-tagged, and gAMA-linear files keep the
    /// display path (linear is monotone and the astro-master quick-look case;
    /// HLG's nominal SDR fallback was considered, but the facade can't know the
    /// display context, so HLG refuses alongside PQ).</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        try
        {
            var png = PngReader.Decode(data);
            if (png.Cicp is { } cicp &&
                cicp.TransferFunction is TransferFunction.Pq or TransferFunction.Hlg)
                return false;
            if (rgbaDestination.Length < (long)png.Width * png.Height * 4) return false;
            png.ExpandToRgba8(rgbaDestination);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    // The layout TryDecode produces. Indexed always expands to RGBA8; the rest keep
    // their native channel count and bit depth. Mirrors the ToDecoded mapping below.
    private static bool TryMapLayout(int colorType, int bitDepth, out int channels, out SampleFormat format)
    {
        format = bitDepth == 16 ? SampleFormat.UInt16 : SampleFormat.UInt8;
        switch (colorType)
        {
            case 0: channels = 1; break;                                    // greyscale
            case 2: channels = 3; break;                                    // RGB
            case 3: channels = 4; format = SampleFormat.UInt8; break;       // indexed -> palette-expanded RGBA8
            case 4: channels = 2; break;                                    // greyscale + alpha
            case 6: channels = 4; break;                                    // RGBA
            default: channels = 0; return false;
        }
        return true;
    }

    private static RasterImage ToDecoded(PngImage png)
    {
        var icc = png.IccProfile;
        var color = MapColorEncoding(png);

        // Indexed: palette (+ optional tRNS) expansion to RGBA8 - PngImage.ToRgba8 already does this.
        if (png.ColorType == 3)
            return new RasterImage(png.Width, png.Height, 4, SampleFormat.UInt8, png.ToRgba8(), icc, color);

        var channels = PngReader.SamplesPerPixel(png.ColorType);
        if (png.BitDepth == 16)
        {
            // PngImage.Pixels holds 16-bit samples big-endian; IDecodedImage requires
            // host byte order. AsUInt16Samples returns host-native ushort[].
            var samples = png.AsUInt16Samples();
            var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
            return new RasterImage(png.Width, png.Height, channels, SampleFormat.UInt16, bytes, icc, color);
        }

        // 8-bit: Pixels are already the interleaved host-order raster.
        return new RasterImage(png.Width, png.Height, channels, SampleFormat.UInt8, png.Pixels, icc, color);
    }

    // cICP is authoritative when present (PNG-3 gives it precedence over iCCP /
    // sRGB / gAMA); CicpChunk and ColorEncoding share the H.273 vocabulary, so
    // the mapping is a straight property copy. gAMA 1.0 declares linear-light
    // samples (e.g. astro masters) - H.273 codepoint 8 (Linear) says exactly
    // that. Anything else (sRGB chunk, gAMA ~1/2.2, untagged) lands on the sRGB
    // assumption; the raw gAMA / cHRM values stay available via PngReader for
    // consumers that need them.
    private static ColorEncoding MapColorEncoding(PngImage png)
    {
        if (png.Cicp is { } cicp)
            return new ColorEncoding
            {
                Primaries = cicp.ColorPrimaries,
                Transfer = cicp.TransferFunction,
                FullRange = cicp.VideoFullRangeFlag,
            };

        if (png.Gamma == 1.0)
            return ColorEncoding.AssumedSrgb with { Transfer = TransferFunction.Linear };

        return ColorEncoding.AssumedSrgb;
    }
}
