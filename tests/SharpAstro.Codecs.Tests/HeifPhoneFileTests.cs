using System.Buffers.Binary;
using ImageMagick;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The HEIF reader against real phone HEICs (issue #73; the gain-map ones are what #70 wants too): two
/// iPhone 13 Pro files, two Samsung Galaxy files and a Xiaomi file. <c>fetch-heif-external.sh</c> puts them in
/// <c>Fixtures/heif-external/</c> (they are not committed) and every test here skips, visibly, when its file is
/// absent. Magick.NET (libheif) is the reference for the primary image's size and depth.
/// <para>
/// What the files show, against the Canon files the decoder was first built on: every phone picture is 8-bit
/// 4:2:0 HEVC in 512x512 tiles of a <c>grid</c> (the Canon R7 is 10-bit 4:2:2 in 1792x960 tiles), in the Main or
/// Main Still Picture profile, with SAO on, and with an ICC profile (Apple 536 bytes, Samsung 632) where Canon
/// has <c>nclx</c>. The grids overhang their output (4032x3024 is 8x6 tiles = 4096x3072), which is the case
/// <c>CheckCodedSize</c>'s grid bound exists for. The auxiliary items (<c>auxl</c> reference, <c>auxC</c> URN)
/// are listed so #70 can rely on them: Apple's and Samsung's gain maps, Apple's depth map, and Samsung's
/// ISO 21496-1 <c>tmap</c> item.
/// </para>
/// </summary>
public class HeifPhoneFileTests(ITestOutputHelper output)
{
    private const string AppleGainMapUrn = "urn:com:apple:photo:2020:aux:hdrgainmap";
    private const string SamsungGainMapUrn = "urn:com:samsung:photo:2024:aux:hdrgainmap";
    private const string HevcDepthUrn = "urn:mpeg:hevc:2015:auxid:2";

    /// <summary>What one phone file's primary image is, as pinned here.</summary>
    private sealed record Phone(
        string Name, string Device,
        string[] CompatibleBrands, uint PrimaryId, uint Width, uint Height, int Rows, int Columns,
        int IccLength, int Rotation,
        int ProfileIdc, int NalType, bool Wpp, bool SignHiding, bool ScalingList, bool CuQpDelta, bool DeblockingOverride)
    {
        public override string ToString() => Name;
    }

    private static readonly Phone[] Phones =
    [
        new("apple-iphone13pro-hdr-sample.heic", "iPhone 13 Pro", ["mif1", "MiHE", "miaf", "MiHB", "heic"], 7, 1512, 850, 2, 3,
            536, 0, ProfileIdc: 3, NalType: 20, Wpp: true, SignHiding: false, ScalingList: true, CuQpDelta: true, DeblockingOverride: false),
        new("apple-iphone13pro-portrait-pug.heic", "iPhone 13 Pro", ["mif1", "MiHE", "miaf", "MiHB", "heic"], 49, 4032, 3024, 6, 8,
            536, 0, ProfileIdc: 3, NalType: 20, Wpp: true, SignHiding: false, ScalingList: true, CuQpDelta: true, DeblockingOverride: false),
        new("samsung-200mp-hdrgainmap.heic", "Samsung Galaxy, 200 MP", ["mif1", "heic"], 769, 16320, 12240, 24, 32,
            632, 270, ProfileIdc: 1, NalType: 19, Wpp: false, SignHiding: false, ScalingList: false, CuQpDelta: false, DeblockingOverride: true),
        new("samsung-tmap-aux-ycbcr.heic", "Samsung Galaxy, tmap", ["mif1", "heic", "tmap"], 33, 4000, 1848, 4, 8,
            632, 0, ProfileIdc: 3, NalType: 20, Wpp: false, SignHiding: true, ScalingList: false, CuQpDelta: true, DeblockingOverride: true),
        new("xiaomi-android.heic", "Xiaomi", ["mif1", "heic"], 10024, 2592, 1944, 4, 6,
            0, 0, ProfileIdc: 3, NalType: 19, Wpp: false, SignHiding: false, ScalingList: false, CuQpDelta: true, DeblockingOverride: true),
    ];

    public static TheoryData<string> Names => new(Phones.Select(p => p.Name));

    private static Phone Pinned(string name) => Phones.Single(p => p.Name == name);

    [Theory]
    [MemberData(nameof(Names))]
    public void IsHeif_AndBrandsAreWhatThePhoneWrote(string name)
    {
        var phone = Pinned(name);
        var file = HeifCanonFileTests.Load(name);
        HeifReader.IsHeif(file.AsSpan(0, 64)).ShouldBeTrue();

        var heif = HeifReader.Read(file);
        heif.MajorBrand.ShouldBe("heic");
        heif.CompatibleBrands.ShouldBe(phone.CompatibleBrands);
        heif.PrimaryItemId.ShouldBe(phone.PrimaryId);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Primary_IsAGridOfEightBit420Tiles(string name)
    {
        var phone = Pinned(name);
        var file = HeifCanonFileTests.Load(name);
        var heif = HeifReader.Read(file);
        heif.PrimaryItem.Type.ShouldBe("grid");

        var image = heif.DescribePrimary(file);
        (image.Width, image.Height).ShouldBe((phone.Width, phone.Height));
        image.Grid.ShouldNotBeNull();
        (image.Grid.Rows, image.Grid.Columns).ShouldBe((phone.Rows, phone.Columns));
        image.Grid.TileItemIds.Count.ShouldBe(phone.Rows * phone.Columns);
        (image.TileWidth, image.TileHeight).ShouldBe((512u, 512u));
        image.BitDepth.ShouldBe(8);
        image.ChromaFormat.ShouldBe(HevcChromaFormat.Yuv420);

        // No phone file here signals nclx on the picture: Apple and Samsung give an ICC profile, Xiaomi nothing.
        image.Nclx.ShouldBeNull();
        if (phone.IccLength == 0) image.Icc.ShouldBeNull();
        else
        {
            image.Icc.ShouldNotBeNull();
            image.Icc.Profile.Length.ShouldBe(phone.IccLength);
            image.Icc.Restricted.ShouldBeFalse();
        }

        image.Rotation.ShouldBe(phone.Rotation);
        image.Mirror.ShouldBeNull();
        image.CleanAperture.ShouldBeNull();
        image.Grid.TileItemIds.ShouldAllBe(id => heif.GetItem(id)!.Hidden);

        // The grid overhangs its output by less than a tile each way, never more.
        (image.Grid.Columns * 512).ShouldBeGreaterThanOrEqualTo((int)phone.Width);
        ((image.Grid.Columns - 1) * 512).ShouldBeLessThan((int)phone.Width);
        (image.Grid.Rows * 512).ShouldBeGreaterThanOrEqualTo((int)phone.Height);
        ((image.Grid.Rows - 1) * 512).ShouldBeLessThan((int)phone.Height);
    }

    /// <summary>
    /// Size and depth against libheif. libheif applies <c>irot</c>, which this reader reports separately, so the
    /// Samsung 200 MP file (rotated 270) comes back from Magick with its sides swapped.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Primary_SizeAndDepthAgreeWithMagick(string name)
    {
        var phone = Pinned(name);
        var file = HeifCanonFileTests.Load(name);
        var image = HeifReader.Read(file).DescribePrimary(file);

        using var magick = new MagickImage(file, MagickFormat.Heic);
        var swapped = phone.Rotation is 90 or 270;
        ((uint)magick.Width, (uint)magick.Height).ShouldBe(swapped ? (image.Height, image.Width) : (image.Width, image.Height));
        ((int)magick.Depth).ShouldBe(image.BitDepth);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Primary_FirstTileIsInsideThePhoneEnvelope(string name)
    {
        var phone = Pinned(name);
        var file = HeifCanonFileTests.Load(name);
        var heif = HeifReader.Read(file);
        var image = heif.DescribePrimary(file);
        var tile = heif.GetItem(image.Grid!.TileItemIds[0])!;

        var survey = HevcSurvey.Of(image.Coding, heif.ReadItemData(file, tile));
        output.WriteLine($"{name} ({phone.Device}): first tile of {image.Grid.TileItemIds.Count}");
        output.WriteLine(survey.Report());

        var sps = survey.Sps;
        var pps = survey.Pps;
        (survey.Sps.Width, survey.Sps.Height).ShouldBe((512, 512));
        sps.ProfileTierLevel.ProfileIdc.ShouldBe(phone.ProfileIdc);
        sps.ChromaFormatIdc.ShouldBe(1);
        (sps.BitDepthLuma, sps.BitDepthChroma).ShouldBe((8, 8));
        sps.SeparateColourPlanes.ShouldBeFalse();
        sps.RangeExtension.Any.ShouldBeFalse();
        sps.OtherExtensions.ShouldBeFalse();
        sps.ConformanceWindow.ShouldBe((0, 0, 0, 0));
        sps.SampleAdaptiveOffset.ShouldBeTrue();
        sps.PcmEnabled.ShouldBeFalse();
        sps.StrongIntraSmoothing.ShouldBeFalse();
        sps.ScalingListEnabled.ShouldBe(phone.ScalingList);

        pps.Tiles.ShouldBeFalse();
        pps.EntropyCodingSync.ShouldBe(phone.Wpp);
        pps.SignDataHiding.ShouldBe(phone.SignHiding);
        pps.CuQpDeltaEnabled.ShouldBe(phone.CuQpDelta);
        pps.DeblockingDisabled.ShouldBeFalse();
        pps.DeblockingOverrideEnabled.ShouldBe(phone.DeblockingOverride);
        pps.DependentSliceSegments.ShouldBeFalse();
        pps.TransformSkip.ShouldBeFalse();
        pps.TransquantBypass.ShouldBeFalse();
        pps.CrossComponentPrediction.ShouldBeFalse();
        pps.ChromaQpOffsetList.ShouldBeFalse();
        pps.OtherExtensions.ShouldBeFalse();

        survey.NalTypeCounts.Keys.ShouldBe([phone.NalType]);
        survey.Slices.Count.ShouldBe(1);
        survey.Slices[0].SliceType.ShouldBe(2);
        survey.Slices[0].FirstInPicture.ShouldBeTrue();
        survey.Sei.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Metadata_IsExifAndUsuallyXmp(string name)
    {
        var file = HeifCanonFileTests.Load(name);
        var heif = HeifReader.Read(file);

        var metadata = heif.MetadataOf(heif.PrimaryItem);
        metadata.Select(m => m.Type).ShouldBe(name.StartsWith("xiaomi") ? ["Exif"] : ["Exif", "mime"]);

        var exif = heif.ReadItemData(file, metadata[0]);
        var offset = BinaryPrimitives.ReadUInt32BigEndian(exif);
        Convert.ToHexString(exif.AsSpan(4 + (int)offset, 4)).ShouldBeOneOf("49492A00", "4D4D002A");
        output.WriteLine($"{name}: Exif item {metadata[0].Id}, {exif.Length} bytes, hidden {metadata[0].Hidden}");

        if (metadata.Count > 1)
        {
            metadata[1].ContentType.ShouldBe("application/rdf+xml");
            System.Text.Encoding.UTF8.GetString(heif.ReadItemData(file, metadata[1])).ShouldContain("x:xmpmeta");
        }
    }

    [Fact]
    public void Thumbnails_OnlySamsungWritesThem()
    {
        foreach (var phone in Phones)
        {
            var file = HeifCanonFileTests.Load(phone.Name);
            var heif = HeifReader.Read(file);
            var thumbnails = heif.ThumbnailsOf(heif.PrimaryItem).Select(t => heif.Describe(file, t)).ToList();
            switch (phone.Name)
            {
                case "samsung-200mp-hdrgainmap.heic":
                    // Written with the primary's own irot, so it is displayed upright with it.
                    thumbnails.Select(t => (t.Width, t.Height, t.Rotation)).ShouldBe([(512u, 384u, 270)]);
                    break;
                case "samsung-tmap-aux-ycbcr.heic":
                    thumbnails.Select(t => (t.Width, t.Height, t.Rotation)).ShouldBe([(512u, 236u, 0)]);
                    break;
                default:
                    thumbnails.ShouldBeEmpty(phone.Name);
                    continue;
            }
            thumbnails.ShouldAllBe(t => t.Grid == null && t.ChromaFormat == HevcChromaFormat.Yuv420 && t.BitDepth == 8);
            thumbnails.ShouldAllBe(t => t.Icc != null && t.Icc.Profile.Length == phone.IccLength);
        }
    }

    // ---- Auxiliary and derived items: what sits beside the primary -----------------------------------

    /// <summary>An auxiliary image as the file lists it: an <c>auxl</c> reference to its master and an <c>auxC</c> URN.</summary>
    private sealed record Aux(uint Id, string Type, string Urn, uint Master, uint Width, uint Height, HevcChromaFormat Chroma, bool Hidden);

    private static List<Aux> AuxiliariesOf(HeifFile heif, byte[] file)
    {
        var found = new List<Aux>();
        foreach (var item in heif.Items)
        {
            var urn = item.Property<HeifAuxiliaryType>();
            if (urn is null) continue;
            var image = heif.Describe(file, item);
            item.ReferencesOfType("auxl").Count.ShouldBe(1, $"{item} is an auxiliary image of exactly one master");
            found.Add(new Aux(item.Id, item.Type, urn.Urn, item.ReferencesOfType("auxl")[0], image.Width, image.Height, image.ChromaFormat, item.Hidden));
        }
        return found;
    }

    [Fact]
    public void Apple_HdrSample_HasAGainMapAsMonochromeHevc()
    {
        var file = HeifCanonFileTests.Load("apple-iphone13pro-hdr-sample.heic");
        var heif = HeifReader.Read(file);

        // The gain map is one 4:0:0 HEVC picture at half the primary's size (756x426), with a clean aperture of
        // 756x425 that the primary does not have: the gain map is cropped by one row, the picture is not.
        AuxiliariesOf(heif, file).ShouldBe([new Aux(10, "hvc1", AppleGainMapUrn, 7, 756, 426, HevcChromaFormat.Monochrome, true)]);

        var gainMap = heif.Describe(file, heif.GetItem(10)!);
        gainMap.BitDepth.ShouldBe(8);
        gainMap.Nclx.ShouldBeNull();
        gainMap.Icc.ShouldBeNull();
        gainMap.Rotation.ShouldBe(0);
        gainMap.CleanAperture.ShouldNotBeNull();
        (gainMap.CleanAperture.WidthN, gainMap.CleanAperture.WidthD, gainMap.CleanAperture.HeightN, gainMap.CleanAperture.HeightD)
            .ShouldBe((756u, 1u, 425u, 1u));
        // 23008-12 allows the offset as a fraction: -4194304/8388608 is exactly -1/2, so the crop is centred on a half row.
        (gainMap.CleanAperture.VerticalOffsetN, gainMap.CleanAperture.VerticalOffsetD).ShouldBe((-4194304, 8388608u));

        // The gain map has its own XMP (Apple's headroom and map metadata); the primary has Exif and XMP.
        var maps = heif.MetadataOf(gainMap.Item);
        maps.Select(m => (m.Type, m.ContentType)).ShouldBe([("mime", "application/rdf+xml")]);
        System.Text.Encoding.UTF8.GetString(heif.ReadItemData(file, maps[0])).ShouldContain("x:xmpmeta");

        var survey = HevcSurvey.Of(gainMap.Coding, heif.ReadItemData(file, gainMap.Item));
        output.WriteLine("apple gain map:");
        output.WriteLine(survey.Report());
        survey.Sps.ChromaFormatIdc.ShouldBe(0);
        (survey.Sps.BitDepthLuma, survey.Sps.Width, survey.Sps.Height).ShouldBe((8, 768, 448)); // coded at 32-aligned size, shown as 756x426
    }

    [Fact]
    public void Apple_Portrait_HasAGainMapAndADepthMap()
    {
        var file = HeifCanonFileTests.Load("apple-iphone13pro-portrait-pug.heic");
        var heif = HeifReader.Read(file);

        AuxiliariesOf(heif, file).ShouldBe(
        [
            new Aux(52, "hvc1", AppleGainMapUrn, 49, 2016, 1512, HevcChromaFormat.Monochrome, true),
            new Aux(54, "hvc1", HevcDepthUrn, 49, 768, 576, HevcChromaFormat.Monochrome, true),
        ]);

        // The depth map is MPEG's auxid 2 (depth), and its auxC subtype carries Apple's own depth parameters.
        var depth = heif.GetItem(54)!.Property<HeifAuxiliaryType>()!;
        Convert.ToHexString(depth.Subtype).ShouldBe("000000110000000D4E01B109351F7BFC010BC62020");

        // Each auxiliary image has its own XMP item (cdsc to it), as the gain map does in the HDR sample.
        heif.MetadataOf(heif.GetItem(52)!).Select(m => m.Type).ShouldBe(["mime"]);
        heif.MetadataOf(heif.GetItem(54)!).Select(m => m.Type).ShouldBe(["mime"]);
    }

    [Fact]
    public void Samsung_200Mp_HasAGainMapAsAGrid()
    {
        var file = HeifCanonFileTests.Load("samsung-200mp-hdrgainmap.heic");
        var heif = HeifReader.Read(file);

        // Samsung's gain map is a grid of its own (48 tiles), a quarter of the primary's size in each direction,
        // coded 4:2:0 (Apple's is 4:0:0), with no irot: it is stored in the sensor's orientation,
        // the primary rotates 270 degrees to show upright.
        AuxiliariesOf(heif, file).ShouldBe([new Aux(819, "grid", SamsungGainMapUrn, 769, 4080, 3060, HevcChromaFormat.Yuv420, true)]);
        var gainMap = heif.Describe(file, heif.GetItem(819)!);
        (gainMap.Grid!.Rows, gainMap.Grid.Columns).ShouldBe((6, 8));
        gainMap.Grid.TileItemIds.Count.ShouldBe(48);
        (gainMap.TileWidth, gainMap.TileHeight).ShouldBe((512u, 512u));
        gainMap.BitDepth.ShouldBe(8);
        gainMap.Icc.ShouldBeNull();
        gainMap.Rotation.ShouldBe(0);

        // Its tiles carry no ICC profile (the primary's do), and the gain map has its own XMP.
        heif.MetadataOf(gainMap.Item).Select(m => m.Type).ShouldBe(["mime"]);

        // The envelope this pushes at: 24x32 tiles code 16384x12288 samples (201 MP) for a 199.8 MP image.
        var primary = heif.DescribePrimary(file);
        var coded = (long)primary.Grid!.Columns * primary.TileWidth * primary.Grid.Rows * primary.TileHeight;
        coded.ShouldBe(16384L * 12288);
        coded.ShouldBeLessThanOrEqualTo(HeifLimits.MaxImagePixels);
    }

    [Fact]
    public void Samsung_Tmap_HasAnIso21496TmapItemBesideAGridPrimary()
    {
        var file = HeifCanonFileTests.Load("samsung-tmap-aux-ycbcr.heic");
        var heif = HeifReader.Read(file);

        // The 'tmap' brand is declared, and a 'tmap' item exists (id 38), but the PRIMARY item is the plain grid
        // (id 33): a reader that only understands the primary shows the SDR base, which is what this one does.
        heif.CompatibleBrands.ShouldContain("tmap");
        heif.PrimaryItem.Type.ShouldBe("grid");
        var tmap = heif.Items.Single(i => i.Type == "tmap");
        tmap.Id.ShouldBe(38u);
        tmap.Hidden.ShouldBeFalse();

        // Inputs, in order: the base image, then the gain map (23008-12 / ISO 21496-1: 'dimg' lists the base first).
        tmap.ReferencesOfType("dimg").ShouldBe([33u, 37u]);
        heif.GetItem(33)!.Type.ShouldBe("grid");
        heif.GetItem(37)!.Type.ShouldBe("grid");
        (tmap.Property<HeifImageSize>()!.Width, tmap.Property<HeifImageSize>()!.Height).ShouldBe((4000u, 1848u));
        tmap.Property<HeifIccColour>()!.Profile.Length.ShouldBe(632);
        tmap.Property<HeifPixelInformation>()!.BitsPerChannel.ShouldBe([(byte)8, 8, 8]);
        heif.MetadataOf(tmap).ShouldBeEmpty();

        // The gain map is both an auxiliary image of the base and the tmap's second input: a 1000x462 grid of two
        // tiles, which is a quarter of the base each way, and it carries nclx where the base has ICC.
        AuxiliariesOf(heif, file).ShouldBe([new Aux(37, "grid", SamsungGainMapUrn, 33, 1000, 462, HevcChromaFormat.Yuv420, true)]);
        var gainMap = heif.Describe(file, heif.GetItem(37)!);
        (gainMap.Grid!.Rows, gainMap.Grid.Columns).ShouldBe((1, 2));
        gainMap.Nclx.ShouldBe(new HeifNclxColour(ColorPrimaries.Unspecified, TransferFunction.Unspecified, MatrixCoefficients.Unspecified, FullRange: true));
        heif.MetadataOf(gainMap.Item).Select(m => m.Type).ShouldBe(["mime"]);

        // The reader does not describe a 'tmap' item, and says so by name: the refusal #70 lifts.
        var refusal = Should.Throw<NotSupportedException>(() => heif.Describe(file, tmap));
        refusal.Message.ShouldBe("Item 38 is a 'tmap' item; only 'hvc1' and 'grid' images are described.");
        // The tmap's own data (the ISO 21496-1 metadata) is stored in the file and readable as bytes.
        heif.ReadItemData(file, tmap).Length.ShouldBeGreaterThan(0);
        output.WriteLine($"tmap item data: {Convert.ToHexString(heif.ReadItemData(file, tmap))}");
    }

    [Fact]
    public void Xiaomi_HasNoAuxiliaryItemsAndNoColourSignalling()
    {
        var file = HeifCanonFileTests.Load("xiaomi-android.heic");
        var heif = HeifReader.Read(file);

        AuxiliariesOf(heif, file).ShouldBeEmpty();
        heif.Items.Select(i => i.Type).Distinct().Order().ShouldBe(["Exif", "grid", "hvc1"]);
        heif.Items.Where(i => i.Type == "hvc1").ShouldAllBe(i => i.Hidden);

        // Neither nclx nor ICC anywhere (not on the grid, not on a tile), and the Exif item is not hidden.
        heif.Items.SelectMany(i => i.Properties).Select(p => p.Property).OfType<HeifIccColour>().ShouldBeEmpty();
        heif.Items.SelectMany(i => i.Properties).Select(p => p.Property).OfType<HeifNclxColour>().ShouldBeEmpty();
        heif.MetadataOf(heif.PrimaryItem).Single().Hidden.ShouldBeFalse();

        // pillow_heif keeps this file for its Exif block, which is over 64 KiB (a JPEG APP1 cannot hold that).
        var exif = heif.ReadItemData(file, heif.MetadataOf(heif.PrimaryItem).Single());
        exif.Length.ShouldBeGreaterThan(65535);
    }
}
