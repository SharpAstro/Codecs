using SharpAstro.Tga;
using Shouldly;
using static SharpAstro.Codecs.Tests.TgaTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>Files the reader refuses: each is an <see cref="InvalidDataException"/>, never a clamped or guessed image.</summary>
public sealed class TgaRefusalTests
{
    private static InvalidDataException Refused(byte[] tga) =>
        Should.Throw<InvalidDataException>(() => TgaReader.Decode(tga));

    [Fact]
    public void RlePacket_RunningPastTheImageEnd_IsRefused()
    {
        // 3 pixels declared, a run of 4. ImageMagick clips the run and returns an image; the code refuses.
        byte[] tga = Build(10, 3, 1, 24, 0x20, Run(4, 1, 2, 3));
        MagickRead(tga).Width.ShouldBe(3);
        Refused(tga).Message.ShouldContain("past the end");
    }

    [Fact]
    public void RawRlePacket_RunningPastTheImageEnd_IsRefused()
    {
        // 2 pixels declared; a raw packet of 3 whose bytes are all present.
        Refused(Build(10, 2, 1, 24, 0x20, Raw(3, 1, 2, 3, 4, 5, 6, 7, 8, 9))).Message.ShouldContain("past the end");
    }

    [Fact]
    public void RlePacket_PastTheEnd_AfterSomeGoodPackets_IsRefused()
    {
        Refused(Build(10, 2, 2, 24, 0x20, Concat(Run(3, 1, 2, 3), Run(2, 4, 5, 6))));
    }

    [Fact]
    public void RleData_EndingBeforeTheImageIsComplete_IsRefused()
    {
        // 4 pixels declared, 2 coded; padded so the up-front size check passes and the packet loop sees the end.
        byte[] tga = Build(10, 4, 1, 24, 0x20, Concat(Run(2, 1, 2, 3), new byte[4]));
        // The padding is read as packet headers (0x00: raw packet of 1 pixel), so the data really does run out.
        Refused(tga);
        Refused(Build(10, 4, 1, 24, 0x20, Run(2, 1, 2, 3)));
    }

    [Fact]
    public void RleRunPacket_CutShort_IsRefused()
    {
        Refused(Build(10, 128, 1, 24, 0x20, [0xFF, 1, 2]));
    }

    [Fact]
    public void RleRawPacket_CutShort_IsRefused()
    {
        Refused(Build(10, 3, 1, 24, 0x20, [0x02, 1, 2, 3, 4, 5, 6, 7]));
    }

    [Fact]
    public void TruncatedPixelData_IsRefused()
    {
        byte[] whole = Build(2, 3, 2, 24, 0x20, new byte[18]);
        Refused(whole[..^1]).Message.ShouldContain("truncated");
        Refused(Build(2, 3, 2, 24, 0x20, new byte[17])).Message.ShouldContain("truncated");
    }

    [Fact]
    public void FooterBytes_AreNotPixelData_WhenTheImageIsShort()
    {
        // 2x2 at 24 bits needs 12 bytes; 8 present, then a 26-byte TGA 2.0 footer. The footer must not fill the gap.
        byte[] footer = [.. new byte[8], .. new byte[8], .. "TRUEVISION-XFILE."u8.ToArray(), 0];
        Refused(Build(2, 2, 2, 24, 0x20, footer)).Message.ShouldContain("truncated");
    }

    [Fact]
    public void FooterBytes_AreNotPixelData_WhenAnRleImageIsShort()
    {
        // 2x2 at 24 bits RLE needs at least one packet (1 + 3 bytes); 3 are present, then a 26-byte footer. Untrimmed,
        // the footer would satisfy the up-front size check; trimmed, the check refuses it as truncated.
        byte[] footer = [.. new byte[8], .. new byte[8], .. "TRUEVISION-XFILE."u8.ToArray(), 0];
        Refused(Build(10, 2, 2, 24, 0x20, [.. Run(4, 1, 2), .. footer])).Message.ShouldContain("truncated");
    }

    [Fact]
    public void FooterBytes_AreNotPixelData_WhenAnRleImageIsComplete()
    {
        // A whole RLE image followed by a footer decodes exactly as it does without one.
        byte[] footer = [.. new byte[8], .. new byte[8], .. "TRUEVISION-XFILE."u8.ToArray(), 0];
        byte[] pixels = Run(4, 1, 2, 3);
        TgaReader.Decode(Build(10, 2, 2, 24, 0x20, [.. pixels, .. footer])).Rgba
            .ShouldBe(TgaReader.Decode(Build(10, 2, 2, 24, 0x20, pixels)).Rgba);
    }

    [Fact]
    public void TruncatedHeader_IsRefused()
    {
        Refused(new byte[17]);
        Refused([]);
        TgaReader.TryReadInfo(new byte[17], out _).ShouldBeFalse();
    }

    [Fact]
    public void TruncatedColorMap_OrImageId_IsRefused()
    {
        // Map declared with 256 entries of 24 bits, only 10 bytes present.
        Refused(Build(1, 1, 1, 8, 0x20, [0], new byte[10], mapLength: 256, mapEntryBits: 24));
        // Image ID of 200 bytes with none present.
        byte[] noId = Build(2, 1, 1, 24, 0x20, [1, 2, 3])[..18];
        noId[0] = 200;
        Refused(noId);
    }

    [Theory]
    [InlineData((byte)0)]  // "no image data"
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    [InlineData((byte)8)]
    [InlineData((byte)12)] // Huffman/delta/quadtree types of the old spec
    [InlineData((byte)32)]
    [InlineData((byte)33)]
    [InlineData((byte)255)]
    public void UnknownImageType_IsRefused(byte type)
    {
        byte[] tga = Build(type, 1, 1, 24, 0x20, [1, 2, 3]);
        Refused(tga);
        TgaReader.TryReadInfo(tga, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData((byte)2, (byte)0)]
    [InlineData((byte)2, (byte)1)]
    [InlineData((byte)2, (byte)8)]
    [InlineData((byte)2, (byte)12)]
    [InlineData((byte)2, (byte)48)]
    [InlineData((byte)10, (byte)8)]
    [InlineData((byte)3, (byte)16)] // grey + alpha: refused, ImageMagick and stb_image disagree on it
    [InlineData((byte)3, (byte)24)]
    [InlineData((byte)11, (byte)1)]
    [InlineData((byte)1, (byte)16)] // colour-mapped with 16-bit indices
    [InlineData((byte)1, (byte)4)]
    [InlineData((byte)9, (byte)24)]
    public void UnknownDepthForTheType_IsRefused(byte type, byte depth)
    {
        byte[] map = new byte[3];
        byte[] tga = type is 1 or 9
            ? Build(type, 1, 1, depth, 0x20, new byte[8], map, mapLength: 1, mapEntryBits: 24)
            : Build(type, 1, 1, depth, 0x20, new byte[16]);
        Refused(tga);
        TgaReader.TryReadInfo(tga, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)12)]
    [InlineData((byte)48)]
    public void UnknownColorMapEntrySize_IsRefused(byte entryBits)
    {
        Refused(Build(1, 1, 1, 8, 0x20, [0], new byte[8], mapLength: 1, mapEntryBits: entryBits));
    }

    [Fact]
    public void ColorMappedImage_WithoutAMap_IsRefused()
    {
        Refused(Build(1, 1, 1, 8, 0x20, [0]));
        Refused(Build(9, 1, 1, 8, 0x20, Run(1, 0)));
    }

    [Fact]
    public void ColorMapType_Above1_IsRefused()
    {
        Refused(Build(2, 1, 1, 24, 0x20, [1, 2, 3], mapType: 2));
    }

    [Fact]
    public void ColorIndex_OutsideTheMap_IsRefused()
    {
        // Magick refuses an index above the map as well ("invalid colormap index").
        byte[] map = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte[] tga = Build(1, 3, 1, 8, 0x20, [2, 1, 3], map, mapLength: 3, mapEntryBits: 24);
        Should.Throw<Exception>(() => MagickRead(tga));
        Refused(tga).Message.ShouldContain("outside the colour map");
        Refused(Build(9, 3, 1, 8, 0x20, Concat(Run(2, 1), Run(1, 200)), map, mapLength: 3, mapEntryBits: 24));
    }

    [Fact]
    public void ColorIndex_WithAnEmptyMap_IsRefused()
    {
        Refused(Build(1, 1, 1, 8, 0x20, [0], [], mapLength: 0, mapEntryBits: 24, mapType: 1));
    }

    [Theory]
    [InlineData((byte)0x40)]
    [InlineData((byte)0x80)]
    [InlineData((byte)0xC0)]
    public void InterleavedImages_AreRefused(byte interleave)
    {
        // ImageMagick ignores these bits and reads the rows in order, which is wrong for an interleaved file.
        Refused(Build(2, 1, 2, 24, (byte)(0x20 | interleave), new byte[6])).Message.ShouldContain("interleave");
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    public void EmptyImage_IsRefused(int w, int h)
    {
        Refused(Build(2, w, h, 24, 0x20, []));
    }

    [Theory]
    [InlineData(8193, 8192)]
    [InlineData(65535, 65535)]
    [InlineData(65535, 1025)]
    public void DeclaredSizeAboveTheCeiling_IsRefusedFromTheHeader(int w, int h)
    {
        TgaReader.MaximumPixels.ShouldBe(1L << 26);
        // Header only: nothing follows, so a decoder that allocated first would have to allocate 17 GiB to find out.
        byte[] tga = Build(2, w, h, 32, 0x20, []);
        Refused(tga).Message.ShouldContain("limit");
        TgaReader.TryReadInfo(tga, out _).ShouldBeFalse();
    }

    [Fact]
    public void ASizeAtTheCeiling_IsAcceptedByTheHeader()
    {
        // 8192 x 8192 = 2^26 exactly.
        byte[] tga = Build(2, 8192, 8192, 32, 0x20, []);
        TgaReader.TryReadInfo(tga, out TgaInfo info).ShouldBeTrue();
        info.Width.ShouldBe(8192);
        info.Height.ShouldBe(8192);
    }

    [Theory]
    [InlineData((byte)2)]  // raw
    [InlineData((byte)10)] // RLE
    public void AShortFileDeclaringAHugeImage_AllocatesNothingBig(byte type)
    {
        // At the ceiling the raster would be 256 MiB; the file is 19 bytes. The size check comes first.
        byte[] tga = Build(type, 8192, 8192, 32, 0x20, [0x00]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Refused(tga);
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(1 << 20);
    }
}
