using System.Globalization;
using System.Text;

namespace SharpAstro.Radiance;

/// <summary>
/// Reads and writes Radiance RGBE pictures (<c>.hdr</c> / <c>.pic</c>) as scene-linear float.
///
/// <para><b>File layout.</b> A magic line (<c>#?RADIANCE</c> or <c>#?RGBE</c>), header lines up to a blank
/// line (<c>FORMAT=</c>, <c>EXPOSURE=</c>, comments, ...), the resolution line (<c>-Y h +X w</c>), then
/// <c>h</c> scanlines of 4-byte pixels: three 8-bit mantissas and one shared exponent.</para>
///
/// <para><b>Scanline codings</b> (all three are read; the writer emits the first): new-style RLE
/// (<c>2,2,hi,lo</c> then each of the four channels run-length coded in turn; widths 8..32767), old RLE
/// (a pixel <c>1,1,1,n</c> repeats the previous pixel <c>n &lt;&lt; shift</c> times, the shift growing by 8 for each
/// consecutive repeat pixel) and flat (4 bytes per pixel).</para>
///
/// <para><b>Value convention.</b> A pixel is <c>mantissa * 2^(exponent - 136)</c> with no half-step offset, and
/// exponent byte 0 means zero. This is stb_image's reading and ImageMagick's (measured against Magick.NET in
/// <c>RadianceOracleTests</c>), not the <c>(mantissa + 0.5)</c> of Radiance's own <c>colr_color</c>. The
/// product with a power of two is exact in <c>float</c>, so decode is lossless with respect to the RGBE bytes.</para>
///
/// <para>Every refusal is an <see cref="InvalidDataException"/> whose message names the offending feature.</para>
/// </summary>
public static class RadianceFile
{
    /// <summary>Default ceiling on <c>width * height</c> (2^28; 3 floats each is about 3 GiB), checked from the header
    /// before anything is allocated. Pass a lower <c>maximumPixels</c> to <see cref="Read"/> for a tighter bound: a
    /// run-length coded scanline can describe far more pixels than it has bytes, so input size is no bound.</summary>
    public const long DefaultMaximumPixels = 1L << 28;

    private const int MinRleWidth = 8;
    private const int MaxRleWidth = 32767;
    private const int MaxHeaderBytes = 1 << 20;

    private static ReadOnlySpan<byte> Magic1 => "#?RADIANCE"u8;
    private static ReadOnlySpan<byte> Magic2 => "#?RGBE"u8;

    /// <summary>True when <paramref name="data"/> starts with a Radiance magic line (<c>#?RADIANCE</c> or <c>#?RGBE</c>).</summary>
    public static bool HasMagic(ReadOnlySpan<byte> data)
    {
        static bool Starts(ReadOnlySpan<byte> d, ReadOnlySpan<byte> m) =>
            d.Length > m.Length && d[..m.Length].SequenceEqual(m) && (d[m.Length] == (byte)'\n' || d[m.Length] == (byte)'\r');
        return Starts(data, Magic1) || Starts(data, Magic2);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Decode a Radiance picture.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="maximumPixels">Refuse a picture declaring more pixels than this (default <see cref="DefaultMaximumPixels"/>).</param>
    /// <exception cref="InvalidDataException">Malformed or truncated data, a run or literal that overflows its scanline,
    /// an unsupported FORMAT or orientation, or a size over <paramref name="maximumPixels"/>.</exception>
    public static RadianceImage Read(ReadOnlySpan<byte> data, long maximumPixels = DefaultMaximumPixels)
    {
        int pos = 0;
        string magic = ReadLine(data, ref pos) ?? throw new InvalidDataException("Radiance: empty file.");
        if (magic != "#?RADIANCE" && magic != "#?RGBE")
            throw new InvalidDataException("Radiance: missing '#?RADIANCE' / '#?RGBE' magic line.");

        var headerLines = new List<string>();
        var format = RadianceFormat.Rgbe;
        double? exposure = null;
        while (true)
        {
            string line = ReadLine(data, ref pos) ?? throw new InvalidDataException("Radiance: header ends before its blank line.");
            if (line.Length == 0) break;
            headerLines.Add(line);
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal))
            {
                string v = line["FORMAT=".Length..];
                format = v switch
                {
                    "32-bit_rle_rgbe" => RadianceFormat.Rgbe,
                    "32-bit_rle_xyze" => RadianceFormat.Xyze,
                    _ => throw new InvalidDataException($"Radiance: unsupported FORMAT '{v}' (only 32-bit_rle_rgbe and 32-bit_rle_xyze)."),
                };
            }
            else if (line.StartsWith("EXPOSURE=", StringComparison.Ordinal))
            {
                if (!double.TryParse(line["EXPOSURE=".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double e)
                    || !double.IsFinite(e) || e <= 0)
                    throw new InvalidDataException($"Radiance: bad EXPOSURE line '{line}'.");
                exposure = (exposure ?? 1.0) * e;
            }
        }

        string res = ReadLine(data, ref pos) ?? throw new InvalidDataException("Radiance: missing resolution line.");
        var (width, height) = ParseResolution(res);
        if ((long)width * height > maximumPixels)
            throw new InvalidDataException($"Radiance: {width}x{height} exceeds the {maximumPixels}-pixel limit.");

        var pixels = new float[checked(width * height * 3)];
        var scan = new byte[width * 4];
        var body = data[pos..];
        int bp = 0;
        for (int y = 0; y < height; y++)
        {
            ReadScanline(body, ref bp, scan, width);
            int o = y * width * 3;
            for (int x = 0; x < width; x++, o += 3)
            {
                int e = scan[x * 4 + 3];
                if (e == 0) continue; // exponent byte 0 means black; the array is already 0f
                float f = MathF.ScaleB(1f, e - 136);
                pixels[o] = scan[x * 4] * f;
                pixels[o + 1] = scan[x * 4 + 1] * f;
                pixels[o + 2] = scan[x * 4 + 2] * f;
            }
        }

        return new RadianceImage
        {
            Width = width,
            Height = height,
            Pixels = pixels,
            Format = format,
            Exposure = exposure,
            HeaderLines = headerLines,
        };
    }

    /// <summary>One header line without its terminator (a trailing CR is dropped), or null at end of data.</summary>
    private static string? ReadLine(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length) return null;
        int nl = data[pos..].IndexOf((byte)'\n');
        if (nl < 0) throw new InvalidDataException("Radiance: header line has no terminator.");
        if (nl > MaxHeaderBytes || pos > MaxHeaderBytes) throw new InvalidDataException("Radiance: header too large.");
        var line = data.Slice(pos, nl);
        pos += nl + 1;
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        return Encoding.Latin1.GetString(line);
    }

    private static (int w, int h) ParseResolution(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool shape = parts.Length == 4
            && parts[0].Length == 2 && (parts[0][0] is '+' or '-') && (parts[0][1] is 'X' or 'Y')
            && parts[2].Length == 2 && (parts[2][0] is '+' or '-') && (parts[2][1] is 'X' or 'Y')
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        if (!shape) throw new InvalidDataException($"Radiance: malformed resolution line '{line}'.");
        if (parts[0] != "-Y" || parts[2] != "+X")
            throw new InvalidDataException(
                $"Radiance: unsupported orientation '{parts[0]} {parts[2]}' (only '-Y h +X w', top-down left-to-right).");
        int h = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int w = int.Parse(parts[3], CultureInfo.InvariantCulture);
        if (w <= 0 || h <= 0) throw new InvalidDataException($"Radiance: empty image '{line}'.");
        return (w, h);
    }

    /// <summary>Decode one scanline into <paramref name="scan"/> (<c>width * 4</c> bytes, pixel-interleaved RGBE).</summary>
    private static void ReadScanline(ReadOnlySpan<byte> d, ref int p, byte[] scan, int width)
    {
        int first = 0; // pixels already read into scan by the sniff below
        if (width >= MinRleWidth && width <= MaxRleWidth)
        {
            Need(d, p, 4);
            // Radiance's rule (freadcolrs): 2,2 and a clear top bit in the third byte marks a new-style line.
            if (d[p] == 2 && d[p + 1] == 2 && (d[p + 2] & 0x80) == 0)
            {
                int embedded = (d[p + 2] << 8) | d[p + 3];
                if (embedded != width)
                    throw new InvalidDataException($"Radiance: new-style scanline declares width {embedded}, header says {width}.");
                p += 4;
                ReadNewRle(d, ref p, scan, width);
                return;
            }
            d.Slice(p, 4).CopyTo(scan);
            p += 4;
            first = 1;
        }
        ReadOldScanline(d, ref p, scan, width, first);
    }

    private static void ReadNewRle(ReadOnlySpan<byte> d, ref int p, byte[] scan, int width)
    {
        for (int c = 0; c < 4; c++)
        {
            int x = 0;
            while (x < width)
            {
                Need(d, p, 1);
                int code = d[p++];
                if (code > 128)
                {
                    int count = code - 128;
                    if (count > width - x) throw new InvalidDataException("Radiance: RLE run overflows the scanline width.");
                    Need(d, p, 1);
                    byte v = d[p++];
                    for (int i = 0; i < count; i++) scan[(x++) * 4 + c] = v;
                }
                else
                {
                    int count = code;
                    if (count == 0) throw new InvalidDataException("Radiance: RLE literal of length 0.");
                    if (count > width - x) throw new InvalidDataException("Radiance: RLE literal overflows the scanline width.");
                    Need(d, p, count);
                    for (int i = 0; i < count; i++) scan[(x++) * 4 + c] = d[p++];
                }
            }
        }
    }

    /// <summary>Flat pixels, with old-style <c>1,1,1,n</c> repeats (Radiance's oldreadcolrs).</summary>
    private static void ReadOldScanline(ReadOnlySpan<byte> d, ref int p, byte[] scan, int width, int i)
    {
        int shift = 0;
        while (i < width)
        {
            Need(d, p, 4);
            byte r = d[p], g = d[p + 1], b = d[p + 2], e = d[p + 3];
            p += 4;
            if (r == 1 && g == 1 && b == 1)
            {
                if (i == 0) throw new InvalidDataException("Radiance: old-style repeat with no previous pixel.");
                // The shift grows by 8 per consecutive repeat pixel; past 32 any non-zero count is far over any width.
                long count = (long)e << Math.Min(shift, 32);
                if (count > width - i) throw new InvalidDataException("Radiance: old-style run overflows the scanline width.");
                for (long k = 0; k < count; k++)
                {
                    Buffer.BlockCopy(scan, (i - 1) * 4, scan, i * 4, 4);
                    i++;
                }
                shift += 8;
            }
            else
            {
                scan[i * 4] = r; scan[i * 4 + 1] = g; scan[i * 4 + 2] = b; scan[i * 4 + 3] = e;
                i++;
                shift = 0;
            }
        }
    }

    private static void Need(ReadOnlySpan<byte> d, int p, int n)
    {
        if (n > d.Length - p) throw new InvalidDataException("Radiance: truncated pixel data.");
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Encode scene-linear float RGB as a Radiance RGBE file: <c>#?RADIANCE</c>, <c>FORMAT=32-bit_rle_rgbe</c>,
    /// <c>-Y h +X w</c>, new-style RLE scanlines (flat for widths under 8 or over 32767, which new-style cannot carry).
    /// </summary>
    /// <param name="rgb">Interleaved R,G,B, <c>width * height * 3</c> floats, top scanline first.</param>
    /// <remarks>
    /// RGBE keeps 8 mantissa bits and one exponent for all three channels, so the largest channel keeps its top 8 bits
    /// (truncated, not rounded: stb_image_write and Radiance's own writer truncate too) and smaller ones lose more.
    /// Negative and NaN samples become 0; +Infinity saturates; a pixel whose largest channel is below 1e-32 becomes 0.
    /// </remarks>
    public static byte[] Write(ReadOnlySpan<float> rgb, int width, int height) =>
        Write(rgb, width, height, RadianceFormat.Rgbe, null);

    /// <summary>Encode a <see cref="RadianceImage"/>, keeping its <see cref="RadianceImage.Format"/> and <see cref="RadianceImage.Exposure"/>.</summary>
    public static byte[] Write(RadianceImage image) =>
        Write(image.Pixels, image.Width, image.Height, image.Format, image.Exposure);

    private static byte[] Write(ReadOnlySpan<float> rgb, int width, int height, RadianceFormat format, double? exposure)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Radiance: empty image.");
        if ((long)width * height * 3 != rgb.Length)
            throw new ArgumentException($"Radiance: expected {(long)width * height * 3} samples for {width}x{height}, got {rgb.Length}.", nameof(rgb));

        var text = new StringBuilder("#?RADIANCE\n");
        text.Append("FORMAT=").Append(format == RadianceFormat.Rgbe ? "32-bit_rle_rgbe" : "32-bit_rle_xyze").Append('\n');
        if (exposure is { } ex) text.Append("EXPOSURE=").Append(ex.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        text.Append('\n');
        text.Append("-Y ").Append(height.ToString(CultureInfo.InvariantCulture))
            .Append(" +X ").Append(width.ToString(CultureInfo.InvariantCulture)).Append('\n');

        var output = new MemoryStream(text.Length + width * height * 4 / 2);
        output.Write(Encoding.ASCII.GetBytes(text.ToString()));

        bool rle = width >= MinRleWidth && width <= MaxRleWidth;
        var scan = new byte[width * 4];
        var channel = new byte[width];
        var line = new List<byte>(width * 2);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 3;
                FloatToRgbe(rgb[o], rgb[o + 1], rgb[o + 2], scan.AsSpan(x * 4, 4));
            }
            if (!rle)
            {
                output.Write(scan);
                continue;
            }
            output.WriteByte(2); output.WriteByte(2);
            output.WriteByte((byte)(width >> 8)); output.WriteByte((byte)width);
            for (int c = 0; c < 4; c++)
            {
                for (int x = 0; x < width; x++) channel[x] = scan[x * 4 + c];
                line.Clear();
                EncodeChannel(channel, line);
                output.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line));
            }
        }
        return output.ToArray();
    }

    /// <summary>New-style RLE of one channel: runs of 4..127 equal bytes as <c>128+n, v</c>, everything else as literals of up to 128.</summary>
    private static void EncodeChannel(byte[] v, List<byte> o)
    {
        const int MinRun = 4;
        int n = v.Length, i = 0;
        while (i < n)
        {
            int run = 1;
            while (i + run < n && run < 127 && v[i + run] == v[i]) run++;
            if (run >= MinRun)
            {
                o.Add((byte)(128 + run)); o.Add(v[i]);
                i += run;
                continue;
            }
            // Literal: extend until a run of MinRun+ starts or 128 bytes are held.
            int start = i, end = i;
            while (end < n && end - start < 128)
            {
                int r = 1;
                while (end + r < n && r < MinRun && v[end + r] == v[end]) r++;
                if (r >= MinRun) break;
                end++;
            }
            o.Add((byte)(end - start));
            for (int k = start; k < end; k++) o.Add(v[k]);
            i = end;
        }
    }

    internal static void FloatToRgbe(float r, float g, float b, Span<byte> dst)
    {
        // Negative and NaN clamp to 0 (the comparison is false for NaN).
        double dr = r > 0 ? r : 0, dg = g > 0 ? g : 0, db = b > 0 ? b : 0;
        double max = Math.Max(dr, Math.Max(dg, db));
        if (!(max >= 1e-32))
        {
            dst[0] = dst[1] = dst[2] = dst[3] = 0;
            return;
        }
        // max = f * 2^e with f in [0.5, 1): scaling by 2^(8-e) is exact, so the largest channel's mantissa is in 128..255.
        int e = double.IsInfinity(max) ? 128 : Math.ILogB(max) + 1;
        if (e > 127) e = 127; // beyond what the exponent byte holds: Mantissa() then saturates at 255 * 2^(127-136)
        dst[0] = Mantissa(dr, e); dst[1] = Mantissa(dg, e); dst[2] = Mantissa(db, e);
        dst[3] = (byte)(e + 128);
    }

    private static byte Mantissa(double v, int e) => (byte)Math.Min(255.0, Math.Floor(Math.ScaleB(v, 8 - e)));
}
