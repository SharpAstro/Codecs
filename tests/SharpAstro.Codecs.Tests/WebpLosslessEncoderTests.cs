using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

// The WebP lossless (VP8L) encoder's tests. The encoder is spec-derived from RFC 9649; cwebp and
// dwebp are used only as black-box oracles. Every class name starts with WebpLosslessEncoder so the
// leg's check filter finds them all.

/// <summary>Builds the deterministic corpus every encoder test shares.</summary>
internal static class WebpLosslessEncoderCorpus
{
    public sealed record Image(string Name, int Width, int Height, byte[] Rgba);

    public static IEnumerable<object[]> Names() =>
        Build().Select(static image => new object[] { image.Name });

    /// <summary>Every corpus image at every effort 0 to 9.</summary>
    public static IEnumerable<object[]> NamesByEffort() =>
        Build().SelectMany(static image => Enumerable.Range(0, 10).Select(effort => new object[] { image.Name, effort }));

    public static Image Get(string name) => Build().Single(image => image.Name == name);

    public static int[] ToArgb(byte[] rgba)
    {
        int[] argb = new int[rgba.Length / 4];
        for (int i = 0; i < argb.Length; i++)
        {
            argb[i] = (rgba[i * 4 + 3] << 24) | (rgba[i * 4] << 16) | (rgba[i * 4 + 1] << 8) | rgba[i * 4 + 2];
        }

        return argb;
    }

    public static IEnumerable<Image> Build()
    {
        yield return new("photo-noise", 128, 96, PhotoLike(128, 96));
        yield return new("flat-ui", 200, 120, FlatUi(200, 120));
        yield return new("line-art", 160, 100, LineArt(160, 100));
        yield return new("palette-16", 64, 64, Palette(64, 64, 16));
        yield return new("palette-2", 50, 40, Palette(50, 40, 2));
        yield return new("palette-3", 50, 40, Palette(50, 40, 3));
        yield return new("alpha-gradient", 100, 40, AlphaGradient(100, 40));
        yield return new("odd-3x7", 3, 7, PhotoLike(3, 7));
        yield return new("odd-257x129", 257, 129, PhotoLike(257, 129));
        yield return new("one-pixel", 1, 1, [12, 34, 56, 255]);
        yield return new("one-pixel-translucent", 1, 1, [12, 34, 56, 100]);
        yield return new("transparent-black", 32, 32, new byte[32 * 32 * 4]);
        yield return new("transparent-colour", 31, 17, TransparentWithColour(31, 17));
        yield return new("uniform-bytes", 256, 64, UniformBytes(256, 64));
        yield return new("wide-strip", 4096, 1, PhotoLike(4096, 1));
        yield return new("tall-strip", 1, 4096, PhotoLike(1, 4096));
        yield return new("tiled-pattern", 192, 128, Tiled(192, 128, 24, 20));
        yield return new("long-repeat", 200, 100, LongRepeat(200, 100));
        yield return new("far-repeat", 512, 300, FarRepeat(512, 300));
    }

    // A 24x20 tile of eight noisy colours repeated across the image: the period is unrelated to
    // the width, so the repeats are at distances (24, one row up, ...) a plane code cannot all name.
    private static byte[] Tiled(int w, int h, int tileW, int tileH)
    {
        uint s = 0xabcdef1u;
        byte[][] tile = new byte[tileW * tileH][];
        for (int i = 0; i < tile.Length; i++)
        {
            byte level = (byte)(Next(ref s) % 8 * 32);
            tile[i] = [level, (byte)(255 - level), (byte)(level / 2 + 7), 255];
        }

        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                tile[(y % tileH) * tileW + (x % tileW)].CopyTo(px, (y * w + x) * 4);
            }
        }

        return px;
    }

    // 20000 pixels from a period-5 colour sequence: runs far longer than the 4096 a copy can carry.
    private static byte[] LongRepeat(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            int k = p % 5;
            px[p * 4] = (byte)(k * 50);
            px[p * 4 + 1] = (byte)(255 - k * 40);
            px[p * 4 + 2] = (byte)(k * k * 9);
            px[p * 4 + 3] = 255;
        }

        return px;
    }

    // Noise in rows 0-99, a gradient in 100-199, then rows 0-99 again at 200-299: the copy is
    // 200 rows back, a distance (102400 at width 512) whose prefix code has many extra bits.
    private static byte[] FarRepeat(int w, int h)
    {
        uint s = 0x5eed5u;
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < w * 4; x++)
            {
                px[y * w * 4 + x] = x % 4 == 3 ? (byte)255 : (byte)Next(ref s);
            }
        }

        for (int y = 100; y < 200; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x / 2);
                px[i + 1] = (byte)y;
                px[i + 2] = 40;
                px[i + 3] = 255;
            }
        }

        Array.Copy(px, 0, px, 200 * w * 4, 100 * w * 4);
        return px;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    // Smooth gradients plus per-channel noise: every channel uses many symbols with a skew.
    private static byte[] PhotoLike(int w, int h)
    {
        uint s = 0x9e3779b9u ^ (uint)(w * 31 + h);
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = Clamp(x * 255 / Math.Max(1, w - 1) + (int)(Next(ref s) % 24) - 12);
                px[i + 1] = Clamp(y * 255 / Math.Max(1, h - 1) + (int)(Next(ref s) % 24) - 12);
                px[i + 2] = Clamp(128 + (int)(Next(ref s) % 61) - 30);
                px[i + 3] = 255;
            }
        }

        return px;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    // A handful of flat colours in rectangles, like a screenshot of an interface.
    private static byte[] FlatUi(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) = (x, y) switch
                {
                    _ when y < 16 => ((byte)40, (byte)44, (byte)52),
                    _ when x < 40 => ((byte)30, (byte)33, (byte)39),
                    _ when (x - 60) is >= 0 and < 80 && (y - 40) is >= 0 and < 24 => ((byte)0, (byte)122, (byte)204),
                    _ when ((x / 8) + (y / 8)) % 11 == 0 => ((byte)200, (byte)200, (byte)200),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                int i = (y * w + x) * 4;
                px[i] = r;
                px[i + 1] = g;
                px[i + 2] = b;
                px[i + 3] = 255;
            }
        }

        return px;
    }

    // Black lines and a ring on white.
    private static byte[] LineArt(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x - w / 2.0;
                double dy = y - h / 2.0;
                double r = Math.Sqrt(dx * dx + dy * dy);
                bool ink = (x + y) % 23 == 0 || (x - y + h) % 37 == 0 || Math.Abs(r - 30) < 1.2;
                if (ink)
                {
                    int i = (y * w + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = 0;
                }
            }
        }

        return px;
    }

    private static byte[] Palette(int w, int h, int colours)
    {
        uint s = 0x1234567u + (uint)colours;
        byte[][] palette = Enumerable.Range(0, colours)
            .Select(_ => new[] { (byte)Next(ref s), (byte)Next(ref s), (byte)Next(ref s), (byte)255 })
            .ToArray();
        byte[] px = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            // Skewed: low indices dominate, runs are common.
            int index = (int)(Next(ref s) % (uint)colours);
            if ((Next(ref s) & 3) != 0)
            {
                index %= Math.Max(1, colours / 2);
            }

            palette[index].CopyTo(px, p * 4);
        }

        return px;
    }

    private static byte[] AlphaGradient(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / (w - 1));
                px[i + 1] = (byte)(y * 255 / (h - 1));
                px[i + 2] = 90;
                px[i + 3] = (byte)(x * 255 / (w - 1));
            }
        }

        return px;
    }

    // Alpha 0 with colour underneath: lossless means the colour must survive.
    private static byte[] TransparentWithColour(int w, int h)
    {
        uint s = 77;
        byte[] px = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            px[p * 4] = (byte)Next(ref s);
            px[p * 4 + 1] = (byte)Next(ref s);
            px[p * 4 + 2] = (byte)Next(ref s);
        }

        return px;
    }

    // Every byte value equally often in every channel: all 256 codes get length 8.
    private static byte[] UniformBytes(int w, int h)
    {
        uint s = 99;
        byte[] px = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            px[p * 4] = (byte)(p & 255);
            px[p * 4 + 1] = (byte)((p * 7) & 255);
            px[p * 4 + 2] = (byte)((p * 13 + 5) & 255);
            px[p * 4 + 3] = (byte)(p % 3 == 0 ? 255 : Next(ref s) & 0xff);
        }

        return px;
    }

    /// <summary>A green channel whose histogram is Fibonacci, so an unlimited Huffman tree is 23 deep.</summary>
    public static byte[] FibonacciGreen(out int width, out int height)
    {
        long a = 1, b = 1;
        var greens = new List<byte>();
        for (int symbol = 0; symbol < 24; symbol++)
        {
            for (long i = 0; i < a; i++)
            {
                greens.Add((byte)(symbol * 10));
            }

            (a, b) = (b, a + b);
        }

        // 256 wide rows; the tail of the last row repeats the commonest green, which only
        // widens the Fibonacci skew.
        width = 256;
        height = (greens.Count + width - 1) / width;
        uint s = 5;
        for (int i = greens.Count - 1; i > 0; i--)
        {
            int j = (int)(Next(ref s) % (uint)(i + 1));
            (greens[i], greens[j]) = (greens[j], greens[i]);
        }

        byte[] px = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            px[i * 4 + 1] = i < greens.Count ? greens[i] : (byte)230;
            px[i * 4 + 3] = 255;
        }

        return px;
    }
}

public sealed class WebpLosslessEncoderBitWriterTests
{
    // An independent reader: bit n of the stream is bit (n % 8) of byte (n / 8); a value's bits come
    // lowest first.
    private static uint Read(ReadOnlySpan<byte> bytes, ref int bit, int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++, bit++)
        {
            value |= (uint)((bytes[bit >> 3] >> (bit & 7)) & 1) << i;
        }

        return value;
    }

    [Fact]
    public void ValuesPackLeastSignificantBitFirst()
    {
        var writer = new Vp8LBitWriter(0);
        writer.WriteBits(0b101, 3);
        writer.WriteBits(0b11, 2);
        writer.WriteBits(0b0, 1);
        writer.WriteBits(0b1111111, 7);
        Assert.Equal(13, writer.BitCount);

        // bits: 1,0,1 | 1,1 | 0 | 1,1,1,1,1,1,1 -> byte0 = 0b0_11_101 | (1<<6)|(1<<7)
        byte[] bytes = writer.Finish().ToArray();
        Assert.Equal(new byte[] { 0b1101_1101, 0b0001_1111 }, bytes);
    }

    [Fact]
    public void HighBitsOfAValueAreMaskedOff()
    {
        var writer = new Vp8LBitWriter(0);
        writer.WriteBits(0xffffffff, 4);
        writer.WriteBits(0, 4);
        Assert.Equal(new byte[] { 0x0f }, writer.Finish().ToArray());
    }

    [Fact]
    public void ZeroBitWritesAreNoOps()
    {
        var writer = new Vp8LBitWriter(0);
        writer.WriteBits(0xff, 0);
        Assert.Equal(0, writer.BitCount);
        Assert.True(writer.Finish().IsEmpty);
    }

    [Fact]
    public void ThirtyTwoBitValuesRoundTrip()
    {
        var writer = new Vp8LBitWriter(0);
        writer.WriteBits(1, 1);
        writer.WriteBits(0xdeadbeef, 32);
        writer.WriteBits(0xffffffff, 32);
        byte[] bytes = writer.Finish().ToArray();
        int bit = 0;
        Assert.Equal(1u, Read(bytes, ref bit, 1));
        Assert.Equal(0xdeadbeefu, Read(bytes, ref bit, 32));
        Assert.Equal(0xffffffffu, Read(bytes, ref bit, 32));
    }

    [Fact]
    public void RandomWritesReadBackThroughAnIndependentReader()
    {
        var random = new Random(1234);
        var writer = new Vp8LBitWriter(2); // tiny, so the buffer grows many times
        var expected = new List<(uint Value, int Count)>();
        for (int i = 0; i < 5000; i++)
        {
            int count = random.Next(0, 33);
            uint value = (uint)random.NextInt64(0, 1L << 32);
            writer.WriteBits(value, count);
            expected.Add((count == 32 ? value : value & ((1u << count) - 1), count));
        }

        long bitCount = writer.BitCount;
        byte[] bytes = writer.Finish().ToArray();
        int bit = 0;
        foreach ((uint value, int count) in expected)
        {
            Assert.Equal(value, Read(bytes, ref bit, count));
        }

        Assert.Equal(expected.Sum(static e => (long)e.Count), bitCount);
        Assert.Equal((bit + 7) / 8, bytes.Length);
    }

    [Fact]
    public void CountOutsideZeroToThirtyTwoIsRefused()
    {
        var writer = new Vp8LBitWriter(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, 33));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, -1));
    }
}

public sealed class WebpLosslessEncoderHuffmanTests
{
    private static long Kraft(ReadOnlySpan<int> lengths, int limit)
    {
        long sum = 0;
        foreach (int length in lengths)
        {
            if (length > 0)
            {
                sum += 1L << (limit - length);
            }
        }

        return sum;
    }

    private static long Cost(ReadOnlySpan<int> histogram, ReadOnlySpan<int> lengths)
    {
        long cost = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            cost += (long)histogram[i] * lengths[i];
        }

        return cost;
    }

    [Fact]
    public void EmptyHistogramGetsNoCode()
    {
        int[] lengths = new int[10];
        Vp8LHuffman.BuildLengths(new int[10], 15, lengths);
        Assert.All(lengths, static l => Assert.Equal(0, l));
    }

    [Fact]
    public void SingleSymbolGetsLengthOne()
    {
        int[] histogram = new int[256];
        histogram[77] = 1000;
        int[] lengths = new int[256];
        Vp8LHuffman.BuildLengths(histogram, 15, lengths);
        Assert.Equal(1, lengths[77]);
        Assert.Equal(1, lengths.Count(static l => l != 0));
    }

    [Fact]
    public void TwoSymbolsGetOneBitEach()
    {
        int[] histogram = new int[256];
        histogram[3] = 1;
        histogram[200] = 1_000_000;
        int[] lengths = new int[256];
        Vp8LHuffman.BuildLengths(histogram, 15, lengths);
        Assert.Equal(1, lengths[3]);
        Assert.Equal(1, lengths[200]);
    }

    [Fact]
    public void SkewedFibonacciHistogramRespectsTheLengthLimitAndKraftIsOne()
    {
        // Fibonacci weights are the worst case for a Huffman tree: depth n-1 for n symbols.
        int[] histogram = new int[40];
        long a = 1, b = 1;
        for (int i = 0; i < histogram.Length; i++)
        {
            histogram[i] = (int)a;
            (a, b) = (b, a + b);
        }

        int[] unlimited = new int[40];
        Vp8LHuffman.BuildLengths(histogram, 40, unlimited);
        Assert.True(unlimited.Max() > 15, "the histogram must actually need limiting");

        int[] lengths = new int[40];
        Vp8LHuffman.BuildLengths(histogram, 15, lengths);
        Assert.InRange(lengths.Max(), 1, 15);
        Assert.All(lengths, static l => Assert.True(l >= 1));
        Assert.Equal(1L << 15, Kraft(lengths, 15));
        Assert.True(Cost(histogram, lengths) >= Cost(histogram, unlimited));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(100)]
    [InlineData(280)]
    public void RandomHistogramsGiveCompleteCodesAndTheOptimalCostWhenTheLimitDoesNotBind(int symbols)
    {
        var random = new Random(symbols);
        for (int round = 0; round < 50; round++)
        {
            int[] histogram = new int[symbols];
            for (int i = 0; i < symbols; i++)
            {
                histogram[i] = random.Next(4) == 0 ? 0 : random.Next(1, 1 + (1 << random.Next(1, 12)));
            }

            if (histogram.Count(static h => h > 0) < 2)
            {
                continue;
            }

            int[] lengths = new int[symbols];
            Vp8LHuffman.BuildLengths(histogram, 15, lengths);
            Assert.Equal(1L << 15, Kraft(lengths, 15));
            for (int i = 0; i < symbols; i++)
            {
                Assert.Equal(histogram[i] > 0, lengths[i] > 0);
                Assert.True(lengths[i] <= 15);
            }

            int[] unlimited = new int[symbols];
            Vp8LHuffman.BuildLengths(histogram, 30, unlimited);
            if (unlimited.Max() <= 15)
            {
                Assert.Equal(Cost(histogram, unlimited), Cost(histogram, lengths));
            }
        }
    }

    [Fact]
    public void CanonicalCodesArePrefixFreeWhenReadMostSignificantBitFirst()
    {
        int[] histogram = [5, 9, 12, 13, 16, 45, 0, 1];
        int[] lengths = new int[histogram.Length];
        Vp8LHuffman.BuildLengths(histogram, 15, lengths);
        uint[] codes = new uint[lengths.Length];
        Vp8LHuffman.AssignCodes(lengths, codes);

        // Un-reverse each stored code into the bit string the decoder reads first-bit-first.
        var strings = new List<string>();
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] == 0)
            {
                continue;
            }

            var s = new StringBuilder();
            for (int bit = 0; bit < lengths[i]; bit++)
            {
                s.Append(((codes[i] >> bit) & 1) == 1 ? '1' : '0');
            }

            strings.Add(s.ToString());
        }

        foreach (string x in strings)
        {
            foreach (string y in strings)
            {
                if (!ReferenceEquals(x, y))
                {
                    Assert.False(y.StartsWith(x, StringComparison.Ordinal), $"{x} is a prefix of {y}");
                }
            }
        }
    }

    [Fact]
    public void MoreSymbolsThanTheLimitCanCodeIsRefused()
    {
        int[] histogram = Enumerable.Repeat(1, 9).ToArray();
        Assert.Throws<ArgumentException>(() => Vp8LHuffman.BuildLengths(histogram, 3, new int[9]));
    }
}

public sealed class WebpLosslessEncoderRoundTripTests
{
    public static IEnumerable<object[]> CorpusNames() => WebpLosslessEncoderCorpus.Names();

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void RoundTripsByteForByteThroughOurDecoder(string name)
    {
        var image = WebpLosslessEncoderCorpus.Get(name);
        byte[] file = WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height);

        WebpImage decoded = WebpDecoder.Decode(file);
        Assert.Equal(image.Width, decoded.Width);
        Assert.Equal(image.Height, decoded.Height);
        Assert.True(image.Rgba.AsSpan().SequenceEqual(decoded.Rgba), "decoded pixels differ from the source");

        WebpInfo info = WebpDecoder.ReadInfo(file);
        Assert.Equal(WebpFormat.Lossless, info.Format);
        Assert.Equal(image.Rgba.Where(static (_, i) => i % 4 == 3).Any(static a => a != 255), info.HasAlpha);
    }

    [Fact]
    public void ALimitedLengthCodeFromAFibonacciHistogramRoundTrips()
    {
        byte[] rgba = WebpLosslessEncoderCorpus.FibonacciGreen(out int w, out int h);
        byte[] file = WebpEncoder.EncodeLossless(rgba, w, h);
        Assert.True(rgba.AsSpan().SequenceEqual(WebpDecoder.Decode(file).Rgba));
    }

    [Fact]
    public void RgbOverloadDecodesOpaque()
    {
        const int w = 37, h = 11;
        byte[] rgb = new byte[w * h * 3];
        new Random(5).NextBytes(rgb);
        byte[] expected = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            expected[p * 4] = rgb[p * 3];
            expected[p * 4 + 1] = rgb[p * 3 + 1];
            expected[p * 4 + 2] = rgb[p * 3 + 2];
            expected[p * 4 + 3] = 255;
        }

        WebpImage decoded = WebpDecoder.Decode(WebpEncoder.EncodeLosslessRgb(rgb, w, h));
        Assert.False(decoded.HasAlpha);
        Assert.True(expected.AsSpan().SequenceEqual(decoded.Rgba));
    }

    [Fact]
    public void GrayOverloadDecodesAsEqualChannels()
    {
        const int w = 41, h = 13;
        byte[] gray = new byte[w * h];
        new Random(6).NextBytes(gray);
        WebpImage decoded = WebpDecoder.Decode(WebpEncoder.EncodeLosslessGray(gray, w, h));
        Assert.False(decoded.HasAlpha);
        for (int p = 0; p < w * h; p++)
        {
            Assert.Equal(gray[p], decoded.Rgba[p * 4]);
            Assert.Equal(gray[p], decoded.Rgba[p * 4 + 1]);
            Assert.Equal(gray[p], decoded.Rgba[p * 4 + 2]);
            Assert.Equal(255, decoded.Rgba[p * 4 + 3]);
        }
    }

    [Fact]
    public void StridedInputIgnoresThePaddingBetweenRows()
    {
        const int w = 9, h = 5, stride = w * 4 + 13;
        byte[] padded = new byte[stride * h];
        Array.Fill(padded, (byte)0xAB);
        byte[] tight = new byte[w * h * 4];
        new Random(7).NextBytes(tight);
        for (int y = 0; y < h; y++)
        {
            tight.AsSpan(y * w * 4, w * 4).CopyTo(padded.AsSpan(y * stride));
        }

        byte[] fromTight = WebpEncoder.EncodeLossless(tight, w, h);
        byte[] fromPadded = WebpEncoder.EncodeLossless(padded, w, h, stride);
        Assert.Equal(fromTight, fromPadded);
    }

    [Fact]
    public void TheLastRowNeedsNoTrailingPadding()
    {
        const int w = 4, h = 3, stride = 24;
        byte[] buffer = new byte[stride * (h - 1) + w * 4];
        _ = WebpEncoder.EncodeLossless(buffer, w, h, stride);
    }

    [Fact]
    public void TheLargestDimensionsRoundTrip()
    {
        byte[] wide = new byte[16384 * 4];
        new Random(8).NextBytes(wide);
        Assert.True(wide.AsSpan().SequenceEqual(
            WebpDecoder.Decode(WebpEncoder.EncodeLossless(wide, 16384, 1)).Rgba));

        byte[] tall = new byte[16384 * 4];
        new Random(9).NextBytes(tall);
        Assert.True(tall.AsSpan().SequenceEqual(
            WebpDecoder.Decode(WebpEncoder.EncodeLossless(tall, 1, 16384)).Rgba));
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        var image = WebpLosslessEncoderCorpus.Get("photo-noise");
        Assert.Equal(
            WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height),
            WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height));
    }

    [Fact]
    public void FlatImagesCostAlmostNothing()
    {
        // Every channel has one symbol, so every code is a zero-bit code and the pixels cost no bits.
        byte[] rgba = new byte[256 * 256 * 4];
        Array.Fill(rgba, (byte)200);
        byte[] file = WebpEncoder.EncodeLossless(rgba, 256, 256);
        Assert.True(file.Length < 64, $"a flat 256x256 image took {file.Length} bytes");
        Assert.True(rgba.AsSpan().SequenceEqual(WebpDecoder.Decode(file).Rgba));
    }
}

public sealed class WebpLosslessEncoderMetadataTests
{
    private static List<string> Chunks(byte[] file)
    {
        var types = new List<string>();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal((uint)(file.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(file, 8, 4));
        int offset = 12;
        while (offset < file.Length)
        {
            types.Add(Encoding.ASCII.GetString(file, offset, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4));
            offset += 8 + (int)length + (int)(length & 1);
        }

        Assert.Equal(file.Length, offset);
        return types;
    }

    private static byte[] Pixels(int w, int h, bool alpha)
    {
        byte[] px = new byte[w * h * 4];
        new Random(11).NextBytes(px);
        if (!alpha)
        {
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        }

        return px;
    }

    [Fact]
    public void NoMetadataGivesSimpleVp8l()
    {
        byte[] file = WebpEncoder.EncodeLossless(Pixels(10, 6, alpha: true), 10, 6);
        Assert.Equal(new[] { "VP8L" }, Chunks(file).ToArray());

        file = WebpEncoder.EncodeLossless(Pixels(10, 6, alpha: false), 10, 6, options: new WebpEncodeOptions());
        Assert.Equal(new[] { "VP8L" }, Chunks(file).ToArray());

        file = WebpEncoder.EncodeLossless(
            Pixels(10, 6, alpha: false), 10, 6, options: new WebpEncodeOptions { IccProfile = [], Exif = null, Xmp = [] });
        Assert.Equal(new[] { "VP8L" }, Chunks(file).ToArray());
    }

    [Fact]
    public void AllMetadataRoundTripsInTheRequiredChunkOrder()
    {
        byte[] icc = Enumerable.Range(0, 131).Select(static i => (byte)(i * 3)).ToArray(); // odd length: padded
        byte[] exif = [0x49, 0x49, 0x2a, 0x00, 1, 2, 3, 4];
        byte[] xmp = Encoding.UTF8.GetBytes("<x:xmpmeta/>"); // 12 bytes
        byte[] pixels = Pixels(13, 9, alpha: true);

        byte[] file = WebpEncoder.EncodeLossless(
            pixels, 13, 9, options: new WebpEncodeOptions { IccProfile = icc, Exif = exif, Xmp = xmp });

        Assert.Equal(new[] { "VP8X", "ICCP", "VP8L", "EXIF", "XMP " }, Chunks(file).ToArray());
        byte flags = file[20];
        Assert.Equal(0x20 | 0x10 | 0x08 | 0x04, flags);

        WebpImage decoded = WebpDecoder.Decode(file);
        Assert.Equal(icc, decoded.IccProfile);
        Assert.Equal(exif, decoded.Exif);
        Assert.Equal(xmp, decoded.Xmp);
        Assert.True(pixels.AsSpan().SequenceEqual(decoded.Rgba));
        Assert.True(decoded.HasAlpha);
        Assert.Equal(13, WebpDecoder.ReadInfo(file).Width);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void EachKindAloneRoundTrips(bool icc, bool exif, bool xmp)
    {
        byte[] iccBytes = [1, 2, 3];
        byte[] exifBytes = [4, 5, 6, 7];
        byte[] xmpBytes = [8];
        byte[] pixels = Pixels(5, 5, alpha: false);
        byte[] file = WebpEncoder.EncodeLossless(
            pixels,
            5,
            5,
            options: new WebpEncodeOptions
            {
                IccProfile = icc ? iccBytes : null,
                Exif = exif ? exifBytes : null,
                Xmp = xmp ? xmpBytes : null,
            });

        var expectedChunks = new List<string> { "VP8X" };
        if (icc) expectedChunks.Add("ICCP");
        expectedChunks.Add("VP8L");
        if (exif) expectedChunks.Add("EXIF");
        if (xmp) expectedChunks.Add("XMP ");
        Assert.Equal(expectedChunks, Chunks(file));
        Assert.Equal(0, file[20] & 0x10); // opaque: no alpha flag

        WebpImage decoded = WebpDecoder.Decode(file);
        Assert.Equal(icc ? iccBytes : null, decoded.IccProfile);
        Assert.Equal(exif ? exifBytes : null, decoded.Exif);
        Assert.Equal(xmp ? xmpBytes : null, decoded.Xmp);
        Assert.False(decoded.HasAlpha);
        Assert.True(pixels.AsSpan().SequenceEqual(decoded.Rgba));
    }

    [Fact]
    public void MetadataIsAttachedToRgbAndGrayEncodesToo()
    {
        byte[] exif = [9, 9, 9];
        var options = new WebpEncodeOptions { Exif = exif };
        Assert.Equal(exif, WebpDecoder.Decode(WebpEncoder.EncodeLosslessRgb(new byte[3 * 4], 2, 2, options: options)).Exif);
        Assert.Equal(exif, WebpDecoder.Decode(WebpEncoder.EncodeLosslessGray(new byte[4], 2, 2, options: options)).Exif);
    }
}

public sealed class WebpLosslessEncoderRefusalTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-5, 4)]
    [InlineData(4, -5)]
    [InlineData(16385, 1)]
    [InlineData(1, 16385)]
    public void DimensionsOutsideTheHeaderRangeAreRefused(int width, int height)
    {
        byte[] buffer = new byte[64];
        Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossless(buffer, width, height));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLosslessRgb(buffer, width, height));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLosslessGray(buffer, width, height));
    }

    [Fact]
    public void MoreThanTheDecodersPixelLimitIsRefusedBeforeAnythingIsAllocated()
    {
        // 8192 x 16384 is 2^27 pixels, over the 2^26 limit; the buffer is empty on purpose, so the
        // pixel limit has to fire before the buffer length is even looked at, let alone before the
        // 512 MiB of working memory would be.
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossless([], 8192, 16384));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossless([], 16384, 16384));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64 * 1024, $"the refusal allocated {allocated} bytes");
    }

    [Fact]
    public void TheLimitItselfIsNotRefusedForSize()
    {
        // 8192 x 8192 is exactly 2^26 pixels; with a short buffer the failure must be the buffer's,
        // not the pixel limit's.
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLossless(new byte[16], 8192, 8192));
    }

    [Fact]
    public void ABufferShorterThanTheImageIsRefused()
    {
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLossless(new byte[4 * 4 * 4 - 1], 4, 4));
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLosslessRgb(new byte[4 * 4 * 3 - 1], 4, 4));
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLosslessGray(new byte[4 * 4 - 1], 4, 4));
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLossless([], 1, 1));
        // With a stride the last row still needs its pixels but not its padding.
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLossless(new byte[24 * 2 + 15], 4, 3, 24));
    }

    [Fact]
    public void AStrideSmallerThanARowIsRefused()
    {
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLossless(new byte[256], 4, 4, 15));
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLossless(new byte[256], 4, 4, -16));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void AnEffortOutsideZeroToNineIsRefused(int effort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WebpEncoder.EncodeLossless(new byte[4], 1, 1, options: new WebpEncodeOptions { Effort = effort }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void EveryEffortInRangeIsAccepted(int effort)
    {
        byte[] pixels = [1, 2, 3, 4];
        byte[] file = WebpEncoder.EncodeLossless(pixels, 1, 1, options: new WebpEncodeOptions { Effort = effort });
        Assert.True(pixels.AsSpan().SequenceEqual(WebpDecoder.Decode(file).Rgba));
    }
}

public sealed class WebpLosslessEncoderOracleTests
{
    public static IEnumerable<object[]> CorpusNames() => WebpLosslessEncoderCorpus.Names();

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void DwebpDecodesOurFileToTheSourcePixels(string name)
    {
        string dwebp = WebpOracle.RequireDwebp();
        var image = WebpLosslessEncoderCorpus.Get(name);
        byte[] file = WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height);

        (int w, int h, byte[] pixels) = RunDwebpPam(dwebp, file);
        Assert.Equal(image.Width, w);
        Assert.Equal(image.Height, h);
        Assert.True(image.Rgba.AsSpan().SequenceEqual(pixels), Describe(image.Rgba, pixels));
    }

    public static IEnumerable<object[]> NamesByEffort() => WebpLosslessEncoderCorpus.NamesByEffort();

    [Theory]
    [MemberData(nameof(NamesByEffort))]
    public void DwebpDecodesOurFileAtEveryEffortToTheSourcePixels(string name, int effort)
    {
        string dwebp = WebpOracle.RequireDwebp();
        var image = WebpLosslessEncoderCorpus.Get(name);
        byte[] file = WebpEncoder.EncodeLossless(
            image.Rgba, image.Width, image.Height, options: new WebpEncodeOptions { Effort = effort });

        (int w, int h, byte[] pixels) = RunDwebpPam(dwebp, file);
        Assert.Equal(image.Width, w);
        Assert.Equal(image.Height, h);
        Assert.True(image.Rgba.AsSpan().SequenceEqual(pixels), Describe(image.Rgba, pixels));
    }

    [Theory]
    [InlineData("tiled-pattern")]
    [InlineData("palette-16")]
    [InlineData("far-repeat")]
    [InlineData("flat-ui")]
    public void DwebpReadsEveryCacheSizeWithAndWithoutCopies(string name)
    {
        string dwebp = WebpOracle.RequireDwebp();
        var image = WebpLosslessEncoderCorpus.Get(name);
        int[] argb = WebpLosslessEncoderCorpus.ToArgb(image.Rgba);
        var plan = Vp8LEncoderPlan.Create((long)image.Width * image.Height, 9, WebpLimits.MaximumEncoderWorkingBytes);
        for (int cacheBits = 0; cacheBits <= 11; cacheBits++)
        {
            foreach (bool copies in new[] { false, true })
            {
                byte[] vp8l = Vp8LEncoder.EncodeCandidate(argb, image.Width, image.Height, false, plan, cacheBits, copies);
                byte[] file = WebpWriter.Write(vp8l, image.Width, image.Height, false, null, null, null);
                (_, _, byte[] pixels) = RunDwebpPam(dwebp, file);
                Assert.True(
                    image.Rgba.AsSpan().SequenceEqual(pixels),
                    $"cache {cacheBits}, copies {copies}: {Describe(image.Rgba, pixels)}");
            }
        }
    }

    [Fact]
    public void DwebpReadsALimitedLengthFibonacciCode()
    {
        string dwebp = WebpOracle.RequireDwebp();
        byte[] rgba = WebpLosslessEncoderCorpus.FibonacciGreen(out int w, out int h);
        byte[] file = WebpEncoder.EncodeLossless(rgba, w, h);
        (_, _, byte[] pixels) = RunDwebpPam(dwebp, file);
        Assert.True(rgba.AsSpan().SequenceEqual(pixels), Describe(rgba, pixels));
    }

    [Fact]
    public void DwebpReadsTheRgbAndGrayOverloads()
    {
        string dwebp = WebpOracle.RequireDwebp();
        byte[] rgb = new byte[17 * 9 * 3];
        new Random(21).NextBytes(rgb);
        (_, _, byte[] fromRgb) = RunDwebpPam(dwebp, WebpEncoder.EncodeLosslessRgb(rgb, 17, 9));
        for (int p = 0; p < 17 * 9; p++)
        {
            Assert.Equal(rgb[p * 3], fromRgb[p * 4]);
            Assert.Equal(rgb[p * 3 + 1], fromRgb[p * 4 + 1]);
            Assert.Equal(rgb[p * 3 + 2], fromRgb[p * 4 + 2]);
            Assert.Equal(255, fromRgb[p * 4 + 3]);
        }

        byte[] gray = new byte[17 * 9];
        new Random(22).NextBytes(gray);
        (_, _, byte[] fromGray) = RunDwebpPam(dwebp, WebpEncoder.EncodeLosslessGray(gray, 17, 9));
        for (int p = 0; p < 17 * 9; p++)
        {
            Assert.Equal(gray[p], fromGray[p * 4]);
            Assert.Equal(gray[p], fromGray[p * 4 + 1]);
            Assert.Equal(gray[p], fromGray[p * 4 + 2]);
            Assert.Equal(255, fromGray[p * 4 + 3]);
        }
    }

    [Fact]
    public void DwebpReadsTheExtendedLayoutWithMetadata()
    {
        string dwebp = WebpOracle.RequireDwebp();
        var image = WebpLosslessEncoderCorpus.Get("alpha-gradient");
        byte[] file = WebpEncoder.EncodeLossless(
            image.Rgba,
            image.Width,
            image.Height,
            options: new WebpEncodeOptions
            {
                IccProfile = new byte[101],
                Exif = [1, 2, 3],
                Xmp = Encoding.UTF8.GetBytes("<x/>"),
            });
        (_, _, byte[] pixels) = RunDwebpPam(dwebp, file);
        Assert.True(image.Rgba.AsSpan().SequenceEqual(pixels), Describe(image.Rgba, pixels));
    }

    internal static string Describe(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return $"lengths differ: expected {expected.Length}, dwebp gave {actual.Length}";
        }

        int i = expected.AsSpan().CommonPrefixLength(actual);
        if (i == expected.Length)
        {
            return "identical";
        }

        return $"first difference at byte {i} (pixel {i / 4}, channel {i % 4}): expected {expected[i]}, dwebp gave {actual[i]}";
    }

    internal static (int Width, int Height, byte[] Rgba) RunDwebpPam(string dwebp, byte[] file)
    {
        string stem = Path.Combine(Path.GetTempPath(), $"webp-lossless-encoder-{Guid.NewGuid():N}");
        string input = stem + ".webp";
        string output = stem + ".pam";
        try
        {
            File.WriteAllBytes(input, file);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(dwebp)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-quiet");
            process.StartInfo.ArgumentList.Add("-pam");
            process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(output);
            process.Start();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"dwebp exited {process.ExitCode}: {error}");
            return ReadPam(File.ReadAllBytes(output));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static (int Width, int Height, byte[] Rgba) ReadPam(byte[] pam)
    {
        int width = 0, height = 0, depth = 0;
        int lineStart = 0;
        while (lineStart < pam.Length)
        {
            int lineEnd = Array.IndexOf(pam, (byte)'\n', lineStart);
            if (lineEnd < 0) break;
            string line = Encoding.ASCII.GetString(pam, lineStart, lineEnd - lineStart).TrimEnd('\r');
            lineStart = lineEnd + 1;
            if (line.StartsWith("WIDTH ", StringComparison.Ordinal)) width = int.Parse(line[6..]);
            else if (line.StartsWith("HEIGHT ", StringComparison.Ordinal)) height = int.Parse(line[7..]);
            else if (line.StartsWith("DEPTH ", StringComparison.Ordinal)) depth = int.Parse(line[6..]);
            else if (line == "ENDHDR")
            {
                Assert.Equal(4, depth);
                return (width, height, pam.AsSpan(lineStart).ToArray());
            }
        }

        throw new InvalidDataException("The dwebp output does not contain a PAM header.");
    }
}
