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
        byte[]? iccProfileRaw = null;       // zlib-deflated, as on disk
        string? iccProfileName = null;
        byte[]? rawExif = null;
        bool sawSrgb = false;
        byte srgbRenderingIntent = 0;
        uint? gamma100k = null;
        ChromaticityChunk? chrm = null;

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
            // Any other chunk — silently skip. PNG is forwards-compatible by design:
            // ancillary unknown chunks are explicitly permitted, and critical unknown
            // chunks have a leading uppercase letter we'd need to reject. We defer the
            // critical-chunk rejection until we encounter a real one.

            pos += 8 + len + 4;
        }

        if (width is null) throw new InvalidDataException("No IHDR chunk found");
        if (idatChunks.Count == 0) throw new InvalidDataException("No IDAT chunks found");

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
            IccProfile = iccProfileRaw,
            IccProfileName = iccProfileName,
            SrgbRenderingIntent = sawSrgb ? srgbRenderingIntent : null,
            Gamma = gamma100k.HasValue ? gamma100k.Value / 100_000.0 : null,
            Chromaticity = chrm,
            Exif = rawExif,
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

        if (colorType == 3)
            throw new NotSupportedException("Indexed-color PNGs (ColorType=3) not yet supported");
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
}
