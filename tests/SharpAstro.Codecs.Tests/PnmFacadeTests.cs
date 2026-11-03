using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Pnm;
using Shouldly;
using static SharpAstro.Codecs.Tests.PnmFixtures;

namespace SharpAstro.Codecs.Tests;

/// <summary>The facade's Netpbm adapter: the sniff, the format it presents, and "never throws".</summary>
public sealed class PnmFacadeTests
{
    // ---- sniffing ------------------------------------------------------------------------

    [Theory]
    [InlineData("P1\n")]
    [InlineData("P2 ")]
    [InlineData("P3\t")]
    [InlineData("P4\r")]
    [InlineData("P5\n")]
    [InlineData("P6\n")]
    [InlineData("P7\n")]
    [InlineData("P5\v")]
    [InlineData("P5\f")]
    public void TheMagicWithAWhitespaceByte_IsRecognised(string signature)
    {
        var bytes = Ascii(signature);
        ImageCodecs.CanDecode(bytes).ShouldBeTrue();
        PnmImageDecoder.CanDecode(bytes).ShouldBeTrue();
    }

    [Theory]
    [InlineData("P8\n")]
    [InlineData("P0\n")]
    [InlineData("P9 ")]
    [InlineData("Pa\n")]
    [InlineData("P5")]          // no whitespace at all
    [InlineData("P5x")]         // something else after the digit
    [InlineData("P51")]
    [InlineData("P5#")]         // a comment is not the separator the sniff wants
    [InlineData("p5\n")]        // lower case
    [InlineData("P")]
    [InlineData("")]
    [InlineData(" P5\n")]       // leading whitespace is not Netpbm
    [InlineData("PNG")]
    public void NearMisses_AreRefused(string signature)
    {
        var bytes = Ascii(signature);
        PnmImageDecoder.CanDecode(bytes).ShouldBeFalse();
        ImageCodecs.CanDecode(bytes).ShouldBeFalse();
        ImageCodecs.TryDecode(bytes, out var image).ShouldBeFalse();
        image.ShouldBeNull();
    }

    [Fact]
    public void NetpbmDoesNotClaimOtherFormats_AndTheyKeepTheirDecoder()
    {
        PnmImageDecoder.CanDecode([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).ShouldBeFalse();
        PnmImageDecoder.CanDecode([0xFF, 0xD8, 0xFF, 0xE0]).ShouldBeFalse();
        PnmImageDecoder.CanDecode("RIFF\0\0\0\0WEBP"u8).ShouldBeFalse();
        PnmImageDecoder.CanDecode("II*\0"u8).ShouldBeFalse();
    }

    // ---- what it presents ----------------------------------------------------------------

    [Theory]
    [InlineData(1, 255, 1, SampleFormat.UInt8)]
    [InlineData(1, 15, 1, SampleFormat.UInt8)]
    [InlineData(1, 256, 1, SampleFormat.UInt16)]
    [InlineData(1, 65535, 1, SampleFormat.UInt16)]
    [InlineData(2, 255, 2, SampleFormat.UInt8)]
    [InlineData(3, 4095, 3, SampleFormat.UInt16)]
    [InlineData(4, 1, 4, SampleFormat.UInt8)]
    [InlineData(4, 65535, 4, SampleFormat.UInt16)]
    public void ChannelsAndSampleFormat_FollowTheHeader(int depth, int maxval, int channels, SampleFormat format)
    {
        var file = Pam(3, 2, depth, maxval, null, Samples(3 * 2 * depth, maxval));

        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(3, 2, channels, format));

        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        (image!.Width, image.Height, image.Channels, image.SampleFormat).ShouldBe((3, 2, channels, format));
        image.Pixels.Length.ShouldBe(3 * 2 * channels * (format == SampleFormat.UInt8 ? 1 : 2));
        image.IccProfile.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void FullScaleMaxvals_PassThroughUntouched()
    {
        var eight = Samples(5 * 3, 255);
        ImageCodecs.TryDecode(Raw(5, 5, 3, 255, eight), out var a).ShouldBeTrue();
        a!.Pixels.ToArray().ShouldBe(eight.Select(s => (byte)s).ToArray());

        var sixteen = Samples(5 * 3, 65535);
        ImageCodecs.TryDecode(Raw(5, 5, 3, 65535, sixteen), out var b).ShouldBeTrue();
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(b!.Pixels).ToArray().ShouldBe(sixteen);
    }

    [Fact]
    public void ASmallMaxval_IsScaledToFillTheFormat()
    {
        // maxval 15: 0 -> 0, 15 -> 255, 5 -> 85 (255 * 5 / 15 exactly), 1 -> 17.
        ImageCodecs.TryDecode(Raw(5, 4, 1, 15, [0, 15, 5, 1]), out var image).ShouldBeTrue();
        image!.Pixels.ToArray().ShouldBe(new byte[] { 0, 255, 85, 17 });
        image.ToFloats()[4].ShouldBe(1f); // the facade divides by the format's max: maxval is full scale

        // maxval 1000 -> 16-bit: 1000 -> 65535, 500 -> 32768 (32767.5 rounds half up).
        ImageCodecs.TryDecode(Raw(5, 3, 1, 1000, [0, 1000, 500]), out var wide).ShouldBeTrue();
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(wide!.Pixels).ToArray()
            .ShouldBe(new ushort[] { 0, 65535, 32768 });
        wide.ToFloats()[4].ShouldBe(1f);
    }

    [Fact]
    public void AlphaIsKept_AndGreyExpandsAcrossRgb()
    {
        ImageCodecs.TryDecode(Pam(1, 1, 2, 255, "GRAYSCALE_ALPHA", [10, 200]), out var ga).ShouldBeTrue();
        ga!.ToRgba8().ShouldBe(new byte[] { 10, 10, 10, 200 });

        ImageCodecs.TryDecode(Pam(1, 1, 4, 255, "RGB_ALPHA", [1, 2, 3, 4]), out var rgba).ShouldBeTrue();
        rgba!.ToRgba8().ShouldBe(new byte[] { 1, 2, 3, 4 });

        ImageCodecs.TryDecode(Raw(6, 1, 1, 255, [1, 2, 3]), out var rgb).ShouldBeTrue();
        rgb!.ToRgba8().ShouldBe(new byte[] { 1, 2, 3, 255 });
    }

    [Fact]
    public void TryDecodeIntoRgba8_FillsTheCallersBuffer_AndRefusesASmallOne()
    {
        var file = MagickWrite(MagickFormat.Ppm, "RGB", 6, 4, 16, false);
        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        var destination = new byte[info.Width * info.Height * 4];

        ImageCodecs.TryDecodeIntoRgba8(file, destination).ShouldBeTrue();
        using var magick = new MagickImage(file);
        // 16-bit through the display path: >> 8, as the facade does for every 16-bit format.
        var expected = magick.GetPixels().ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8)).ToArray();
        destination.ShouldBe(expected);

        ImageCodecs.TryDecodeIntoRgba8(file, new byte[destination.Length - 1]).ShouldBeFalse();
    }

    // ---- never throws ----------------------------------------------------------------------

    [Fact]
    public void BadDataMakesEveryEntryPointReturnFalse_NeverThrow()
    {
        var bad = new[]
        {
            Ascii("P5\n4 4\n255\n12"),                                    // truncated
            Ascii("P5\n4 4\n255\n"),                                      // empty raster
            Ascii("P6\n2 2\n9\n\x0A\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01"), // sample 10 > 9
            Ascii("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 4\nMAXVAL 255\nTUPLTYPE CMYK\nENDHDR\n\0\0\0\0"), // CMYK
            Ascii("P5\n100000 100000\n255\n\0"),                          // declares 10^10 samples
            Ascii("P2\n2 2\n255\n1 2 three 4"),
            Ascii("P5\n\0\0\0"),
        };
        foreach (var file in bad)
        {
            ImageCodecs.CanDecode(file).ShouldBe(PnmImageDecoder.CanDecode(file));
            ImageCodecs.TryDecode(file, out var image).ShouldBeFalse();
            image.ShouldBeNull();
            ImageCodecs.TryDecodeIntoRgba8(file, new byte[4096]).ShouldBeFalse();
            // The header alone may well be fine (a truncated raster); the info call only needs that.
            ImageCodecs.TryReadInfo(file, out _).ShouldBe(PnmHeaderOk(file));
        }
    }

    private static bool PnmHeaderOk(byte[] file)
    {
        try { PnmReader.ReadHeader(file); return true; }
        catch (InvalidDataException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    [Fact]
    public void EveryPrefixOfAValidFile_IsHandledWithoutThrowing()
    {
        var file = MagickWrite(MagickFormat.Pam, "RGBA", 5, 4, 12, false);
        for (var length = 0; length < file.Length; length++)
        {
            var prefix = file.AsSpan(0, length);
            var decoded = ImageCodecs.TryDecode(prefix, out _);
            decoded.ShouldBeFalse($"a {length}-byte prefix of a {file.Length}-byte file is incomplete");
            ImageCodecs.TryDecodeIntoRgba8(prefix, new byte[5 * 4 * 4]).ShouldBeFalse();
        }

        ImageCodecs.TryDecode(file, out _).ShouldBeTrue();
    }
}
