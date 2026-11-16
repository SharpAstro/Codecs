# SharpAstro.Vector

A format-neutral vector scene for .NET, with no dependencies.

- `VectorScene`: nodes in painting order, in a y-down coordinate system of a given width and height.
- `VectorShape`: a `VectorPath` with a transform, an optional `VectorFill` and an optional `VectorStroke`.
- `VectorGroup`: children that must be drawn into one layer and blended at an opacity.
- `VectorPath` / `PathBuilder` / `IPathSink`: absolute move, line, quad, cubic and close.
- `Rgba32`: an sRGB colour, 8 bits a channel, alpha not premultiplied.

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
