using System.IO.Compression;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.Vector;
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

    [Theory]
    // Internal entities are not expanded, so neither a harmless one nor a billion-laughs chain does anything but fail.
    [InlineData("<!DOCTYPE svg [<!ENTITY c \"red\">]>", "<rect width=\"1\" height=\"1\" fill=\"&c;\"/>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;\">]>", "<desc>&c;</desc>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>", "<desc>&x;</desc>")]
    public void ADocumentThatUsesAnEntity_IsRefused(string doctype, string body)
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
            "<text x=\"1\" y=\"1\">words <tspan>more</tspan></text><text>again</text>" +
            "<image href=\"a.png\"/><switch><g/></switch><foreignObject><rect width=\"1\" height=\"1\"/></foreignObject>" +
            "<defs><pattern id=\"p\"/></defs>" +
            RectWith("clip-path=\"url(#c)\" mask=\"url(#m)\" filter=\"url(#f)\" marker-end=\"url(#k)\" id=\"kept\"") +
            RectWith("style=\"mix-blend-mode: multiply; paint-order: stroke; vector-effect: non-scaling-stroke; transform: scale(2)\"") +
            RectWith("clip-path=\"none\" mask=\"none\" filter=\"none\" marker=\"none\" mix-blend-mode=\"normal\" paint-order=\"normal\"") +
            RectWith("stroke=\"red\" stroke-linejoin=\"arcs\"") +
            RectWith("fill=\"url(#p) green\" id=\"patterned\"") +
            RectWith("fill=\"url(other.svg#g) blue\"") +
            "<use href=\"other.svg#thing\"/>");

        // The shapes are still there, drawn without what was not read.
        document.Scene.Nodes.Length.ShouldBe(6);
        document.Scene.Nodes[0].Id.ShouldBe("kept");
        ((VectorShape)document.Scene.Nodes[3]).Stroke!.Join.ShouldBe(LineJoin.Miter);
        FillColor((VectorShape)document.Scene.Nodes[4]).ShouldBe(new Rgba32(0, 128, 0));
        FillColor((VectorShape)document.Scene.Nodes[5]).ShouldBe(new Rgba32(0, 0, 255));

        document.Issues.Select(issue => issue.Kind).Distinct().ShouldBe([SvgIssueKind.Unsupported]);
        document.Issues.ToDictionary(issue => issue.What, issue => issue.Count).ShouldBe(new Dictionary<string, int>
        {
            ["<text> element"] = 2,
            ["<image> element"] = 1,
            ["<switch> element"] = 1,
            ["<foreignObject> element"] = 1,
            ["clip-path"] = 1,
            ["mask"] = 1,
            ["filter"] = 1,
            ["marker-end"] = 1,
            ["mix-blend-mode"] = 1,
            ["paint-order"] = 1,
            ["vector-effect"] = 1,
            ["transform in a style attribute or sheet"] = 1,
            ["stroke-linejoin: arcs"] = 1,
            ["pattern"] = 1,
            ["paint server in another document"] = 1,
            ["use of an element in another document"] = 1,
        }, ignoreOrder: true);
        // Most frequent first.
        document.Issues[0].ShouldBe(new SvgIssue(SvgIssueKind.Unsupported, "<text> element", 2));
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
        Should.Throw<NotSupportedException>(() => Doc("<text>x</text>", options: strict)).Message.ShouldContain("<text> element");
        Should.Throw<NotSupportedException>(() => Doc("<rect width=\"1\" height=\"1\" filter=\"url(#f)\"/>", options: strict));
        Doc("<rect width=\"1\" height=\"1\" fill=\"bogus\"/><defs><text>x</text></defs>", options: strict)
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
