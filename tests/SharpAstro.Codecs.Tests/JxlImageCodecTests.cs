using ImageMagick;
using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// JPEG XL public façade (<see cref="JxlImageCodec"/>) — Rung 6 for the lossless Modular path.
/// Exercises the typed encode/decode entry points end-to-end, including decoding a real
/// libjxl-encoded image through the public API.
/// </summary>
public sealed class JxlImageCodecTests
{
    [Fact]
    public void Rgb24_RoundTrips_PixelExact()
    {
        const int w = 40, h = 30;
        (int[] r, int[] g, int[] b) = MakeRgb(w, h, max: 256);

        byte[] jxl = JxlImageCodec.EncodeRgb24(r, g, b, w, h);
        (int dw, int dh, int[] dr, int[] dg, int[] db) = JxlImageCodec.DecodeRgb24(jxl);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        dr.ShouldBe(r);
        dg.ShouldBe(g);
        db.ShouldBe(b);

        // …and through the general Decode, reporting 8-bit RGB.
        JxlImage img = JxlImageCodec.Decode(jxl);
        img.ColorChannels.ShouldBe(3);
        img.BitsPerSample.ShouldBe(8);
    }

    [Fact]
    public void Gray8_RoundTrips_PixelExact()
    {
        const int w = 33, h = 17;
        int[] y = MakeGray(w, h, max: 256);

        byte[] jxl = JxlImageCodec.EncodeGray8(y, w, h);
        (int dw, int dh, int[] dy) = JxlImageCodec.DecodeGray8(jxl);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        dy.ShouldBe(y);
    }

    [Fact]
    public void Rgb48_And_Gray16_RoundTrip_PixelExact()
    {
        const int w = 48, h = 24;
        (int[] r, int[] g, int[] b) = MakeRgb(w, h, max: 65536);
        byte[] rgb = JxlImageCodec.EncodeRgb48(r, g, b, w, h);
        (_, _, int[] dr, int[] dg, int[] db) = JxlImageCodec.DecodeRgb48(rgb);
        dr.ShouldBe(r); dg.ShouldBe(g); db.ShouldBe(b);
        JxlImageCodec.Decode(rgb).BitsPerSample.ShouldBe(16);

        int[] y = MakeGray(w, h, max: 65536);
        byte[] gray = JxlImageCodec.EncodeGray16(y, w, h);
        (_, _, int[] dy) = JxlImageCodec.DecodeGray16(gray);
        dy.ShouldBe(y);
    }

    [Fact]
    public void EncodedRgb24_DecodesInLibjxl()
    {
        const int w = 32, h = 24;
        (int[] r, int[] g, int[] b) = MakeRgb(w, h, max: 256);

        byte[] jxl = JxlImageCodec.EncodeRgb24(r, g, b, w, h);

        using var img = new MagickImage(jxl); // libjxl decode of our façade output
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        using IPixelCollection<float> px = img.GetPixels();
        int mc = (int)px.Channels;
        float[] v = px.GetValues()!;
        for (int i = 0; i < w * h; i++)
        {
            ((int)Math.Round(v[i * mc + 0] / 257f)).ShouldBe(r[i]);
            ((int)Math.Round(v[i * mc + 1] / 257f)).ShouldBe(g[i]);
            ((int)Math.Round(v[i * mc + 2] / 257f)).ShouldBe(b[i]);
        }
    }

    [Fact]
    public void Decode_MagickEncodedLossless_MatchesLibjxl()
    {
        // A real libjxl-encoded lossless image decoded through the public façade.
        using var truth = new MagickImage(MagickColors.SteelBlue, 24, 16);
        truth.Quality = 100;
        byte[] jxl = truth.ToByteArray(MagickFormat.Jxl);

        (int w, int h, int[] r, int[] g, int[] b) = JxlImageCodec.DecodeRgb24(jxl);
        w.ShouldBe(24);
        h.ShouldBe(16);

        using var reload = new MagickImage(jxl);
        using IPixelCollection<float> px = reload.GetPixels();
        int mc = (int)px.Channels;
        float[] vals = px.GetValues()!;
        for (int i = 0; i < w * h; i++)
        {
            r[i].ShouldBe((int)Math.Round(vals[i * mc + 0]));
            g[i].ShouldBe((int)Math.Round(vals[i * mc + 1]));
            b[i].ShouldBe((int)Math.Round(vals[i * mc + 2]));
        }
    }

    [Fact]
    public void GrayF32_RoundTrips_BitExact()
    {
        const int w = 20, h = 15;
        var y = new float[w * h];
        for (int i = 0; i < y.Length; i++)
            y[i] = (i - 100) * 3.5f + (i % 7) * 0.0009765625f; // +/-, fractional, a few large

        byte[] jxl = JxlImageCodec.EncodeGrayF32(y, w, h);
        (int dw, int dh, float[] dy) = JxlImageCodec.DecodeGrayF32(jxl);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        dy.ShouldBe(y); // lossless => bit-exact

        JxlImageCodec.Decode(jxl).FloatingPoint.ShouldBeTrue();
    }

    [Fact]
    public void GrayF16_RoundTrips_BitExact()
    {
        const int w = 24, h = 16;
        var y = new Half[w * h];
        for (int i = 0; i < y.Length; i++)
            y[i] = (Half)((i - 50) * 0.25f);

        byte[] jxl = JxlImageCodec.EncodeGrayF16(y, w, h);
        (int dw, int dh, Half[] dy) = JxlImageCodec.DecodeGrayF16(jxl);
        dw.ShouldBe(w);
        dh.ShouldBe(h);
        dy.ShouldBe(y);
    }

    [Fact]
    public void RgbF16_Interleaved_RoundTrips_BitExact()
    {
        const int w = 18, h = 12;
        var rgb = new Half[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = (Half)(i * 0.5f);
            rgb[i * 3 + 1] = (Half)(-i * 0.125f);
            rgb[i * 3 + 2] = (Half)(i % 13 * 2.0f);
        }

        byte[] jxl = JxlImageCodec.EncodeRgbF16(rgb, w, h);
        (int dw, int dh, Half[] drgb) = JxlImageCodec.DecodeRgbF16(jxl);
        dw.ShouldBe(w);
        dh.ShouldBe(h);
        drgb.ShouldBe(rgb);
    }

    [Fact]
    public void RgbF32_Interleaved_RoundTrips_BitExact()
    {
        const int w = 16, h = 10;
        var rgb = new float[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = i * 12.5f;
            rgb[i * 3 + 1] = -i * 0.001f;
            rgb[i * 3 + 2] = 65000f + i;
        }

        byte[] jxl = JxlImageCodec.EncodeRgbF32(rgb, w, h);
        (int dw, int dh, float[] drgb) = JxlImageCodec.DecodeRgbF32(jxl);
        dw.ShouldBe(w);
        dh.ShouldBe(h);
        drgb.ShouldBe(rgb);
    }

    [Fact]
    public void EncodedGrayF32_DecodesInLibjxl()
    {
        // libjxl scales a decoded float sample by QuantumRange (65535); F32 keeps it as an HDRI
        // float. Use [0,1] values so the scaling stays well-conditioned.
        const int w = 16, h = 8;
        var y = new float[w * h];
        for (int i = 0; i < y.Length; i++) y[i] = (i % 50) / 49f;

        byte[] jxl = JxlImageCodec.EncodeGrayF32(y, w, h);

        using var img = new MagickImage(jxl); // libjxl decode of our float output
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        using IPixelCollection<float> px = img.GetPixels();
        int mc = (int)px.Channels;
        float[] v = px.GetValues()!;
        for (int i = 0; i < w * h; i++)
            v[i * mc].ShouldBe(y[i] * 65535f, tolerance: 1.5f);
    }

    [Fact]
    public void EncodedRgbF16_DecodesInLibjxl()
    {
        const int w = 12, h = 6;
        var rgb = new Half[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = (Half)((i % 50) / 49f);
            rgb[i * 3 + 1] = (Half)((i % 30) / 29f);
            rgb[i * 3 + 2] = (Half)((i % 17) / 16f);
        }

        byte[] jxl = JxlImageCodec.EncodeRgbF16(rgb, w, h);

        using var img = new MagickImage(jxl);
        img.Width.ShouldBe((uint)w);
        using IPixelCollection<float> px = img.GetPixels();
        int mc = (int)px.Channels;
        float[] v = px.GetValues()!;
        for (int i = 0; i < w * h; i++)
            for (int c = 0; c < 3; c++)
            {
                // The half value is what's stored; libjxl gives round(half * 65535) at depth 16.
                float expected = (float)rgb[i * 3 + c] * 65535f;
                v[i * mc + c].ShouldBe(expected, tolerance: 1.5f);
            }
    }

    [Fact]
    public void Rgb24Lossy_RoundTrips_WithinTolerance()
    {
        const int w = 64, h = 48; // multiples of 8
        (int[] r, int[] g, int[] b) = SmoothRgb(w, h);

        byte[] jxl = JxlImageCodec.EncodeRgb24Lossy(r, g, b, w, h);

        // The general Decode auto-detects VarDCT and returns 8-bit RGB.
        JxlImage img = JxlImageCodec.Decode(jxl);
        img.ColorChannels.ShouldBe(3);
        img.BitsPerSample.ShouldBe(8);
        img.Width.ShouldBe(w);
        img.Height.ShouldBe(h);
        Rmse8(img.Channels, [r, g, b], w, h).ShouldBeLessThan(0.03);
    }

    [Fact]
    public void EncodedRgb24Lossy_DecodesInLibjxl()
    {
        const int w = 64, h = 48;
        (int[] r, int[] g, int[] b) = SmoothRgb(w, h);

        byte[] jxl = JxlImageCodec.EncodeRgb24Lossy(r, g, b, w, h);

        using var img = new MagickImage(jxl); // libjxl decode of our lossy façade output
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        using IPixelCollection<float> px = img.GetPixels();
        int mc = (int)px.Channels;
        float[] v = px.GetValues()!;
        int[][] src = [r, g, b];
        double sumSq = 0;
        for (int i = 0; i < w * h; i++)
            for (int c = 0; c < 3; c++)
            {
                double e = src[c][i] / 255.0 - v[i * mc + c] / 65535.0;
                sumSq += e * e;
            }
        Math.Sqrt(sumSq / (3.0 * w * h)).ShouldBeLessThan(0.05);
    }

    [Fact]
    public void Lossy_Distance_TradesQualityForSize_Monotonically()
    {
        // Larger distance must shrink the file and raise the reconstruction error — the core contract
        // of the quality knob. Use a textured image (a pure gradient is too compressible to separate).
        const int w = 128, h = 128;
        int[][] src = Textured(w, h);

        double[] distances = [0.5, 1.0, 2.0, 4.0, 8.0];
        var sizes = new int[distances.Length];
        var rmses = new double[distances.Length];
        for (int i = 0; i < distances.Length; i++)
        {
            byte[] jxl = JxlImageCodec.EncodeRgb24Lossy(src[0], src[1], src[2], w, h, distances[i]);
            JxlImage img = JxlImageCodec.Decode(jxl);
            sizes[i] = jxl.Length;
            rmses[i] = Rmse8(img.Channels, src, w, h);
        }

        for (int i = 1; i < distances.Length; i++)
        {
            sizes[i].ShouldBeLessThan(sizes[i - 1], $"size at d={distances[i]} should be < d={distances[i - 1]}");
            rmses[i].ShouldBeGreaterThan(rmses[i - 1], $"error at d={distances[i]} should be > d={distances[i - 1]}");
        }
        // A low distance is genuinely high fidelity; a high distance is genuinely lossy.
        rmses[0].ShouldBeLessThan(0.02);
        rmses[^1].ShouldBeGreaterThan(rmses[0] * 2);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(10.0)]
    public void Lossy_AnyDistance_DecodesInLibjxl(double distance)
    {
        const int w = 96, h = 64;
        int[][] src = Textured(w, h);

        byte[] jxl = JxlImageCodec.EncodeRgb24Lossy(src[0], src[1], src[2], w, h, distance);

        using var img = new MagickImage(jxl); // libjxl must accept every quality setting
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
    }

    [Fact]
    public void Lossy_NonPositiveDistance_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            JxlImageCodec.EncodeRgb24Lossy(new int[64], new int[64], new int[64], 8, 8, distance: 0));

    [Fact]
    public void DecodeGrayF32_OnIntegerImage_Throws()
    {
        byte[] integer = JxlImageCodec.EncodeGray8(MakeGray(16, 16, max: 256), 16, 16);
        Should.Throw<InvalidDataException>(() => JxlImageCodec.DecodeGrayF32(integer));
    }

    [Fact]
    public void DecodeRgb24_OnGrayImage_Throws()
    {
        int[] y = MakeGray(16, 16, max: 256);
        byte[] gray = JxlImageCodec.EncodeGray8(y, 16, 16);
        Should.Throw<InvalidDataException>(() => JxlImageCodec.DecodeRgb24(gray));
    }

    private static (int[] R, int[] G, int[] B) MakeRgb(int w, int h, int max)
    {
        var r = new int[w * h];
        var g = new int[w * h];
        var b = new int[w * h];
        uint state = 0xc0ffee11;
        uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        for (int i = 0; i < w * h; i++) { r[i] = (int)(Next() % (uint)max); g[i] = (int)(Next() % (uint)max); b[i] = (int)(Next() % (uint)max); }
        return (r, g, b);
    }

    private static int[] MakeGray(int w, int h, int max)
    {
        var y = new int[w * h];
        uint state = 0x5eed1234;
        uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        for (int i = 0; i < w * h; i++) y[i] = (int)(Next() % (uint)max);
        return y;
    }

    // A smooth gradient — DCT-friendly, so the lossy reconstruction stays within a tight RMSE
    // (random noise would not compress well and isn't representative of lossy use).
    private static (int[] R, int[] G, int[] B) SmoothRgb(int w, int h)
    {
        var r = new int[w * h];
        var g = new int[w * h];
        var b = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = Math.Clamp(x * 255 / w, 0, 255);
                g[i] = Math.Clamp(y * 255 / h, 0, 255);
                b[i] = Math.Clamp((x + y) * 255 / (w + h), 0, 255);
            }
        return (r, g, b);
    }

    private static double Rmse8(int[][] a, int[][] b, int w, int h)
    {
        double sumSq = 0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < w * h; i++)
            {
                double e = (a[c][i] - b[c][i]) / 255.0;
                sumSq += e * e;
            }
        return Math.Sqrt(sumSq / (3.0 * w * h));
    }

    // Gradient + sinusoidal texture: has real high-frequency content, so the quantizer knob actually
    // changes the file size and error (a pure gradient compresses near-perfectly at every distance).
    private static int[][] Textured(int w, int h)
    {
        var ch = new int[3][];
        for (int c = 0; c < 3; c++) ch[c] = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double grad = x * 180.0 / w + y * 40.0 / h;
                double tex = 28 * Math.Sin(x * 0.7) * Math.Cos(y * 0.5) + 14 * Math.Sin((x + y) * 1.1);
                ch[0][i] = Math.Clamp((int)Math.Round(grad + tex), 0, 255);
                ch[1][i] = Math.Clamp((int)Math.Round(grad * 0.7 + tex * 0.6 + 50), 0, 255);
                ch[2][i] = Math.Clamp((int)Math.Round(150 - grad * 0.4 + tex), 0, 255);
            }
        return ch;
    }
}
