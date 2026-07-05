using SharpAstro.Codecs.Abstractions;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="IDecodedImage.ColorEncoding"/> — the meaning tier beside the
/// container tier. The key property under test: PNG-3 colour signalling (cICP,
/// gAMA 1.0) survives the codec-neutral <see cref="ImageCodecs"/> facade instead
/// of being dropped at the adapter boundary, and untagged content lands on the
/// same sRGB assumption consumers already baked in.
/// </summary>
public sealed class ColorEncodingTests
{
    private static readonly byte[] TinyRgba = new byte[4 * 4 * 4];

    [Fact]
    public void RasterImage_defaults_to_the_assumed_srgb_encoding()
    {
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0]);

        img.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
        img.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT709);
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Srgb);
        img.ColorEncoding.FullRange.ShouldBeTrue();
        img.ColorEncoding.Float.ShouldBe(FloatSemantics.NotApplicable);
    }

    [Fact]
    public void RasterImage_carries_an_explicit_encoding_verbatim()
    {
        var pq = new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Pq };
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0], iccProfile: null, colorEncoding: pq);

        img.ColorEncoding.ShouldBeSameAs(pq);
    }

    [Fact]
    public void Png_cicp_hdr10_survives_the_facade_decode()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq });

        ImageCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT2020);
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Pq);
        img.ColorEncoding.FullRange.ShouldBeTrue();
    }

    [Fact]
    public void Untagged_png_lands_on_the_assumed_srgb_encoding()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions());

        ImageCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
    }

    [Fact]
    public void Png_gamma_one_maps_to_the_linear_transfer_codepoint()
    {
        // gAMA 1.0 = code value proportional to light intensity (e.g. a
        // linear-light astro master stored as 16-bit PNG).
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Gamma = 1.0 });

        ImageCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        img.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT709);
    }

    [Fact]
    public void Png_cicp_takes_precedence_over_gamma()
    {
        // PNG-3 gives cICP precedence over gAMA when both are present.
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions
        {
            Cicp = CicpChunk.Bt2020Hlg,
            Gamma = 1.0,
        });

        ImageCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.ColorEncoding.Transfer.ShouldBe(TransferFunction.Hlg);
        img.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT2020);
    }
}
