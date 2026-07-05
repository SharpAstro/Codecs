using System.Buffers.Binary;
using System.IO.Compression;

namespace SharpAstro.Png;

/// <summary>
/// Pure-managed PNG decoder — the dual of <see cref="PngWriter"/>. Parses
/// the PNG chunked container, validates CRC32 on every chunk, decompresses
/// the concatenated IDAT stream via <see cref="ZLibStream"/>, then defers
/// row-defilter to the existing <see cref="PngPredictor"/>. Preserves
/// metadata that <c>SharpAstro.StbImage</c> discards: ICC profile, sRGB
/// declaration, gAMA / cHRM color hints, and EXIF.
/// </summary>
/// <remarks>
/// <para>First-cut scope:</para>
/// <list type="bullet">
///   <item>Bit depth 8 and 16 per sample.</item>
///   <item>Color types 0 (Gray), 2 (RGB), 4 (Gray + Alpha), 6 (RGBA).</item>
///   <item>Non-interlaced only (<c>InterlaceMethod = 0</c>).</item>
/// </list>
/// <para>Indexed-color (PLTE / tRNS lookup), sub-byte bit depths (1/2/4),
/// and Adam7 interlacing are deferred to a follow-up. APNG and PNG-3 HDR
/// chunks (<c>cICP</c> / <c>mDCv</c> / <c>cLLI</c>) will layer on top of
/// this decoder.</para>
/// </remarks>
public static class PngReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Parse a PNG byte stream into a <see cref="PngImage"/>. The returned
    /// <see cref="PngImage.Pixels"/> is unfiltered raw sample data in row-major
    /// order; 16-bit samples are returned in PNG's big-endian byte order to
    /// preserve bit patterns exactly (see <see cref="PngImage.AsUInt16Samples"/>
    /// for a native-endian view).
    /// </summary>
    public static PngImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("PNG signature missing — not a PNG file");

        int pos = Signature.Length;
        int? width = null, height = null, bitDepth = null, colorType = null,
             compressionMethod = null, filterMethod = null, interlaceMethod = null;
        var idatChunks = new List<byte[]>();
        byte[]? palette = null;             // PLTE: 3 bytes (RGB) per entry
        byte[]? paletteAlpha = null;        // tRNS (indexed meaning): 1 alpha byte per entry
        byte[]? iccProfileRaw = null;       // zlib-deflated, as on disk
        string? iccProfileName = null;
        byte[]? rawExif = null;
        bool sawSrgb = false;
        byte srgbRenderingIntent = 0;
        uint? gamma100k = null;
        ChromaticityChunk? chrm = null;
        CicpChunk? cicp = null;
        MdcvChunk? mdcv = null;
        ClliChunk? clli = null;

        while (pos < data.Length)
        {
            if (pos + 8 > data.Length)
                throw new InvalidDataException($"Truncated chunk header at byte {pos}");

            uint dataLen = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            // Spec says lengths are capped at 2^31-1; reject anything larger so we don't
            // wrap when adding to a position.
            if (dataLen > int.MaxValue)
                throw new InvalidDataException($"Chunk length {dataLen} exceeds int.MaxValue");
            int len = (int)dataLen;

            if (pos + 8 + len + 4 > data.Length)
                throw new InvalidDataException($"Chunk at byte {pos} extends past end of file");

            var typeBytes = data.Slice(pos + 4, 4);
            var chunkData = data.Slice(pos + 8, len);
            var declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 8 + len, 4));
            var computedCrc = Crc32(typeBytes, chunkData);
            if (declaredCrc != computedCrc)
                throw new InvalidDataException(
                    $"CRC mismatch on chunk '{TypeAscii(typeBytes)}' at byte {pos}: " +
                    $"declared 0x{declaredCrc:X8}, computed 0x{computedCrc:X8}");

            // --- chunk dispatch -----------------------------------------------------
            // Critical chunks (uppercase first letter) must be understood; ancillary
            // chunks (lowercase first letter) can be skipped if unrecognised.
            if (typeBytes.SequenceEqual("IHDR"u8))
            {
                if (width is not null)
                    throw new InvalidDataException("Multiple IHDR chunks");
                if (len != 13)
                    throw new InvalidDataException($"IHDR length must be 13, got {len}");
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                compressionMethod = chunkData[10];
                filterMethod = chunkData[11];
                interlaceMethod = chunkData[12];

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"IHDR dimensions must be positive, got {width}×{height}");
                if (compressionMethod != 0)
                    throw new InvalidDataException($"Unsupported compression method {compressionMethod} (PNG only defines 0)");
                if (filterMethod != 0)
                    throw new InvalidDataException($"Unsupported filter method {filterMethod} (PNG only defines 0)");
                if (interlaceMethod != 0)
                    throw new NotSupportedException("Adam7 interlacing not yet supported (InterlaceMethod=1)");
                ValidateColorTypeAndBitDepth(colorType.Value, bitDepth.Value);
            }
            else if (typeBytes.SequenceEqual("IDAT"u8))
            {
                if (width is null)
                    throw new InvalidDataException("IDAT before IHDR");
                idatChunks.Add(chunkData.ToArray());
            }
            else if (typeBytes.SequenceEqual("IEND"u8))
            {
                pos += 8 + len + 4;
                break;
            }
            else if (typeBytes.SequenceEqual("PLTE"u8))
            {
                if (len == 0 || len % 3 != 0)
                    throw new InvalidDataException($"PLTE length must be a positive multiple of 3, got {len}");
                palette = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("tRNS"u8))
            {
                // For indexed color (the only case this reader expands), tRNS is one
                // alpha byte per palette entry, in palette order; entries past its end
                // are fully opaque. For truecolor/gray tRNS carries a single transparent
                // sample — not handled here (only the indexed meaning is surfaced).
                paletteAlpha = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("iCCP"u8))
            {
                // iCCP layout: keyword (1..79 bytes) + null + compression method (1 byte) + compressed profile.
                var nullIdx = chunkData.IndexOf((byte)0);
                if (nullIdx <= 0)
                    throw new InvalidDataException("iCCP chunk missing keyword/profile separator");
                iccProfileName = System.Text.Encoding.Latin1.GetString(chunkData[..nullIdx]);
                if (nullIdx + 1 >= chunkData.Length)
                    throw new InvalidDataException("iCCP chunk truncated after keyword");
                if (chunkData[nullIdx + 1] != 0)
                    throw new InvalidDataException(
                        $"iCCP compression method must be 0, got {chunkData[nullIdx + 1]}");
                iccProfileRaw = DeflateInflate(chunkData[(nullIdx + 2)..]);
            }
            else if (typeBytes.SequenceEqual("sRGB"u8))
            {
                if (len != 1)
                    throw new InvalidDataException($"sRGB chunk length must be 1, got {len}");
                sawSrgb = true;
                srgbRenderingIntent = chunkData[0];
            }
            else if (typeBytes.SequenceEqual("gAMA"u8))
            {
                if (len != 4)
                    throw new InvalidDataException($"gAMA chunk length must be 4, got {len}");
                gamma100k = BinaryPrimitives.ReadUInt32BigEndian(chunkData);
            }
            else if (typeBytes.SequenceEqual("cHRM"u8))
            {
                if (len != 32)
                    throw new InvalidDataException($"cHRM chunk length must be 32, got {len}");
                chrm = ChromaticityChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("eXIf"u8))
            {
                rawExif = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("cICP"u8))
            {
                cicp = CicpChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("mDCV"u8)
                  || typeBytes.SequenceEqual("mDCv"u8))
            {
                // Canonical PNG-3 spec name is "mDCV" (uppercase V = not safe to copy).
                // Pre-final-spec drafts and some early implementations used the lowercase
                // "mDCv" form; accept both on read so we round-trip those legacy files
                // correctly. The writer emits the canonical "mDCV".
                mdcv = MdcvChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("cLLI"u8))
            {
                clli = ClliChunk.Read(chunkData);
            }
            // Any other chunk — silently skip. PNG is forwards-compatible by design:
            // ancillary unknown chunks are explicitly permitted, and critical unknown
            // chunks have a leading uppercase letter we'd need to reject. We defer the
            // critical-chunk rejection until we encounter a real one.

            pos += 8 + len + 4;
        }

        if (width is null) throw new InvalidDataException("No IHDR chunk found");
        if (idatChunks.Count == 0) throw new InvalidDataException("No IDAT chunks found");
        if (colorType == 3 && palette is null)
            throw new InvalidDataException("Indexed-color PNG missing required PLTE chunk");

        // Concatenate IDAT chunks then inflate as a single zlib stream — the
        // spec lets a producer split IDATs anywhere, even mid-deflate-token.
        var compressed = Concat(idatChunks);
        var raw = DeflateInflate(compressed);

        var pixels = UnfilterAndPack(raw, width.Value, height!.Value, bitDepth!.Value, colorType!.Value);

        return new PngImage
        {
            Width = width.Value,
            Height = height.Value,
            BitDepth = bitDepth.Value,
            ColorType = colorType.Value,
            Pixels = pixels,
            Palette = palette,
            PaletteAlpha = paletteAlpha,
            IccProfile = iccProfileRaw,
            IccProfileName = iccProfileName,
            SrgbRenderingIntent = sawSrgb ? srgbRenderingIntent : null,
            Gamma = gamma100k.HasValue ? gamma100k.Value / 100_000.0 : null,
            Chromaticity = chrm,
            Exif = rawExif,
            Cicp = cicp,
            Mdcv = mdcv,
            Clli = clli,
        };
    }

    /// <summary>
    /// Validate that the (color type, bit depth) combo is one PNG allows.
    /// Spec table 11.1: not every depth is legal with every color type.
    /// </summary>
    private static void ValidateColorTypeAndBitDepth(int colorType, int bitDepth)
    {
        bool ok = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16, // Gray
            2 => bitDepth is 8 or 16,                // RGB
            3 => bitDepth is 1 or 2 or 4 or 8,       // Indexed (palette)
            4 => bitDepth is 8 or 16,                // Gray + Alpha
            6 => bitDepth is 8 or 16,                // RGBA
            _ => false
        };
        if (!ok)
            throw new InvalidDataException(
                $"Invalid (ColorType={colorType}, BitDepth={bitDepth}) combination per PNG spec");

        // Indexed-color is supported at 8-bit (one palette index per byte). Sub-byte
        // indexed (1/2/4-bit) shares the general sub-byte gap below (it needs bit
        // unpacking the unfilter path doesn't do yet).
        if (colorType == 3 && bitDepth != 8)
            throw new NotSupportedException(
                $"Indexed-color PNGs are supported at 8-bit only for now (got {bitDepth}-bit)");
        if (bitDepth is 1 or 2 or 4)
            throw new NotSupportedException(
                $"Sub-byte bit depths ({bitDepth}-bit) not yet supported; only 8 and 16 bit-per-sample for now");
    }

    /// <summary>
    /// Samples per pixel as a function of color type.
    /// Gray=1, RGB=3, Indexed=1, GrayAlpha=2, RGBA=4.
    /// </summary>
    internal static int SamplesPerPixel(int colorType) => colorType switch
    {
        0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
        _ => throw new InvalidDataException($"Unknown color type {colorType}")
    };

    /// <summary>
    /// Run row-defilter, returning the raw sample bytes in row-major order.
    /// 16-bit samples stay in PNG's network (big-endian) byte order so the
    /// returned buffer is byte-identical to what the encoder fed in.
    /// </summary>
    private static byte[] UnfilterAndPack(byte[] inflated, int width, int height, int bitDepth, int colorType)
    {
        var samplesPerPixel = SamplesPerPixel(colorType);
        var bytesPerSample = bitDepth / 8;
        var bytesPerPixel = samplesPerPixel * bytesPerSample;
        var rowBytes = width * bytesPerPixel;
        var expected = (rowBytes + 1) * height;
        if (inflated.Length != expected)
            throw new InvalidDataException(
                $"Inflated stream length {inflated.Length} doesn't match expected {expected} " +
                $"({width}×{height} × {bytesPerPixel} bytes-per-pixel + 1 filter byte per row)");

        // PngPredictor's bpp argument is the "bytes per pixel" rounded up to at
        // least 1 — for 8-bit single-channel images that's 1; for 16-bit RGBA
        // it's 8. The filter formulas only need the "left neighbour" offset, so
        // bytesPerPixel here matches PNG spec §9.2.
        return PngPredictor.Unfilter(inflated, rowBytes, bytesPerPixel);
    }

    /// <summary>
    /// Inflate a deflate-compressed byte stream (zlib-wrapped). PNG IDAT
    /// payload is the concatenation of zero or more zlib streams — but the
    /// spec mandates exactly one logical zlib stream across all IDATs, so
    /// concat-then-inflate works.
    /// </summary>
    private static byte[] DeflateInflate(ReadOnlySpan<byte> compressed)
    {
        // Copy the span into a backing array so we can wrap it in a stream.
        // ZLibStream is the .NET 6+ wrapper that handles the 2-byte zlib header
        // + Adler32 trailer around the raw deflate bitstream.
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Concat(List<byte[]> chunks)
    {
        var total = 0;
        foreach (var c in chunks) total += c.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var c in chunks)
        {
            Array.Copy(c, 0, result, offset, c.Length);
            offset += c.Length;
        }
        return result;
    }

    private static string TypeAscii(ReadOnlySpan<byte> typeBytes) =>
        System.Text.Encoding.ASCII.GetString(typeBytes);

    // ---------------- CRC32 (same polynomial as PngWriter.Crc32) -----------------

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            t[n] = c;
        }
        return t;
    }
}

/// <summary>
/// Decoded PNG image — sample data plus the ancillary metadata
/// <see cref="PngReader"/> extracted from chunks like <c>iCCP</c> / <c>sRGB</c>
/// / <c>gAMA</c> / <c>cHRM</c> / <c>eXIf</c>.
/// </summary>
public sealed record PngImage
{
    /// <summary>Image width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Bits per sample as declared in IHDR (8 or 16 in the v1 decoder).</summary>
    public required int BitDepth { get; init; }

    /// <summary>PNG color type: 0=Gray, 2=RGB, 3=Indexed, 4=Gray+Alpha, 6=RGBA.</summary>
    public required int ColorType { get; init; }

    /// <summary>
    /// Raw unfiltered sample bytes in row-major order. For 16-bit images the
    /// bytes are in PNG's big-endian network order — see <see cref="AsUInt16Samples"/>
    /// for a native-endian convenience view.
    /// </summary>
    public required byte[] Pixels { get; init; }

    /// <summary>
    /// Palette (<c>PLTE</c>) for indexed-color images (<see cref="ColorType"/> == 3):
    /// RGB triples, 3 bytes per entry, in palette-index order. Null for non-indexed
    /// images. Each byte in <see cref="Pixels"/> is a single index into this palette
    /// (the reader keeps indexed pixels un-expanded to stay faithful to the file's
    /// declared <see cref="ColorType"/> — expand via palette lookup at the consumer).
    /// </summary>
    public byte[]? Palette { get; init; }

    /// <summary>
    /// Per-palette-entry alpha (<c>tRNS</c>) for indexed-color images: one byte per
    /// entry, in palette order. May be shorter than <see cref="Palette"/> — entries
    /// past its end are fully opaque (255). Null when an indexed image has no tRNS
    /// chunk (all opaque) or when the image is not indexed.
    /// </summary>
    public byte[]? PaletteAlpha { get; init; }

    /// <summary>Decompressed ICC profile bytes from the <c>iCCP</c> chunk, or null if absent.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Keyword from the <c>iCCP</c> chunk (e.g. "ICC Profile" or a custom name).</summary>
    public string? IccProfileName { get; init; }

    /// <summary>Rendering intent from the <c>sRGB</c> chunk, or null if absent. 0..3 per PNG spec.</summary>
    public byte? SrgbRenderingIntent { get; init; }

    /// <summary>Decoded gamma (e.g. 0.45455 for sRGB) from the <c>gAMA</c> chunk, or null if absent.</summary>
    public double? Gamma { get; init; }

    /// <summary>Chromaticity values from the <c>cHRM</c> chunk, or null if absent.</summary>
    public ChromaticityChunk? Chromaticity { get; init; }

    /// <summary>Raw EXIF blob from the <c>eXIf</c> chunk, or null if absent.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>PNG-3 <c>cICP</c> Coding-Independent Code Points (HDR color signaling), or null if absent.</summary>
    public CicpChunk? Cicp { get; init; }

    /// <summary>PNG-3 <c>mDCv</c> Mastering Display Color Volume, or null if absent.</summary>
    public MdcvChunk? Mdcv { get; init; }

    /// <summary>PNG-3 <c>cLLI</c> Content Light Level Information, or null if absent.</summary>
    public ClliChunk? Clli { get; init; }

    /// <summary>True if any PNG-3 HDR signaling chunk (cICP / mDCv / cLLI) is present.</summary>
    public bool HasHdrSignaling => Cicp is not null || Mdcv is not null || Clli is not null;

    /// <summary>
    /// Sample count per row (<see cref="Width"/> × samples-per-pixel for the color type).
    /// Useful for unpacking <see cref="Pixels"/> without re-deriving the layout.
    /// </summary>
    public int SamplesPerPixel => PngReader.SamplesPerPixel(ColorType);

    /// <summary>
    /// Returns <see cref="Pixels"/> as a <c>ushort[]</c> in host-native endian.
    /// Only valid for 16-bit images; throws otherwise.
    /// </summary>
    public ushort[] AsUInt16Samples()
    {
        if (BitDepth != 16)
            throw new InvalidOperationException($"AsUInt16Samples is only valid for 16-bit images; this is {BitDepth}-bit");
        var n = Pixels.Length / 2;
        var result = new ushort[n];
        for (var i = 0; i < n; i++)
            result[i] = BinaryPrimitives.ReadUInt16BigEndian(Pixels.AsSpan(i * 2, 2));
        return result;
    }

    /// <summary>
    /// Expand the raw <see cref="Pixels"/> samples into tightly-packed 8-bit RGBA
    /// (row-major, 4 bytes per pixel, top-down) — the layout most GPU / UI consumers
    /// want. Handles every color type the decoder produces: greyscale (0), RGB (2),
    /// indexed (3, via <see cref="Palette"/> + optional <see cref="PaletteAlpha"/>),
    /// greyscale + alpha (4), and RGBA (6). 16-bit samples are truncated to their high
    /// byte (<see cref="Pixels"/> holds 16-bit values big-endian, so the byte at the
    /// sample offset already IS the high byte).
    /// </summary>
    /// <remarks>
    /// The faithful-decode counterpart to the "give me display pixels" need:
    /// <see cref="PngReader.Decode"/> keeps samples in their declared color type to
    /// preserve the file exactly; this collapses them to RGBA8 on demand, so consumers
    /// don't each re-implement the big-endian truncation and palette lookup.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// <see cref="ColorType"/> is not one of the five PNG color types (0/2/3/4/6), the
    /// image is indexed but carries no <see cref="Palette"/>, or a pixel indexes past
    /// the palette's end.
    /// </exception>
    public byte[] ToRgba8()
    {
        var dst = new byte[Width * Height * 4];
        ExpandToRgba8(dst);
        return dst;
    }

    /// <summary>
    /// Expand the raw <see cref="Pixels"/> samples into a caller-provided 8-bit RGBA
    /// destination (row-major, 4 bytes per pixel, top-down) - identical mapping to
    /// <see cref="ToRgba8()"/> but writing into <paramref name="destination"/> instead
    /// of allocating a result, so a codec facade can decode straight into a pooled or
    /// UI-owned buffer. <paramref name="destination"/> must be at least
    /// <c>Width * Height * 4</c> bytes.
    /// </summary>
    public void ExpandToRgba8(Span<byte> destination)
    {
        var w = Width;
        var h = Height;
        var src = Pixels;
        var spp = SamplesPerPixel;
        var step = BitDepth == 16 ? 2 : 1; // bytes per sample (high byte first for 16-bit)
        var rowBytes = w * spp * step;
        if (destination.Length < w * h * 4)
            throw new ArgumentException($"Destination too small: {destination.Length} < {w * h * 4} (Width*Height*4).", nameof(destination));

        for (var y = 0; y < h; y++)
        {
            var srcRow = y * rowBytes;
            var dstRow = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var s = srcRow + x * spp * step;
                byte r, g, b, a;
                switch (ColorType)
                {
                    case 0: // greyscale
                        r = g = b = src[s];
                        a = 255;
                        break;
                    case 2: // RGB
                        r = src[s];
                        g = src[s + step];
                        b = src[s + 2 * step];
                        a = 255;
                        break;
                    case 3: // indexed: src[s] is an index into Palette (+ optional PaletteAlpha)
                        if (Palette is not { } pal)
                            throw new InvalidDataException("Indexed PNG has no palette to expand to RGBA8");
                        var idx = src[s];
                        var pi = idx * 3;
                        if (pi + 2 >= pal.Length)
                            throw new InvalidDataException(
                                $"Palette index {idx} out of range (palette has {pal.Length / 3} entries)");
                        r = pal[pi];
                        g = pal[pi + 1];
                        b = pal[pi + 2];
                        a = PaletteAlpha is { } pa && idx < pa.Length ? pa[idx] : (byte)255;
                        break;
                    case 4: // greyscale + alpha
                        r = g = b = src[s];
                        a = src[s + step];
                        break;
                    case 6: // RGBA
                        r = src[s];
                        g = src[s + step];
                        b = src[s + 2 * step];
                        a = src[s + 3 * step];
                        break;
                    default:
                        // Unreachable in practice: SamplesPerPixel (read above) already
                        // throws for any color type outside {0,2,3,4,6}. Kept for definite
                        // assignment of r/g/b/a and as a defensive backstop.
                        throw new InvalidDataException($"Cannot expand PNG color type {ColorType} to RGBA8");
                }
                var d = dstRow + x * 4;
                destination[d] = r;
                destination[d + 1] = g;
                destination[d + 2] = b;
                destination[d + 3] = a;
            }
        }
    }
}

/// <summary>
/// PNG <c>cHRM</c> chunk payload: chromaticity coordinates of the white point
/// and three primaries, encoded as fixed-point (×100000) per PNG spec §11.3.3.5.
/// </summary>
public sealed record ChromaticityChunk(
    double WhiteX, double WhiteY,
    double RedX, double RedY,
    double GreenX, double GreenY,
    double BlueX, double BlueY)
{
    internal static ChromaticityChunk Read(ReadOnlySpan<byte> chunkData)
    {
        // Local helper can't close over a ReadOnlySpan<byte>; inline the eight reads.
        var wx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(0, 4))  / 100_000.0;
        var wy = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4))  / 100_000.0;
        var rx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(8, 4))  / 100_000.0;
        var ry = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(12, 4)) / 100_000.0;
        var gx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(16, 4)) / 100_000.0;
        var gy = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(20, 4)) / 100_000.0;
        var bx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(24, 4)) / 100_000.0;
        var by = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(28, 4)) / 100_000.0;
        return new ChromaticityChunk(wx, wy, rx, ry, gx, gy, bx, by);
    }

    internal void Write(Span<byte> dst)
    {
        // dst must be exactly 32 bytes; caller writes the chunk wrapper.
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0, 4),  (uint)Math.Round(WhiteX * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4),  (uint)Math.Round(WhiteY * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(8, 4),  (uint)Math.Round(RedX   * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(12, 4), (uint)Math.Round(RedY   * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(16, 4), (uint)Math.Round(GreenX * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(20, 4), (uint)Math.Round(GreenY * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(24, 4), (uint)Math.Round(BlueX  * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(28, 4), (uint)Math.Round(BlueY  * 100_000.0));
    }
}

/// <summary>
/// PNG-3 <c>cICP</c> chunk — Coding-Independent Code Points (4 bytes).
/// Identifies the colour space + transfer function of the image's sample
/// values using H.273 / ITU CICP numbering. This is how PNG-3 declares
/// HDR — pixel data stays 16-bit integer, but cICP says e.g. "BT.2020
/// primaries + PQ transfer function" to flag it as HDR10.
/// </summary>
/// <param name="ColorPrimaries">H.273 §8.1 colour primaries codepoint.</param>
/// <param name="TransferFunction">H.273 §8.2 transfer characteristics codepoint.</param>
/// <param name="MatrixCoefficients">H.273 §8.3 matrix codepoint. PNG-3 §11.3.2.6 requires <see cref="SharpAstro.Color.Icc.MatrixCoefficients.Identity"/> (RGB) — PNG doesn't carry YCbCr.</param>
/// <param name="VideoFullRangeFlag">PNG-3 requires <c>true</c> (full range 0..2^N-1).</param>
public sealed record CicpChunk(
    SharpAstro.Color.Icc.ColorPrimaries ColorPrimaries,
    SharpAstro.Color.Icc.TransferFunction TransferFunction,
    SharpAstro.Color.Icc.MatrixCoefficients MatrixCoefficients,
    bool VideoFullRangeFlag)
{
    internal static CicpChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 4)
            throw new InvalidDataException($"cICP chunk length must be 4, got {chunkData.Length}");
        return new CicpChunk(
            ColorPrimaries:       (SharpAstro.Color.Icc.ColorPrimaries)chunkData[0],
            TransferFunction:     (SharpAstro.Color.Icc.TransferFunction)chunkData[1],
            MatrixCoefficients:   (SharpAstro.Color.Icc.MatrixCoefficients)chunkData[2],
            VideoFullRangeFlag:   chunkData[3] != 0);
    }

    internal void Write(Span<byte> dst)
    {
        dst[0] = (byte)ColorPrimaries;
        dst[1] = (byte)TransferFunction;
        dst[2] = (byte)MatrixCoefficients;
        dst[3] = (byte)(VideoFullRangeFlag ? 1 : 0);
    }

    /// <summary>BT.2020 + PQ (SMPTE 2084) — the standard "HDR10" signalling.</summary>
    public static CicpChunk Hdr10Pq => new(
        SharpAstro.Color.Icc.ColorPrimaries.BT2020,
        SharpAstro.Color.Icc.TransferFunction.Pq,
        SharpAstro.Color.Icc.MatrixCoefficients.Identity,
        true);

    /// <summary>BT.2020 + HLG (ARIB STD-B67) — broadcast HDR.</summary>
    public static CicpChunk Bt2020Hlg => new(
        SharpAstro.Color.Icc.ColorPrimaries.BT2020,
        SharpAstro.Color.Icc.TransferFunction.Hlg,
        SharpAstro.Color.Icc.MatrixCoefficients.Identity,
        true);

    /// <summary>
    /// sRGB primaries + PQ transfer — "narrow-gamut HDR". Non-canonical for
    /// HDR10 (which is BT.2020 + PQ) but valid PNG-3 / ICC v4.4; sidesteps
    /// the gamut-mismatch desaturation that consumer HDR displays sometimes
    /// produce on BT.2020-encoded content displayed without a correct
    /// inverse-gamut tonemap. Use when the source content is sRGB-saturation
    /// and you want PQ HDR luminance without re-mapping primaries.
    /// </summary>
    public static CicpChunk SrgbPq => new(
        SharpAstro.Color.Icc.ColorPrimaries.BT709,
        SharpAstro.Color.Icc.TransferFunction.Pq,
        SharpAstro.Color.Icc.MatrixCoefficients.Identity,
        true);

    /// <summary>sRGB primaries + sRGB transfer — explicit signal of SDR sRGB.</summary>
    public static CicpChunk Srgb => new(
        SharpAstro.Color.Icc.ColorPrimaries.BT709,
        SharpAstro.Color.Icc.TransferFunction.Srgb,
        SharpAstro.Color.Icc.MatrixCoefficients.Identity,
        true);
}

/// <summary>
/// PNG-3 <c>mDCv</c> chunk — Mastering Display Color Volume (24 bytes).
/// Tells the renderer what reference display the content was mastered on,
/// so playback can tone-map appropriately. Mirrors the SMPTE ST 2086 /
/// HEVC SEI "mastering display color volume" payload.
/// </summary>
/// <param name="RedX">Raw u16; primaries are in units of 0.00002 (so 35400 ≈ 0.708).</param>
/// <param name="MaxLuminanceUnits">Raw u32; units of 0.0001 cd/m² (10000000 = 1000 cd/m²).</param>
public sealed record MdcvChunk(
    ushort RedX, ushort RedY,
    ushort GreenX, ushort GreenY,
    ushort BlueX, ushort BlueY,
    ushort WhitePointX, ushort WhitePointY,
    uint MaxLuminanceUnits,
    uint MinLuminanceUnits)
{
    internal static MdcvChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 24)
            throw new InvalidDataException($"mDCv chunk length must be 24, got {chunkData.Length}");
        return new MdcvChunk(
            RedX:        BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(0, 2)),
            RedY:        BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(2, 2)),
            GreenX:      BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(4, 2)),
            GreenY:      BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(6, 2)),
            BlueX:       BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(8, 2)),
            BlueY:       BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(10, 2)),
            WhitePointX: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(12, 2)),
            WhitePointY: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(14, 2)),
            MaxLuminanceUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(16, 4)),
            MinLuminanceUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(20, 4)));
    }

    internal void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(0, 2),  RedX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2),  RedY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4, 2),  GreenX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6, 2),  GreenY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(8, 2),  BlueX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(10, 2), BlueY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(12, 2), WhitePointX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(14, 2), WhitePointY);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(16, 4), MaxLuminanceUnits);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(20, 4), MinLuminanceUnits);
    }
}

/// <summary>
/// PNG-3 <c>cLLI</c> chunk — Content Light Level Information (8 bytes).
/// Describes the brightest pixel (<c>MaxCll</c>) and brightest average frame
/// (<c>MaxFall</c>) the content actually contains, so the renderer can
/// pre-allocate tone-mapping headroom. Both values are raw u32 in units of
/// 0.0001 cd/m² (i.e., divide by 10000 to get nits).
/// </summary>
public sealed record ClliChunk(uint MaxCllUnits, uint MaxFallUnits)
{
    internal static ClliChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 8)
            throw new InvalidDataException($"cLLI chunk length must be 8, got {chunkData.Length}");
        return new ClliChunk(
            MaxCllUnits:  BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(0, 4)),
            MaxFallUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4)));
    }

    internal void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0, 4), MaxCllUnits);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), MaxFallUnits);
    }
}
