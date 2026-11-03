using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Pnm;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for Netpbm (P1-P7), bridging <see cref="PnmReader"/> into the
/// <c>SharpAstro.Codecs</c> facade.
/// <para>
/// The result has 1 (grey), 2 (grey + alpha), 3 (RGB) or 4 (RGBA) channels, as UInt8 when the file's
/// <c>maxval</c> is at most 255 and UInt16 above that, with samples <b>scaled to fill the format</b>
/// (the facade divides by the format's maximum): <c>maxval</c> 255 and 65535 pass through untouched, any
/// other is scaled to 0..255 or 0..65535. PBM bitmaps become 0 (black) / 255 (white). The scaling
/// reproduces ImageMagick's values exactly, so a <c>maxval</c> 15 file decodes to the same pixels
/// ImageMagick gives (see <see cref="ScaleTable"/>).
/// </para>
/// <para>
/// Where ImageMagick departs from the Netpbm documents, the documents win. Its binary (P4-P7) reader
/// takes a raster whose <c>maxval</c> needs exactly 8 or 16 bits (128..254, 32768..65534) as full-scale
/// samples without dividing by <c>maxval</c>, so a sample equal to <c>maxval</c> stays 254 instead of
/// becoming white; and it reads a 1-bit grey+alpha PAM with the alpha inverted. This decoder divides
/// by <c>maxval</c> always (its plain P2/P3 reader does too, and agrees to the sample).
/// </para>
/// <para>
/// Netpbm has no colour-management signalling, so the colour encoding is reported as assumed sRGB.
/// Malformed, truncated or unsupported data makes the <c>Try</c> methods return false rather than throw.
/// </para>
/// </summary>
public sealed class PnmImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 3;

    /// <inheritdoc />
    /// <remarks>
    /// <c>P</c>, a digit <c>1</c>..<c>7</c> and a whitespace byte: every Netpbm header has them, and
    /// requiring the whitespace keeps <c>P8</c>, <c>P0</c> and <c>P5x</c> out. A positive result
    /// only means the signature matched.
    /// </remarks>
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= SignatureLength &&
        header[0] == (byte)'P' &&
        header[1] is >= (byte)'1' and <= (byte)'7' &&
        header[2] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            var header = PnmReader.ReadHeader(data);
            info = new ImageInfo(header.Width, header.Height, header.Channels, FormatFor(header.MaxValue));
            return true;
        }
        catch (Exception e) when (IsDecodeFailure(e))
        {
            info = default;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        try
        {
            image = ToRaster(PnmReader.Read(data));
            return true;
        }
        catch (Exception e) when (IsDecodeFailure(e))
        {
            image = null;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        try
        {
            var header = PnmReader.ReadHeader(data);
            if (rgbaDestination.Length < (long)header.Width * header.Height * 4)
                return false;

            ToRaster(PnmReader.Read(data)).ExpandToRgba8(rgbaDestination);
            return true;
        }
        catch (Exception e) when (IsDecodeFailure(e))
        {
            return false;
        }
    }

    private static bool IsDecodeFailure(Exception e) =>
        e is InvalidDataException or NotSupportedException or ArgumentException or OverflowException or OutOfMemoryException;

    private static SampleFormat FormatFor(int maxValue) => maxValue <= 255 ? SampleFormat.UInt8 : SampleFormat.UInt16;

    /// <summary>
    /// Scales a native image to fill UInt8 (<c>maxval</c> up to 255) or UInt16, as a
    /// <see cref="RasterImage"/>.
    /// </summary>
    internal static RasterImage ToRaster(PnmImage image)
    {
        var format = FormatFor(image.MaxValue);
        var samples = image.Samples;
        var table = ScaleTable(image.MaxValue);
        if (format == SampleFormat.UInt8)
        {
            var bytes = new byte[samples.Length];
            if (table is null)
            {
                for (var i = 0; i < samples.Length; i++) bytes[i] = (byte)samples[i];
            }
            else
            {
                for (var i = 0; i < samples.Length; i++) bytes[i] = (byte)table[samples[i]];
            }

            return new RasterImage(image.Width, image.Height, image.Channels, format, bytes);
        }

        var pixels = new byte[checked(samples.Length * 2)];
        Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
        if (table is null)
        {
            samples.CopyTo(wide);
        }
        else
        {
            for (var i = 0; i < samples.Length; i++) wide[i] = table[samples[i]];
        }

        return new RasterImage(image.Width, image.Height, image.Channels, format, pixels);
    }

    /// <summary>
    /// The map from a native sample (0..<paramref name="maxValue"/>) to the format's full range, or
    /// null when the file already fills it (255 or 65535).
    /// <para>
    /// 8-bit is <c>round(255 * v / maxval)</c>, half up, in exact integer arithmetic. 16-bit is the
    /// same with 65535, but evaluated as ImageMagick does: in <c>float</c>, as
    /// <c>floor((float)(65535.0 * v / maxval) + 0.5f)</c>. That is deliberate parity, not a better
    /// rounding: a sample whose exact value is a hair under a half (for <c>maxval</c> 4095, 7 of them)
    /// rounds up in single precision, and "decodes to ImageMagick's pixels exactly" is the contract.
    /// </para>
    /// </summary>
    internal static ushort[]? ScaleTable(int maxValue)
    {
        if (maxValue is 255 or 65535) return null;

        var table = new ushort[maxValue + 1];
        if (maxValue < 255)
        {
            for (var v = 0; v <= maxValue; v++)
                table[v] = (ushort)((v * 510 + maxValue) / (2 * maxValue));
        }
        else
        {
            for (var v = 0; v <= maxValue; v++)
            {
                var scaled = MathF.Floor((float)(65535.0 * v / maxValue) + 0.5f);
                table[v] = (ushort)Math.Min(scaled, 65535f);
            }
        }

        return table;
    }
}
