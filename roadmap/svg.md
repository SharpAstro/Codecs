# Roadmap: SVG (`SharpAstro.Svg` on `SharpAstro.Vector`)

**Status: rung 1 shipped in 3.35.** `SharpAstro.Svg` reads paths, basic shapes, transforms, the
viewBox, solid paints and stroke properties into a `SharpAstro.Vector.VectorScene`.

## The design, and why it is not a codec like the others

**SVG is read into a vector scene; the caller draws it.** There is no rasteriser and no font code in this
repo, and `SharpAstro.Svg` is **not registered with the `SharpAstro.Codecs` facade**: the facade's contract is
pixels, and nothing below it can make them from a scene.

That shape is forced by the dependency direction:

```
DIR.Lib ──► Fonts.Lib ──► SharpAstro.Png (Codecs)
   │            │
   │            └──► SharpAstro.Svg ──► SharpAstro.Vector      (planned: OpenType `SVG ` glyphs)
   └──► draws text runs with its own text pipeline             (planned: rung 5)
```

Codecs is the bottom layer. The anti-aliased fill, the stroker and the COLR paint code live in Fonts.Lib,
which already references Codecs, so a rasterising SVG codec here could not use them, and moving them down
would have been a breaking change to Fonts.Lib for no gain. Reading to a scene needs none of it. It also
means **text can never be drawn twice**: a `text` element will be handed to the caller as runs (string, font
properties, positions), and the caller draws them with the text code it already has.

`SharpAstro.Vector` is deliberately not named after SVG. Most vector formats reduce to the same model (paths
with fill and stroke, groups with opacity and clip, images, text runs), so a later reader for another format
can emit the same scene and reuse every renderer written for it.

- `IPathSink` has the same five calls as Fonts.Lib's `IGlyphSink`, and `Rgba32` the same shape as Fonts.Lib's
  `Rgba32`. When Fonts.Lib takes this package it can drop its own copies. (DIR.Lib's `RGBAColor32` names its
  members differently; folding that in is a wider change, not planned here.)
- A scene is flat except where compositing needs a layer: transforms are multiplied out onto each shape, and
  a `VectorGroup` appears only for opacity over more than one paint.
- Paths are absolute move / line / quad / cubic / close. Arcs, circles and rounded corners are cubics.

**Provenance:** written from the W3C SVG 1.1 and SVG 2 specifications (and CSS Color for the colour syntax).
resvg, librsvg and browser sources are not read; renderers are used as programs only.

## Rungs

| Rung | What | State |
|---|---|---|
| 1 | Root size / viewBox / preserveAspectRatio; `g`, `a`; `path`, `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`; `transform`; solid fill and stroke, every stroke property; opacity; `display`, `visibility`; presentation attributes and the `style` attribute; SVGZ; limits | written |
| 2 | `defs`, `use`, `symbol`, nested `svg`; `linearGradient` / `radialGradient` (with `href` inheritance and `gradientTransform`); `<style>` sheets with the selectors real files use | open |
| 3 | `clipPath`, `mask`, `image` (decoded by the caller or the facade), markers, `paint-order`, `pattern` | open |
| 4 | Fonts.Lib reads the OpenType `SVG ` table through this package, and carries the one reference scene rasteriser | open, in Fonts.Lib |
| 5 | `text` / `tspan` as text runs, then per-character positioning, then `textPath` | open |
| 6 | Filters, blend modes | open, only if asked for |

**Not planned until someone asks:** deprecated features (SVG fonts: `<font>` / `<glyph>`, `tref`, `altGlyph`,
the `cursor` element), animation, scripting.

Rung 2 is where amplification starts: `use` can repeat content, so a few hundred bytes can describe a very
large scene. The limits in `SvgReadOptions` (elements, depth, path verbs, characters) are charged against
what a document declares, and the path-verb limit must be charged per `use` expansion, before the expansion
is built.

## Validation

Four layers, in `tests/SharpAstro.Codecs.Tests/Svg*Tests.cs`:

1. **Structural** (`SvgGeometryTests`, `SvgSceneTests`, `SvgReaderTests`): the exact segments, paints,
   transforms and issues a document produces. Curves are checked against the curve they stand for (every
   point of an arc on its ellipse, the turn it makes, the side its sweep flag names).
2. **The colour names against ImageMagick's table**: 145 of 148 agree; the three that do not (`gray`, `grey`,
   `rebeccapurple`) are pinned with ImageMagick's values.
3. **Pixels against ImageMagick's SVG renderer** (`SvgRenderOracleTests`): the structural tests cannot catch a
   misreading of the specification, because the same misreading would be in the expectation. So twelve
   documents are drawn by ImageMagick and by a plain test-only fill rasteriser over our scene, and compared.
   Measured: mean difference 0.08 to 0.44 of 255 (bound 0.8), no pixel further than 72 off. Fills only; the
   test rasteriser has no stroker.

4. **Pixels against a headless browser** (`SvgBrowserOracleTests`, Chrome or Edge through `OracleGate`): the same
   documents plus four ImageMagick cannot be trusted with (`hsl()`, a visible child of a hidden group, the newer
   colour syntaxes, a root sized from its viewBox). One page, one launch, cut into tiles. Measured with Edge 157:
   mean difference 0.00 to 0.80 of 255 (bound 1.2; the largest is 8-bit compositing of a half-transparent layer).
   The browser's own curves are a little coarse (up to 100 levels off the true ellipse at a few pixels where we are
   within 6), so an edge may sit one pixel away; a shape in the wrong place still fails. This is the oracle the later
   rungs are meant to be held against.

**Mutation-checked:** seventeen deliberate bugs, one at a time, all caught: arc sweep inverted, arc radii not
scaled up, a relative arc's end not offset, the transform list applied left to right, nested transforms
multiplied the wrong way, viewBox alignment ignored, slice read as meet, S not mirroring, S and T mirroring a
stale control point, closepath not returning to the subpath start, evenodd read as nonzero, group opacity
dropped, the style attribute losing to the presentation attribute, currentColor resolved where it is written,
skewX using the angle as a factor, a percentage radius taken of the width. The first run missed two (S and T):
the test paths had the control point straight above the end point, so mirroring x changed nothing. Paths for
such tests must be asymmetric.

**Known limits of the oracle.** ImageMagick's renderer does not read `hsl()` and does not draw a
`visibility="visible"` child of a hidden group; both were taken out of the oracle documents and stay covered
structurally. Strokes have no pixel oracle yet (the scene carries the parameters; nothing here draws them).
The browser oracle is the answer to ImageMagick's gaps; text will need a font the page loads itself, or the result
depends on what each machine has installed.

## Refused and reported

- A document that uses an XML entity it declares itself is refused (`InvalidDataException`): DTD processing
  is ignored, so entities are never expanded and nothing outside the document is fetched. Some Adobe
  Illustrator exports rely on entities; reading the internal subset safely (bounded expansion) is open.
- Everything valid but unread is left out of the scene and listed in `SvgDocument.Issues` as `Unsupported`;
  everything the document got wrong is handled as the specification says an error is and listed as `Invalid`.
  `SvgReadOptions.ThrowOnUnsupported` turns the first of the former into an exception.
