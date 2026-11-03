using System.Text;
using ImageMagick;
using SharpAstro.Pnm;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The Netpbm writer, judged by two independent readers: Magick.NET (ImageMagick) reads our bytes and
/// its pixels must equal the source exactly, and our own <see cref="PnmReader"/> must hand the very
/// samples back. Only maxval 255 and 65535 are compared against Magick, because those are the two
/// where ImageMagick's own scaling is the identity.
/// </summary>
public sealed class PnmWriterTests
{
    private const int W = 13, H = 9; // not a multiple of anything, so no row happens to line up

    private static string MagickMapping(PnmTupleType t) => t switch
    {
        PnmTupleType.Grayscale => "I",
        PnmTupleType.GrayscaleAlpha => "IA",
        PnmTupleType.Rgb => "RGB",
        _ => "RGBA",
    };

    private static int ChannelsOf(PnmTupleType t) => t switch
    {
        PnmTupleType.Grayscale => 1,
        PnmTupleType.GrayscaleAlpha => 2,
        PnmTupleType.Rgb => 3,
        _ => 4,
    };

    private static ushort[] RandomSamples(int count, int maxValue, int seed = 7)
    {
        var rng = new Random(seed);
        var samples = new ushort[count];
        for (var i = 0; i < count; i++) samples[i] = (ushort)rng.Next(maxValue + 1);
        // The extremes must be present, or a sign / high-byte slip hides in the middle of the range.
        samples[0] = 0;
        samples[1] = (ushort)maxValue;
        return samples;
    }

    public static TheoryData<PnmTupleType, int, bool, string> Variants()
    {
        var data = new TheoryData<PnmTupleType, int, bool, string>();
        foreach (var maxValue in new[] { 255, 65535 })
        {
            data.Add(PnmTupleType.Grayscale, maxValue, false, "P5");
            data.Add(PnmTupleType.Rgb, maxValue, false, "P6");
            data.Add(PnmTupleType.Grayscale, maxValue, true, "P7");
            data.Add(PnmTupleType.GrayscaleAlpha, maxValue, false, "P7");
            data.Add(PnmTupleType.Rgb, maxValue, true, "P7");
            data.Add(PnmTupleType.RgbAlpha, maxValue, false, "P7");
        }
        return data;
    }

    // ---- Magick reads our bytes; our reader round-trips them ---------------------------------

    [Theory]
    [MemberData(nameof(Variants))]
    public void MagickReadsWhatWeWrite_ToTheSourcePixels(PnmTupleType tuple, int maxValue, bool pam, string magic)
    {
        var channels = ChannelsOf(tuple);
        var samples = RandomSamples(W * H * channels, maxValue);

        var file = PnmWriter.Encode(samples, W, H, tuple, maxValue, pam);
        Encoding.ASCII.GetString(file, 0, 2).ShouldBe(magic, "the variant the case claims is the one written");

        using var magick = new MagickImage(file);
        ((int)magick.Width).ShouldBe(W);
        ((int)magick.Height).ShouldBe(H);

        var mapping = MagickMapping(tuple);
        var pixels = magick.GetPixels();
        if (maxValue == 255)
        {
            var theirs = pixels.ToByteArray(mapping)!;
            theirs.Length.ShouldBe(samples.Length);
            for (var i = 0; i < theirs.Length; i++)
                theirs[i].ShouldBe((byte)samples[i], $"sample {i}");
        }
        else
        {
            var theirs = pixels.ToShortArray(mapping)!;
            theirs.Length.ShouldBe(samples.Length);
            for (var i = 0; i < theirs.Length; i++)
                theirs[i].ShouldBe(samples[i], $"sample {i}");
        }
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void OurReaderRoundTripsWhatWeWrite_Exactly(PnmTupleType tuple, int maxValue, bool pam, string magic)
    {
        var samples = RandomSamples(W * H * ChannelsOf(tuple), maxValue);

        var image = PnmReader.Read(PnmWriter.Encode(samples, W, H, tuple, maxValue, pam));

        image.Header.Version.ShouldBe(int.Parse(magic[1..]));
        image.Width.ShouldBe(W);
        image.Height.ShouldBe(H);
        image.MaxValue.ShouldBe(maxValue);
        image.TupleType.ShouldBe(tuple);
        image.Samples.ShouldBe(samples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(254)]
    [InlineData(256)]
    [InlineData(1023)]
    [InlineData(4095)]
    [InlineData(32768)]
    public void AnyMaxval_RoundTripsThroughOurReader_AtItsNativeValues(int maxValue)
    {
        var samples = RandomSamples(W * H * 3, maxValue);

        var image = PnmReader.Read(PnmWriter.Encode(samples, W, H, PnmTupleType.Rgb, maxValue));

        image.MaxValue.ShouldBe(maxValue);
        image.Samples.ShouldBe(samples);
    }

    [Fact]
    public void TheByteOverload_WritesTheSameFileAsTheUshortOne()
    {
        foreach (var tuple in new[] { PnmTupleType.Grayscale, PnmTupleType.GrayscaleAlpha, PnmTupleType.Rgb, PnmTupleType.RgbAlpha })
        {
            var wide = RandomSamples(W * H * ChannelsOf(tuple), 255);
            var narrow = wide.Select(s => (byte)s).ToArray();

            PnmWriter.Encode(narrow, W, H, tuple).ShouldBe(PnmWriter.Encode(wide, W, H, tuple, 255));
            var low = narrow.Select(s => (byte)(s % 101)).ToArray();
            PnmWriter.Encode(low, W, H, tuple, 100, pam: true)
                .ShouldBe(PnmWriter.Encode(low.Select(s => (ushort)s).ToArray(), W, H, tuple, 100, pam: true));
        }
    }

    [Theory]
    [InlineData(PnmTupleType.Grayscale, 255, false)]
    [InlineData(PnmTupleType.RgbAlpha, 255, false)]
    [InlineData(PnmTupleType.Rgb, 65535, false)]
    [InlineData(PnmTupleType.GrayscaleAlpha, 65535, true)]
    public void Write_ToAStream_IsTheSameAsEncode_AcrossChunkBoundaries(PnmTupleType tuple, int maxValue, bool pam)
    {
        // Wide enough that the raster is several 64 KiB chunks.
        const int w = 300, h = 120;
        var samples = RandomSamples(w * h * ChannelsOf(tuple), maxValue);

        using var stream = new MemoryStream();
        PnmWriter.Write(stream, samples, w, h, tuple, maxValue, pam);
        stream.ToArray().ShouldBe(PnmWriter.Encode(samples, w, h, tuple, maxValue, pam));

        if (maxValue == 255)
        {
            var bytes = samples.Select(s => (byte)s).ToArray();
            using var narrow = new MemoryStream();
            PnmWriter.Write(narrow, bytes, w, h, tuple, 255, pam);
            narrow.ToArray().ShouldBe(PnmWriter.Encode(bytes, w, h, tuple, 255, pam));
        }
    }

    [Fact]
    public void ASixteenBitSample_IsWrittenBigEndian()
    {
        var file = PnmWriter.Encode(new ushort[] { 0x0102, 0xFFFE }, 2, 1, PnmTupleType.Grayscale, 65535);

        file[^4..].ShouldBe(new byte[] { 0x01, 0x02, 0xFF, 0xFE });
    }

    // ---- headers, literally ------------------------------------------------------------------

    [Fact]
    public void P5_Header_IsExactlyNetpbms()
    {
        var file = PnmWriter.Encode(new byte[] { 1, 2, 3, 4, 5, 6 }, 3, 2, PnmTupleType.Grayscale);

        Encoding.ASCII.GetString(file, 0, file.Length - 6).ShouldBe("P5\n3 2\n255\n");
        file[^6..].ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6 });
    }

    [Fact]
    public void P6_Header_IsExactlyNetpbms()
    {
        var file = PnmWriter.Encode(new ushort[] { 1, 2, 3 }, 1, 1, PnmTupleType.Rgb, 1023);

        Encoding.ASCII.GetString(file, 0, file.Length - 6).ShouldBe("P6\n1 1\n1023\n");
        file[^6..].ShouldBe(new byte[] { 0, 1, 0, 2, 0, 3 });
    }

    [Theory]
    [InlineData(PnmTupleType.Grayscale, 1, "GRAYSCALE")]
    [InlineData(PnmTupleType.GrayscaleAlpha, 2, "GRAYSCALE_ALPHA")]
    [InlineData(PnmTupleType.Rgb, 3, "RGB")]
    [InlineData(PnmTupleType.RgbAlpha, 4, "RGB_ALPHA")]
    public void P7_Header_IsExactlyTheDocumentedFields(PnmTupleType tuple, int depth, string name)
    {
        var raster = new byte[2 * 1 * depth];
        var file = PnmWriter.Encode(raster, 2, 1, tuple, 255, pam: true);

        Encoding.ASCII.GetString(file, 0, file.Length - raster.Length)
            .ShouldBe($"P7\nWIDTH 2\nHEIGHT 1\nDEPTH {depth}\nMAXVAL 255\nTUPLTYPE {name}\nENDHDR\n");
    }

    [Fact]
    public void AlphaImages_AreWrittenAsPam_EvenWithoutAskingForIt()
    {
        PnmWriter.Encode(new byte[4], 2, 1, PnmTupleType.GrayscaleAlpha)[..2].ShouldBe("P7"u8.ToArray());
        PnmWriter.Encode(new byte[8], 2, 1, PnmTupleType.RgbAlpha)[..2].ShouldBe("P7"u8.ToArray());
    }

    // ---- refusals ---------------------------------------------------------------------------

    [Fact]
    public void AShortBuffer_Throws()
    {
        Should.Throw<ArgumentException>(() => PnmWriter.Encode(new ushort[3 * 2 * 3 - 1], 3, 2, PnmTupleType.Rgb, 255));
        Should.Throw<ArgumentException>(() => PnmWriter.Encode(new byte[3 * 2 - 1], 3, 2, PnmTupleType.Grayscale));
        Should.Throw<ArgumentException>(() => PnmWriter.Write(new MemoryStream(), new byte[1], 3, 2, PnmTupleType.Grayscale));
    }

    [Fact]
    public void ALongerBuffer_WritesOnlyTheImage()
    {
        var file = PnmWriter.Encode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 2, PnmTupleType.Grayscale);

        file[^4..].ShouldBe(new byte[] { 1, 2, 3, 4 });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void AMaxvalOutOfRange_Throws(int maxValue)
    {
        Should.Throw<ArgumentException>(() => PnmWriter.Encode(new ushort[3], 1, 1, PnmTupleType.Rgb, maxValue));
        Should.Throw<ArgumentException>(() => PnmWriter.Write(new MemoryStream(), new ushort[3], 1, 1, PnmTupleType.Rgb, maxValue));
    }

    [Fact]
    public void ByteSamples_RefuseAMaxvalAbove255()
    {
        Should.Throw<ArgumentException>(() => PnmWriter.Encode(new byte[3], 1, 1, PnmTupleType.Rgb, 256));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-3, 2)]
    public void ADimensionThatIsNotPositive_Throws(int width, int height)
    {
        Should.Throw<ArgumentException>(() => PnmWriter.Encode(new byte[16], width, height, PnmTupleType.Grayscale));
    }

    [Theory]
    [InlineData(PnmTupleType.BlackAndWhite)]
    [InlineData(PnmTupleType.BlackAndWhiteAlpha)]
    [InlineData((PnmTupleType)99)]
    public void ATupleTypeItCannotWrite_Throws(PnmTupleType tuple)
    {
        Should.Throw<ArgumentException>(() => PnmWriter.Encode(new byte[16], 1, 1, tuple));
    }

    [Fact]
    public void ASampleAboveMaxval_Throws_AndNamesIt()
    {
        var ex = Should.Throw<ArgumentException>(() => PnmWriter.Encode(new ushort[] { 0, 16 }, 2, 1, PnmTupleType.Grayscale, 15));
        ex.Message.ShouldContain("sample 1");

        Should.Throw<ArgumentException>(() => PnmWriter.Encode(new byte[] { 0, 101 }, 2, 1, PnmTupleType.Grayscale, 100));
    }

    [Fact]
    public void ANullStream_Throws()
    {
        Should.Throw<ArgumentNullException>(() => PnmWriter.Write(null!, new byte[1], 1, 1, PnmTupleType.Grayscale));
    }
}
