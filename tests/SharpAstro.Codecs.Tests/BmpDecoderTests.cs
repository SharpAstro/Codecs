using ImageMagick;
using SharpAstro.Bmp;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="BmpDecoder"/> against ImageMagick, pixel by pixel and exact. Magick writes the files it can
/// (OS/2 core, BMP3, V5; 1/4/8-bit palettes, RLE8, 16-bit 5-5-5 / 5-6-5 / 4-4-4-4 / 1-5-5-5, 24 and 32 bit with
/// alpha), BmpDib hand-builds the rest, and in both cases the expected pixels are what Magick reads from the
/// same bytes. Where the source raster survives the format (every lossless depth) it is checked too, so the
/// oracle itself cannot drift unseen.
/// </summary>
public sealed class BmpDecoderTests
{
    private const int Height = 21;

    // ------------------------------------------------------------ helpers

    /// <summary>A raster with <paramref name="colors"/> distinct colours (0 = a gradient of many).</summary>
    private static byte[] MakeRaster(int w, int h, bool alpha, int colors = 0)
    {
        int ch = alpha ? 4 : 3;
        var px = new byte[w * h * ch];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * ch;
                int c = colors == 0 ? y * w + x : (x * 3 + y * 5) % colors;
                px[i] = (byte)(c * 37 + 11);
                px[i + 1] = (byte)(c * 91 + 5);
                px[i + 2] = (byte)(c * 53 + 200);
                if (alpha) px[i + 3] = (byte)(c * 29 + 3);
            }
        }
        return px;
    }

    private static byte[] MagickWrite(byte[] raster, int w, int h, bool alpha, MagickFormat format,
        Action<MagickImage>? configure = null, params (string Name, string Value)[] defines)
    {
        using var img = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        img.Alpha(alpha ? AlphaOption.Set : AlphaOption.Off);
        img.ImportPixels(raster, new PixelImportSettings((uint)w, (uint)h, StorageType.Char,
            alpha ? PixelMapping.RGBA : PixelMapping.RGB));
        foreach (var (name, value) in defines)
            img.Settings.SetDefine(name, value);
        configure?.Invoke(img);
        using var ms = new MemoryStream();
        img.Write(ms, format);
        return ms.ToArray();
    }

    private static (int Width, int Height, int Channels, byte[] Pixels) MagickRead(byte[] file)
    {
        using var img = new MagickImage(file);
        bool alpha = img.HasAlpha;
        using var pixels = img.GetPixels();
        var bytes = pixels.ToByteArray(0, 0, img.Width, img.Height, alpha ? PixelMapping.RGBA : PixelMapping.RGB)!;
        return ((int)img.Width, (int)img.Height, alpha ? 4 : 3, bytes);
    }

    private static void ShouldMatch(byte[] expected, int channels, BmpImage actual, string what, int tolerance = 0)
    {
        actual.Pixels.Length.ShouldBe(expected.Length, what);
        for (int i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - actual.Pixels[i]) > tolerance)
            {
                int pixel = i / channels;
                Assert.Fail($"{what}: pixel ({pixel % actual.Width}, {pixel / actual.Width}) channel {i % channels}: " +
                            $"expected {expected[i]}, decoded {actual.Pixels[i]}");
            }
        }
    }

    /// <summary>
    /// Decodes with us and with Magick and demands the same size, channels and every sample, exactly (see
    /// <see cref="ShouldEqualReferenceAndMagick"/> for masked channels narrower than 8 bits, where ImageMagick's
    /// own expansion has quirks that are pinned rather than copied).
    /// </summary>
    private static BmpImage ShouldEqualMagick(byte[] file, string what)
    {
        var ours = BmpDecoder.Decode(file);
        var (w, h, ch, px) = MagickRead(file);
        ours.Width.ShouldBe(w, what);
        ours.Height.ShouldBe(h, what);
        ours.Channels.ShouldBe(ch, what);
        ShouldMatch(px, ch, ours, what);
        BmpDecoder.ReadInfo(file).Width.ShouldBe(w, what);
        return ours;
    }

    /// <summary>
    /// 16- and 32-bit masked pixels, all EXACT. Ours must equal <see cref="OurExpansion"/> (stb_image's bit
    /// replication up to 8 bits, ImageMagick's rounding above) applied to the raw words read out of the file,
    /// and ImageMagick's read must equal <see cref="MagickExpansion"/> applied to the same words. Where the two
    /// expansions agree, which is every 8-bit and every wider channel and all but a pinned handful of 5- and
    /// 6-bit values, that makes ours and ImageMagick's pixels identical; where they do not, the difference is
    /// spelled out value by value in <see cref="MagickExpansion"/> rather than allowed by a tolerance.
    /// </summary>
    private static BmpImage ShouldEqualReferenceAndMagick(byte[] file, uint[] masks, string what)
    {
        var ours = BmpDecoder.Decode(file);
        ShouldMatch(ReferenceMasked(file, masks, OurExpansion), ours.Channels, ours, what + " (reference)");
        var (w, h, ch, px) = MagickRead(file);
        ours.Width.ShouldBe(w, what);
        ours.Height.ShouldBe(h, what);
        ours.Channels.ShouldBe(ch, what);
        var magickModel = ReferenceMasked(file, masks, MagickExpansion);
        px.Length.ShouldBe(magickModel.Length, what);
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i] != magickModel[i])
            {
                int pixel = i / ch;
                Assert.Fail($"{what} (ImageMagick's own read): pixel ({pixel % w}, {pixel / w}) channel {i % ch}: " +
                            $"MagickExpansion says {magickModel[i]}, ImageMagick read {px[i]}");
            }
        }
        return ours;
    }

    /// <summary>
    /// What we do with an <paramref name="bits"/>-bit channel value, written independently of the decoder:
    /// bit replication up to 8 bits (stb_image's <c>stbi__shiftsigned</c>), so a 5-bit 24 is 198 and a full
    /// channel of any width is 255; above 8 bits, round to nearest from the value left-aligned in 16 bits.
    /// </summary>
    private static int OurExpansion(uint v, int bits, int bpp, int channel)
    {
        if (bits <= 8)
        {
            // Spell the replication out bit by bit: output bit 7-i is value bit (bits-1 - i % bits).
            int r = 0;
            for (int i = 0; i < 8; i++)
                r |= (int)((v >> (bits - 1 - i % bits)) & 1) << (7 - i);
            return r;
        }
        uint x = bits <= 16 ? v << (16 - bits) : v >> (bits - 16);
        return (int)Math.Round(x / 257.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// ImageMagick's own expansion, measured value by value (Magick.NET Q16-HDRI). It agrees with
    /// <see cref="OurExpansion"/> on every 8-bit channel and every channel wider than 8 bits. Below 8 bits:
    /// <list type="bullet">
    /// <item>16-bit pixels, 5-bit channels: bit replication except 3 → 25 and 7 → 58 (replication gives 24, 57).</item>
    /// <item>16-bit pixels, a 6-bit GREEN channel: replication except the eight values below. A 6-bit red or blue
    /// is not replicated, only shifted.</item>
    /// <item>16-bit pixels, any other width: shifted, not replicated (a 4-bit 15 is 240, a 1-bit alpha of 1 is 128).</item>
    /// <item>32-bit pixels: no replication at all; the value left-aligned in 16 bits and rounded (a 2-bit 3 is 191).</item>
    /// </list>
    /// These are ImageMagick's and are not copied; each is pinned here so the oracle cannot drift unseen.
    /// </summary>
    private static int MagickExpansion(uint v, int bits, int bpp, int channel)
    {
        if (bits == 8) return (int)v;
        if (bpp == 16 && bits < 8)
        {
            if (bits == 5)
                return v switch { 3 => 25, 7 => 58, _ => OurExpansion(v, 5, bpp, channel) };
            if (bits == 6 && channel == 1)
                return v switch
                {
                    9 => 37, 11 => 45, 12 => 49, 13 => 53, 14 => 57, 15 => 61, 25 => 102, 27 => 110,
                    _ => OurExpansion(v, 6, bpp, channel),
                };
            return (int)(v << (8 - bits));
        }
        uint x = bits <= 16 ? v << (16 - bits) : v >> (bits - 16);
        return (int)Math.Round(x / 257.0, MidpointRounding.AwayFromZero);
    }

    private static byte[] ReferenceMasked(byte[] file, uint[] masks, Func<uint, int, int, int, int> expand)
    {
        int offBits = (int)BitConverter.ToUInt32(file, 10);
        int w = BitConverter.ToInt32(file, 18), hRaw = BitConverter.ToInt32(file, 22);
        int bpp = BitConverter.ToUInt16(file, 28);
        bool topDown = hRaw < 0;
        int h = Math.Abs(hRaw);
        int ch = masks[3] != 0 ? 4 : 3;
        int stride = (w * bpp + 31) / 32 * 4;
        var result = new byte[w * h * ch];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int at = offBits + (topDown ? y : h - 1 - y) * stride + x * (bpp / 8);
                ulong px = bpp == 16 ? BitConverter.ToUInt16(file, at) : BitConverter.ToUInt32(file, at);
                for (int c = 0; c < ch; c++)
                {
                    uint m = masks[c];
                    int shift = 0;
                    while (((m >> shift) & 1) == 0) shift++;
                    uint max = m >> shift;
                    int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount(max);
                    result[(y * w + x) * ch + c] = (byte)expand((uint)((px & m) >> shift), bits, bpp, c);
                }
            }
        }
        return result;
    }

    /// <summary>The masks a 16/32-bit BI_BITFIELDS file carries: in the header from 52 bytes on, else after it.</summary>
    private static uint[] ReadMasks(byte[] file)
    {
        uint hsz = BitConverter.ToUInt32(file, 14);
        uint compression = BitConverter.ToUInt32(file, 30);
        if (compression == 0)
            return [0x7C00, 0x03E0, 0x001F, 0];
        int at = hsz >= 52 ? 14 + 40 : 14 + (int)hsz;
        uint alpha = hsz >= 56 || compression == 6 ? BitConverter.ToUInt32(file, at + 12) : 0;
        return [BitConverter.ToUInt32(file, at), BitConverter.ToUInt32(file, at + 4), BitConverter.ToUInt32(file, at + 8), alpha];
    }


    // ------------------------------------------------------------ written by ImageMagick

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void Bmp2_OS2Core_24Bit(int w)
    {
        var raster = MakeRaster(w, Height, alpha: false);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp2);
        BmpDecoder.ReadInfo(file).HeaderKind.ShouldBe(BmpHeaderKind.Os2Core);
        ShouldMatch(raster, 3, ShouldEqualMagick(file, "BMP2 24"), "BMP2 24 vs source");
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    [InlineData(5, 16)]
    [InlineData(17, 16)]
    [InlineData(1, 100)]
    [InlineData(3, 100)]
    [InlineData(5, 100)]
    [InlineData(17, 100)]
    public void Bmp2_OS2Core_Indexed(int w, int colors)
    {
        var raster = MakeRaster(w, Height, alpha: false, colors);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp2, i => i.ColorType = ColorType.Palette);
        var info = BmpDecoder.ReadInfo(file);
        info.HeaderKind.ShouldBe(BmpHeaderKind.Os2Core);
        info.BitsPerPixel.ShouldBe(ExpectedIndexedDepth(raster, 3));
        ShouldMatch(raster, 3, ShouldEqualMagick(file, "BMP2 indexed"), "BMP2 indexed vs source");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void Bmp3_24Bit(int w)
    {
        var raster = MakeRaster(w, Height, alpha: false);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp3);
        var info = BmpDecoder.ReadInfo(file);
        info.HeaderKind.ShouldBe(BmpHeaderKind.Info);
        info.BitsPerPixel.ShouldBe(24);
        info.Compression.ShouldBe(BmpCompression.Rgb);
        info.TopDown.ShouldBeFalse();
        ShouldMatch(raster, 3, ShouldEqualMagick(file, "BMP3 24"), "BMP3 24 vs source");
    }

    [Theory]
    [InlineData(1, 2, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(5, 2, 1)]
    [InlineData(17, 2, 1)]
    [InlineData(1, 16, 4)]
    [InlineData(3, 16, 4)]
    [InlineData(5, 16, 4)]
    [InlineData(17, 16, 4)]
    [InlineData(1, 100, 8)]
    [InlineData(3, 100, 8)]
    [InlineData(5, 100, 8)]
    [InlineData(17, 100, 8)]
    public void Bmp3_Indexed_1_4_8(int w, int colors, int expectedDepth)
    {
        var raster = MakeRaster(w, Height, alpha: false, colors);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp3, i => i.ColorType = ColorType.Palette);
        BmpDecoder.ReadInfo(file).BitsPerPixel.ShouldBe(expectedDepth);
        ExpectedIndexedDepth(raster, 3).ShouldBe(expectedDepth);
        ShouldMatchSourceAndMagick(file, raster, w, $"BMP3 {expectedDepth}-bit");
    }

    /// <summary>
    /// Lossless files: we must reproduce the source raster, and ImageMagick's own read of the file. The one
    /// exception is RLE8 one pixel wide: ImageMagick's writer encodes each scanline's three padding bytes as
    /// pixels, and its reader spills them into the rows above, so it misreads its own file. There the source
    /// raster alone is the oracle.
    /// </summary>
    private static void ShouldMatchSourceAndMagick(byte[] file, byte[] raster, int w, string what)
    {
        ShouldMatch(raster, 3, BmpDecoder.Decode(file), what + " vs source");
        if (w != 1 || BmpDecoder.ReadInfo(file).Compression != BmpCompression.Rle8)
            ShouldEqualMagick(file, what);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void Bmp3_Rle8(int w)
    {
        var raster = MakeRaster(w, Height, alpha: false, colors: 7);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp3,
            i =>
            {
                i.ColorType = ColorType.Palette;
                i.Settings.Compression = CompressionMethod.RLE;
            });
        var info = BmpDecoder.ReadInfo(file);
        info.Compression.ShouldBe(BmpCompression.Rle8);
        info.BitsPerPixel.ShouldBe(8);
        ShouldMatchSourceAndMagick(file, raster, w, "BMP3 RLE8");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void Rle8_RunsAndAbsoluteRuns_FromImageMagick(int w)
    {
        // Many colours make Magick emit absolute runs; few make it emit encoded runs. Both in one file.
        var raster = MakeRaster(w, Height, alpha: false, colors: 90);
        for (int y = 0; y < Height; y += 3)
            for (int x = 0; x < w; x++)
                raster[(y * w + x) * 3] = raster[(y * w + x) * 3 + 1] = raster[(y * w + x) * 3 + 2] = 77;
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp3,
            i =>
            {
                i.ColorType = ColorType.Palette;
                i.Settings.Compression = CompressionMethod.RLE;
            });
        BmpDecoder.ReadInfo(file).Compression.ShouldBe(BmpCompression.Rle8);
        ShouldMatchSourceAndMagick(file, raster, w, "RLE8 mixed");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void V5_24Bit_NoAlpha(int w)
    {
        var raster = MakeRaster(w, Height, alpha: false);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp);
        var info = BmpDecoder.ReadInfo(file);
        info.HeaderKind.ShouldBe(BmpHeaderKind.V5);
        info.BitsPerPixel.ShouldBe(24);
        info.Channels.ShouldBe(3);
        ShouldMatch(raster, 3, ShouldEqualMagick(file, "V5 24"), "V5 24 vs source");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void V5_32Bit_BitfieldsWithAlpha(int w)
    {
        var raster = MakeRaster(w, Height, alpha: true);
        var file = MagickWrite(raster, w, Height, true, MagickFormat.Bmp);
        var info = BmpDecoder.ReadInfo(file);
        info.HeaderKind.ShouldBe(BmpHeaderKind.V5);
        info.BitsPerPixel.ShouldBe(32);
        info.Compression.ShouldBe(BmpCompression.Bitfields);
        info.Channels.ShouldBe(4);
        ShouldMatch(raster, 4, ShouldEqualMagick(file, "V5 32 alpha"), "V5 32 alpha vs source");
    }

    [Theory]
    [InlineData(1, "RGB555")]
    [InlineData(3, "RGB555")]
    [InlineData(5, "RGB555")]
    [InlineData(17, "RGB555")]
    [InlineData(1, "RGB565")]
    [InlineData(3, "RGB565")]
    [InlineData(5, "RGB565")]
    [InlineData(17, "RGB565")]
    public void V5_16Bit_RgbSubtypes(int w, string subtype)
    {
        var raster = MakeRaster(w, Height, alpha: false);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp, null, ("bmp:subtype", subtype));
        var info = BmpDecoder.ReadInfo(file);
        info.BitsPerPixel.ShouldBe(16);
        info.Compression.ShouldBe(BmpCompression.Bitfields);
        info.Channels.ShouldBe(3);
        ShouldEqualReferenceAndMagick(file, ReadMasks(file), $"V5 {subtype}");
    }

    [Theory]
    [InlineData(1, "ARGB4444")]
    [InlineData(3, "ARGB4444")]
    [InlineData(5, "ARGB4444")]
    [InlineData(17, "ARGB4444")]
    [InlineData(1, "ARGB1555")]
    [InlineData(3, "ARGB1555")]
    [InlineData(5, "ARGB1555")]
    [InlineData(17, "ARGB1555")]
    public void V5_16Bit_AlphaSubtypes(int w, string subtype)
    {
        var raster = MakeRaster(w, Height, alpha: true);
        var file = MagickWrite(raster, w, Height, true, MagickFormat.Bmp, null, ("bmp:subtype", subtype));
        var info = BmpDecoder.ReadInfo(file);
        info.BitsPerPixel.ShouldBe(16);
        info.Channels.ShouldBe(4);
        var masks = ReadMasks(file);
        ShouldEqualReferenceAndMagick(file, masks, $"V5 {subtype}");
    }

    [Theory]
    [InlineData(1, "RGB555")]
    [InlineData(3, "RGB555")]
    [InlineData(5, "RGB565")]
    [InlineData(17, "RGB565")]
    public void Bmp3_16Bit_FromImageMagick_IsMalformed_AndRefused(int w, string subtype)
    {
        // ImageMagick's BMP3 + bmp:subtype writes BI_BITFIELDS but never the three mask words: the pixel data
        // starts right after the 40-byte header, so a reader that follows the format reads pixels as masks
        // (and ImageMagick itself decodes its own file to garbage). There is no right answer to match; the
        // file is refused, not guessed at.
        var raster = MakeRaster(w, Height, alpha: false);
        var file = MagickWrite(raster, w, Height, false, MagickFormat.Bmp3, null, ("bmp:subtype", subtype));
        BitConverter.ToUInt32(file, 30).ShouldBe(3u);
        BitConverter.ToUInt32(file, 10).ShouldBe(54u);
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file));
        BmpDecoder.TryReadInfo(file, out _).ShouldBeFalse();
    }

    private static int ExpectedIndexedDepth(byte[] raster, int channels)
    {
        var distinct = new HashSet<int>();
        for (int i = 0; i < raster.Length; i += channels)
            distinct.Add(raster[i] | raster[i + 1] << 8 | raster[i + 2] << 16);
        return distinct.Count <= 2 ? 1 : distinct.Count <= 16 ? 4 : 8;
    }

    // ------------------------------------------------------------ top-down: Magick reads, we match

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void TopDown_24Bit_IsTheSamePictureAsBottomUp(int w)
    {
        var raster = MakeRaster(w, Height, alpha: false);
        var bottomUp = new BmpDib
        {
            Width = w, Height = Height, BitsPerPixel = 24,
            Pixels = BmpDib.PackRows(w, Height, 24, false, (x, y) => Pack24(raster, w, x, y)),
        }.Build();
        var topDown = new BmpDib
        {
            Width = w, Height = -Height, BitsPerPixel = 24,
            Pixels = BmpDib.PackRows(w, Height, 24, true, (x, y) => Pack24(raster, w, x, y)),
        }.Build();

        ShouldMatch(raster, 3, ShouldEqualMagick(bottomUp, "bottom-up"), "bottom-up vs source");
        var td = ShouldEqualMagick(topDown, "top-down");
        ShouldMatch(raster, 3, td, "top-down vs source");
        td.Info.TopDown.ShouldBeTrue();
        BmpDecoder.ReadInfo(topDown).Height.ShouldBe(Height);
    }

    private static uint Pack24(byte[] rgb, int w, int x, int y)
    {
        int i = (y * w + x) * 3;
        return (uint)(rgb[i + 2] | rgb[i + 1] << 8 | rgb[i] << 16);
    }

    // ------------------------------------------------------------ hand-built, read by Magick for the expected pixels

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 1, true)]
    [InlineData(5, 1, false)]
    [InlineData(17, 1, true)]
    [InlineData(1, 4, true)]
    [InlineData(3, 4, false)]
    [InlineData(5, 4, true)]
    [InlineData(17, 4, false)]
    [InlineData(1, 8, false)]
    [InlineData(3, 8, true)]
    [InlineData(5, 8, false)]
    [InlineData(17, 8, true)]
    public void Indexed_BiRgb_WithPaletteOfFullSize(int w, int bpp, bool topDown)
    {
        int entries = 1 << bpp;
        var file = new BmpDib
        {
            Width = w, Height = topDown ? -Height : Height, BitsPerPixel = bpp,
            Palette = BmpDib.MakePalette(entries),
            Pixels = BmpDib.PackRows(w, Height, bpp, topDown, (x, y) => (uint)((x * 5 + y * 3) % entries)),
        }.Build();
        ShouldEqualMagick(file, $"{bpp}-bit indexed, top-down {topDown}");
    }

    [Theory]
    [InlineData(3, 4, 5)]
    [InlineData(17, 4, 11)]
    [InlineData(5, 8, 20)]
    [InlineData(17, 8, 200)]
    [InlineData(5, 1, 2)]
    public void Indexed_WithBiClrUsed_SmallerThanTheDepth(int w, int bpp, int clrUsed)
    {
        var file = new BmpDib
        {
            Width = w, Height = Height, BitsPerPixel = bpp, ClrUsed = (uint)clrUsed,
            Palette = BmpDib.MakePalette(clrUsed),
            Pixels = BmpDib.PackRows(w, Height, bpp, false, (x, y) => (uint)((x * 5 + y * 3) % clrUsed)),
        }.Build();
        ShouldEqualMagick(file, $"{bpp}-bit, biClrUsed {clrUsed}");
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(5, 4)]
    [InlineData(17, 8)]
    [InlineData(1, 24)]
    [InlineData(3, 24)]
    [InlineData(17, 24)]
    public void Os2Core_HandBuilt(int w, int bpp)
    {
        int entries = bpp == 24 ? 0 : 1 << bpp;
        var file = new BmpDib
        {
            HeaderSize = 12, Width = w, Height = Height, BitsPerPixel = bpp,
            Palette = BmpDib.MakePalette(entries, core: true),
            Pixels = BmpDib.PackRows(w, Height, bpp, false,
                (x, y) => bpp == 24 ? (uint)((x * 40 + y * 9) | (y * 70 + x) << 8 | (x * 11 + y * 13) << 16) : (uint)((x * 5 + y * 3) % entries)),
        }.Build();
        var info = BmpDecoder.ReadInfo(file);
        info.HeaderKind.ShouldBe(BmpHeaderKind.Os2Core);
        info.BitsPerPixel.ShouldBe(bpp);
        ShouldEqualMagick(file, $"OS/2 core {bpp}-bit");
    }

    [Theory]
    [InlineData(1, 40)]
    [InlineData(3, 40)]
    [InlineData(5, 108)]
    [InlineData(17, 108)]
    [InlineData(3, 124)]
    [InlineData(17, 124)]
    public void Sixteen_BiRgb_Is555_WhateverTheHeader(int w, int headerSize)
    {
        // Distinct 5-bit values in every channel, plus bit 15 set on some pixels (it is ignored).
        var file = new BmpDib
        {
            HeaderSize = headerSize, Width = w, Height = Height, BitsPerPixel = 16,
            // A V4/V5 header's masks are not used unless BI_BITFIELDS says so: leave junk there.
            Masks = headerSize > 40 ? [0xF800, 0x07E0, 0x001F, 0] : null,
            Pixels = BmpDib.PackRows(w, Height, 16, false,
                (x, y) => (uint)(((x * 7 + y) & 31) << 10 | ((x * 3 + y * 5) & 31) << 5 | ((x + y * 11) & 31) | ((x + y) % 4 == 0 ? 0x8000 : 0))),
        }.Build();
        BmpDecoder.ReadInfo(file).Channels.ShouldBe(3);
        ShouldEqualReferenceAndMagick(file, [0x7C00, 0x03E0, 0x001F, 0], $"16-bit BI_RGB, header {headerSize}");
    }

    public static TheoryData<string, int, uint[], int> MaskLayouts => new()
    {
        // name, bits per pixel, masks (r, g, b, a), header size
        { "565", 16, new uint[] { 0xF800, 0x07E0, 0x001F, 0 }, 40 },
        { "565 in a V4 header", 16, new uint[] { 0xF800, 0x07E0, 0x001F, 0 }, 108 },
        { "555 in a V5 header", 16, new uint[] { 0x7C00, 0x03E0, 0x001F, 0 }, 124 },
        { "4444 with alpha", 16, new uint[] { 0x0F00, 0x00F0, 0x000F, 0xF000 }, 108 },
        { "1555 with alpha", 16, new uint[] { 0x7C00, 0x03E0, 0x001F, 0x8000 }, 124 },
        { "BGR order 565", 16, new uint[] { 0x001F, 0x07E0, 0xF800, 0 }, 40 },
        { "8-8-8 in 32 bits", 32, new uint[] { 0x00FF0000, 0x0000FF00, 0x000000FF, 0 }, 40 },
        { "8-8-8-8 ARGB", 32, new uint[] { 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 }, 108 },
        { "8-8-8-8 RGBA order", 32, new uint[] { 0xFF000000, 0x00FF0000, 0x0000FF00, 0x000000FF }, 124 },
        { "8-8-8-8 ABGR order", 32, new uint[] { 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000 }, 124 },
        { "10-10-10", 32, new uint[] { 0x3FF00000, 0x000FFC00, 0x000003FF, 0 }, 40 },
        { "10-10-10-2 with alpha", 32, new uint[] { 0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000 }, 124 },
        { "11-11-10", 32, new uint[] { 0xFFE00000, 0x001FFC00, 0x000003FF, 0 }, 40 },
        { "16-bit red only high bits", 16, new uint[] { 0xFF00, 0x00F0, 0x000F, 0 }, 40 },
        { "gaps between channels", 32, new uint[] { 0x00FF0000, 0x00000FF0, 0x0000000F, 0 }, 40 },
    };

    [Theory]
    [MemberData(nameof(MaskLayouts))]
    public void Bitfields_AnyMaskLayout_IsScaledLikeImageMagick(string name, int bpp, uint[] masks, int headerSize)
    {
        foreach (int w in new[] { 1, 3, 5, 17 })
        {
            var rnd = new Random(w * 31 + bpp);
            var file = new BmpDib
            {
                HeaderSize = headerSize, Width = w, Height = Height, BitsPerPixel = bpp,
                Compression = 3, Masks = masks,
                Pixels = BmpDib.PackRows(w, Height, bpp, false, (x, y) =>
                {
                    uint v = (uint)rnd.NextInt64(0, bpp == 16 ? 0x10000 : 0x1_0000_0000);
                    // Walk the extremes of every channel through the first pixels.
                    if (x == 0 && y < 4) v = y switch { 0 => 0, 1 => uint.MaxValue, 2 => 0x55555555, _ => 0xAAAAAAAA };
                    return bpp == 16 ? v & 0xFFFF : v;
                }),
            }.Build();
            var info = BmpDecoder.ReadInfo(file);
            info.Channels.ShouldBe(masks[3] != 0 ? 4 : 3, name);
            ShouldEqualReferenceAndMagick(file, masks, $"{name}, width {w}");
        }
    }

    /// <summary>A 256x256 16-bit BI_BITFIELDS file holding every one of the 65536 pixel words once.</summary>
    private static byte[] EveryWord16(uint[] masks) => new BmpDib
    {
        HeaderSize = 124, Width = 256, Height = 256, BitsPerPixel = 16, Compression = 3, Masks = masks,
        Pixels = BmpDib.PackRows(256, 256, 16, false, (x, y) => (uint)(y * 256 + x)),
    }.Build();

    [Theory]
    [InlineData("555", new uint[] { 0x7C00, 0x03E0, 0x001F, 0 })]
    [InlineData("565", new uint[] { 0xF800, 0x07E0, 0x001F, 0 })]
    [InlineData("BGR 565", new uint[] { 0x001F, 0x07E0, 0xF800, 0 })]
    [InlineData("1555", new uint[] { 0x7C00, 0x03E0, 0x001F, 0x8000 })]
    [InlineData("4444", new uint[] { 0x0F00, 0x00F0, 0x000F, 0xF000 })]
    public void Sixteen_EveryPixelWord_IsExact(string name, uint[] masks) =>
        ShouldEqualReferenceAndMagick(EveryWord16(masks), masks, $"every word, {name}");

    /// <summary>
    /// The 5-6-5 and 5-5-5 cases stated the other way round from <see cref="MagickExpansion"/>: over every 5- and
    /// 6-bit value our pixels are ImageMagick's, tolerance 0, except on exactly these values, where ImageMagick
    /// departs from bit replication (and so from stb_image) by one level.
    /// </summary>
    [Theory]
    [InlineData("555", new uint[] { 0x7C00, 0x03E0, 0x001F, 0 })]
    [InlineData("565", new uint[] { 0xF800, 0x07E0, 0x001F, 0 })]
    public void Sixteen_555And565_EqualImageMagick_ButForAPinnedHandful(string name, uint[] masks)
    {
        uint[] fiveBitExceptions = [3, 7];
        uint[] greenSixBitExceptions = [9, 11, 12, 13, 14, 15, 25, 27];
        var file = EveryWord16(masks);
        var ours = BmpDecoder.Decode(file);
        var (_, _, ch, px) = MagickRead(file);
        ch.ShouldBe(3);
        int differing = 0;
        for (int i = 0; i < px.Length; i++)
        {
            int c = i % 3;
            int pixel = i / 3;
            // Bottom-up file: image row y holds the words y * 256 .. y * 256 + 255 as PackRows laid them out.
            uint word = (uint)pixel;
            uint m = masks[c];
            uint v = (word & m) >> System.Numerics.BitOperations.TrailingZeroCount(m);
            bool excepted = System.Numerics.BitOperations.PopCount(m) == 6
                ? greenSixBitExceptions.Contains(v)
                : fiveBitExceptions.Contains(v);
            if (excepted)
            {
                Math.Abs(ours.Pixels[i] - px[i]).ShouldBe(1, $"{name}: {v} in channel {c} is a pinned exception");
                differing++;
            }
            else if (ours.Pixels[i] != px[i])
            {
                Assert.Fail($"{name}: word 0x{word:X4} channel {c} (value {v}): ImageMagick {px[i]}, decoded {ours.Pixels[i]}");
            }
        }
        differing.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void FullChannel_Is255_AtEveryMaskWidth()
    {
        for (int bits = 1; bits <= 24; bits++)
        {
            uint red = ((1u << bits) - 1) << 8;
            var file = new BmpDib
            {
                Width = 2, Height = 1, BitsPerPixel = 32, Compression = 3, Masks = [red, 0, 0xFF, 0],
                Pixels = BmpDib.PackRows(2, 1, 32, false, (x, _) => x == 0 ? red : 0),
            }.Build();
            var px = BmpDecoder.Decode(file).Pixels;
            px[0].ShouldBe((byte)255, $"{bits}-bit full red");
            px[3].ShouldBe((byte)0, $"{bits}-bit zero red");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void ThirtyTwoBit_BiRgb_WithA40ByteHeader_HasAlpha(int w)
    {
        var file = new BmpDib
        {
            Width = w, Height = Height, BitsPerPixel = 32,
            Pixels = BmpDib.PackRows(w, Height, 32, false,
                (x, y) => (uint)((x * 40 + 5) | (y * 11 + 9) << 8 | (x + y * 7) << 16 | ((x * 13 + y * 29 + 1) & 0xFF) << 24)),
        }.Build();
        var info = BmpDecoder.ReadInfo(file);
        info.Channels.ShouldBe(4);
        ShouldEqualMagick(file, "32-bit BI_RGB");
    }

    [Theory]
    [InlineData(108)]
    [InlineData(124)]
    public void ThirtyTwoBit_BiRgb_InAV4OrV5Header_IsOpaque(int headerSize)
    {
        // The masks only count under BI_BITFIELDS; ImageMagick agrees and reads three channels.
        var file = new BmpDib
        {
            HeaderSize = headerSize, Width = 5, Height = Height, BitsPerPixel = 32,
            Masks = [0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000],
            Pixels = BmpDib.PackRows(5, Height, 32, false, (x, y) => (uint)((x * 40 + 5) | (y * 11 + 9) << 8 | (x + y * 7) << 16 | 0x80 << 24)),
        }.Build();
        BmpDecoder.ReadInfo(file).Channels.ShouldBe(3);
        ShouldEqualMagick(file, $"32-bit BI_RGB, header {headerSize}");
    }

    [Fact]
    public void ThirtyTwoBit_BiRgb_WithAllZeroFourthByte_IsOpaque_UnlikeImageMagick()
    {
        // X8R8G8B8 writers leave the fourth byte zero. That is not transparency: we (like stb_image) read
        // the file as opaque RGBA, and ImageMagick likewise finds no alpha channel in it (3 channels), with
        // the same colours.
        var file = new BmpDib
        {
            Width = 5, Height = 4, BitsPerPixel = 32,
            Pixels = BmpDib.PackRows(5, 4, 32, false, (x, y) => (uint)((x * 40 + 5) | (y * 11 + 9) << 8 | (x + y * 7) << 16)),
        }.Build();
        var ours = BmpDecoder.Decode(file);
        ours.Channels.ShouldBe(4);
        var (_, _, ch, magick) = MagickRead(file);
        ch.ShouldBe(3);
        for (int p = 0; p < 5 * 4; p++)
        {
            ours.Pixels[p * 4 + 3].ShouldBe((byte)255);
            for (int c = 0; c < 3; c++) ours.Pixels[p * 4 + c].ShouldBe(magick[p * 3 + c]);
        }
    }

    [Fact]
    public void AlphaBitfields_WithAFourthMaskAfterTheHeader()
    {
        var file = new BmpDib
        {
            Width = 5, Height = Height, BitsPerPixel = 32, Compression = 6,
            Masks = [0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000], FourMasksAfterHeader = true,
            Pixels = BmpDib.PackRows(5, Height, 32, false, (x, y) => (uint)((x * 40 + 5) | (y * 11 + 9) << 8 | (x + y * 7) << 16 | ((x * 13 + y * 29 + 1) & 0xFF) << 24)),
        }.Build();
        var ours = BmpDecoder.Decode(file);
        ours.Channels.ShouldBe(4);
        ours.Info.Compression.ShouldBe(BmpCompression.AlphaBitfields);
        // ImageMagick does not read BI_ALPHABITFIELDS, so the expected pixels are worked out by hand.
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                int i = (y * 5 + x) * 4;
                ours.Pixels[i].ShouldBe((byte)(x + y * 7));
                ours.Pixels[i + 1].ShouldBe((byte)(y * 11 + 9));
                ours.Pixels[i + 2].ShouldBe((byte)(x * 40 + 5));
                ours.Pixels[i + 3].ShouldBe((byte)((x * 13 + y * 29 + 1) & 0xFF));
            }
        }
    }

    // ------------------------------------------------------------ RLE: hand-built streams Magick also reads

    private static byte[] Rle(int w, int h, int bpp, byte[] stream, int entries = 16, int headerSize = 40, int heightSign = 1) =>
        new BmpDib
        {
            HeaderSize = headerSize, Width = w, Height = h * heightSign, BitsPerPixel = bpp,
            Compression = bpp == 8 ? 1u : 2u, ClrUsed = (uint)entries,
            Palette = BmpDib.MakePalette(entries), Pixels = stream,
        }.Build();

    [Fact]
    public void Rle8_EncodedRuns_EndOfLine_EndOfBitmap()
    {
        var file = Rle(4, 3, 8, [4, 1, 0, 0, 2, 2, 2, 3, 0, 0, 4, 0, 0, 1]);
        ShouldEqualMagick(file, "RLE8 runs");
    }

    [Fact]
    public void Rle8_NoEndOfBitmapAfterTheLastRow_IsComplete()
    {
        // ImageMagick refuses this stream (it wants the end-of-bitmap marker), so the reference is the same
        // picture with the marker.
        var bare = Rle(4, 3, 8, [4, 1, 0, 0, 2, 2, 2, 3, 0, 0, 4, 0]);
        var marked = Rle(4, 3, 8, [4, 1, 0, 0, 2, 2, 2, 3, 0, 0, 4, 0, 0, 1]);
        var picture = ShouldEqualMagick(marked, "RLE8 with EOB");
        BmpDecoder.Decode(bare).Pixels.ShouldBe(picture.Pixels);
    }

    [Fact]
    public void Rle8_AbsoluteRuns_EvenAndOddLengthsWithPadding()
    {
        // 4 pixels (even, no pad) then 3 pixels + 1 pad byte + a run.
        var file = Rle(4, 3, 8, [0, 4, 1, 2, 3, 0, 0, 0, 4, 3, 0, 0, 0, 3, 0, 1, 2, 0, 1, 1, 0, 1]);
        ShouldEqualMagick(file, "RLE8 absolute");
    }

    [Fact]
    public void Rle8_Delta_LeavesPaletteEntryZeroBehind()
    {
        var file = Rle(4, 3, 8, [2, 1, 0, 2, 1, 1, 1, 2, 0, 0, 4, 1, 0, 1]);
        ShouldEqualMagick(file, "RLE8 delta");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void Rle4_EncodedRuns_AlternateNibbles(int w)
    {
        // A run of n nibble pairs per row: counts and values chosen so odd and even lengths both occur.
        var stream = new List<byte>();
        for (int row = 0; row < 3; row++)
        {
            stream.AddRange([(byte)w, (byte)(0x12 + row * 0x22), 0, 0]);
        }
        stream[^1] = 1;
        var file = Rle(w, 3, 4, stream.ToArray());
        ShouldEqualMagick(file, $"RLE4 runs, width {w}");
    }

    [Fact]
    public void Rle4_AbsoluteRuns_OfEveryLengthAndPadding()
    {
        // Lengths 3..8 cover 1 and 2 data bytes, with and without a pad byte.
        for (int n = 3; n <= 8; n++)
        {
            int w = n;
            var dataBytes = (n + 1) / 2;
            var row = new List<byte> { 0, (byte)n };
            for (int i = 0; i < dataBytes; i++) row.Add((byte)(((i * 2 + 1) << 4) | ((i * 2 + 2) & 15)));
            if (dataBytes % 2 == 1) row.Add(0);
            row.AddRange([0, 0]);
            var stream = new List<byte>();
            for (int r = 0; r < 2; r++) stream.AddRange(row);
            stream[^1] = 1;
            var file = Rle(w, 2, 4, stream.ToArray());
            ShouldEqualMagick(file, $"RLE4 absolute run of {n}");
        }
    }

    [Fact]
    public void Rle4_Delta_LeavesPaletteEntryZeroBehind()
    {
        // Row 0: 2 pixels, delta right 1 down 1, 1 pixel, end of line, then a full row.
        var file = Rle(4, 3, 4, [2, 0x31, 0, 2, 1, 1, 1, 0x20, 0, 0, 4, 0x45, 0, 1]);
        ShouldEqualMagick(file, "RLE4 delta");
    }

    // ------------------------------------------------------------ what Magick cannot read or write: ours only

    [Fact]
    public void Rle8_TopDown_PlacesRowsFromTheTop()
    {
        var stream = new byte[] { 4, 1, 0, 0, 4, 2, 0, 1 };
        var bottomUp = BmpDecoder.Decode(Rle(4, 2, 8, stream));
        var topDown = BmpDecoder.Decode(Rle(4, 2, 8, stream, heightSign: -1));
        topDown.Info.TopDown.ShouldBeTrue();
        // The first row of the stream is the bottom row of a bottom-up file and the top row of a top-down one.
        topDown.Pixels.AsSpan(0, 12).SequenceEqual(bottomUp.Pixels.AsSpan(12, 12)).ShouldBeTrue();
        topDown.Pixels.AsSpan(12, 12).SequenceEqual(bottomUp.Pixels.AsSpan(0, 12)).ShouldBeTrue();
    }

    [Fact]
    public void Rle_UnwrittenPixels_TakePaletteEntryZero()
    {
        var file = Rle(4, 2, 8, [4, 2, 0, 1]); // one row only, then end of bitmap
        var image = BmpDecoder.Decode(file);
        var palette = BmpDib.MakePalette(16);
        // File row 0 is the bottom row; the top row was never written.
        for (int x = 0; x < 4; x++)
        {
            image.Pixels[x * 3].ShouldBe(palette[2]);
            image.Pixels[x * 3 + 1].ShouldBe(palette[1]);
            image.Pixels[x * 3 + 2].ShouldBe(palette[0]);
            image.Pixels[12 + x * 3].ShouldBe(palette[10]);
        }
    }

    [Fact]
    public void V5_EmbeddedIccProfile_IsReported()
    {
        var profile = new byte[300];
        new Random(7).NextBytes(profile);
        var file = new BmpDib
        {
            HeaderSize = 124, Width = 3, Height = 2, BitsPerPixel = 24, IccProfile = profile,
            Pixels = BmpDib.PackRows(3, 2, 24, false, (x, y) => (uint)(x * 50 + y * 90)),
        }.Build();

        var info = BmpDecoder.ReadInfo(file);
        info.IccProfile.ShouldNotBeNull();
        info.IccProfile!.ShouldBe(profile);
        var image = BmpDecoder.Decode(file);
        image.IccProfile.ShouldBe(profile);
        image.ToRasterImage().IccProfile.ToArray().ShouldBe(profile);
        // The profile after the pixels is not mistaken for pixel data, and ImageMagick agrees on the pixels.
        ShouldEqualMagick(file, "V5 with ICC profile");
    }

    [Theory]
    [InlineData(0x4C494E4Bu)] // 'LINK': a file name, not a profile
    [InlineData(0x73524742u)] // 'sRGB'
    [InlineData(0u)]          // LCS_CALIBRATED_RGB
    public void V5_NonEmbeddedColourSpace_HasNoProfile(uint colorSpace)
    {
        var file = new BmpDib
        {
            HeaderSize = 124, Width = 3, Height = 2, BitsPerPixel = 24, ColorSpaceType = colorSpace == 0 ? 0 : colorSpace,
            Pixels = BmpDib.PackRows(3, 2, 24, false, (x, y) => (uint)(x * 50 + y * 90)),
        }.Build();
        BmpDecoder.ReadInfo(file).IccProfile.ShouldBeNull();
    }

    [Theory]
    [InlineData(4000u, 10u)]         // starts beyond the file
    [InlineData(124u, 0xFFFFFFF0u)]  // size overflows
    [InlineData(10u, 20u)]           // starts inside the header
    public void V5_IccProfileOutsideTheFile_IsRefused(uint offset, uint size)
    {
        var file = new BmpDib
        {
            HeaderSize = 124, Width = 3, Height = 2, BitsPerPixel = 24, IccProfile = new byte[40],
            ProfileOffsetOverride = offset, ProfileSizeOverride = size,
            Pixels = BmpDib.PackRows(3, 2, 24, false, (x, y) => (uint)(x * 50 + y * 90)),
        }.Build();
        Should.Throw<InvalidDataException>(() => BmpDecoder.ReadInfo(file));
        BmpDecoder.TryReadInfo(file, out _).ShouldBeFalse();
    }

    [Fact]
    public void Info_ReportsHeaderKindDepthCompressionAndRowOrder()
    {
        var file = new BmpDib
        {
            HeaderSize = 108, Width = 5, Height = -3, BitsPerPixel = 32, Compression = 3,
            Masks = [0xFF0000, 0xFF00, 0xFF, 0xFF000000],
            Pixels = BmpDib.PackRows(5, 3, 32, true, (x, y) => 0x80112233),
        }.Build();
        BmpDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.Width.ShouldBe(5);
        info.Height.ShouldBe(3);
        info.Channels.ShouldBe(4);
        info.HeaderKind.ShouldBe(BmpHeaderKind.V4);
        info.BitsPerPixel.ShouldBe(32);
        info.Compression.ShouldBe(BmpCompression.Bitfields);
        info.TopDown.ShouldBeTrue();
        info.IccProfile.ShouldBeNull();
        info.ToImageInfo().ShouldBe(new SharpAstro.Codecs.Abstractions.ImageInfo(5, 3, 4, SharpAstro.Codecs.Abstractions.SampleFormat.UInt8));
        BmpDecoder.HasBmpSignature(file).ShouldBeTrue();
        BmpDecoder.HasBmpSignature(file.AsSpan(0, 20)).ShouldBeFalse();
        BmpDecoder.HasBmpSignature("BM not really a bitmap at all"u8).ShouldBeFalse();
    }

    // ------------------------------------------------------------ refusals

    [Fact]
    public void BiJpeg_IsRefusedByName()
    {
        var file = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 0, Compression = 4, Pixels = new byte[16] }.Build();
        Should.Throw<NotSupportedException>(() => BmpDecoder.Decode(file)).Message.ShouldContain("BI_JPEG");
        Should.Throw<NotSupportedException>(() => BmpDecoder.ReadInfo(file)).Message.ShouldContain("BI_JPEG");
        BmpDecoder.TryDecode(file, out _).ShouldBeFalse();
    }

    [Fact]
    public void BiPng_IsRefusedByName()
    {
        var file = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 0, Compression = 5, Pixels = new byte[16] }.Build();
        Should.Throw<NotSupportedException>(() => BmpDecoder.Decode(file)).Message.ShouldContain("BI_PNG");
    }

    [Fact]
    public void Os2Huffman1D_IsRefusedByName()
    {
        foreach (int headerSize in new[] { 40, 64 })
        {
            var file = new BmpDib
            {
                HeaderSize = headerSize, Width = 8, Height = 2, BitsPerPixel = 1, Compression = 3,
                Palette = BmpDib.MakePalette(2), Pixels = new byte[8],
            }.Build();
            Should.Throw<NotSupportedException>(() => BmpDecoder.Decode(file)).Message.ShouldContain("Huffman 1D");
        }
    }

    [Fact]
    public void Os2Rle24_AndBiCmyk_AreRefusedByName()
    {
        var rle24 = new BmpDib { HeaderSize = 64, Width = 2, Height = 2, BitsPerPixel = 24, Compression = 4, Pixels = new byte[16] }.Build();
        Should.Throw<NotSupportedException>(() => BmpDecoder.Decode(rle24)).Message.ShouldContain("RLE24");
        var cmyk = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 32, Compression = 11, Pixels = new byte[16] }.Build();
        Should.Throw<NotSupportedException>(() => BmpDecoder.Decode(cmyk)).Message.ShouldContain("BI_CMYK");
    }

    // ------------------------------------------------------------ hardening

    [Fact]
    public void NotABmp_IsInvalidData()
    {
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode([]));
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode("PNG not a bmp file at all, it is far too long"u8));
        BmpDecoder.TryDecode("BM"u8, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BadWidth_IsInvalidData(int width)
    {
        var file = new BmpDib { Width = width, Height = 2, BitsPerPixel = 24, Pixels = new byte[64] }.Build();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public void BadHeight_IsInvalidData(int height)
    {
        var file = new BmpDib { Width = 2, Height = height, BitsPerPixel = 24, Pixels = new byte[64] }.Build();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(48)]
    public void BadBitsPerPixel_IsInvalidData(int bpp)
    {
        var file = new BmpDib { Width = 2, Height = 2, BitsPerPixel = bpp, Pixels = new byte[64] }.Build();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file));
    }

    [Fact]
    public void RleWithTheWrongDepth_IsInvalidData()
    {
        var file = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 24, Compression = 1, Pixels = new byte[16] }.Build();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file));
    }

    [Fact]
    public void UnknownHeaderSize_AndUnknownCompression_AreInvalidData()
    {
        var file = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 24, Pixels = new byte[16] }.Build();
        var oddHeader = (byte[])file.Clone();
        oddHeader[14] = 41;
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(oddHeader));
        var oddCompression = (byte[])file.Clone();
        oddCompression[30] = 9;
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(oddCompression));
    }

    [Fact]
    public void Masks_AllZero_OrWiderThanThePixel_AreInvalidData()
    {
        var zero = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 16, Compression = 3, Masks = [0, 0, 0], Pixels = new byte[8] }.Build();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(zero));
        var wide = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 16, Compression = 3, Masks = [0xF800, 0x07E0, 0x1_001F], Pixels = new byte[8] }.Build();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(wide));
    }

    [Fact]
    public void PixelOffset_InsideTheHeader_OrBeyondTheFile_IsInvalidData()
    {
        var good = new BmpDib { Width = 2, Height = 2, BitsPerPixel = 24, Pixels = new byte[16] };
        good.PixelOffsetOverride = 20;
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(good.Build()));
        good.PixelOffsetOverride = 5000;
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(good.Build()));
    }

    [Theory]
    [InlineData(100_000, 100_000)]
    [InlineData(16385, 16384)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, -300_000_000)]
    public void DeclaredSizeAbovePixelLimit_IsRefusedBeforeAllocating(int width, int height)
    {
        var file = new BmpDib { Width = width, Height = height, BitsPerPixel = 24, Pixels = new byte[16] }.Build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file));
        ex.Message.ShouldContain("limit");
        BmpDecoder.TryReadInfo(file, out _).ShouldBeFalse();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(1 << 20);
    }

    [Fact]
    public void DeclaredSizeAtThePixelLimit_ButNoData_IsTruncated_NotAllocated()
    {
        // 16384 x 16384 is exactly 2^28: legal to declare, but 1 GiB of rows are nowhere in a 70-byte file.
        var file = new BmpDib { Width = 16384, Height = 16384, BitsPerPixel = 32, Pixels = new byte[16] }.Build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file)).Message.ShouldContain("truncated");
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(1 << 20);
    }

    [Fact]
    public void RleDeclaredAbovePixelLimit_IsRefused()
    {
        var file = Rle(20000, 20000, 8, [0, 1]);
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file)).Message.ShouldContain("limit");
    }

    public static IEnumerable<object[]> WholeFiles()
    {
        yield return ["24-bit", new BmpDib { Width = 5, Height = 4, BitsPerPixel = 24, Pixels = BmpDib.PackRows(5, 4, 24, false, (x, y) => (uint)(x * 9 + y * 70)) }.Build()];
        yield return ["8-bit", new BmpDib { Width = 5, Height = 4, BitsPerPixel = 8, Palette = BmpDib.MakePalette(256), Pixels = BmpDib.PackRows(5, 4, 8, false, (x, y) => (uint)(x * 9 + y)) }.Build()];
        yield return ["4-bit top-down", new BmpDib { Width = 5, Height = -4, BitsPerPixel = 4, Palette = BmpDib.MakePalette(16), Pixels = BmpDib.PackRows(5, 4, 4, true, (x, y) => (uint)(x + y)) }.Build()];
        yield return ["1-bit", new BmpDib { Width = 17, Height = 4, BitsPerPixel = 1, Palette = BmpDib.MakePalette(2), Pixels = BmpDib.PackRows(17, 4, 1, false, (x, y) => (uint)(x + y)) }.Build()];
        yield return ["565", new BmpDib { Width = 5, Height = 4, BitsPerPixel = 16, Compression = 3, Masks = [0xF800, 0x07E0, 0x1F], Pixels = BmpDib.PackRows(5, 4, 16, false, (x, y) => (uint)(x * 999 + y)) }.Build()];
        yield return ["32 alpha V5", new BmpDib { HeaderSize = 124, Width = 5, Height = 4, BitsPerPixel = 32, Compression = 3, Masks = [0xFF0000, 0xFF00, 0xFF, 0xFF000000], IccProfile = new byte[30], Pixels = BmpDib.PackRows(5, 4, 32, false, (x, y) => (uint)(x * 99999 + y)) }.Build()];
        yield return ["core", new BmpDib { HeaderSize = 12, Width = 5, Height = 4, BitsPerPixel = 8, Palette = BmpDib.MakePalette(256, core: true), Pixels = BmpDib.PackRows(5, 4, 8, false, (x, y) => (uint)(x + y)) }.Build()];
    }

    [Theory]
    [MemberData(nameof(WholeFiles))]
    public void EveryTruncationOfAnUncompressedFile_IsInvalidData(string name, byte[] file)
    {
        _ = BmpDecoder.Decode(file); // the whole file is fine
        // Anything shorter than the pixels need is refused; only a V5 file's trailing profile may be cut,
        // and then the profile itself no longer fits.
        int pixelsEnd = BmpDecoder.ReadInfo(file).IccProfile is null ? file.Length : file.Length - 30;
        for (int length = 0; length < pixelsEnd; length++)
        {
            var ex = Record.Exception(() => BmpDecoder.Decode(file.AsSpan(0, length)));
            ex.ShouldBeOfType<InvalidDataException>($"{name} cut at {length}");
        }
        for (int length = pixelsEnd; length < file.Length; length++)
            Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file.AsSpan(0, length)), $"{name} cut at {length}");
    }

    [Fact]
    public void TruncatedRle_IsInvalidData_UnlessItEndedOnTheLastPixel()
    {
        var file = Rle(4, 3, 8, [4, 1, 0, 0, 2, 2, 2, 3, 0, 0, 4, 0, 0, 1]);
        int dataStart = file.Length - 14;
        for (int length = 0; length < dataStart + 12; length++)
            Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file.AsSpan(0, length)), $"cut at {length}");
        // Cutting after the last row's pixels (no EOL, no EOB) is a complete image; a dangling byte is not.
        BmpDecoder.Decode(file.AsSpan(0, dataStart + 12)).Height.ShouldBe(3);
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(file.AsSpan(0, dataStart + 13)).Height.ToString());
    }

    [Fact]
    public void Rle_RunLeavingItsRow_AbsoluteRunPastTheData_OrExtraRows_AreInvalidData()
    {
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 3, 8, [5, 1, 0, 1])));          // run wider than the row
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 3, 8, [0, 9, 1, 2, 3])));        // absolute run past the data
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 3, 8, [0, 2, 9, 9, 0, 1])));     // delta leaving the image
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 1, 8, [4, 1, 0, 0, 4, 1, 0, 1]))); // second row of a one-row image
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 1, 8, [0, 0, 0, 0, 0, 0, 0, 1])));  // more end-of-lines than rows
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 3, 4, [9, 0x11, 0, 1])));      // past the row's padded storage
        BmpDecoder.Decode(Rle(3, 3, 8, [4, 1, 0, 0, 3, 2, 0, 0, 3, 3, 0, 1])).Height.ShouldBe(3); // overshoot into the stride padding is dropped
    }

    [Fact]
    public void Rle_StreamsThatNeverFinish_AreBoundedByTheirInput()
    {
        // A megabyte of zero-length deltas stands still in the picture but not in the input.
        var stream = new byte[1 << 20];
        for (int i = 0; i < stream.Length; i += 4) { stream[i] = 0; stream[i + 1] = 2; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 3, 8, stream)));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        var eol = new byte[1 << 20]; // all end-of-lines
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(Rle(4, 3, 8, eol)));
    }

    [Fact]
    public void PaletteBeyondThePixelOffset_ReadsAsTheEntriesThatFit_AndBlackBeyond()
    {
        // biClrUsed claims 256 entries but the pixel data starts after 3: the table is what fits.
        var file = new BmpDib
        {
            Width = 4, Height = 1, BitsPerPixel = 8, ClrUsed = 256,
            Palette = BmpDib.MakePalette(3),
            Pixels = [0, 1, 2, 200],
        }.Build();
        var image = BmpDecoder.Decode(file);
        var pal = BmpDib.MakePalette(3);
        image.Pixels[3].ShouldBe(pal[4 + 2]);
        image.Pixels[9].ShouldBe((byte)0);
        image.Pixels[10].ShouldBe((byte)0);
        image.Pixels[11].ShouldBe((byte)0);
    }

    [Fact]
    public void RandomCorruptions_OnlyEverThrowTheDocumentedExceptions()
    {
        var files = WholeFiles().Select(o => (byte[])o[1]).ToList();
        files.Add(Rle(4, 3, 8, [4, 1, 0, 0, 2, 2, 2, 3, 0, 0, 0, 3, 5, 6, 7, 0, 0, 1]));
        files.Add(Rle(5, 3, 4, [5, 0x12, 0, 0, 0, 5, 0x12, 0x34, 0x50, 0, 0, 2, 1, 1, 3, 0x21, 0, 1]));

        var rnd = new Random(0x424D50);
        foreach (var source in files)
        {
            for (int n = 0; n < 1500; n++)
            {
                var copy = (byte[])source.Clone();
                int flips = 1 + rnd.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    // Keep the dimension fields tame: a corrupted RLE width would legitimately allocate 700 MB.
                    int at = rnd.Next(copy.Length);
                    if (at is >= 18 and < 26) at += 8;
                    copy[at] = (byte)rnd.Next(256);
                }
                try
                {
                    var image = BmpDecoder.Decode(copy);
                    image.Pixels.Length.ShouldBe(image.Width * image.Height * image.Channels);
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                }
            }
        }
    }
}
