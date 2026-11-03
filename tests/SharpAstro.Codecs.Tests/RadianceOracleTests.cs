using System.Text;
using ImageMagick;
using SharpAstro.Radiance;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Radiance oracle: Magick.NET (ImageMagick Q16-HDRI) reads and writes <c>.hdr</c>, so both directions are
/// checked with no install and no skip. RGBE is lossy against the source floats, so every comparison is
/// decoded-to-decoded: ImageMagick's floats for some bytes against ours for the same bytes.
///
/// <para>ImageMagick's reading, measured (it is the Done-when's reference): a pixel is
/// <c>mantissa * 2^(exponent - 136)</c> (no <c>+0.5</c> half step, unlike Radiance's own colr_color), exponent
/// byte 0 is black, and the value lands in the quantum domain multiplied by exactly
/// <see cref="Q"/> = 65535 (QuantumRange of Q16-HDRI), the same factor <see cref="ExrOracleTests"/> measured for EXR.
/// The GAMMA=1 line its writer adds is ignored on read. The product is exact in float, so equality is
/// bit-for-bit, with no tolerance.</para>
/// </summary>
public sealed class RadianceOracleTests
{
    private const float Q = 65535f;

    /// <summary>A float image that exercises zeros, tiny values, HDR values over 1, constant runs (RLE runs),
    /// noise (RLE literals) and a mix.</summary>
    internal static float[] TestImage(int w, int h, int seed = 7)
    {
        var rnd = new Random(seed);
        var px = new float[w * h * 3];
        float Noise() => MathF.Pow(10f, (float)(rnd.NextDouble() * 9 - 6)); // 1e-6 .. 1e3
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                switch (y % 5)
                {
                    case 0: break;                                                       // black row
                    case 1: px[o] = 2.5f; px[o + 1] = 0.25f; px[o + 2] = 1000f; break;    // one long run
                    case 2: px[o] = Noise(); px[o + 1] = Noise(); px[o + 2] = Noise(); break; // literals
                    case 3: // runs, literals and zeros interleaved
                        if ((x / 9) % 3 == 0) { px[o] = 1f; px[o + 1] = 0.5f; px[o + 2] = 0.125f; }
                        else if ((x / 9) % 3 == 1) { px[o] = Noise(); px[o + 1] = Noise(); px[o + 2] = Noise(); }
                        break;
                    default: px[o] = Noise() * 1e-3f; px[o + 1] = 0; px[o + 2] = Noise(); break; // one channel exactly 0, tiny values
                }
            }
        }
        return px;
    }

    /// <summary>The bytes ImageMagick writes for <paramref name="rgb"/> (scene-linear, 1.0 = 1.0).</summary>
    internal static byte[] MagickWrite(float[] rgb, int w, int h)
    {
        using var m = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        m.ColorSpace = ColorSpace.RGB;
        using (var px = m.GetPixels())
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    px.SetPixel(x, y, [rgb[o] * Q, rgb[o + 1] * Q, rgb[o + 2] * Q]);
                }
        m.Format = MagickFormat.Hdr;
        return m.ToByteArray();
    }

    /// <summary>ImageMagick's decode of <paramref name="hdr"/>, in quantum units (scene-linear * <see cref="Q"/>), interleaved.
    /// <see cref="AssertSameAsMagick"/> scales ours up rather than theirs down, so no float division is involved.</summary>
    private static float[] MagickQuantums(byte[] hdr, out int w, out int h)
    {
        using var m = new MagickImage(hdr);
        w = (int)m.Width; h = (int)m.Height;
        m.ChannelCount.ShouldBe(3u);
        using var px = m.GetPixels();
        return px.ToArray()!;
    }

    internal static void AssertSameAsMagick(byte[] hdr, RadianceImage ours)
    {
        var theirs = MagickQuantums(hdr, out int w, out int h);
        w.ShouldBe(ours.Width); h.ShouldBe(ours.Height);
        theirs.Length.ShouldBe(ours.Pixels.Length);
        for (int i = 0; i < theirs.Length; i++)
            (ours.Pixels[i] * Q).ShouldBe(theirs[i], $"sample {i} (pixel {i / 3}, channel {i % 3})");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]     // < 8 wide: flat scanlines
    [InlineData(7, 3)]
    [InlineData(8, 5)]     // narrowest new-style RLE
    [InlineData(20, 10)]
    [InlineData(137, 10)]  // more than one literal block of 128 and runs of 127
    [InlineData(1000, 6)]
    public void MagickWritten_DecodesToMagicksOwnFloats(int w, int h)
    {
        var src = TestImage(w, h);
        var hdr = MagickWrite(src, w, h);

        var ours = RadianceFile.Read(hdr);
        ours.Format.ShouldBe(RadianceFormat.Rgbe);
        ours.Exposure.ShouldBeNull();
        AssertSameAsMagick(hdr, ours);

        // The wide cases must really be new-style RLE, or this would not test it.
        string text = Encoding.Latin1.GetString(hdr);
        int res = text.IndexOf("-Y ", StringComparison.Ordinal);
        int body = text.IndexOf('\n', res) + 1;
        bool rle = hdr[body] == 2 && hdr[body + 1] == 2;
        rle.ShouldBe(w >= 8, "ImageMagick writes new-style RLE from width 8 and flat below");
    }

    [Fact]
    public void MagickWritten_ValuesAreNearTheSource()
    {
        // Sanity that "decoded-to-decoded" is not hiding a scale error: RGBE keeps the largest channel to 8 bits.
        const int w = 40, h = 10;
        var src = TestImage(w, h);
        var ours = RadianceFile.Read(MagickWrite(src, w, h));
        for (int p = 0; p < w * h; p++)
        {
            float max = Math.Max(src[p * 3], Math.Max(src[p * 3 + 1], src[p * 3 + 2]));
            for (int c = 0; c < 3; c++)
                MathF.Abs(ours.Pixels[p * 3 + c] - src[p * 3 + c]).ShouldBeLessThanOrEqualTo(max / 64f + 1e-30f);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]       // flat
    [InlineData(8, 4)]
    [InlineData(20, 10)]
    [InlineData(300, 7)]
    [InlineData(40000, 1)]   // wider than new-style RLE can carry: flat
    public void OurWriter_ReadByMagick_EqualsOurDecode(int w, int h)
    {
        var src = TestImage(w, h, seed: 11);
        var hdr = RadianceFile.Write(src, w, h);

        var ours = RadianceFile.Read(hdr);
        AssertSameAsMagick(hdr, ours);
    }
}
