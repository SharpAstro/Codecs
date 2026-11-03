using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="PngImageDecoder.TryDecodeRgba8"/>: PNG to an RGBA8 array the caller owns, matching
/// <see cref="PngImageDecoder.TryDecodeIntoRgba8"/> pixel for pixel.
/// </summary>
public sealed class PngTryDecodeRgba8Tests
{
    [Fact]
    public void Rgba8_ComesBackAsIs()
    {
        const int w = 7, h = 5;
        var src = new byte[w * h * 4];
        new Random(0x5EED).NextBytes(src);

        PngImageDecoder.TryDecodeRgba8(PngWriter.Encode(src, w, h), out var rgba, out var width, out var height).ShouldBeTrue();

        width.ShouldBe(w);
        height.ShouldBe(h);
        rgba.ShouldBe(src);
    }

    [Fact]
    public void Rgb8_ExpandsWithOpaqueAlpha()
    {
        const int w = 3, h = 2;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 11);

        PngImageDecoder.TryDecodeRgba8(PngWriter.EncodeRgb8(rgb, w, h), out var rgba, out _, out _).ShouldBeTrue();

        rgba.Length.ShouldBe(w * h * 4);
        for (var p = 0; p < w * h; p++)
        {
            rgba[p * 4 + 0].ShouldBe(rgb[p * 3 + 0]);
            rgba[p * 4 + 1].ShouldBe(rgb[p * 3 + 1]);
            rgba[p * 4 + 2].ShouldBe(rgb[p * 3 + 2]);
            rgba[p * 4 + 3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void Gray16_MatchesTryDecodeIntoRgba8()
    {
        const int w = 4, h = 4;
        var gray = new ushort[w * h];
        for (var i = 0; i < gray.Length; i++) gray[i] = (ushort)(i * 4099);
        var png = PngWriter.EncodeGray16(gray, w, h);

        var into = new byte[w * h * 4];
        PngImageDecoder.TryDecodeIntoRgba8(png, into).ShouldBeTrue();
        PngImageDecoder.TryDecodeRgba8(png, out var rgba, out _, out _).ShouldBeTrue();

        rgba.ShouldBe(into);
    }

    [Fact]
    public void NotAPng_IsFalse()
    {
        PngImageDecoder.TryDecodeRgba8("not a png at all"u8, out var rgba, out var width, out var height).ShouldBeFalse();

        rgba.ShouldBeNull();
        width.ShouldBe(0);
        height.ShouldBe(0);
    }

    [Fact]
    public void Adam7_DecodesThroughEveryEntryPointAsTheNonInterlacedFileDoes()
    {
        // Adam7 used to be refused: Decode threw NotSupportedException and every Try entry point said false.
        // All four now succeed and agree with the non-interlaced encoding of the same raster.
        const int w = 16, h = 16;
        var rgb = new byte[w * h * 3];
        new Random(7).NextBytes(rgb);
        using var img = new MagickImage();
        img.ReadPixels(rgb, new PixelReadSettings(w, h, StorageType.Char, "RGB"));
        // 256 pixels of random colour would otherwise be written as a palette (at most 256 colours): force RGB.
        img.Settings.SetDefine(MagickFormat.Png, "color-type", "2");
        img.Settings.Interlace = Interlace.Png;
        var png = img.ToByteArray(MagickFormat.Png);
        img.Settings.Interlace = Interlace.NoInterlace;
        var plain = img.ToByteArray(MagickFormat.Png);
        // IHDR's depth, colour type and interlace method, so this is an 8-bit RGB Adam7 file and the other is not.
        (png[24], png[25], png[28], plain[28]).ShouldBe(((byte)8, (byte)2, (byte)1, (byte)0));

        PngImageDecoder.TryDecodeRgba8(plain, out var expectedRgba, out _, out _).ShouldBeTrue();
        var expected = PngReader.Decode(plain);

        var decoded = PngReader.Decode(png);
        (decoded.Width, decoded.Height, decoded.BitDepth, decoded.ColorType).ShouldBe((w, h, 8, 2));
        decoded.Pixels.ShouldBe(expected.Pixels);

        PngReader.TryDecode(png, out var tried).ShouldBeTrue();
        tried.ShouldNotBeNull().Pixels.ShouldBe(expected.Pixels);

        PngImageDecoder.TryDecodeRgba8(png, out var rgba, out var width, out var height).ShouldBeTrue();
        (width, height).ShouldBe((w, h));
        rgba.ShouldBe(expectedRgba);

        var into = new byte[w * h * 4];
        PngImageDecoder.TryDecodeIntoRgba8(png, into).ShouldBeTrue();
        into.ShouldBe(expectedRgba);
        // And the pixels are the ones that went in, not merely the same as another decode of them.
        for (var p = 0; p < w * h; p++)
        {
            (into[p * 4], into[p * 4 + 1], into[p * 4 + 2], into[p * 4 + 3]).ShouldBe((rgb[p * 3], rgb[p * 3 + 1], rgb[p * 3 + 2], (byte)255));
        }
    }

    [Fact]
    public void PqTagged_IsRefused_ButPngReaderTryDecodeKeepsIt()
    {
        var png = PngWriter.Encode(new byte[2 * 2 * 4], 2, 2, new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq });

        PngImageDecoder.TryDecodeRgba8(png, out var rgba, out _, out _).ShouldBeFalse();
        rgba.ShouldBeNull();

        // The refusal belongs to the RGBA8 projection, not the reader: a float projection starts here.
        PngReader.TryDecode(png, out var decoded).ShouldBeTrue();
        decoded.Cicp.ShouldNotBeNull().TransferFunction.ShouldBe(TransferFunction.Pq);
    }
}
