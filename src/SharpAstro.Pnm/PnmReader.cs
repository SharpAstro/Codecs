using System.Text;

namespace SharpAstro.Pnm;

/// <summary>
/// Reads the Netpbm family: <c>P1</c>/<c>P4</c> (PBM), <c>P2</c>/<c>P5</c> (PGM), <c>P3</c>/<c>P6</c>
/// (PPM), plain and raw, and <c>P7</c> (PAM), per the Netpbm format documentation.
/// <para>
/// What the format documents say, and so what this reader does:
/// </para>
/// <list type="bullet">
/// <item>Header tokens are separated by whitespace, and <c>#</c> starts a comment running to the end
/// of the line wherever whitespace is allowed (plain rasters included). A PAM header is line-based
/// (<c>WIDTH</c>/<c>HEIGHT</c>/<c>DEPTH</c>/<c>MAXVAL</c>/<c>TUPLTYPE</c>/<c>ENDHDR</c>), and a line
/// starting with <c>#</c> is a comment.</item>
/// <item>Exactly <b>one</b> whitespace byte follows the last header token of a raw file, and it is
/// part of the format: a raster whose first sample is <c>0x20</c> must not lose it to a greedy skip.</item>
/// <item>PBM is bits, <b>1 = black</b>, most significant bit first, each row padded to a byte (raw),
/// or the characters <c>0</c>/<c>1</c> with <i>no</i> separators needed (plain). PAM's
/// <c>BLACKANDWHITE</c> is the other way round (0 = black); the reader returns both as grey with
/// <c>MaxValue = 1</c> and 0 = black.</item>
/// <item>A <c>maxval</c> up to 255 is one byte per sample; 256..65535 is two, big-endian.</item>
/// </list>
/// <para>
/// Refused rather than guessed: a sample above <c>maxval</c>, a truncated raster, a header
/// out of range, a PAM <c>TUPLTYPE</c> this reader does not know (<c>CMYK</c> and the like), and a
/// raster declared larger than the sample ceiling, which is checked before anything is allocated.
/// A raw raster must also fit in the input, so a tiny file cannot declare a huge image and make the
/// reader allocate for it.
/// </para>
/// </summary>
public static class PnmReader
{
    /// <summary>
    /// The default ceiling on <c>Width * Height * Channels</c>: 2^28 samples (512 MiB of
    /// <see cref="PnmImage.Samples"/>; a 1200 dpi A4 greyscale scan is about 139 million). The ceiling is on
    /// the <i>declared</i> geometry and is checked before any raster is allocated; callers who need
    /// more can pass their own.
    /// </summary>
    public const long DefaultMaximumSamples = 1L << 28;

    /// <summary>
    /// Reads and validates the header only. Throws <see cref="InvalidDataException"/> for malformed
    /// or over-ceiling headers and <see cref="NotSupportedException"/> for a PAM tuple type this
    /// reader does not handle.
    /// </summary>
    /// <param name="data">The file, or at least its header.</param>
    /// <param name="maximumSamples">Ceiling on <c>Width * Height * Channels</c>.</param>
    public static PnmHeader ReadHeader(ReadOnlySpan<byte> data, long maximumSamples = DefaultMaximumSamples)
    {
        if (data.Length < 3 || data[0] != (byte)'P' || data[1] is < (byte)'1' or > (byte)'7' || !IsSpace(data[2]))
            throw new InvalidDataException("Not a Netpbm file: expected P1..P7 followed by whitespace.");

        var version = data[1] - (byte)'0';
        PnmHeader header;
        if (version == 7)
        {
            header = ReadPamHeader(data);
        }
        else
        {
            var pos = 2;
            var width = ReadNumber(data, ref pos, "width");
            var height = ReadNumber(data, ref pos, "height");
            var maxValue = 1;
            if (version is not (1 or 4))
            {
                maxValue = ReadNumber(data, ref pos, "maxval");
                if (maxValue is < 1 or > 65535)
                    throw new InvalidDataException($"Netpbm maxval {maxValue} is outside 1..65535.");
            }

            EndOfHeader(data, ref pos);
            var tuple = version switch
            {
                1 or 4 => PnmTupleType.BlackAndWhite,
                2 or 5 => PnmTupleType.Grayscale,
                _ => PnmTupleType.Rgb,
            };
            header = new PnmHeader(version, width, height, maxValue, tuple, pos);
        }

        if (header.Width <= 0 || header.Height <= 0)
            throw new InvalidDataException("Netpbm width and height must be positive.");

        var limit = Math.Min(maximumSamples, Array.MaxLength);
        if ((long)header.Width * header.Height * header.Channels > limit)
            throw new InvalidDataException(
                $"Netpbm image of {header.Width}x{header.Height}x{header.Channels} samples exceeds the ceiling of {limit}.");

        return header;
    }

    /// <summary>Reads a whole Netpbm file at its native sample values.</summary>
    /// <param name="data">The file.</param>
    /// <param name="maximumSamples">Ceiling on <c>Width * Height * Channels</c>, checked before allocating.</param>
    /// <exception cref="InvalidDataException">Malformed header or raster, a sample above <c>maxval</c>, a truncated raster, or an over-ceiling size.</exception>
    /// <exception cref="NotSupportedException">A PAM tuple type this reader does not handle.</exception>
    public static PnmImage Read(ReadOnlySpan<byte> data, long maximumSamples = DefaultMaximumSamples)
    {
        var header = ReadHeader(data, maximumSamples);
        var channels = header.Channels;
        var count = checked(header.Width * header.Height * channels);
        var raster = data[header.DataOffset..];
        var maxValue = header.MaxValue;

        // A raster the input cannot possibly hold is refused before it is allocated. Plain forms
        // take at least one byte per sample (P1 may pack them with no separators, but no fewer).
        long needed = header.Version switch
        {
            4 => (long)((header.Width + 7) / 8) * header.Height,
            5 or 6 or 7 => (long)count * (maxValue > 255 ? 2 : 1),
            _ => count,
        };
        if (raster.Length < needed)
            throw new InvalidDataException($"Netpbm raster is truncated: {raster.Length} bytes, at least {needed} needed.");

        var samples = new ushort[count];
        switch (header.Version)
        {
            case 1:
                ReadPlainBitmap(raster, header, samples);
                break;
            case 2 or 3:
                ReadPlainSamples(raster, maxValue, samples);
                break;
            case 4:
                ReadRawBitmap(raster, header, samples);
                break;
            default:
                ReadRawSamples(raster, maxValue, samples);
                break;
        }

        return new PnmImage(header, samples);
    }

    private static void ReadPlainBitmap(ReadOnlySpan<byte> raster, PnmHeader header, ushort[] samples)
    {
        var pos = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            SkipSeparators(raster, ref pos);
            if (pos >= raster.Length)
                throw new InvalidDataException("Netpbm plain bitmap is truncated.");
            var c = raster[pos++];
            if (c is not ((byte)'0' or (byte)'1'))
                throw new InvalidDataException($"Netpbm plain bitmap holds 0x{c:X2}; only '0' and '1' are allowed.");
            samples[i] = c == (byte)'1' ? (ushort)0 : (ushort)1; // 1 = black in the file
        }
    }

    private static void ReadPlainSamples(ReadOnlySpan<byte> raster, int maxValue, ushort[] samples)
    {
        var pos = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = ReadNumber(raster, ref pos, "sample");
            if (value > maxValue)
                throw new InvalidDataException($"Netpbm sample {value} exceeds maxval {maxValue}.");
            samples[i] = (ushort)value;
        }
    }

    private static void ReadRawBitmap(ReadOnlySpan<byte> raster, PnmHeader header, ushort[] samples)
    {
        var stride = (header.Width + 7) / 8;
        var o = 0;
        for (var y = 0; y < header.Height; y++)
        {
            var row = raster.Slice(y * stride, stride);
            for (var x = 0; x < header.Width; x++)
                samples[o++] = (row[x >> 3] >> (7 - (x & 7)) & 1) != 0 ? (ushort)0 : (ushort)1;
        }
    }

    private static void ReadRawSamples(ReadOnlySpan<byte> raster, int maxValue, ushort[] samples)
    {
        if (maxValue <= 255)
        {
            if (maxValue == 255)
            {
                for (var i = 0; i < samples.Length; i++) samples[i] = raster[i];
                return;
            }

            for (var i = 0; i < samples.Length; i++)
            {
                int v = raster[i];
                if (v > maxValue) throw SampleAbove(v, maxValue);
                samples[i] = (ushort)v;
            }

            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var v = raster[2 * i] << 8 | raster[2 * i + 1];
            if (v > maxValue) throw SampleAbove(v, maxValue);
            samples[i] = (ushort)v;
        }
    }

    private static InvalidDataException SampleAbove(int value, int maxValue) =>
        new($"Netpbm sample {value} exceeds maxval {maxValue}.");

    // ---- header lexing -------------------------------------------------------------------

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

    /// <summary>Skips whitespace and <c>#</c> comments (a comment runs to the end of its line).</summary>
    private static void SkipSeparators(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            var b = data[pos];
            if (IsSpace(b))
            {
                pos++;
            }
            else if (b == (byte)'#')
            {
                while (pos < data.Length && data[pos] is not ((byte)'\n' or (byte)'\r')) pos++;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads one unsigned decimal token. It must be followed by the end of the data, whitespace or
    /// a comment, so <c>12x</c> is an error rather than 12.
    /// </summary>
    private static int ReadNumber(ReadOnlySpan<byte> data, ref int pos, string what)
    {
        SkipSeparators(data, ref pos);
        if (pos >= data.Length)
            throw new InvalidDataException($"Netpbm data ended where the {what} was expected.");

        long value = 0;
        var start = pos;
        while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (data[pos] - (byte)'0');
            if (value > int.MaxValue)
                throw new InvalidDataException($"Netpbm {what} is out of range.");
            pos++;
        }

        if (pos == start || (pos < data.Length && !IsSpace(data[pos]) && data[pos] != (byte)'#'))
            throw new InvalidDataException($"Netpbm {what} is not a number.");

        return (int)value;
    }

    /// <summary>
    /// Consumes the single whitespace byte that ends the header. A comment right after the last
    /// token is consumed up to and including its newline, which is then that byte.
    /// </summary>
    private static void EndOfHeader(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length) return; // the raster is missing; the length check reports it
        if (data[pos] == (byte)'#')
        {
            while (pos < data.Length && data[pos] is not ((byte)'\n' or (byte)'\r')) pos++;
            // A comment's line ending is CR LF on some writers; both bytes belong to the comment.
            if (pos + 1 < data.Length && data[pos] == (byte)'\r' && data[pos + 1] == (byte)'\n') pos++;
        }

        if (pos < data.Length) pos++;
    }

    // ---- PAM -----------------------------------------------------------------------------

    private static PnmHeader ReadPamHeader(ReadOnlySpan<byte> data)
    {
        // The rest of the "P7" line must be blank.
        var pos = 2;
        while (pos < data.Length && data[pos] != (byte)'\n')
        {
            if (!IsSpace(data[pos])) throw new InvalidDataException("Netpbm P7 magic line holds more than P7.");
            pos++;
        }

        pos++;

        int width = -1, height = -1, depth = -1, maxValue = -1;
        string? tuple = null;
        var ended = false;
        while (pos < data.Length && !ended)
        {
            var end = data[pos..].IndexOf((byte)'\n');
            var line = end < 0 ? data[pos..] : data.Slice(pos, end);
            pos = end < 0 ? data.Length : pos + end + 1;

            line = TrimEnd(line);
            if (line.IsEmpty || line[0] == (byte)'#') continue;

            var split = 0;
            while (split < line.Length && !IsSpace(line[split])) split++;
            var keyword = line[..split];
            var value = TrimStart(line[split..]);

            if (keyword.SequenceEqual("ENDHDR"u8)) ended = true;
            else if (keyword.SequenceEqual("WIDTH"u8)) width = PamNumber(value, "WIDTH", width);
            else if (keyword.SequenceEqual("HEIGHT"u8)) height = PamNumber(value, "HEIGHT", height);
            else if (keyword.SequenceEqual("DEPTH"u8)) depth = PamNumber(value, "DEPTH", depth);
            else if (keyword.SequenceEqual("MAXVAL"u8)) maxValue = PamNumber(value, "MAXVAL", maxValue);
            else if (keyword.SequenceEqual("TUPLTYPE"u8))
            {
                // Several TUPLTYPE lines concatenate, separated by a space.
                var text = Encoding.ASCII.GetString(value);
                tuple = tuple is null ? text : tuple + " " + text;
            }
            else throw new InvalidDataException($"Netpbm PAM header has an unknown line '{Encoding.ASCII.GetString(keyword)}'.");
        }

        if (!ended) throw new InvalidDataException("Netpbm PAM header has no ENDHDR.");
        if (width < 0 || height < 0 || depth < 0 || maxValue < 0)
            throw new InvalidDataException("Netpbm PAM header needs WIDTH, HEIGHT, DEPTH and MAXVAL.");
        if (maxValue is < 1 or > 65535)
            throw new InvalidDataException($"Netpbm maxval {maxValue} is outside 1..65535.");
        if (depth < 1)
            throw new InvalidDataException("Netpbm PAM DEPTH must be at least 1.");

        PnmTupleType type;
        switch (tuple)
        {
            case null or "":
                // No TUPLTYPE: the depth is all there is to go on.
                type = depth switch
                {
                    1 => PnmTupleType.Grayscale,
                    2 => PnmTupleType.GrayscaleAlpha,
                    3 => PnmTupleType.Rgb,
                    4 => PnmTupleType.RgbAlpha,
                    _ => throw new NotSupportedException($"Netpbm PAM with DEPTH {depth} and no TUPLTYPE is not supported."),
                };
                break;
            case "BLACKANDWHITE": type = PnmTupleType.BlackAndWhite; break;
            case "BLACKANDWHITE_ALPHA": type = PnmTupleType.BlackAndWhiteAlpha; break;
            case "GRAYSCALE": type = PnmTupleType.Grayscale; break;
            case "GRAYSCALE_ALPHA": type = PnmTupleType.GrayscaleAlpha; break;
            case "RGB": type = PnmTupleType.Rgb; break;
            case "RGB_ALPHA": type = PnmTupleType.RgbAlpha; break;
            default:
                throw new NotSupportedException($"Netpbm PAM TUPLTYPE '{tuple}' is not supported.");
        }

        var header = new PnmHeader(7, width, height, maxValue, type, pos);
        if (header.Channels != depth)
            throw new InvalidDataException($"Netpbm PAM TUPLTYPE '{tuple}' needs DEPTH {header.Channels}, not {depth}.");
        if (type is PnmTupleType.BlackAndWhite or PnmTupleType.BlackAndWhiteAlpha && maxValue != 1)
            throw new InvalidDataException("Netpbm PAM BLACKANDWHITE needs MAXVAL 1.");

        return header;
    }

    private static int PamNumber(ReadOnlySpan<byte> value, string name, int previous)
    {
        if (previous >= 0) throw new InvalidDataException($"Netpbm PAM header repeats {name}.");
        if (value.IsEmpty) throw new InvalidDataException($"Netpbm PAM {name} has no value.");
        long number = 0;
        foreach (var b in value)
        {
            if (b is < (byte)'0' or > (byte)'9') throw new InvalidDataException($"Netpbm PAM {name} is not a number.");
            number = number * 10 + (b - (byte)'0');
            if (number > int.MaxValue) throw new InvalidDataException($"Netpbm PAM {name} is out of range.");
        }

        return (int)number;
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> s)
    {
        var n = s.Length;
        while (n > 0 && IsSpace(s[n - 1])) n--;
        return s[..n];
    }

    private static ReadOnlySpan<byte> TrimStart(ReadOnlySpan<byte> s)
    {
        var i = 0;
        while (i < s.Length && IsSpace(s[i])) i++;
        return s[i..];
    }
}
