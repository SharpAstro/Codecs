using System.Buffers.Binary;
using ImageMagick;
using SharpAstro.Exr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// EXR Rung 4 — PIZ (wavelet + Huffman). The most involved scheme. Self round-trip is
/// bit-exact (the bitmap/LUT, wavelet and Huffman are all lossless and reversible); the
/// decisive checks are the Magick.NET (OpenEXR) cross-directions: OpenEXR must decode
/// our PIZ blocks, and we must decode OpenEXR's — value-exact within the ×65535 scaling.
/// Sizes include odd dimensions and &gt; 32 rows to exercise the wavelet odd-line/odd-column
/// paths and multi-block layout; diverse FLOAT data pushes the 16-bit (vs 14-bit) wavelet.
/// </summary>
public sealed class ExrPizTests
{
    private readonly ITestOutputHelper _out;
    public ExrPizTests(ITestOutputHelper output) => _out = output;

    private const float Q = 65535f;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(17, 13)]
    [InlineData(32, 32)]
    [InlineData(33, 40)]      // > 32 rows: multiple PIZ blocks + odd dimensions
    [InlineData(64, 70)]
    [InlineData(128, 96)]
    public void FloatMono_RoundTripsBitExact(int w, int h)
    {
        var y = ExrUncompressedTests.FloatChannelBytes(w, h, (x, yy) => MathF.Sin(x * 0.21f) * MathF.Cos(yy * 0.17f) * 1000f);
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Piz };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, y);

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.GetData("Y").ShouldBe(y);
    }

    [Theory]
    [InlineData(17, 13)]
    [InlineData(40, 40)]
    [InlineData(50, 70)]
    public void HalfRgb_RoundTripsBitExact(int w, int h)
    {
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Piz };
        img.AddChannel(new ExrChannel { Name = "R", Type = ExrPixelType.Half }, ExrUncompressedTests.HalfChannelBytes(w, h, 1));
        img.AddChannel(new ExrChannel { Name = "G", Type = ExrPixelType.Half }, ExrUncompressedTests.HalfChannelBytes(w, h, 2));
        img.AddChannel(new ExrChannel { Name = "B", Type = ExrPixelType.Half }, ExrUncompressedTests.HalfChannelBytes(w, h, 3));

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.GetData("R").ShouldBe(img.GetData("R"));
        rt.GetData("G").ShouldBe(img.GetData("G"));
        rt.GetData("B").ShouldBe(img.GetData("B"));
    }

    [Fact]
    public void FloatMono_DiverseData_RoundTripsBitExact()
    {
        // Many distinct 16-bit sub-words -> exercises the 16-bit wavelet path (maxValue >= 2^14).
        const int w = 200, h = 200;
        var rng = new Random(12345);
        var y = ExrUncompressedTests.FloatChannelBytes(w, h, (x, yy) => (float)(rng.NextDouble() * 2000.0 - 1000.0));
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Piz };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, y);

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.GetData("Y").ShouldBe(y);
    }

    [Theory]
    [InlineData(17, 13)]
    [InlineData(64, 70)]
    public void OurEncodePiz_ReadByMagick_ValuesMatch(int w, int h)
    {
        float F(int x, int y) => x * 0.05f - y * 0.02f + 0.3f;
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Piz };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, ExrUncompressedTests.FloatChannelBytes(w, h, F));

        using var m = new MagickImage(ExrFile.Write(img));
        m.Width.ShouldBe((uint)w); m.Height.ShouldBe((uint)h);
        using var px = m.GetPixels();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Close(px.GetPixel(x, y).ToArray()![0], F(x, y) * Q, $"Y({x},{y})");
    }

    [Theory]
    [InlineData(40, 50)]
    [InlineData(64, 70)]
    public void MagickEncodePiz_ReadByUs_ValuesMatch(int w, int h)
    {
        using var m = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        m.ColorSpace = ColorSpace.RGB;
        using (var px = m.GetPixels())
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    px.SetPixel(x, y, [(x * 0.02f + 0.1f) * Q, (y * 0.015f) * Q, 0.4f * Q]);
        m.Format = MagickFormat.Exr;
        m.Settings.Compression = CompressionMethod.Piz;
        var bytes = m.ToByteArray();

        var img = ExrFile.Read(bytes);
        _out.WriteLine($"Magick wrote {img.Compression}");
        img.Compression.ShouldBe(ExrCompression.Piz);
        img.Width.ShouldBe(w); img.Height.ShouldBe(h);

        using var rt = m.GetPixels();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var e = rt.GetPixel(x, y).ToArray()!;
                Close(ReadFloatOrHalf(img, "R", w, x, y) * Q, e[0], $"R({x},{y})");
                Close(ReadFloatOrHalf(img, "G", w, x, y) * Q, e[1], $"G({x},{y})");
                Close(ReadFloatOrHalf(img, "B", w, x, y) * Q, e[2], $"B({x},{y})");
            }
    }

    [Fact]
    public void DiverseFloat_PizInterop_BothDirections()
    {
        // Diverse data -> many distinct 16-bit words -> the 16-bit wavelet path; verify
        // OpenEXR interop both ways for that path (the gradient cases above use 14-bit).
        const int w = 160, h = 120;
        var rng = new Random(99);
        var vals = new float[w * h];
        for (var i = 0; i < vals.Length; i++) vals[i] = (float)(rng.NextDouble() * 4000.0 - 2000.0);

        // our-encode (PIZ) -> Magick decode
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Piz };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float },
            ExrUncompressedTests.FloatChannelBytes(w, h, (x, y) => vals[y * w + x]));
        using (var m = new MagickImage(ExrFile.Write(img)))
        using (var px = m.GetPixels())
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    Close(px.GetPixel(x, y).ToArray()![0], vals[y * w + x] * Q, $"enc Y({x},{y})");

        // Magick-encode (PIZ) -> our decode
        using var m2 = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        m2.ColorSpace = ColorSpace.RGB;
        using (var px = m2.GetPixels())
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    px.SetPixel(x, y, [vals[y * w + x] * Q, 0f, 0f]);
        m2.Format = MagickFormat.Exr;
        m2.Settings.Compression = CompressionMethod.Piz;
        var dec = ExrFile.Read(m2.ToByteArray());
        using (var rt = m2.GetPixels())
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    Close(ReadFloatOrHalf(dec, "R", w, x, y) * Q, rt.GetPixel(x, y).ToArray()![0], $"dec R({x},{y})");
    }

    // ----------------------------------------------------------------- helpers

    private void Close(float got, float expected, string where)
    {
        float tol = Math.Max(1.0f, Math.Abs(expected) * 1e-3f);
        if (Math.Abs(got - expected) > tol) _out.WriteLine($"MISMATCH {where}: got {got}, expected {expected}");
        Math.Abs(got - expected).ShouldBeLessThanOrEqualTo(tol, where);
    }

    private static float ReadFloatOrHalf(ExrImage img, string ch, int w, int x, int y)
    {
        int i = img.IndexOf(ch);
        var data = img.GetData(i);
        return img.Channels[i].Type == ExrPixelType.Half
            ? (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((y * w + x) * 2, 2)))
            : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((y * w + x) * 4, 4));
    }
}
