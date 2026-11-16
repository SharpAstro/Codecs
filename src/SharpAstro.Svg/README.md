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

Written from the W3C SVG 1.1 and SVG 2 specifications.

## What is read

- The root's `width`, `height`, `viewBox` and `preserveAspectRatio`.
- `g` and `a`; `path` (every command, arcs included), `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`.
- `transform` (matrix, translate, scale, rotate, skewX, skewY).
- Solid `fill` and `stroke`: hex, `rgb()`, `hsl()`, named colours, `currentColor`; `fill-rule`; `fill-opacity`,
  `stroke-opacity`, `opacity`; `stroke-width`, `-linecap`, `-linejoin`, `-miterlimit`, `-dasharray`, `-dashoffset`.
- `display` and `visibility`; presentation attributes and the `style` attribute, inherited as CSS says.
- Lengths in px, pt, pc, mm, cm, in, Q, em, ex (taken as half an em) and %.

In the scene every path is absolute move / line / quad / cubic / close (arcs and circles become cubics), every
transform is multiplied out onto its shape, and a group appears only where opacity needs a layer.

## What is not read yet

Each of these is left out of the scene and reported in `SvgDocument.Issues`
(`SvgReadOptions.ThrowOnUnsupported` makes the first one an exception):

- gradients and patterns (the fallback colour after `url(...)` is used, or nothing)
- `use`, `symbol`, nested `svg`, `switch`, `image`, `text`, `foreignObject`
- `clip-path`, `mask`, `filter`, markers, `mix-blend-mode`, `paint-order`, `vector-effect`
- `<style>` sheets and `class` selectors, `transform` written as a CSS property, animation

XML entities are not expanded and nothing outside the document is fetched; a document that relies on an entity
it declares itself (some Adobe Illustrator exports do) is refused with `InvalidDataException`.

## Limits

`SvgReadOptions` bounds the characters of XML (after gzip), the element count, the nesting depth and the total
number of path segments. With nothing here that can repeat content (`use` is not read), a scene's size is
proportional to the document's.
