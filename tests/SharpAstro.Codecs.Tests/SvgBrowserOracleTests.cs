using System.Diagnostics;
using System.Text;
using ImageMagick;
using SharpAstro.Svg;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The scene against a browser. Chromium is the renderer most SVG is written for, so it is the stronger of the two
/// pixel oracles: it reads what ImageMagick's renderer does not (<c>hsl()</c>, a visible child of a hidden group), and it
/// is what the later rungs (gradients, <c>use</c>, clipping, text) will have to agree with.
/// </summary>
/// <remarks>
/// <para>Every document is put on ONE page, as an <c>img</c> with a data URI so that it is rendered as a standalone SVG
/// document, and the page is captured in a single headless launch (about a second and a half, however many documents).
/// The capture is cut into one tile a document. Device scale 1 and the sRGB profile are forced, so a tile is the
/// document's pixels.</para>
/// <para>An external program, so it goes through <see cref="OracleGate"/>: skipped where no browser is found, a failure
/// under <c>REQUIRE_ORACLES</c>. ImageMagick (<see cref="SvgRenderOracleTests"/>) stays as the check that needs nothing.
/// Still a tolerance and still fills only, for the same reasons as there.</para>
/// <para><b>The browser's curves are not exact, and that was measured, not assumed.</b> With a plain per-pixel threshold
/// two documents failed on a handful of pixels along tight curves (7 of 14,400 on the rotated arc, 4 of 7,200 on a
/// stretched circle). At each of those pixels the coverage of the true ellipse was computed independently: ours was within
/// 6 levels of it, the browser lighter by 70 to 100. So here a pixel only counts as wrong when it is also outside what
/// the browser has within one pixel of it: an edge may sit a pixel away, a shape in the wrong place still fails.</para>
/// </remarks>
public sealed class SvgBrowserOracleTests
{
    /// <summary>The ImageMagick documents, and those taken out of them because ImageMagick draws them wrong.</summary>
    public static TheoryData<string, string, string> Documents
    {
        get
        {
            var documents = new TheoryData<string, string, string>();
            foreach (var row in SvgRenderOracleTests.Documents) documents.Add(row.Data.Item1, row.Data.Item2, row.Data.Item3);
            documents.Add("hsl", "width=\"90\" height=\"40\"",
                "<rect x=\"5\" y=\"5\" width=\"20\" height=\"30\" fill=\"hsl(300, 60%, 40%)\"/>" +
                "<rect x=\"35\" y=\"5\" width=\"20\" height=\"30\" fill=\"hsl(30deg 100% 50%)\"/>" +
                "<rect x=\"65\" y=\"5\" width=\"20\" height=\"30\" fill=\"hsla(200, 50%, 50%, 0.5)\"/>");
            documents.Add("visible-in-hidden", "width=\"90\" height=\"40\"",
                "<g visibility=\"hidden\"><rect x=\"5\" y=\"5\" width=\"80\" height=\"30\" fill=\"#c00\"/>" +
                "<rect x=\"30\" y=\"12\" width=\"30\" height=\"16\" visibility=\"visible\" fill=\"#06c\"/></g>");
            documents.Add("colour-syntax", "width=\"120\" height=\"40\"",
                "<rect x=\"4\" y=\"4\" width=\"24\" height=\"32\" fill=\"#f80c\"/>" +
                "<rect x=\"32\" y=\"4\" width=\"24\" height=\"32\" fill=\"#0088ff80\"/>" +
                "<rect x=\"60\" y=\"4\" width=\"24\" height=\"32\" fill=\"rgb(0 128 64 / 50%)\"/>" +
                "<rect x=\"88\" y=\"4\" width=\"24\" height=\"32\" fill=\"rebeccapurple\" stroke=\"none\"/>");
            documents.Add("root-defaults", "viewBox=\"0 0 60 30\" width=\"120\"",
                "<rect width=\"60\" height=\"30\" fill=\"#ddd\"/><circle cx=\"30\" cy=\"15\" r=\"12\" fill=\"gray\"/>");
            foreach ((string name, string root, string body) in Rung2) documents.Add(name, root, body);
            return documents;
        }
    }

    private const string XLink = "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    /// <summary>Rung 2: <c>use</c>, <c>symbol</c>, nested <c>svg</c>, gradients and style sheets.</summary>
    internal static readonly (string Name, string Root, string Body)[] Rung2 =
    [
        ("use", $"width=\"120\" height=\"80\" {XLink}",
            "<defs><path id=\"wedge\" d=\"M0 0 L30 5 L10 30 Z\"/>" +
            "<g id=\"pair\"><use href=\"#wedge\"/><use href=\"#wedge\" x=\"20\" y=\"10\" fill=\"#f80\"/></g>" +
            "<rect id=\"box\" width=\"20\" height=\"12\" fill=\"#06c\"/></defs>" +
            // The wedge has no fill of its own, so it takes the fill of whatever uses it.
            "<use href=\"#wedge\" x=\"5\" y=\"5\" fill=\"#c00\"/>" +
            "<use xlink:href=\"#wedge\" transform=\"translate(60 5) rotate(20) scale(1.3)\" fill=\"#080\"/>" +
            "<use href=\"#pair\" x=\"5\" y=\"42\" fill=\"#a0a\"/>" +
            "<use href=\"#pair\" x=\"60\" y=\"42\" fill=\"#555\" opacity=\"0.5\"/>" +
            // x and y move the instance after the use's own transform is applied to it.
            "<use href=\"#box\" x=\"40\" y=\"10\" transform=\"scale(1.5)\"/>"),
        ("symbol", "width=\"140\" height=\"70\"",
            "<defs><symbol id=\"s\" viewBox=\"0 0 20 10\"><rect width=\"20\" height=\"10\" fill=\"#ddd\"/>" +
            "<circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"#c33\"/><rect x=\"12\" y=\"2\" width=\"6\" height=\"6\" fill=\"#36c\"/></symbol>" +
            "<symbol id=\"plain\"><rect x=\"2\" y=\"2\" width=\"20\" height=\"10\" fill=\"#484\"/></symbol></defs>" +
            "<use href=\"#s\" x=\"4\" y=\"4\" width=\"60\" height=\"30\"/>" +
            // A box of another shape: the symbol is fitted and centred, or pushed to a corner, or stretched.
            "<use href=\"#s\" x=\"70\" y=\"4\" width=\"30\" height=\"30\"/>" +
            "<use href=\"#s\" x=\"104\" y=\"4\" width=\"30\" height=\"30\" preserveAspectRatio=\"none\"/>" +
            "<use href=\"#plain\" x=\"4\" y=\"40\"/>" +
            "<use href=\"#plain\" x=\"40\" y=\"40\" width=\"40\" height=\"20\" fill=\"red\"/>"),
        ("nested-svg", "width=\"140\" height=\"70\"",
            "<rect width=\"140\" height=\"70\" fill=\"#eee\"/>" +
            "<svg x=\"10\" y=\"5\" width=\"60\" height=\"60\" viewBox=\"0 0 30 30\">" +
            "<rect width=\"30\" height=\"30\" fill=\"#fc6\"/><rect x=\"25%\" y=\"25%\" width=\"50%\" height=\"50%\" fill=\"#630\"/></svg>" +
            "<svg x=\"80\" y=\"5\" width=\"50\" height=\"60\" viewBox=\"0 0 30 30\" preserveAspectRatio=\"xMinYMax meet\">" +
            "<rect width=\"30\" height=\"30\" fill=\"#6cf\"/><circle cx=\"15\" cy=\"15\" r=\"10%\" fill=\"#036\"/></svg>" +
            "<svg x=\"100\" y=\"5\" width=\"30\" height=\"20\"><rect x=\"10%\" y=\"10%\" width=\"80%\" height=\"80%\" fill=\"#090\"/></svg>"),
        ("linear-gradient", "width=\"170\" height=\"110\"",
            "<defs>" +
            "<linearGradient id=\"a\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"0.5\" stop-color=\"#ff0\"/><stop offset=\"1\" stop-color=\"#00f\"/></linearGradient>" +
            "<linearGradient id=\"b\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"20%\" stop-color=\"#000\"/><stop offset=\"80%\" stop-color=\"#0c8\"/></linearGradient>" +
            "<linearGradient id=\"c\" x2=\"0.25\" spreadMethod=\"reflect\"><stop offset=\"0\" stop-color=\"#036\"/><stop offset=\"1\" stop-color=\"#fc0\"/></linearGradient>" +
            "<linearGradient id=\"d\" x2=\"0.25\" spreadMethod=\"repeat\"><stop offset=\"0\" stop-color=\"#036\"/><stop offset=\"1\" stop-color=\"#fc0\"/></linearGradient>" +
            "<linearGradient id=\"e\" gradientUnits=\"userSpaceOnUse\" x1=\"10\" y1=\"0\" x2=\"160\" y2=\"0\"><stop offset=\"0\" stop-color=\"#f0f\"/><stop offset=\"1\" stop-color=\"#0ff\"/></linearGradient>" +
            "<linearGradient id=\"f\" gradientTransform=\"rotate(45 0.5 0.5)\"><stop offset=\"0.3\" stop-color=\"#fff\"/><stop offset=\"0.3\" stop-color=\"#c00\"/><stop offset=\"0.7\" stop-color=\"#c00\"/><stop offset=\"0.7\" stop-color=\"#008\"/></linearGradient>" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\" stop-opacity=\"0\"/></linearGradient>" +
            "</defs>" +
            "<rect x=\"5\" y=\"5\" width=\"75\" height=\"30\" fill=\"url(#a)\"/><rect x=\"90\" y=\"5\" width=\"75\" height=\"30\" fill=\"url(#b)\"/>" +
            "<rect x=\"5\" y=\"40\" width=\"75\" height=\"30\" fill=\"url(#c)\"/><rect x=\"90\" y=\"40\" width=\"75\" height=\"30\" fill=\"url(#d)\"/>" +
            "<rect x=\"5\" y=\"75\" width=\"45\" height=\"30\" fill=\"url(#e)\"/><rect x=\"120\" y=\"75\" width=\"45\" height=\"30\" fill=\"url(#e)\"/>" +
            "<circle cx=\"68\" cy=\"90\" r=\"14\" fill=\"url(#f)\"/><rect x=\"86\" y=\"75\" width=\"30\" height=\"30\" fill=\"url(#g)\"/>"),
        ("radial-gradient", "width=\"170\" height=\"110\"",
            "<defs>" +
            "<radialGradient id=\"a\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#c00\"/></radialGradient>" +
            "<radialGradient id=\"b\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.3\" fy=\"0.3\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
            "<radialGradient id=\"c\" r=\"0.2\" spreadMethod=\"repeat\"><stop offset=\"0\" stop-color=\"#fc0\"/><stop offset=\"1\" stop-color=\"#060\"/></radialGradient>" +
            "<radialGradient id=\"d\" r=\"0.2\" spreadMethod=\"reflect\"><stop offset=\"0\" stop-color=\"#fc0\"/><stop offset=\"1\" stop-color=\"#060\"/></radialGradient>" +
            "<radialGradient id=\"e\" gradientUnits=\"userSpaceOnUse\" cx=\"30\" cy=\"90\" r=\"25\"><stop offset=\"0\" stop-color=\"#0ff\"/><stop offset=\"1\" stop-color=\"#408\"/></radialGradient>" +
            "<radialGradient id=\"f\" gradientTransform=\"translate(0.5 0.5) rotate(30) scale(1 0.4) translate(-0.5 -0.5)\"><stop offset=\"0\" stop-color=\"#ff0\"/><stop offset=\"1\" stop-color=\"#800\"/></radialGradient>" +
            "<radialGradient id=\"g\" fr=\"0.2\"><stop offset=\"0\" stop-color=\"#000\"/><stop offset=\"1\" stop-color=\"#9cf\"/></radialGradient>" +
            "</defs>" +
            "<rect x=\"5\" y=\"5\" width=\"50\" height=\"50\" fill=\"url(#a)\"/><rect x=\"60\" y=\"5\" width=\"50\" height=\"50\" fill=\"url(#b)\"/>" +
            "<rect x=\"115\" y=\"5\" width=\"50\" height=\"25\" fill=\"url(#c)\"/><rect x=\"115\" y=\"32\" width=\"50\" height=\"23\" fill=\"url(#d)\"/>" +
            "<rect x=\"5\" y=\"62\" width=\"50\" height=\"44\" fill=\"url(#e)\"/><rect x=\"60\" y=\"62\" width=\"50\" height=\"44\" fill=\"url(#f)\"/>" +
            "<rect x=\"115\" y=\"62\" width=\"50\" height=\"44\" fill=\"url(#g)\"/>"),
        ("gradient-inheritance", $"width=\"150\" height=\"80\" {XLink}",
            "<defs>" +
            "<linearGradient id=\"stops\"><stop offset=\"0\" stop-color=\"#103\"/><stop offset=\"0.5\" stop-color=\"#e44\"/><stop offset=\"1\" stop-color=\"#fe8\"/></linearGradient>" +
            // Takes its stops from another gradient, and its own geometry.
            "<linearGradient id=\"down\" href=\"#stops\" x2=\"0\" y2=\"1\"/>" +
            // A radial one takes the stops of a linear one, and through it the spread method of a third.
            "<linearGradient id=\"spread\" xlink:href=\"#stops\" spreadMethod=\"reflect\"/>" +
            "<radialGradient id=\"rings\" href=\"#spread\" r=\"0.25\"/>" +
            // Its own stops win over inherited ones.
            "<linearGradient id=\"own\" href=\"#stops\"><stop offset=\"0\" stop-color=\"#0a0\"/><stop offset=\"1\" stop-color=\"#005\"/></linearGradient>" +
            "</defs>" +
            "<rect x=\"5\" y=\"5\" width=\"65\" height=\"32\" fill=\"url(#stops)\"/><rect x=\"80\" y=\"5\" width=\"65\" height=\"32\" fill=\"url(#down)\"/>" +
            "<rect x=\"5\" y=\"43\" width=\"65\" height=\"32\" fill=\"url(#rings)\"/><rect x=\"80\" y=\"43\" width=\"65\" height=\"32\" fill=\"url(#own)\"/>"),
        ("gradient-edges", "width=\"150\" height=\"50\"",
            "<defs>" +
            "<linearGradient id=\"one\"><stop offset=\"0.5\" stop-color=\"#c60\"/></linearGradient>" +
            "<linearGradient id=\"none\"/>" +
            "<linearGradient id=\"point\" x1=\"0.5\" x2=\"0.5\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#06c\"/></linearGradient>" +
            "<radialGradient id=\"zero\" r=\"0\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#093\"/></radialGradient>" +
            // Offsets that go backwards are pulled up to the one before; ones outside 0..1 are clamped.
            "<linearGradient id=\"order\"><stop offset=\"-1\" stop-color=\"#000\"/><stop offset=\"0.6\" stop-color=\"#fff\"/><stop offset=\"0.3\" stop-color=\"#c0c\"/><stop offset=\"7\" stop-color=\"#0cc\"/></linearGradient>" +
            "</defs>" +
            "<rect x=\"4\" y=\"4\" width=\"24\" height=\"42\" fill=\"url(#one)\"/><rect x=\"32\" y=\"4\" width=\"24\" height=\"42\" fill=\"url(#none) #999\"/>" +
            "<rect x=\"60\" y=\"4\" width=\"24\" height=\"42\" fill=\"url(#point)\"/><rect x=\"88\" y=\"4\" width=\"24\" height=\"42\" fill=\"url(#zero)\"/>" +
            "<rect x=\"116\" y=\"4\" width=\"30\" height=\"42\" fill=\"url(#order)\"/>"),
        ("style-sheet", "width=\"160\" height=\"70\"",
            "<style>" +
            "rect { fill: #999 } .warm { fill: #e70 } #special { fill: #06c } g.group > rect { fill: #080 } " +
            "g.group rect.deep { fill: #808 } [data-kind=\"x\"] { fill: #c00 } rect:first-child { fill: #000 } " +
            "/* a comment { fill: red } */ .loud { fill: #ff0 !important } .warm.cool { fill: #0cc } " +
            "circle, ellipse { fill: #555 } rect + circle { fill: #0a0 } .late ~ ellipse { fill: #a00 } @media print { rect { fill: #f0f } }" +
            "</style>" +
            "<rect x=\"4\" y=\"4\" width=\"20\" height=\"28\"/><rect x=\"28\" y=\"4\" width=\"20\" height=\"28\"/>" +
            "<rect x=\"52\" y=\"4\" width=\"20\" height=\"28\" class=\"warm\"/><rect x=\"76\" y=\"4\" width=\"20\" height=\"28\" class=\"warm\" id=\"special\"/>" +
            "<rect x=\"100\" y=\"4\" width=\"20\" height=\"28\" class=\"cool warm\"/><rect x=\"124\" y=\"4\" width=\"20\" height=\"28\" data-kind=\"x\" fill=\"#fff\"/>" +
            "<g class=\"group\"><circle cx=\"14\" cy=\"52\" r=\"10\"/><rect x=\"28\" y=\"38\" width=\"20\" height=\"28\"/>" +
            "<g><rect x=\"52\" y=\"38\" width=\"20\" height=\"28\" class=\"other\"/><rect x=\"76\" y=\"38\" width=\"20\" height=\"28\" class=\"deep\"/></g></g>" +
            // The style attribute beats the sheet, except where the sheet says !important.
            "<rect x=\"100\" y=\"38\" width=\"20\" height=\"28\" class=\"warm late\" style=\"fill: #333\"/>" +
            "<circle cx=\"134\" cy=\"46\" r=\"7\"/><ellipse cx=\"150\" cy=\"46\" rx=\"5\" ry=\"7\"/>" +
            "<rect x=\"124\" y=\"56\" width=\"32\" height=\"10\" class=\"loud\" style=\"fill: #333\"/>"),
        ("radial-focal", "width=\"170\" height=\"60\"",
            "<defs>" +
            // A focal point outside the circle, on its edge, and a focal radius larger than the radius.
            "<radialGradient id=\"out\" r=\"0.3\" fx=\"0.95\" fy=\"0.5\"><stop offset=\"0\" stop-color=\"#ff0\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
            "<radialGradient id=\"edge\" r=\"0.4\" fx=\"0.9\" fy=\"0.5\"><stop offset=\"0\" stop-color=\"#ff0\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
            "<radialGradient id=\"wide\" r=\"0.2\" fr=\"0.4\"><stop offset=\"0\" stop-color=\"#ff0\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
            "</defs>" +
            "<rect x=\"5\" y=\"5\" width=\"50\" height=\"50\" fill=\"url(#out)\"/><rect x=\"60\" y=\"5\" width=\"50\" height=\"50\" fill=\"url(#edge)\"/>" +
            "<rect x=\"115\" y=\"5\" width=\"50\" height=\"50\" fill=\"url(#wide)\"/>"),
        ("gradient-fallbacks", "width=\"150\" height=\"50\"",
            "<defs>" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/></linearGradient>" +
            "<linearGradient id=\"flat\" gradientTransform=\"scale(1 0)\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/></linearGradient>" +
            "</defs>" +
            // A box with no height: bounding-box units cannot be applied, so the fallback is.
            "<path d=\"M5 10 H70 V10 H5 Z M5 5 h20 v2 h-20 z\" fill=\"url(#g) #0a0\"/>" +
            "<path d=\"M80 20 h60\" fill=\"url(#g) #0a0\" stroke=\"none\"/>" +
            "<rect x=\"5\" y=\"25\" width=\"60\" height=\"20\" fill=\"url(#flat) #c0c\"/>"),
        ("use-and-style", "width=\"160\" height=\"60\"",
            "<style>.mark { fill: #c00 } g .mark { fill: #06c } use.blue { color: #00a } .tint { stop-color: #0a0 }</style>" +
            "<defs>" +
            // Selectors see an element where it stands in the document; what is inherited comes from the use.
            "<g id=\"inner\"><rect class=\"mark\" width=\"14\" height=\"14\"/><rect x=\"16\" width=\"14\" height=\"14\" fill=\"currentColor\"/>" +
            "<rect x=\"32\" width=\"14\" height=\"14\"/></g>" +
            "<svg id=\"view\" viewBox=\"0 0 10 10\" width=\"20\" height=\"20\"><circle cx=\"5\" cy=\"5\" r=\"5\"/></svg>" +
            "<linearGradient id=\"styled\"><stop offset=\"0\" style=\"stop-color: #ff0; stop-opacity: 0.5\"/><stop offset=\"1\" class=\"tint\" stop-color=\"#f0f\"/></linearGradient>" +
            "<linearGradient id=\"current\" color=\"#909\"><stop offset=\"0\" stop-color=\"currentColor\" color=\"#f60\"/><stop offset=\"1\" stop-color=\"#fff\"/></linearGradient>" +
            "</defs>" +
            "<use href=\"#inner\" x=\"4\" y=\"4\" fill=\"#888\" color=\"#fa0\"/>" +
            "<use href=\"#inner\" x=\"4\" y=\"24\" class=\"blue\" fill=\"#333\"/>" +
            // A use of an svg: its own size, then the size the use gives it.
            "<use href=\"#view\" x=\"60\" y=\"4\" fill=\"#084\"/><use href=\"#view\" x=\"84\" y=\"4\" width=\"40\" height=\"30\" fill=\"#840\"/>" +
            "<rect x=\"60\" y=\"40\" width=\"40\" height=\"16\" fill=\"url(#styled)\"/><rect x=\"106\" y=\"40\" width=\"40\" height=\"16\" fill=\"url(#current)\"/>" +
            "<g display=\"none\"><rect id=\"hidden\" width=\"10\" height=\"10\" fill=\"#f0f\"/></g><use href=\"#hidden\" x=\"130\" y=\"4\"/>"),
        // Uses that lead back to themselves. An error, so issues are expected; what matters is what is still drawn.
        ("use-cycles", "width=\"150\" height=\"70\"",
            // Every instance would land somewhere of its own, so what is and is not drawn can be told apart.
            "<g id=\"a\"><rect width=\"10\" height=\"10\" fill=\"#c00\"/><use href=\"#b\" x=\"12\"/></g>" +
            "<g id=\"b\"><circle cx=\"5\" cy=\"30\" r=\"5\" fill=\"#06c\"/><use href=\"#a\" x=\"40\" y=\"25\"/></g>" +
            "<g id=\"self\" transform=\"translate(90 0)\"><rect width=\"10\" height=\"10\" fill=\"#080\"/><use href=\"#self\" x=\"14\"/></g>" +
            "<defs><g id=\"p\"><rect width=\"8\" height=\"8\" fill=\"#a0a\"/><use href=\"#q\" x=\"10\"/></g>" +
            "<g id=\"q\"><rect width=\"8\" height=\"8\" fill=\"#fa0\"/><use href=\"#p\" x=\"10\"/></g></defs>" +
            "<use href=\"#p\" x=\"90\" y=\"40\"/>"),
    ];

    /// <summary>Documents that are in error on purpose: their issues are not required to be empty.</summary>
    private static readonly HashSet<string> InError = ["use-cycles"];

    /// <summary>The page, rendered once for the whole class.</summary>
    private static readonly Lazy<Dictionary<string, (int Width, int Height, byte[] Rgb)>> Rendered = new(RenderAll);

    [Theory]
    [MemberData(nameof(Documents))]
    public void OurScene_DrawnPlainly_MatchesTheBrowsersRenderingOfTheDocument(string name, string root, string body)
    {
        SvgBrowser.Require();
        SvgDocument document = SvgReader.Parse(Wrap(body, root));
        if (!InError.Contains(name)) document.Issues.ShouldBeEmpty(name);

        (int width, int height, byte[] rgb) = Rendered.Value[name];
        ((int)MathF.Round(document.Width), (int)MathF.Round(document.Height)).ShouldBe((width, height), name);
        SvgRenderOracleTests.Compare(name, "browser", document.Scene, width, height, rgb, MaxMeanDifference, edgeSlack: true);
    }

    /// <summary>
    /// Of 255, over all pixels and channels. Measured with Edge 157 on Windows: 0.00 to 0.80 over these documents. The
    /// largest is the group-opacity document, where a half-transparent layer over a flat colour comes out one level
    /// apart over a large area (the browser composites in 8 bits).
    /// </summary>
    private const double MaxMeanDifference = 1.2;

    private static Dictionary<string, (int Width, int Height, byte[] Rgb)> RenderAll()
    {
        // The tiles are stacked down the page, each at a known place. Sizes come from our reading of the root; the test
        // then checks the browser drew the same size by comparing every pixel of the tile, the white around it included.
        var tiles = new List<(string Name, int Y, int Width, int Height)>();
        var html = new StringBuilder("<!doctype html><html><body style=\"margin:0;background:#fff\">");
        int y = 0, pageWidth = 1;
        foreach (var row in Documents)
        {
            (string name, string root, string body) = row.Data;
            string svg = Wrap(body, root);
            SvgDocument document = SvgReader.Parse(svg);
            int width = (int)MathF.Round(document.Width), height = (int)MathF.Round(document.Height);
            html.Append($"<img style=\"position:absolute;left:0;top:{y}px\" src=\"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}\">");
            tiles.Add((name, y, width, height));
            y += height + Gap;
            pageWidth = Math.Max(pageWidth, width + Gap);
        }
        html.Append("</body></html>");

        byte[] page = SvgBrowser.Capture(html.ToString(), pageWidth, y, out int pageHeight);
        pageHeight.ShouldBeGreaterThanOrEqualTo(y);

        var rendered = new Dictionary<string, (int, int, byte[])>();
        foreach ((string name, int top, int width, int height) in tiles)
        {
            // The tile, and a check that the margin right of and below it is untouched white: an image drawn larger
            // than we think the document is would spill into it.
            byte[] rgb = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
            {
                page.AsSpan(((top + row) * pageWidth) * 3, width * 3).CopyTo(rgb.AsSpan(row * width * 3));
            }
            for (int row = 0; row < height + Gap; row++)
            {
                int from = row < height ? width : 0;
                ReadOnlySpan<byte> margin = page.AsSpan(((top + row) * pageWidth + from) * 3, (width + Gap - from) * 3);
                margin.IndexOfAnyExcept((byte)255).ShouldBe(-1, $"{name}: the browser drew outside {width} x {height}");
            }
            rendered[name] = (width, height, rgb);
        }
        return rendered;
    }

    /// <summary>White pixels kept right of and below every tile.</summary>
    private const int Gap = 4;
}

/// <summary>Finds a Chromium-family browser and has it capture a page, headless.</summary>
internal static class SvgBrowser
{
    /// <summary>The variable that names the browser's executable, ahead of the usual places.</summary>
    public const string Variable = "SVG_BROWSER";

    private static readonly Lazy<string?> Path_ = new(Resolve);

    public static void Require() => OracleGate.RequireOrSkip(Path_.Value is not null, "A Chromium-family browser (Chrome or Edge)",
        $"Set {Variable} to its executable, or install Google Chrome or Microsoft Edge.");

    /// <summary>The page as RGB bytes, <paramref name="width"/> wide; the height the browser gave is returned.</summary>
    public static byte[] Capture(string html, int width, int height, out int capturedHeight)
    {
        string browser = Path_.Value ?? throw new InvalidOperationException("No browser.");
        string work = Path.Combine(Path.GetTempPath(), "sharpastro-svg-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string page = Path.Combine(work, "page.html"), shot = Path.Combine(work, "page.png");
            File.WriteAllText(page, html);
            var start = new ProcessStartInfo(browser)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in (string[])
            [
                "--headless=new", "--disable-gpu", "--no-sandbox", "--hide-scrollbars", "--no-first-run",
                "--disable-extensions", "--disable-background-networking",
                "--force-device-scale-factor=1", "--force-color-profile=srgb",
                // A profile of its own, so that a running browser is neither reused nor disturbed.
                $"--user-data-dir={Path.Combine(work, "profile")}",
                $"--window-size={width},{height}",
                $"--screenshot={shot}",
                new Uri(page).AbsoluteUri,
            ])
            {
                start.ArgumentList.Add(argument);
            }

            // The capture is done when the browser says so on stderr ("N bytes written to file ..."), not when it exits:
            // measured here with Edge 157, about one launch in eight writes the whole screenshot and then never exits.
            // So wait for the message or the exit, whichever is first, give a browser that has finished a moment to
            // leave on its own, and end it if it does not. Everything waits on an event or a timer.
            var log = new StringBuilder();
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (log) log.AppendLine(e.Data);
                if (e.Data.Contains("written to file", StringComparison.Ordinal)) written.TrySetResult();
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                Task exited = process.WaitForExitAsync();
                Task.WhenAny(exited, written.Task, Task.Delay(CaptureTimeout)).GetAwaiter().GetResult();
                if (written.Task.IsCompleted) exited.Wait(ExitGrace);
                bool finished = exited.IsCompleted || written.Task.IsCompleted;
                if (!finished || !File.Exists(shot))
                {
                    string said;
                    lock (log) said = log.ToString();
                    throw new InvalidOperationException(
                        $"{browser} made no screenshot ({(exited.IsCompleted ? $"exit code {process.ExitCode}" : $"still running after {CaptureTimeout.TotalSeconds:0} s")}). {said}");
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }

            using var image = new MagickImage(shot);
            ((int)image.Width).ShouldBe(width, "the browser's capture is not as wide as the window that was asked for");
            capturedHeight = (int)image.Height;
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            using IPixelCollection<float> pixels = image.GetPixels();
            return pixels.ToByteArray(PixelMapping.RGB)!;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>How long a capture may take before the browser is given up on.</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a browser that has written its screenshot gets to exit by itself.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);

    private static string? Resolve()
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named)
        {
            // Named explicitly: use it or have nothing, so that a wrong path shows as a missing oracle.
            return File.Exists(named) ? named : null;
        }

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (string? root in (string?[])
                [Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LocalAppData")])
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
        }
        else
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string name in (string[])["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge"])
                {
                    candidates.Add(Path.Combine(directory, name));
                }
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
