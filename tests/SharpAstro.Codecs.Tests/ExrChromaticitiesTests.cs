using ImageMagick;
using SharpAstro.Exr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// EXR <c>chromaticities</c> attribute — optional colour-primary signalling. Verifies
/// the attribute round-trips through our writer/reader, is omitted by default (so files
/// stay minimal and readers assume Rec.709), and that a tagged file still opens in
/// OpenEXR (Magick.NET).
/// </summary>
public sealed class ExrChromaticitiesTests
{
    private readonly ITestOutputHelper _out;
    public ExrChromaticitiesTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Rec709_RoundTripsThroughHeader()
    {
        const int w = 16, h = 12;
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Zip, Chromaticities = ExrChromaticities.Rec709 };
        img.AddChannel(new ExrChannel { Name = "R", Type = ExrPixelType.Float }, ExrUncompressedTests.FloatChannelBytes(w, h, (x, y) => x * 0.1f));
        img.AddChannel(new ExrChannel { Name = "G", Type = ExrPixelType.Float }, ExrUncompressedTests.FloatChannelBytes(w, h, (x, y) => y * 0.1f));
        img.AddChannel(new ExrChannel { Name = "B", Type = ExrPixelType.Float }, ExrUncompressedTests.FloatChannelBytes(w, h, (x, y) => 0.5f));

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.Chromaticities.ShouldNotBeNull();
        rt.Chromaticities!.Value.ShouldBe(ExrChromaticities.Rec709);
        // pixels unaffected
        rt.GetData("R").ShouldBe(img.GetData("R"));
    }

    [Fact]
    public void CustomPrimaries_RoundTrip()
    {
        const int w = 8, h = 8;
        var custom = new ExrChromaticities(0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f, 0.3127f, 0.3290f); // Rec.2020-ish
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Piz, Chromaticities = custom };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Half }, ExrUncompressedTests.HalfChannelBytes(w, h, 1));

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.Chromaticities.ShouldBe(custom);
    }

    [Fact]
    public void Default_OmitsAttribute()
    {
        const int w = 8, h = 8;
        var img = new ExrImage { Width = w, Height = h, Compression = ExrCompression.Zip };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, ExrUncompressedTests.FloatChannelBytes(w, h, (x, y) => 1f));

        var rt = ExrFile.Read(ExrFile.Write(img));
        rt.Chromaticities.ShouldBeNull();
    }

    [Fact]
    public void Facade_RgbFloat_WithRec709_RoundTripsAndOpensInMagick()
    {
        const int w = 20, h = 16;
        var rgb = new float[w * h * 3];
        for (var i = 0; i < w * h; i++) { rgb[i * 3] = i * 0.01f; rgb[i * 3 + 1] = i * 0.02f; rgb[i * 3 + 2] = 0.3f; }

        var bytes = ExrImageCodec.EncodeRgbFloat(rgb, w, h, ExrCompression.Zip, ExrChromaticities.Rec709);

        // Our reader sees the tag.
        ExrFile.Read(bytes).Chromaticities.ShouldBe(ExrChromaticities.Rec709);

        // OpenEXR (Magick.NET) still opens the tagged file at the right size + non-zero pixels.
        using var m = new MagickImage(bytes);
        m.Width.ShouldBe((uint)w); m.Height.ShouldBe((uint)h);
        m.Format.ShouldBe(MagickFormat.Exr);
        using var px = m.GetPixels();
        px.GetPixel(w - 1, h - 1).ToArray()![0].ShouldBeGreaterThan(0f);
    }
}
