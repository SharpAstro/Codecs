using System;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Exr;
using SharpAstro.Jxl;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The 3.5 facade family — TIFF / JXR / EXR / JXL decode through
/// <see cref="ImageCodecs"/> with correct layout, pixel fidelity, and
/// <see cref="IDecodedImage.ColorEncoding"/>; float-sample content refuses the
/// 8-bit display path (<see cref="ImageCodecs.TryDecodeIntoRgba8"/>) instead of
/// projecting HDR values wrongly.
/// </summary>
public sealed class CodecAdapterTests
{
    // ---------------------------------------------------------------- TIFF

    [Fact]
    public void Tiff_uint16_gray_decodes_through_facade()
    {
        int[] y = [0, 1000, 30000, 65535];
        var tiff = HdrTiff.Uint16Gray(2, 2, y);

        ImageCodecs.CanDecode(tiff).ShouldBeTrue();
        ImageCodecs.TryReadInfo(tiff, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(2, 2, 1, SampleFormat.UInt16));

        ImageCodecs.TryDecode(tiff, out var img).ShouldBeTrue();
        img!.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
        var f = img.ToFloats();
        f[0].ShouldBe(0f);
        f[4].ShouldBe(1000 / 65535f);
        f[12].ShouldBe(1f);
    }

    [Fact]
    public void Tiff_float32_gray_is_verbatim_scene_linear_and_refuses_rgba8()
    {
        float[] y = [0f, 0.5f, 2.5f, -0.25f];
        var tiff = HdrTiff.Float32Gray(2, 2, y);

        ImageCodecs.TryDecode(tiff, out var img).ShouldBeTrue();
        img!.SampleFormat.ShouldBe(SampleFormat.Float32);
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        img.ColorEncoding.Float.ShouldBe(FloatSemantics.SceneReferred);

        var f = img.ToFloats();
        f[4].ShouldBe(0.5f);
        f[8].ShouldBe(2.5f);    // HDR highlight, unclamped
        f[12].ShouldBe(-0.25f); // negative survives

        ImageCodecs.TryDecodeIntoRgba8(tiff, new byte[2 * 2 * 4]).ShouldBeFalse();
    }

    [Fact]
    public void Tiff_half_gray_widens_losslessly_to_float32()
    {
        Half[] y = [(Half)0.5f, (Half)1.5f, (Half)(-2f), (Half)0f];
        var tiff = HdrTiff.HalfGray(2, 2, y);

        ImageCodecs.TryDecode(tiff, out var img).ShouldBeTrue();
        img!.SampleFormat.ShouldBe(SampleFormat.Float32);

        var f = img.ToFloats();
        f[0].ShouldBe(0.5f);
        f[4].ShouldBe(1.5f);
        f[8].ShouldBe(-2f);
    }

    // ----------------------------------------------------------------- JXR

    [Fact]
    public void Jxr_gray8_round_trips_through_facade_including_display_path()
    {
        int[] y = [0, 64, 128, 255];
        var jxr = JxrImageCodec.EncodeGray8(y, 2, 2);

        ImageCodecs.TryReadInfo(jxr, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(2, 2, 1, SampleFormat.UInt8));

        ImageCodecs.TryDecode(jxr, out var img).ShouldBeTrue();
        img!.Pixels.ToArray().ShouldBe(new byte[] { 0, 64, 128, 255 });
        img.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);

        var rgba = new byte[2 * 2 * 4];
        ImageCodecs.TryDecodeIntoRgba8(jxr, rgba).ShouldBeTrue();
        rgba[0].ShouldBe((byte)0);
        rgba[4].ShouldBe((byte)64);
        rgba[3].ShouldBe((byte)255); // synthesized alpha
    }

    [Fact]
    public void Jxr_rgb24_interleaves_channel_planes()
    {
        int[] r = [255, 0, 0, 10];
        int[] g = [0, 255, 0, 20];
        int[] b = [0, 0, 255, 30];
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, 2, 2);

        ImageCodecs.TryDecode(jxr, out var img).ShouldBeTrue();
        (img!.Channels, img.SampleFormat).ShouldBe((3, SampleFormat.UInt8));
        img.Pixels[..6].ToArray().ShouldBe(new byte[] { 255, 0, 0, 0, 255, 0 });
    }

    [Fact]
    public void Jxr_grayf32_is_scrgb_display_referred_linear()
    {
        // Powers of two survive the BD32F mantissa quantization exactly.
        float[] y = [0f, 0.5f, 1f, 2f];
        var jxr = JxrImageCodec.EncodeGrayF32(y, 2, 2);

        ImageCodecs.TryDecode(jxr, out var img).ShouldBeTrue();
        img!.SampleFormat.ShouldBe(SampleFormat.Float32);
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        img.ColorEncoding.Float.ShouldBe(FloatSemantics.DisplayReferred); // scRGB: 1.0 = white

        var f = img.ToFloats();
        f[4].ShouldBe(0.5f);
        f[8].ShouldBe(1f);
        f[12].ShouldBe(2f);

        ImageCodecs.TryDecodeIntoRgba8(jxr, new byte[16]).ShouldBeFalse();
    }

    // ----------------------------------------------------------------- EXR

    [Fact]
    public void Exr_mono_float_is_scene_referred_and_refuses_rgba8()
    {
        float[] y = [0f, 0.25f, 4.5f, -1f];
        var exr = ExrImageCodec.EncodeMonoFloat(y, 2, 2);

        ImageCodecs.CanDecode(exr).ShouldBeTrue();
        ImageCodecs.TryDecode(exr, out var img).ShouldBeTrue();
        (img!.Channels, img.SampleFormat).ShouldBe((1, SampleFormat.Float32));
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        img.ColorEncoding.Float.ShouldBe(FloatSemantics.SceneReferred);

        var f = img.ToFloats();
        f[4].ShouldBe(0.25f);
        f[8].ShouldBe(4.5f);
        f[12].ShouldBe(-1f);

        ImageCodecs.TryDecodeIntoRgba8(exr, new byte[16]).ShouldBeFalse();
    }

    [Fact]
    public void Exr_rgb_half_widens_to_interleaved_float_rgb()
    {
        // Quarters up to 2.75 are exactly representable as halves.
        var rgb = new Half[2 * 2 * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (Half)(i * 0.25f);
        var exr = ExrImageCodec.EncodeRgbHalf(rgb, 2, 2);

        ImageCodecs.TryDecode(exr, out var img).ShouldBeTrue();
        (img!.Channels, img.SampleFormat).ShouldBe((3, SampleFormat.Float32));

        var f = img.ToFloats();
        f[0].ShouldBe(0f);
        f[1].ShouldBe(0.25f);
        f[2].ShouldBe(0.5f);
        f[3].ShouldBe(1f); // synthesized alpha
        f[4].ShouldBe(0.75f);
    }

    // ----------------------------------------------------------------- JXL

    [Fact]
    public void Jxl_rgb24_round_trips_through_facade_including_display_path()
    {
        int[] r = [255, 0, 0, 10];
        int[] g = [0, 255, 0, 20];
        int[] b = [0, 0, 255, 30];
        var jxl = JxlImageCodec.EncodeRgb24(r, g, b, 2, 2);

        ImageCodecs.CanDecode(jxl).ShouldBeTrue();
        ImageCodecs.TryReadInfo(jxl, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(2, 2, 3, SampleFormat.UInt8));

        ImageCodecs.TryDecode(jxl, out var img).ShouldBeTrue();
        img!.Pixels[..6].ToArray().ShouldBe(new byte[] { 255, 0, 0, 0, 255, 0 });

        var rgba = new byte[16];
        ImageCodecs.TryDecodeIntoRgba8(jxl, rgba).ShouldBeTrue();
        rgba[0].ShouldBe((byte)255);
        rgba[3].ShouldBe((byte)255); // synthesized alpha
    }

    [Fact]
    public void Jxl_grayf32_is_verbatim_float()
    {
        float[] y = [0f, 0.5f, 123.456f, -7.25f];
        var jxl = JxlImageCodec.EncodeGrayF32(y, 2, 2);

        ImageCodecs.TryDecode(jxl, out var img).ShouldBeTrue();
        img!.SampleFormat.ShouldBe(SampleFormat.Float32);
        img.ColorEncoding.Float.ShouldBe(FloatSemantics.SceneReferred);

        var f = img.ToFloats();
        f[8].ShouldBe(123.456f); // lossless Modular float: bit-exact verbatim
        f[12].ShouldBe(-7.25f);

        ImageCodecs.TryDecodeIntoRgba8(jxl, new byte[16]).ShouldBeFalse();
    }

    // ------------------------------------------------- sniff disambiguation

    [Fact]
    public void Tiff_and_jxr_ii_signatures_do_not_collide()
    {
        // Both start "II" but differ at byte 2 (0x2A vs 0xBC).
        var tiff = HdrTiff.Uint16Gray(1, 1, [42]);
        var jxr = JxrImageCodec.EncodeGray8([42], 1, 1);

        ImageCodecs.TryDecode(tiff, out var tiffImg).ShouldBeTrue();
        tiffImg!.SampleFormat.ShouldBe(SampleFormat.UInt16);

        ImageCodecs.TryDecode(jxr, out var jxrImg).ShouldBeTrue();
        jxrImg!.SampleFormat.ShouldBe(SampleFormat.UInt8);
    }
}
