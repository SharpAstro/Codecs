using System;
using SharpAstro.Codecs.Abstractions;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

public sealed class RasterImageTests
{
    [Fact]
    public void Gray8_expands_across_rgb_opaque()
    {
        // 2x1 gray: [10, 200]
        var img = new RasterImage(2, 1, 1, SampleFormat.UInt8, [10, 200]);

        var rgba = img.ToRgba8();

        rgba.Length.ShouldBe(2 * 1 * 4);
        rgba.ShouldBe([10, 10, 10, 255, 200, 200, 200, 255]);
    }

    [Fact]
    public void GrayAlpha8_maps_second_channel_to_alpha()
    {
        // 1x1 gray+alpha: gray 128, alpha 64
        var img = new RasterImage(1, 1, 2, SampleFormat.UInt8, [128, 64]);

        img.ToRgba8().ShouldBe([128, 128, 128, 64]);
    }

    [Fact]
    public void Rgb8_copies_and_sets_opaque_alpha()
    {
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [1, 2, 3]);

        img.ToRgba8().ShouldBe([1, 2, 3, 255]);
    }

    [Fact]
    public void Rgba8_round_trips_verbatim()
    {
        var img = new RasterImage(1, 1, 4, SampleFormat.UInt8, [1, 2, 3, 4]);

        img.ToRgba8().ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public void UInt16_gray_scales_high_byte()
    {
        // one 16-bit gray sample = 0xABCD (little-endian bytes CD AB); >> 8 -> 0xAB
        var img = new RasterImage(1, 1, 1, SampleFormat.UInt16, [0xCD, 0xAB]);

        img.ToRgba8().ShouldBe([0xAB, 0xAB, 0xAB, 255]);
    }

    [Fact]
    public void UInt16_rgb_scales_each_channel()
    {
        // R=0x1234, G=0x5678, B=0x9ABC  ->  0x12, 0x56, 0x9A
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt16, [0x34, 0x12, 0x78, 0x56, 0xBC, 0x9A]);

        img.ToRgba8().ShouldBe([0x12, 0x56, 0x9A, 255]);
    }

    [Fact]
    public void ExpandToRgba8_writes_into_caller_buffer_zero_copy_style()
    {
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [7, 8, 9]);
        Span<byte> dst = stackalloc byte[4];

        img.ExpandToRgba8(dst);

        dst.ToArray().ShouldBe([7, 8, 9, 255]);
    }

    [Fact]
    public void Float32_has_no_canonical_rgba_mapping()
    {
        var img = new RasterImage(1, 1, 1, SampleFormat.Float32, new byte[4]);

        Should.Throw<NotSupportedException>(() => img.ToRgba8());
    }

    [Fact]
    public void IccProfile_defaults_empty_and_round_trips_when_present()
    {
        new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0]).IccProfile.IsEmpty.ShouldBeTrue();

        byte[] icc = [1, 2, 3];
        new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0], icc).IccProfile.ToArray().ShouldBe(icc);
    }

    [Theory]
    [InlineData(SampleFormat.UInt8, 1)]
    [InlineData(SampleFormat.UInt16, 2)]
    [InlineData(SampleFormat.Float32, 4)]
    public void BytesPerSample_matches_format(SampleFormat format, int expected)
        => RasterImage.BytesPerSample(format).ShouldBe(expected);

    [Fact]
    public void Ctor_rejects_undersized_pixel_buffer()
        // 2x2 RGB needs 12 bytes; give it 3
        => Should.Throw<ArgumentException>(() => new RasterImage(2, 2, 3, SampleFormat.UInt8, [0, 0, 0]));

    [Theory]
    [InlineData(0, 1, 3)]
    [InlineData(1, 0, 3)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 5)]
    public void Ctor_rejects_bad_dimensions(int w, int h, int channels)
        => Should.Throw<ArgumentOutOfRangeException>(() => new RasterImage(w, h, channels, SampleFormat.UInt8, new byte[64]));

    [Fact]
    public void ExpandToRgba8_rejects_undersized_destination()
    {
        var img = new RasterImage(2, 2, 3, SampleFormat.UInt8, new byte[12]);

        Should.Throw<ArgumentException>(() =>
        {
            var tooSmall = new byte[8]; // needs 2*2*4 = 16
            img.ExpandToRgba8(tooSmall);
        });
    }
}
