using SharpAstro.Bmp;
using SharpAstro.Codecs.Abstractions;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>BMP through the <see cref="ImageCodecs"/> facade: sniff, info, decode, RGBA8, and refusals.</summary>
public sealed class BmpFacadeTests
{
    private static readonly byte[] Rgb = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180]; // 3 x 2

    private static readonly byte[] Rgba = [10, 20, 30, 0, 40, 50, 60, 128, 70, 80, 90, 255, 100, 110, 120, 7]; // 2 x 2

    [Fact]
    public void Sniffs_Bm()
    {
        ImageCodecs.CanDecode(BmpEncoder.EncodeRgb24(Rgb, 3, 2)).ShouldBeTrue();
        ImageCodecs.CanDecode(BmpEncoder.EncodeRgba32(Rgba, 2, 2)).ShouldBeTrue();
        BmpImageDecoder.CanDecode(BmpEncoder.EncodeRgb24(Rgb, 3, 2)).ShouldBeTrue();
        BmpImageDecoder.SignatureLength.ShouldBe(26);
    }

    [Fact]
    public void DoesNotClaim_OtherFormats_OrTwoLetterCoincidences()
    {
        BmpImageDecoder.CanDecode("BM"u8).ShouldBeFalse();
        BmpImageDecoder.CanDecode(new byte[64]).ShouldBeFalse();

        // "BM" followed by text: not a BMP, and nothing else wants it either.
        byte[] text = "BMW is a car maker and this is plain text, not an image."u8.ToArray();
        BmpImageDecoder.CanDecode(text).ShouldBeFalse();
        ImageCodecs.CanDecode(text).ShouldBeFalse();
        ImageCodecs.TryDecode(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void ReadsInfo()
    {
        ImageCodecs.TryReadInfo(BmpEncoder.EncodeRgb24(Rgb, 3, 2), out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(3, 2, 3, SampleFormat.UInt8));

        ImageCodecs.TryReadInfo(BmpEncoder.EncodeRgba32(Rgba, 2, 2), out info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(2, 2, 4, SampleFormat.UInt8));
    }

    [Fact]
    public void Decodes_Rgb8_AndRgba8()
    {
        ImageCodecs.TryDecode(BmpEncoder.EncodeRgb24(Rgb, 3, 2), out var image).ShouldBeTrue();
        image!.Width.ShouldBe(3);
        image.Height.ShouldBe(2);
        image.Channels.ShouldBe(3);
        image.SampleFormat.ShouldBe(SampleFormat.UInt8);
        image.Pixels.ToArray().ShouldBe(Rgb);

        ImageCodecs.TryDecode(BmpEncoder.EncodeRgba32(Rgba, 2, 2), out image).ShouldBeTrue();
        image!.Channels.ShouldBe(4);
        image.Pixels.ToArray().ShouldBe(Rgba);
    }

    [Fact]
    public void PassesTheV5IccProfileThrough()
    {
        byte[] profile = new byte[128];
        for (int i = 0; i < profile.Length; i++) profile[i] = (byte)(i + 1);
        var file = new BmpDib
        {
            HeaderSize = 124,
            Width = 1,
            Height = 1,
            BitsPerPixel = 24,
            Pixels = [1, 2, 3, 0],
            IccProfile = profile,
            ColorSpaceType = 0x4D424544,
        }.Build();

        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        image!.IccProfile.ToArray().ShouldBe(profile);
    }

    [Fact]
    public void DecodesIntoRgba8_OpaqueGetsAlpha255()
    {
        var rgba = new byte[3 * 2 * 4];
        ImageCodecs.TryDecodeIntoRgba8(BmpEncoder.EncodeRgb24(Rgb, 3, 2), rgba).ShouldBeTrue();
        for (int p = 0; p < 6; p++)
        {
            rgba[p * 4].ShouldBe(Rgb[p * 3]);
            rgba[p * 4 + 1].ShouldBe(Rgb[p * 3 + 1]);
            rgba[p * 4 + 2].ShouldBe(Rgb[p * 3 + 2]);
            rgba[p * 4 + 3].ShouldBe((byte)255);
        }

        var withAlpha = new byte[16];
        ImageCodecs.TryDecodeIntoRgba8(BmpEncoder.EncodeRgba32(Rgba, 2, 2), withAlpha).ShouldBeTrue();
        withAlpha.ShouldBe(Rgba);
    }

    [Fact]
    public void RgbaDestinationThatIsTooSmall_AnswersFalse()
    {
        ImageCodecs.TryDecodeIntoRgba8(BmpEncoder.EncodeRgb24(Rgb, 3, 2), new byte[23]).ShouldBeFalse();
    }

    [Fact]
    public void BiPng_AnswersFalse_NotThrows()
    {
        var file = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 0, Compression = 5, Pixels = new byte[16] }.Build();
        ImageCodecs.CanDecode(file).ShouldBeTrue("the header is a BMP header");
        ImageCodecs.TryReadInfo(file, out _).ShouldBeFalse();
        ImageCodecs.TryDecode(file, out var image).ShouldBeFalse();
        image.ShouldBeNull();
        ImageCodecs.TryDecodeIntoRgba8(file, new byte[16]).ShouldBeFalse();
    }

    [Fact]
    public void TruncatedFile_AnswersFalse()
    {
        var file = BmpEncoder.EncodeRgb24(Rgb, 3, 2);
        var cut = file.AsSpan(0, file.Length - 5);
        ImageCodecs.TryDecode(cut, out _).ShouldBeFalse();
        ImageCodecs.TryDecodeIntoRgba8(cut, new byte[24]).ShouldBeFalse();
    }
}
