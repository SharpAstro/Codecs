using SharpAstro.Tga;
using Shouldly;
using static SharpAstro.Codecs.Tests.TgaTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The variants Magick.NET cannot write (15/16-bit pixels, 8/15/16/32-bit map entries, attribute-bit alpha, the
/// right-to-left bit, hand-placed RLE packets) are built byte by byte here and compared with ImageMagick's READ of
/// the same bytes. Where ImageMagick reads a variant differently from the specification, the test says so and says
/// which side the code follows; none of those is silently matched.
/// </summary>
public sealed class TgaHandBuiltTests
{
    [Theory]
    [InlineData((byte)16, (byte)0x20, false)] // 16-bit, no attribute bits: opaque
    [InlineData((byte)16, (byte)0x21, true)]  // 16-bit, 1 attribute bit: bit 15 is alpha
    [InlineData((byte)16, (byte)0x28, true)]  // attribute count is only tested for "any", as in ImageMagick
    [InlineData((byte)15, (byte)0x20, false)]
    // 15-bit pixels with an attribute bit declared: the spec has no bit for it to be (15 bits of colour), yet
    // ImageMagick reads bit 15 as alpha, exactly as for 16-bit. The code follows ImageMagick (one rule for a
    // 16-bit word), since the header contradicts itself and there is no better reading.
    [InlineData((byte)15, (byte)0x21, true)]
    public void EveryFiveFiveFiveWord_MatchesMagick(byte depth, byte descriptor, bool alpha)
    {
        // 256x256: pixel i is the 16-bit word i, so every colour and both alpha states are present.
        var px = new byte[65536 * 2];
        for (int i = 0; i < 65536; i++)
        {
            px[i * 2] = (byte)i;
            px[i * 2 + 1] = (byte)(i >> 8);
        }

        byte[] tga = Build(2, 256, 256, depth, descriptor, px);
        TgaImage image = AssertSameAsMagick(tga, $"depth {depth} descriptor {descriptor:X2}");
        image.HasAlpha.ShouldBe(alpha);
    }

    [Theory]
    [InlineData((byte)0x20)] // no attribute bits declared
    [InlineData((byte)0x28)] // 8 declared: the spec case
    [InlineData((byte)0x21)] // something else
    public void ThirtyTwoBit_KeepsItsAlphaByte_WhateverTheDescriptorSays(byte descriptor)
    {
        // The spec ties alpha to the descriptor's attribute-bit count, so attribute count 0 would mean "no alpha".
        // ImageMagick (like stb_image) keeps the stored alpha byte of a 32-bit pixel regardless; the code follows
        // them, because writers do emit 32-bit files with a clear count and a real alpha channel.
        byte[] px = [1, 2, 3, 0, 4, 5, 6, 255, 7, 8, 9, 128, 10, 11, 12, 77];
        TgaImage image = AssertSameAsMagick(Build(2, 4, 1, 32, descriptor, px), $"descriptor {descriptor:X2}");
        image.HasAlpha.ShouldBeTrue();
        image.Rgba.ShouldBe(new byte[] { 3, 2, 1, 0, 6, 5, 4, 255, 9, 8, 7, 128, 12, 11, 10, 77 });
    }

    [Fact]
    public void TwentyFourBit_WithAttributeBitsDeclared_IsStillOpaque()
    {
        TgaImage image = AssertSameAsMagick(Build(2, 2, 1, 24, 0x28, [1, 2, 3, 4, 5, 6]), "24-bit attr 8");
        image.HasAlpha.ShouldBeFalse();
        image.Rgba.ShouldBe(new byte[] { 3, 2, 1, 255, 6, 5, 4, 255 });
    }

    [Theory]
    [InlineData((byte)8, (byte)0x20)]
    [InlineData((byte)15, (byte)0x20)]
    [InlineData((byte)15, (byte)0x21)]
    [InlineData((byte)16, (byte)0x20)]
    [InlineData((byte)16, (byte)0x21)]
    [InlineData((byte)16, (byte)0x28)]
    [InlineData((byte)24, (byte)0x20)]
    [InlineData((byte)24, (byte)0x28)]
    [InlineData((byte)32, (byte)0x20)]
    [InlineData((byte)32, (byte)0x28)]
    public void ColorMap_EveryEntrySizeAndAlphaDeclaration_MatchesMagick(byte entryBits, byte descriptor)
    {
        // Random entries, so 16-bit entries have bit 15 both set and clear. Only 32-bit entries carry alpha: a
        // 15/16-bit entry is read opaque even when the descriptor declares attribute bits (ImageMagick does the
        // same, and the spec's attribute count describes pixels, of which a colour index has none).
        const int entries = 256;
        int entryBytes = (entryBits + 7) / 8;
        var map = new byte[entries * entryBytes];
        var rng = new Random(entryBits * 31 + descriptor);
        rng.NextBytes(map);

        // 16x16 image naming every entry once, in a scrambled order.
        var indices = new byte[entries];
        for (int i = 0; i < entries; i++)
        {
            indices[i] = (byte)((i * 37 + 11) & 0xFF);
        }

        byte[] tga = Build(1, 16, 16, 8, descriptor, indices, map, mapFirst: 0, mapLength: entries, mapEntryBits: entryBits);
        TgaImage image = AssertSameAsMagick(tga, $"map {entryBits}-bit descriptor {descriptor:X2}");
        image.Info.ColorMapEntryBits.ShouldBe(entryBits);
    }

    [Fact]
    public void ColorMap_FirstEntryIndex_OffsetsTheIndices()
    {
        // Spec: pixel value v names entry v - first. ImageMagick agrees for values inside the map.
        byte[] map = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte[] tga = Build(1, 3, 1, 8, 0x20, [5, 4, 3], map, mapFirst: 3, mapLength: 3, mapEntryBits: 24);
        TgaImage image = AssertSameAsMagick(tga, "first entry 3");
        image.Rgba.ShouldBe(new byte[] { 9, 8, 7, 255, 6, 5, 4, 255, 3, 2, 1, 255 });
    }

    [Fact]
    public void ColorMap_IndexBelowTheFirstEntry_IsRefused_WhereMagickGivesBlack()
    {
        // ImageMagick reads an index below the map's first entry as black without complaint. The code follows
        // the specification, which names no colour for it, and refuses.
        byte[] map = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte[] tga = Build(1, 3, 1, 8, 0x20, [0, 4, 3], map, mapFirst: 3, mapLength: 3, mapEntryBits: 24);
        MagickRead(tga).Rgba.Take(4).ShouldBe(new byte[] { 0, 0, 0, 255 });
        Should.Throw<InvalidDataException>(() => TgaReader.Decode(tga));
    }

    [Fact]
    public void ColorMap_PresentInATrueColorImage_IsSkipped_WhereMagickIndexesIt()
    {
        // The spec says a map in a true-colour file is only a hint for hardware with fewer colours; the pixels
        // are the 24 bits stored. ImageMagick instead treats the pixel as an index into the map (it reads this
        // file as map entry 1, 06 05 04). The code follows the specification.
        byte[] map = [1, 2, 3, 4, 5, 6];
        byte[] tga = Build(2, 1, 1, 24, 0x20, [7, 8, 9], map, mapLength: 2, mapEntryBits: 24);
        MagickRead(tga).Rgba.ShouldBe(new byte[] { 6, 5, 4, 255 });
        TgaReader.Decode(tga).Rgba.ShouldBe(new byte[] { 9, 8, 7, 255 });
    }

    [Fact]
    public void ColorMap_PresentInAGrayImage_IsSkipped_WhereMagickRefuses()
    {
        // ImageMagick fails such a file ("invalid colormap index"). The spec allows a map in any image type
        // (it is an optimisation hint outside type 1/9), and the code skips it as it does for true colour.
        byte[] tga = Build(3, 2, 1, 8, 0x20, [10, 200], [1, 2, 3, 4, 5, 6], mapLength: 2, mapEntryBits: 24);
        Should.Throw<Exception>(() => MagickRead(tga));
        TgaReader.Decode(tga).Rgba.ShouldBe(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 });
    }

    [Fact]
    public void Gray_RawAndRle_MatchMagick()
    {
        byte[] ramp = Enumerable.Range(0, 40).Select(i => (byte)(i * 6 + 1)).ToArray();
        AssertSameAsMagick(Build(3, 8, 5, 8, 0x20, ramp), "grey raw");
        byte[] rle = Concat(Raw(8, ramp[..8]), Run(24, 77), Raw(8, ramp[8..16]));
        AssertSameAsMagick(Build(11, 8, 5, 8, 0x20, rle), "grey rle");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void EveryOrigin_RawAndRle_MatchesMagick_AndMovesPixelsAsTheDescriptorSays(bool top, bool rtl, bool rle)
    {
        // 5x3, every pixel distinct: pixel n is (n, 2n, 3n) in RGB.
        const int w = 5, h = 3;
        var bgr = new List<byte>();
        for (int n = 0; n < w * h; n++)
        {
            bgr.AddRange([(byte)(3 * n), (byte)(2 * n), (byte)n]);
        }

        byte[] body = rle ? Concat(Raw(7, [.. bgr.Take(21)]), Raw(8, [.. bgr.Skip(21)])) : [.. bgr];
        byte descriptor = (byte)((top ? 0x20 : 0) | (rtl ? 0x10 : 0));
        byte[] tga = Build(rle ? (byte)10 : (byte)2, w, h, 24, descriptor, body);
        TgaImage image = AssertSameAsMagick(tga, $"top={top} rtl={rtl} rle={rle}");
        image.Info.TopOrigin.ShouldBe(top);
        image.Info.RightToLeft.ShouldBe(rtl);

        // And against the specification directly: stored pixel n lands at (col, row) as the bits say.
        for (int n = 0; n < w * h; n++)
        {
            int x = n % w, y = n / w;
            int col = rtl ? w - 1 - x : x;
            int row = top ? y : h - 1 - y;
            int o = (row * w + col) * 4;
            image.Rgba.AsSpan(o, 4).ToArray().ShouldBe(new[] { (byte)n, (byte)(2 * n), (byte)(3 * n), (byte)255 });
        }
    }

    [Fact]
    public void Rle_PacketsMayCrossRowEnds_AndCoverTheWholeImage()
    {
        // 3x3 = 9 pixels as: run of 5 (crosses the first row end), raw of 3, run of 1.
        byte[] body = Concat(Run(5, 1, 2, 3), Raw(3, 10, 11, 12, 20, 21, 22, 30, 31, 32), Run(1, 99, 98, 97));
        AssertSameAsMagick(Build(10, 3, 3, 24, 0x20, body), "packets across rows");
    }

    [Fact]
    public void Rle_FullSizePackets_MatchMagick()
    {
        // 128 is the longest packet either kind can hold: 16x16 = 2 runs of 128, then 1 raw of 128 + 1 run of 128.
        var raw = new byte[128 * 3];
        new Random(5).NextBytes(raw);
        AssertSameAsMagick(Build(10, 16, 16, 24, 0x20, Concat(Run(128, 1, 2, 3), Run(128, 4, 5, 6))), "two 128 runs");
        AssertSameAsMagick(Build(10, 16, 16, 24, 0x20, Concat(Raw(128, raw), Run(128, 9, 9, 9))), "128 raw + 128 run");
    }

    [Theory]
    [InlineData((byte)16, (byte)0x21)]
    [InlineData((byte)32, (byte)0x28)]
    [InlineData((byte)15, (byte)0x20)]
    public void Rle_AtEveryPixelSize_MatchesMagick(byte depth, byte descriptor)
    {
        int bpp = (depth + 7) / 8;
        var pixelA = Enumerable.Range(0, bpp).Select(i => (byte)(0x13 + i * 0x31)).ToArray();
        var pixelB = Enumerable.Range(0, bpp).Select(i => (byte)(0xE0 - i * 0x1B)).ToArray();
        byte[] body = Concat(Run(4, pixelA), Raw(2, [.. pixelB, .. pixelA]), Run(2, pixelB));
        AssertSameAsMagick(Build(10, 4, 2, depth, descriptor, body), $"RLE {depth}-bit");
    }

    [Fact]
    public void Rle_ColorMapped_MatchesMagick()
    {
        byte[] map = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte[] body = Concat(Run(3, 2), Raw(3, 0, 1, 2));
        AssertSameAsMagick(Build(9, 3, 2, 8, 0x20, body, map, mapLength: 3, mapEntryBits: 24), "RLE palette");
    }

    [Fact]
    public void ImageId_IsSkipped()
    {
        byte[] tga = Build(2, 2, 1, 24, 0x20, [1, 2, 3, 4, 5, 6], idLength: 37);
        AssertSameAsMagick(tga, "37-byte image ID").Rgba.ShouldBe(new byte[] { 3, 2, 1, 255, 6, 5, 4, 255 });
    }

    [Fact]
    public void TheV2Footer_AndAnyOtherTrailingBytes_AreIgnored()
    {
        // TGA 2.0 appends a footer ("TRUEVISION-XFILE.\0", preceded by two offsets) after the pixel data.
        byte[] footer = [.. new byte[8], .. "TRUEVISION-XFILE."u8.ToArray(), 0];
        byte[] tga = [.. Build(2, 1, 1, 24, 0x20, [1, 2, 3]), .. footer];
        AssertSameAsMagick(tga, "footer").Rgba.ShouldBe(new byte[] { 3, 2, 1, 255 });
    }

    [Fact]
    public void TryReadInfo_ReportsTheHeader()
    {
        byte[] tga = Build(10, 7, 3, 32, 0x38, Run(21, 1, 2, 3, 4));
        TgaReader.TryReadInfo(tga, out TgaInfo info).ShouldBeTrue();
        info.ShouldBe(new TgaInfo(7, 3, TgaImageType.RleTrueColor, 32, 0, HasAlpha: true, TopOrigin: true, RightToLeft: true));
        info.IsRle.ShouldBeTrue();
        info.IsGrayscale.ShouldBeFalse();
    }
}
