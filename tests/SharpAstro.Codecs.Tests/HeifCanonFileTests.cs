using System.Buffers.Binary;
using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The HEIF reader against real Canon files (issue #66): an EOS R7 HDR-PQ <c>.HIF</c> (a 20-tile grid)
/// and the HEVC previews inside an EOS R6 HDR-PQ <c>.CR3</c>. Both are third-party and unlicensed, so
/// they are not committed: <c>fetch-heif-external.sh</c> puts them in <c>Fixtures/heif-external/</c>, and
/// every test here skips, visibly, when its file is absent. Magick.NET (libheif) is the reference for what
/// the files contain.
/// <para>
/// The envelope tests pin what these streams use, measured by <see cref="HevcSurvey"/>, because that is
/// the scope of the HEVC decoder (roadmap/heif.md, rung 2): if a new Canon file steps outside it, these
/// fail first and say how.
/// </para>
/// </summary>
public class HeifCanonFileTests(ITestOutputHelper output)
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "heif-external");

    internal static byte[] Load(string name)
    {
        var path = Path.Combine(Root, name);
        Assert.SkipUnless(File.Exists(path), $"{name} is not present; run tests/SharpAstro.Codecs.Tests/fetch-heif-external.sh");
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void R7_IsRecognisedAsHeif()
    {
        var file = Load("canon-r7-hdrpq.hif");
        HeifReader.IsHeif(file.AsSpan(0, 64)).ShouldBeTrue();
        HeifReader.IsHeif(Load("canon-r6-hdrpq.cr3").AsSpan(0, 64)).ShouldBeFalse("a CR3 is ISOBMFF but not HEIF");
    }

    [Fact]
    public void R7_PrimaryIsATwentyTileGridOfTenBitPqImages()
    {
        var file = Load("canon-r7-hdrpq.hif");
        var heif = HeifReader.Read(file);
        heif.MajorBrand.ShouldBe("heix");
        heif.PrimaryItem.Type.ShouldBe("grid");

        var image = heif.DescribePrimary(file);
        (image.Width, image.Height).ShouldBe((6960u, 4640u));
        image.Grid.ShouldNotBeNull();
        (image.Grid.Rows, image.Grid.Columns).ShouldBe((5, 4));
        image.Grid.TileItemIds.ShouldBe(Enumerable.Range(256, 20).Select(i => (uint)i));
        (image.TileWidth, image.TileHeight).ShouldBe((1792u, 960u));
        image.BitDepth.ShouldBe(10);
        image.ChromaFormat.ShouldBe(HevcChromaFormat.Yuv422);
        image.Nclx.ShouldBe(new HeifNclxColour(ColorPrimaries.BT2020, TransferFunction.Pq, MatrixCoefficients.BT2020NCL, FullRange: true));
        image.Icc.ShouldBeNull();
        image.Rotation.ShouldBe(0);
        image.Mirror.ShouldBeNull();
        image.CleanAperture.ShouldBeNull();

        // The tiles are hidden; the image a viewer shows is the grid.
        image.Grid.TileItemIds.ShouldAllBe(id => heif.GetItem(id)!.Hidden);
    }

    [Fact]
    public void R7_HasTwoHevcThumbnailsAndExifAndXmp()
    {
        var file = Load("canon-r7-hdrpq.hif");
        var heif = HeifReader.Read(file);

        var thumbnails = heif.ThumbnailsOf(heif.PrimaryItem).Select(t => heif.Describe(file, t)).ToList();
        thumbnails.Select(t => (t.Width, t.Height)).ShouldBe([(320u, 214u), (1620u, 1080u)]);
        thumbnails.ShouldAllBe(t => t.Grid == null && t.ChromaFormat == HevcChromaFormat.Yuv422 && t.BitDepth == 10);

        var metadata = heif.MetadataOf(heif.PrimaryItem);
        metadata.Select(m => m.Type).ShouldBe(["Exif", "mime"]);
        metadata[1].ContentType.ShouldBe("application/rdf+xml");

        // A HEIF Exif item is a 32-bit offset to the TIFF header, then the Exif data (23008-12 A.2.1).
        var exif = heif.ReadItemData(file, metadata[0]);
        var offset = BinaryPrimitives.ReadUInt32BigEndian(exif);
        var tiff = Convert.ToHexString(exif.AsSpan(4 + (int)offset, 4));
        tiff.ShouldBeOneOf("49492A00", "4D4D002A");

        var xmp = System.Text.Encoding.UTF8.GetString(heif.ReadItemData(file, metadata[1]));
        xmp.ShouldContain("x:xmpmeta");
    }

    [Fact]
    public void R7_SizeAndDepthAgreeWithMagick()
    {
        var file = Load("canon-r7-hdrpq.hif");
        var image = HeifReader.Read(file).DescribePrimary(file);

        using var magick = new MagickImage(file, MagickFormat.Heic);
        ((uint)magick.Width, (uint)magick.Height).ShouldBe((image.Width, image.Height));
        ((int)magick.Depth).ShouldBe(image.BitDepth);
    }

    [Fact]
    public void R7_EveryCodedImageIsInsideTheMeasuredEnvelope()
    {
        var file = Load("canon-r7-hdrpq.hif");
        var heif = HeifReader.Read(file);
        foreach (var item in heif.Items.Where(i => i.Type == "hvc1"))
        {
            var image = heif.Describe(file, item);
            var survey = HevcSurvey.Of(image.Coding, heif.ReadItemData(file, item));
            AssertInsideEnvelope(survey, $"R7 {item}");

            // The coded picture covers ispe but is not cropped to it by the stream: there is no
            // conformance window, so the crop to ispe is the reader's job.
            survey.Sps.Width.ShouldBeGreaterThanOrEqualTo((int)image.Width);
            survey.Sps.Height.ShouldBeGreaterThanOrEqualTo((int)image.Height);
            if (item.Id == 512) output.WriteLine($"thumbnail: coded {survey.Sps.Width}x{survey.Sps.Height}, ispe {image.Width}x{image.Height}");
            if (item.Id == 256) output.WriteLine(survey.Report());
        }
    }

    [Fact]
    public void R6_CrPreviewsAreInsideTheMeasuredEnvelope()
    {
        var file = Load("canon-r6-hdrpq.cr3");
        var previews = Cr3HevcPreviews.Find(file);
        previews.Select(p => (p.Name, p.Width, p.Height)).ShouldBe([("THMB", 320, 214), ("PRVW", 1620, 1080)]);
        foreach (var preview in previews)
        {
            var survey = HevcSurvey.Of(HeifProperties.ReadHevcConfiguration(preview.HvcC), preview.NalUnits);
            AssertInsideEnvelope(survey, $"R6 {preview.Name}");
            output.WriteLine($"--- {preview.Name}: coded {survey.Sps.Width}x{survey.Sps.Height}");
            output.WriteLine(survey.Report());
        }
    }

    /// <summary>
    /// Magick.NET can serve as the pixel oracle for the CR3 previews too, once each is wrapped as a
    /// one-item HEIF; this pins that the wrapping is right, so rung 2 can compare pixels against it.
    /// <para>
    /// It also pins a difference that rung 2 must allow for: Canon codes these pictures larger than they
    /// are (320x320 for a 320x214 thumbnail, 1632x1088 for 1620x1080) and gives the real size only as
    /// <c>ispe</c> (and in its own header), with no conformance window and no <c>clap</c>. The libheif in
    /// Magick.NET 14.13.1 does not crop to <c>ispe</c> and hands back the whole coded picture. This
    /// reader's decode will crop to <c>ispe</c>, since that is the size the file says the image has, so
    /// a pixel comparison takes the top-left <c>ispe</c> region of Magick's output.
    /// </para>
    /// </summary>
    [Fact]
    public void R6_CrPreviewsDecodeThroughMagickOnceWrappedAsHeif()
    {
        var file = Load("canon-r6-hdrpq.cr3");
        foreach (var preview in Cr3HevcPreviews.Find(file))
        {
            var heif = HeifBuilder.WrapHevcPicture(preview.HvcC, preview.NalUnits, (uint)preview.Width, (uint)preview.Height, preview.Colr, preview.Pixi);

            // Our reader sees what was wrapped...
            var described = HeifReader.Read(heif).DescribePrimary(heif);
            (described.Width, described.Height).ShouldBe(((uint)preview.Width, (uint)preview.Height));
            var sps = HevcSurvey.Of(described.Coding, HeifReader.Read(heif).ReadItemData(heif, described.Item)).Sps;

            // ...and libheif decodes it, at the coded size rather than the ispe size.
            using var magick = new MagickImage(heif, MagickFormat.Heic);
            ((int)magick.Width, (int)magick.Height).ShouldBe((sps.Width, sps.Height), preview.Name);
            (sps.Width > preview.Width || sps.Height > preview.Height).ShouldBeTrue($"{preview.Name} is coded larger than it is");
            ((int)magick.Depth).ShouldBe(10);
        }
    }

    /// <summary>
    /// What every Canon HEVC picture seen so far uses. Rung 2's decoder is scoped to exactly this, and a
    /// stream outside it is refused by name rather than decoded wrongly.
    /// </summary>
    private static void AssertInsideEnvelope(HevcSurvey survey, string what)
    {
        var sps = survey.Sps;
        var pps = survey.Pps;
        sps.ProfileTierLevel.ProfileIdc.ShouldBe(4, what);
        sps.ProfileTierLevel.IntraConstraint.ShouldBeTrue(what);
        sps.ChromaFormatIdc.ShouldBe(2, what);
        (sps.BitDepthLuma, sps.BitDepthChroma).ShouldBe((10, 10), what);
        sps.SeparateColourPlanes.ShouldBeFalse(what);
        sps.RangeExtension.Any.ShouldBeFalse(what);
        sps.OtherExtensions.ShouldBeFalse(what);
        sps.SampleAdaptiveOffset.ShouldBeFalse(what);
        sps.PcmEnabled.ShouldBeFalse(what);
        sps.StrongIntraSmoothing.ShouldBeFalse(what);
        sps.ScalingListEnabled.ShouldBeTrue(what);
        sps.Log2MaxTransformBlockSize.ShouldBeLessThanOrEqualTo(4, what);
        sps.ConformanceWindow.ShouldBe((0, 0, 0, 0), what);

        pps.DeblockingDisabled.ShouldBeTrue(what);
        pps.DeblockingOverrideEnabled.ShouldBeFalse(what);
        pps.Tiles.ShouldBeFalse(what);
        pps.EntropyCodingSync.ShouldBeFalse(what);
        pps.DependentSliceSegments.ShouldBeFalse(what);
        pps.TransformSkip.ShouldBeFalse(what);
        pps.TransquantBypass.ShouldBeFalse(what);
        pps.SignDataHiding.ShouldBeFalse(what);
        pps.CrossComponentPrediction.ShouldBeFalse(what);
        pps.ChromaQpOffsetList.ShouldBeFalse(what);
        pps.OtherExtensions.ShouldBeFalse(what);

        survey.NalTypeCounts.Keys.ShouldBe([19], what); // IDR_W_RADL slices only: no AUD, no SEI, no parameter sets in the data
        survey.Slices.ShouldAllBe(s => s.SliceType == 2 && !s.Dependent, what);
        survey.Slices[0].FirstInPicture.ShouldBeTrue(what);
        survey.Slices.Skip(1).ShouldAllBe(s => !s.FirstInPicture, what);
        survey.Sei.ShouldBeEmpty(what); // in particular, no decoded-picture-hash SEI to check a decode against
    }
}

/// <summary>
/// The HEVC pictures Canon puts in a CR3 when HDR PQ is on: the <c>THMB</c> thumbnail in the
/// <c>moov</c> Canon uuid box and the <c>PRVW</c> preview in its own uuid box. Each holds a small header
/// (width and height), then boxes: <c>CISZ</c> (coded size), <c>hvcC</c>, <c>colr</c>, <c>pixi</c> and
/// <c>IMGD</c>, whose body is a 32-bit byte count followed by the 4-byte length-prefixed NAL units.
/// Layout established by dumping the R6 file; used only by tests, to get small real HEVC pictures.
/// </summary>
internal static class Cr3HevcPreviews
{
    internal sealed record Preview(string Name, int Width, int Height, byte[] HvcC, byte[] NalUnits, byte[]? Colr, byte[]? Pixi);

    private static readonly byte[] CanonMoovUuid = Convert.FromHexString("85c0b687820f11e08111f4ce462b6a48");
    private static readonly byte[] CanonPreviewUuid = Convert.FromHexString("eaf42b5e1c984b88b9fbb7dc406e4d16");

    public static List<Preview> Find(byte[] file)
    {
        var found = new List<Preview>();
        foreach (var (type, start, end) in Boxes(file, 0, file.Length))
        {
            if (type == "moov")
            {
                foreach (var inner in Boxes(file, start + 8, end))
                    if (inner.Type == "uuid" && file.AsSpan(inner.Start + 8, 16).SequenceEqual(CanonMoovUuid))
                        foreach (var canon in Boxes(file, inner.Start + 24, inner.End))
                            if (canon.Type == "THMB") found.Add(Read("THMB", file, canon.Start, canon.End));
            }
            else if (type == "uuid" && file.AsSpan(start + 8, 16).SequenceEqual(CanonPreviewUuid))
            {
                // The preview uuid box holds 8 bytes of its own before the PRVW box.
                foreach (var inner in Boxes(file, start + 32, end))
                    if (inner.Type == "PRVW") found.Add(Read("PRVW", file, inner.Start, inner.End));
            }
        }
        return found;
    }

    private static Preview Read(string name, byte[] file, int start, int end)
    {
        // 8-byte box header, then version/flags (4), a 16-bit field, width, height, 0xFFFF, a 32-bit size.
        var width = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(start + 14));
        var height = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(start + 16));
        byte[]? hvcC = null, nals = null, colr = null, pixi = null;
        foreach (var (type, s, e) in Boxes(file, start + 24, end))
        {
            var body = file.AsSpan(s + 8, e - s - 8);
            switch (type)
            {
                case "hvcC": hvcC = body.ToArray(); break;
                case "colr": colr = body.ToArray(); break;
                case "pixi": pixi = body.ToArray(); break;
                case "IMGD": nals = body[4..].ToArray(); break;
            }
        }
        return new Preview(name, width, height,
            hvcC ?? throw new InvalidDataException($"{name} has no hvcC"),
            nals ?? throw new InvalidDataException($"{name} has no IMGD"), colr, pixi);
    }

    private static IEnumerable<(string Type, int Start, int End)> Boxes(byte[] file, int start, int end)
    {
        var position = start;
        while (end - position >= 8)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            var type = System.Text.Encoding.Latin1.GetString(file, position + 4, 4);
            if (size == 1) size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(position + 8));
            if (size < 8 || size > end - position) yield break;
            yield return (type, position, position + (int)size);
            position += (int)size;
        }
    }
}
