using System.Buffers.Binary;
using System.IO.Compression;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Round-trip tests for the new <see cref="PngReader"/> against the existing
/// <see cref="PngWriter"/>. Encode-then-decode must reproduce the original
/// sample data byte-for-byte; ancillary chunks the writer emits (iCCP, sRGB)
/// must come back through the reader.
/// </summary>
public sealed class PngReaderTests
{
    [Fact]
    public void Rgba8_RoundTrip_RecoversPixels()
    {
        const int w = 8, h = 6;
        var src = new byte[w * h * 4];
        var rng = new Random(unchecked((int)0xA98A98A9));
        rng.NextBytes(src);

        var png = PngWriter.Encode(src, w, h);
        var img = PngReader.Decode(png);

        img.Width.ShouldBe(w);
        img.Height.ShouldBe(h);
        img.BitDepth.ShouldBe(8);
        img.ColorType.ShouldBe(6);
        img.Pixels.ShouldBe(src);
    }

    [Fact]
    public void Gray8_RoundTrip()
    {
        const int w = 12, h = 10;
        var src = new byte[w * h];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)((i * 17 + 3) & 0xFF);

        var png = PngWriter.EncodeGray8(src, w, h);
        var img = PngReader.Decode(png);

        img.ColorType.ShouldBe(0);
        img.BitDepth.ShouldBe(8);
        img.Pixels.ShouldBe(src);
    }

    [Fact]
    public void Gray16_RoundTrip_PreservesEachSample()
    {
        const int w = 8, h = 8;
        var src = new ushort[w * h];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)(i * 1031);

        var png = PngWriter.EncodeGray16(src, w, h);
        var img = PngReader.Decode(png);

        img.BitDepth.ShouldBe(16);
        img.ColorType.ShouldBe(0);
        // 16-bit samples come back in PNG's big-endian byte order; ushort view normalises.
        var decoded = img.AsUInt16Samples();
        decoded.ShouldBe(src);
    }

    [Fact]
    public void Rgba16_RoundTrip_HdrTarget()
    {
        // The killer case: 16-bit RGBA. SharpAstro.StbImage drops these to 8-bit on
        // decode — this reader is the first managed path that round-trips faithfully.
        const int w = 16, h = 12;
        var src = new ushort[w * h * 4];
        var rng = new Random(unchecked((int)0x1606_1606));
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        var png = PngWriter.EncodeRgba16(src, w, h);
        var img = PngReader.Decode(png);

        img.BitDepth.ShouldBe(16);
        img.ColorType.ShouldBe(6);
        img.Width.ShouldBe(w);
        img.Height.ShouldBe(h);
        img.AsUInt16Samples().ShouldBe(src);
    }

    [Fact]
    public void IccProfile_RoundTrip_ThroughIccpChunk()
    {
        // Encode with an embedded ICC profile (just any byte payload — the chunk
        // is content-neutral) and verify the reader extracts it back.
        var src = new byte[4 * 4 * 4];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)i;
        var icc = new byte[100];
        for (var i = 0; i < icc.Length; i++) icc[i] = (byte)(0xA0 + (i & 0x1F));

        var png = PngWriter.Encode(src, 4, 4, icc);
        var img = PngReader.Decode(png);

        img.IccProfile.ShouldNotBeNull();
        img.IccProfile!.ShouldBe(icc);
        img.IccProfileName.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void EmptyPng_ThrowsOnSignatureMismatch()
    {
        var notAPng = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        Should.Throw<InvalidDataException>(() => PngReader.Decode(notAPng));
    }

    [Fact]
    public void CorruptedCrc_Throws()
    {
        // Build a real PNG then flip one byte inside an IDAT chunk's CRC.
        var src = new byte[4 * 4];
        var png = PngWriter.EncodeGray8(src, 4, 4);
        // Find any IDAT chunk by searching for "IDAT" then flip the CRC byte after it.
        var idatIdx = IndexOfAscii(png, "IDAT");
        idatIdx.ShouldBeGreaterThan(0);
        var dataLen = BitConverter.ToUInt32(new[] { png[idatIdx - 1], png[idatIdx - 2], png[idatIdx - 3], png[idatIdx - 4] }, 0);
        var crcOffset = idatIdx + 4 + (int)dataLen;
        png[crcOffset] ^= 0xFF;

        Should.Throw<InvalidDataException>(() => PngReader.Decode(png));
    }

    [Fact]
    public void TruncatedFile_Throws()
    {
        var src = new byte[16];
        var png = PngWriter.EncodeGray8(src, 4, 4);
        Should.Throw<InvalidDataException>(() => PngReader.Decode(png.AsSpan(0, png.Length - 5).ToArray()));
    }

    [Fact]
    public void Rgba8_Various_Sizes()
    {
        // Each size exercises a different mix of filter selections.
        foreach (var (w, h) in new (int, int)[] { (1, 1), (3, 5), (17, 19), (64, 1), (1, 64), (33, 47) })
        {
            var src = new byte[w * h * 4];
            for (var i = 0; i < src.Length; i++) src[i] = (byte)((i * 37 + w + h) & 0xFF);

            var png = PngWriter.Encode(src, w, h);
            var img = PngReader.Decode(png);

            img.Width.ShouldBe(w);
            img.Height.ShouldBe(h);
            img.Pixels.ShouldBe(src, $"size {w}x{h}");
        }
    }

    [Fact]
    public void Indexed8_Decodes_KeepingIndicesAndSurfacingPaletteAndTrns()
    {
        // PngWriter can't emit indexed color, so hand-build a minimal 8-bit
        // indexed PNG (PLTE + tRNS) — the shape Noto Color Emoji's CBDT glyphs use.
        const int w = 2, h = 3;
        byte[] palette = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120]; // 4 RGB entries
        byte[] trns = [0, 255, 128]; // entry 3 absent -> opaque
        byte[] indices = [0, 1, 2, 3, 1, 0]; // row-major, one index per pixel

        var png = BuildIndexedPng(w, h, palette, trns, indices);
        var img = PngReader.Decode(png);

        img.Width.ShouldBe(w);
        img.Height.ShouldBe(h);
        img.BitDepth.ShouldBe(8);
        img.ColorType.ShouldBe(3);                // faithful to the file; indices not expanded
        img.Pixels.ShouldBe(indices);
        img.Palette.ShouldBe(palette);
        img.PaletteAlpha.ShouldBe(trns);
    }

    [Fact]
    public void Indexed_MissingPlte_Throws()
    {
        var png = BuildIndexedPng(1, 1, palette: null, trns: null, indices: [0]);
        Should.Throw<InvalidDataException>(() => PngReader.Decode(png));
    }

    /// <summary>
    /// Assemble a valid 8-bit indexed PNG: signature + IHDR + optional PLTE +
    /// optional tRNS + IDAT (filter-type-0 rows, zlib-deflated) + IEND, each
    /// chunk carrying a correct CRC32.
    /// </summary>
    private static byte[] BuildIndexedPng(int w, int h, byte[]? palette, byte[]? trns, byte[] indices)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // signature

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 3; // color type = indexed
        // ihdr[10..12] = compression/filter/interlace = 0
        WriteChunk(ms, "IHDR"u8, ihdr);

        if (palette is not null) WriteChunk(ms, "PLTE"u8, palette);
        if (trns is not null) WriteChunk(ms, "tRNS"u8, trns);

        // Filter type 0 (None) prefixed on each row.
        var raw = new byte[(w + 1) * h];
        for (var y = 0; y < h; y++)
        {
            raw[y * (w + 1)] = 0;
            Array.Copy(indices, y * w, raw, y * (w + 1) + 1, w);
        }
        WriteChunk(ms, "IDAT"u8, ZlibCompress(raw));
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        uint c = 0xFFFFFFFFu;
        foreach (var x in type) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in data) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, c ^ 0xFFFFFFFFu);
        s.Write(crc);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var outMs = new MemoryStream();
        using (var z = new ZLibStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return outMs.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            t[n] = c;
        }
        return t;
    }

    private static int IndexOfAscii(byte[] data, string needle)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i + bytes.Length <= data.Length; i++)
        {
            var match = true;
            for (var j = 0; j < bytes.Length; j++)
                if (data[i + j] != bytes[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
