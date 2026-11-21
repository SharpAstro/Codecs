using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// One photograph in several formats (aaron-rust/hdr-sample-images, CC0): two 16-bit PNGs from Lightroom are
/// the masters, 1A BT.2020 PQ and 1B BT.709 sRGB, and the other files were encoded from them with the stock
/// tools. So each decode must report the colour its PNG reports and carry the PNG's samples: exactly where the
/// encoder is lossless, closely where it is not. <c>fetch-hdr-samples.sh</c> puts the files in
/// <c>Fixtures/hdr-samples/</c> (CI restores them from its cache); every test skips when a file is absent.
/// </summary>
/// <remarks>
/// The bounds are what the decoders measured on these files when this was written (HEIC HDR 4.39 / 0.13,
/// HEIC SDR 5.95 / 0.99, JPEG 5.13 / 0.89 codes), with a little room for rounding, never loosened to make a
/// change pass. The channel-mean bound is what pins the Y'CbCr matrix of a heif-enc file with no nclx:
/// converted with BT.601 the HDR HEIC's means sit 0.12 codes from the master's, with BT.709 2.30 and with
/// BT.2020 2.74 (SDR: 0.99, 4.34, 4.90), measured once by converting the decoded planes each way.
/// </remarks>
public class HdrSampleFormatsTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hdr-samples");

    private static IDecodedImage Load(string name)
    {
        var path = Path.Combine(Root, name);
        Assert.SkipUnless(File.Exists(path), $"{name} is not present; run tests/SharpAstro.Codecs.Tests/fetch-hdr-samples.sh");
        ImageCodecs.TryDecode(File.ReadAllBytes(path), out var image).ShouldBeTrue($"{name} did not decode");
        return image;
    }

    /// <summary>
    /// The heif-enc HEICs: the HDR one says BT.2020 PQ only in its ICC profile's cicp tag (no nclx, no VUI
    /// colour), the SDR one has an sRGB profile and no tag. Both lossy at heif-enc's default quality.
    /// </summary>
    [Theory]
    [InlineData("3A_HEIC_HDR.heic", "1A_PNG_HDR.png", 4.5, 0.5)]
    [InlineData("3B_HEIC_SDR.heic", "1B_PNG_SDR.png", 6.1, 1.2)]
    public void Heic_MatchesItsPngMaster(string file, string master, double maxMeanError, double maxChannelMeanShift) =>
        Compare(Load(file), Load(master), maxMeanError, maxChannelMeanShift);

    /// <summary>The ImageMagick JPEG of the SDR master: 8-bit, no ICC profile, sRGB by assumption.</summary>
    [Fact]
    public void Jpeg_MatchesItsPngMaster() =>
        Compare(Load("5B_JPEG_SDR.jpg"), Load("1B_PNG_SDR.png"), 5.3, 1.0);

    /// <summary>
    /// The ImageMagick TIFFs keep the PNGs' samples and profile, so they match exactly; the HDR one names
    /// BT.2020 PQ only through its profile's cicp tag. Fetched with <c>--all</c> only (545 MB).
    /// </summary>
    [Theory]
    [InlineData("6A_TIFF_HDR.tiff", "1A_PNG_HDR.png")]
    [InlineData("6B_TIFF_SDR.tiff", "1B_PNG_SDR.png")]
    public void Tiff_IsItsPngMaster(string file, string master) => Compare(Load(file), Load(master), 0, 0);

    [Fact]
    public void PngMasters_SayWhatTheyAre()
    {
        Load("1A_PNG_HDR.png").ColorEncoding.ShouldBe(new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Pq });
        Load("1B_PNG_SDR.png").ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
    }

    /// <summary>
    /// Same size and colour, then the mean absolute error over every R, G and B sample and the largest shift
    /// of one channel's mean, both in units of a 10-bit code (1/1023 of full scale).
    /// </summary>
    private static void Compare(IDecodedImage image, IDecodedImage master, double maxMeanError, double maxChannelMeanShift)
    {
        (image.Width, image.Height).ShouldBe((master.Width, master.Height));
        image.ColorEncoding.ShouldBe(master.ColorEncoding);

        var pixels = image.Width * image.Height;
        double error = 0;
        Span<double> sumImage = stackalloc double[3], sumMaster = stackalloc double[3];
        for (var p = 0; p < pixels; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                double a = Sample(image, p, c), b = Sample(master, p, c);
                error += Math.Abs(a - b);
                sumImage[c] += a;
                sumMaster[c] += b;
            }
        }
        var meanError = error / (3.0 * pixels) * 1023;
        double shift = 0;
        for (var c = 0; c < 3; c++) shift = Math.Max(shift, Math.Abs(sumImage[c] - sumMaster[c]) / pixels * 1023);
        TestContext.Current.TestOutputHelper?.WriteLine($"mean |error| {meanError:0.000} codes, largest channel mean shift {shift:0.000} codes");
        meanError.ShouldBeLessThanOrEqualTo(maxMeanError);
        shift.ShouldBeLessThanOrEqualTo(maxChannelMeanShift);
    }

    /// <summary>One sample scaled to 0..1, whatever the raster's sample type.</summary>
    private static double Sample(IDecodedImage image, int pixel, int channel)
    {
        var index = pixel * image.Channels + (image.Channels >= 3 ? channel : 0);
        return image.SampleFormat switch
        {
            SampleFormat.UInt8 => image.Pixels[index] / 255.0,
            SampleFormat.UInt16 => MemoryMarshal.Cast<byte, ushort>(image.Pixels)[index] / 65535.0,
            _ => MemoryMarshal.Cast<byte, float>(image.Pixels)[index],
        };
    }
}
