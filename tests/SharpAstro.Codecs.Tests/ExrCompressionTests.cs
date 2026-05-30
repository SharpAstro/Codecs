using System.Buffers.Binary;
using ImageMagick;
using SharpAstro.Exr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// EXR Rung 3 — RLE / ZIP / ZIPS compression. Self round-trip is bit-exact (the
/// reorder+predictor+entropy transform is lossless); Magick.NET cross-checks confirm
/// interop (our compressed blocks inflate in OpenEXR, and OpenEXR's inflate through
/// us) value-exact within the ×QuantumRange (65535) scaling.
/// </summary>
public sealed class ExrCompressionTests
{
    private readonly ITestOutputHelper _out;
    public ExrCompressionTests(ITestOutputHelper output) => _out = output;

    private const float Q = 65535f;

    [Theory]
    [InlineData(ExrCompression.Rle, 16, 16)]
    [InlineData(ExrCompression.Rle, 37, 40)]
    [InlineData(ExrCompression.Zips, 37, 40)]
    [InlineData(ExrCompression.Zip, 16, 16)]
    [InlineData(ExrCompression.Zip, 64, 40)]   // > 16 rows: multiple ZIP blocks + partial last block
    [InlineData(ExrCompression.Zip, 13, 33)]
    public void FloatMono_RoundTripsBitExact(ExrCompression comp, int w, int h)
    {
        var y = ExrUncompressedTests.FloatChannelBytes(w, h, (x, yy) => MathF.Sin(x * 0.3f) + yy * 0.01f);
        var img = new ExrImage { Width = w, Height = h, Compression = comp };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, y);

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.GetData("Y").ShouldBe(y);
    }

    [Theory]
    [InlineData(ExrCompression.Rle, 40, 24)]
    [InlineData(ExrCompression.Zip, 40, 40)]
    [InlineData(ExrCompression.Zips, 19, 17)]
    public void HalfRgb_RoundTripsBitExact(ExrCompression comp, int w, int h)
    {
        var img = new ExrImage { Width = w, Height = h, Compression = comp };
        img.AddChannel(new ExrChannel { Name = "R", Type = ExrPixelType.Half }, ExrUncompressedTests.HalfChannelBytes(w, h, 1));
        img.AddChannel(new ExrChannel { Name = "G", Type = ExrPixelType.Half }, ExrUncompressedTests.HalfChannelBytes(w, h, 2));
        img.AddChannel(new ExrChannel { Name = "B", Type = ExrPixelType.Half }, ExrUncompressedTests.HalfChannelBytes(w, h, 3));

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.GetData("R").ShouldBe(img.GetData("R"));
        rt.GetData("G").ShouldBe(img.GetData("G"));
        rt.GetData("B").ShouldBe(img.GetData("B"));
    }

    [Theory]
    [InlineData(ExrCompression.Rle)]
    [InlineData(ExrCompression.Zip)]
    [InlineData(ExrCompression.Zips)]
    public void OurEncode_Compressed_ReadByMagick_ValuesMatch(ExrCompression comp)
    {
        const int w = 24, h = 40;
        float F(int x, int y) => x * 0.05f - y * 0.02f + 0.3f;
        var img = new ExrImage { Width = w, Height = h, Compression = comp };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, ExrUncompressedTests.FloatChannelBytes(w, h, F));

        using var m = new MagickImage(ExrFile.Write(img));
        m.Width.ShouldBe((uint)w); m.Height.ShouldBe((uint)h);
        using var px = m.GetPixels();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Close(px.GetPixel(x, y).ToArray()![0], F(x, y) * Q, $"{comp} Y({x},{y})");
    }

    [Theory]
    [InlineData(CompressionMethod.Zip, ExrCompression.Zip)]
    [InlineData(CompressionMethod.RLE, ExrCompression.Rle)]
    public void MagickEncode_Compressed_ReadByUs_ValuesMatch(CompressionMethod method, ExrCompression expected)
    {
        const int w = 30, h = 40;
        using var m = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        m.ColorSpace = ColorSpace.RGB;
        using (var px = m.GetPixels())
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    px.SetPixel(x, y, [(x * 0.02f) * Q, (y * 0.015f) * Q, 0.25f * Q]);
        m.Format = MagickFormat.Exr;
        m.Settings.Compression = method;
        var bytes = m.ToByteArray();

        var img = ExrFile.Read(bytes);
        _out.WriteLine($"Magick wrote compression {img.Compression} (asked {method})");
        img.Compression.ShouldBe(expected, $"Magick should write {expected} for {method}");
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
