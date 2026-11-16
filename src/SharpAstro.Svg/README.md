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
- `transform` (matrix, translate, scale, rotate, skewX, skewY).
- `fill` and `stroke`: colours (hex, `rgb()`, `hsl()`, names, `currentColor`), `linearGradient` and
  `radialGradient` (bounding-box and user-space units, `gradientTransform`, `spreadMethod`, focal point and
  radius, stops with `stop-color` / `stop-opacity`, inheritance through `href`), with a fallback colour.
- `fill-rule`; `fill-opacity`, `stroke-opacity`, `opacity`; `stroke-width`, `-linecap`, `-linejoin`,
  `-miterlimit`, `-dasharray`, `-dashoffset`; `display`, `visibility`.
- Presentation attributes, the `style` attribute and `<style>` sheets, cascaded as CSS says.
- Lengths in px, pt, pc, mm, cm, in, Q, em, ex (taken as half an em) and %.

In the scene every path is absolute move / line / quad / cubic / close (arcs and circles become cubics), every
transform is multiplied out onto its shape, every `use` is expanded (instances of one `path` share one
`VectorPath`), a gradient is a paint with its own transform into the shape's coordinates, and a group appears
only where opacity needs a layer.

### Style sheets

Selectors are built from a type or `*`, `.class`, `#id`, `[attr]` and `[attr=value]` (also `~=`, `|=`, `^=`,
`$=`, `*=`), `:first-child`, `:last-child`, `:only-child`, `:root` and `:link`, joined by the descendant, child
(`>`), next-sibling (`+`) and later-sibling (`~`) combinators. Specificity, source order and `!important` are
honoured. `:hover` and the like never match. `@media` rules with plain media types are decided (`screen` and
`all` apply). Any other selector or at-rule that could change the picture is reported and left out.

## What is not read yet

Each of these is left out of the scene and reported in `SvgDocument.Issues`
(`SvgReadOptions.ThrowOnUnsupported` makes the first one an exception):

- `pattern` paints (the fallback colour after `url(...)` is used, or nothing)
- `image`, `text`, `switch`, `foreignObject`
- `clip-path`, `mask`, `filter`, markers, `mix-blend-mode`, `paint-order`, `vector-effect`
- the clip a nested `svg` or a `symbol` puts round its content, reported only when content really reaches
  outside it
- references to other documents; `transform` written as a CSS property; animation

XML entities are not expanded and nothing outside the document is fetched; a document that relies on an entity
it declares itself (some Adobe Illustrator exports do) is refused with `InvalidDataException`.

## Limits

`SvgReadOptions` bounds the characters of XML (after gzip), the elements, the nesting depth, the path segments
and the selector tests the style sheets cost. `use` can make a small document describe a very large drawing, so
elements, depth and path segments are counted for every instance a `use` makes: forty lines of nested `use`
elements asking for 2^40 rectangles are refused when the count passes `MaxElements`.
