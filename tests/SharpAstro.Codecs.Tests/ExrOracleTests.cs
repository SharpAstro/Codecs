using System.Buffers.Binary;
using ImageMagick;
using SharpAstro.Exr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// EXR oracle — cross-checks against Magick.NET (ImageMagick Q16-HDRI, which links
/// OpenEXR). ImageMagick maps EXR sample values into its quantum domain by exactly
/// ×<see cref="Quantum.Max"/> (65535), measured in <see cref="ExrUncompressedTests"/>,
/// so the comparisons here scale by that factor. This is value-exact interop
/// validation (within float/half precision), the EXR analogue of the JXR oracle.
/// </summary>
public sealed class ExrOracleTests
{
    private readonly ITestOutputHelper _out;
    public ExrOracleTests(ITestOutputHelper output) => _out = output;

    private const float Q = 65535f; // ImageMagick QuantumRange (Q16-HDRI)

    [Fact]
    public void OurEncode_FloatMono_ReadByMagick_ValuesMatch()
    {
        const int w = 16, h = 10;
        float F(int x, int y) => x * 0.1f - y * 0.03f + 0.2f;
        var data = ExrUncompressedTests.FloatChannelBytes(w, h, F);
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.None };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, data);

        using var m = new MagickImage(ExrFile.Write(img));
        m.Width.ShouldBe((uint)w); m.Height.ShouldBe((uint)h);
        using var px = m.GetPixels();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float got = px.GetPixel(x, y).ToArray()![0];
                Close(got, F(x, y) * Q, $"Y({x},{y})");
            }
    }

    [Fact]
    public void OurEncode_HalfRgb_ReadByMagick_ValuesMatch()
    {
        const int w = 12, h = 8;
        var r = ExrUncompressedTests.HalfChannelBytes(w, h, 1);
        var g = ExrUncompressedTests.HalfChannelBytes(w, h, 2);
        var b = ExrUncompressedTests.HalfChannelBytes(w, h, 3);
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.None };
        img.AddChannel(new ExrChannel { Name = "R", Type = ExrPixelType.Half }, r);
        img.AddChannel(new ExrChannel { Name = "G", Type = ExrPixelType.Half }, g);
        img.AddChannel(new ExrChannel { Name = "B", Type = ExrPixelType.Half }, b);

        using var m = new MagickImage(ExrFile.Write(img));
        using var px = m.GetPixels();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var vals = px.GetPixel(x, y).ToArray()!; // [R, G, B(, A)]
                Close(vals[0], ReadHalf(r, w, x, y) * Q, $"R({x},{y})");
                Close(vals[1], ReadHalf(g, w, x, y) * Q, $"G({x},{y})");
                Close(vals[2], ReadHalf(b, w, x, y) * Q, $"B({x},{y})");
            }
    }

    [Fact]
    public void MagickEncode_Uncompressed_ReadByUs_ValuesMatch()
    {
        const int w = 14, h = 9;
        using var m = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        m.ColorSpace = ColorSpace.RGB;
        using (var px = m.GetPixels())
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    px.SetPixel(x, y, [(x * 0.05f + 0.1f) * Q, (y * 0.04f) * Q, 0.5f * Q]);
        m.Format = MagickFormat.Exr;
        m.Settings.Compression = CompressionMethod.NoCompression;
        var bytes = m.ToByteArray();

        // Confirm Magick actually wrote NONE (our decoder only handles NONE this rung).
        byte comp = ReadCompressionByte(bytes);
        _out.WriteLine($"Magick EXR compression byte = {comp} ({(ExrCompression)comp})");
        comp.ShouldBe((byte)ExrCompression.None, "force Magick to write uncompressed EXR for this rung");

        var img = ExrFile.Read(bytes);
        img.Width.ShouldBe(w); img.Height.ShouldBe(h);
        using var rt = m.GetPixels();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var expect = rt.GetPixel(x, y).ToArray()!;
                Close(ReadFloatOrHalf(img, "R", w, x, y) * Q, expect[0], $"R({x},{y})");
                Close(ReadFloatOrHalf(img, "G", w, x, y) * Q, expect[1], $"G({x},{y})");
                Close(ReadFloatOrHalf(img, "B", w, x, y) * Q, expect[2], $"B({x},{y})");
            }
    }

    // ----------------------------------------------------------------- helpers

    private void Close(float got, float expected, string where)
    {
        float tol = Math.Max(1.0f, Math.Abs(expected) * 1e-3f); // half precision + quantum scaling
        if (Math.Abs(got - expected) > tol)
            _out.WriteLine($"MISMATCH {where}: got {got}, expected {expected}");
        Math.Abs(got - expected).ShouldBeLessThanOrEqualTo(tol, where);
    }

    private static float ReadHalf(byte[] data, int w, int x, int y)
        => (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((y * w + x) * 2, 2)));

    private static float ReadFloatOrHalf(ExrImage img, string ch, int w, int x, int y)
    {
        int i = img.IndexOf(ch);
        var c = img.Channels[i];
        var data = img.GetData(i);
        return c.Type == ExrPixelType.Half
            ? ReadHalf(data, w, x, y)
            : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((y * w + x) * 4, 4));
    }

    // Walk our own header just far enough to read the compression attribute value.
    private static byte ReadCompressionByte(byte[] bytes)
    {
        var img = ExrFile.Read(bytes);
        return (byte)img.Compression;
    }
}
