# Roadmap: SVG (`SharpAstro.Svg` on `SharpAstro.Vector`)

**Status: rungs 1 and 2 shipped (3.35, 3.36).** `SharpAstro.Svg` reads paths, basic shapes, transforms,
viewports, `use` and `symbol`, solid and gradient paints, stroke properties and style sheets into a
`SharpAstro.Vector.VectorScene`.

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
| 1 | Root size / viewBox / preserveAspectRatio; `g`, `a`; `path`, `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`; `transform`; solid fill and stroke, every stroke property; opacity; `display`, `visibility`; presentation attributes and the `style` attribute; SVGZ; limits | shipped 3.35 |
| 2 | `defs`, `use`, `symbol`, nested `svg`; `linearGradient` / `radialGradient` (with `href` inheritance and `gradientTransform`); `<style>` sheets with the selectors real files use; limits that follow `use` | shipped 3.36 |
| 3 | `clipPath`, `mask`, the clip of nested viewports, `image` (decoded by the caller or the facade), markers, `paint-order`, `pattern`; a stroker in the test rasteriser | open |
| 4a | The one scene rasteriser: `SharpAstro.Fonts.Scenes.SceneRasterizer` (fills, strokes, gradients, layers), held against the browser with strokes | written, Fonts.Lib 1.18 |
| 4b | Fonts.Lib reads the OpenType `SVG ` table through this package | open, in Fonts.Lib |
| D | SVG in DIR.Lib: `IconBaker.Bake(VectorScene, size)`, `VectorIcon` (read once, rasterise once a size), `bake-icons --svg` | written, DIR.Lib 11.70 |
| 5 | `text` / `tspan` as text runs, then per-character positioning, then `textPath` | open |
| 6 | Filters, blend modes | open, only if asked for |

**Not planned until someone asks:** deprecated features (SVG fonts: `<font>` / `<glyph>`, `tref`, `altGlyph`,
the `cursor` element), animation, scripting.

## All the way to DIR.Lib: how an SVG gets drawn, and what it costs

The goal set for this work: SVG reachable in DIR.Lib, considered against the icon baking that is already there, with
performance watched and no XML parsed in a frame.

```
bytes ──SvgReader (Codecs)──► VectorScene ──SceneRasterizer (Fonts.Lib)──► coverage or RGBA
                                   │                                              │
                                   └── kept by VectorIcon (DIR.Lib) ──────────────┴─► kept per size, drawn every frame
```

- **Read once, rasterise once a size, keep both.** `VectorIcon.FromSvg` reads a document and keeps the scene; `Mask(size)`
  and `Image(w, h)` rasterise on first use and keep the result. Drawing from an icon after that is a dictionary lookup
  that allocates nothing (a test draws ten thousand frames to hold it there).
- **It complements icon baking, it does not replace it.** `IconBaker` bakes a glyph to coverage runs drawn as rectangles,
  for three reasons: it works where the font is not installed, the mark is one colour and takes the ink, and it is
  identical on every host. An SVG source has all three and needs no font to contain the mark, so `IconBaker` gained a
  second input and kept its output: `Bake(VectorScene, size)` produces the same runs, drawn by the same
  `DrawCoverageMask`. Glyph baking and the hand-drawn `IconKind` set are untouched.
- **For an app's own icons nothing happens at run time at all.** `bake-icons --svg Name=file.svg` reads and rasterises at
  build time and writes the masks as generated constants.
- **A scene keeps its own box** where a glyph's ink is centred: the icons of a set share one grid (a 24-unit viewBox with
  its own margins), and that grid is what aligns them.

**Measured** (arm64, 2026-11-16; `SvgReadBenchmarks`, `--job short`, before and after in one sitting):

| Read | Time before | Time after | Allocated before | Allocated after |
|---|---|---|---|---|
| A 294-byte outline icon | 4.2 us | 5.1 us | 23.9 KB | 9.8 KB |
| A sprite of 200 symbols and 200 uses, with a style sheet | 1.14 ms | 0.94 ms | 1.82 MB | 1.00 MB |
| A 2 MB drawing of 5,000 paths | 52 ms | 41 ms | 26.7 MB | 11.1 MB |

The allocation figures are exact and are the result: 59%, 45% and 58% less. **The times are not a result.** A short job is
three iterations, and its error bars here are as wide as the differences (the icon's "before" was 4.2 +- 5.1 us), so they
say only that nothing got slower by much. Reading an icon and baking it at 20 px takes 0.25 to 0.5 ms (a stopwatch over 100
runs, in DIR.Lib's tests); drawing from a kept icon is a lookup and allocates nothing.

What the allocation was, in the order it was found:

- **An array for every curve.** `PathBuilder.CubicTo` did `AddRange([c1x, c1y, ...])`, which allocates the collection
  expression's array each call. Six `Add`s instead.
- **A dictionary for every element's attributes.** An element has two or three; they are now a small array searched in order.
- **Two dictionaries keyed by element** (parsed path, cascade result) are now fields on the element.
- **A list pair for every path being built.** One `PathBuilder` is cleared and reused.
- **A fill and a stroke object for every shape**, where a drawing has few and repeats them: the one before is reused when it is the same.
- **The XML reader's buffers**, which were most of an icon's 24 KB. Given a stream that knows its length it sizes them to
  the document, so the byte-array entry no longer wraps its input in a forward-only stream.

What is left is mostly the XML reader's own (its name table, the attribute strings it must hand over as UTF-16) and the
scene itself. A hand-written XML tokeniser would cut more, and is not planned: the reader is meant to run once a document.

**What building it found, in other code:**

- **Fonts.Lib's stroker drew round caps and every join into the stroke.** It had only ever stroked thin SHX pen fonts.
  It was rewritten as a union of convex pieces (a rectangle a segment, a piece a corner, a piece an open end), all wound
  the same way, which has no inside-of-corner case to get wrong. Found by areas worked out by hand, confirmed by the browser.
- **Fonts.Lib's radial gradient projection had a sign wrong**, mirroring every gradient whose circles do not share a
  centre. That is COLR's as well as SVG's: Noto's storm-cloud emoji had lost the glow behind its lightning bolt. Found by an
  SVG focal gradient against the browser, and the emoji then checked against the browser from the same font.
- **`SharpAstro.Vector` could not be a namespace** (see `CLAUDE.md`): it hides `System.Numerics.Vector`.

## A more efficient form than SVG: `VectorSceneFile`

Asked for as an option ("more compact/efficient"). **The choice: a binary form of the scene itself, not another vector
format. Built, and measured: it is the efficient one, and it is not the compact one.**

`VectorSceneFile.Write(scene)` / `Read(bytes)` in `SharpAstro.Vector`: the magic `SAVS`, a version, a table of paths
(equal paths stored once) and the nodes. `SvgReadBenchmarks`, one sitting, `--job short`:

| Document | Read the SVG | Load the scene file | Allocated: SVG | Allocated: scene file |
|---|---|---|---|---|
| Icon | 3.7 to 5 us | 0.25 to 0.30 us | 9.8 KB | 1.09 KB |
| Sprite, 200 symbols and uses | 0.93 ms | 0.029 ms | 999 KB | 86.5 KB |
| Large, 5,000 paths | 41 to 45 ms | 0.9 to 1.2 ms | 11.1 MB | 3.89 MB |

| Document | SVG bytes | Scene file bytes | SVG gzipped | Scene file gzipped |
|---|---|---|---|---|
| Icon | 294 | 469 | 203 | 174 |
| Sprite | 53,268 | 30,138 | 1,930 | 2,289 |
| Large | 2,069,246 | 2,942,524 | 625,004 | 1,111,538 |

So: 15 to 50 times faster to load, and between 0.6 and 1.6 times the size. It is smaller only where shapes repeat; a
coordinate costs four bytes, and SVG text often spends two. A form that was also small would need quantised, variable-length
coordinates (what IconVG and TinyVG do), and is not built.

The bytes are not trusted: every count is checked against the input that is left before anything is allocated for it,
verbs and tags are validated, nesting is limited. The tests cut the file at every length and flip every byte.

A `VectorScene` is already everything SVG is after the reading is done: no XML, no CSS, no units, no `use` left to follow.
Writing it out as versioned binary (verbs, coordinates, paints, transforms) gives a file that loads by copying arrays, at
a few percent of the cost of a read. It is a cache and bake format owned here, like the SDF glyph disk cache in DIR.Lib,
not an interchange format.

- **TinyVG and IconVG** are existing compact binary icon formats. They are worth reading only if files must be exchanged
  with other tools; neither has the tooling SVG has.
- **Lottie** is the JSON one. It is an animation format, verbose for a still picture, and a rung of its own if animated
  icons are wanted.
- **Generated constants** (what `bake-icons` writes) are the most compact of all for an app's fixed icons, because
  nothing is loaded.

In DIR.Lib `VectorIcon.Load` takes either form (it looks at the first four bytes), `VectorIcon.ToSceneFile()` writes
one, and `bake-icons --scenes <dir>` writes one a `--svg`, for an app that wants its drawings in colour and at any size
without reading SVG at start-up.

## What rung 2 settled

- **Amplification is charged by what is drawn.** `use` can repeat content, so forty nested `use` elements (3 KB)
  can ask for 2^40 rectangles. `MaxElements` counts every element visited, each instance included, and an empty
  leaf is no way round it (nothing is drawn, but the walk would still take for ever). `MaxDepth` and
  `MaxPathVerbs` are charged the same way. Path data and point lists are parsed once an element and every
  instance shares the `VectorPath`.
- **Applying a sheet has a budget** (`MaxSelectorTests`): the cost is elements times rules and a document chooses
  both, and one descendant selector that cannot match tries every choice of ancestors (60 choose 4 for one
  element and one rule in the test). An element's matches are kept, so a `use` does not pay again.
- **Selectors see an element where it stands in the document; inheritance comes from the `use`.** `g rect` matches
  a rect in a `g` in `defs` although it is drawn under no `g`.
- **A gradient is resolved against the shape that uses it**, because bounding-box units need the box: the paint
  on a style is a reference until then, and the box is of the curve itself (`VectorPath.TryGetBounds`), not of
  its control points and not of the stroke.
- **Three answers came from the browser, not from reading:** a focal point outside the circle is kept (SVG 2),
  where SVG 1.1 pulled it onto the edge; a `gradientTransform` with no inverse uses the fallback colour; and of
  two groups that use each other, each is drawn once with the other inside it, because a `use` is circular when
  its target is an ancestor of it *or of any use it is inside of*.
- **The clip of a nested viewport is not in the scene yet** (rung 3). It is reported only when content can reach
  outside the viewport, judged by bounding boxes grown by the stroke, so it can be said of a shape that only
  comes close.
- **Stops interpolate in sRGB, not premultiplied**, which is what the browser draws for a stop with
  `stop-opacity="0"` (the scene says so on the paint types).

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
   documents, four ImageMagick cannot be trusted with, and twelve for rung 2. **Rung 2 has no ImageMagick layer:**
   its renderer got ten of those documents wrong where the browser and this reader agree (measured), so without a
   browser rung 2 is covered structurally only. The first four are (`hsl()`, a visible child of a hidden group, the newer
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

Rung 2: thirty-two more (use, viewports, overflow, gradients, cascade, selectors, media). Thirty were caught at
once. Dropping the ancestor check from the cycle test was not, and looking at why showed the reader expanded
mutual uses one level further than a browser does; and a rule about which attributes a gradient inherits had
no test. Not run, deliberately: turning off the element count, which would make the doubling test walk 2^40
elements.

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
