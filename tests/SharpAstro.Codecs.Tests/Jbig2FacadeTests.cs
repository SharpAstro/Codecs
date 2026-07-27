using SharpAstro.Codecs.Abstractions;
using SharpAstro.Jbig2;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Facade tests for the JBIG2 adapter — the courtesy path, for standalone
/// <c>.jb2</c> files that actually have a header to sniff. The format's real use
/// (PDF's <c>/JBIG2Decode</c>) cannot come through the facade at all, and that
/// asymmetry is asserted here too: an embedded stream has no signature, so
/// <see cref="ImageCodecs.CanDecode"/> must not claim it.
/// </summary>
public sealed class Jbig2FacadeTests
{
    private static readonly string[] Glyph =
    [
        "..######..",
        "..#....#..",
        "..#.##.#..",
        "..#....#..",
        "..######..",
    ];

    private static byte[] BuildFile()
    {
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        return Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)),
            Jbig2StreamBuilder.Segment(2, SegmentType.EndOfFile, 0, []));
    }

    [Fact]
    public void CanDecode_RecognisesTheJb2FileSignature()
    {
        ImageCodecs.CanDecode(BuildFile()).ShouldBeTrue();
        Jbig2ImageDecoder.CanDecode(Jbig2Decoder.FileSignature).ShouldBeTrue();
    }

    [Fact]
    public void CanDecode_DoesNotClaimAnEmbeddedStream()
    {
        // A PDF-embedded stream starts with a segment header, not a signature.
        // Sniffing must not claim it — the caller has to go to Jbig2Decoder.Decode
        // with the globals and dimensions the PDF holds.
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var embedded = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));

        Jbig2ImageDecoder.CanDecode(embedded).ShouldBeFalse();
    }

    [Fact]
    public void TryReadInfo_ReportsGrayEightBit()
    {
        ImageCodecs.TryReadInfo(BuildFile(), out var info).ShouldBeTrue();

        info.Width.ShouldBe(10);
        info.Height.ShouldBe(5);
        info.Channels.ShouldBe(1);
        info.SampleFormat.ShouldBe(SampleFormat.UInt8);
    }

    [Fact]
    public void TryDecode_ProducesTheGrayFidelityRaster()
    {
        ImageCodecs.TryDecode(BuildFile(), out var image).ShouldBeTrue();

        image!.Width.ShouldBe(10);
        image.Height.ShouldBe(5);
        image.Channels.ShouldBe(1);
        image.SampleFormat.ShouldBe(SampleFormat.UInt8);

        var expected = Jbig2StreamBuilder.FromRows(Glyph);
        for (var i = 0; i < expected.Data.Length; i++)
            image.Pixels[i].ShouldBe(expected.Data[i] != 0 ? (byte)0 : (byte)255, $"pixel {i}");
    }

    [Fact]
    public void TryDecodeIntoRgba8_ExpandsGrayAcrossRgbWithOpaqueAlpha()
    {
        var destination = new byte[10 * 5 * 4];
        ImageCodecs.TryDecodeIntoRgba8(BuildFile(), destination).ShouldBeTrue();

        var expected = Jbig2StreamBuilder.FromRows(Glyph);
        for (var i = 0; i < expected.Data.Length; i++)
        {
            var value = expected.Data[i] != 0 ? (byte)0 : (byte)255;
            destination[i * 4].ShouldBe(value);
            destination[i * 4 + 1].ShouldBe(value);
            destination[i * 4 + 2].ShouldBe(value);
            destination[i * 4 + 3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void TryDecodeIntoRgba8_RejectsAnUndersizedDestination()
    {
        Jbig2ImageDecoder.TryDecodeIntoRgba8(BuildFile(), new byte[16]).ShouldBeFalse();
    }

    [Fact]
    public void TryDecode_OnAFileNeedingAnUnimplementedFeature_ReturnsFalse()
    {
        // The facade contract is "false for undecodable", not "throws". A .jb2
        // full of text regions is exactly that until rung 3 lands.
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(8, 8)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1, [0, 0, 0, 0]));

        Jbig2ImageDecoder.TryDecode(file, out var image).ShouldBeFalse();
        image.ShouldBeNull();
        ImageCodecs.TryDecode(file, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecode_OnATruncatedFile_ReturnsFalse()
    {
        var file = BuildFile();

        Jbig2ImageDecoder.TryDecode(file.AsSpan(0, 20).ToArray(), out var image).ShouldBeFalse();
        image.ShouldBeNull();
    }

    [Fact]
    public void Jb2Signature_DoesNotCollideWithTheOtherRegisteredCodecs()
    {
        // 0x97 leads nothing else in the family, but the registry is order-
        // sensitive, so assert the sniff actually reaches JBIG2 rather than being
        // swallowed by a codec registered ahead of it.
        ImageCodecs.TryReadInfo(BuildFile(), out var info).ShouldBeTrue();
        info.Channels.ShouldBe(1);
        info.SampleFormat.ShouldBe(SampleFormat.UInt8);
    }
}
