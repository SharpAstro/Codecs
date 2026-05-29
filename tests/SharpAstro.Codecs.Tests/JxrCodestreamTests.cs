using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 7e (part 1) — the single-tile SPATIAL OL_NONE YUV444 BD8 codestream
/// assembler (<see cref="JxrCodestream"/>) + container façade
/// (<see cref="JxrImageCodec"/>). Two checks here, both oracle-free:
/// <list type="number">
///   <item>the fixed codestream <b>header</b> bytes (IMAGE_HEADER +
///   IMAGE_PLANE_HEADER + PROFILE_LEVEL_INFO + PACKET_HEADER) match jxrlib's
///   reference encoder byte-for-byte (<c>JxrEncApp -f -l 0 -d 3 -q 1 -c 9</c>);</item>
///   <item>pixels survive an our-encode ↔ our-decode round-trip losslessly.</item>
/// </list>
/// The bit-exact MB-data check against the reference decoder lives in the oracle suite.
/// </summary>
public sealed class JxrCodestreamTests
{
    // First 39 bytes of the codestream JxrEncApp emits for a 16×16 RGB image with
    // -f (spatial) -l 0 (OL_NONE) -d 3 (YUV444) -q 1 (lossless): the WMPHOTO image
    // header, the YUV444 all-bands uniform-QP=0 plane header, the profile/level
    // record, and the spatial packet header. (Lifted from a hexdump of the file.)
    private static readonly byte[] ReferenceHeader =
    {
        0x57, 0x4D, 0x50, 0x48, 0x4F, 0x54, 0x4F, 0x00, // "WMPHOTO\0"
        0x11, 0x00,                                     // RESERVED_B=1; spatial, OL_NONE, no index table
        0xC0, 0x71, 0x00, 0x0F, 0x00, 0x0F,             // short+long word, RGB/BD8, 16×16
        0x60, 0x00, 0xC0, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, // plane header
        0x00, 0x04, 0x6F, 0xFF, 0x00, 0x01,             // PROFILE_LEVEL_INFO (profile 111, level 255)
        0x00, 0x00, 0x01, 0x00,                         // PACKET_HEADER (spatial, tile 0)
    };

    private static (int[] r, int[] g, int[] b) Flat(int w, int h, int r, int g, int b)
    {
        var ra = new int[w * h]; var ga = new int[w * h]; var ba = new int[w * h];
        Array.Fill(ra, r); Array.Fill(ga, g); Array.Fill(ba, b);
        return (ra, ga, ba);
    }

    private static (int[] r, int[] g, int[] b) Gradient(int w, int h)
    {
        var r = new int[w * h]; var g = new int[w * h]; var b = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = (x * 3 + y * 2) & 0xff;
                g[i] = (128 + x - y) & 0xff;
                b[i] = (x + y * 3) & 0xff;
            }
        return (r, g, b);
    }

    [Fact]
    public void Header_MatchesJxrlibReference()
    {
        // Same flat colour as the reference BMP (R=100, G=150, B=200). The header
        // is content-independent, but use the matching image for clarity.
        var (r, g, b) = Flat(16, 16, 100, 150, 200);
        var cs = JxrCodestream.Encode(r, g, b, 16, 16);

        cs.Length.ShouldBeGreaterThan(ReferenceHeader.Length);
        for (var i = 0; i < ReferenceHeader.Length; i++)
            cs[i].ShouldBe(ReferenceHeader[i], $"codestream byte {i} (0x{i:X2})");
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 16)]
    [InlineData(16, 32)]
    [InlineData(48, 32)]
    [InlineData(64, 48)]
    public void Codestream_RoundTrip_Lossless(int w, int h)
    {
        var (r, g, b) = Gradient(w, h);
        var cs = JxrCodestream.Encode(r, g, b, w, h);
        var (dw, dh, dr, dg, db) = JxrCodestream.Decode(cs);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
        {
            dr[i].ShouldBe(r[i], $"R[{i}]");
            dg[i].ShouldBe(g[i], $"G[{i}]");
            db[i].ShouldBe(b[i], $"B[{i}]");
        }
    }

    [Theory]
    [InlineData(16, 16, 1)]
    [InlineData(32, 16, 1)]
    [InlineData(16, 32, 1)]
    [InlineData(48, 32, 1)]
    [InlineData(64, 48, 1)]
    [InlineData(80, 80, 1)]
    [InlineData(16, 16, 2)]
    [InlineData(32, 16, 2)]
    [InlineData(16, 32, 2)]
    [InlineData(48, 32, 2)]
    [InlineData(64, 48, 2)]
    [InlineData(80, 80, 2)]
    public void Codestream_RoundTrip_Lossless_Overlap(int w, int h, int overlap)
    {
        // Self round-trip with the Photo Overlap filter on: the inverse overlap is the
        // exact structural inverse of the forward (proven in 7f.0), so lossless identity
        // must hold. (Conformance vs jxrlib is the oracle suite's job.)
        var (r, g, b) = Gradient(w, h);
        var cs = JxrCodestream.Encode(r, g, b, w, h, overlap: overlap);
        var (dw, dh, dr, dg, db) = JxrCodestream.Decode(cs);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
        {
            dr[i].ShouldBe(r[i], $"R[{i}] (overlap {overlap}, {w}x{h})");
            dg[i].ShouldBe(g[i], $"G[{i}] (overlap {overlap}, {w}x{h})");
            db[i].ShouldBe(b[i], $"B[{i}] (overlap {overlap}, {w}x{h})");
        }
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    public void Container_RoundTrip_Lossless(int w, int h)
    {
        var (r, g, b) = Gradient(w, h);
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h);

        // The container must parse as a valid JXR file with matching dimensions.
        var file = JxrContainer.Read(jxr);
        file.Width.ShouldBe((uint)w);
        file.Height.ShouldBe((uint)h);

        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr);
        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
        {
            dr[i].ShouldBe(r[i], $"R[{i}]");
            dg[i].ShouldBe(g[i], $"G[{i}]");
            db[i].ShouldBe(b[i], $"B[{i}]");
        }
    }
}
