# SharpAstro.Vector

A format-neutral vector scene for .NET, with no dependencies. The namespace is `SharpAstro.VectorGraphics`
(not `SharpAstro.Vector`, which would hide `System.Numerics.Vector` from any consumer in a `SharpAstro.*` namespace).

- `VectorScene`: nodes in painting order, in a y-down coordinate system of a given width and height.
- `VectorShape`: a `VectorPath` with a transform, an optional `VectorFill` and an optional `VectorStroke`.
- `VectorGroup`: children that must be drawn into one layer and blended at an opacity.
- `VectorPath` / `PathBuilder` / `IPathSink`: absolute move, line, quad, cubic and close.
- `SolidPaint`, `LinearGradientPaint`, `RadialGradientPaint` (two circles): what a fill or stroke paints with. A
  gradient carries its stops, its extend mode and a transform from gradient space to the shape's coordinates.
- `Rgba32`: an sRGB colour, 8 bits a channel, alpha not premultiplied.

`VectorSceneFile.Write` / `Read` turn a scene into bytes and back: a binary form that loads 15 to 50 times faster than
an SVG is read. It is a bake and cache format, not an interchange format, and it is not reliably smaller than the SVG.

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
}
```
