using System.Buffers.Binary;
using System.Text;
using ImageMagick;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// An APNG whose IHDR says Adam7 has interlaced frame data (<c>fdAT</c> is <c>IDAT</c>'s format, interlace included), and
/// the frame decode, which reuses one buffer, must de-interlace each frame. Pillow cannot write an interlaced APNG, so the
/// file is built by hand from two libpng (Magick.NET) encodings of one random raster, interlaced and not: the same two
/// frames (the raster as <c>IDAT</c> and as <c>fdAT</c>) wrapped in each, and the composited frames must be identical.
/// </summary>
public sealed class PngApngAdam7Tests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // 1x1: pass 1 only. 3x2: most passes empty. 9x7 and 33x17: every pass populated and ragged.
    [Theory]
    [InlineData(MagickFormat.Png32, 1, 1)]
    [InlineData(MagickFormat.Png32, 3, 2)]
    [InlineData(MagickFormat.Png32, 9, 7)]
    [InlineData(MagickFormat.Png32, 33, 17)]
    [InlineData(MagickFormat.Png24, 33, 17)]
    [InlineData(MagickFormat.Png48, 9, 7)]
    [InlineData(MagickFormat.Png64, 33, 17)]
    [InlineData(MagickFormat.Png8, 9, 7)]
    public void AnInterlacedApng_CompositesAsItsNonInterlacedTwin(MagickFormat format, int width, int height)
    {
        var interlaced = Encode(format, width, height, Interlace.Png);
        var plain = Encode(format, width, height, Interlace.NoInterlace);
        Chunks(interlaced)[0].Data[12].ShouldBe((byte)1);
        Chunks(plain)[0].Data[12].ShouldBe((byte)0);

        var interlacedApng = TwoFrameApng(interlaced, width, height);
        var plainApng = TwoFrameApng(plain, width, height);

        var fromInterlaced = PngReader.DecodeFrames(interlacedApng);
        var fromPlain = PngReader.DecodeFrames(plainApng);

        fromInterlaced.Frames.Count.ShouldBe(2);
        fromInterlaced.Frames.Count.ShouldBe(fromPlain.Frames.Count);
        for (var i = 0; i < 2; i++)
            fromInterlaced.Frames[i].Rgba.ShouldBe(fromPlain.Frames[i].Rgba);

        // The streaming path takes the same reused buffer and must agree with the materialised one.
        var streamed = PngReader.EnumerateFrames(interlacedApng).ToArray();
        streamed.Length.ShouldBe(2);
        for (var i = 0; i < 2; i++)
            streamed[i].Rgba.ShouldBe(fromPlain.Frames[i].Rgba);
    }

    private static byte[] Encode(MagickFormat format, int width, int height, Interlace interlace)
    {
        var rng = new Random(width * 1000 + height);
        var bytes = new byte[width * height * 4];
        rng.NextBytes(bytes);
        using var image = new MagickImage(bytes, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        image.Settings.Interlace = interlace;
        image.Format = format;
        using var ms = new MemoryStream();
        image.Write(ms, format);
        return ms.ToArray();
    }

    /// <summary>The still's IHDR, palette and friends, an <c>acTL</c> of two frames, and the still's image data twice: as <c>IDAT</c> (frame 0) and as <c>fdAT</c> (frame 1).</summary>
    private static byte[] TwoFrameApng(byte[] still, int width, int height)
    {
        var source = Chunks(still);
        var idat = source.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        var result = new List<(string Type, byte[] Data)>();
        foreach (var chunk in source)
        {
            if (chunk.Type == "IDAT" || chunk.Type == "IEND") continue;
            result.Add(chunk);
        }
        result.Add(("acTL", [0, 0, 0, 2, 0, 0, 0, 0]));
        result.Add(("fcTL", FrameControl(0, width, height)));
        result.Add(("IDAT", idat));
        result.Add(("fcTL", FrameControl(1, width, height)));
        var fdat = new byte[4 + idat.Length];
        BinaryPrimitives.WriteUInt32BigEndian(fdat, 2);
        idat.CopyTo(fdat, 4);
        result.Add(("fdAT", fdat));
        result.Add(("IEND", []));
        return Build(result);
    }

    private static byte[] FrameControl(uint sequence, int width, int height)
    {
        var data = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(data, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)height);
        // x, y offsets 0; delay 1/10 s; dispose NONE (0); blend SOURCE (0).
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(22), 10);
        return data;
    }

    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        var pos = Signature.Length;
        while (pos < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            chunks.Add((Encoding.ASCII.GetString(png, pos + 4, 4), png.AsSpan(pos + 8, length).ToArray()));
            pos += 12 + length;
        }
        return chunks;
    }

    private static byte[] Build(IEnumerable<(string Type, byte[] Data)> chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        Span<byte> word = stackalloc byte[4];
        foreach (var (type, data) in chunks)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
            ms.Write(word);
            ms.Write(typeBytes);
            ms.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(word, Crc32([.. typeBytes, .. data]));
            ms.Write(word);
        }
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
