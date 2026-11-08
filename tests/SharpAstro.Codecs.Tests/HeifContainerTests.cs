using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The HEIF container reader (ISO/IEC 23008-12 over 14496-12) against files built box by box with
/// <see cref="HeifBuilder"/>: the item structure, every <c>iloc</c> / <c>iref</c> / <c>ipma</c> field-width
/// variant, grid resolution, refusal of malformed structure, the declared-size limits, and a truncation
/// and byte-flip sweep in which nothing but <see cref="InvalidDataException"/> or
/// <see cref="NotSupportedException"/> may escape.
/// </summary>
public class HeifContainerTests
{
    private static readonly byte[] Hvcc10Bit422 = HeifBuilder.HvcC(chromaFormat: 2, bitDepthLuma: 10, bitDepthChroma: 10, profileIdc: 4);

    /// <summary>
    /// A file shaped like Canon's: a 2x3 grid of hidden 64x32 tiles cropped to 180x60, a thumbnail, Exif
    /// and XMP, with the grid layout in <c>idat</c>.
    /// </summary>
    internal static HeifBuilder CanonShaped(bool wideGrid = false)
    {
        var b = new HeifBuilder { CompatibleBrands = ["mif1", "heix", "miaf"], MajorBrand = "heix" };
        b.AddItem(new HeifBuilder.Item(1, "grid", HeifBuilder.GridLayout(2, 3, 180, 60, wideGrid), InIdat: true));
        for (uint t = 0; t < 6; t++)
            b.AddItem(new HeifBuilder.Item(10 + t, "hvc1", [0, 0, 0, 2, (byte)(t * 2), 1], Hidden: true));
        b.AddItem(new HeifBuilder.Item(20, "hvc1", [0, 0, 0, 2, 0x26, 1]));
        b.AddItem(new HeifBuilder.Item(30, "Exif", [0, 0, 0, 0, 0x4D, 0x4D, 0, 0x2A], Hidden: true));
        b.AddItem(new HeifBuilder.Item(31, "mime", "<x:xmpmeta/>"u8.ToArray(), Hidden: true, ContentType: "application/rdf+xml"));
        b.Reference("dimg", 1, 10, 11, 12, 13, 14, 15);
        b.Reference("thmb", 20, 1);
        b.Reference("cdsc", 30, 1);
        b.Reference("cdsc", 31, 1);

        var hvcc = b.Property("hvcC", Hvcc10Bit422);
        var tileSize = b.Property("ispe", HeifBuilder.Ispe(64, 32));
        var nclx = b.Property("colr", HeifBuilder.Nclx(9, 16, 9, true));
        for (uint t = 0; t < 6; t++) b.Associate(10 + t, hvcc, essential: true).Associate(10 + t, tileSize).Associate(10 + t, nclx);
        b.Associate(1, "pixi", HeifBuilder.Pixi(10, 10, 10));
        b.Associate(1, "ispe", HeifBuilder.Ispe(180, 60));
        b.Associate(1, "irot", HeifBuilder.Irot(90), essential: true);
        b.Associate(1, "imir", HeifBuilder.Imir(1), essential: true);
        b.Associate(20, hvcc, essential: true).Associate(20, "ispe", HeifBuilder.Ispe(48, 16)).Associate(20, nclx);
        return b;
    }

    [Fact]
    public void ItemsReferencesAndProperties_AreRead()
    {
        var file = CanonShaped().Build();
        HeifReader.IsHeif(file).ShouldBeTrue();
        var heif = HeifReader.Read(file);

        heif.MajorBrand.ShouldBe("heix");
        heif.CompatibleBrands.ShouldBe(["mif1", "heix", "miaf"]);
        heif.PrimaryItemId.ShouldBe(1u);
        heif.Items.Select(i => i.Id).ShouldBe([1u, 10, 11, 12, 13, 14, 15, 20, 30, 31]);
        heif.GetItem(10)!.Hidden.ShouldBeTrue();
        heif.GetItem(20)!.Hidden.ShouldBeFalse();
        heif.PrimaryItem.ReferencesOfType("dimg").ShouldBe([10u, 11, 12, 13, 14, 15]);
        heif.ThumbnailsOf(heif.PrimaryItem).Select(i => i.Id).ShouldBe([20u]);
        heif.MetadataOf(heif.PrimaryItem).Select(i => i.Id).ShouldBe([30u, 31]);
        heif.GetItem(31)!.ContentType.ShouldBe("application/rdf+xml");
        heif.ReadItemData(file, heif.GetItem(31)!).ShouldBe("<x:xmpmeta/>"u8.ToArray());

        var tile = heif.GetItem(10)!;
        tile.Properties.Select(p => (p.Property.Type, p.Essential)).ShouldBe([("hvcC", true), ("ispe", false), ("colr", false)]);
        tile.Property<HevcDecoderConfiguration>()!.ChromaFormat.ShouldBe(HevcChromaFormat.Yuv422);
    }

    [Fact]
    public void AGrid_IsDescribedWithItsTilesColourAndOrientation()
    {
        var file = CanonShaped().Build();
        var image = HeifReader.Read(file).DescribePrimary(file);

        (image.Width, image.Height).ShouldBe((180u, 60u));
        image.Grid.ShouldBe(new HeifGrid(2, 3, 180, 60, [10u, 11, 12, 13, 14, 15]), new GridComparer());
        (image.TileWidth, image.TileHeight).ShouldBe((64u, 32u));
        image.BitDepth.ShouldBe(10);
        image.ChromaFormat.ShouldBe(HevcChromaFormat.Yuv422);
        image.Nclx.ShouldBe(new HeifNclxColour(ColorPrimaries.BT2020, TransferFunction.Pq, MatrixCoefficients.BT2020NCL, true));
        image.Rotation.ShouldBe(90);
        image.Mirror.ShouldBe(HeifMirrorAxis.Horizontal);
    }

    [Fact]
    public void AWideGridLayout_IsRead() =>
        HeifReader.Read(CanonShaped(wideGrid: true).Build()).DescribePrimary(CanonShaped(wideGrid: true).Build()).Width.ShouldBe(180u);

    [Fact]
    public void AThumbnail_IsDescribedOnItsOwn()
    {
        var file = CanonShaped().Build();
        var heif = HeifReader.Read(file);
        var thumbnail = heif.Describe(file, heif.GetItem(20)!);
        (thumbnail.Width, thumbnail.Height, thumbnail.Grid).ShouldBe((48u, 16u, null));
        thumbnail.BitDepth.ShouldBe(10); // from hvcC: no pixi on this one
    }

    [Theory]
    [InlineData(0, 0, false, 0)]
    [InlineData(1, 0, false, 0)]
    [InlineData(2, 1, true, 1)]
    [InlineData(1, 1, true, 0)]
    [InlineData(2, 0, false, 1)]
    public void EveryFieldWidthVariant_ReadsTheSameStructure(int ilocVersion, int irefVersion, bool ipmaWide, int ipmaVersion)
    {
        var builder = CanonShaped();
        builder.IlocVersion = ilocVersion;
        builder.IrefVersion = irefVersion;
        builder.IpmaWideIndices = ipmaWide;
        builder.IpmaVersion = ipmaVersion;

        if (ilocVersion == 0)
        {
            // Version 0 has no construction method, so the grid layout cannot live in idat.
            builder = Rebuild(builder, inIdat: false);
            builder.IlocVersion = 0;
            builder.IrefVersion = irefVersion;
            builder.IpmaWideIndices = ipmaWide;
            builder.IpmaVersion = ipmaVersion;
        }

        var file = builder.Build();
        var image = HeifReader.Read(file).DescribePrimary(file);
        (image.Width, image.Height, image.Grid!.Rows, image.Grid.Columns).ShouldBe((180u, 60u, 2, 3));
        image.Grid.TileItemIds.ShouldBe([10u, 11, 12, 13, 14, 15]);
    }

    /// <summary>
    /// Item data located through a base offset, through several extents, or both (14496-12 8.11.3):
    /// every item must read back as written, and the grid's layout, split across two <c>idat</c>
    /// extents, must still be parsed whole. Canon writes neither form, so only this test sees them.
    /// </summary>
    [Theory]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, true, true)]
    public void ItemData_ThroughBaseOffsetsAndSeveralExtents_ReadsBackWhole(int ilocVersion, bool baseOffsets, bool twoExtents)
    {
        var builder = CanonShaped();
        builder.IlocVersion = ilocVersion;
        builder.IlocBaseOffsets = baseOffsets;
        builder.IlocTwoExtents = twoExtents;
        var file = builder.Build();
        var heif = HeifReader.Read(file);

        heif.ReadItemData(file, heif.GetItem(31)!).ShouldBe("<x:xmpmeta/>"u8.ToArray());
        heif.ReadItemData(file, heif.GetItem(30)!).ShouldBe(new byte[] { 0, 0, 0, 0, 0x4D, 0x4D, 0, 0x2A });
        heif.ReadItemData(file, heif.GetItem(12)!).ShouldBe(new byte[] { 0, 0, 0, 2, 4, 1 });
        heif.ReadItemData(file, heif.GetItem(1)!).ShouldBe(HeifBuilder.GridLayout(2, 3, 180, 60));
        var image = heif.DescribePrimary(file);
        (image.Width, image.Height, image.Grid!.Rows, image.Grid.Columns).ShouldBe((180u, 60u, 2, 3));
    }

    private static HeifBuilder Rebuild(HeifBuilder _, bool inIdat)
    {
        var b = CanonShaped();
        var fresh = new HeifBuilder { CompatibleBrands = b.CompatibleBrands, MajorBrand = b.MajorBrand };
        // Same structure with the grid layout in mdat.
        fresh.AddItem(new HeifBuilder.Item(1, "grid", HeifBuilder.GridLayout(2, 3, 180, 60), InIdat: inIdat));
        for (uint t = 0; t < 6; t++) fresh.AddItem(new HeifBuilder.Item(10 + t, "hvc1", [0, 0, 0, 2, 0x26, 1], Hidden: true));
        fresh.Reference("dimg", 1, 10, 11, 12, 13, 14, 15);
        var hvcc = fresh.Property("hvcC", Hvcc10Bit422);
        var size = fresh.Property("ispe", HeifBuilder.Ispe(64, 32));
        for (uint t = 0; t < 6; t++) fresh.Associate(10 + t, hvcc, true).Associate(10 + t, size);
        fresh.Associate(1, "ispe", HeifBuilder.Ispe(180, 60));
        return fresh;
    }

    [Theory]
    [InlineData("heic", new string[0], true)]
    [InlineData("mif1", new[] { "heic" }, true)]
    [InlineData("mif1", new[] { "miaf", "heix" }, true)]
    [InlineData("avif", new[] { "mif1", "miaf" }, false)] // AVIF lists mif1 too
    [InlineData("mif1", new string[0], false)]
    [InlineData("crx ", new[] { "isom" }, false)]          // Canon CR3
    public void IsHeif_ClaimsHevcBrandsOnly(string major, string[] compatible, bool expected)
    {
        var file = new HeifBuilder { MajorBrand = major, CompatibleBrands = compatible }
            .AddItem(new HeifBuilder.Item(1, "hvc1", [1])).Build();
        HeifReader.IsHeif(file).ShouldBe(expected);
    }

    [Fact]
    public void IsHeif_AnswersFalseForShortOrForeignHeaders()
    {
        var file = CanonShaped().Build();
        HeifReader.IsHeif(file.AsSpan(0, 15)).ShouldBeFalse();
        HeifReader.IsHeif(file.AsSpan(0, 20)).ShouldBeFalse(); // cut inside the ftyp box
        HeifReader.IsHeif("\x89PNG\r\n\x1a\n........"u8).ShouldBeFalse();
    }

    // ---- malformed structure -----------------------------------------------------------------------

    public static TheoryData<string, Func<byte[]>> Malformed => new()
    {
        { "no meta box", () => HeifBuilder.Box("ftyp", HeifBuilder.Concat(HeifBuilder.Ascii("heic"), new byte[4])) },
        { "handler is not pict", () => { var b = CanonShaped(); b.Handler = "vide"; return b.Build(); } },
        { "duplicate item ID", () => CanonShaped().AddItem(new HeifBuilder.Item(10, "hvc1", [1])).Build() },
        { "iinf count disagrees", () => { var b = CanonShaped(); b.DeclaredItemCount = 3; return b.Build(); } },
        { "primary item undeclared", () => { var b = CanonShaped(); b.PrimaryItemId = 99; return b.Build(); } },
        { "reference to an undeclared item", () => CanonShaped().Reference("thmb", 20, 77).Build() },
        { "reference from an undeclared item", () => CanonShaped().Reference("thmb", 77, 1).Build() },
        { "ipma property index past ipco", () => CanonShaped().Associate(20, 99).Build() },
        { "ipma for an undeclared item", () => CanonShaped().Associate(77, 1).Build() },
        { "box runs past its parent", () => { var f = CanonShaped().Build(); f[28] = 0x7F; return f; } }, // meta's size, after a 28-byte ftyp
        { "box smaller than its header", () => { var f = CanonShaped().Build(); f[28] = 0; f[29] = 0; f[30] = 0; f[31] = 7; return f; } },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedStructure_IsRefused(string what, Func<byte[]> make)
    {
        var file = make();
        Should.Throw<InvalidDataException>(() => HeifReader.Read(file), what);
    }

    public static TheoryData<string, Func<byte[]>> BadGrids => new()
    {
        { "fewer tiles than the layout", () => Grid(rows: 2, columns: 3, tiles: 5).Build() },
        { "tiles that do not cover the output", () => Grid(rows: 2, columns: 3, tiles: 6, outputWidth: 200).Build() },
        { "tiles of different sizes", () => Grid(rows: 1, columns: 2, tiles: 2, oddTile: true).Build() },
        { "an ispe that disagrees with the layout", () => Grid(rows: 1, columns: 2, tiles: 2, gridIspeWidth: 100).Build() },
        { "an empty output", () => Grid(rows: 1, columns: 2, tiles: 2, outputWidth: 0).Build() },
        { "a tile with no hvcC", () => Grid(rows: 1, columns: 2, tiles: 2, tileWithoutHvcc: true).Build() },
        { "an extent past the end of the file", () => Grid(rows: 1, columns: 2, tiles: 2, truncateLayout: true).Build() },
    };

    private static HeifBuilder Grid(int rows, int columns, int tiles, uint outputWidth = 180, bool oddTile = false,
        uint? gridIspeWidth = null, bool tileWithoutHvcc = false, bool truncateLayout = false)
    {
        var b = new HeifBuilder();
        var layout = HeifBuilder.GridLayout(rows, columns, outputWidth, 60);
        b.AddItem(new HeifBuilder.Item(1, "grid", truncateLayout ? layout[..3] : layout));
        var ids = Enumerable.Range(10, tiles).Select(i => (uint)i).ToArray();
        foreach (var id in ids) b.AddItem(new HeifBuilder.Item(id, "hvc1", [1], Hidden: true));
        b.Reference("dimg", 1, ids);
        var hvcc = b.Property("hvcC", Hvcc10Bit422);
        var size = b.Property("ispe", HeifBuilder.Ispe(64, 32));
        var odd = b.Property("ispe", HeifBuilder.Ispe(64, 30));
        foreach (var id in ids)
        {
            if (!(tileWithoutHvcc && id == ids[^1])) b.Associate(id, hvcc, true);
            b.Associate(id, oddTile && id == ids[^1] ? odd : size);
        }
        if (gridIspeWidth is { } w) b.Associate(1, "ispe", HeifBuilder.Ispe(w, 60));
        return b;
    }

    [Theory]
    [MemberData(nameof(BadGrids))]
    public void InconsistentGrids_AreRefusedWhenDescribed(string what, Func<byte[]> make)
    {
        var file = make();
        var heif = HeifReader.Read(file); // the structure itself is well formed
        Should.Throw<InvalidDataException>(() => heif.DescribePrimary(file), what);
    }

    [Fact]
    public void AnExtentPastTheEndOfTheFile_IsRefusedWhenRead()
    {
        var file = CanonShaped().Build();
        var heif = HeifReader.Read(file);
        var cut = file[..^3]; // the last item's data loses its last bytes
        Should.Throw<InvalidDataException>(() => heif.ReadItemData(cut, heif.GetItem(31)!));
    }

    [Fact]
    public void AnItemInAnotherFile_OrBuiltFromOthers_IsNotSupported_ButDoesNotSpoilTheRest()
    {
        // Patch the Exif item's iloc entry (version 1: id, construction method, data reference) after
        // building, since the builder never writes the exotic forms.
        var file = CanonShaped().Build();
        var heif = HeifReader.Read(file);
        heif.DescribePrimary(file).Width.ShouldBe(180u);

        var exifEntry = FindIlocEntry(file, 30);
        file[exifEntry + 3] = 2; // construction method 2: from other items
        Should.Throw<NotSupportedException>(() => HeifReader.Read(file).ReadItemData(file, HeifReader.Read(file).GetItem(30)!));
        HeifReader.Read(file).DescribePrimary(file).Width.ShouldBe(180u);

        file[exifEntry + 3] = 0;
        file[exifEntry + 5] = 1; // data_reference_index 1: another file
        Should.Throw<NotSupportedException>(() => HeifReader.Read(file).ReadItemData(file, HeifReader.Read(file).GetItem(30)!));
    }

    private static int FindIlocEntry(byte[] file, ushort itemId)
    {
        var iloc = file.AsSpan().IndexOf("iloc"u8);
        // body: version/flags (4), sizes (2), count (2), then 16-byte entries for iloc v1 with 4-byte
        // offsets and lengths and no base offset: id, construction method, data reference, extent count,
        // offset, length.
        for (var at = iloc + 4 + 4 + 2 + 2; at < file.Length; at += 16)
            if (file[at] == itemId >> 8 && file[at + 1] == (itemId & 0xFF)) return at;
        throw new InvalidOperationException("item not found");
    }

    // ---- declared-size limits -----------------------------------------------------------------------

    [Fact]
    public void AnHevcImageLargerThanAnyLevelAllows_IsRefusedBeforeAnythingIsSized()
    {
        var b = new HeifBuilder().AddItem(new HeifBuilder.Item(1, "hvc1", [1]));
        b.Associate(1, "hvcC", Hvcc10Bit422, true).Associate(1, "ispe", HeifBuilder.Ispe(20000, 100));
        var file = b.Build();
        var e = Should.Throw<InvalidDataException>(() => HeifReader.Read(file).DescribePrimary(file));
        e.Message.ShouldContain("16888");
    }

    [Fact]
    public void AGridOutputOverThePixelLimit_IsRefused_FromTwentyBytesOfLayout()
    {
        // 256x256 tiles of 16888 square would be legal tile by tile; the output is what is too large.
        var b = new HeifBuilder();
        b.AddItem(new HeifBuilder.Item(1, "grid", HeifBuilder.GridLayout(1, 1, 30000, 30000, wide: true)));
        b.AddItem(new HeifBuilder.Item(10, "hvc1", [1], Hidden: true));
        b.Reference("dimg", 1, 10);
        b.Associate(10, "hvcC", Hvcc10Bit422, true).Associate(10, "ispe", HeifBuilder.Ispe(16000, 16000));
        var file = b.Build();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = Should.Throw<InvalidDataException>(() => HeifReader.Read(file).DescribePrimary(file));
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(256 * 1024);
        e.Message.ShouldContain("pixel limit");
    }

    [Fact]
    public void ACamerasLargestStills_AreInsideTheLimits()
    {
        // 100 Mpixel (a medium-format still) as a grid of 512-pixel tiles.
        var b = new HeifBuilder();
        b.AddItem(new HeifBuilder.Item(1, "grid", HeifBuilder.GridLayout(23, 23, 11648, 8736, wide: true)));
        var ids = Enumerable.Range(10, 23 * 23).Select(i => (uint)i).ToArray();
        foreach (var id in ids) b.AddItem(new HeifBuilder.Item(id, "hvc1", [1], Hidden: true));
        b.Reference("dimg", 1, ids);
        var hvcc = b.Property("hvcC", Hvcc10Bit422);
        var size = b.Property("ispe", HeifBuilder.Ispe(512, 384));
        foreach (var id in ids) b.Associate(id, hvcc, true).Associate(id, size);
        b.IlocVersion = 2;
        b.IpmaWideIndices = true;
        var file = b.Build();

        var image = HeifReader.Read(file).DescribePrimary(file);
        ((long)image.Width * image.Height).ShouldBeGreaterThan(100_000_000);
    }

    // ---- robustness ---------------------------------------------------------------------------------

    /// <summary>
    /// Reading, describing every image and reading every item of a damaged file may fail, but only with
    /// the two documented exceptions: anything else (an index out of range, an overflow, an
    /// out-of-memory) is a reader bug, and callers' <c>Try</c> wrappers would let it escape.
    /// </summary>
    private static void ReadEverything(byte[] file)
    {
        try
        {
            var heif = HeifReader.Read(file);
            foreach (var item in heif.Items)
            {
                if (item.Type is "hvc1" or "grid") heif.Describe(file, item);
                if (item.Type is "Exif" or "mime" or "hvc1") heif.ReadItemData(file, item);
            }
            heif.ThumbnailsOf(heif.PrimaryItem);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
        }
    }

    [Fact]
    public void EveryTruncation_FailsCleanly()
    {
        var file = CanonShaped().Build();
        for (var length = 0; length < file.Length; length++) ReadEverything(file[..length]);
    }

    [Fact]
    public void RandomByteFlips_FailCleanly()
    {
        var original = CanonShaped().Build();
        var random = new Random(66);
        for (var trial = 0; trial < 20000; trial++)
        {
            var file = (byte[])original.Clone();
            var flips = random.Next(1, 5);
            for (var f = 0; f < flips; f++) file[random.Next(file.Length)] = (byte)random.Next(256);
            ReadEverything(file);
        }
    }

    [Fact]
    public void RandomByteFlipsInTheRealR7MetaBox_FailCleanly()
    {
        var original = HeifCanonFileTests.Load("canon-r7-hdrpq.hif");
        var random = new Random(7);
        const int metaEnd = 2214; // ftyp (32) + meta (2182): the structure, not the 13 MB of tile data
        for (var trial = 0; trial < 5000; trial++)
        {
            var file = (byte[])original.Clone();
            for (var f = 0; f < random.Next(1, 4); f++) file[random.Next(metaEnd)] = (byte)random.Next(256);
            ReadEverything(file);
        }
    }

    private sealed class GridComparer : IEqualityComparer<HeifGrid?>
    {
        public bool Equals(HeifGrid? x, HeifGrid? y) =>
            x is not null && y is not null && (x.Rows, x.Columns, x.OutputWidth, x.OutputHeight) == (y.Rows, y.Columns, y.OutputWidth, y.OutputHeight)
            && x.TileItemIds.SequenceEqual(y.TileItemIds);

        public int GetHashCode(HeifGrid? obj) => 0;
    }
}
