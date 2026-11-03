using SharpAstro.Codecs.Abstractions;
using SharpAstro.Gif;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>The facade sniffs GIF87a / GIF89a and presents the first frame as RGBA8.</summary>
public sealed class GifFacadeTests
{
    private static readonly byte[] Colours = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];

    private static byte[] Build(bool gif89a, int transparentIndex = -1)
    {
        var b = new GifBuilder().Header(4, 3, Colours, gif89a: gif89a);
        if (transparentIndex >= 0)
        {
            b.GraphicControl(transparentIndex: transparentIndex);
        }
        return b.Image(0, 0, 4, 3, [0, 1, 2, 3, 3, 2, 1, 0, 1, 1, 2, 2], 2).Trailer().ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothVersionsAreSniffed_AndDecodeThroughTheFacade(bool gif89a)
    {
        var gif = Build(gif89a);

        ImageCodecs.CanDecode(gif).ShouldBeTrue();
        GifImageDecoder.CanDecode(gif).ShouldBeTrue();

        ImageCodecs.TryReadInfo(gif, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(4, 3, 4, SampleFormat.UInt8));

        ImageCodecs.TryDecode(gif, out var image).ShouldBeTrue();
        image!.Width.ShouldBe(4);
        image.Height.ShouldBe(3);
        image.Channels.ShouldBe(4);
        image.SampleFormat.ShouldBe(SampleFormat.UInt8);
        image.Pixels.ToArray().ShouldBe(GifDecoder.Decode(gif).Rgba);
        image.Pixels[..8].ToArray().ShouldBe([255, 0, 0, 255, 0, 255, 0, 255]);

        var dst = new byte[4 * 3 * 4];
        ImageCodecs.TryDecodeIntoRgba8(gif, dst).ShouldBeTrue();
        dst.ShouldBe(image.Pixels.ToArray());
    }

    [Fact]
    public void ATransparentIndexComesOutAsAlphaZero()
    {
        var gif = Build(true, transparentIndex: 3);
        ImageCodecs.TryDecode(gif, out var image).ShouldBeTrue();
        image!.Pixels[12..16].ToArray().ShouldBe([255, 255, 0, 0], "the palette colour stays under alpha 0, as in Magick's pixels");
        image.Pixels[0..4].ToArray().ShouldBe([255, 0, 0, 255]);
    }

    [Fact]
    public void ATruncatedFile_StillDecodesThroughTheFacade()
    {
        var gif = Build(true)[..^6];
        ImageCodecs.TryDecode(gif, out var image).ShouldBeTrue();
        image!.Width.ShouldBe(4);
    }

    [Fact]
    public void ADestinationThatIsTooSmall_IsRefusedWithoutThrowing()
    {
        ImageCodecs.TryDecodeIntoRgba8(Build(true), new byte[4 * 3 * 4 - 1]).ShouldBeFalse();
    }

    [Fact]
    public void LookAlikesAndDamagedFiles_AreNotDecoded()
    {
        // Right letters, wrong version; and the signature alone.
        byte[] wrongVersion = [.. "GIF88a"u8.ToArray(), .. new byte[20]];
        ImageCodecs.CanDecode(wrongVersion).ShouldBeFalse();
        ImageCodecs.TryDecode(wrongVersion, out _).ShouldBeFalse();

        var signatureOnly = "GIF89a"u8.ToArray();
        ImageCodecs.CanDecode(signatureOnly).ShouldBeTrue();
        ImageCodecs.TryDecode(signatureOnly, out var image).ShouldBeFalse();
        image.ShouldBeNull();
        ImageCodecs.TryReadInfo(signatureOnly, out _).ShouldBeFalse();
        ImageCodecs.TryDecodeIntoRgba8(signatureOnly, new byte[64]).ShouldBeFalse();
    }

    [Fact]
    public void AHugeScreen_IsRefusedByTheFacade()
    {
        var gif = new GifBuilder().Header(65535, 65535, Colours).Image(0, 0, 1, 1, [0], 2).Trailer().ToArray();
        ImageCodecs.CanDecode(gif).ShouldBeTrue();
        ImageCodecs.TryReadInfo(gif, out _).ShouldBeFalse();
        ImageCodecs.TryDecode(gif, out _).ShouldBeFalse();
    }
}
