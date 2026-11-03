# SharpAstro.Gif

Pure-managed GIF decoding for .NET 10, written from the GIF89a specification (GIF87a is its subset).
There is no encoder, by design.

`GifDecoder.Decode` gives a still: the first frame, composited onto the logical screen, as
non-premultiplied RGBA8. `GifDecoder.DecodeAnimation` and `GifAnimationDecoder` give every frame, each as
the whole logical screen after the frame was drawn, with the Graphic Control Extension's disposal applied.
Global and local palettes, interlaced images and the transparent colour index are applied. The raw delay
(1/100 s, 0 stays 0), disposal method, the `NETSCAPE2.0` / `ANIMEXTS1.0` loop count and every frame's
rectangle are reported. Unknown extensions are skipped by their sub-block lengths. The `SharpAstro.Codecs`
facade sniffs `GIF87a` / `GIF89a` and returns the first frame.

```csharp
using SharpAstro.Gif;

byte[] data = File.ReadAllBytes("image.gif");

// One still.
GifImage image = GifDecoder.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}, {image.Info.FrameCount} frame(s)");

// Every frame as its own canvas (refused above 2^28 pixels in all, before anything is allocated).
GifAnimation animation = GifDecoder.DecodeAnimation(data);
foreach (GifAnimationFrame frame in animation.Frames)
{
    Console.WriteLine($"frame {frame.Index}: {frame.DelayCentiseconds} cs, disposal {frame.DisposalMethod}");
}

// Or through one canvas, however many frames: Current.Rgba is that canvas, valid until the next MoveNext.
var stream = new GifAnimationDecoder(data);
while (stream.MoveNext())
{
    Show(stream.Current.Rgba, stream.Current.DelayCentiseconds);
}
```

Compositing follows GIF89a where it is specific and Magick.NET's `Coalesce()` where it is not (and the tests
compare every frame with it): disposal 0, 1 and the reserved 4-7 leave the canvas, 2 clears the frame's
rectangle to alpha 0 (the RGB stays), 3 puts back what the rectangle held before. A frame is disposed before the next
is drawn and the last one's disposal never applies. The canvas before frame 0 is the global table's colour at the
background index (alpha 0 when frame 0 has a transparent index, white without a global table).

Incomplete files decode what is there and say so rather than failing: a truncated LZW stream sets
`IsTruncated` (the unreached pixels keep the canvas as it was), and a missing trailer or bytes after it
show in `GifInfo.HasTrailer` / `GifInfo.UnparsedByteCount`. What is refused, before any raster is
allocated: a logical screen above 67,108,864 pixels, a frame rectangle that is empty or does not lie
inside the screen, an LZW minimum code size outside 2-8, and an animation whose screen × frame count is
above the limit. A frame with no colour table at all reads as an all-black palette, and an index past the end
of its table as opaque black.
