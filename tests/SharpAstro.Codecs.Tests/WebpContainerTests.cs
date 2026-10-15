using System.Buffers.Binary;
using SharpAstro.Webp;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

public sealed class WebpContainerTests
{
    [Fact]
    public void ReadInfo_ReportsLosslessHeaderDimensionsAndAlpha()
    {
        WebpInfo info = WebpDecoder.ReadInfo(BuildFile(
            ("VP8L", BuildVp8L(9, 7, hasAlpha: true))));

        info.ShouldBe(new WebpInfo(9, 7, true, WebpFormat.Lossless, 1));
    }

    [Fact]
    public void ReadInfo_UsesExtendedCanvasAndCountsAnimationFrames()
    {
        byte[] vp8x = new byte[10];
        vp8x[0] = 0x12;
        WriteUInt24(vp8x.AsSpan(4), 16);
        WriteUInt24(vp8x.AsSpan(7), 8);
        byte[] data = BuildFile(
            ("VP8X", vp8x),
            ("ANIM", new byte[6]),
            ("ANMF", BuildAnimationFrame(BuildVp8L(9, 7, hasAlpha: false), 9, 7)));

        WebpDecoder.ReadInfo(data).ShouldBe(new WebpInfo(17, 9, true, WebpFormat.Animated, 1));
    }

    [Fact]
    public void TryReadInfo_ReturnsFalseForMalformedContainers()
    {
        WebpDecoder.TryReadInfo("not a webp"u8, out _).ShouldBeFalse();
        WebpDecoder.TryReadInfo(BuildFile(("VP8L", [0x2f, 0, 0])), out _).ShouldBeFalse();
    }

    [Fact]
    public void ReadInfoRejectsAnimationFramesOutsideCanvas()
    {
        byte[] vp8x = new byte[10];
        vp8x[0] = 0x12;
        WriteUInt24(vp8x.AsSpan(4), 3);
        WriteUInt24(vp8x.AsSpan(7), 3);
        byte[] data = BuildFile(
            ("VP8X", vp8x),
            ("ANIM", new byte[6]),
            ("ANMF", BuildAnimationFrame(BuildVp8L(1, 1, hasAlpha: false), 1, 1, x: 6)));

        Should.Throw<InvalidDataException>(() => WebpDecoder.ReadInfo(data));
    }

    [Fact]
    public void ReadInfoRejectsCanvasAbovePixelLimit()
    {
        byte[] data = BuildFile(("VP8L", BuildVp8L(8193, 8193, hasAlpha: false)));

        Should.Throw<InvalidDataException>(() => WebpDecoder.ReadInfo(data));
    }

    [Fact]
    public void ReadInfoRejectsAnimationChunkWithoutAnimationFlag()
    {
        byte[] data = BuildFile(
            ("VP8X", new byte[10]),
            ("ANIM", new byte[6]),
            ("VP8L", BuildVp8L(1, 1, hasAlpha: false)));

        Should.Throw<InvalidDataException>(() => WebpDecoder.ReadInfo(data));
    }

    [Fact]
    public void TryDecodeTo_ReturnsFalseForUnsupportedLaterRungs()
    {
        WebpDecoder.TryDecodeTo(BuildFile(
            ("VP8L", BuildVp8L(2, 2, hasAlpha: false))), new byte[16]).ShouldBeFalse();
    }

    [Fact]
    public void LossyFixture_DecodesToOpaqueRgba()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "webp", "tiny.webp"));

        WebpImage image = WebpDecoder.Decode(file);

        image.Width.ShouldBe(2);
        image.Height.ShouldBe(2);
        image.HasAlpha.ShouldBeFalse();
        image.Rgba.Length.ShouldBe(16);
        for (int index = 3; index < image.Rgba.Length; index += 4)
        {
            image.Rgba[index].ShouldBe((byte)255);
        }
    }

    private static byte[] BuildVp8L(int width, int height, bool hasAlpha)
    {
        var data = new byte[5];
        data[0] = 0x2f;
        uint bits = (uint)(width - 1) | (uint)(height - 1) << 14;
        if (hasAlpha) bits |= 1u << 28;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), bits);
        return data;
    }

    private static byte[] BuildFile(params (string Type, byte[] Data)[] chunks)
    {
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        output.Write(new byte[4]);
        output.Write("WEBP"u8);
        foreach ((string type, byte[] payload) in chunks)
        {
            output.Write(System.Text.Encoding.ASCII.GetBytes(type));
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
            output.Write(length);
            output.Write(payload);
            if ((payload.Length & 1) != 0) output.WriteByte(0);
        }

        byte[] file = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        return file;
    }

    private static byte[] BuildAnimationFrame(byte[] image, int width, int height, int x = 0, int y = 0)
    {
        using var output = new MemoryStream();
        byte[] header = new byte[16];
        WriteUInt24(header, x / 2);
        WriteUInt24(header.AsSpan(3), y / 2);
        WriteUInt24(header.AsSpan(6), width - 1);
        WriteUInt24(header.AsSpan(9), height - 1);
        output.Write(header);
        output.Write("VP8L"u8);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)image.Length);
        output.Write(length);
        output.Write(image);
        if ((image.Length & 1) != 0) output.WriteByte(0);
        return output.ToArray();
    }

    private static void WriteUInt24(Span<byte> data, int value)
    {
        data[0] = (byte)value;
        data[1] = (byte)(value >> 8);
        data[2] = (byte)(value >> 16);
    }
}
