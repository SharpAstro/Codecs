using System.Buffers.Binary;
using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-1 tests for the JXR tag-based container (T.832 Annex A). The codestream
/// payload is synthetic — Phase 1 of SharpAstro.Jxr treats it as opaque, so
/// these tests only verify that the FILE_HEADER + IFD + codestream bytes round-trip.
/// </summary>
public sealed class JxrContainerRoundTripTests
{
    private static byte[] FakeCodestream(int length, int seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)((i * 31 + seed) & 0xFF);
        return bytes;
    }

    [Fact]
    public void FileHeader_HasFixedSignature()
    {
        var file = new JxrFile(
            Width: 4, Height: 3,
            PixelFormat: JxrPixelFormat.Rgb24Bpp,
            Codestream: FakeCodestream(64, 1));

        var bytes = JxrContainer.Write(file);

        // T.832 Annex A clause A.5: II 0xBC 0x01 + FIRST_IFD_OFFSET (le32).
        bytes[0].ShouldBe((byte)'I');
        bytes[1].ShouldBe((byte)'I');
        bytes[2].ShouldBe((byte)0xBC);
        bytes[3].ShouldBe((byte)0x01);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)).ShouldBe(8u);
    }

    [Fact]
    public void Minimal_RoundTrips()
    {
        var codestream = FakeCodestream(127, 42);
        var written = JxrContainer.Write(new JxrFile(
            Width: 256, Height: 128,
            PixelFormat: JxrPixelFormat.Bgra32Bpp,
            Codestream: codestream));

        var read = JxrContainer.Read(written);

        read.Width.ShouldBe(256u);
        read.Height.ShouldBe(128u);
        read.PixelFormat.ShouldBe(JxrPixelFormat.Bgra32Bpp);
        read.Codestream.ShouldBe(codestream);
        read.AlphaCodestream.ShouldBeNull();
        read.IccProfile.ShouldBeNull();
        read.XmpMetadata.ShouldBeNull();
        // Writer now emits SpatialXfrmPrimary / Resolution X/Y as defaults
        // (0 / 96 dpi) so WIC's WMPhoto decoder accepts the file. Round-trip
        // surfaces those defaults rather than null.
        read.SpatialXfrmPrimary.ShouldBe(0u);
        read.WidthResolution.ShouldBe(96.0f);
        read.HeightResolution.ShouldBe(96.0f);
    }

    [Fact]
    public void WithAlphaPlane_RoundTrips()
    {
        var primary = FakeCodestream(200, 1);
        var alpha = FakeCodestream(53, 2);
        var written = JxrContainer.Write(new JxrFile(
            Width: 100, Height: 60,
            PixelFormat: JxrPixelFormat.Bgra32Bpp,
            Codestream: primary,
            AlphaCodestream: alpha));

        var read = JxrContainer.Read(written);

        read.Codestream.ShouldBe(primary);
        read.AlphaCodestream.ShouldNotBeNull().ShouldBe(alpha);
    }

    [Fact]
    public void WithMetadataBlobs_RoundTrips()
    {
        var icc = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44 };
        var xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/' />"u8.ToArray();
        var written = JxrContainer.Write(new JxrFile(
            Width: 32, Height: 32,
            PixelFormat: JxrPixelFormat.Rgb48Bpp,
            Codestream: FakeCodestream(89, 3),
            IccProfile: icc,
            XmpMetadata: xmp,
            SpatialXfrmPrimary: 3,
            WidthResolution: 96.0f,
            HeightResolution: 96.0f));

        var read = JxrContainer.Read(written);

        read.IccProfile.ShouldNotBeNull().ShouldBe(icc);
        read.XmpMetadata.ShouldNotBeNull().ShouldBe(xmp);
        read.SpatialXfrmPrimary.ShouldBe(3u);
        read.WidthResolution.ShouldBe(96.0f);
        read.HeightResolution.ShouldBe(96.0f);
    }

    [Fact]
    public void InlinedMetadata_RoundTrips()
    {
        // ICC profile ≤ 4 bytes is stored inline in VALUES_OR_OFFSET, not at a file offset.
        // Exercises the inline branch of GetBytes.
        var icc = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var written = JxrContainer.Write(new JxrFile(
            Width: 1, Height: 1,
            PixelFormat: JxrPixelFormat.Gray8Bpp,
            Codestream: FakeCodestream(8, 0),
            IccProfile: icc));

        var read = JxrContainer.Read(written);
        read.IccProfile.ShouldNotBeNull().ShouldBe(icc);
    }

    [Fact]
    public void WriteThenReadStream_RoundTrips()
    {
        var ms = new MemoryStream();
        JxrContainer.Write(new JxrFile(
            Width: 8, Height: 8,
            PixelFormat: JxrPixelFormat.Rgb24Bpp,
            Codestream: FakeCodestream(40, 9)), ms);

        ms.Position = 0;
        var read = JxrContainer.Read(ms);
        read.Width.ShouldBe(8u);
        read.Height.ShouldBe(8u);
    }

    [Fact]
    public void EveryDeclaredPixelFormat_RoundTripsByteExact()
    {
        // Touch every named JxrPixelFormat constant — confirms each one writes a
        // 16-byte payload that reads back equal, and that no two constants collide.
        var allFormats = new[]
        {
            JxrPixelFormat.BlackWhite, JxrPixelFormat.Gray8Bpp, JxrPixelFormat.Bgr16Bpp555,
            JxrPixelFormat.Bgr16Bpp565, JxrPixelFormat.Gray16Bpp, JxrPixelFormat.Bgr24Bpp,
            JxrPixelFormat.Rgb24Bpp, JxrPixelFormat.Bgr32Bpp, JxrPixelFormat.Bgra32Bpp,
            JxrPixelFormat.Pbgra32Bpp, JxrPixelFormat.GrayFloat32Bpp, JxrPixelFormat.RgbFixedPoint48Bpp,
            JxrPixelFormat.GrayFixedPoint16Bpp, JxrPixelFormat.Bgr101010_32Bpp, JxrPixelFormat.Rgb48Bpp,
            JxrPixelFormat.Rgba64Bpp, JxrPixelFormat.Prgba64Bpp, JxrPixelFormat.RgbFixedPoint96Bpp,
            JxrPixelFormat.RgbaFloat128Bpp, JxrPixelFormat.PrgbaFloat128Bpp, JxrPixelFormat.RgbFloat128Bpp,
            JxrPixelFormat.Cmyk32Bpp, JxrPixelFormat.RgbaFixedPoint64Bpp, JxrPixelFormat.RgbaFixedPoint128Bpp,
            JxrPixelFormat.Cmyk64Bpp, JxrPixelFormat.Channels3_24Bpp, JxrPixelFormat.Channels8_64Bpp,
            JxrPixelFormat.RgbaHalf64Bpp, JxrPixelFormat.RgbHalf48Bpp, JxrPixelFormat.Rgbe32Bpp,
            JxrPixelFormat.GrayHalf16Bpp, JxrPixelFormat.GrayFixedPoint32Bpp,
            JxrPixelFormat.RgbFixedPoint64Bpp, JxrPixelFormat.RgbFixedPoint128Bpp, JxrPixelFormat.RgbHalf64Bpp,
            JxrPixelFormat.Ycc420_12Bpp, JxrPixelFormat.Ycc422_16Bpp, JxrPixelFormat.Ycc444_24Bpp,
            JxrPixelFormat.CmykDirect32Bpp, JxrPixelFormat.CmykDirect64Bpp,
        };

        var seen = new HashSet<string>();
        foreach (var pf in allFormats)
        {
            var written = JxrContainer.Write(new JxrFile(
                Width: 2, Height: 2,
                PixelFormat: pf,
                Codestream: FakeCodestream(8, 0)));
            var read = JxrContainer.Read(written);
            read.PixelFormat.ShouldBe(pf);
            seen.Add(pf.ToString()).ShouldBeTrue($"duplicate GUID: {pf}");
        }
    }

    [Fact]
    public void MissingRequiredTag_ThrowsOnRead()
    {
        // Build a malformed JXR file that's missing PIXEL_FORMAT. Manually craft a
        // header + IFD that mentions only IMAGE_WIDTH / IMAGE_HEIGHT / IMAGE_OFFSET /
        // IMAGE_BYTE_COUNT (which makes it valid on every axis except PixelFormat).
        var bytes = new byte[64];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'I'; bytes[2] = 0xBC; bytes[3] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8); // FIRST_IFD_OFFSET

        const int numEntries = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), numEntries);
        WriteEntry(bytes, 10, 0xBC80, 4, 1, 16); // ImageWidth = 16
        WriteEntry(bytes, 22, 0xBC81, 4, 1, 16); // ImageHeight = 16
        WriteEntry(bytes, 34, 0xBCC0, 4, 1, 0); // ImageOffset = 0 (bogus but parse-shaped)
        WriteEntry(bytes, 46, 0xBCC1, 4, 1, 0); // ImageByteCount = 0

        Should.Throw<InvalidDataException>(() => JxrContainer.Read(bytes))
            .Message.ShouldContain("PIXEL_FORMAT");

        static void WriteEntry(byte[] buf, int off, ushort tag, ushort type, uint num, uint valOrOff)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 2, 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 4, 4), num);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 8, 4), valOrOff);
        }
    }

    [Fact]
    public void WrongSignature_Throws()
    {
        var bytes = new byte[16];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'M'; bytes[2] = 0xBC; bytes[3] = 0x01;
        Should.Throw<InvalidDataException>(() => JxrContainer.Read(bytes))
            .Message.ShouldContain("signature");
    }

    [Fact]
    public void WrongMagicByte_Throws()
    {
        var bytes = new byte[16];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'I'; bytes[2] = 0x2A; bytes[3] = 0x00; // TIFF magic
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        Should.Throw<InvalidDataException>(() => JxrContainer.Read(bytes))
            .Message.ShouldContain("0xBC");
    }

    [Fact]
    public void RealWorldFixture_SeagullNebula_Parses()
    {
        // 2963 × 2991 BGRA32 JXR produced by an HDR processing tool — has a separate
        // alpha plane and uses the standard 72-dpi resolution tags. The codestream is
        // opaque to Phase 1 but we sanity-check the WMPHOTO codestream magic (T.832 §8.4.1).
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "seagull_nebula.jxr");
        var bytes = File.ReadAllBytes(path);

        var file = JxrContainer.Read(bytes);

        file.Width.ShouldBe(2963u);
        file.Height.ShouldBe(2991u);
        file.PixelFormat.ShouldBe(JxrPixelFormat.Bgra32Bpp);
        file.WidthResolution.ShouldBe(72.0f);
        file.HeightResolution.ShouldBe(72.0f);
        file.SpatialXfrmPrimary.ShouldBe(0u);

        // Primary codestream is a real WMPHOTO bitstream — verify the 8-byte magic
        // at the head ("WMPHOTO\0" per T.832 §8.4.1).
        file.Codestream.Length.ShouldBeGreaterThan(8);
        var primaryMagic = System.Text.Encoding.ASCII.GetString(file.Codestream, 0, 7);
        primaryMagic.ShouldBe("WMPHOTO");
        file.Codestream[7].ShouldBe((byte)0);

        // Separate alpha plane is itself a WMPHOTO codestream.
        file.AlphaCodestream.ShouldNotBeNull().Length.ShouldBeGreaterThan(8);
        var alphaMagic = System.Text.Encoding.ASCII.GetString(file.AlphaCodestream!, 0, 7);
        alphaMagic.ShouldBe("WMPHOTO");
    }

    [Fact]
    public void RealWorldFixture_HdrFloat128Bpp_Parses()
    {
        // 1000×1000 RgbFloat128Bpp (BD32F) HDR JXR produced by Microsoft's WMP
        // encoder via the WIC HDR wallpaper pipeline (Spruill-1/JXRCreator MIT
        // sample). Different encoder than the user's astro JXRs, so this also
        // serves as a cross-validation that our reader handles producer-diverse
        // tag orderings and the float-pixel-format GUID variant.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hdr_128bpp_float_sample.jxr");
        var file = JxrContainer.Read(File.ReadAllBytes(path));

        file.Width.ShouldBe(1000u);
        file.Height.ShouldBe(1000u);
        file.PixelFormat.ShouldBe(JxrPixelFormat.RgbFloat128Bpp);
        file.AlphaCodestream.ShouldBeNull();
        file.Codestream.Length.ShouldBeGreaterThan(8);
        System.Text.Encoding.ASCII.GetString(file.Codestream, 0, 7).ShouldBe("WMPHOTO");
    }

    [Fact]
    public void RealWorldFixture_SeagullNebula_RoundTrips()
    {
        // Read → Write → Read again, and confirm semantically equal output. The
        // bytes won't match byte-for-byte (our writer's layout differs from the
        // source encoder — different IFD entry ordering, no trailing padding) but
        // the round-tripped JxrFile fields and codestream blobs must match.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "seagull_nebula.jxr");
        var original = JxrContainer.Read(File.ReadAllBytes(path));

        var rewritten = JxrContainer.Write(original);
        var reread = JxrContainer.Read(rewritten);

        reread.Width.ShouldBe(original.Width);
        reread.Height.ShouldBe(original.Height);
        reread.PixelFormat.ShouldBe(original.PixelFormat);
        reread.SpatialXfrmPrimary.ShouldBe(original.SpatialXfrmPrimary);
        reread.WidthResolution.ShouldBe(original.WidthResolution);
        reread.HeightResolution.ShouldBe(original.HeightResolution);
        reread.Codestream.ShouldBe(original.Codestream);
        reread.AlphaCodestream.ShouldNotBeNull().ShouldBe(original.AlphaCodestream!);
    }
}
