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
/// Still a tolerance, for the same reason as there. Since rung 3 the test rasteriser draws strokes, and the rung 3
/// documents here are the ones that hold them, with clips, masks, markers, patterns and images, against the browser.</para>
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
            foreach ((string name, string root, string body) in Rung3) documents.Add(name, root, body);
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

    /// <summary>
    /// A small PNG as a data URI: 8 x 6 pixels in 2 x 2 blocks of flat colour, one row of them half transparent. Blocks,
    /// so that how each renderer blends between pixels when it enlarges the picture changes edges and not areas.
    /// </summary>
    internal static string Picture()
    {
        const int width = 8, height = 6;
        ReadOnlySpan<byte> palette =
        [
            200, 40, 40, 255, 40, 120, 200, 255, 250, 200, 30, 255, 30, 150, 80, 255,
            120, 40, 160, 255, 240, 240, 240, 255, 20, 20, 20, 255, 240, 130, 30, 255,
            0, 90, 160, 128, 200, 0, 100, 128, 0, 0, 0, 128, 255, 255, 255, 128,
        ];
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                palette.Slice((y / 2 * 4 + x / 2) * 4, 4).CopyTo(rgba.AsSpan((y * width + x) * 4));
            }
        }
        return "data:image/png;base64," + Convert.ToBase64String(SharpAstro.Png.PngWriter.Encode(rgba, width, height));
    }

    /// <summary>Rung 3: strokes (which the test rasteriser draws since this rung), clips, masks, markers, <c>paint-order</c>, patterns and images.</summary>
    internal static readonly (string Name, string Root, string Body)[] Rung3 =
    [
        ("stroke", "width=\"160\" height=\"110\"", """
            <path d="M10 15 H50" stroke="#c00" stroke-width="8"/>
            <path d="M10 30 H50" stroke="#080" stroke-width="8" stroke-linecap="round"/>
            <path d="M10 45 H50" stroke="#00c" stroke-width="8" stroke-linecap="square"/>
            <path d="M70 40 L85 10 L100 40" fill="none" stroke="#444" stroke-width="6"/>
            <path d="M110 40 L125 10 L140 40" fill="none" stroke="#a60" stroke-width="6" stroke-linejoin="bevel"/>
            <path d="M70 80 L85 55 L100 80" fill="none" stroke="#06a" stroke-width="6" stroke-linejoin="round"/>
            <path d="M112 80 L120 52 L128 80" fill="none" stroke="#a0a" stroke-width="6" stroke-miterlimit="2"/>
            <path d="M10 60 C 30 50 40 90 60 70" fill="none" stroke="#333" stroke-width="3" stroke-dasharray="6 3 2 3" stroke-dashoffset="4"/>
            <rect x="10" y="85" width="30" height="16" fill="#fc0" stroke="#036" stroke-width="4" stroke-linejoin="round"/>
            <circle cx="60" cy="95" r="8" fill="none" stroke="#090" stroke-width="2" transform="translate(-40 0) scale(1.8 1)"/>
            <path d="M135 60 l12 0 l0 12 l-12 0 z" fill="none" stroke="#c06" stroke-width="5"/>
            <path d="M135 92 h0" stroke="#000" stroke-width="8" stroke-linecap="round"/>
            <path d="M150 92 h0" stroke="#000" stroke-width="8" stroke-linecap="square"/>
            """),
        ("clip-path", "width=\"170\" height=\"110\"", """
            <defs>
            <clipPath id="c1"><circle cx="30" cy="30" r="20"/></clipPath>
            <clipPath id="c2" clipPathUnits="objectBoundingBox"><path d="M0 0 H1 L0.3 1 Z"/></clipPath>
            <clipPath id="c3"><path d="M110 5 h50 v50 h-50 z M122 12 h26 v26 h-26 z" clip-rule="evenodd"/></clipPath>
            <clipPath id="c4" transform="translate(0 60) rotate(10)"><rect x="12" y="2" width="30" height="20"/><circle cx="50" cy="20" r="12"/></clipPath>
            <clipPath id="c5"><rect x="65" y="62" width="40" height="40" clip-path="url(#c5a)"/></clipPath>
            <clipPath id="c5a"><circle cx="70" cy="70" r="26"/></clipPath>
            <clipPath id="c6" clip-path="url(#c6a)"><rect x="112" y="62" width="50" height="40"/></clipPath>
            <clipPath id="c6a"><ellipse cx="130" cy="82" rx="30" ry="14"/></clipPath>
            </defs>
            <rect x="5" y="5" width="50" height="50" fill="#c33" clip-path="url(#c1)"/>
            <rect x="60" y="8" width="40" height="44" fill="#36c" clip-path="url(#c2)"/>
            <rect x="105" y="0" width="60" height="60" fill="#084" clip-path="url(#c3)"/>
            <g clip-path="url(#c4)"><rect x="0" y="60" width="60" height="50" fill="#fa0"/><circle cx="20" cy="80" r="14" fill="#606" opacity="0.6"/></g>
            <rect x="60" y="58" width="50" height="50" fill="#555" clip-path="url(#c5)"/>
            <rect x="108" y="58" width="60" height="50" fill="#b08" stroke="#000" stroke-width="3" clip-path="url(#c6)"/>
            """),
        ("viewport-clip", "width=\"140\" height=\"70\"", """
            <svg x="5" y="5" width="50" height="40" viewBox="0 0 25 20"><circle cx="20" cy="16" r="10" fill="#c33"/><rect x="-5" y="-5" width="12" height="12" fill="#36c"/></svg>
            <svg x="65" y="5" width="30" height="40" overflow="visible"><circle cx="28" cy="30" r="12" fill="#084"/></svg>
            <defs>
            <symbol id="s" viewBox="0 0 10 10"><circle cx="9" cy="5" r="5" fill="#a0a"/><path d="M0 0 L12 12" stroke="#000" stroke-width="2"/></symbol>
            <path id="q" d="M0 0 h30 l-20 20 z"/>
            <clipPath id="u"><use href="#q" x="4" transform="translate(100 46)"/></clipPath>
            </defs>
            <use href="#s" x="105" y="8" width="28" height="28"/>
            <rect x="95" y="40" width="45" height="30" fill="#f60" clip-path="url(#u)"/>
            """),
        ("mask", "width=\"170\" height=\"110\"", """
            <defs>
            <linearGradient id="fade"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></linearGradient>
            <mask id="m1"><rect x="5" y="5" width="50" height="45" fill="url(#fade)"/></mask>
            <mask id="m2" style="mask-type: alpha"><circle cx="80" cy="28" r="20" fill="#f00" fill-opacity="0.6"/></mask>
            <mask id="m3" maskContentUnits="objectBoundingBox"><rect width="1" height="1" fill="#888"/><circle cx="0.4" cy="0.5" r="0.3" fill="#fff"/></mask>
            <mask id="m4" maskUnits="userSpaceOnUse" x="10" y="62" width="30" height="40"><rect x="0" y="55" width="60" height="55" fill="#fff"/></mask>
            <mask id="m5" x="0" y="0" width="0.6" height="1"><rect x="60" y="55" width="110" height="55" fill="#0f0"/></mask>
            <mask id="m6"><rect x="115" y="60" width="50" height="45" fill="#fff"/><circle cx="136" cy="82" r="12" fill="#000"/></mask>
            </defs>
            <rect x="5" y="5" width="50" height="45" fill="#06c" mask="url(#m1)"/>
            <rect x="60" y="5" width="50" height="45" fill="#080" mask="url(#m2)"/>
            <rect x="115" y="5" width="50" height="45" fill="#c00" mask="url(#m3)"/>
            <rect x="5" y="60" width="50" height="45" fill="#850" mask="url(#m4)"/>
            <rect x="60" y="60" width="50" height="45" fill="#508" mask="url(#m5)"/>
            <g mask="url(#m6)" opacity="0.8"><rect x="115" y="60" width="30" height="45" fill="#f80"/><rect x="135" y="60" width="30" height="45" fill="#08f"/></g>
            """),
        ("markers", "width=\"180\" height=\"120\"", """
            <defs>
            <marker id="arrow" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto"><path d="M0 0 L6 3 L0 6 Z" fill="#c00"/></marker>
            <marker id="dot" markerWidth="4" markerHeight="4" refX="2" refY="2"><circle cx="2" cy="2" r="1.5" fill="#06c"/></marker>
            <marker id="tri" viewBox="0 0 10 10" markerWidth="12" markerHeight="8" refX="0" refY="5" orient="auto-start-reverse" markerUnits="userSpaceOnUse"><path d="M0 0 L10 5 L0 10 Z" fill="#080"/></marker>
            <marker id="big" markerWidth="3" markerHeight="3" refX="1" refY="1" orient="30" overflow="visible"><rect width="3" height="2" fill="#a0a" fill-opacity="0.7"/><rect x="2.5" width="2" height="1" fill="#000"/></marker>
            <marker id="clipped" markerWidth="3" markerHeight="3" refX="1.5" refY="1.5" orient="auto"><circle cx="2.5" cy="1.5" r="1.5" fill="#f80"/></marker>
            </defs>
            <line x1="10" y1="12" x2="60" y2="22" stroke="#333" stroke-width="2" marker-start="url(#dot)" marker-end="url(#arrow)"/>
            <polyline points="80,25 100,8 120,25 140,8 165,20" fill="none" stroke="#333" stroke-width="2" marker-start="url(#arrow)" marker-mid="url(#arrow)" marker-end="url(#arrow)"/>
            <path d="M10 60 C 30 30 50 90 70 55 A 20 12 20 0 1 105 70 Q 120 40 130 62" fill="none" stroke="#555" stroke-width="1.5" marker-start="url(#tri)" marker-mid="url(#dot)" marker-end="url(#tri)"/>
            <path d="M145 40 l25 5 l-10 25 z" fill="#eee" stroke="#333" stroke-width="2" marker-start="url(#arrow)" marker-mid="url(#arrow)" marker-end="url(#dot)"/>
            <path d="M10 100 L40 85 M55 85 L80 105 L100 90" fill="none" stroke="#333" stroke-width="2" style="marker: url(#arrow)"/>
            <polygon points="120,90 140,82 150,105" fill="none" stroke="#333" stroke-width="3" marker-mid="url(#big)" marker-start="url(#clipped)"/>
            """),
        ("paint-order", "width=\"150\" height=\"60\"", """
            <defs><marker id="m" markerWidth="4" markerHeight="4" refX="2" refY="2"><circle cx="2" cy="2" r="2" fill="#000"/></marker></defs>
            <circle cx="25" cy="30" r="18" fill="#fc0" stroke="#036" stroke-width="10"/>
            <circle cx="75" cy="30" r="18" fill="#fc0" stroke="#036" stroke-width="10" paint-order="stroke"/>
            <path d="M105 12 L140 12 L140 45" fill="none" stroke="#c00" stroke-width="6" marker-mid="url(#m)" marker-start="url(#m)" paint-order="markers"/>
            <circle cx="118" cy="40" r="10" fill="#0a0" stroke="#000" stroke-width="6" paint-order="stroke fill" opacity="0.5"/>
            """),
        ("pattern", "width=\"170\" height=\"110\"", """
            <defs>
            <pattern id="p1" width="0.25" height="0.25"><rect width="6" height="6" fill="#c33"/><circle cx="9" cy="8" r="3" fill="#36c"/></pattern>
            <pattern id="p2" patternUnits="userSpaceOnUse" x="3" y="2" width="12" height="10"><path d="M0 0 L12 10" stroke="#084" stroke-width="2"/><rect width="4" height="4" fill="#fa0"/></pattern>
            <pattern id="p3" patternUnits="userSpaceOnUse" width="10" height="10" patternTransform="rotate(30) scale(1.5 1)"><rect width="5" height="10" fill="#606"/></pattern>
            <pattern id="p4" patternUnits="userSpaceOnUse" width="16" height="12" viewBox="0 0 4 4"><circle cx="2" cy="2" r="1.8" fill="#066"/></pattern>
            <pattern id="p5" width="0.5" height="0.5" patternContentUnits="objectBoundingBox"><rect width="0.25" height="0.25" fill="#b08"/><rect x="0.25" y="0.25" width="0.2" height="0.25" fill="#333"/></pattern>
            <pattern id="p6" href="#p2" patternTransform="skewX(20)"/>
            </defs>
            <rect x="5" y="5" width="48" height="48" fill="url(#p1)"/>
            <rect x="60" y="5" width="50" height="48" fill="url(#p2)" stroke="#000"/>
            <circle cx="140" cy="29" r="24" fill="url(#p3)"/>
            <rect x="5" y="60" width="50" height="45" fill="url(#p4)"/>
            <rect x="60" y="60" width="50" height="45" fill="url(#p5)"/>
            <path d="M120 65 h40 v35 h-40 z" fill="#eee" stroke="url(#p6)" stroke-width="8"/>
            """),
        // What the specification leaves open, or says in a way a reader could take two ways, one case a cell (30 x 30,
        // six across). Several are errors, so issues are expected; what matters is what is still drawn.
        ("rung3-edges", "width=\"180\" height=\"150\"", """
            <defs>
            <marker id="ar" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto"><path d="M0 0 L4 2 L0 4 Z" fill="#c00"/></marker>
            <marker id="hid" display="none" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto"><path d="M0 0 L4 2 L0 4 Z" fill="#06c"/></marker>
            <marker id="faint" opacity="0.3" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto"><path d="M0 0 L4 2 L0 4 Z" fill="#080"/></marker>
            <clipPath id="bb" clipPathUnits="objectBoundingBox"><rect width="1" height="1"/></clipPath>
            <clipPath id="bb2" clipPathUnits="objectBoundingBox"><circle cx="0.5" cy="0.5" r="0.5"/></clipPath>
            <clipPath id="loop"><rect width="20" height="20" clip-path="url(#loop)"/></clipPath>
            <clipPath id="withg"><g><rect width="12" height="12"/></g><circle cx="19" cy="19" r="6"/><rect x="14" width="12" height="8" visibility="hidden"/></clipPath>
            <clipPath id="circ"><circle cx="16" cy="14" r="10"/></clipPath>
            <clipPath id="empty"/>
            <mask id="zero" width="0"><rect width="100" height="100" fill="#fff"/></mask>
            <mask id="mm" mask="url(#half)"><rect width="28" height="28" fill="#fff"/></mask>
            <mask id="half"><rect width="14" height="28" fill="#fff"/></mask>
            <mask id="ml"><rect width="20" height="28" fill="#fff" mask="url(#ml)"/></mask>
            <marker id="mloop" markerWidth="6" markerHeight="6" refX="3" refY="3" overflow="visible"><circle cx="3" cy="3" r="2" fill="#c00"/><path d="M0 1 H5" stroke="#00c" stroke-width="1" marker-end="url(#mloop)"/></marker>
            <pattern id="ploop" patternUnits="userSpaceOnUse" width="8" height="8"><rect width="8" height="8" fill="url(#ploop) #c0c"/><rect width="4" height="4" fill="#fc0"/></pattern>
            <mask id="mop" opacity="0.4"><rect width="28" height="28" fill="#fff"/></mask>
            <pattern id="pop" opacity="0.4" patternUnits="userSpaceOnUse" width="8" height="8"><rect width="5" height="5" fill="#900"/></pattern>
            <mask id="mta" mask-type="alpha"><rect width="20" height="28" fill="#000" fill-opacity="0.5"/></mask>
            <rect id="ub" width="20" height="20" fill="#0aa"/>
            <clipPath id="circ2"><circle cx="10" cy="10" r="9"/></clipPath>
            <clipPath id="circ3"><circle cx="7" cy="6" r="5"/></clipPath>
            </defs>
            <g transform="translate(0 0)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker="url(#ar)"/></g>
            <g transform="translate(30 0)"><rect x="2" y="2" width="24" height="24" fill="#c33" clip-path="url(#nope)"/></g>
            <g transform="translate(60 0)"><rect x="2" y="2" width="24" height="24" fill="#36c" mask="url(#nope)"/></g>
            <g transform="translate(90 0)"><path d="M2 14 H26" stroke="#084" stroke-width="6" clip-path="url(#bb)"/></g>
            <g transform="translate(120 0)"><rect x="2" y="2" width="24" height="24" fill="#a0a" clip-path="url(#loop)"/></g>
            <g transform="translate(150 0)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#ar)" visibility="hidden"/></g>
            <g transform="translate(0 30)"><rect x="2" y="2" width="24" height="24" fill="#f80" clip-path="url(#withg)"/></g>
            <g transform="translate(30 30)"><rect x="2" y="2" width="24" height="24" fill="#066" mask="url(#zero)"/></g>
            <g transform="translate(60 30)"><rect x="4" y="4" width="18" height="18" fill="none" stroke="#333" marker-start="url(#ar)" marker-mid="url(#ar)"/></g>
            <g transform="translate(90 30)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#hid)"/></g>
            <g transform="translate(120 30)"><path d="M4 14 H22" stroke="none" stroke-width="3" marker-end="url(#ar)"/></g>
            <g transform="translate(150 30)"><rect x="2" y="2" width="24" height="24" fill="#555" clip-path="url(#ar)"/></g>
            <g transform="translate(0 60)"><rect x="2" y="2" width="24" height="24" fill="#840" mask="url(#mm)"/></g>
            <g transform="translate(30 60)"><svg x="2" y="2" width="24" height="24" clip-path="url(#circ)"><rect width="24" height="24" fill="#c0c"/></svg></g>
            <g transform="translate(60 60)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#faint)"/></g>
            <g transform="translate(90 60)"><rect x="2" y="2" width="24" height="24" fill="#08a" mask="url(#ml)"/></g>
            <g transform="translate(120 60)"><g clip-path="url(#bb2)"><rect x="4" y="4" width="10" height="10" fill="#c33" transform="rotate(20 9 9)"/><circle cx="20" cy="20" r="6" fill="#36c"/></g></g>
            <g transform="translate(150 60)"><rect x="2" y="2" width="24" height="24" fill="#333" clip-path="url(#empty)"/></g>
            <g transform="translate(0 90)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#mloop)"/></g>
            <g transform="translate(30 90)"><rect x="2" y="2" width="24" height="24" fill="url(#ploop) #0a0"/></g>
            <g transform="translate(60 90)"><rect x="2" y="2" width="24" height="24" fill="#036" mask="url(#mop)"/></g>
            <g transform="translate(90 90)"><rect x="2" y="2" width="24" height="24" fill="url(#pop)"/></g>
            <g transform="translate(120 90)"><rect x="2" y="2" width="24" height="24" fill="#036" mask="url(#mta)"/></g>
            <g transform="translate(150 90)"><use href="#ub" x="6" y="4" clip-path="url(#circ2)"/></g>
            <g transform="translate(0 120)"><svg x="2" y="2" width="24" height="24" viewBox="0 0 12 12" clip-path="url(#circ3)"><rect width="12" height="12" fill="#770"/></svg></g>
            """),
        ("image", "width=\"170\" height=\"70\"", $"""
            <rect width="170" height="70" fill="#ddd"/>
            <image x="5" y="5" width="48" height="60" href="{Picture()}"/>
            <image x="60" y="5" width="48" height="60" preserveAspectRatio="xMinYMax slice" href="{Picture()}"/>
            <image x="115" y="5" width="50" height="25" preserveAspectRatio="none" opacity="0.6" href="{Picture()}"/>
            <image width="16" height="12" transform="translate(120 38) rotate(15) scale(2)" href="{Picture()}"/>
            """),
    ];

    /// <summary>Documents that are in error on purpose: their issues are not required to be empty.</summary>
    private static readonly HashSet<string> InError = ["use-cycles", "rung3-edges"];

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
        SvgRenderOracleTests.Compare(name, "browser", document.Scene, width, height, rgb, MeanAllowance.GetValueOrDefault(name, MaxMeanDifference), edgeSlack: true);
    }

    /// <summary>
    /// Of 255, over all pixels and channels. Measured with Edge 157 on Windows: 0.00 to 0.80 over these documents. The
    /// largest is the group-opacity document, where a half-transparent layer over a flat colour comes out one level
    /// apart over a large area (the browser composites in 8 bits).
    /// </summary>
    private const double MaxMeanDifference = 1.2;

    /// <summary>
    /// Documents allowed a larger mean, each with what was measured. Patterns: 1.48 with no pixel plainly wrong. The
    /// browser's tile edges are softer than ours wherever a tile does not start on a pixel (the rotated and the skewed
    /// pattern most), which over a whole area of small tiles adds up; where the shapes are is not in question.
    /// </summary>
    private static readonly Dictionary<string, double> MeanAllowance = new() { ["pattern"] = 2.0 };

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

        if (Environment.GetEnvironmentVariable("SVG_ORACLE_DUMP") is { Length: > 0 } dump)
        {
            // The page the browser is shown, and every document by itself, for opening in a browser with a window.
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, "browser-page.html"), html.ToString());
            foreach (var row in Documents) File.WriteAllText(Path.Combine(dump, row.Data.Item1 + ".svg"), Wrap(row.Data.Item3, row.Data.Item2));
        }

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
