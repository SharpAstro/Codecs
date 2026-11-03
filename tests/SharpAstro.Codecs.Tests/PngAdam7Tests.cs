using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Adam7 interlaced PNGs decode into the same row-major samples as the non-interlaced encoding of the same raster.
/// The oracle is libpng through Magick.NET: one random raster is encoded twice, <see cref="Interlace.Png"/> and
/// <see cref="Interlace.NoInterlace"/>, and the interlaced file's IHDR is checked to say InterlaceMethod 1 so a test
/// cannot pass on a file that is not interlaced after all. The sizes include ones that leave passes empty, whose
/// filter bytes are then absent from the stream (a 1x1 image is pass 1 alone).
/// </summary>
public sealed class PngAdam7Tests
{
    // 1x1: pass 1 only. 2x3, 5x1: most passes empty. 7x9, 33x17: odd sizes, every pass populated and ragged.
    // 8x8: one pixel per pass-1 cell, the exact block. 1x9: one column, so only the passes that start at column 0.
    private static readonly (int Width, int Height)[] Sizes = [(1, 1), (2, 3), (5, 1), (7, 9), (33, 17), (8, 8), (1, 9)];

    // Every (colour type, depth) the reader covers.
    private static readonly (int ColorType, int Depth)[] Formats =
    [
        (0, 1), (0, 2), (0, 4), (0, 8), (0, 16),
        (2, 8), (2, 16),
        (3, 1), (3, 2), (3, 4), (3, 8),
        (4, 8), (4, 16),
        (6, 8), (6, 16),
    ];

    // The seven Adam7 passes, restated here from PNG section 8.2 rather than shared with the reader:
    // first column, first row, column step, row step.
    private static readonly (int X0, int Y0, int Dx, int Dy)[] Passes =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    public static TheoryData<int, int, int, int> Rasters()
    {
        var data = new TheoryData<int, int, int, int>();
        foreach (var (colorType, depth) in Formats)
        {
            foreach (var (width, height) in Sizes)
            {
                data.Add(colorType, depth, width, height);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Rasters))]
    public void AnInterlacedFileDecodesAsItsNonInterlacedEncoding(int colorType, int depth, int width, int height)
    {
        using var image = MakeImage(colorType, depth, width, height);
        var interlaced = Encode(image, Interlace.Png);
        var plain = Encode(image, Interlace.NoInterlace);

        // What Magick emitted, not what was asked for: the test is about these files and nothing else.
        Ihdr(interlaced).ShouldBe((width, height, depth, colorType, 1));
        Ihdr(plain).ShouldBe((width, height, depth, colorType, 0));
        // The stream holds exactly the passes the spec derives from the size, no more and no fewer.
        InflatedLength(interlaced).ShouldBe(ExpectedInflatedLength(width, height, colorType, depth, interlaced: true));

        var decoded = PngReader.Decode(interlaced);
        var expected = PngReader.Decode(plain);

        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);
        decoded.BitDepth.ShouldBe(expected.BitDepth);
        decoded.SourceBitDepth.ShouldBe(depth);
        decoded.ColorType.ShouldBe(colorType);
        decoded.Palette.ShouldBe(expected.Palette);
        decoded.PaletteAlpha.ShouldBe(expected.PaletteAlpha);
        decoded.Pixels.ShouldBe(expected.Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryFilterTypeIsUnfilteredAgainstTheRowAboveInItsOwnPass(int filter)
    {
        // Up, Average and Paeth read the row above, which in an interlaced image is the previous row of the SAME pass
        // (zeros for a pass's first row); Sub and Average read the pixel to the left, a whole pixel at the pass's
        // own pitch. Forcing one filter type for every row leaves a wrong "above" or "left" nowhere to hide.
        foreach (var (colorType, depth) in new[] { (6, 8), (2, 16), (0, 2), (3, 4), (4, 8), (0, 16) })
        {
            using var image = MakeImage(colorType, depth, 33, 17);
            // ImageMagick's PNG quality is two digits: the tens digit is the filter type (0 none .. 4 Paeth, 5 adaptive),
            // the units digit the zlib level. (The png:compression-filter define does not take: every row stays None.)
            image.Quality = (uint)(filter * 10 + 9);
            var interlaced = Encode(image, Interlace.Png);
            var plain = Encode(image, Interlace.NoInterlace);

            // The quality must have taken: a forced filter that libpng ignored would test nothing. One exception, observed
            // rather than assumed: for a sub-byte depth libpng never writes Paeth (it came back as a mix of None, Sub, Up
            // and Average whatever was asked), so that case compares the decodes only. Paeth at a sub-byte depth is covered
            // by AnIndexedImageUsingEveryPaletteEntry, whose files are built by hand with all five filters.
            if (!(depth < 8 && filter == 4))
            {
                FilterTypesUsed(interlaced).ShouldContain((byte)filter, $"colour type {colorType}, {depth}-bit");
            }

            PngReader.Decode(interlaced).Pixels.ShouldBe(PngReader.Decode(plain).Pixels, $"colour type {colorType}, {depth}-bit, filter {filter}");
        }
    }

    [Theory]
    [InlineData(6, 8)]
    [InlineData(2, 16)]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public void AShortOrOverlongStreamIsInvalidData(int colorType, int depth)
    {
        // 7x9: a sub-byte row is padded to a whole byte in every pass, and every pass is populated.
        const int w = 7, h = 9;
        using var image = MakeImage(colorType, depth, w, h);
        var png = Encode(image, Interlace.Png);
        PngReader.TryDecode(png, out _).ShouldBeTrue();

        var raw = Inflate(IdatPayload(png));
        var passSizes = PassSizes(w, h, colorType, depth);
        var rowBytes = (w * SamplesPerPixel(colorType) * depth + 7) / 8;

        (string Name, byte[] Raw)[] broken =
        [
            ("empty", []),
            ("one byte short", raw[..^1]),
            ("only the first pass", raw[..passSizes[0]]),
            ("last pass missing", raw[..^passSizes[^1]]),
            ("half", raw[..(raw.Length / 2)]),
            ("one byte long", [.. raw, 0]),
            ("a whole row too many", [.. raw, .. new byte[rowBytes + 1]]),
        ];
        foreach (var (name, bytes) in broken)
        {
            var file = WithIdat(png, Deflate(bytes));
            Should.Throw<InvalidDataException>(() => PngReader.Decode(file), name);
            PngReader.TryDecode(file, out var decoded).ShouldBeFalse(name);
            decoded.ShouldBeNull(name);
        }
    }

    [Fact]
    public void ACutCompressedPayloadIsInvalidData()
    {
        using var image = MakeImage(6, 8, 33, 17);
        var png = Encode(image, Interlace.Png);
        var payload = IdatPayload(png);

        // Cuts into the deflate data. Not a cut of just the Adler-32 trailer: .NET's ZLibStream does not insist on it,
        // so a stream missing only that still inflates to every row and decodes, rightly.
        foreach (var keep in new[] { payload.Length / 2, payload.Length - 16, 2 })
        {
            var file = WithIdat(png, payload[..keep]);
            Should.Throw<InvalidDataException>(() => PngReader.Decode(file), $"first {keep} of {payload.Length} bytes");
            PngReader.TryDecode(file, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void AnAdam7FlagOnAStreamThatIsNotInterlacedIsInvalidData()
    {
        // The same 7x9 raster: the plain stream is 9 rows, the Adam7 one is 7 passes, and the two lengths differ,
        // so a header that lies about the interlace method cannot decode to a plausible wrong picture.
        using var image = MakeImage(6, 8, 7, 9);
        var plain = Encode(image, Interlace.NoInterlace);
        PngReader.Decode(plain).Width.ShouldBe(7);

        var lying = WithInterlaceMethod(plain, 1);
        Should.Throw<InvalidDataException>(() => PngReader.Decode(lying));
        PngReader.TryDecode(lying, out _).ShouldBeFalse();
    }

    [Fact]
    public void AnInterlaceMethodOtherThanZeroOrOneIsInvalidDataNotUnsupported()
    {
        using var image = MakeImage(6, 8, 7, 9);
        var plain = Encode(image, Interlace.NoInterlace);

        foreach (var method in new byte[] { 2, 7, 255 })
        {
            var file = WithInterlaceMethod(plain, method);
            Should.Throw<InvalidDataException>(() => PngReader.Decode(file));
            PngReader.TryDecode(file, out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void AnIndexedImageUsingEveryPaletteEntryDecodesToItsIndices(int depth)
    {
        // The one case ImageMagick cannot write (see MakeImage): a palette of all 2^depth entries, so a 1-bit file has
        // both indices in play. The files are built here from the spec's pass table, not by libpng, and the expected
        // result is the index array that went in. Rows cycle through all five filters, so the ones that read the row above
        // read the previous row of the same pass, and zeros at the head of each.
        var entries = 1 << depth;
        var palette = new byte[entries * 3];
        for (var k = 0; k < entries; k++)
        {
            (palette[k * 3], palette[k * 3 + 1], palette[k * 3 + 2]) = ((byte)(k * 37), (byte)(255 - k * 53), (byte)(k * 91));
        }

        foreach (var (width, height) in Sizes)
        {
            var random = new Random(depth * 7919 + width * 101 + height);
            var indices = new byte[width * height];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = (byte)random.Next(entries);
            }

            var interlaced = IndexedPng(indices, width, height, depth, palette, interlaced: true);
            var plain = IndexedPng(indices, width, height, depth, palette, interlaced: false);
            Ihdr(interlaced).ShouldBe((width, height, depth, 3, 1));

            var decoded = PngReader.Decode(interlaced);
            decoded.Pixels.ShouldBe(indices, $"{width}x{height}, {depth}-bit, Adam7");
            decoded.Palette.ShouldBe(palette);
            PngReader.Decode(plain).Pixels.ShouldBe(indices, $"{width}x{height}, {depth}-bit, plain");
        }
    }

    [Fact]
    public void MetadataSurvivesDeinterlacing()
    {
        // Everything that is not the samples reads exactly as it does for the non-interlaced file.
        using var image = MakeImage(3, 4, 13, 11);
        var interlaced = Encode(image, Interlace.Png);
        var plain = Encode(image, Interlace.NoInterlace);

        var decoded = PngReader.Decode(interlaced);
        var expected = PngReader.Decode(plain);

        decoded.Palette.ShouldNotBeNull().ShouldBe(expected.Palette);
        decoded.Gamma.ShouldBe(expected.Gamma);
        decoded.SrgbRenderingIntent.ShouldBe(expected.SrgbRenderingIntent);
        decoded.Text.Count.ShouldBe(expected.Text.Count);
        decoded.Pixels.ShouldBe(expected.Pixels);
    }

    // ---- fixtures ---------------------------------------------------------------

    /// <summary>
    /// A random raster for the colour type and depth, as an RGB(A) image whose PNG defines pick the colour type and depth
    /// the writer emits. Grey is R=G=B, grey levels are whole levels of the depth, indexed pixels draw from a palette
    /// of as many colours as ImageMagick will write at the depth (see below), and the other types use the full sample range.
    /// </summary>
    private static MagickImage MakeImage(int colorType, int depth, int width, int height)
    {
        var random = new Random(colorType * 1_000_003 + depth * 10_007 + width * 101 + height);
        var sixteen = depth == 16;
        var max = sixteen ? 65535 : 255;
        var levels = 1 << Math.Min(depth, 8);
        // Only the types that carry alpha get an alpha channel: with one, ImageMagick writes a palette entry too many.
        var hasAlpha = colorType is 4 or 6;
        var stride = hasAlpha ? 4 : 3;
        var samples = new ushort[width * height * stride];
        for (var p = 0; p < width * height; p++)
        {
            int r, g, b, a = max;
            switch (colorType)
            {
                case 0:
                    r = g = b = sixteen ? random.Next(65536) : random.Next(levels) * 255 / (levels - 1);
                    break;
                case 3:
                    // One colour short of the depth's capacity: ImageMagick widens a palette of exactly 2^depth colours
                    // to the next depth (as PngSubByteDepthTests found), so it cannot emit a full 1-, 2- or 4-bit palette.
                    // A 1-bit indexed file is therefore all index 0 here; AnIndexedImageUsingEveryPaletteEntry covers what
                    // this cannot, with files built by hand.
                    var k = random.Next(Math.Max(1, levels - 1));
                    (r, g, b) = (k * 37 % 256, 255 - k * 53 % 256, k * 91 % 256);
                    break;
                case 4:
                    r = g = b = random.Next(max + 1);
                    a = random.Next(max + 1);
                    break;
                case 6:
                    (r, g, b, a) = (random.Next(max + 1), random.Next(max + 1), random.Next(max + 1), random.Next(max + 1));
                    break;
                default:
                    (r, g, b) = (random.Next(max + 1), random.Next(max + 1), random.Next(max + 1));
                    break;
            }
            samples[p * stride] = (ushort)r;
            samples[p * stride + 1] = (ushort)g;
            samples[p * stride + 2] = (ushort)b;
            if (hasAlpha)
            {
                samples[p * stride + 3] = (ushort)a;
            }
        }

        var image = new MagickImage();
        var bytes = sixteen ? MemoryMarshal.AsBytes<ushort>(samples).ToArray() : [.. samples.Select(s => (byte)s)];
        image.ReadPixels(bytes, new PixelReadSettings((uint)width, (uint)height, sixteen ? StorageType.Short : StorageType.Char, hasAlpha ? "RGBA" : "RGB"));
        if (colorType == 3)
        {
            image.ColorType = ColorType.Palette;
        }
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", depth.ToString(CultureInfo.InvariantCulture));
        image.Settings.SetDefine(MagickFormat.Png, "color-type", colorType.ToString(CultureInfo.InvariantCulture));
        return image;
    }

    private static byte[] Encode(MagickImage image, Interlace interlace)
    {
        image.Settings.Interlace = interlace;
        return image.ToByteArray(MagickFormat.Png);
    }

    // IHDR's fields, read where PNG puts them: signature (8), length (4), "IHDR" (4), then width, height, depth, colour type,
    // compression, filter, interlace.
    private static (int Width, int Height, int Depth, int ColorType, int Interlace) Ihdr(byte[] png) =>
        ((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)),
         png[24], png[25], png[28]);

    private static int SamplesPerPixel(int colorType) => colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };

    private static int PassExtent(int size, int start, int step) => size > start ? (size - start + step - 1) / step : 0;

    /// <summary>The inflated bytes of each non-empty pass (or the one image, if not interlaced): rows × (filter byte + packed row).</summary>
    private static int[] PassSizes(int width, int height, int colorType, int depth, bool interlaced = true)
    {
        var bitsPerPixel = SamplesPerPixel(colorType) * depth;
        var sizes = new List<int>();
        foreach (var (x0, y0, dx, dy) in interlaced ? Passes : [(0, 0, 1, 1)])
        {
            var passWidth = PassExtent(width, x0, dx);
            var passHeight = PassExtent(height, y0, dy);
            if (passWidth > 0 && passHeight > 0)
            {
                sizes.Add(passHeight * (1 + (passWidth * bitsPerPixel + 7) / 8));
            }
        }
        return [.. sizes];
    }

    private static int ExpectedInflatedLength(int width, int height, int colorType, int depth, bool interlaced) =>
        PassSizes(width, height, colorType, depth, interlaced).Sum();

    private static int InflatedLength(byte[] png) => Inflate(IdatPayload(png)).Length;

    /// <summary>The filter byte at the head of every row of the inflated stream, walking the passes as the spec lays them out.</summary>
    private static HashSet<byte> FilterTypesUsed(byte[] png)
    {
        var (width, height, depth, colorType, interlace) = Ihdr(png);
        var raw = Inflate(IdatPayload(png));
        var bitsPerPixel = SamplesPerPixel(colorType) * depth;
        var used = new HashSet<byte>();
        var pos = 0;
        foreach (var (x0, y0, dx, dy) in interlace == 1 ? Passes : [(0, 0, 1, 1)])
        {
            var passWidth = PassExtent(width, x0, dx);
            var passHeight = PassExtent(height, y0, dy);
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }
            for (var j = 0; j < passHeight; j++)
            {
                used.Add(raw[pos]);
                pos += 1 + (passWidth * bitsPerPixel + 7) / 8;
            }
        }
        pos.ShouldBe(raw.Length);
        return used;
    }

    /// <summary>An indexed PNG built by hand: the pixels laid out per the pass table (or as one image), rows packed, filtered None / Sub / Up / Average / Paeth in turn.</summary>
    private static byte[] IndexedPng(byte[] indices, int width, int height, int depth, byte[] palette, bool interlaced)
    {
        var raw = new List<byte>();
        foreach (var (x0, y0, dx, dy) in interlaced ? Passes : [(0, 0, 1, 1)])
        {
            var passWidth = PassExtent(width, x0, dx);
            var passHeight = PassExtent(height, y0, dy);
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }
            var rowBytes = (passWidth * depth + 7) / 8;
            var above = new byte[rowBytes];
            for (var j = 0; j < passHeight; j++)
            {
                var row = new byte[rowBytes];
                for (var i = 0; i < passWidth; i++)
                {
                    var bit = i * depth;
                    row[bit >> 3] |= (byte)(indices[(y0 + j * dy) * width + x0 + i * dx] << (8 - depth - (bit & 7)));
                }
                var filter = j % 5;
                raw.Add((byte)filter);
                for (var k = 0; k < rowBytes; k++)
                {
                    // The filters' "left" is the previous byte: a pixel for 8-bit, and for a depth under 8 the spec's one-byte step.
                    int left = k > 0 ? row[k - 1] : 0, up = above[k], upLeft = k > 0 ? above[k - 1] : 0;
                    raw.Add((byte)(row[k] - filter switch
                    {
                        1 => left,
                        2 => up,
                        3 => (left + up) / 2,
                        4 => Paeth(left, up, upLeft),
                        _ => 0,
                    }));
                }
                above = row;
            }
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        (ihdr[8], ihdr[9], ihdr[12]) = ((byte)depth, 3, (byte)(interlaced ? 1 : 0));
        return Build([("IHDR", ihdr), ("PLTE", palette), ("IDAT", Deflate([.. raw])), ("IEND", [])]);
    }

    // PNG section 9.4's Paeth predictor, restated for the test encoder.
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ---- PNG surgery -------------------------------------------------------------

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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

    private static byte[] IdatPayload(byte[] png) =>
        [.. Chunks(png).Where(c => c.Type == "IDAT").SelectMany(c => c.Data)];

    /// <summary>The file with its IDAT chunks replaced by one holding <paramref name="zlib"/>, every CRC recomputed.</summary>
    private static byte[] WithIdat(byte[] png, byte[] zlib)
    {
        var result = new List<(string Type, byte[] Data)>();
        var placed = false;
        foreach (var chunk in Chunks(png))
        {
            if (chunk.Type != "IDAT")
            {
                result.Add(chunk);
            }
            else if (!placed)
            {
                result.Add(("IDAT", zlib));
                placed = true;
            }
        }
        return Build(result);
    }

    private static byte[] WithInterlaceMethod(byte[] png, byte method)
    {
        var chunks = Chunks(png);
        chunks[0].Data[12] = method;
        return Build(chunks);
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var z = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }
        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
