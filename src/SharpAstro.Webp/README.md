# SharpAstro.Webp

Pure-managed WebP decoding for .NET 10, derived from RFC 6386 and RFC 9649.
The only encoder is lossless (VP8L): `WebpEncoder.EncodeLossless` / `EncodeLosslessRgb` /
`EncodeLosslessGray`, at its third rung (the four transforms, LZ77 backward references with the 2D distance codes, a colour cache
and meta prefix codes, each chosen per image by exact cost), with optional ICC/EXIF/XMP; `WebpEncodeOptions.Effort` 0 is literal-only, higher trades time for size. There is no lossy encoder.

Encode, at an effort from 0 (literal-only, fastest, largest) to 9 (slowest, smallest); the default is 6:

```csharp
byte[] file = WebpEncoder.EncodeLossless(rgba, width, height, 0, new WebpEncodeOptions { Effort = 6 });
```

Measured against `cwebp -lossless` (libwebp 1.6.0) on 1080p inputs: effort 6 is 1.0-1.1x the size of cwebp's
default on photographic images and 0.84x on a screenshot, and takes about 6.5 s a photo against cwebp's 1.1 s
(6-13x slower). Effort 0 takes 30-45 ms and is 2-13x larger. Effort 9 is within 1% of `cwebp -z 9` on a photo.
Size is not monotone in effort, so measure your own content if the last few percent matter. The
tables, and the benchmark and report that produce them, are in the repository's `CODECS.md`.
Every effort is bounded: the match finder's chain depth (2 to 512) and nice length (16 to 4096) are fixed per
effort, so a one-colour or all-distinct image costs no more per pixel than a photo; the working memory is
charged up front against a 1 GiB ceiling; dimensions over 16384, or over 2^26 pixels, are refused before
anything is allocated. A few seconds per megapixel at effort 6 and 20-30 s per 1080p image at effort 9 is what to budget.

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
