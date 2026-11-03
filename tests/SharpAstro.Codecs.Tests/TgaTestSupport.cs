using ImageMagick;
using SharpAstro.Tga;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>Hand-built TGA bytes, and the ImageMagick read every TGA test compares against.</summary>
internal static class TgaTestSupport
{
    /// <summary>The 18-byte header plus image ID, colour map and pixel data, all as the caller says.</summary>
    public static byte[] Build(
        byte type, int width, int height, byte depth, byte descriptor, byte[] pixels,
        byte[]? map = null, int mapFirst = 0, int mapLength = 0, byte mapEntryBits = 0,
        int idLength = 0, byte? mapType = null)
    {
        var l = new List<byte>
        {
            (byte)idLength,
            mapType ?? (byte)(mapLength > 0 || map is { Length: > 0 } ? 1 : 0),
            type,
            (byte)mapFirst, (byte)(mapFirst >> 8),
            (byte)mapLength, (byte)(mapLength >> 8),
            mapEntryBits,
            0, 0, 0, 0,
            (byte)width, (byte)(width >> 8),
            (byte)height, (byte)(height >> 8),
            depth,
            descriptor,
        };
        for (int i = 0; i < idLength; i++)
        {
            l.Add((byte)(0xA0 + i));
        }

        if (map is not null)
        {
            l.AddRange(map);
        }

        l.AddRange(pixels);
        return [.. l];
    }

    /// <summary>Run-length packet: a repeat of one pixel (<paramref name="pixel"/> is its bytes).</summary>
    public static byte[] Run(int count, params byte[] pixel) => [(byte)(0x80 | (count - 1)), .. pixel];

    /// <summary>Run-length packet: <paramref name="count"/> raw pixels, concatenated in <paramref name="pixels"/>.</summary>
    public static byte[] Raw(int count, params byte[] pixels) => [(byte)(count - 1), .. pixels];

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>ImageMagick's read of a TGA, as top-down RGBA8.</summary>
    public static (int Width, int Height, byte[] Rgba) MagickRead(byte[] tga)
    {
        // TGA has no magic number, so ImageMagick has to be told the format.
        using var image = new MagickImage(tga, new MagickReadSettings { Format = MagickFormat.Tga });
        return ((int)image.Width, (int)image.Height, image.GetPixels().ToByteArray(PixelMapping.RGBA)!);
    }

    /// <summary>Decodes with <see cref="TgaReader"/> and demands every byte equal ImageMagick's read of the same file.</summary>
    public static TgaImage AssertSameAsMagick(byte[] tga, string what)
    {
        TgaImage ours = TgaReader.Decode(tga);
        (int w, int h, byte[] expected) = MagickRead(tga);
        ours.Width.ShouldBe(w, what);
        ours.Height.ShouldBe(h, what);
        ours.Rgba.Length.ShouldBe(expected.Length, what);
        for (int i = 0; i < expected.Length; i++)
        {
            if (ours.Rgba[i] != expected[i])
            {
                int px = i / 4;
                Assert.Fail(
                    $"{what}: pixel ({px % w},{px / w}) channel {"RGBA"[i % 4]}: " +
                    $"TgaReader {ours.Rgba[i]}, Magick.NET {expected[i]}");
            }
        }

        return ours;
    }

    /// <summary>A 13x7 raster of RGBA with gradients, flat runs (so RLE has run packets) and noise (raw packets).</summary>
    public static byte[] SourceRgba(int w, int h, bool alpha)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(54);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (y % 3 == 0)
                {
                    // flat stretch
                    rgba[o] = 200; rgba[o + 1] = 30; rgba[o + 2] = 90; rgba[o + 3] = 255;
                }
                else if (y % 3 == 1)
                {
                    rgba[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                    rgba[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                    rgba[o + 2] = (byte)(255 - x * 20);
                    rgba[o + 3] = alpha ? (byte)(255 - x * 12) : (byte)255;
                }
                else
                {
                    rgba[o] = (byte)rng.Next(256);
                    rgba[o + 1] = (byte)rng.Next(256);
                    rgba[o + 2] = (byte)rng.Next(256);
                    rgba[o + 3] = alpha ? (byte)rng.Next(256) : (byte)255;
                }
            }
        }

        return rgba;
    }
}
