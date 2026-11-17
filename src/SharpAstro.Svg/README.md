# SharpAstro.Svg

Reads an SVG document into a resolved `SharpAstro.Vector.VectorScene`. It does not draw: there is no
rasteriser and no font code in this package, and it is not part of the `SharpAstro.Codecs` pixel facade.
The caller renders the scene with whatever it has.

```csharp
SvgDocument document = SvgReader.Read(File.ReadAllBytes("drawing.svg"));   // .svg or .svgz
VectorScene scene = document.Scene;          // pixels at 96 to the inch, viewBox already applied
foreach (SvgIssue issue in document.Issues)  // everything the scene leaves out
    Console.WriteLine($"{issue.Kind}: {issue.What} x{issue.Count}");
```

Written from the W3C SVG 1.1 and SVG 2 specifications, and held against a headless browser.

## What is read

- The root's `width`, `height`, `viewBox` and `preserveAspectRatio`; nested `svg` as a viewport of its own.
- `g` and `a`; `path` (every command, arcs included), `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`.
- `defs`, `use` (with `href` or `xlink:href`) and `symbol`.
- `clipPath` and `clip-path` (both units, `clip-rule`, a clip on a clip), `mask` (both units, `mask-type`), and the
  clip a nested `svg`, a `symbol` or a marker puts round its content.
- Markers (`marker-start`, `-mid`, `-end`, the `marker` shorthand in style sheets; `orient`, `markerUnits`, `refX`,
  `refY`, `viewBox`) and `paint-order`.
- `image`: the picture is carried as the file it is, not decoded. One in a `data:` URI comes with its bytes; one in
  another file comes with its address, and **nothing is fetched**.
- `transform` (matrix, translate, scale, rotate, skewX, skewY).
- `fill` and `stroke`: colours (hex, `rgb()`, `hsl()`, names, `currentColor`), `linearGradient` and
  `radialGradient` (bounding-box and user-space units, `gradientTransform`, `spreadMethod`, focal point and
  radius, stops with `stop-color` / `stop-opacity`, inheritance through `href`), and `pattern` (both units for
  the tile and for its content, `patternTransform`, `viewBox`, inheritance through `href`), with a fallback colour.
- `fill-rule`; `fill-opacity`, `stroke-opacity`, `opacity`; `stroke-width`, `-linecap`, `-linejoin`,
  `-miterlimit`, `-dasharray`, `-dashoffset`; `display`, `visibility`.
- Presentation attributes, the `style` attribute and `<style>` sheets, cascaded as CSS says.
- Lengths in px, pt, pc, mm, cm, in, Q, em, ex (taken as half an em) and %.

In the scene every path is absolute move / line / quad / cubic / close (arcs and circles become cubics), every
transform is multiplied out onto its shape, every `use` is expanded (instances of one `path` share one
`VectorPath`), a gradient or a pattern is a paint with its own transform into the shape's coordinates, a marker is
its content placed on the vertex, `paint-order` is the order of the nodes, and a group appears only where opacity, a
clip or a mask needs a layer. A viewport's clip is only there when its content can reach outside it.

```csharp
foreach (VectorNode node in scene.Nodes)
{
    if (node is VectorImage image)
    {
        // image.Data: the PNG or JPEG file, to decode with SharpAstro.Codecs or anything else.
        // image.Source: the address, when the document only named the picture. Fetching it is your decision.
    }
}
```

### Style sheets

Selectors are built from a type or `*`, `.class`, `#id`, `[attr]` and `[attr=value]` (also `~=`, `|=`, `^=`,
`$=`, `*=`), `:first-child`, `:last-child`, `:only-child`, `:root` and `:link`, joined by the descendant, child
(`>`), next-sibling (`+`) and later-sibling (`~`) combinators. Specificity, source order and `!important` are
honoured. `:hover` and the like never match. `@media` rules with plain media types are decided (`screen` and
`all` apply). Any other selector or at-rule that could change the picture is reported and left out.

## What is not read yet

Each of these is left out of the scene and reported in `SvgDocument.Issues`
(`SvgReadOptions.ThrowOnUnsupported` makes the first one an exception):

- `switch`, `foreignObject`
- in text: vertical writing and text from right to left (drawn as horizontal, left-to-right text), `font-variant`,
  wrapping to a width (`inline-size`), `tref`, `textLength` on a `tspan`, a colour or style of its own for a
  `text-decoration`; a pattern, a clip or a mask in the units of a text's bounding box
- `filter`, `mix-blend-mode`, `vector-effect`
- an `image` with no `width` or `height` (its size would be the picture's own, which is only known once it is
  decoded), and an `image` that is itself an SVG document
- a `clip-path` or `mask` that is not a reference to an element (`inset(...)`, a CSS mask image); text in a `clipPath`
- `context-fill` and `context-stroke` in a marker
- references to elements of other documents; `transform` written as a CSS property; animation
- geometry written as a CSS property (`width`, `height`, `x`, `y`, `cx`, `cy`, `r`, `rx`, `ry` in a sheet or a
  `style` attribute): it is read from attributes only
- an XML entity kept in another file (`SYSTEM`, `PUBLIC`): it is never loaded and stands for nothing

Entities a document declares itself are expanded (some Adobe Illustrator exports write their namespace and every
style as one). Nothing outside the document is fetched.

## Text

`text`, `tspan` and `textPath` become one `VectorText` node: spans in reading order, each a string with the font it
asks for (`VectorFont`: family names, size, weight, slant, stretch), its paints, its anchor, baseline, spacing and
lines, and whatever `x`, `y`, `dx`, `dy` and `rotate` say about single characters. White space is already handled.

**It is not laid out, and no font is read**: this package has none. Whoever draws the scene shapes each span with its
own fonts and places the result as the spans say. `SharpAstro.Fonts` does exactly that (`SceneText`,
`SceneRasterizer` with `SceneRenderOptions.ShapeText`), and `DIR.Lib` supplies the machine's fonts (`SceneFonts`).

## What a document takes from its surroundings

`SvgReadOptions` carries three things a document cannot know by itself. Together they are what a font needs to
draw the glyphs of an OpenType `SVG ` table, and none of them is about fonts:

- `CurrentColor`: what `currentColor` is where the document sets no `color` (the colour of the text around it).
- `CustomProperties`: CSS custom properties, as an ancestor of the root would declare them, for the document's
  `var()` to find. A document's own declarations win.
- `ElementId`: draw only the element with this id, as a `use` of it standing alone in the root. Its own transform and
  properties apply and so do the root's; the groups it stands in give it nothing.

```csharp
var options = new SvgReadOptions
{
    ElementId = "glyph14",
    ViewportWidth = 1000, ViewportHeight = 1000,           // the em square
    CurrentColor = new Rgba32(32, 32, 32),
    CustomProperties = new Dictionary<string, string> { ["--color0"] = "#c02020" },
};
VectorScene glyph = SvgReader.Read(documentBytes, options).Scene;   // a part of a larger array is not copied
```

## Limits

`SvgReadOptions` bounds the characters of XML (after gzip), what its entities expand to, the elements, the
nesting depth, the path segments and the selector tests the style sheets cost. What `var()` puts into property
values is bounded by the character limit too: a custom property can hold another twice. `use` can make a small document describe a very large drawing, so
elements, depth and path segments are counted for every instance a `use` makes: forty lines of nested `use`
elements asking for 2^40 rectangles are refused when the count passes `MaxElements`. The same holds for every
marker on every vertex, every use of a mask or a clip, and every use of a pattern (a pattern is worked out once,
and still counted each time it is used).

An embedded picture is as large as the document makes it, within `MaxCharacters`. The reader does not decode it,
so it has nothing to say about the picture's dimensions: whoever decodes it should apply their own limit.
