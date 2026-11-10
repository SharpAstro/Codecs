using System.Buffers.Binary;
using System.Text;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Apple's gain-map dialect (an <c>auxl</c> auxiliary image typed by <c>auxC</c>, headroom from the Exif MakerNote or the aux
/// item's XMP), read best-effort from Apple's public article. The files are built to that description from the same x265
/// fixtures as the <c>tmap</c> ones: no real iPhone file is in the repository, so what is checked is that the reader does what
/// the article says, against a transcription of the article's formula that shares no code with the core.
/// </summary>
public partial class HeifGainMapTests
{
    private const uint AppleAuxId = 2, ExifId = 4, XmpId = 5, OtherMapId = 6, AlphaId = 7, DepthId = 8;
    private const string AppleUrn = "urn:com:apple:photo:2020:aux:hdrgainmap";
    private const string AlphaUrn = "urn:mpeg:hevc:2015:auxid:1";
    private const string DepthUrn = "urn:mpeg:hevc:2015:auxid:2";

    // ---- the Exif item with Apple's MakerNote ------------------------------------------------------

    /// <summary>
    /// An Exif item (a 4-byte offset to the TIFF header, <c>Exif\0\0</c>, then the TIFF) whose Exif IFD holds a MakerNote:
    /// <c>Apple iOS\0</c>, version 1, a byte-order mark and an IFD at offset 14 with keys 33 and 48 as the given TIFF type
    /// (5 RATIONAL, 10 SRATIONAL, 11 FLOAT, 12 DOUBLE). Offsets in the note count from its start.
    /// </summary>
    private static byte[] ExifWithMakerNote(double maker33, double maker48, ushort type = 10, bool little = false, string make = "Apple iOS", bool tag33 = true)
    {
        byte[] U16(int v) { var b = new byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
        byte[] U32(uint v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }

        // The two values: stored out of the entry (8 bytes each) except FLOAT, which fits it.
        byte[] Value(double v) => type switch
        {
            5 => [.. U32((uint)Math.Round(v * 1000000)), .. U32(1000000)],
            10 => [.. U32((uint)(int)Math.Round(v * 1000000)), .. U32(1000000)],
            12 => little ? BitConverter.GetBytes(v) : BitConverter.GetBytes(v).Reverse().ToArray(),
            _ => [],
        };
        var entries = tag33 ? 2 : 1;
        var ifdSize = 2 + 12 * entries + 4;
        var dataAt = 14 + ifdSize; // offsets from the start of the note

        var note = new List<byte>();
        note.AddRange(Encoding.ASCII.GetBytes(make + "\0"));          // 10 bytes
        note.AddRange(new byte[] { 0, 1 });
        note.AddRange(Encoding.ASCII.GetBytes(little ? "II" : "MM"));
        note.AddRange(U16(entries));
        var data = new List<byte>();
        void Entry(ushort tag, double value)
        {
            note.AddRange(U16(tag));
            note.AddRange(U16(type));
            note.AddRange(U32(1));
            if (type == 11)
            {
                var bits = BitConverter.SingleToInt32Bits((float)value);
                note.AddRange(U32((uint)bits));
            }
            else
            {
                note.AddRange(U32((uint)(dataAt + data.Count)));
                data.AddRange(Value(value));
            }
        }
        if (tag33) Entry(33, maker33);
        Entry(48, maker48);
        note.AddRange(U32(0));
        note.AddRange(data);

        // TIFF: header, IFD0 {ExifIFD pointer}, ExifIFD {MakerNote}, then the note.
        var tiff = new List<byte>();
        tiff.AddRange(Encoding.ASCII.GetBytes(little ? "II" : "MM"));
        tiff.AddRange(U16(0x002A));
        tiff.AddRange(U32(8));
        tiff.AddRange(U16(1));                                   // IFD0 at 8
        tiff.AddRange(U16(0x8769)); tiff.AddRange(U16(4)); tiff.AddRange(U32(1)); tiff.AddRange(U32(26));
        tiff.AddRange(U32(0));
        tiff.AddRange(U16(1));                                   // ExifIFD at 26
        tiff.AddRange(U16(0x927C)); tiff.AddRange(U16(7)); tiff.AddRange(U32((uint)note.Count)); tiff.AddRange(U32(44));
        tiff.AddRange(U32(0));
        tiff.AddRange(note);                                     // at 44

        return [.. HeifBuilder.U32(6), .. "Exif\0\0"u8.ToArray(), .. tiff];
    }

    private static byte[] AuxC(string urn) => HeifBuilder.Concat(HeifBuilder.FullBoxHeader(0, 0), Encoding.UTF8.GetBytes(urn + "\0"));

    private static byte[] Xmp(string capacityMax) =>
        Encoding.UTF8.GetBytes($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\" hdrgm:HDRCapacityMax=\"{capacityMax}\"/></rdf:RDF></x:xmpmeta>");

    internal sealed class AppleOptions
    {
        public string Urn { get; set; } = AppleUrn;
        public byte[]? Exif { get; set; }
        public byte[]? Xmp { get; set; }
        public bool WithTmap { get; set; }
        public bool AlphaAndDepthFirst { get; set; }
        public string AuxType { get; set; } = "hvc1";
        public uint AuxRefersTo { get; set; } = BaseId;
        /// <summary>An <c>irot</c> on the primary (item 1), as Apple writes for a portrait photo.</summary>
        public byte[]? BaseIrot { get; set; }
        /// <summary>An <c>irot</c> on the aux map (item 2); Apple writes none.</summary>
        public byte[]? MapIrot { get; set; }
    }

    /// <summary>
    /// An Apple-layout file: the base (item 1) is the primary, the gain map is a hidden item 2 with an <c>auxC</c> and an
    /// <c>auxl</c> reference to the base, the Exif (item 4) and the XMP (item 5, on the aux item) are metadata items with
    /// <c>cdsc</c> references. Optionally also a <c>tmap</c> (item 3) over the base and a second map (item 6), and alpha and
    /// depth auxiliary items (7, 8) before the Apple one.
    /// </summary>
    private static byte[] BuildApple(ImageSpec baseSpec, ImageSpec mapSpec, Action<AppleOptions>? configure = null)
    {
        var o = new AppleOptions();
        configure?.Invoke(o);
        var builder = new HeifBuilder { PrimaryItemId = BaseId, IlocVersion = 1 };
        AddImage(builder, BaseId, baseSpec, hidden: false, firstTileId: 10);

        if (o.AlphaAndDepthFirst)
        {
            foreach (var (id, urn) in new[] { (AlphaId, AlphaUrn), (DepthId, DepthUrn) })
            {
                AddImage(builder, id, mapSpec, hidden: true, firstTileId: 20);
                builder.Associate(id, "auxC", AuxC(urn));
                builder.Reference("auxl", id, BaseId);
            }
        }

        if (o.AuxType == "hvc1")
        {
            AddImage(builder, AppleAuxId, mapSpec, hidden: true, firstTileId: 30);
        }
        else
        {
            builder.AddItem(new HeifBuilder.Item(AppleAuxId, o.AuxType, new byte[8], Hidden: true));
        }
        builder.Associate(AppleAuxId, "auxC", AuxC(o.Urn));
        if (o.BaseIrot is not null) builder.Associate(BaseId, "irot", o.BaseIrot, essential: true);
        if (o.MapIrot is not null) builder.Associate(AppleAuxId, "irot", o.MapIrot, essential: true);
        builder.Reference("auxl", AppleAuxId, o.AuxRefersTo);

        if (o.Exif is not null)
        {
            builder.AddItem(new HeifBuilder.Item(ExifId, "Exif", o.Exif, Hidden: true));
            builder.Reference("cdsc", ExifId, BaseId);
        }
        if (o.Xmp is not null)
        {
            builder.AddItem(new HeifBuilder.Item(XmpId, "mime", o.Xmp, Hidden: true, ContentType: "application/rdf+xml"));
            builder.Reference("cdsc", XmpId, AppleAuxId);
        }
        if (o.WithTmap)
        {
            AddImage(builder, OtherMapId, mapSpec, hidden: true, firstTileId: 40);
            builder.AddItem(new HeifBuilder.Item(TmapId, "tmap", TmapData(Scalar())));
            builder.Reference("dimg", TmapId, BaseId, OtherMapId);
            builder.Associate(TmapId, "ispe", HeifBuilder.Ispe(Displayed(baseSpec.Fixture).W, Displayed(baseSpec.Fixture).H));
        }
        return builder.Build();
    }

    /// <summary>Headrooms by hand from the article's formula, for the keys the tests use (no code shared with the reader).</summary>
    private static double HeadroomOf(double maker33, double maker48)
    {
        var stops = maker33 < 1.0
            ? (maker48 <= 0.01 ? -20.0 * maker48 + 1.8 : -0.101 * maker48 + 1.601)
            : (maker48 <= 0.01 ? -70.0 * maker48 + 3.0 : -0.303 * maker48 + 2.303);
        return Math.Pow(2.0, Math.Max(stops, 0.0));
    }

    internal static byte[] AppleFile(double maker33 = 2, double maker48 = 0, Action<AppleOptions>? configure = null) =>
        BuildApple(Base256, Mono8, o => { o.Exif = ExifWithMakerNote(maker33, maker48); configure?.Invoke(o); });

    // ---- structure ---------------------------------------------------------------------------------

    [Fact]
    public void AnAppleAuxGainMap_IsFound_AndTypedByItsAuxC()
    {
        var file = AppleFile();
        var heif = HeifReader.Read(file);
        heif.GetItem(AppleAuxId)!.Property<HeifAuxiliaryType>().ShouldNotBeNull().Urn.ShouldBe(AppleUrn);

        var info = heif.GainMapOfPrimary(file).ShouldNotBeNull();
        info.Dialect.ShouldBe(HeifGainMapDialect.AppleAux);
        info.Tmap.ShouldBeNull();
        (info.Base.Id, info.Map.Id).ShouldBe((BaseId, AppleAuxId));
        info.Layout.ShouldBe(HeifGainMapLayout.BaseIsPrimary);
        info.Alternatives.ShouldBeNull();
        var apple = info.Apple.ShouldNotBeNull();
        apple.Aux.Id.ShouldBe(AppleAuxId);
        apple.Source.ShouldBe(HeifAppleHeadroomSource.MakerNote);
        apple.Headroom.ShouldBe(8, 1e-9, "maker33 >= 1 and maker48 = 0: 3 stops");
        info.Metadata.HdrCapacityMax.ShouldBe(8, 1e-9);
        (info.Metadata.GainMapMin, info.Metadata.GainMapMax, info.Metadata.OffsetSdr, info.Metadata.OffsetHdr).ShouldBe((1.0, 8.0, 0.0, 0.0));

        // The aux item is no image of its own with a gain map.
        heif.GainMapOf(file, heif.GetItem(AppleAuxId)!).ShouldBeNull();
    }

    [Theory]
    [InlineData(2.0, 0.0, 3.0)]        // maker33 >= 1, maker48 <= 0.01: -70*0 + 3
    [InlineData(1.0, 0.01, 2.3)]       // the boundaries: maker33 = 1 is the second branch, maker48 = 0.01 the first of it: -0.7 + 3
    [InlineData(2.0, 0.5, 2.1515)]     // -0.303*0.5 + 2.303
    [InlineData(0.5, 0.0, 1.8)]        // maker33 < 1: -20*0 + 1.8
    [InlineData(0.99, 0.01, 1.6)]      // -0.2 + 1.8
    [InlineData(0.5, 0.5, 1.5505)]     // -0.101*0.5 + 1.601
    public void HeadroomFromStops_IsApplesFormula(double maker33, double maker48, double stops)
    {
        HeifAppleGainMap.HeadroomFromStops(maker33, maker48).ShouldBe(Math.Pow(2, stops), 1e-9);
        HeadroomOf(maker33, maker48).ShouldBe(Math.Pow(2, stops), 1e-9);
    }

    [Fact]
    public void HeadroomFromStops_NeverGoesBelowOne()
    {
        HeifAppleGainMap.HeadroomFromStops(2, 20).ShouldBe(1);   // -6.06 + 2.303 < 0: max(stops, 0)
        HeifAppleGainMap.HeadroomFromStops(0.5, 50).ShouldBe(1);
    }

    [Theory]
    [InlineData((ushort)10, false)] // SRATIONAL
    [InlineData((ushort)5, false)]  // RATIONAL
    [InlineData((ushort)11, false)] // FLOAT, in the entry
    [InlineData((ushort)12, false)] // DOUBLE
    [InlineData((ushort)10, true)]  // little-endian note
    [InlineData((ushort)11, true)]
    public void TheMakerNoteKeys_AreReadInAnyOfTheNumericTypes(ushort type, bool little)
    {
        var file = BuildApple(Base256, Mono8, o => o.Exif = ExifWithMakerNote(0.5, 0.5, type, little));
        var info = HeifReader.Read(file).GainMapOfPrimary(file).ShouldNotBeNull();
        info.Apple!.Headroom.ShouldBe(HeadroomOf(0.5, 0.5), 1e-6);
    }

    // ---- headroom from XMP, and which wins ---------------------------------------------------------

    [Fact]
    public void WithoutMakerNote_TheAuxItemsXmpGivesTheHeadroom()
    {
        var file = BuildApple(Base256, Mono8, o => o.Xmp = Xmp("2"));
        var apple = HeifReader.Read(file).GainMapOfPrimary(file).ShouldNotBeNull().Apple!;
        apple.Source.ShouldBe(HeifAppleHeadroomSource.Xmp);
        apple.Headroom.ShouldBe(4, 1e-9, "HDRCapacityMax is log2");
    }

    [Fact]
    public void TheMakerNote_IsPreferredToTheXmp()
    {
        var file = BuildApple(Base256, Mono8, o => { o.Exif = ExifWithMakerNote(2, 0); o.Xmp = Xmp("2"); });
        var apple = HeifReader.Read(file).GainMapOfPrimary(file).ShouldNotBeNull().Apple!;
        (apple.Source, apple.Headroom).ShouldBe((HeifAppleHeadroomSource.MakerNote, 8.0));
    }

    // ---- missing or absurd headroom ----------------------------------------------------------------

    [Fact]
    public void NoHeadroomAnywhere_IsNoGainMap()
    {
        var file = BuildApple(Base256, Mono8);
        HeifReader.Read(file).GainMapOfPrimary(file).ShouldBeNull();
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file)).Message.ShouldContain("no gain map");
    }

    [Fact]
    public void AnExifWithoutTheKeys_OrFromAnotherMake_OrWithNonFiniteValues_IsNoGainMap()
    {
        foreach (var exif in new[]
        {
            ExifWithMakerNote(2, 0, tag33: false),             // key 33 missing
            ExifWithMakerNote(2, 0, make: "Apple Xyz"),        // another note (and 9 bytes: not "Apple iOS\0")
            ExifWithMakerNote(double.NaN, 0, 11),              // a float that is not a number
            ExifWithMakerNote(2, double.PositiveInfinity, 12),
            new byte[] { 0, 0, 0, 0, 1, 2, 3 },               // not a TIFF at all
            Array.Empty<byte>(),
        })
        {
            var file = BuildApple(Base256, Mono8, o => o.Exif = exif);
            HeifReader.Read(file).GainMapOfPrimary(file).ShouldBeNull();
        }
    }

    [Fact]
    public void AHeadroomOfOneOrLess_IsNoGainMap()
    {
        var file = AppleFile(2, 20);   // stops < 0: headroom 1, nothing to apply
        HeifReader.Read(file).GainMapOfPrimary(file).ShouldBeNull();
    }

    [Theory]
    [InlineData(2.0, -1.0)]   // 70 + 3 = 73 stops
    [InlineData(0.5, -100.0)] // 2000 + 1.8
    public void AnAbsurdHeadroom_IsRefused(double maker33, double maker48)
    {
        var file = AppleFile(maker33, maker48);
        Should.Throw<InvalidDataException>(() => HeifReader.Read(file).GainMapOfPrimary(file)).Message.ShouldContain("headroom");
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file));
    }

    [Fact]
    public void AnAbsurdXmpHeadroom_IsRefused()
    {
        var file = BuildApple(Base256, Mono8, o => o.Xmp = Xmp("12"));   // 2^12
        Should.Throw<InvalidDataException>(() => HeifReader.Read(file).GainMapOfPrimary(file));
    }

    // ---- which auxiliary items are gain maps -------------------------------------------------------

    [Fact]
    public void AlphaAndDepthAuxiliaryImages_AreNotGainMaps()
    {
        // Alone: nothing, even with a perfectly good Exif headroom on the image.
        var builder = new HeifBuilder { PrimaryItemId = BaseId, IlocVersion = 1 };
        AddImage(builder, BaseId, Base256, hidden: false, firstTileId: 10);
        foreach (var (id, urn) in new[] { (AlphaId, AlphaUrn), (DepthId, DepthUrn) })
        {
            AddImage(builder, id, Mono8, hidden: true, firstTileId: 20);
            builder.Associate(id, "auxC", AuxC(urn));
            builder.Reference("auxl", id, BaseId);
        }
        builder.AddItem(new HeifBuilder.Item(ExifId, "Exif", ExifWithMakerNote(2, 0), Hidden: true));
        builder.Reference("cdsc", ExifId, BaseId);
        var file = builder.Build();
        var heif = HeifReader.Read(file);
        heif.GainMapOfPrimary(file).ShouldBeNull();
        heif.GetItem(AlphaId)!.Property<HeifAuxiliaryType>()!.Urn.ShouldBe(AlphaUrn);
        heif.GetItem(DepthId)!.Property<HeifAuxiliaryType>()!.Urn.ShouldBe(DepthUrn);

        // Beside the Apple one, they are passed over and the Apple map is the one found.
        var both = AppleFile(configure: o => o.AlphaAndDepthFirst = true);
        HeifReader.Read(both).GainMapOfPrimary(both).ShouldNotBeNull().Map.Id.ShouldBe(AppleAuxId);
    }

    [Fact]
    public void AnotherVendorsAuxiliaryImage_AndAnAppleOneOfAnotherImage_AreNotThisImagesGainMap()
    {
        var samsung = AppleFile(configure: o => o.Urn = "urn:com:samsung:photo:2024:aux:hdrgainmap");
        HeifReader.Read(samsung).GainMapOfPrimary(samsung).ShouldBeNull("Samsung's URN is not Apple's (and Samsung's files carry a tmap)");

        var elsewhere = AppleFile(configure: o => o.AuxRefersTo = ExifId); // an auxl to some other item
        HeifReader.Read(elsewhere).GainMapOfPrimary(elsewhere).ShouldBeNull();
    }

    [Fact]
    public void AnAppleAuxiliaryItem_OfAnUndecodableType_IsNotSupported()
    {
        var file = AppleFile(configure: o => o.AuxType = "av01");
        Should.Throw<NotSupportedException>(() => HeifReader.Read(file).GainMapOfPrimary(file)).Message.ShouldContain("'av01'");
    }

    // ---- precedence --------------------------------------------------------------------------------

    [Fact]
    public void WhenBothCarriersArePresent_TheTmapIsUsed()
    {
        var file = AppleFile(configure: o => o.WithTmap = true);
        var heif = HeifReader.Read(file);
        var info = heif.GainMapOfPrimary(file).ShouldNotBeNull();
        info.Dialect.ShouldBe(HeifGainMapDialect.IsoTmap);
        (info.Tmap!.Id, info.Map.Id).ShouldBe((TmapId, OtherMapId));
        info.Apple.ShouldBeNull();
        HeifGainMap.Decode(file).Info.Dialect.ShouldBe(HeifGainMapDialect.IsoTmap);

        // And the Apple route is still there for a file that has only it.
        HeifReader.Read(AppleFile()).GainMapOfPrimary(AppleFile()).ShouldNotBeNull().Dialect.ShouldBe(HeifGainMapDialect.AppleAux);
    }

    // ---- reconstruction ----------------------------------------------------------------------------

    [Fact]
    public void SdrHeadroom_IsTheLinearisedBase_Exactly_ForAppleToo()
    {
        var file = AppleFile();
        var hdr = HeifGainMap.Decode(file, displayHeadroom: 1);
        var expected = HeifDecoder.Decode(file, HeifReader.Read(file).GetItem(BaseId)).ToLinearRgbaFloat(out _);
        hdr.Weight.ShouldBe(0);
        hdr.Rgba.ShouldBe(expected, "bit for bit: base * (1 + 0 * g)");
    }

    [Theory]
    [InlineData(2.0, 0.0, null)]       // full authored headroom (8), the default
    [InlineData(2.0, 0.0, 2.0)]        // display 2 of authored 8
    [InlineData(0.5, 0.5, null)]       // 2^1.5505, from the other branch
    [InlineData(2.0, 0.0, 100.0)]      // above the authored: the full effect
    public void TheReconstruction_MatchesApplesFormula_PixelForPixel(double maker33, double maker48, double? display)
    {
        var file = AppleFile(maker33, maker48);
        var hdr = HeifGainMap.Decode(file, displayHeadroom: display);
        var authored = HeadroomOf(maker33, maker48);
        var applied = Math.Clamp(display ?? authored, 1, authored);
        hdr.Weight.ShouldBe((applied - 1) / (authored - 1), 1e-12);
        hdr.DisplayHeadroom.ShouldBe(display ?? authored, 1e-12);

        var heif = HeifReader.Read(file);
        var baseRgb = HeifDecoder.Decode(file, heif.GetItem(BaseId));
        var mapRgb = HeifDecoder.Decode(file, heif.GetItem(AppleAuxId));
        (hdr.Width, hdr.Height).ShouldBe((baseRgb.Width, baseRgb.Height));
        var (w, h, mw, mh) = (baseRgb.Width, baseRgb.Height, mapRgb.Width, mapRgb.Height);

        // The article, per pixel in double: base linearised (sRGB here), the map's G' over 255 resized (centre-aligned
        // bilinear) and then linearised by BT.709's inverse, and sdr * (1 + (headroom - 1) * gainmap).
        static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Rec709ToLinear(double v) => v < 0.081242858298635 ? v / 4.5 : Math.Pow((v + 0.099296826809442) / 1.099296826809442, 1 / 0.45);
        double MapAt(int x, int y) => mapRgb.Samples[3 * (y * mw + x) + 1] / 255.0;

        var worst = 0.0;
        for (var y = 0; y < h; y++)
        {
            var fy = (y + 0.5) * mh / h - 0.5;
            var y0 = (int)Math.Floor(fy);
            var ty = Math.Clamp(fy - y0, 0, 1);
            int ya = Math.Clamp(y0, 0, mh - 1), yb = Math.Clamp(y0 + 1, 0, mh - 1);
            for (var x = 0; x < w; x++)
            {
                var fx = (x + 0.5) * mw / w - 0.5;
                var x0 = (int)Math.Floor(fx);
                var tx = Math.Clamp(fx - x0, 0, 1);
                int xa = Math.Clamp(x0, 0, mw - 1), xb = Math.Clamp(x0 + 1, 0, mw - 1);
                var code = (MapAt(xa, ya) * (1 - tx) + MapAt(xb, ya) * tx) * (1 - ty) + (MapAt(xa, yb) * (1 - tx) + MapAt(xb, yb) * tx) * ty;
                var gain = Rec709ToLinear(code);
                for (var c = 0; c < 3; c++)
                {
                    var expected = SrgbToLinear(baseRgb.Samples[3 * (y * w + x) + c] / 255.0) * (1 + (applied - 1) * gain);
                    worst = Math.Max(worst, Math.Abs(hdr.Rgba[4 * (y * w + x) + c] - expected) / Math.Max(1.0, Math.Abs(expected)));
                }
                hdr.Rgba[4 * (y * w + x) + 3].ShouldBe(1f);
            }
        }
        output.WriteLine($"apple {maker33}/{maker48}, display {display}: headroom {authored:0.000}, worst relative difference {worst:E2}");
        worst.ShouldBeLessThan(2e-5);
    }

    [Fact]
    public void TheAppleGain_ActuallyBoosts_AndNeverAttenuates()
    {
        // A guard against a formula and a reconstruction that both do nothing: the peak passes SDR white, and no pixel
        // is darker than the base (the gain 1 + (H - 1) g is at least 1).
        var file = AppleFile();
        var hdr = HeifGainMap.Decode(file);
        var sdr = HeifGainMap.Decode(file, displayHeadroom: 1);
        hdr.Rgba.Where((_, i) => i % 4 != 3).Max().ShouldBeGreaterThan(1.5f);
        for (var i = 0; i < hdr.Rgba.Length; i++) hdr.Rgba[i].ShouldBeGreaterThanOrEqualTo(sdr.Rgba[i]);
        Enumerable.Range(0, hdr.Width * hdr.Height).Count(p => hdr.Rgba[4 * p + 1] > sdr.Rgba[4 * p + 1] * 1.5f).ShouldBeGreaterThan(100);
    }

    [Fact]
    public void AnAppleMap_OfAMonochromeOrColourImage_DrivesAllThreeChannelsFromOne()
    {
        // The article's map is single-channel; a 4:2:0 colour one is read through its G' (the same as Y' for neutral chroma).
        var file = BuildApple(Base256, Colour10, o => o.Exif = ExifWithMakerNote(2, 0));
        var hdr = HeifGainMap.Decode(file);
        hdr.Info.Dialect.ShouldBe(HeifGainMapDialect.AppleAux);
        (hdr.Width, hdr.Height).ShouldBe((256, 192));
        hdr.Rgba.Where((_, i) => i % 4 != 3).Max().ShouldBeGreaterThan(1.5f);
    }

    [Fact]
    public void AMapLargerThanItsBase_IsRefusedBeforeDecoding_ForAppleToo()
    {
        // The tmap path's rule applies to Apple's map too: here the map declares more pixels than its base.
        var file = BuildApple(Base256, Mono8 with { DeclaredSize = (400, 400) }, o => o.Exif = ExifWithMakerNote(2, 0));
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file)).Message.ShouldContain("larger than its base");
    }

    // ---- Apple's portrait layout: a rotated primary, an unrotated aux map -------------------------

    [Fact]
    public void ApplesPortraitLayout_AnAuxMapWithNoIrotOnARotatedPrimary_IsTurnedLikeTheBase_AndReconstructs()
    {
        // Landscape-stored primary with an irot, an unrotated landscape aux map (stored in the sensor's orientation).
        var apple = AppleFile(configure: o => o.BaseIrot = HeifBuilder.Irot(90));
        var heif = HeifReader.Read(apple);
        heif.GainMapOfPrimary(apple)!.Dialect.ShouldBe(HeifGainMapDialect.AppleAux);

        var image = HeifGainMap.Decode(apple);
        (image.Width, image.Height).ShouldBe((192, 256));

        // The map is turned with the base: the same picture as an aux map that carries the irot itself.
        var explicitly = HeifGainMap.Decode(AppleFile(configure: o => { o.BaseIrot = HeifBuilder.Irot(90); o.MapIrot = HeifBuilder.Irot(90); }));
        (explicitly.Width, explicitly.Height).ShouldBe((192, 256));
        image.Rgba.ShouldBe(explicitly.Rgba);

        // And through the facade it is HDR at the turned size, not the silent SDR fallback.
        ImageCodecs.TryReadInfo(apple, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(192, 256, 4, SampleFormat.Float32));
    }

    [Fact]
    public void AnAuxMapStoredInTheOtherOrientationThanARotatedPrimary_IsStillRefused()
    {
        // A portrait-stored map under a landscape-stored primary cannot be the sensor-orientation map, rotated or not.
        var file = BuildApple(Base256, new ImageSpec("x265-400p8", HeifBuilder.Nclx(2, 2, 2, true), (192, 256)),
            o => { o.Exif = ExifWithMakerNote(2, 0); o.BaseIrot = HeifBuilder.Irot(90); });
        var heif = HeifReader.Read(file);
        var info = heif.GainMapOfPrimary(file)!;
        info.Dialect.ShouldBe(HeifGainMapDialect.AppleAux);
        HeifGainMap.MapMatchesBaseShape(heif.Describe(file, info.Base), heif.Describe(file, info.Map), info.Dialect).ShouldBeFalse();
    }
}
