using System.Buffers.Binary;
using ImageMagick;
using SharpAstro.Exr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// EXR Rung 2 — uncompressed scanline pixels. Self round-trip is bit-exact (our
/// encode → our decode); the Magick.NET probe measures how ImageMagick (Q16-HDRI,
/// which links OpenEXR) maps EXR sample values into its quantum domain so the
/// cross-checks in <see cref="ExrOracleTests"/> can assert correctly.
/// </summary>
public sealed class ExrUncompressedTests
{
    private readonly ITestOutputHelper _out;
    public ExrUncompressedTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(16, 16)]
    [InlineData(37, 19)]
    [InlineData(64, 40)]
    public void FloatMono_RoundTripsBitExact(int w, int h)
    {
        var y = FloatChannelBytes(w, h, (x, yy) => (x * 1.5f - yy * 0.25f));
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.None };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, y);

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.Width.ShouldBe(w); rt.Height.ShouldBe(h);
        rt.GetData("Y").ShouldBe(y);
    }

    [Theory]
    [InlineData(7, 3)]
    [InlineData(37, 19)]
    [InlineData(64, 40)]
    public void HalfRgb_RoundTripsBitExact(int w, int h)
    {
        var (r, g, b) = (HalfChannelBytes(w, h, 1), HalfChannelBytes(w, h, 2), HalfChannelBytes(w, h, 3));
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.None };
        img.AddChannel(new ExrChannel { Name = "R", Type = ExrPixelType.Half }, r);
        img.AddChannel(new ExrChannel { Name = "G", Type = ExrPixelType.Half }, g);
        img.AddChannel(new ExrChannel { Name = "B", Type = ExrPixelType.Half }, b);

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.GetData("R").ShouldBe(r);
        rt.GetData("G").ShouldBe(g);
        rt.GetData("B").ShouldBe(b);
        // Channels come back in ascending name order (B, G, R).
        rt.Channels.Select(c => c.Name).ShouldBe(["B", "G", "R"]);
    }

    [Fact]
    public void MagickProbe_ReadsOurFloatExr_AndReportsScaling()
    {
        const int w = 4, h = 4;
        // Known values at a few pixels.
        var y = FloatChannelBytes(w, h, (x, yy) => x == 1 && yy == 1 ? 2.5f : (x == 2 && yy == 2 ? 0.5f : 0f));
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.None };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, y);
        var bytes = ExrFile.Write(img);
        _out.WriteLine($"our EXR: {bytes.Length} bytes, magic {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");

        using var m = new MagickImage(bytes);
        _out.WriteLine($"Magick read: {m.Width}x{m.Height} fmt={m.Format} depth={m.Depth} channels={m.ChannelCount}");
        m.Width.ShouldBe((uint)w);
        m.Height.ShouldBe((uint)h);

        using var px = m.GetPixels();
        float Sample(int x, int yy) => px.GetPixel(x, yy).ToArray()![0];
        _out.WriteLine($"QuantumRange = {Quantum.Max}");
        _out.WriteLine($"Magick pixel(1,1) [exr 2.5] = {Sample(1, 1)}  ratio={Sample(1, 1) / 2.5f}");
        _out.WriteLine($"Magick pixel(2,2) [exr 0.5] = {Sample(2, 2)}  ratio={Sample(2, 2) / 0.5f}");
        _out.WriteLine($"Magick pixel(0,0) [exr 0.0] = {Sample(0, 0)}");
    }

    // ----------------------------------------------------------------- helpers

    internal static byte[] FloatChannelBytes(int w, int h, Func<int, int, float> f)
    {
        var bytes = new byte[w * h * 4];
        for (var yy = 0; yy < h; yy++)
            for (var x = 0; x < w; x++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((yy * w + x) * 4, 4), f(x, yy));
        return bytes;
    }

    internal static byte[] HalfChannelBytes(int w, int h, int salt)
    {
        var bytes = new byte[w * h * 2];
        for (var yy = 0; yy < h; yy++)
            for (var x = 0; x < w; x++)
            {
                var v = (Half)((x * 0.1f + yy * 0.05f) * salt);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan((yy * w + x) * 2, 2), BitConverter.HalfToInt16Bits(v));
            }
        return bytes;
    }
}
