using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Tga;
using Shouldly;
using static SharpAstro.Codecs.Tests.TgaTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// TGA through the <see cref="ImageCodecs"/> facade. A TGA has no magic number, so the facade recognises one by
/// its TGA 2.0 footer only: footered files decode exactly as <see cref="TgaReader"/> does, footerless ones and
/// arbitrary bytes are not recognised, and a footered file that is corrupt returns false rather than throwing.
/// </summary>
public sealed class TgaFacadeTests
{
    private static readonly byte[] Footer =
        [0, 0, 0, 0, 0, 0, 0, 0, .. "TRUEVISION-XFILE."u8, 0];

    private static byte[] WithFooter(byte[] tga) => [.. tga, .. Footer];

    private static byte[] WriteWithMagick(bool alpha, bool rle)
    {
        const int w = 13, h = 7;
        byte[] source = SourceRgba(w, h, alpha);
        using var image = new MagickImage(source, new PixelReadSettings(w, h, StorageType.Char, PixelMapping.RGBA));
        if (!alpha)
        {
            image.Alpha(AlphaOption.Off);
        }

        image.Orientation = OrientationType.TopLeft;
        image.Settings.Compression = rle ? CompressionMethod.RLE : CompressionMethod.NoCompression;
        using var ms = new MemoryStream();
        image.Write(ms, MagickFormat.Tga);
        return ms.ToArray();
    }

    private static byte[] OurFile(bool alpha, bool rle)
    {
        const int w = 13, h = 7;
        byte[] rgba = SourceRgba(w, h, alpha);
        var options = new TgaWriteOptions { Rle = rle };
        if (alpha)
        {
            return TgaWriter.EncodeRgba8(rgba, w, h, options);
        }

        byte[] rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = rgba[i * 4];
            rgb[i * 3 + 1] = rgba[i * 4 + 1];
            rgb[i * 3 + 2] = rgba[i * 4 + 2];
        }

        return TgaWriter.EncodeRgb8(rgb, w, h, options);
    }

    private static bool HasFooter(byte[] tga) =>
        tga.Length >= 18 && tga.AsSpan(tga.Length - 18).SequenceEqual("TRUEVISION-XFILE.\0"u8);

    public static TheoryData<bool, bool> AlphaAndRle() => new()
    {
        { false, false }, { false, true }, { true, false }, { true, true },
    };

    private static void AssertFacadeEqualsReader(byte[] tga, string what)
    {
        TgaImage expected = TgaReader.Decode(tga);
        int w = expected.Width, h = expected.Height;

        ImageCodecs.CanDecode(tga).ShouldBeTrue(what);

        ImageCodecs.TryReadInfo(tga, out ImageInfo info).ShouldBeTrue(what);
        info.Width.ShouldBe(w, what);
        info.Height.ShouldBe(h, what);
        info.Channels.ShouldBe(expected.HasAlpha ? 4 : 3, what);
        info.SampleFormat.ShouldBe(SampleFormat.UInt8, what);

        ImageCodecs.TryDecode(tga, out IDecodedImage? image).ShouldBeTrue(what);
        image!.Width.ShouldBe(w, what);
        image.Height.ShouldBe(h, what);
        image.Channels.ShouldBe(info.Channels, what);
        image.ToRgba8().ShouldBe(expected.Rgba, what);

        // A larger destination is fine; only the first width * height * 4 bytes are written.
        byte[] rgba = new byte[w * h * 4 + 5];
        ImageCodecs.TryDecodeIntoRgba8(tga, rgba).ShouldBeTrue(what);
        rgba.AsSpan(0, w * h * 4).ToArray().ShouldBe(expected.Rgba, what);

        ImageCodecs.TryDecodeIntoRgba8(tga, new byte[w * h * 4 - 1]).ShouldBeFalse(what + " (short destination)");
    }

    [Theory]
    [MemberData(nameof(AlphaAndRle))]
    public void OurFooteredTga_DecodesThroughTheFacadeAsTgaReaderDoes(bool alpha, bool rle)
    {
        byte[] tga = OurFile(alpha, rle);
        HasFooter(tga).ShouldBeTrue("TgaWriter always writes the footer");
        AssertFacadeEqualsReader(tga, $"ours alpha={alpha} rle={rle}");
    }

    [Theory]
    [MemberData(nameof(AlphaAndRle))]
    public void MagickWrittenTga_IsRecognisedExactlyWhenItCarriesTheFooter(bool alpha, bool rle)
    {
        byte[] tga = WriteWithMagick(alpha, rle);
        if (HasFooter(tga))
        {
            AssertFacadeEqualsReader(tga, $"Magick alpha={alpha} rle={rle}");
        }
        else
        {
            // Magick.NET (as of the pinned version) writes a TGA 1.0 file with no footer: not for the facade,
            // but still readable directly.
            ImageCodecs.CanDecode(tga).ShouldBeFalse();
            ImageCodecs.TryDecode(tga, out _).ShouldBeFalse();
            TgaReader.Decode(tga).Width.ShouldBe(13);

            // Appending the footer is the only thing that turns it into one the facade takes.
            AssertFacadeEqualsReader(WithFooter(tga), $"Magick+footer alpha={alpha} rle={rle}");
        }
    }

    [Theory]
    [MemberData(nameof(AlphaAndRle))]
    public void FooterlessTga_IsNotRecognisedButStillDecodesThroughTgaReader(bool alpha, bool rle)
    {
        byte[] withFooter = OurFile(alpha, rle);
        byte[] bare = withFooter[..^Footer.Length];

        ImageCodecs.CanDecode(bare).ShouldBeFalse();
        ImageCodecs.TryReadInfo(bare, out _).ShouldBeFalse();
        ImageCodecs.TryDecode(bare, out IDecodedImage? image).ShouldBeFalse();
        image.ShouldBeNull();
        ImageCodecs.TryDecodeIntoRgba8(bare, new byte[13 * 7 * 4]).ShouldBeFalse();

        TgaReader.Decode(bare).Rgba.ShouldBe(TgaReader.Decode(withFooter).Rgba);
    }

    [Fact]
    public void ArbitraryBytes_AreNotRecognised()
    {
        var rng = new Random(54);
        for (int length = 0; length < 200; length += 7)
        {
            byte[] noise = new byte[length];
            rng.NextBytes(noise);
            ImageCodecs.CanDecode(noise).ShouldBeFalse($"{length} random bytes");
            ImageCodecs.TryDecode(noise, out _).ShouldBeFalse();
        }

        // Zeros and a plausible-looking uncompressed header with no footer: a header guess would claim these.
        ImageCodecs.CanDecode(new byte[1000]).ShouldBeFalse();
        ImageCodecs.CanDecode(Build(2, 1, 1, 24, 0x20, [1, 2, 3])).ShouldBeFalse();
    }

    [Fact]
    public void TheFooterAlone_OnArbitraryBytes_IsNotEnough()
    {
        // The footer signature on bytes whose header is no TGA (image type 0x42) is not claimed.
        byte[] junk = new byte[18];
        junk[2] = 0x42;
        ImageCodecs.CanDecode(WithFooter(junk)).ShouldBeFalse();
        ImageCodecs.CanDecode(Footer).ShouldBeFalse();
    }

    [Fact]
    public void AFooteredTga_WithACorruptBody_ReturnsFalseAndNeverThrows()
    {
        // Each of these parses as a TGA header and ends in the footer, so the facade claims it, then declines it.
        byte[][] corrupt =
        [
            // RLE run of 4 pixels in a 3x1 image: overruns the image and is refused.
            WithFooter(Build(10, 3, 1, 24, 0x20, Run(4, 1, 2, 3))),
            // 100x100 raw 24-bit image with only a few bytes of pixels.
            WithFooter(Build(2, 100, 100, 24, 0x20, new byte[9])),
            // Colour index outside the 2-entry map.
            WithFooter(Build(1, 2, 1, 8, 0x20, [0, 9], map: [1, 2, 3, 4, 5, 6], mapLength: 2, mapEntryBits: 24)),
        ];

        foreach (byte[] tga in corrupt)
        {
            ImageCodecs.CanDecode(tga).ShouldBeTrue();

            IDecodedImage? image = null;
            Should.NotThrow(() => ImageCodecs.TryDecode(tga, out image)).ShouldBeFalse();
            image.ShouldBeNull();

            Should.NotThrow(() => ImageCodecs.TryDecodeIntoRgba8(tga, new byte[100 * 100 * 4])).ShouldBeFalse();
        }
    }

    [Fact]
    public void TgaImageDecoder_CanDecode_RequiresTheFooterAndASaneHeader()
    {
        byte[] good = OurFile(alpha: true, rle: false);
        TgaImageDecoder.CanDecode(good).ShouldBeTrue();
        TgaImageDecoder.CanDecode(good[..^1]).ShouldBeFalse();
        TgaImageDecoder.CanDecode(good[..^Footer.Length]).ShouldBeFalse();
        TgaImageDecoder.CanDecode([]).ShouldBeFalse();
    }
}
