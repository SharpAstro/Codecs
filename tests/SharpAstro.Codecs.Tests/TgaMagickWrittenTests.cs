using ImageMagick;
using SharpAstro.Tga;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// TGAs written by ImageMagick (Magick.NET) must decode to exactly the pixels ImageMagick reads back from them.
/// Each case asserts the header Magick wrote (type, depth, origin bit), so a Magick change that quietly stops
/// producing a variant fails here rather than leaving the variant uncovered.
/// </summary>
public sealed class TgaMagickWrittenTests
{
    public enum Kind { TrueColor, TrueColorAlpha, Gray, Palette }

    public static TheoryData<Kind, bool, bool> Cases()
    {
        var data = new TheoryData<Kind, bool, bool>();
        foreach (Kind kind in Enum.GetValues<Kind>())
        {
            foreach (bool rle in new[] { false, true })
            {
                foreach (bool top in new[] { false, true })
                {
                    data.Add(kind, rle, top);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WrittenByMagick_DecodesToMagicksPixels(Kind kind, bool rle, bool top)
    {
        // 13x7: odd width and height, so row/column mixups and a row that ends mid-packet both show.
        const int w = 13, h = 7;
        byte[] tga = WriteWithMagick(kind, rle, top, w, h);

        TgaInfo info = TgaReader.ReadInfo(tga);
        info.Width.ShouldBe(w);
        info.Height.ShouldBe(h);
        info.IsRle.ShouldBe(rle);
        info.TopOrigin.ShouldBe(top);
        info.RightToLeft.ShouldBeFalse();
        TgaImageType expectedType = kind switch
        {
            Kind.Gray => rle ? TgaImageType.RleGrayscale : TgaImageType.Grayscale,
            Kind.Palette => rle ? TgaImageType.RleColorMapped : TgaImageType.ColorMapped,
            _ => rle ? TgaImageType.RleTrueColor : TgaImageType.TrueColor,
        };
        info.ImageType.ShouldBe(expectedType);
        info.BitsPerPixel.ShouldBe(kind switch { Kind.TrueColor => 24, Kind.TrueColorAlpha => 32, _ => 8 });

        TgaImage image = TgaTestSupport.AssertSameAsMagick(tga, $"{kind} rle={rle} top={top}");
        image.HasAlpha.ShouldBe(kind == Kind.TrueColorAlpha);
        if (kind != Kind.TrueColorAlpha)
        {
            for (int i = 3; i < image.Rgba.Length; i += 4)
            {
                image.Rgba[i].ShouldBe((byte)255);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TrueColorWrittenByMagick_ReproducesTheSource(bool rle, bool top)
    {
        // Lossless kinds only: the source raster itself is the expected output, not just Magick's read.
        const int w = 13, h = 7;
        byte[] source = TgaTestSupport.SourceRgba(w, h, alpha: true);
        byte[] tga = WriteWithMagick(Kind.TrueColorAlpha, rle, top, w, h);
        byte[] decoded = TgaReader.Decode(tga).Rgba;
        if (top)
        {
            decoded.ShouldBe(source);
            return;
        }

        // Magick's writer treats Orientation as a tag only: it writes the rows in memory order and, for a
        // bottom-left file, leaves descriptor bit 5 clear. Read by the spec (and by Magick's own reader, which
        // the other tests compare against) the first stored row is then the BOTTOM row, so the decoded image
        // is the source flipped. The code follows the spec; this pins that the flip is exactly a row reversal.
        int stride = w * 4;
        for (int y = 0; y < h; y++)
        {
            decoded.AsSpan(y * stride, stride).ToArray()
                .ShouldBe(source.AsSpan((h - 1 - y) * stride, stride).ToArray(), $"row {y}");
        }
    }

    [Fact]
    public void ABigImageWithLongRuns_MatchesMagick()
    {
        // 300 wide: a flat row is longer than the 128 pixels one RLE packet holds, so it takes several.
        const int w = 300, h = 5;
        using var image = new MagickImage(new MagickColor(10000, 20000, 30000), w, h);
        image.Alpha(AlphaOption.Off);
        image.Settings.Compression = CompressionMethod.RLE;
        using var ms = new MemoryStream();
        image.Write(ms, MagickFormat.Tga);
        byte[] tga = ms.ToArray();
        TgaReader.ReadInfo(tga).IsRle.ShouldBeTrue();
        TgaTestSupport.AssertSameAsMagick(tga, "300x5 flat RLE");
    }

    private static byte[] WriteWithMagick(Kind kind, bool rle, bool top, int w, int h)
    {
        byte[] source = TgaTestSupport.SourceRgba(w, h, alpha: kind == Kind.TrueColorAlpha);
        using var image = new MagickImage(source, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));
        switch (kind)
        {
            case Kind.TrueColor:
                image.Alpha(AlphaOption.Off);
                break;
            case Kind.Gray:
                image.Alpha(AlphaOption.Off);
                image.ColorType = ColorType.Grayscale;
                break;
            case Kind.Palette:
                image.Alpha(AlphaOption.Off);
                image.Quantize(new QuantizeSettings { Colors = 16, DitherMethod = DitherMethod.No });
                image.ColorType = ColorType.Palette;
                break;
        }

        image.Orientation = top ? OrientationType.TopLeft : OrientationType.BottomLeft;
        image.Settings.Compression = rle ? CompressionMethod.RLE : CompressionMethod.NoCompression;
        using var ms = new MemoryStream();
        image.Write(ms, MagickFormat.Tga);
        return ms.ToArray();
    }
}
