using System.Buffers.Binary;

namespace SharpAstro.Tga;

/// <summary>The Truevision image type field (header byte 2).</summary>
public enum TgaImageType : byte
{
    ColorMapped = 1,
    TrueColor = 2,
    Grayscale = 3,
    RleColorMapped = 9,
    RleTrueColor = 10,
    RleGrayscale = 11,
}

/// <summary>What the 18-byte TGA header says about an image, after it has been checked.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="ImageType">Image type (1/2/3 raw, 9/10/11 run-length coded).</param>
/// <param name="BitsPerPixel">Bits per stored pixel (the index size for a colour-mapped image).</param>
/// <param name="ColorMapEntryBits">Bits per colour-map entry, 0 when the file has no map.</param>
/// <param name="HasAlpha">True when the file carries alpha: 32-bit pixels or map entries, or 15/16-bit ones when the
/// descriptor declares at least one attribute bit.</param>
/// <param name="TopOrigin">True when stored rows run top to bottom (descriptor bit 5); false for bottom-left origin.</param>
/// <param name="RightToLeft">True when stored pixels run right to left (descriptor bit 4).</param>
public readonly record struct TgaInfo(
    int Width,
    int Height,
    TgaImageType ImageType,
    int BitsPerPixel,
    int ColorMapEntryBits,
    bool HasAlpha,
    bool TopOrigin,
    bool RightToLeft)
{
    /// <summary>True for the run-length coded types 9, 10 and 11.</summary>
    public bool IsRle => ImageType >= TgaImageType.RleColorMapped;

    /// <summary>True for the greyscale types 3 and 11.</summary>
    public bool IsGrayscale => ImageType is TgaImageType.Grayscale or TgaImageType.RleGrayscale;
}

/// <summary>A decoded TGA image in tightly packed, top-down, left-to-right, non-premultiplied RGBA8.</summary>
public sealed class TgaImage
{
    internal TgaImage(TgaInfo info, byte[] rgba)
    {
        Info = info;
        Rgba = rgba;
    }

    /// <summary>The header facts the image was decoded from.</summary>
    public TgaInfo Info { get; }

    /// <summary>The image width in pixels.</summary>
    public int Width => Info.Width;

    /// <summary>The image height in pixels.</summary>
    public int Height => Info.Height;

    /// <summary>True when the file carries alpha. When false every alpha byte of <see cref="Rgba"/> is 255.</summary>
    public bool HasAlpha => Info.HasAlpha;

    /// <summary><c>Width * Height * 4</c> bytes: R, G, B, A per pixel, row 0 on top, whatever the file's origin.</summary>
    public byte[] Rgba { get; }
}

/// <summary>
/// Reader for Truevision TGA 2.0 (and the original 1.0 layout, which is the same bytes without a footer).
/// Written from the Truevision TGA File Format Specification, version 2.0. Every decoded pixel is checked
/// against ImageMagick (Magick.NET) in the test suite.
/// </summary>
/// <remarks>
/// <para>Supported: image types 1/2/3 and their run-length forms 9/10/11; 8, 15, 16, 24 and 32-bit true-colour
/// and 8-bit grey pixels; 8-bit colour-map indices; 8, 15, 16, 24 and 32-bit colour-map entries; both vertical
/// origins and both horizontal directions; the image ID is skipped. The extension area and developer area are
/// not read (so the "attributes type" byte is not consulted, and alpha is never un-premultiplied).</para>
/// <para>Alpha: a 32-bit pixel or map entry always carries its alpha byte as stored, even when the descriptor's
/// attribute-bit count is 0 (what stb_image and ImageMagick both do, and what real writers rely on). A 15 or
/// 16-bit pixel carries a one-bit alpha (bit 15) when the descriptor's attribute-bit count is not 0, and is
/// opaque otherwise. A 15 or 16-bit colour-map entry is always opaque, whatever the descriptor says: the
/// descriptor describes pixels, and ImageMagick reads such entries as opaque too.</para>
/// <para>15 and 16-bit colour is 5-5-5 and is widened to 8 bits as <c>round(v * 255 / 31)</c>, which is what
/// ImageMagick does (checked for all 65536 words). Not the bit-replication <c>(v &lt;&lt; 3) | (v &gt;&gt; 2)</c>:
/// that one differs for 4 of the 32 values (3, 7, 24, 28).</para>
/// <para>Refused with <see cref="InvalidDataException"/>, never clamped or guessed: a truncated file, an RLE
/// packet that runs past the end of the image, an RLE stream that ends early, an unknown image type, a depth
/// the type does not allow, a colour index outside the map, a non-zero interleave field, and a declared
/// size above <see cref="MaximumPixels"/>.</para>
/// </remarks>
public static class TgaReader
{
    /// <summary>
    /// The most pixels a file may declare (2^26: 8192 x 8192). Checked on the header, before anything is
    /// allocated, because both dimensions are 16-bit and the header alone can claim 4.3 billion pixels.
    /// </summary>
    public const long MaximumPixels = 1L << 26;

    private const int HeaderSize = 18;

    /// <summary>Reads and checks the header. Returns false for anything <see cref="Decode"/> would refuse in it.</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out TgaInfo info)
    {
        return TryParseHeader(data, out info, out _, out _, out _) is null;
    }

    /// <summary>Reads and checks the header, throwing <see cref="InvalidDataException"/> on a bad one.</summary>
    public static TgaInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        string? error = TryParseHeader(data, out TgaInfo info, out _, out _, out _);
        return error is null ? info : throw new InvalidDataException(error);
    }

    /// <summary>Decodes a TGA file to RGBA8.</summary>
    /// <exception cref="InvalidDataException">The data is not a TGA this reader accepts; see the type remarks.</exception>
    public static TgaImage Decode(ReadOnlySpan<byte> data)
    {
        string? error = TryParseHeader(data, out TgaInfo info, out int mapOffset, out int mapBytes, out int idLength);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }

        int w = info.Width, h = info.Height;
        long pixelCount = (long)w * h;
        int bytesPerSample = info.BitsPerPixel == 8 ? 1 : (info.BitsPerPixel + 7) / 8;
        int dataOffset = HeaderSize + idLength + mapBytes;
        ReadOnlySpan<byte> pixels = data[dataOffset..];

        // Check the pixel data's size before allocating the raster, so a short file cannot buy a big
        // allocation. A raw image needs exactly this much. An RLE packet covers at most 128 pixels for
        // (1 + bytes per pixel) bytes, so an RLE image needs at least this much.
        long minBytes = info.IsRle
            ? (pixelCount + 127) / 128 * (1 + bytesPerSample)
            : pixelCount * bytesPerSample;
        if (pixels.Length < minBytes)
        {
            throw new InvalidDataException(
                $"TGA pixel data is truncated: {pixels.Length} bytes, at least {minBytes} needed for {w}x{h}.");
        }

        bool isMapped = info.ImageType is TgaImageType.ColorMapped or TgaImageType.RleColorMapped;
        uint[]? palette = null;
        int firstEntry = 0;
        if (isMapped)
        {
            firstEntry = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]);
            palette = ReadPalette(data.Slice(mapOffset, mapBytes), info, BinaryPrimitives.ReadUInt16LittleEndian(data[5..]));
        }

        PixelKind kind = isMapped ? PixelKind.Mapped
            : info.IsGrayscale ? PixelKind.Gray8
            : info.BitsPerPixel switch
            {
                15 or 16 => PixelKind.Word16,
                24 => PixelKind.Bgr24,
                _ => PixelKind.Bgra32,
            };
        bool word16Alpha = info.HasAlpha && kind == PixelKind.Word16;

        var rgba = new byte[pixelCount * 4];
        var placer = new Placer(rgba, w, h, info.TopOrigin, info.RightToLeft);

        int pos = 0;
        if (!info.IsRle)
        {
            for (long i = 0; i < pixelCount; i++)
            {
                placer.Put(Convert(kind, pixels, pos, palette, firstEntry, word16Alpha));
                pos += bytesPerSample;
            }
        }
        else
        {
            long remaining = pixelCount;
            while (remaining > 0)
            {
                if (pos >= pixels.Length)
                {
                    throw new InvalidDataException("TGA RLE data ends before the image is complete.");
                }

                int header = pixels[pos++];
                int count = (header & 0x7F) + 1;
                if (count > remaining)
                {
                    throw new InvalidDataException(
                        $"TGA RLE packet of {count} pixels runs {count - remaining} pixels past the end of the image.");
                }

                if ((header & 0x80) != 0)
                {
                    if (pos + bytesPerSample > pixels.Length)
                    {
                        throw new InvalidDataException("TGA RLE data is truncated inside a run packet.");
                    }

                    uint px = Convert(kind, pixels, pos, palette, firstEntry, word16Alpha);
                    pos += bytesPerSample;
                    for (int i = 0; i < count; i++)
                    {
                        placer.Put(px);
                    }
                }
                else
                {
                    if (pos + (long)count * bytesPerSample > pixels.Length)
                    {
                        throw new InvalidDataException("TGA RLE data is truncated inside a raw packet.");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        placer.Put(Convert(kind, pixels, pos, palette, firstEntry, word16Alpha));
                        pos += bytesPerSample;
                    }
                }

                remaining -= count;
            }
        }

        return new TgaImage(info, rgba);
    }

    private enum PixelKind { Mapped, Gray8, Word16, Bgr24, Bgra32 }

    /// <summary>Header checks shared by <see cref="TryReadInfo"/>, <see cref="ReadInfo"/> and <see cref="Decode"/>;
    /// returns the reason for a refusal, or null.</summary>
    private static string? TryParseHeader(
        ReadOnlySpan<byte> data, out TgaInfo info, out int mapOffset, out int mapBytes, out int idLength)
    {
        info = default;
        mapOffset = 0;
        mapBytes = 0;
        idLength = 0;
        if (data.Length < HeaderSize)
        {
            return $"TGA data is {data.Length} bytes, shorter than the {HeaderSize}-byte header.";
        }

        idLength = data[0];
        int mapType = data[1];
        int type = data[2];
        int mapLength = BinaryPrimitives.ReadUInt16LittleEndian(data[5..]);
        int mapEntryBits = data[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int depth = data[16];
        int descriptor = data[17];

        if (type is not (1 or 2 or 3 or 9 or 10 or 11))
        {
            return type == 0
                ? "TGA image type 0: the file holds no image data."
                : $"TGA image type {type} is not supported (expected 1, 2, 3, 9, 10 or 11).";
        }

        var imageType = (TgaImageType)type;
        bool mapped = imageType is TgaImageType.ColorMapped or TgaImageType.RleColorMapped;
        bool gray = imageType is TgaImageType.Grayscale or TgaImageType.RleGrayscale;

        if (mapType > 1)
        {
            return $"TGA colour-map type {mapType} is not supported (expected 0 or 1).";
        }

        if ((descriptor & 0xC0) != 0)
        {
            return $"TGA descriptor interleave field {descriptor >> 6} is not supported (only non-interleaved images).";
        }

        if (mapped)
        {
            if (mapType != 1)
            {
                return "TGA colour-mapped image has no colour map.";
            }

            if (depth != 8)
            {
                return $"TGA colour-mapped images with {depth}-bit indices are not supported (only 8).";
            }

            if (mapEntryBits is not (8 or 15 or 16 or 24 or 32))
            {
                return $"TGA colour-map entries of {mapEntryBits} bits are not supported (expected 8, 15, 16, 24 or 32).";
            }
        }
        else if (gray)
        {
            if (depth != 8)
            {
                return $"TGA greyscale images with {depth}-bit pixels are not supported (only 8).";
            }
        }
        else if (depth is not (15 or 16 or 24 or 32))
        {
            return $"TGA true-colour images with {depth}-bit pixels are not supported (expected 15, 16, 24 or 32).";
        }

        if (width == 0 || height == 0)
        {
            return $"TGA image is {width}x{height}: empty.";
        }

        if ((long)width * height > MaximumPixels)
        {
            return $"TGA image {width}x{height} is over the {MaximumPixels}-pixel limit.";
        }

        // A map may be present in a non-mapped image; it is skipped, whatever its entry size.
        int entryBytes = (mapEntryBits + 7) / 8;
        mapBytes = mapType == 1 ? mapLength * entryBytes : 0;
        mapOffset = HeaderSize + idLength;
        if ((long)mapOffset + mapBytes > data.Length)
        {
            return "TGA data is truncated inside the image ID or colour map.";
        }

        int alphaBits = descriptor & 0x0F;
        bool hasAlpha = mapped
            ? mapEntryBits == 32
            : !gray && (depth == 32 || (depth is 15 or 16 && alphaBits > 0));

        info = new TgaInfo(
            width,
            height,
            imageType,
            depth,
            mapped ? mapEntryBits : (mapType == 1 ? mapEntryBits : 0),
            hasAlpha,
            TopOrigin: (descriptor & 0x20) != 0,
            RightToLeft: (descriptor & 0x10) != 0);
        return null;
    }

    private static uint[] ReadPalette(ReadOnlySpan<byte> map, TgaInfo info, int length)
    {
        var palette = new uint[length];
        int entryBits = info.ColorMapEntryBits;
        int entryBytes = (entryBits + 7) / 8;
        for (int i = 0; i < length; i++)
        {
            ReadOnlySpan<byte> e = map.Slice(i * entryBytes, entryBytes);
            palette[i] = entryBits switch
            {
                8 => Pack(e[0], e[0], e[0], 255),
                15 or 16 => Word16(e, alpha: false),
                24 => Pack(e[2], e[1], e[0], 255),
                _ => Pack(e[2], e[1], e[0], e[3]),
            };
        }

        return palette;
    }

    private static uint Convert(PixelKind kind, ReadOnlySpan<byte> src, int pos, uint[]? palette, int firstEntry, bool word16Alpha)
    {
        switch (kind)
        {
            case PixelKind.Gray8:
                return Pack(src[pos], src[pos], src[pos], 255);
            case PixelKind.Word16:
                return Word16(src.Slice(pos, 2), word16Alpha);
            case PixelKind.Bgr24:
                return Pack(src[pos + 2], src[pos + 1], src[pos], 255);
            case PixelKind.Bgra32:
                return Pack(src[pos + 2], src[pos + 1], src[pos], src[pos + 3]);
            default:
                // A pixel value v names map entry v - firstEntry (spec: "first entry index").
                int index = src[pos] - firstEntry;
                if ((uint)index >= (uint)palette!.Length)
                {
                    throw new InvalidDataException(
                        $"TGA colour index {src[pos]} is outside the colour map (entries {firstEntry}..{firstEntry + palette.Length - 1}).");
                }

                return palette[index];
        }
    }

    // Arrangement ARRRRRGGGGGBBBBB, little endian.
    private static uint Word16(ReadOnlySpan<byte> e, bool alpha)
    {
        int v = e[0] | (e[1] << 8);
        return Pack(
            Widen((v >> 10) & 31),
            Widen((v >> 5) & 31),
            Widen(v & 31),
            alpha && (v & 0x8000) == 0 ? (byte)0 : (byte)255);
    }

    // round(v * 255 / 31), which is what ImageMagick's 5 -> 8 bit scaling gives (all 32 values checked in the tests).
    private static byte Widen(int v5) => (byte)(((v5 * 255) + 15) / 31);

    private static uint Pack(byte r, byte g, byte b, byte a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    /// <summary>Writes pixels in stored order to where the descriptor's origin bits put them.</summary>
    private struct Placer
    {
        private readonly byte[] _rgba;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _top;
        private readonly bool _rtl;
        private int _x;
        private int _y;

        public Placer(byte[] rgba, int width, int height, bool top, bool rtl)
        {
            _rgba = rgba;
            _width = width;
            _height = height;
            _top = top;
            _rtl = rtl;
            _x = 0;
            _y = 0;
        }

        public void Put(uint rgba)
        {
            int row = _top ? _y : _height - 1 - _y;
            int col = _rtl ? _width - 1 - _x : _x;
            BinaryPrimitives.WriteUInt32LittleEndian(_rgba.AsSpan(((row * _width) + col) * 4), rgba);
            if (++_x == _width)
            {
                _x = 0;
                _y++;
            }
        }
    }
}
