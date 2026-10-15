# SharpAstro.Webp

Pure-managed WebP decoding for .NET 10, derived from RFC 6386 and RFC 9649.
There is no encoder.

Supported input includes VP8 lossy, VP8L lossless, ALPH transparency, and
animated WebP with full-canvas RGBA frames, random seeking, and frame timing.
ICC, EXIF, and XMP metadata are preserved. The `SharpAstro.Codecs` facade
decodes still images and returns frame 0 for animated WebP; use this package's
animation API to access the full timeline. Images larger than 67,108,864 pixels
are rejected to bound decoder allocations.

```csharp
using SharpAstro.Webp;

byte[] data = File.ReadAllBytes("image.webp");
WebpImage image = WebpDecoder.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}");
```

Decode every frame into a full-canvas RGBA buffer:

```csharp
WebpAnimation animation = WebpDecoder.DecodeAnimation(data);
foreach (WebpFrame frame in animation.Frames)
{
    Console.WriteLine($"{frame.TimestampMs} ms: {frame.DurationMs} ms");
    byte[] rgba = frame.Rgba;
}
```

For on-demand playback, seek to the next frame to produce, then decode it:

```csharp
var decoder = new WebpAnimationDecoder(data);
decoder.Seek(12);
byte[] canvas = new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4];
if (decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo frameInfo))
{
    Console.WriteLine($"Displayed frame {frameInfo.Index}");
}
```

The decoder is spec-derived from RFC 6386 / RFC 9649; libwebp/libvpx source was
not consulted — their binaries are used only as test oracles. Written with AI
assistance, whose training data may include libwebp.

Known oracle divergence: `anim-vp8l-alpha-hint.webp` has a VP8L animation frame
with transparent decoded pixels despite a cleared `alpha_is_used` hint. The
RFC 9649 blend flag requires alpha blending, which this decoder follows;
`anim_dump` 1.6.0 emits that frame uncomposited. It is excluded from the
`anim_dump` comparison but remains covered by compositing and
seek-versus-sequential tests.
