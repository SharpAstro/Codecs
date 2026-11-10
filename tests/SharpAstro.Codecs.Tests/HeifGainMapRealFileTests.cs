using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Real phone HEICs that carry a gain map, through the facade: the iPhone 13 Pro files (Apple's auxiliary map) and
/// Samsung's ISO/TS 21496-1 <c>tmap</c> file. <c>fetch-heif-external.sh</c> fetches them into
/// <c>Fixtures/heif-external/</c> (they are not committed); every test skips, visibly, when its file is absent.
/// <para>
/// What is checked is what no synthetic file can say: that the gain map is FOUND in a file a phone wrote, that the
/// base decodes as libheif (Magick.NET) decodes it, that the reconstruction is a sane HDR image (finite, above SDR
/// white somewhere, and exactly the linearised base at SDR headroom), and that the 8-bit tier is the base. The
/// reconstruction's arithmetic is checked against the formula in <see cref="HeifGainMapTests"/>.
/// </para>
/// </summary>
public class HeifGainMapRealFileTests(ITestOutputHelper output)
{
    public static TheoryData<string, HeifGainMapDialect> Files => new()
    {
        { "apple-iphone13pro-hdr-sample.heic", HeifGainMapDialect.AppleAux },
        { "apple-iphone13pro-portrait-pug.heic", HeifGainMapDialect.AppleAux },
        { "samsung-tmap-aux-ycbcr.heic", HeifGainMapDialect.IsoTmap },
    };

    private static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    [Theory]
    [MemberData(nameof(Files))]
    public void TheGainMap_IsFound_InTheDialectTheFileUses(string name, HeifGainMapDialect dialect)
    {
        var file = HeifCanonFileTests.Load(name);

        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        info.SampleFormat.ShouldBe(SampleFormat.Float32, "a file with a gain map presents as the HDR float tier");
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();

        var decoded = image.ShouldBeOfType<HeifGainMapDecodedImage>();
        var found = decoded.Source.Info;
        found.Dialect.ShouldBe(dialect);
        found.Layout.ShouldBe(HeifGainMapLayout.BaseIsPrimary);
        decoded.Source.AuthoredHeadroom.ShouldBeGreaterThan(1);
        (decoded.Width, decoded.Height).ShouldBe((info.Width, info.Height));
        output.WriteLine($"{name}: {decoded.Width}x{decoded.Height}, {dialect}, authored headroom {decoded.Source.AuthoredHeadroom:0.000} ({Math.Log2(decoded.Source.AuthoredHeadroom):0.00} stops)");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheBase_MatchesLibheif_InSizeAndPixels(string name, HeifGainMapDialect dialect)
    {
        _ = dialect;
        var file = HeifCanonFileTests.Load(name);
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        var baseRgb = image.ShouldBeOfType<HeifGainMapDecodedImage>().Source.DecodeBase();

        // The base is the primary here (and has no rotation), so libheif's decode of the file is its decode of the base;
        // the 8-bit 4:2:0 tolerance is the one HeifPhoneFileTests measured and holds (one code, on a small share).
        var (differing, max, first, total) = HeifDecodeTests.MeasureAgainstLibheif(name, file, baseRgb, sameSize: true);
        output.WriteLine($"{name}: {differing} of {total} samples differ from libheif ({(double)differing / total:P3}), max {max}, first {first}");
        max.ShouldBeLessThanOrEqualTo(1);
        ((double)differing / total).ShouldBeLessThan(0.10);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheReconstruction_IsFinite_AndBoostsAboveSdrWhite(string name, HeifGainMapDialect dialect)
    {
        _ = dialect;
        var file = HeifCanonFileTests.Load(name);
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();

        var floats = image!.ToFloats();

        floats.Length.ShouldBe(image.Width * image.Height * 4);
        var peak = 0f;
        for (var i = 0; i < floats.Length; i++)
        {
            float.IsFinite(floats[i]).ShouldBeTrue($"sample {i}");
            if (i % 4 == 3)
            {
                floats[i].ShouldBe(1f);
                continue;
            }
            floats[i].ShouldBeGreaterThanOrEqualTo(0f, $"sample {i}");
            peak = Math.Max(peak, floats[i]);
        }
        output.WriteLine($"{name}: peak {peak:0.000} (1.0 = SDR white)");
        peak.ShouldBeGreaterThan(1f, "the brightest reconstructed pixel exceeds SDR white");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void AtSdrHeadroom_TheReconstruction_IsTheLinearisedBase(string name, HeifGainMapDialect dialect)
    {
        _ = dialect;
        var file = HeifCanonFileTests.Load(name);
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        var source = image.ShouldBeOfType<HeifGainMapDecodedImage>().Source;

        var sdr = source.Reconstruct(1);

        sdr.Weight.ShouldBe(0);
        var baseRgb = source.DecodeBase();
        var transfer = baseRgb.ColorEncoding.Transfer;
        output.WriteLine($"{name}: the base's transfer is {transfer}");
        // A gain map is authored against an SDR base, so a base that states no transfer is sRGB (as the reader treats it).
        float[]? linear = transfer == TransferFunction.Unspecified ? null : baseRgb.ToLinearRgbaFloat(out _);
        var max = (1 << baseRgb.BitDepth) - 1;
        for (var p = 0; p < baseRgb.Width * baseRgb.Height; p += 13)
        {
            for (var c = 0; c < 3; c++)
            {
                var expected = linear is null ? (float)SrgbToLinear((double)baseRgb.Samples[3 * p + c] / max) : linear[4 * p + c];
                sdr.Rgba[4 * p + c].ShouldBe(expected, 1e-6f, $"pixel {p}, channel {c}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheEightBitTier_StaysTheBase(string name, HeifGainMapDialect dialect)
    {
        _ = dialect;
        var file = HeifCanonFileTests.Load(name);
        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        var rgba8 = new byte[info.Width * info.Height * 4];

        ImageCodecs.TryDecodeIntoRgba8(file, rgba8).ShouldBeTrue();

        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        image!.ToRgba8().ShouldBe(rgba8);
        var baseRgb = image.ShouldBeOfType<HeifGainMapDecodedImage>().Source.DecodeBase();
        var max = (1 << baseRgb.BitDepth) - 1;
        for (var p = 0; p < baseRgb.Width * baseRgb.Height; p += 17)
        {
            rgba8[4 * p + 1].ShouldBe((byte)((baseRgb.Samples[3 * p + 1] * 255 + max / 2) / max));
            rgba8[4 * p + 3].ShouldBe((byte)255);
        }
    }
}
