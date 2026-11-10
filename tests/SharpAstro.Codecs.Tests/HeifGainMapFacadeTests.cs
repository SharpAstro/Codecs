using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// A gain-map HEIF through the <c>SharpAstro.Codecs</c> facade: the float tier is the HDR reconstruction at the authored
/// headroom, the 8-bit tier stays the SDR base, and a plain HEIF is untouched. The files are the synthetic ones
/// <see cref="HeifGainMapTests"/> builds (real HEVC pictures from the committed x265 fixtures, a written
/// <c>tmap</c> or Apple auxiliary item); the reconstruction itself is checked against the formula there, so what is
/// asserted here is that the facade hands out THAT result, in the right tier, and falls back when it must.
/// </summary>
public sealed class HeifGainMapFacadeTests
{
    private static byte[] Tmap() => HeifGainMapTests.Build(HeifGainMapTests.Base256, HeifGainMapTests.Mono8);
    private static byte[] Apple() => HeifGainMapTests.AppleFile();

    /// <summary>The same base picture with no gain map at all: a plain HEIF.</summary>
    private static byte[] Plain() => HeifGainMapTests.Build(HeifGainMapTests.Base256, HeifGainMapTests.Mono8, configure: o => o.NoTmap = true);

    public static TheoryData<string> Dialects => ["tmap", "apple"];

    private static byte[] File(string dialect) => dialect == "tmap" ? Tmap() : Apple();

    // ------------------------------------------------------------- float tier

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TryDecode_PresentsTheHdrFloatTier(string dialect)
    {
        var file = File(dialect);

        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        var decoded = image.ShouldBeOfType<HeifGainMapDecodedImage>();
        (decoded.Width, decoded.Height).ShouldBe((256, 192));
        decoded.SampleFormat.ShouldBe(SampleFormat.Float32);
        decoded.Channels.ShouldBe(4);
        decoded.Pixels.Length.ShouldBe(256 * 192 * 4 * sizeof(float));
        decoded.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        decoded.ColorEncoding.Float.ShouldBe(FloatSemantics.DisplayReferred);
        decoded.IccProfile.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void ToFloats_IsTheReconstruction_AtTheAuthoredHeadroom(string dialect)
    {
        var file = File(dialect);
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();

        var floats = image!.ToFloats();

        var expected = HeifGainMap.Decode(file);
        expected.DisplayHeadroom.ShouldBe(expected.Metadata.HdrCapacityMax);
        floats.ShouldBe(expected.Rgba);
        floats.Where((_, i) => i % 4 != 3).Max().ShouldBeGreaterThan(1.0f, "the boost reaches above SDR white");
        image.ToFloats().ShouldNotBeSameAs(floats, "a fresh array each call");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TryDecodeRgbaFloat_IsTheSameHdr(string dialect)
    {
        var file = File(dialect);

        HeifImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out var width, out var height, out var encoding).ShouldBeTrue();

        var expected = HeifGainMap.Decode(file);
        (width, height).ShouldBe((expected.Width, expected.Height));
        rgba.ShouldBe(expected.Rgba);
        encoding.ShouldBe(expected.ColorEncoding);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Source_ReconstructsAtOtherHeadrooms(string dialect)
    {
        var file = File(dialect);
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        var source = image.ShouldBeOfType<HeifGainMapDecodedImage>().Source;

        source.AuthoredHeadroom.ShouldBeGreaterThan(1);
        source.Reconstruct(2).Rgba.ShouldBe(HeifGainMap.Decode(file, displayHeadroom: 2).Rgba);
        source.Reconstruct().Rgba.ShouldBe(image!.ToFloats());

        // Headroom 1 is W = 0: the base, linearised, whatever the gain map says.
        var sdr = source.Reconstruct(1);
        sdr.Weight.ShouldBe(0);
        var baseLinear = HeifDecoder.Decode(file, source.Info.Base).ToLinearRgbaFloat(out _);
        for (var p = 0; p < sdr.Width * sdr.Height; p += 11)
            sdr.Rgba[4 * p + 1].ShouldBe(baseLinear[4 * p + 1], 1e-6f);
    }

    // ------------------------------------------------------------- 8-bit tier

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TheEightBitTier_IsTheSdrBase_AsForThePlainFile(string dialect)
    {
        var file = File(dialect);
        var plain = Plain();   // the same base picture, nothing else
        var expected = new byte[256 * 192 * 4];
        ImageCodecs.TryDecodeIntoRgba8(plain, expected).ShouldBeTrue();

        var direct = new byte[expected.Length];
        ImageCodecs.TryDecodeIntoRgba8(file, direct).ShouldBeTrue();
        direct.ShouldBe(expected);

        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        image!.ToRgba8().ShouldBe(expected);
    }

    // ------------------------------------------------------------- info and plain files

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TryReadInfo_SaysFloatForAGainMap(string dialect)
    {
        ImageCodecs.TryReadInfo(File(dialect), out var info).ShouldBeTrue();

        info.ShouldBe(new ImageInfo(256, 192, 4, SampleFormat.Float32));
    }

    [Fact]
    public void APlainHeif_IsUnchanged()
    {
        var file = Plain();

        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(256, 192, 3, SampleFormat.UInt16));
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        image.ShouldBeOfType<RasterImage>().SampleFormat.ShouldBe(SampleFormat.UInt16);
        HeifImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out _, out _, out _).ShouldBeTrue();
        rgba!.Where((_, i) => i % 4 != 3).Max().ShouldBeLessThanOrEqualTo(1.0f);
    }

    // ------------------------------------------------------------- the tmap as the primary item

    [Fact]
    public void WhenTheTmapIsThePrimary_BothTiersWork_AndShowTheSameRotatedPicture()
    {
        var file = HeifGainMapTests.Build(HeifGainMapTests.Base256, HeifGainMapTests.Mono8,
            configure: o => { o.TmapIsPrimary = true; o.TmapIrot = HeifBuilder.Irot(90); });

        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(192, 256, 4, SampleFormat.Float32));
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        (image!.Width, image.Height).ShouldBe((192, 256));
        image.ToFloats().Length.ShouldBe(192 * 256 * 4);

        // The 8-bit tier is the base turned the same way, not the unrotated base, and not a failure.
        var rgba8 = new byte[192 * 256 * 4];
        ImageCodecs.TryDecodeIntoRgba8(file, rgba8).ShouldBeTrue();
        image.ToRgba8().ShouldBe(rgba8);
        var unrotated = new byte[256 * 192 * 4];
        ImageCodecs.TryDecodeIntoRgba8(Plain(), unrotated).ShouldBeTrue();
        // 90 degrees anticlockwise: (x, y) -> (y, width - 1 - x).
        for (var y = 0; y < 192; y += 7)
        {
            for (var x = 0; x < 256; x += 7)
            {
                var from = 4 * (y * 256 + x);
                var to = 4 * ((256 - 1 - x) * 192 + y);
                rgba8.AsSpan(to, 4).ToArray().ShouldBe(unrotated.AsSpan(from, 4).ToArray());
            }
        }
    }

    /// <summary>
    /// The tmap's own clap + irot + imir, in both layouts: <c>TryReadInfo</c>, the HDR tier and the 8-bit tier (the static
    /// path and the decoded image's) all describe one picture of one size. The layout with the base as the primary used to
    /// leave the static 8-bit path unrotated, so it disagreed with the other three.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTmapsClapIrotAndImir_GiveEveryTierTheSameSize(bool tmapIsPrimary)
    {
        var file = HeifGainMapTests.Build(HeifGainMapTests.Base256, HeifGainMapTests.Mono8, configure: o =>
        {
            o.TmapIsPrimary = tmapIsPrimary;
            o.TmapProperties.Add(("clap", HeifBuilder.Clap(200, 120)));
            o.TmapProperties.Add(("irot", HeifBuilder.Irot(90)));
            o.TmapProperties.Add(("imir", HeifBuilder.Imir(1)));
        });

        // 256x192 cropped to 200x120, then turned 90 degrees: 120x200.
        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(120, 200, 4, SampleFormat.Float32));
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        (image!.Width, image.Height).ShouldBe((120, 200));
        image.ToFloats().Length.ShouldBe(120 * 200 * 4);
        HeifImageDecoder.TryDecodeRgbaFloat(file, out var floats, out var fw, out var fh, out _).ShouldBeTrue();
        (fw, fh, floats!.Length).ShouldBe((120, 200, 120 * 200 * 4));

        var rgba8 = new byte[120 * 200 * 4];
        ImageCodecs.TryDecodeIntoRgba8(file, rgba8).ShouldBeTrue();
        image.ToRgba8().ShouldBe(rgba8);
    }

    // ------------------------------------------------------------- what falls back to the plain base

    [Fact]
    public void AGainMapThatCannotBeUsed_LeavesThePlainBase()
    {
        // A tmap of a version nobody defined: GainMapOf refuses it, the file is still a perfectly good HEIF.
        var badVersion = HeifGainMapTests.Build(HeifGainMapTests.Base256, HeifGainMapTests.Mono8,
            configure: o => o.TmapBytes = HeifGainMapTests.TmapData(HeifGainMapTests.Scalar(), version: 7));
        // The backward direction: the base is the HDR rendition, which is not reconstructed.
        var backward = HeifGainMapTests.Build(HeifGainMapTests.Base256, HeifGainMapTests.Mono8,
            HeifGainMapTests.Scalar() with { BaseRenditionIsHdr = true });

        foreach (var file in new[] { badVersion, backward })
        {
            ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
            image.ShouldBeOfType<RasterImage>();
            ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
            info.SampleFormat.ShouldBe(SampleFormat.UInt16);
            HeifImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out _, out _, out _).ShouldBeTrue();
            rgba!.Where((_, i) => i % 4 != 3).Max().ShouldBeLessThanOrEqualTo(1.0f);
            var rgba8 = new byte[256 * 192 * 4];
            ImageCodecs.TryDecodeIntoRgba8(file, rgba8).ShouldBeTrue();
        }
    }

    [Fact]
    public void AMapLargerThanItsBase_LeavesThePlainBase_NotAnException()
    {
        var file = HeifGainMapTests.Build(HeifGainMapTests.Base256, HeifGainMapTests.Mono8 with { DeclaredSize = (400, 400) });

        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();

        image.ShouldBeOfType<RasterImage>();
    }
}
