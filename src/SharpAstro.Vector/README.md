# SharpAstro.Vector

A format-neutral vector scene for .NET, with no dependencies. The namespace is `SharpAstro.VectorGraphics`
(not `SharpAstro.Vector`, which would hide `System.Numerics.Vector` from any consumer in a `SharpAstro.*` namespace).

- `VectorScene`: nodes in painting order, in a y-down coordinate system of a given width and height.
- `VectorShape`: a `VectorPath` with a transform, an optional `VectorFill` and an optional `VectorStroke`.
- `VectorGroup`: children that must be drawn into one layer, which is cut to its `Clip` (a `VectorClip`: a union of
  outlines) and multiplied by its `Mask` (a `VectorMask`: a little scene, by luminance or alpha) when it has them, and
  then put over what is below at an opacity. Before the clip, the layer is replaced by what its `Filter` makes of it (a
  `VectorFilter`: an area and steps such as a blur, an offset, a flood, a merge); and its colour is mixed with the
  colour below by its `Blend` (a `VectorBlendMode`: multiply, screen and the rest).
- `VectorImage`: a raster picture, carried as the file it is (or only named by its address), with the box it goes in
  and how it is fitted. The scene does not decode it.
- `VectorPath` / `PathBuilder` / `IPathSink`: absolute move, line, quad, cubic and close.
- `SolidPaint`, `LinearGradientPaint`, `RadialGradientPaint` (two circles), `PatternPaint` (a repeated tile): what a
  fill or stroke paints with. A gradient carries its stops, its extend mode and a transform from gradient space to
  the shape's coordinates; a pattern carries its tile's content, the tile and such a transform.
- `Rgba32`: an sRGB colour, 8 bits a channel, alpha not premultiplied.

`VectorSceneFile.Write` / `Read` turn a scene into bytes and back: a binary form that loads 15 to 50 times faster than
an SVG is read. It is a bake and cache format, not an interchange format, and it is not reliably smaller than the SVG.
The layout is version 2 since 3.37 (clips, masks, images, patterns); version 1 files are still read.

There is no rasteriser and no font code here. A reader such as `SharpAstro.Svg` produces a scene; the caller
draws it with whatever renderer it has, by replaying each path into its own `IPathSink`.

```csharp
foreach (VectorNode node in scene.Nodes)
{
    if (node is VectorShape shape)
    {
        shape.Path.Replay(mySink);      // geometry, in the shape's local units
        // shape.Transform, shape.Fill, shape.Stroke say how to paint it
    }
    // else a VectorGroup (a layer: Children, Opacity, Clip, Mask, Filter, Blend) or a VectorImage
}
```

A renderer that meets a node or a paint it does not know should say so, not draw something else in its place.
