using System.Text.RegularExpressions;
using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Pnm;
using Shouldly;
using static SharpAstro.Codecs.Tests.PnmFixtures;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The Netpbm reader, judged by Magick.NET (ImageMagick) in both roles: it WRITES every variant it can
/// emit, and READS the same bytes back as the oracle for the pixels. Before a case is trusted, the
/// bytes are checked to really be the variant the case claims (magic, maxval) - a harness that
/// quietly wrote P6 for every case would pass for the wrong reason.
/// </summary>
public sealed class PnmReaderTests
{
    // ---- every variant ImageMagick writes ---------------------------------------------------

    public static TheoryData<MagickFormat, string, int, bool> MagickVariants()
    {
        var data = new TheoryData<MagickFormat, string, int, bool>();

        // PBM: bits; plain (P1) and raw (P4). Magick quantises to 1 bit whatever the depth.
        data.Add(MagickFormat.Pbm, "I", 1, false);
        data.Add(MagickFormat.Pbm, "I", 1, true);

        // PGM / PPM: raw writes maxval 2^depth - 1, plain writes 255 or 65535.
        foreach (var depth in new[] { 1, 2, 4, 8, 10, 12, 16 })
        {
            data.Add(MagickFormat.Pgm, "I", depth, false);
            data.Add(MagickFormat.Ppm, "RGB", depth, false);
        }

        foreach (var depth in new[] { 8, 12, 16 })
        {
            data.Add(MagickFormat.Pgm, "I", depth, true);
            data.Add(MagickFormat.Ppm, "RGB", depth, true);
        }

        // PAM: GRAYSCALE / BLACKANDWHITE, RGB, and the _ALPHA forms.
        foreach (var mapping in new[] { "I", "IA", "RGB", "RGBA" })
        {
            foreach (var depth in new[] { 1, 4, 8, 12, 16 })
                data.Add(MagickFormat.Pam, mapping, depth, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MagickVariants))]
    public void EveryVariantMagickWrites_DecodesToMagicksPixels(MagickFormat format, string mapping, int depth, bool plain)
    {
        // 13 x 9: not a multiple of 8, so PBM rows have padding bits.
        var file = MagickWrite(format, mapping, 13, 9, depth, plain);

        // Is it really the variant claimed?
        var magic = MagickMagic(format, plain);
        HeaderText(file).ShouldStartWith(magic + "\n");

        var header = PnmReader.ReadHeader(file);
        header.Version.ShouldBe(magic[1] - '0');
        header.Channels.ShouldBe(mapping.Length);

        if (format != MagickFormat.Pbm)
        {
            var expectedMaxval = plain ? (depth <= 8 ? 255 : 65535) : (1 << depth) - 1;
            var maxvalText = format == MagickFormat.Pam
                ? Regex.Match(HeaderText(file, 128), @"MAXVAL (\d+)\n").Groups[1].Value
                : Regex.Match(HeaderText(file), @"^P\d\n13 9\n(\d+)\n").Groups[1].Value;
            maxvalText.ShouldBe(expectedMaxval.ToString());
            header.MaxValue.ShouldBe(expectedMaxval);
        }

        if (format == MagickFormat.Pam && mapping == "IA" && depth == 1)
        {
            // ImageMagick reads its own 1-bit grey+alpha PAM back with the alpha inverted (a sample of 1
            // comes out transparent, against the PAM spec and against the RGBA 1-bit form, which it
            // reads correctly), so the oracle is wrong here. The spec's reading of the raw bytes is
            // the judge: one byte per sample, each 0 or 1.
            var raster = file[^(13 * 9 * 2)..].Select(b => (ushort)b).ToArray();
            ShouldDecodeLikeTheSpec(file, raster, 2, 1);
            return;
        }

        ShouldDecodeLikeMagick(file, mapping.Length);
    }

    [Fact]
    public void MagickPamTupleTypes_AreTheOnesTheReaderMaps()
    {
        // Magick names them; confirm what it wrote against what we read.
        var cases = new (string Mapping, int Depth, string Tuple, PnmTupleType Type)[]
        {
            ("I", 1, "BLACKANDWHITE", PnmTupleType.BlackAndWhite),
            ("I", 8, "GRAYSCALE", PnmTupleType.Grayscale),
            ("IA", 8, "GRAYSCALE_ALPHA", PnmTupleType.GrayscaleAlpha),
            ("IA", 1, "BLACKANDWHITE_ALPHA", PnmTupleType.BlackAndWhiteAlpha),
            ("RGB", 8, "RGB", PnmTupleType.Rgb),
            ("RGBA", 8, "RGB_ALPHA", PnmTupleType.RgbAlpha),
        };
        foreach (var (mapping, depth, tuple, type) in cases)
        {
            var file = MagickWrite(MagickFormat.Pam, mapping, 4, 3, depth, false);
            HeaderText(file, 128).ShouldContain($"TUPLTYPE {tuple}\n");
            PnmReader.ReadHeader(file).TupleType.ShouldBe(type);
        }
    }

    // ---- maxvals ImageMagick will not write: it reads them, and is the oracle ---------------

    private static readonly int[] OddMaxvals =
        [1, 2, 3, 4, 5, 7, 10, 15, 31, 63, 100, 127, 254, 255, 256, 257, 1000, 1023, 4095, 4096, 10000, 32767, 32768, 65534, 65535];

    public static TheoryData<int, string> OddMaxvalCases()
    {
        var data = new TheoryData<int, string>();
        foreach (var maxval in OddMaxvals)
        {
            foreach (var kind in new[] { "P2", "P3", "P5", "P6", "GRAYSCALE", "GRAYSCALE_ALPHA", "RGB", "RGB_ALPHA" })
                data.Add(maxval, kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OddMaxvalCases))]
    public void OddMaxval_DecodesToMagicksPixels(int maxval, string kind)
    {
        const int w = 11, h = 7;
        var depth = kind switch { "P2" or "P5" or "GRAYSCALE" => 1, "GRAYSCALE_ALPHA" => 2, "P3" or "P6" or "RGB" => 3, _ => 4 };
        var samples = Samples(w * h * depth, maxval);
        var file = kind switch
        {
            "P2" => Plain(2, w, h, maxval, samples),
            "P3" => Plain(3, w, h, maxval, samples),
            "P5" => Raw(5, w, h, maxval, samples),
            "P6" => Raw(6, w, h, maxval, samples),
            _ => Pam(w, h, depth, maxval, kind, samples),
        };

        // The bytes are what the case says: the header names this maxval.
        HeaderText(file, 100).ShouldContain(maxval.ToString());
        PnmReader.Read(file).Samples.ShouldBe(samples); // native samples untouched

        var raw = kind is not ("P2" or "P3");
        if ((raw && MagickReadsRawMaxvalUnscaled(maxval)) || (kind == "GRAYSCALE_ALPHA" && maxval == 1))
        {
            // Here ImageMagick does not divide by maxval (see MagickReadsRawMaxvalUnscaled), or inverts
            // a 1-bit grey+alpha PAM's alpha (see the Magick-written IA case above): the Netpbm
            // documents are the judge, not it.
            ShouldDecodeLikeTheSpec(file, samples, depth, maxval);
            return;
        }

        ShouldDecodeLikeMagick(file, depth);
    }

    [Fact]
    public void EveryEightBitMaxval_ScalesEverySampleLikeMagick()
    {
        // The whole 8-bit scaling table: every maxval 1..255, every sample 0..maxval, as one P2 row.
        for (var maxval = 1; maxval <= 255; maxval++)
        {
            var samples = Enumerable.Range(0, maxval + 1).Select(v => (ushort)v).ToArray();
            var file = Plain(2, maxval + 1, 1, maxval, samples);
            ShouldDecodeLikeMagick(file, 1);
        }
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(300)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1023)]
    [InlineData(4095)] // 7 samples round differently in float32 than in exact arithmetic
    [InlineData(4096)]
    [InlineData(12345)]
    [InlineData(31999)]
    [InlineData(40000)]
    [InlineData(60000)]
    [InlineData(65000)]
    [InlineData(65534)] // 63 of them
    public void EverySampleOfAWideMaxval_ScalesLikeMagick(int maxval)
    {
        var samples = Enumerable.Range(0, maxval + 1).Select(v => (ushort)v).ToArray();

        // Plain: ImageMagick scales every maxval, so it is the oracle for every sample.
        ShouldDecodeLikeMagick(Plain(2, maxval + 1, 1, maxval, samples), 1);

        // Raw: the same samples, where ImageMagick scales a raw raster by its maxval too.
        var raw = Raw(5, maxval + 1, 1, maxval, samples);
        if (MagickReadsRawMaxvalUnscaled(maxval)) ShouldDecodeLikeTheSpec(raw, samples, 1, maxval);
        else ShouldDecodeLikeMagick(raw, 1);
    }

    // ---- PBM ---------------------------------------------------------------------------------

    [Fact]
    public void PlainBitmap_NeedsNoSeparators_AndOneIsBlack()
    {
        // P1 digits may run together; 1 = black, so the decoded samples are 0 = black.
        var image = PnmReader.Read(Ascii("P1\n5 2\n10110\n01001\n"));
        image.MaxValue.ShouldBe(1);
        image.Samples.ShouldBe(new ushort[] { 0, 1, 0, 0, 1, 1, 0, 1, 1, 0 });

        PnmReader.Read(Ascii("P1 5 2 1 0 1 1 0 0 1 0 0 1")).Samples.ShouldBe(image.Samples);
        // Mixed: some separated, some not, with a comment in the middle.
        PnmReader.Read(Ascii("P1\n5 2\n1 0110 # a comment\n01001")).Samples.ShouldBe(image.Samples);
    }

    [Fact]
    public void RawBitmap_UnpacksMsbFirst_AndRowsArePadded()
    {
        // 10 x 2: each row is 2 bytes, the last 6 bits of the second byte are padding (ignored).
        byte[] file = [.. Ascii("P4\n10 2\n"), 0b1010_0000, 0b1100_0000, 0b0101_0101, 0b0111_1111];
        var image = PnmReader.Read(file);
        image.Samples.ShouldBe(new ushort[]
        {
            0, 1, 0, 1, 1, 1, 1, 1, 0, 0,
            1, 0, 1, 0, 1, 0, 1, 0, 1, 0,
        });

        ShouldDecodeLikeMagick(file, 1);
    }

    [Fact]
    public void Bitmap_BecomesZeroAndFullScale_AtEightBits()
    {
        ImageCodecs.TryDecode(Ascii("P1\n2 1\n10"), out var image).ShouldBeTrue();
        image!.SampleFormat.ShouldBe(SampleFormat.UInt8);
        image.Channels.ShouldBe(1);
        image.Pixels.ToArray().ShouldBe(new byte[] { 0, 255 });
    }

    [Fact]
    public void PamBlackAndWhite_IsZeroBlack_UnlikePbm()
    {
        var pam = PnmReader.Read(Pam(2, 1, 1, 1, "BLACKANDWHITE", [0, 1]));
        pam.Samples.ShouldBe(new ushort[] { 0, 1 });
        pam.TupleType.ShouldBe(PnmTupleType.BlackAndWhite);
    }

    // ---- headers ----------------------------------------------------------------------------

    [Fact]
    public void Comments_AreSkippedWhereverWhitespaceIsAllowed()
    {
        byte[] file =
        [
            .. Ascii("P5 # the magic is followed by a comment\n#a whole-line comment\n3#glued to the width\n 2 \n#between\n255#glued\n"),
            1, 2, 3, 4, 5, 6,
        ];
        var image = PnmReader.Read(file);
        (image.Width, image.Height, image.MaxValue).ShouldBe((3, 2, 255));
        image.Samples.ShouldBe(new ushort[] { 1, 2, 3, 4, 5, 6 });
    }

    [Fact]
    public void Comments_AreSkippedInPlainRasters()
    {
        var image = PnmReader.Read(Ascii("P2\n3 1\n9\n1 # one\n# a line\n2 3 # tail"));
        image.Samples.ShouldBe(new ushort[] { 1, 2, 3 });
    }

    [Fact]
    public void ACommentLineMayHoldWhateverBytesItLikes()
    {
        byte[] file =
        [
            .. Ascii("P5\n# "), 0xFF, 0xFE, 0x00, .. Ascii(" binary junk P6 9 9 255\n2 1\n255\n"), 7, 8,
        ];
        PnmReader.Read(file).Samples.ShouldBe(new ushort[] { 7, 8 });
    }

    [Fact]
    public void ExactlyOneWhitespaceByte_SeparatesTheMaxvalFromARawRaster()
    {
        // The first sample is 0x20 (a space) and the second 0x0A: neither may be eaten as whitespace.
        byte[] file = [.. Ascii("P5\n2 1\n255\n"), 0x20, 0x0A];
        PnmReader.Read(file).Samples.ShouldBe(new ushort[] { 0x20, 0x0A });

        // Same with a single space as the separator, and a comment-terminating newline.
        byte[] spaced = [.. Ascii("P5 2 1 255 "), 0x20, 0x0A];
        PnmReader.Read(spaced).Samples.ShouldBe(new ushort[] { 0x20, 0x0A });

        byte[] commented = [.. Ascii("P5 2 1 255# note\n"), 0x20, 0x0A];
        PnmReader.Read(commented).Samples.ShouldBe(new ushort[] { 0x20, 0x0A });

        byte[] crlf = [.. Ascii("P5 2 1 255# note\r\n"), 0x20, 0x0A];
        PnmReader.Read(crlf).Samples.ShouldBe(new ushort[] { 0x20, 0x0A });
    }

    [Fact]
    public void SixteenBitSamples_AreBigEndian()
    {
        byte[] file = [.. Ascii("P5\n2 1\n65535\n"), 0x12, 0x34, 0xFF, 0x01];
        PnmReader.Read(file).Samples.ShouldBe(new ushort[] { 0x1234, 0xFF01 });

        byte[] odd = [.. Ascii("P5\n2 1\n1000\n"), 0x03, 0xE8, 0x00, 0x07];
        PnmReader.Read(odd).Samples.ShouldBe(new ushort[] { 1000, 7 });
    }

    [Fact]
    public void Pam_HeaderLines_CommentsAndTupleTypeVariants()
    {
        byte[] file =
        [
            .. Ascii("P7\n# made by hand\nWIDTH 2\n#again\nHEIGHT 1\nDEPTH 3\nMAXVAL 255\nTUPLTYPE RGB\nENDHDR\n"),
            1, 2, 3, 4, 5, 6,
        ];
        var image = PnmReader.Read(file);
        image.TupleType.ShouldBe(PnmTupleType.Rgb);
        image.Samples.ShouldBe(new ushort[] { 1, 2, 3, 4, 5, 6 });

        // Order of the header lines is free; CRLF line ends are tolerated.
        var shuffled = Concat(Ascii("P7\r\nMAXVAL 255\r\nDEPTH 3\r\nHEIGHT 1\r\nWIDTH 2\r\nTUPLTYPE RGB\r\nENDHDR\n"), [1, 2, 3, 4, 5, 6]);
        PnmReader.Read(shuffled).Samples.ShouldBe(image.Samples);

        // Several TUPLTYPE lines concatenate with a space.
        var split = Concat(Ascii("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB\nTUPLTYPE _ALPHA\nENDHDR\n"), [1, 2, 3, 4]);
        // "RGB _ALPHA" is not RGB_ALPHA: refused, not guessed.
        Should.Throw<NotSupportedException>(() => PnmReader.Read(split));
    }

    [Theory]
    [InlineData(1, PnmTupleType.Grayscale)]
    [InlineData(2, PnmTupleType.GrayscaleAlpha)]
    [InlineData(3, PnmTupleType.Rgb)]
    [InlineData(4, PnmTupleType.RgbAlpha)]
    public void Pam_WithoutATupleType_IsInferredFromTheDepth(int depth, PnmTupleType expected)
    {
        var file = Pam(1, 1, depth, 255, null, new ushort[depth]);
        var header = PnmReader.ReadHeader(file);
        header.TupleType.ShouldBe(expected);
        header.Channels.ShouldBe(depth);
    }

    // ---- refused -----------------------------------------------------------------------------

    [Fact]
    public void ASampleAboveMaxval_IsRefused_InEveryForm()
    {
        Should.Throw<InvalidDataException>(() => PnmReader.Read(Ascii("P2\n2 1\n9\n3 10")));
        Should.Throw<InvalidDataException>(() => PnmReader.Read([.. Ascii("P5\n2 1\n9\n"), 3, 10]));
        Should.Throw<InvalidDataException>(() => PnmReader.Read([.. Ascii("P5\n1 1\n1000\n"), 0x03, 0xE9]));
        Should.Throw<InvalidDataException>(() => PnmReader.Read(Pam(1, 1, 1, 15, "GRAYSCALE", [16])));

        // At maxval itself is fine.
        PnmReader.Read([.. Ascii("P5\n2 1\n9\n"), 3, 9]).Samples.ShouldBe(new ushort[] { 3, 9 });
    }

    [Theory]
    [InlineData("P5\n4 4\n255\n")]               // no raster at all
    [InlineData("P5\n4 4")]                       // no maxval
    [InlineData("P5\n4")]                         // no height
    [InlineData("P5")]                            // nothing
    [InlineData("P5\n3 3\n65535\n123456")]        // 16-bit raster short by 3 bytes
    [InlineData("P2\n3 3\n255\n1 2 3 4 5 6 7 8")] // plain, one sample short
    [InlineData("P1\n3 3\n10101010")]             // plain bitmap, one bit short
    [InlineData("P4\n9 3\n\x01\x02\x03\x04\x05")] // 2 bytes per row x 3 needs 6
    [InlineData("P3\n1 1\n255\n1 2")]             // RGB, one channel short
    public void ATruncatedFile_IsRefused(string text)
    {
        Should.Throw<InvalidDataException>(() => PnmReader.Read(Ascii(text)));
    }

    [Theory]
    [InlineData("P8\n1 1\n255\n\0")]
    [InlineData("P0\n1 1\n255\n\0")]
    [InlineData("P5x1 1\n255\n\0")]
    [InlineData("P5\n0 1\n255\n")]               // zero width
    [InlineData("P5\n1 0\n255\n")]               // zero height
    [InlineData("P5\n1 1\n0\n\0")]               // maxval 0
    [InlineData("P5\n1 1\n65536\n\0\0")]         // maxval above 65535
    [InlineData("P5\n-1 1\n255\n\0")]            // negative
    [InlineData("P5\n1 1x\n255\n\0")]            // junk glued to a token
    [InlineData("P5\n99999999999 1\n255\n\0")]   // beyond int
    [InlineData("P2\n2 1\n255\n1 2x")]           // junk glued to a plain sample
    [InlineData("P1\n2 1\n12")]                  // a '2' in a bitmap
    public void ABadHeaderOrSample_IsRefused(string text)
    {
        Should.Throw<InvalidDataException>(() => PnmReader.Read(Ascii(text)));
    }

    [Fact]
    public void Pam_Baseline_IsAccepted()
    {
        // The bad-header cases below each break exactly one thing of this.
        PnmReader.Read([.. Ascii("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nENDHDR\n"), 0]).Channels.ShouldBe(1);
    }

    [Theory]
    [InlineData("WIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\n")]                              // no ENDHDR
    [InlineData("HEIGHT 1\nDEPTH 1\nMAXVAL 255\nENDHDR\n")]                              // no WIDTH
    [InlineData("WIDTH 1\nWIDTH 2\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nENDHDR\n")]            // repeated
    [InlineData("WIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nCOLOUR RED\nENDHDR\n")]         // unknown line
    [InlineData("WIDTH 1\nHEIGHT 1\nDEPTH 0\nMAXVAL 255\nENDHDR\n")]                     // depth 0
    [InlineData("WIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 0\nENDHDR\n")]                       // maxval 0
    [InlineData("WIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 65536\nENDHDR\n")]                   // maxval too big
    [InlineData("WIDTH 1\nHEIGHT 1\nDEPTH 3\nMAXVAL 255\nTUPLTYPE GRAYSCALE\nENDHDR\n")] // depth/tuple mismatch
    [InlineData("WIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 2\nTUPLTYPE BLACKANDWHITE\nENDHDR\n")] // b/w with maxval 2
    [InlineData("WIDTH x\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nENDHDR\n")]                     // not a number
    public void ABadPamHeader_IsRefused(string header)
    {
        Should.Throw<InvalidDataException>(() => PnmReader.Read([.. Ascii("P7\n" + header), 0, 0, 0, 0]));
    }

    [Theory]
    [InlineData("CMYK", 4)]
    [InlineData("CMYK_ALPHA", 5)]
    [InlineData("HSV", 3)]
    public void APamTupleTypeTheReaderDoesNotKnow_IsNotSupported(string tuple, int depth)
    {
        var file = Pam(1, 1, depth, 255, tuple, new ushort[depth]);
        Should.Throw<NotSupportedException>(() => PnmReader.Read(file));
    }

    [Fact]
    public void ADeclaredSizePastTheCeiling_IsRefusedBeforeAnythingIsAllocated()
    {
        // 16 bytes declare a 100000 x 100000 image: 10^10 samples. It must fail fast on the header.
        var file = Ascii("P5\n100000 100000\n255\n\0");
        Should.Throw<InvalidDataException>(() => PnmReader.ReadHeader(file)).Message.ShouldContain("ceiling");
        Should.Throw<InvalidDataException>(() => PnmReader.Read(file)).Message.ShouldContain("ceiling");

        // Within the ceiling but past the input: refused as truncated, before the raster is allocated.
        var big = Ascii("P5\n16000 16000\n255\n\0"); // 256 M samples = exactly the ceiling, 1 byte of input
        var e = Should.Throw<InvalidDataException>(() => PnmReader.Read(big));
        e.Message.ShouldContain("truncated");

        // A caller's own ceiling applies, to the sample count (channels included).
        var rgb = Pam(10, 10, 3, 255, "RGB", new ushort[300]);
        PnmReader.Read(rgb, maximumSamples: 300).Samples.Length.ShouldBe(300);
        Should.Throw<InvalidDataException>(() => PnmReader.Read(rgb, maximumSamples: 299));
        Should.Throw<InvalidDataException>(() => PnmReader.ReadHeader(rgb, maximumSamples: 299));
    }

    [Fact]
    public void TrailingBytesAfterTheRaster_AreIgnored()
    {
        PnmReader.Read([.. Ascii("P5\n2 1\n255\n"), 1, 2, 3, 4, 5]).Samples.ShouldBe(new ushort[] { 1, 2 });
    }

    [Fact]
    public void NoPrefixOfAValidFile_ThrowsAnythingButTheDocumentedExceptions()
    {
        foreach (var file in new[]
                 {
                     MagickWrite(MagickFormat.Pam, "RGBA", 5, 4, 12, false),
                     MagickWrite(MagickFormat.Ppm, "RGB", 5, 4, 8, true),
                     MagickWrite(MagickFormat.Pbm, "I", 13, 4, 1, false),
                 })
        {
            for (var length = 0; length < file.Length; length++)
            {
                try { PnmReader.Read(file.AsSpan(0, length)); }
                catch (InvalidDataException) { }
                catch (NotSupportedException) { }
            }

            PnmReader.Read(file).ShouldNotBeNull(); // and the whole thing is fine
        }
    }
}
