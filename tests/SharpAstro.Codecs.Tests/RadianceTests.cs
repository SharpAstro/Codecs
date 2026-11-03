using System.Text;
using SharpAstro.Radiance;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>Hand-built Radiance byte streams: every scanline coding, the header features, the refusals, and the writer.</summary>
public sealed class RadianceTests
{
    /// <summary>Header text: magic, the given lines, a blank line, then the resolution line.</summary>
    private static byte[] Head(string resolution, params string[] lines)
    {
        var sb = new StringBuilder("#?RADIANCE\n");
        foreach (var l in lines) sb.Append(l).Append('\n');
        sb.Append('\n').Append(resolution).Append('\n');
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>Offset of the pixel data in a file our writer produced (magic, FORMAT, blank, resolution: four lines).</summary>
    private static int BodyStart(byte[] b)
    {
        int p = 0;
        for (int line = 0; line < 4; line++) p = Array.IndexOf(b, (byte)'\n', p) + 1;
        return p;
    }

    private static byte[] Px(params int[] bytes) => [.. bytes.Select(b => (byte)b)];

    private static byte[] File(int w, int h, byte[] body, params string[] lines) =>
        Cat(Head($"-Y {h} +X {w}", ["FORMAT=32-bit_rle_rgbe", .. lines]), body);

    /// <summary>The value a pixel must decode to: mantissa * 2^(e - 136), 0 when e is 0.</summary>
    private static float V(int m, int e) => e == 0 ? 0f : m * MathF.Pow(2f, e - 136);

    private static void AssertPixel(RadianceImage img, int x, int y, int r, int g, int b, int e)
    {
        int o = (y * img.Width + x) * 3;
        img.Pixels[o].ShouldBe(V(r, e), $"R({x},{y})");
        img.Pixels[o + 1].ShouldBe(V(g, e), $"G({x},{y})");
        img.Pixels[o + 2].ShouldBe(V(b, e), $"B({x},{y})");
    }

    // ------------------------------------------------------------ decoding every scanline coding

    [Fact]
    public void OldFlat_WideLine_Decodes()
    {
        // Width 10 is eligible for new-style RLE, but a first pixel that is not 2,2 says this line is flat.
        var body = new List<byte>();
        for (int x = 0; x < 10; x++) body.AddRange(Px(10 + x, 20 + x, 30 + x, 129));
        var img = RadianceFile.Read(File(10, 1, [.. body]));
        for (int x = 0; x < 10; x++) AssertPixel(img, x, 0, 10 + x, 20 + x, 30 + x, 129);
    }

    [Fact]
    public void Flat_NarrowLine_IsNeverReadAsNewStyle()
    {
        // Width 3 < 8: bytes 2,2,0,3 are an ordinary pixel here, not a new-style marker.
        var body = Cat(Px(2, 2, 0, 3), Px(100, 101, 102, 130), Px(7, 8, 9, 0));
        var img = RadianceFile.Read(File(3, 1, body));
        AssertPixel(img, 0, 0, 2, 2, 0, 3);
        AssertPixel(img, 1, 0, 100, 101, 102, 130);
        AssertPixel(img, 2, 0, 7, 8, 9, 0); // exponent 0: black whatever the mantissas
        img.Pixels[6].ShouldBe(0f);
    }

    [Fact]
    public void OldRle_RepeatsThePreviousPixel_WithTheShiftAccumulator()
    {
        // Width 301 = A + (1,1,1,44) -> 44 more, then a second consecutive repeat (1,1,1,1) -> 1 << 8 = 256 more.
        var body = Cat(Px(50, 60, 70, 128), Px(1, 1, 1, 44), Px(1, 1, 1, 1));
        var img = RadianceFile.Read(File(301, 1, body));
        for (int x = 0; x < 301; x++) AssertPixel(img, x, 0, 50, 60, 70, 128);
    }

    [Fact]
    public void OldRle_ShiftResetsAfterAPlainPixel()
    {
        // A, repeat x2 (count 2), B, repeat x3: the second repeat after B counts 3 again, not 3 << 8.
        var body = Cat(Px(5, 6, 7, 130), Px(1, 1, 1, 2), Px(9, 8, 7, 131), Px(1, 1, 1, 3), Px(11, 12, 13, 132));
        var img = RadianceFile.Read(File(8, 1, body));
        for (int x = 0; x < 3; x++) AssertPixel(img, x, 0, 5, 6, 7, 130);
        for (int x = 3; x < 7; x++) AssertPixel(img, x, 0, 9, 8, 7, 131);
        AssertPixel(img, 7, 0, 11, 12, 13, 132);
    }

    [Fact]
    public void OldRle_InANarrowLine_Decodes()
    {
        var body = Cat(Px(5, 6, 7, 130), Px(1, 1, 1, 3));
        var img = RadianceFile.Read(File(4, 1, body));
        for (int x = 0; x < 4; x++) AssertPixel(img, x, 0, 5, 6, 7, 130);
    }

    [Fact]
    public void NewRle_RunsAndLiterals_Decode()
    {
        // Width 10. R: run of 10; G: literal 4 + run of 6; B: run of 3 + literal 7; E: run of 10.
        var body = Cat(
            Px(2, 2, 0, 10),
            Px(128 + 10, 40),
            Px(4, 1, 2, 3, 4, 128 + 6, 9),
            Px(128 + 3, 8, 7, 1, 2, 3, 4, 5, 6, 7),
            Px(128 + 10, 129));
        var img = RadianceFile.Read(File(10, 1, body));
        int[] g = [1, 2, 3, 4, 9, 9, 9, 9, 9, 9];
        int[] b = [8, 8, 8, 1, 2, 3, 4, 5, 6, 7];
        for (int x = 0; x < 10; x++) AssertPixel(img, x, 0, 40, g[x], b[x], 129);
    }

    [Fact]
    public void NewRle_LiteralOf128AndRunOf127_AreInBounds()
    {
        const int w = 255;
        var ch = new List<byte> { 128 }; // literal of 128 (code 128 is a literal, 129 is the shortest run)
        for (int i = 0; i < 128; i++) ch.Add((byte)(i + 1));
        ch.AddRange(Px(128 + 127, 200)); // run of 127
        byte[] Run(int v) => Px(128 + 127, v, 128 + 127, v, 128 + 1, v);
        var body = Cat(Px(2, 2, 0, w & 0xFF), ch.ToArray(), Run(1), Run(2), Run(130));
        var img = RadianceFile.Read(File(w, 1, body));
        for (int x = 0; x < 128; x++) AssertPixel(img, x, 0, x + 1, 1, 2, 130);
        for (int x = 128; x < w; x++) AssertPixel(img, x, 0, 200, 1, 2, 130);
    }

    [Fact]
    public void ScanlinesOfOneImage_MayMixCodings()
    {
        var newStyle = Cat(Px(2, 2, 0, 8), Px(128 + 8, 1), Px(128 + 8, 2), Px(128 + 8, 3), Px(128 + 8, 130));
        var flat = Cat(Enumerable.Range(0, 8).Select(i => Px(10 + i, 0, 0, 129)).ToArray());
        var img = RadianceFile.Read(File(8, 2, Cat(newStyle, flat)));
        AssertPixel(img, 7, 0, 1, 2, 3, 130);
        AssertPixel(img, 7, 1, 17, 0, 0, 129);
    }

    // ------------------------------------------------------------ header

    [Fact]
    public void Xyze_IsReportedAsXyze()
    {
        var body = Px(1, 2, 3, 128);
        var img = RadianceFile.Read(Cat(Head("-Y 1 +X 1", "FORMAT=32-bit_rle_xyze"), body));
        img.Format.ShouldBe(RadianceFormat.Xyze);
        AssertPixel(img, 0, 0, 1, 2, 3, 128);
    }

    [Fact]
    public void Exposure_IsMultipliedAcrossLines_ReportedAndNotApplied()
    {
        var img = RadianceFile.Read(File(1, 1, Px(64, 64, 64, 129), "EXPOSURE=2", "# a comment", "EXPOSURE=1.25"));
        img.Exposure.ShouldBe(2.5);
        AssertPixel(img, 0, 0, 64, 64, 64, 129); // stored values, untouched
        img.HeaderLines.ShouldBe(["FORMAT=32-bit_rle_rgbe", "EXPOSURE=2", "# a comment", "EXPOSURE=1.25"]);
    }

    [Fact]
    public void NoExposureLine_ReportsNull_AndMissingFormatMeansRgbe()
    {
        var img = RadianceFile.Read(Cat(Head("-Y 1 +X 1"), Px(4, 4, 4, 128)));
        img.Exposure.ShouldBeNull();
        img.Format.ShouldBe(RadianceFormat.Rgbe);
    }

    [Fact]
    public void RgbeMagic_AndCrLfHeaders_AreAccepted()
    {
        var head = Encoding.Latin1.GetBytes("#?RGBE\r\nFORMAT=32-bit_rle_rgbe\r\n\r\n-Y 1 +X 1\n");
        RadianceFile.Read(Cat(head, Px(4, 4, 4, 128))).Width.ShouldBe(1);
        RadianceFile.HasMagic(head).ShouldBeTrue();
        RadianceFile.HasMagic("#?RADIANCE\n"u8).ShouldBeTrue();
        RadianceFile.HasMagic("#?OTHER\n"u8).ShouldBeFalse();
        RadianceFile.HasMagic("#?RADIANCEX\n"u8).ShouldBeFalse();
    }

    // ------------------------------------------------------------ refusals

    private static void Refuse(byte[] data, string mentions, long maximumPixels = RadianceFile.DefaultMaximumPixels)
    {
        var ex = Should.Throw<InvalidDataException>(() => RadianceFile.Read(data, maximumPixels));
        ex.Message.ShouldContain(mentions, Case.Insensitive);
    }

    [Fact]
    public void NewRle_RunOverflowingTheWidth_IsRefused()
    {
        // Width 8, R channel: run of 9.
        Refuse(File(8, 1, Cat(Px(2, 2, 0, 8), Px(128 + 9, 5), Px(128 + 8, 1), Px(128 + 8, 1), Px(128 + 8, 1))), "overflow");
    }

    [Fact]
    public void NewRle_RunOverflowingAfterAPartialFill_IsRefused()
    {
        Refuse(File(8, 1, Cat(Px(2, 2, 0, 8), Px(128 + 5, 5), Px(128 + 4, 5))), "overflow");
    }

    [Fact]
    public void NewRle_LiteralOverflowingTheWidth_IsRefused()
    {
        Refuse(File(8, 1, Cat(Px(2, 2, 0, 8), Px(9, 1, 2, 3, 4, 5, 6, 7, 8, 9))), "overflow");
    }

    [Fact]
    public void NewRle_ZeroLengthLiteral_IsRefused()
    {
        Refuse(File(8, 1, Cat(Px(2, 2, 0, 8), Px(0))), "literal");
    }

    [Fact]
    public void NewRle_EmbeddedWidthDifferingFromTheHeader_IsRefused()
    {
        Refuse(File(8, 1, Cat(Px(2, 2, 0, 9), Px(128 + 8, 1), Px(128 + 8, 1), Px(128 + 8, 1), Px(128 + 8, 1))), "width");
    }

    [Fact]
    public void OldRle_RunOverflowingTheWidth_IsRefused()
    {
        Refuse(File(4, 1, Cat(Px(5, 6, 7, 130), Px(1, 1, 1, 4))), "overflow");
    }

    [Fact]
    public void OldRle_ShiftedRunOverflowingTheWidth_IsRefused()
    {
        // Second consecutive repeat counts 1 << 8 = 256, more than the 20 pixels left.
        Refuse(File(30, 1, Cat(Px(5, 6, 7, 130), Px(1, 1, 1, 5), Px(1, 1, 1, 1))), "overflow");
    }

    [Fact]
    public void OldRle_ManyConsecutiveRepeats_DoNotWrapTheCountToSomethingSmall()
    {
        // Five consecutive repeats put the shift at 32+; a 32-bit shift would wrap the count to 0 or 1 and decode quietly.
        var body = Cat(Px(5, 6, 7, 130), Px(1, 1, 1, 0), Px(1, 1, 1, 0), Px(1, 1, 1, 0), Px(1, 1, 1, 0), Px(1, 1, 1, 1));
        Refuse(File(10, 1, body), "overflow");
    }

    [Fact]
    public void OldRle_RepeatWithNoPreviousPixel_IsRefused()
    {
        Refuse(File(3, 1, Px(1, 1, 1, 2)), "previous pixel");
    }

    [Theory]
    [InlineData("+Y 4 +X 8")]
    [InlineData("-Y 4 -X 8")]
    [InlineData("+Y 4 -X 8")]
    [InlineData("-X 8 -Y 4")]
    [InlineData("+X 8 +Y 4")]
    public void OtherOrientations_AreRefusedByName(string resolution)
    {
        var parts = resolution.Split(' ');
        Refuse(Cat(Head(resolution, "FORMAT=32-bit_rle_rgbe"), new byte[8 * 4 * 4]), $"{parts[0]} {parts[2]}");
    }

    [Theory]
    [InlineData("-Y 4")]
    [InlineData("-Y x +X 8")]
    [InlineData("-Y 4 +X")]
    [InlineData("-Y -4 +X 8")]
    [InlineData("-Y 0 +X 8")]
    [InlineData("-Y 4 +X 0")]
    [InlineData("")]
    public void MalformedResolutionLines_AreRefused(string resolution)
    {
        Should.Throw<InvalidDataException>(() => RadianceFile.Read(Cat(Head(resolution, "FORMAT=32-bit_rle_rgbe"), new byte[512])));
    }

    [Fact]
    public void UnknownFormat_IsRefusedByName()
    {
        Refuse(Cat(Head("-Y 1 +X 1", "FORMAT=32-bit_rle_cmyk"), Px(1, 1, 1, 128)), "32-bit_rle_cmyk");
    }

    [Fact]
    public void BadExposure_IsRefused()
    {
        Refuse(File(1, 1, Px(1, 1, 1, 128), "EXPOSURE=abc"), "EXPOSURE");
        Refuse(File(1, 1, Px(1, 1, 1, 128), "EXPOSURE=0"), "EXPOSURE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("#?SOMETHING\n\n-Y 1 +X 1\n")]
    [InlineData("#?RADIANCE")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n")]
    public void MissingMagicOrHeaderEnd_IsRefused(string text)
    {
        Should.Throw<InvalidDataException>(() => RadianceFile.Read(Encoding.Latin1.GetBytes(text)));
    }

    [Fact]
    public void ADeclaredSizeOverTheCeiling_IsRefusedBeforeAnyPixelIsRead()
    {
        // 6 header bytes and no pixels at all: the ceiling answers first, not "truncated".
        Refuse(Cat(Head("-Y 100000 +X 100000", "FORMAT=32-bit_rle_rgbe")), "limit");
        Refuse(File(10, 10, new byte[400]), "limit", maximumPixels: 99);
        RadianceFile.Read(File(10, 10, new byte[400]), maximumPixels: 100).Width.ShouldBe(10);
    }

    [Fact]
    public void EveryTruncation_IsRefusedAsInvalidData()
    {
        const int w = 20, h = 3;
        var full = RadianceFile.Write(RadianceOracleTests.TestImage(w, h), w, h);
        RadianceFile.Read(full).Width.ShouldBe(w);
        for (int n = 0; n < full.Length; n++)
            Should.Throw<InvalidDataException>(() => RadianceFile.Read(full.AsSpan(0, n)), $"prefix of {n} of {full.Length} bytes");
    }

    [Fact]
    public void CorruptedBodies_OnlyEverThrowInvalidData()
    {
        const int w = 40, h = 4;
        var full = RadianceFile.Write(RadianceOracleTests.TestImage(w, h), w, h);
        int bodyStart = BodyStart(full);
        var rnd = new Random(1);
        for (int round = 0; round < 500; round++)
        {
            var copy = (byte[])full.Clone();
            for (int k = 0; k < 3; k++) copy[bodyStart + rnd.Next(copy.Length - bodyStart)] = (byte)rnd.Next(256);
            try { RadianceFile.Read(copy); }
            catch (InvalidDataException) { }
        }
    }

    // ------------------------------------------------------------ writer

    [Fact]
    public void Writer_EmitsTheDocumentedHeader_AndTheStandardRgbeForOne()
    {
        var bytes = RadianceFile.Write([1f, 1f, 1f], 1, 1);
        Encoding.Latin1.GetString(bytes, 0, bytes.Length - 4).ShouldBe("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n");
        bytes[^4..].ShouldBe(Px(128, 128, 128, 129)); // 1.0 = 128 * 2^(129 - 136)
    }

    [Fact]
    public void Writer_UsesNewStyleRle_FromWidth8_AndFlatOutsideIt()
    {
        foreach (int w in new[] { 8, 32767 })
        {
            var b = RadianceFile.Write(new float[w * 3], w, 1);
            int o = BodyStart(b);
            b[o].ShouldBe((byte)2); b[o + 1].ShouldBe((byte)2);
            ((b[o + 2] << 8) | b[o + 3]).ShouldBe(w);
        }
        // Outside 8..32767 a line is exactly 4 bytes per pixel: no marker, no run codes.
        foreach (int w in new[] { 7, 32768 })
        {
            var b = RadianceFile.Write(new float[w * 3], w, 1);
            (b.Length - BodyStart(b)).ShouldBe(w * 4);
        }
    }

    [Fact]
    public void Writer_ClampsNegativeNanAndTiny_AndSaturatesTheTop()
    {
        float[] rgb = [-1f, float.NaN, -0f, 1e-33f, 0f, 1e-40f, float.PositiveInfinity, 1f, 0f, float.MaxValue, 0f, 0f];
        var img = RadianceFile.Read(RadianceFile.Write(rgb, 4, 1));
        img.Pixels[..3].ShouldBe([0f, 0f, 0f]);  // negative and NaN
        img.Pixels[3..6].ShouldBe([0f, 0f, 0f]); // below 1e-32: black
        // The exponent byte tops out at 255 (2^119 per mantissa unit), so the largest value is 255 * 2^119.
        float top = 255f * MathF.Pow(2f, 119);
        img.Pixels[6].ShouldBe(top);             // +Infinity saturates
        img.Pixels[7].ShouldBe(0f);              // 1.0 is lost next to a saturated channel
        img.Pixels[9].ShouldBe(top);             // as does float.MaxValue (3.4e38 > 1.7e38)
    }

    [Fact]
    public void RoundTrip_IsTheRgbeQuantisationOfTheInput_AndIdempotent()
    {
        const int w = 70, h = 10;
        var src = RadianceOracleTests.TestImage(w, h, seed: 5);
        var hdr = RadianceFile.Write(src, w, h);
        var back = RadianceFile.Read(hdr);
        back.Width.ShouldBe(w); back.Height.ShouldBe(h);

        for (int p = 0; p < w * h; p++)
        {
            float max = Math.Max(src[p * 3], Math.Max(src[p * 3 + 1], src[p * 3 + 2]));
            if (max < 1e-32f) { back.Pixels.AsSpan(p * 3, 3).ToArray().ShouldBe([0f, 0f, 0f]); continue; }
            int e = (int)MathF.Floor(MathF.Log2(max)) + 1;   // max = f * 2^e, f in [0.5, 1)
            float step = MathF.Pow(2f, e - 8);                // one mantissa unit
            for (int c = 0; c < 3; c++)
            {
                float s = Math.Max(src[p * 3 + c], 0f), d = back.Pixels[p * 3 + c];
                d.ShouldBeLessThanOrEqualTo(s);              // truncation, never up
                (s - d).ShouldBeLessThan(step * 1.0001f);    // by less than one mantissa unit
                (d / step).ShouldBe(MathF.Floor(d / step));  // and on the 8-bit grid
            }
        }

        // Re-encoding what was decoded changes nothing: the decoded floats are exactly representable.
        RadianceFile.Write(back.Pixels, w, h).ShouldBe(hdr);
    }

    [Fact]
    public void WriteImage_KeepsFormatAndExposure()
    {
        var src = new RadianceImage { Width = 2, Height = 1, Pixels = [1f, 2f, 3f, 0.5f, 0.25f, 0.125f], Format = RadianceFormat.Xyze, Exposure = 2.5 };
        var back = RadianceFile.Read(RadianceFile.Write(src));
        back.Format.ShouldBe(RadianceFormat.Xyze);
        back.Exposure.ShouldBe(2.5);
        back.Pixels.ShouldBe(src.Pixels); // all exactly representable
    }

    [Fact]
    public void Writer_ChecksItsArguments()
    {
        Should.Throw<ArgumentException>(() => RadianceFile.Write(new float[5], 1, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => RadianceFile.Write(new float[0], 0, 1));
    }

    [Fact]
    public void ReadInfo_reports_the_header_without_touching_the_pixels()
    {
        var f = File(2, 1, Px(128, 64, 32, 129, 0, 0, 0, 0), "EXPOSURE=2", "EXPOSURE=4");
        var info = RadianceFile.ReadInfo(f);
        var full = RadianceFile.Read(f);
        (info.Width, info.Height, info.Format, info.Exposure).ShouldBe((2, 1, RadianceFormat.Rgbe, 8.0));
        info.HeaderLines.ShouldBe(full.HeaderLines);
        info.Pixels.ShouldBeEmpty();

        // No body at all: Read needs scanlines, ReadInfo does not.
        var headOnly = f[..^8];
        RadianceFile.ReadInfo(headOnly).Width.ShouldBe(2);
        Should.Throw<InvalidDataException>(() => RadianceFile.Read(headOnly));

        // The same refusals as Read for the header itself, ceiling included.
        Should.Throw<InvalidDataException>(() => RadianceFile.ReadInfo(f, maximumPixels: 1));
        Should.Throw<InvalidDataException>(() => RadianceFile.ReadInfo(Head("+Y 1 +X 1")));
    }
}
