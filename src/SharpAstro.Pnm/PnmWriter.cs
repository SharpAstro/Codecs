using System.Text;

namespace SharpAstro.Pnm;

/// <summary>
/// Writes Netpbm files: <c>P5</c> (PGM) and <c>P6</c> (PPM) for grey and RGB, and <c>P7</c> (PAM) for
/// grey, grey + alpha, RGB and RGB + alpha. Output is always the raw (binary) form; samples are the
/// file's native values, never scaled, so a <c>maxval</c> 1023 image holds 0..1023.
/// <para>
/// A <c>maxval</c> up to 255 writes one byte per sample, above 255 two bytes, big-endian, which is how
/// the Netpbm documents lay out a raw raster. The header is exactly what Netpbm's own tools write
/// (<c>P5\nW H\nMAXVAL\n</c>; for PAM <c>WIDTH</c>, <c>HEIGHT</c>, <c>DEPTH</c>, <c>MAXVAL</c>,
/// <c>TUPLTYPE</c>, <c>ENDHDR</c>, one per line), with no comment.
/// </para>
/// <para>
/// A PNM cannot carry alpha outside PAM, so a grey + alpha or RGB + alpha image is always written as
/// <c>P7</c>; a plain grey or RGB image is written as <c>P5</c>/<c>P6</c> unless <c>pam</c> asks for <c>P7</c>.
/// </para>
/// </summary>
public static class PnmWriter
{
    /// <summary>Samples are written to a stream this many bytes at a time at most.</summary>
    private const int StreamChunkBytes = 1 << 16;

    /// <summary>
    /// Encodes <paramref name="samples"/> (interleaved, row-major, native values 0..<paramref name="maxValue"/>)
    /// as a Netpbm file.
    /// </summary>
    /// <param name="samples">At least <c>width * height * channels</c> samples; any beyond that are ignored.</param>
    /// <param name="width">Pixels across (&gt; 0).</param>
    /// <param name="height">Pixels down (&gt; 0).</param>
    /// <param name="tupleType"><see cref="PnmTupleType.Grayscale"/>, <see cref="PnmTupleType.GrayscaleAlpha"/>, <see cref="PnmTupleType.Rgb"/> or <see cref="PnmTupleType.RgbAlpha"/>.</param>
    /// <param name="maxValue">The native maximum sample value, 1..65535.</param>
    /// <param name="pam">Write <c>P7</c> even for a grey or RGB image that <c>P5</c>/<c>P6</c> could carry.</param>
    /// <exception cref="ArgumentException">A dimension, the tuple type or <paramref name="maxValue"/> is out of range, the buffer is short, or a sample exceeds <paramref name="maxValue"/>.</exception>
    public static byte[] Encode(ReadOnlySpan<ushort> samples, int width, int height, PnmTupleType tupleType, int maxValue = 65535, bool pam = false)
    {
        var (header, count, bytesPerSample) = Prepare(samples.Length, width, height, tupleType, maxValue, pam);
        CheckSamples(samples[..count], maxValue);

        var result = new byte[header.Length + count * bytesPerSample];
        header.CopyTo(result, 0);
        Fill(samples[..count], result.AsSpan(header.Length), bytesPerSample);
        return result;
    }

    /// <summary>
    /// Encodes 8-bit <paramref name="samples"/> as a Netpbm file; <paramref name="maxValue"/> is at most 255 (the
    /// default writes full-scale samples).
    /// </summary>
    /// <inheritdoc cref="Encode(ReadOnlySpan{ushort}, int, int, PnmTupleType, int, bool)"/>
    public static byte[] Encode(ReadOnlySpan<byte> samples, int width, int height, PnmTupleType tupleType, int maxValue = 255, bool pam = false)
    {
        if (maxValue > 255)
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "byte samples cannot exceed maxval 255; use the ushort overload.");

        var (header, count, _) = Prepare(samples.Length, width, height, tupleType, maxValue, pam);
        samples = samples[..count];
        if (maxValue < 255) CheckSamples(samples, maxValue);

        var result = new byte[header.Length + count];
        header.CopyTo(result, 0);
        samples.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    /// <summary>Writes <paramref name="samples"/> to <paramref name="destination"/> as a Netpbm file; see <see cref="Encode(ReadOnlySpan{ushort}, int, int, PnmTupleType, int, bool)"/>.</summary>
    public static void Write(Stream destination, ReadOnlySpan<ushort> samples, int width, int height, PnmTupleType tupleType, int maxValue = 65535, bool pam = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var (header, count, bytesPerSample) = Prepare(samples.Length, width, height, tupleType, maxValue, pam);
        samples = samples[..count];
        CheckSamples(samples, maxValue);

        destination.Write(header);
        var chunkSamples = StreamChunkBytes / bytesPerSample;
        var buffer = new byte[Math.Min(count, chunkSamples) * bytesPerSample];
        for (var done = 0; done < count; done += chunkSamples)
        {
            var n = Math.Min(chunkSamples, count - done);
            Fill(samples.Slice(done, n), buffer, bytesPerSample);
            destination.Write(buffer, 0, n * bytesPerSample);
        }
    }

    /// <summary>Writes 8-bit <paramref name="samples"/> to <paramref name="destination"/> as a Netpbm file; see <see cref="Encode(ReadOnlySpan{byte}, int, int, PnmTupleType, int, bool)"/>.</summary>
    public static void Write(Stream destination, ReadOnlySpan<byte> samples, int width, int height, PnmTupleType tupleType, int maxValue = 255, bool pam = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (maxValue > 255)
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "byte samples cannot exceed maxval 255; use the ushort overload.");

        var (header, count, _) = Prepare(samples.Length, width, height, tupleType, maxValue, pam);
        samples = samples[..count];
        if (maxValue < 255) CheckSamples(samples, maxValue);

        destination.Write(header);
        destination.Write(samples);
    }

    /// <summary>Validates the arguments and builds the header; returns it with the sample count and bytes per sample.</summary>
    private static (byte[] Header, int Count, int BytesPerSample) Prepare(int available, int width, int height, PnmTupleType tupleType, int maxValue, bool pam)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "height must be positive.");
        if (maxValue is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "maxval must be 1..65535.");

        string tupleName;
        int channels;
        switch (tupleType)
        {
            case PnmTupleType.Grayscale: tupleName = "GRAYSCALE"; channels = 1; break;
            case PnmTupleType.GrayscaleAlpha: tupleName = "GRAYSCALE_ALPHA"; channels = 2; break;
            case PnmTupleType.Rgb: tupleName = "RGB"; channels = 3; break;
            case PnmTupleType.RgbAlpha: tupleName = "RGB_ALPHA"; channels = 4; break;
            default:
                throw new ArgumentException($"tuple type {tupleType} cannot be written; use Grayscale, GrayscaleAlpha, Rgb or RgbAlpha.", nameof(tupleType));
        }

        var count = (long)width * height * channels;
        var bytesPerSample = maxValue > 255 ? 2 : 1;
        if (count * bytesPerSample > Array.MaxLength - 256)
            throw new ArgumentException($"{width} x {height} x {channels} samples is too large to write in one array.");
        if (available < count)
            throw new ArgumentException($"{width} x {height} x {channels} needs {count} samples, but only {available} were given.", "samples");

        var text = pam || channels is 2 or 4
            ? $"P7\nWIDTH {width}\nHEIGHT {height}\nDEPTH {channels}\nMAXVAL {maxValue}\nTUPLTYPE {tupleName}\nENDHDR\n"
            : $"{(channels == 1 ? "P5" : "P6")}\n{width} {height}\n{maxValue}\n";
        return (Encoding.ASCII.GetBytes(text), (int)count, bytesPerSample);
    }

    private static void CheckSamples(ReadOnlySpan<ushort> samples, int maxValue)
    {
        if (maxValue == 65535) return;
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] > maxValue)
                throw new ArgumentException($"sample {i} is {samples[i]}, above maxval {maxValue}.", "samples");
        }
    }

    private static void CheckSamples(ReadOnlySpan<byte> samples, int maxValue)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] > maxValue)
                throw new ArgumentException($"sample {i} is {samples[i]}, above maxval {maxValue}.", "samples");
        }
    }

    /// <summary>Lays samples out as the raster: one byte each, or two big-endian.</summary>
    private static void Fill(ReadOnlySpan<ushort> samples, Span<byte> destination, int bytesPerSample)
    {
        if (bytesPerSample == 1)
        {
            for (var i = 0; i < samples.Length; i++) destination[i] = (byte)samples[i];
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
            {
                destination[2 * i] = (byte)(samples[i] >> 8);
                destination[2 * i + 1] = (byte)samples[i];
            }
        }
    }
}
