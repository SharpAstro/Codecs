using System.Text;
using SharpAstro.Tga;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// TgaWriter output must read back to exactly the input through Magick.NET and through TgaReader, for every
/// depth x compression x origin, on images chosen to hit the RLE packet limits.
/// </summary>
public sealed class TgaWriterTests
{
    public enum Shape { Flat, Gradient, Noise, Alternating, TwoColourRuns }

    // 129 and 257 are one past a full run packet; 128 is exactly one; 1 and 2 are the packet-length minimums.
    public static TheoryData<int, int, Shape, bool, bool, bool> Cases()
    {
        var data = new TheoryData<int, int, Shape, bool, bool, bool>();
        foreach ((int w, int h) in new[] { (1, 1), (2, 3), (13, 7), (127, 2), (128, 2), (129, 3), (257, 2), (300, 5) })
        {
            foreach (Shape shape in Enum.GetValues<Shape>())
            {
                foreach (bool alpha in new[] { false, true })
                {
                    foreach (bool rle in new[] { false, true })
                    {
                        foreach (bool top in new[] { false, true })
                        {
                            data.Add(w, h, shape, alpha, rle, top);
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReadsBackExactly_ThroughMagickAndTgaReader(int w, int h, Shape shape, bool alpha, bool rle, bool top)
    {
        byte[] rgba = Make(w, h, shape, alpha);
        var options = new TgaWriteOptions { Rle = rle, TopOrigin = top };
        byte[] tga = alpha
            ? TgaWriter.EncodeRgba8(rgba, w, h, options)
            : TgaWriter.EncodeRgb8(ToRgb(rgba), w, h, options);
        string what = $"{w}x{h} {shape} alpha={alpha} rle={rle} top={top}";

        TgaInfo info = TgaReader.ReadInfo(tga);
        info.Width.ShouldBe(w, what);
        info.Height.ShouldBe(h, what);
        info.BitsPerPixel.ShouldBe(alpha ? 32 : 24, what);
        info.ImageType.ShouldBe(rle ? TgaImageType.RleTrueColor : TgaImageType.TrueColor, what);
        info.TopOrigin.ShouldBe(top, what);
        info.HasAlpha.ShouldBe(alpha, what);
        tga[17].ShouldBe((byte)((alpha ? 8 : 0) | (top ? 0x20 : 0)), what);

        // Ours: equal to the input.
        TgaImage ours = TgaReader.Decode(tga);
        ours.Rgba.ShouldBe(rgba, what);

        // Magick's: equal to the input, too (the oracle, not just our own reader).
        (int mw, int mh, byte[] magick) = TgaTestSupport.MagickRead(tga);
        mw.ShouldBe(w, what);
        mh.ShouldBe(h, what);
        magick.ShouldBe(rgba, what);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryFileEndsWithTheTga2Footer(bool rle)
    {
        byte[] tga = TgaWriter.EncodeRgb8(new byte[3 * 4 * 4], 4, 4, new TgaWriteOptions { Rle = rle });
        tga.Length.ShouldBeGreaterThan(26);
        byte[] footer = tga[^26..];
        footer[..8].ShouldBe(new byte[8]); // no extension area, no developer area
        Encoding.ASCII.GetString(footer, 8, 17).ShouldBe("TRUEVISION-XFILE.");
        footer[25].ShouldBe((byte)0);
    }

    [Fact]
    public void RleOfAFlatImage_IsMuchSmallerThanRaw()
    {
        const int w = 256, h = 256;
        byte[] rgba = Make(w, h, Shape.Flat, alpha: true);
        int raw = TgaWriter.EncodeRgba8(rgba, w, h).Length;
        int rle = TgaWriter.EncodeRgba8(rgba, w, h, new TgaWriteOptions { Rle = true }).Length;
        rle.ShouldBeLessThan(raw / 20);

        byte[] rgb = ToRgb(rgba);
        TgaWriter.EncodeRgb8(rgb, w, h, new TgaWriteOptions { Rle = true }).Length
            .ShouldBeLessThan(TgaWriter.EncodeRgb8(rgb, w, h).Length / 20);
    }

    [Fact]
    public void RleOfNoise_CostsOneHeaderBytePer128PixelsOfARow()
    {
        const int w = 300, h = 4;
        byte[] rgba = Make(w, h, Shape.Noise, alpha: true);
        int raw = TgaWriter.EncodeRgba8(rgba, w, h).Length;
        int rle = TgaWriter.EncodeRgba8(rgba, w, h, new TgaWriteOptions { Rle = true }).Length;
        // Noise has (all but certainly) no equal neighbours: every row is raw packets of 128, 128 and 44.
        rle.ShouldBe(raw + h * 3);
    }

    [Fact]
    public void RlePackets_NeverCrossARow_AndUseTheFullRangeOfLengths()
    {
        // Row 0 ends in a colour that row 1 starts with, so a writer free to cross rows would make one packet of it.
        const int w = 129, h = 4;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                // Rows 0 and 1: one colour throughout (a run of 129 = 128 + 1 per row, and equal across the boundary).
                // Row 2: all different (raw 128 + raw 1). Row 3: one odd pixel then a run of 128.
                byte v = y < 2 ? (byte)77 : y == 2 ? (byte)(x * 2) : (x == 0 ? (byte)5 : (byte)77);
                rgb[o] = v; rgb[o + 1] = (byte)(v ^ 0x55); rgb[o + 2] = (byte)(v + 1);
            }
        }

        byte[] tga = TgaWriter.EncodeRgb8(rgb, w, h, new TgaWriteOptions { Rle = true });
        List<(bool Run, int Count, int Row)> packets = Packets(tga, w, h, bpp: 3);

        packets.Where(p => p.Row == 0).Select(p => (p.Run, p.Count)).ShouldBe([(true, 128), (false, 1)]);
        packets.Where(p => p.Row == 1).Select(p => (p.Run, p.Count)).ShouldBe([(true, 128), (false, 1)]);
        packets.Where(p => p.Row == 2).Select(p => (p.Run, p.Count)).ShouldBe([(false, 128), (false, 1)]);
        packets.Where(p => p.Row == 3).Select(p => (p.Run, p.Count)).ShouldBe([(false, 1), (true, 128)]);

        TgaReader.Decode(tga).Rgba.ShouldBe(ToRgba(rgb));
        TgaTestSupport.MagickRead(tga).Rgba.ShouldBe(ToRgba(rgb));
    }

    [Theory]
    [InlineData(Shape.Flat)]
    [InlineData(Shape.Gradient)]
    [InlineData(Shape.Noise)]
    [InlineData(Shape.Alternating)]
    [InlineData(Shape.TwoColourRuns)]
    public void RlePackets_AreWithinRowsAndOfLegalLength(Shape shape)
    {
        foreach (int w in new[] { 1, 2, 3, 127, 128, 129, 255, 256, 257, 300 })
        {
            const int h = 4;
            byte[] tga = TgaWriter.EncodeRgba8(Make(w, h, shape, alpha: true), w, h, new TgaWriteOptions { Rle = true });
            // Packets() walks the stream and fails if a packet is longer than 128 or leaves its row.
            int total = Packets(tga, w, h, bpp: 4).Sum(p => p.Count);
            total.ShouldBe(w * h);
        }
    }

    [Fact]
    public void TopAndBottomOrigin_DifferOnlyInRowOrder_AndDecodeTheSame()
    {
        byte[] rgb = ToRgb(Make(5, 4, Shape.Gradient, alpha: false));
        byte[] top = TgaWriter.EncodeRgb8(rgb, 5, 4, new TgaWriteOptions { TopOrigin = true });
        byte[] bottom = TgaWriter.EncodeRgb8(rgb, 5, 4, new TgaWriteOptions { TopOrigin = false });
        TgaReader.Decode(top).Rgba.ShouldBe(TgaReader.Decode(bottom).Rgba);
        // The first stored row of the bottom-origin file is the image's last row.
        bottom[18..(18 + 15)].ShouldBe(top[(18 + 15 * 3)..(18 + 15 * 4)]);
    }

    [Fact]
    public void DefaultsAreRawAndTopOrigin()
    {
        byte[] tga = TgaWriter.EncodeRgb8(new byte[3], 1, 1);
        TgaInfo info = TgaReader.ReadInfo(tga);
        info.ImageType.ShouldBe(TgaImageType.TrueColor);
        info.TopOrigin.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(65536, 1, 65536 * 3)]
    [InlineData(1, 65536, 65536 * 3)]
    [InlineData(2, 2, 11)] // too short
    [InlineData(2, 2, 13)] // too long
    public void BadDimensionsOrLength_ThrowArgumentException_ForRgbAndRgba(int w, int h, int length)
    {
        Should.Throw<ArgumentException>(() => TgaWriter.EncodeRgb8(new byte[length], w, h));
        Should.Throw<ArgumentException>(() => TgaWriter.EncodeRgba8(new byte[length], w, h));
    }

    [Fact]
    public void ARgbaBufferForAnRgbCall_IsRefusedNotMisread()
    {
        Should.Throw<ArgumentException>(() => TgaWriter.EncodeRgb8(new byte[2 * 2 * 4], 2, 2));
        Should.Throw<ArgumentException>(() => TgaWriter.EncodeRgba8(new byte[2 * 2 * 3], 2, 2));
    }

    // ---- helpers ----

    /// <summary>Walks the RLE data of a file this writer made, failing on a packet that is too long or crosses a row.</summary>
    private static List<(bool Run, int Count, int Row)> Packets(byte[] tga, int w, int h, int bpp)
    {
        var packets = new List<(bool, int, int)>();
        int pos = 18;
        for (int row = 0; row < h; row++)
        {
            int inRow = 0;
            while (inRow < w)
            {
                bool run = (tga[pos] & 0x80) != 0;
                int count = (tga[pos] & 0x7F) + 1;
                pos += 1 + (run ? bpp : count * bpp);
                (inRow + count).ShouldBeLessThanOrEqualTo(w, $"a packet of {count} crosses the end of row {row}");
                packets.Add((run, count, row));
                inRow += count;
            }
        }

        (tga.Length - pos).ShouldBe(26, "the rest is the footer");
        return packets;
    }

    private static byte[] Make(int w, int h, Shape shape, bool alpha)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(w * 1000 + h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte r, g, b, a;
                switch (shape)
                {
                    case Shape.Flat:
                        (r, g, b, a) = (200, 30, 90, 255);
                        break;
                    case Shape.Gradient:
                        (r, g, b, a) = ((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)(x + y), (byte)(255 - x));
                        break;
                    case Shape.Noise:
                        (r, g, b, a) = ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                        // Break any accidental equal neighbour so every packet is raw.
                        b = (byte)(x & 1 | b & 0xFE);
                        r = (byte)((r & 0xFE) | ((x >> 1) & 1));
                        break;
                    case Shape.Alternating:
                        (r, g, b, a) = (x % 2 == 0 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)128));
                        break;
                    default:
                        // Runs of 3, 130 and 1 pixels, so run and raw packets alternate and 128 splits a run.
                        int phase = (x + y) % 134;
                        (r, g, b, a) = phase < 3 ? ((byte)10, (byte)20, (byte)30, (byte)255)
                            : phase < 133 ? ((byte)40, (byte)50, (byte)60, (byte)255)
                            : ((byte)70, (byte)80, (byte)90, (byte)255);
                        break;
                }

                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = alpha ? a : (byte)255;
            }
        }

        return rgba;
    }

    private static byte[] ToRgb(byte[] rgba)
    {
        var rgb = new byte[rgba.Length / 4 * 3];
        for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
        {
            rgb[j] = rgba[i];
            rgb[j + 1] = rgba[i + 1];
            rgb[j + 2] = rgba[i + 2];
        }

        return rgb;
    }

    private static byte[] ToRgba(byte[] rgb)
    {
        var rgba = new byte[rgb.Length / 3 * 4];
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            rgba[i] = rgb[j];
            rgba[i + 1] = rgb[j + 1];
            rgba[i + 2] = rgb[j + 2];
            rgba[i + 3] = 255;
        }

        return rgba;
    }
}
