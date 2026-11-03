using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Test support for the Netpbm reader: Magick.NET as the writer (every variant it can emit) and as
/// the reader (the oracle the decoded pixels are compared with, exactly), plus builders for the files
/// Magick will not write - odd maxvals in plain form, comments in odd places.
/// </summary>
internal static class PnmFixtures
{
    /// <summary>The plain-text magic ImageMagick writes for a format and compression.</summary>
    public static string MagickMagic(MagickFormat format, bool plain) => (format, plain) switch
    {
        (MagickFormat.Pbm, false) => "P4",
        (MagickFormat.Pbm, true) => "P1",
        (MagickFormat.Pgm, false) => "P5",
        (MagickFormat.Pgm, true) => "P2",
        (MagickFormat.Ppm, false) => "P6",
        (MagickFormat.Ppm, true) => "P3",
        (MagickFormat.Pam, _) => "P7",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Has Magick.NET write a seeded random image of <paramref name="width"/> x <paramref name="height"/>
    /// in <paramref name="format"/>, quantised to <paramref name="depth"/> bits (which sets the raw
    /// maxval to 2^depth - 1). <paramref name="mapping"/> is Magick's pixel map: I, IA, RGB or RGBA.
    /// </summary>
    public static byte[] MagickWrite(MagickFormat format, string mapping, int width, int height, int depth, bool plain, int seed = 1)
    {
        var channels = mapping.Length;
        var raw = new byte[width * height * channels * 2];
        new Random(seed).NextBytes(raw);

        using var image = new MagickImage(raw, new PixelReadSettings((uint)width, (uint)height, StorageType.Short, mapping));
        image.Depth = (uint)depth;
        if (plain) image.Settings.Compression = CompressionMethod.NoCompression;

        using var stream = new MemoryStream();
        image.Write(stream, format);
        return stream.ToArray();
    }

    /// <summary>The first line of text of the raster-less part of a file, for "is this really that variant" checks.</summary>
    public static string HeaderText(byte[] file, int length = 64) =>
        Encoding.ASCII.GetString(file, 0, Math.Min(length, file.Length));

    /// <summary>
    /// Asserts that decoding <paramref name="file"/> through the facade gives exactly the pixels
    /// Magick.NET reads from it: same size, the sample format the file's maxval implies, and every
    /// RGBA sample equal (8-bit when maxval is at most 255, 16-bit above).
    /// </summary>
    public static void ShouldDecodeLikeMagick(byte[] file, int? expectedChannels = null)
    {
        ImageCodecs.TryDecode(file, out var ours).ShouldBeTrue("the facade should decode a Netpbm file");

        using var magick = new MagickImage(file);
        ours!.Width.ShouldBe((int)magick.Width);
        ours.Height.ShouldBe((int)magick.Height);
        if (expectedChannels is { } channels) ours.Channels.ShouldBe(channels);

        var pixels = magick.GetPixels();
        if (ours.SampleFormat == SampleFormat.UInt8)
        {
            AssertEqual(ours.ToRgba8(), pixels.ToByteArray(PixelMapping.RGBA)!);
        }
        else
        {
            ours.SampleFormat.ShouldBe(SampleFormat.UInt16);
            AssertEqual(ExpandRgba16(ours), pixels.ToShortArray(PixelMapping.RGBA)!);
        }
    }

    /// <summary>
    /// True where ImageMagick's raw/PAM reader does not follow the Netpbm documents: for a binary
    /// raster whose maxval needs exactly 8 or 16 bits (128..254, 32768..65534) it takes the samples as
    /// full-scale 8/16-bit values instead of dividing by maxval, so a sample equal to maxval is not
    /// white. (Its plain P2/P3 reader, and every other raw maxval, scale correctly.) The docs are the
    /// judge here, so those cases are checked against <see cref="ShouldDecodeLikeTheSpec"/> instead.
    /// </summary>
    public static bool MagickReadsRawMaxvalUnscaled(int maxValue) =>
        maxValue is >= 128 and <= 254 or >= 32768 and <= 65534;

    /// <summary>
    /// Asserts the facade's pixels equal the Netpbm documents' meaning of the given native samples,
    /// computed here independently of the decoder: sample / maxval, scaled to 0..255 (maxval at most
    /// 255) or 0..65535 and rounded half up. 16-bit may be one off the exact rational, because the
    /// decoder reproduces ImageMagick's single-precision evaluation (see the decoder's remarks).
    /// </summary>
    public static void ShouldDecodeLikeTheSpec(byte[] file, ushort[] samples, int channels, int maxValue)
    {
        ImageCodecs.TryDecode(file, out var ours).ShouldBeTrue();
        ours!.Channels.ShouldBe(channels);
        var wide = maxValue > 255;
        var full = wide ? 65535L : 255L;

        long Scale(ushort v) => (2 * full * v + maxValue) / (2L * maxValue); // round half up, exact
        var expected = new long[ours.Width * ours.Height * 4];
        for (var p = 0; p < ours.Width * ours.Height; p++)
        {
            var s = p * channels;
            var d = p * 4;
            expected[d] = Scale(samples[s]);
            expected[d + 1] = channels >= 3 ? Scale(samples[s + 1]) : expected[d];
            expected[d + 2] = channels >= 3 ? Scale(samples[s + 2]) : expected[d];
            expected[d + 3] = channels switch { 2 => Scale(samples[s + 1]), 4 => Scale(samples[s + 3]), _ => full };
        }

        var actual = wide
            ? ExpandRgba16(ours).Select(v => (long)v).ToArray()
            : ours.ToRgba8().Select(v => (long)v).ToArray();
        var tolerance = wide ? 1 : 0;
        for (var i = 0; i < actual.Length; i++)
        {
            if (Math.Abs(actual[i] - expected[i]) > tolerance)
                Assert.Fail($"First mismatch at RGBA sample {i}: ours {actual[i]}, the spec's {expected[i]}.");
        }
    }

    /// <summary>Expands a decoded 16-bit raster to RGBA16 the way <c>ToRgba8</c> does for 8-bit.</summary>
    public static ushort[] ExpandRgba16(IDecodedImage image)
    {
        var src = MemoryMarshal.Cast<byte, ushort>(image.Pixels);
        var count = image.Width * image.Height;
        var dst = new ushort[count * 4];
        var n = image.Channels;
        for (var p = 0; p < count; p++)
        {
            var s = p * n;
            var d = p * 4;
            switch (n)
            {
                case 1: dst[d] = dst[d + 1] = dst[d + 2] = src[s]; dst[d + 3] = 65535; break;
                case 2: dst[d] = dst[d + 1] = dst[d + 2] = src[s]; dst[d + 3] = src[s + 1]; break;
                case 3: dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = 65535; break;
                default: src.Slice(s, 4).CopyTo(dst.AsSpan(d, 4)); break;
            }
        }

        return dst;
    }

    private static void AssertEqual<T>(T[] actual, T[] expected) where T : IEquatable<T>
    {
        actual.Length.ShouldBe(expected.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            if (!actual[i].Equals(expected[i]))
                Assert.Fail($"First mismatch at RGBA sample {i} (pixel {i / 4}, channel {i % 4}): ours {actual[i]}, Magick {expected[i]}.");
        }
    }

    // ---- hand-built files -----------------------------------------------------------------

    /// <summary>Deterministic samples in 0..maxval, always including both 0 and maxval.</summary>
    public static ushort[] Samples(int count, int maxValue, int seed = 7)
    {
        var rnd = new Random(seed);
        var s = new ushort[count];
        for (var i = 0; i < count; i++) s[i] = (ushort)rnd.Next(maxValue + 1);
        if (count > 0) s[0] = 0;
        if (count > 1) s[1] = (ushort)maxValue;
        return s;
    }

    /// <summary>A plain P2/P3 file (samples as decimal text, <paramref name="perLine"/> to a line).</summary>
    public static byte[] Plain(int magic, int width, int height, int maxValue, ushort[] samples, int perLine = 12)
    {
        var sb = new StringBuilder();
        sb.Append($"P{magic}\n{width} {height}\n{maxValue}\n");
        for (var i = 0; i < samples.Length; i++)
        {
            sb.Append(samples[i]);
            sb.Append((i + 1) % perLine == 0 ? '\n' : ' ');
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A raw P5/P6 file; two bytes big-endian per sample above maxval 255.</summary>
    public static byte[] Raw(int magic, int width, int height, int maxValue, ushort[] samples) =>
        Concat(Encoding.ASCII.GetBytes($"P{magic}\n{width} {height}\n{maxValue}\n"), Pack(samples, maxValue));

    /// <summary>A raw PAM file with the given TUPLTYPE line (null for none).</summary>
    public static byte[] Pam(int width, int height, int depth, int maxValue, string? tupleType, ushort[] samples) =>
        Concat(
            Encoding.ASCII.GetBytes(
                $"P7\nWIDTH {width}\nHEIGHT {height}\nDEPTH {depth}\nMAXVAL {maxValue}\n" +
                (tupleType is null ? "" : $"TUPLTYPE {tupleType}\n") + "ENDHDR\n"),
            Pack(samples, maxValue));

    public static byte[] Pack(ushort[] samples, int maxValue)
    {
        if (maxValue <= 255) return samples.Select(s => (byte)s).ToArray();
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[2 * i] = (byte)(samples[i] >> 8);
            bytes[2 * i + 1] = (byte)samples[i];
        }

        return bytes;
    }

    public static byte[] Concat(byte[] a, byte[] b) => [.. a, .. b];

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
