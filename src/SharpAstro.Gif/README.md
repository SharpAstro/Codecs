# SharpAstro.Gif

Pure-managed GIF decoding for .NET 10, written from the GIF89a specification (GIF87a is its subset).
There is no encoder.

This release decodes a still: the first frame, composited onto the logical screen, as non-premultiplied
RGBA8. Global and local palettes, interlaced images, and the Graphic Control Extension's transparent
colour index are applied; the rest of the Graphic Control Extension (delay, disposal), the
`NETSCAPE2.0` / `ANIMEXTS1.0` loop count and every frame's rectangle are reported by `ReadInfo`.
Unknown extensions are skipped by their sub-block lengths. The `SharpAstro.Codecs` facade sniffs
`GIF87a` / `GIF89a` and returns that first frame.

```csharp
using SharpAstro.Gif;

byte[] data = File.ReadAllBytes("image.gif");
GifImage image = GifDecoder.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}, {image.Info.FrameCount} frame(s)");
if (image.IsTruncated)
{
    Console.WriteLine("the file ended before the first frame was complete");
}
```

Incomplete files decode what is there and say so rather than failing: a truncated LZW stream sets
`GifImage.IsTruncated` (the unreached pixels keep the canvas colour: the global background, alpha 0 when the frame has a transparent index), and a missing trailer or bytes after it
show in `GifInfo.HasTrailer` / `GifInfo.UnparsedByteCount`. What is refused, before any raster is
allocated: a logical screen above 67,108,864 pixels, a frame rectangle that is empty or does not lie
inside the screen, and an LZW minimum code size outside 2-8. A frame with no colour table at all reads as an all-black palette, and an index past the end of its table as opaque black.
