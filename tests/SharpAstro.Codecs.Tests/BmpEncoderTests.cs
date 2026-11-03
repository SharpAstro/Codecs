using ImageMagick;
using SharpAstro.Bmp;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="BmpEncoder"/>: what ImageMagick reads from our bytes must equal the input exactly (alpha
/// included, odd widths included), our own decoder must round-trip it, and the layout (header sizes,
/// bottom-up, 4-byte row padding, masks) is pinned byte by byte so a reader that is stricter than Magick
/// has nothing to object to.
/// </summary>
public sealed class BmpEncoderTests
{
    private static byte[] MakeRaster(int w, int h, int channels)
    {
        var px = new byte[w * h * channels];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * channels;
                px[i] = (byte)(x * 37 + y * 11 + 5);
                px[i + 1] = (byte)(x * 13 + y * 53 + 100);
                px[i + 2] = (byte)(x * 91 + y * 7 + 200);
                if (channels == 4) px[i + 3] = (byte)(x * 29 + y * 61); // every alpha value class, 0 and 255 included
            }
        }

        // Make sure the extremes are present whatever the width.
        if (channels == 4)
        {
            px[3] = 0;
            px[^1] = 255;
        }

        return px;
    }

    private static (int Width, int Height, int Channels, byte[] Pixels) MagickRead(byte[] file)
    {
        using var img = new MagickImage(file);
        bool alpha = img.HasAlpha;
        using var pixels = img.GetPixels();
        var bytes = pixels.ToByteArray(0, 0, img.Width, img.Height, alpha ? PixelMapping.RGBA : PixelMapping.RGB)!;
        return ((int)img.Width, (int)img.Height, alpha ? 4 : 3, bytes);
    }

    public static TheoryData<int, int, int> Cases()
    {
        var data = new TheoryData<int, int, int>();
        foreach (int channels in new[] { 3, 4 })
            foreach (int w in new[] { 1, 2, 3, 4, 5, 7, 17, 33 })
                data.Add(w, w == 1 ? 1 : 9, channels);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MagickReadsOurOutput_Exactly(int w, int h, int channels)
    {
        var raster = MakeRaster(w, h, channels);
        var file = channels == 4
            ? BmpEncoder.EncodeRgba32(raster, w, h)
            : BmpEncoder.EncodeRgb24(raster, w, h);

        var (mw, mh, mch, mpx) = MagickRead(file);
        mw.ShouldBe(w);
        mh.ShouldBe(h);
        mch.ShouldBe(channels, "alpha must survive in Magick's reading of the 32-bit file, and a 24-bit file has none");
        mpx.ShouldBe(raster);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OurDecoderRoundTrips_Exactly(int w, int h, int channels)
    {
        var raster = MakeRaster(w, h, channels);
        var file = channels == 4
            ? BmpEncoder.EncodeRgba32(raster, w, h)
            : BmpEncoder.EncodeRgb24(raster, w, h);

        var decoded = BmpDecoder.Decode(file);
        decoded.Width.ShouldBe(w);
        decoded.Height.ShouldBe(h);
        decoded.Channels.ShouldBe(channels);
        decoded.Pixels.ShouldBe(raster);

        var info = BmpDecoder.ReadInfo(file);
        info.TopDown.ShouldBeFalse();
        info.BitsPerPixel.ShouldBe(channels == 4 ? 32 : 24);
    }

    [Fact]
    public void Rgb24_Layout_Is40ByteHeader_BottomUp_RowsPaddedToFour()
    {
        // 3 x 2: a 9-byte row pads to 12.
        byte[] rgb =
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9,        // top row
            11, 12, 13, 14, 15, 16, 17, 18, 19, // bottom row
        ];
        var file = BmpEncoder.EncodeRgb24(rgb, 3, 2);

        file.Length.ShouldBe(14 + 40 + 24);
        file[0].ShouldBe((byte)'B');
        file[1].ShouldBe((byte)'M');
        BitConverter.ToUInt32(file, 2).ShouldBe((uint)file.Length);
        BitConverter.ToUInt32(file, 10).ShouldBe(54u);
        BitConverter.ToUInt32(file, 14).ShouldBe(40u);
        BitConverter.ToInt32(file, 18).ShouldBe(3);
        BitConverter.ToInt32(file, 22).ShouldBe(2, "a positive height means bottom-up");
        BitConverter.ToUInt16(file, 26).ShouldBe((ushort)1);
        BitConverter.ToUInt16(file, 28).ShouldBe((ushort)24);
        BitConverter.ToUInt32(file, 30).ShouldBe(0u, "BI_RGB");

        // First stored row is the BOTTOM row, in BGR order, then three zero bytes of padding.
        file[54..66].ShouldBe(new byte[] { 13, 12, 11, 16, 15, 14, 19, 18, 17, 0, 0, 0 });
        file[66..78].ShouldBe(new byte[] { 3, 2, 1, 6, 5, 4, 9, 8, 7, 0, 0, 0 });
    }

    [Fact]
    public void Rgba32_Layout_IsV4Header_WithBitfieldsAndAlphaMask()
    {
        byte[] rgba = [10, 20, 30, 40, 50, 60, 70, 80];  // 2 x 1
        var file = BmpEncoder.EncodeRgba32(rgba, 2, 1);

        file.Length.ShouldBe(14 + 108 + 8);
        BitConverter.ToUInt32(file, 10).ShouldBe(122u);
        BitConverter.ToUInt32(file, 14).ShouldBe(108u);
        BitConverter.ToUInt16(file, 28).ShouldBe((ushort)32);
        BitConverter.ToUInt32(file, 30).ShouldBe(3u, "BI_BITFIELDS");
        BitConverter.ToUInt32(file, 54).ShouldBe(0x00FF0000u);
        BitConverter.ToUInt32(file, 58).ShouldBe(0x0000FF00u);
        BitConverter.ToUInt32(file, 62).ShouldBe(0x000000FFu);
        BitConverter.ToUInt32(file, 66).ShouldBe(0xFF000000u);
        file[122..130].ShouldBe(new byte[] { 30, 20, 10, 40, 70, 60, 50, 80 });
    }

    [Fact]
    public void Stride_SkipsInputPadding()
    {
        // 2 x 2 RGB with 8 bytes per input row (2 bytes of junk after each 6-byte row).
        byte[] padded = [1, 2, 3, 4, 5, 6, 99, 99, 7, 8, 9, 10, 11, 12];
        var file = BmpEncoder.EncodeRgb24(padded, 2, 2, stride: 8);
        BmpDecoder.Decode(file).Pixels.ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });

        byte[] paddedRgba = [1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 99, 99, 9, 10, 11, 12, 13, 14, 15, 16];
        var file4 = BmpEncoder.EncodeRgba32(paddedRgba, 2, 2, stride: 12);
        BmpDecoder.Decode(file4).Pixels.ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
    }

    [Fact]
    public void BadArguments_AreRefusedBeforeAnything_IsWritten()
    {
        var rgb = new byte[12];
        Should.Throw<ArgumentOutOfRangeException>(() => BmpEncoder.EncodeRgb24(rgb, 0, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => BmpEncoder.EncodeRgb24(rgb, 2, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => BmpEncoder.EncodeRgb24(rgb, -1, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => BmpEncoder.EncodeRgb24(rgb, 2, 2, stride: 5), "stride below one row");
        Should.Throw<ArgumentOutOfRangeException>(() => BmpEncoder.EncodeRgb24(rgb, 2, 2, stride: -6));
        Should.Throw<ArgumentException>(() => BmpEncoder.EncodeRgb24(new byte[11], 2, 2), "one byte short");
        Should.Throw<ArgumentException>(() => BmpEncoder.EncodeRgb24(rgb, 2, 2, stride: 8), "the stride needs 14 bytes");
        Should.Throw<ArgumentException>(() => BmpEncoder.EncodeRgba32(new byte[15], 2, 2));
        Should.Throw<ArgumentException>(() => BmpEncoder.EncodeRgb24(rgb, 1 << 20, 1 << 20), "over the pixel limit, and refused before any allocation");
        Should.Throw<ArgumentException>(() => BmpEncoder.EncodeRgba32(rgb, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void LastRowNeedsNoStridePadding()
    {
        // 2 x 2 with stride 8: the input is only 14 bytes, the last row's padding is not required.
        BmpEncoder.EncodeRgb24(new byte[14], 2, 2, stride: 8).Length.ShouldBe(14 + 40 + 16);
    }
}
