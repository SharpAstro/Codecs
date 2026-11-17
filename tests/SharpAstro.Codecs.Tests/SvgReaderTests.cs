using System.IO.Compression;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>The XML layer: what is accepted, what is refused, what is skipped and reported, and the limits.</summary>
public sealed class SvgReaderTests
{
    private const string Rect = "<rect width=\"10\" height=\"10\"/>";

    private static InvalidDataException Refused(string svg, SvgReadOptions? options = null) =>
        Should.Throw<InvalidDataException>(() => SvgReader.Parse(svg, options));

    private static string RectWith(string attributes) => Rect.Replace("/>", " " + attributes + "/>");

    [Fact]
    public void BytesStreamAndText_ReadTheSameDocument()
    {
        string svg = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- a comment -->\n" + Wrap(Rect);
        byte[] bytes = Encoding.UTF8.GetBytes(svg);
        SvgReader.Parse(svg).Scene.Nodes.ShouldHaveSingleItem();
        SvgReader.Read(bytes).Scene.Nodes.ShouldHaveSingleItem();
        using var stream = new MemoryStream(bytes);
        SvgReader.Read(stream).Scene.Nodes.ShouldHaveSingleItem();
        stream.CanRead.ShouldBeTrue();   // left open
        // A byte order mark and UTF-16 are the XML reader's business, and it handles them.
        SvgReader.Read([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Wrap(Rect))]).Scene.Nodes.ShouldHaveSingleItem();
    }

    [Fact]
    public void Svgz_IsUnzippedFirst_AlsoFromAStreamThatCannotSeek()
    {
        byte[] packed = Gzip(Wrap(Rect + Rect));
        SvgReader.Read(packed).Scene.Nodes.Length.ShouldBe(2);
        using var forwardOnly = new ForwardOnlyStream(packed);
        SvgReader.Read(forwardOnly).Scene.Nodes.Length.ShouldBe(2);
        using var plainForwardOnly = new ForwardOnlyStream(Encoding.UTF8.GetBytes(Wrap(Rect)));
        SvgReader.Read(plainForwardOnly).Scene.Nodes.ShouldHaveSingleItem();
    }

    private static byte[] Gzip(string text)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        return packed.ToArray();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<")]
    [InlineData("not xml at all")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    public void WhatIsNotWellFormedXml_IsRefused(string svg)
    {
        Refused(svg).Message.ShouldContain("not XML");
        Should.Throw<InvalidDataException>(() => SvgReader.Read(Encoding.UTF8.GetBytes(svg)));
    }

    [Theory]
    [InlineData("<html xmlns=\"http://www.w3.org/1999/xhtml\"/>")]
    [InlineData("<svg/>")]                                              // no namespace
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/SVG\"/>")]         // the wrong one
    [InlineData("<g xmlns=\"http://www.w3.org/2000/svg\"/>")]
    public void ADocumentWhoseRootIsNotSvg_IsRefused(string svg)
    {
        Refused(svg).Message.ShouldContain("Not an SVG document");
    }

    [Fact]
    public void ADamagedGzipStream_IsRefused()
    {
        Should.Throw<InvalidDataException>(() => SvgReader.Read([0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));
        Should.Throw<InvalidDataException>(() => SvgReader.Read([0x1F, 0x8B]));
        byte[] cut = Gzip(Wrap(Rect));
        Should.Throw<InvalidDataException>(() => SvgReader.Read(cut[..(cut.Length / 2)]));
    }

    [Fact]
    public void ADoctype_IsSkipped_AndNothingItNamesIsFetched()
    {
        // The public identifier SVG 1.1 files carry, and a system identifier that would be a network or file read.
        string svg = "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" + Wrap(Rect);
        SvgReader.Parse(svg).Scene.Nodes.ShouldHaveSingleItem();
        svg = "<!DOCTYPE svg SYSTEM \"file:///C:/Windows/win.ini\">" + Wrap(Rect);
        SvgReader.Parse(svg).Scene.Nodes.ShouldHaveSingleItem();
    }

    [Fact]
    public void EntitiesTheDocumentDeclares_AreExpanded_InAttributesInStyleSheetsAndAsElements()
    {
        // What an Adobe Illustrator export does: the namespace and every style are entities.
        SvgDocument document = SvgReader.Parse(
            "<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\" [" +
            $"<!ENTITY ns_svg \"{Namespace}\">" +
            "<!ENTITY st0 \"fill:#102030;stroke:none;\">" +
            "<!ENTITY green \"#00ff00\">" +
            "<!ENTITY dot \"<circle cx='5' cy='5' r='1' fill='&green;'/>\">" +
            "]><svg xmlns=\"&ns_svg;\" width=\"10\" height=\"10\">" +
            "<style>.b { fill: &green; }</style>" +
            "<rect width=\"1\" height=\"1\" style=\"&st0;\"/><rect class=\"b\" width=\"2\" height=\"2\"/>&dot;</svg>");
        document.Issues.ShouldBeEmpty();
        document.Scene.Nodes.Length.ShouldBe(3);
        FillColor(document.Scene.Nodes[0].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0x10, 0x20, 0x30));
        FillColor(document.Scene.Nodes[1].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0, 255, 0));
        FillColor(document.Scene.Nodes[2].ShouldBeOfType<VectorShape>()).ShouldBe(new Rgba32(0, 255, 0));
    }

    [Fact]
    public void WhatEntitiesExpandTo_IsBounded_HoweverLittleInputAsksForIt()
    {
        // Ten levels of ten: 10^10 characters from under 400 bytes. The limit is on what comes out.
        var doctype = new System.Text.StringBuilder("<!DOCTYPE svg [<!ENTITY e0 \"aaaaaaaaaa\">");
        for (int i = 1; i < 10; i++)
        {
            doctype.Append($"<!ENTITY e{i} \"").Insert(doctype.Length, $"&e{i - 1};", 10).Append("\">");
        }
        doctype.Append("]>");
        doctype.Length.ShouldBeLessThan(1000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Refused(doctype + Wrap("<desc>&e9;</desc>"));
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        // The same limit, set by the caller: a harmless entity is over a limit of nothing, and inside one of its size.
        string small = "<!DOCTYPE svg [<!ENTITY c \"red\">]>" + Wrap("<rect width=\"1\" height=\"1\" fill=\"&c;\"/>");
        Should.Throw<InvalidDataException>(() => SvgReader.Parse(small, new SvgReadOptions { MaxEntityCharacters = 2 }));
        FillColor(SvgReader.Parse(small, new SvgReadOptions { MaxEntityCharacters = 3 }).Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>())
            .ShouldBe(new Rgba32(255, 0, 0));
    }

    [Fact]
    public void AnEntityThatNamesAFile_IsNeverLoaded()
    {
        // The file exists and holds a shape: were it read, the scene would have it.
        string path = Path.Combine(Path.GetTempPath(), $"svg-entity-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, Rect);
        try
        {
            string uri = new Uri(path).AbsoluteUri;
            // Used between elements: nothing stands there, and the document says what it could not have.
            SvgDocument used = SvgReader.Parse($"<!DOCTYPE svg [<!ENTITY x SYSTEM \"{uri}\">]>" + Wrap("&x;"));
            used.Scene.Nodes.ShouldBeEmpty();
            used.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Unsupported, "an entity kept in another file", 1));
            Should.Throw<NotSupportedException>(() => SvgReader.Parse($"<!DOCTYPE svg [<!ENTITY x SYSTEM \"{uri}\">]>" + Wrap("&x;"),
                new SvgReadOptions { ThrowOnUnsupported = true }));
            // Used in an attribute: XML does not allow that at all.
            Refused($"<!DOCTYPE svg [<!ENTITY x PUBLIC \"-//x\" \"{uri}\">]>" + Wrap("<g id=\"&x;\"/>"));
            // Declared and never used: the document is read, the file is not, and it is still said.
            SvgDocument unused = SvgReader.Parse($"<!DOCTYPE svg [<!ENTITY x SYSTEM \"{uri}\">]>" + Wrap(Rect));
            unused.Scene.Nodes.ShouldHaveSingleItem();
            unused.Issues.ShouldHaveSingleItem().What.ShouldBe("an entity kept in another file");
            // A parameter entity pulled into the DOCTYPE itself is not loaded either: what it would have declared is not there.
            Refused($"<!DOCTYPE svg [<!ENTITY % more SYSTEM \"{uri}\"> %more;]>" + Wrap("&fromTheFile;"));
            SvgReader.Parse($"<!DOCTYPE svg [<!ENTITY % more SYSTEM \"{uri}\"> %more;]>" + Wrap(Rect)).Issues.ShouldHaveSingleItem();
            // An entity of the document's own is not one of these, whatever its text says.
            SvgReader.Parse("<!DOCTYPE svg [<!ENTITY x \"SYSTEM\">]>" + Wrap(Rect)).Issues.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("", "<desc>&nobody;</desc>")]                                                  // never declared
    [InlineData("<!DOCTYPE svg [<!ENTITY a \"&b;\"><!ENTITY b \"&a;\">]>", "<desc>&a;</desc>")]   // declared in a circle
    public void AnEntityThatCannotBeExpanded_IsRefused(string doctype, string body)
    {
        Refused(doctype + Wrap(body));
    }

    [Fact]
    public void ElementsOfOtherNamespaces_AndEverythingInsideThem_AreIgnoredSilently()
    {
        SvgDocument document = SvgReader.Parse(
            $"<svg xmlns=\"{Namespace}\" xmlns:x=\"urn:other\" xmlns:svg=\"{Namespace}\" width=\"10\" height=\"10\" x:fill=\"red\">" +
            $"<x:thing>{Rect}<x:deeper>{Rect}</x:deeper></x:thing>" +
            "<metadata><x:rdf><x:rect width=\"5\" height=\"5\"/></x:rdf></metadata>" +
            "<svg:rect width=\"3\" height=\"3\" x:width=\"99\" id=\"prefixed\"/>" +
            "</svg>");
        VectorShape shape = document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        shape.Id.ShouldBe("prefixed");
        Describe(shape.Path).ShouldBe("M 0 0 L 3 0 L 3 3 L 0 3 Z");
        FillColor(shape).ShouldBe(new Rgba32(0, 0, 0));
        document.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void ElementsThatAreNeverDrawnWhereTheyStand_AreSkippedSilently()
    {
        SvgDocument document = Doc(
            $"<title>t</title><desc>d</desc><defs>{Rect}<linearGradient id=\"g\"><stop offset=\"0\"/></linearGradient></defs>" +
            $"<symbol id=\"s\">{Rect}</symbol><clipPath id=\"c\">{Rect}</clipPath><mask id=\"m\">{Rect}</mask>" +
            $"<marker id=\"k\">{Rect}</marker><pattern id=\"p\">{Rect}</pattern><filter id=\"f\"/>");
        document.Scene.Nodes.ShouldBeEmpty();
        document.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void WhatIsNotReadYet_IsLeftOut_AndReportedWithItsCount()
    {
        SvgDocument document = Doc(
            "<image href=\"a.png\"/><switch><g/></switch><switch/><foreignObject><rect width=\"1\" height=\"1\"/></foreignObject>" +
            RectWith("filter=\"url(#f)\" clip-path=\"circle(4px)\" mask=\"linear-gradient(red, blue)\" id=\"kept\"") +
            RectWith("style=\"mix-blend-mode: multiply; vector-effect: non-scaling-stroke; transform: scale(2)\"") +
            RectWith("clip-path=\"none\" mask=\"none\" filter=\"none\" marker-end=\"none\" mix-blend-mode=\"normal\" paint-order=\"normal\"") +
            RectWith("stroke=\"red\" stroke-linejoin=\"arcs\"") +
            RectWith("clip-path=\"url(other.svg#c)\" fill=\"green\"") +
            RectWith("fill=\"url(other.svg#g) blue\"") +
            "<use href=\"other.svg#thing\"/>");

        // The shapes are still there, drawn without what was not read.
        document.Scene.Nodes.Length.ShouldBe(6);
        document.Scene.Nodes.ShouldAllBe(node => node is VectorShape);
        document.Scene.Nodes[0].Id.ShouldBe("kept");
        ((VectorShape)document.Scene.Nodes[3]).Stroke!.Join.ShouldBe(LineJoin.Miter);
        FillColor((VectorShape)document.Scene.Nodes[4]).ShouldBe(new Rgba32(0, 128, 0));
        FillColor((VectorShape)document.Scene.Nodes[5]).ShouldBe(new Rgba32(0, 0, 255));

        document.Issues.Select(issue => issue.Kind).Distinct().ShouldBe([SvgIssueKind.Unsupported]);
        document.Issues.ToDictionary(issue => issue.What, issue => issue.Count).ShouldBe(new Dictionary<string, int>
        {
            ["image with no width or height (sized by the picture)"] = 1,
            ["<switch> element"] = 2,
            ["<foreignObject> element"] = 1,
            ["clip-path: a value that is not a reference"] = 1,
            ["mask: a value that is not a reference"] = 1,
            ["clip-path: an element in another document"] = 1,
            ["filter"] = 1,
            ["mix-blend-mode"] = 1,
            ["vector-effect"] = 1,
            ["transform in a style attribute or sheet"] = 1,
            ["stroke-linejoin: arcs"] = 1,
            ["paint server in another document"] = 1,
            ["use of an element in another document"] = 1,
        }, ignoreOrder: true);
        // Most frequent first.
        document.Issues[0].ShouldBe(new SvgIssue(SvgIssueKind.Unsupported, "<switch> element", 2));
    }

    [Theory]
    [InlineData("url(#missing)", null, "paint: no element with that id")]
    [InlineData("url(#missing) none", null, "paint: no element with that id")]
    [InlineData("url(#missing) blue", "0,0,255", "paint: no element with that id")]
    [InlineData("url( '#missing' ) #0f0", "0,255,0", "paint: no element with that id")]
    [InlineData("url(\"#missing\") currentColor", "255,0,0", "paint: no element with that id")]
    [InlineData("url(#shape) blue", "0,0,255", "paint: not a paint server")]
    public void APaintReferenceThatCannotBeUsed_IsReported_AndItsFallbackIsUsed(string fill, string? expected, string what)
    {
        SvgDocument document = Doc($"<defs><rect id=\"shape\"/></defs><rect width=\"1\" height=\"1\" color=\"red\" fill=\"{fill.Replace("\"", "&quot;")}\"/>");
        document.Issues.ShouldHaveSingleItem().ShouldBe(new SvgIssue(SvgIssueKind.Invalid, what, 1));
        if (expected is null)
        {
            document.Scene.Nodes.ShouldBeEmpty();
        }
        else
        {
            Rgba32 colour = FillColor(document.Scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>());
            $"{colour.R},{colour.G},{colour.B}".ShouldBe(expected);
        }
    }

    [Fact]
    public void ThrowOnUnsupported_MakesTheFirstUnsupportedThingAnException_ButNotAnInvalidOne()
    {
        var strict = new SvgReadOptions { ThrowOnUnsupported = true };
        Should.Throw<NotSupportedException>(() => Doc("<switch/>", options: strict)).Message.ShouldContain("<switch> element");
        Should.Throw<NotSupportedException>(() => Doc("<rect width=\"1\" height=\"1\" filter=\"url(#f)\"/>", options: strict));
        Doc("<rect width=\"1\" height=\"1\" fill=\"bogus\"/><defs><switch/></defs>", options: strict)
            .Issues.ShouldHaveSingleItem().Kind.ShouldBe(SvgIssueKind.Invalid);
    }

    // ---- Limits --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheElementLimit_CountsEveryElement_AndIsExact()
    {
        string svg = Wrap(Rect + Rect + "<g>" + Rect + "</g>");   // 5 elements with the root
        SvgReader.Parse(svg, new SvgReadOptions { MaxElements = 5 }).Scene.Nodes.Length.ShouldBe(3);
        Refused(svg, new SvgReadOptions { MaxElements = 4 }).Message.ShouldContain("more than 4 elements");
    }

    [Fact]
    public void TheDepthLimit_IsExact_AndTheDefaultStopsADeepDocumentBeforeTheWalkCanOverflowTheStack()
    {
        static string Nested(int groups) => Wrap(string.Concat(Enumerable.Repeat("<g>", groups)) + Rect + string.Concat(Enumerable.Repeat("</g>", groups)));

        // Root + 3 groups + the rect is 5 deep.
        SvgReader.Parse(Nested(3), new SvgReadOptions { MaxDepth = 5 }).Scene.Nodes.ShouldHaveSingleItem();
        Refused(Nested(3), new SvgReadOptions { MaxDepth = 4 }).Message.ShouldContain("more than 4 deep");
        SvgReader.Parse(Nested(254)).Scene.Nodes.ShouldHaveSingleItem();
        Refused(Nested(100_000)).Message.ShouldContain("more than 256 deep");
    }

    [Fact]
    public void TheCharacterLimit_AppliesAfterGzip_SoASmallFileCannotBeALargeDocument()
    {
        // 4 MB of spaces in a comment: a few KB gzipped.
        string svg = Wrap("<!--" + new string(' ', 4 << 20) + "-->" + Rect);
        byte[] packed = Gzip(svg);
        packed.Length.ShouldBeLessThan(16 << 10);
        SvgReader.Read(packed).Scene.Nodes.ShouldHaveSingleItem();
        Should.Throw<InvalidDataException>(() => SvgReader.Read(packed, new SvgReadOptions { MaxCharacters = 1 << 20 }));
        Refused(svg, new SvgReadOptions { MaxCharacters = 1 << 20 });
    }

    [Fact]
    public void ThePathVerbLimit_IsOverTheWholeScene()
    {
        string svg = Wrap("<path d=\"M0 0 L1 1 L2 2\"/><path d=\"M0 0 L1 1 L2 2\"/>");   // 3 + 3 verbs
        SvgReader.Parse(svg, new SvgReadOptions { MaxPathVerbs = 6 }).Scene.Nodes.Length.ShouldBe(2);
        Refused(svg, new SvgReadOptions { MaxPathVerbs = 5 }).Message.ShouldContain("more than 5 segments");
    }

    [Fact]
    public void ALimitBelowOne_IsACallersMistake()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SvgReader.Parse(Wrap(Rect), new SvgReadOptions { MaxElements = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => SvgReader.Parse(Wrap(Rect), new SvgReadOptions { MaxDepth = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => SvgReader.Parse(Wrap(Rect), new SvgReadOptions { MaxCharacters = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => SvgReader.Parse(Wrap(Rect), new SvgReadOptions { MaxPathVerbs = 0 }));
    }

    // ---- The scene's own types -----------------------------------------------------------------------------------

    [Fact]
    public void APath_ReplaysIntoASink_WhatWasBuilt()
    {
        var builder = new PathBuilder();
        builder.MoveTo(1, 2);
        builder.LineTo(3, 4);
        builder.QuadTo(5, 6, 7, 8);
        builder.CubicTo(9, 10, 11, 12, 13, 14);
        builder.Close();
        VectorPath path = builder.ToPath();
        builder.LineTo(99, 99);   // the path already taken does not change

        var copy = new PathBuilder();
        path.Replay(copy);
        Describe(copy.ToPath()).ShouldBe("M 1 2 L 3 4 Q 5 6 7 8 C 9 10 11 12 13 14 Z");
        path.Verbs.Length.ShouldBe(5);
        path.Coordinates.Length.ShouldBe(14);
        path.IsEmpty.ShouldBeFalse();
        VectorPath.Empty.IsEmpty.ShouldBeTrue();
        new PathBuilder().ToPath().ShouldBeSameAs(VectorPath.Empty);
    }

    /// <summary>A stream that can only be read forwards, as a network or pipe stream is.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // One byte at a time, so that nothing can rely on a read being filled.
            if (_position >= data.Length || count == 0) return 0;
            buffer[offset] = data[_position++];
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
