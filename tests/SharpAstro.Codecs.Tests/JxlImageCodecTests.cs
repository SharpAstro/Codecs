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
}
