# Changelog

Release notes for the whole family: every package in this repo (the `SharpAstro.Codecs` facade plus
`SharpAstro.Tiff` / `.Png` / `.Exif` / `.Color.Icc` / `.Jpeg` / `.Jpeg.GainMap` / `.Jxr` / `.Exr` /
`.Jxl` / `.Jbig2` / `.Jpeg2000` / `.Webp` / `.Radiance` / `.Pnm` / `.Tga` / `.Bmp`) ships at the same `Major.Minor` with a shared run-number patch, so one section
covers them all.

The number itself lives in exactly one place, `<VersionMajorMinor>` in `Directory.Build.props`; CI
reads it back and appends the run number. Bumping a release is editing that one line and adding a
section here. Newest first.

This file was created at 3.10 by migrating the note trail out of the two comment blocks that carried
it -- the `env:` block in `.github/workflows/dotnet.yml` (35 lines of prose above a variable that no
longer holds the number) and the header comment in `Directory.Build.props`. Entries below 3.10 are
those notes, verbatim in substance. 3.4 has no recorded note; it was never written down.

## 3.32

**`SharpAstro.Webp` gains a lossless (VP8L) encoder** (Codecs #57), measured against `cwebp -lossless`. (3.31 is the GIF
reader's number; this release takes 3.32 so the two do not collide.) The encoder is spec-derived from RFC 9649 alone, with
`cwebp` / `dwebp` 1.6.0 as black-box oracles and no libwebp source read.

- **API:** `WebpEncoder.EncodeLossless` (RGBA8), `EncodeLosslessRgb`, `EncodeLosslessGray`, with an optional stride and
  `WebpEncodeOptions` (`Effort` 0-9, default 6; ICC / EXIF / XMP). The output always decodes to exactly the input pixels, colour
  under transparent pixels included. Dimensions over 16384 or 2^26 pixels are refused before anything is allocated.
- **Three rungs, each chosen per image by exact cost:** literal-only prefix codes (Effort 0); LZ77 backward references with the 2D
  distance codes and a colour cache; the four transforms (subtract green, predictor, cross colour, colour indexing) with meta prefix
  codes, the untransformed image always a candidate from Effort 3.
- **Bounded on hostile input:** chain depth (2-512) and nice length (16-4096) are fixed per effort, and the plan charges its working
  bytes up front against a 1 GiB ceiling (shrinking the window, then dropping backward references). Pinned by
  `WebpLosslessEncoderPathologicalInputTests`: over-size refusal allocates under 64 KiB, and all-distinct, one-colour and
  short-period 1024x1024 images round-trip at efforts 0, 6 and 9 within a timeout and an allocated-bytes-per-pixel bound (the
  all-distinct image, the worst, allocates 14.6 / 859 / 1,375 bytes a pixel over a whole encode at effort 0 / 6 / 9; the bound
  counts cumulative allocation, which is above what is live).
- **Measured in one sitting** (`--webp-encode-report`: every output decoded exactly by our decoder and by `dwebp`; and
  `WebpEncodeBenchmarks`, BenchmarkDotNet). Size in bytes, ratio to `cwebp` at its default in brackets:

  | 1080p image | e0 | e3 | e6 | e9 | cwebp | cwebp -z 9 |
  |---|---:|---:|---:|---:|---:|---:|
  | photo (generated) | 5,764,676 (2.34) | 3,419,704 (1.39) | 2,493,350 (1.01) | 2,442,902 (0.99) | 2,458,700 | 2,420,600 |
  | screenshot (generated) | 1,840,078 (5.23) | 331,212 (0.94) | 295,052 (0.84) | 280,090 (0.80) | 352,006 | 327,912 |
  | lossless-1080p | 1,664,148 (12.69) | 146,992 (1.12) | 147,888 (1.13) | 132,306 (1.01) | 131,186 | 131,752 |
  | lossy-opaque-1080p | 2,054,058 (7.50) | 304,750 (1.11) | 285,248 (1.04) | 282,188 (1.03) | 274,018 | 273,488 |
  | lossy-alpha-1080p | 2,054,058 (7.49) | 316,544 (1.15) | 295,830 (1.08) | 283,060 (1.03) | 274,182 | 273,062 |

  The fifteen corpus images (16x16 to 1080p) are all in `CODECS.md`, at efforts 0, 3, 6 and 9; at effort 6 the small ones are 0.33-1.08 of `cwebp`'s default (`lossless-palette-16`
  0.77 and `lossless-exact-alpha` 0.33, the 17x17 `lossless-near60` 1.08). Encode time, 1080p (BenchmarkDotNet steady state / the report's cold
  single run / `cwebp` default / `cwebp -z 9`):

  | 1080p image | e0 | e3 | e6 | e9 | cwebp | cwebp -z 9 |
  |---|---:|---:|---:|---:|---:|---:|
  | photo | 35.5 ms / 82 ms | 1.19 s / 5.4 s | 6.53 s / 13.8 s | 24.8 s / 55.3 s | 1.1 s | 77.3 s |
  | screenshot | 28.5 ms / 43 ms | 0.82 s / 1.4 s | 4.82 s / 10.7 s | 30.6 s / 41.1 s | 0.61 s | 12.5 s |
  | lossless-1080p | 44.5 ms / 51 ms | 0.92 s / 1.3 s | 6.37 s / 8.5 s | 13.4 s / 22.7 s | 0.75 s | 4.2 s |

  Effort 6 is 1.0-1.1x `cwebp`'s default size on photographic content (1.13 on `lossless-1080p`, where size is not monotone in effort) and smaller on screenshots and palette images, at 6-13x
  its time. Effort 9 is within 1% of `cwebp -z 9` on the photo. Size is not monotone in effort (the photo is 1.39 at effort 3,
  1.29 at 4, 1.02 at 5). BenchmarkDotNet's `Error` at effort 6+ is wider than the gap between neighbouring efforts, so read those
  rows to a factor of about 1.5; the report's first images pay JIT.
- **Allocation, 1080p (BenchmarkDotNet, cumulative):** effort 0 14 MB, effort 3 80-97 MB, effort 6 380-715 MB, effort 9 2.5-5.5 GB.
- **Tools:** `dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks -- --webp-encode-report` (about 11 minutes in
  full; options in `CLAUDE.md`) and `-- --filter '*WebpEncodeBenchmarks*'` (about 18 minutes).

## 3.30

**New package `SharpAstro.Bmp`: BMP read and write, registered with the facade** (issue #53, stb_image parity). BMP was one
of the stb-only formats the family lost when `SharpAstro.StbImage` was removed; it is back, written from the Microsoft
DIB header documentation rather than ported, and checked pixel by pixel against ImageMagick.

- **`BmpDecoder`** reads 12/16/40/52/56/64/108/124-byte headers; 1/4/8-bit indexed, 16-bit (5-5-5 or any `BI_BITFIELDS`
  masks), 24 and 32-bit pixels; `BI_RGB`, `BI_RLE8`, `BI_RLE4`, `BI_BITFIELDS` and `BI_ALPHABITFIELDS`; bottom-up and
  top-down rows; a V5 embedded ICC profile (reported, never applied). Output is 8-bit RGB, or RGBA when the file has
  alpha. Offsets, palette size and pixel data are checked against the file, a header declaring more than 2^28 pixels is
  refused before allocating, and `BI_JPEG`, `BI_PNG`, `BI_CMYK*`, OS/2 Huffman 1D and RLE24 throw `NotSupportedException`
  by name.
- **Mask channels** of 8 bits or fewer are expanded by bit replication, as stb_image does (a 5-bit 24 reads as 198, a full
  channel of any width as 255); wider ones round to nearest from their top 16 bits, which is ImageMagick's arithmetic. Every
  8-bit and wider channel equals ImageMagick exactly. Below 8 bits ImageMagick departs from replication on a few values (5-bit
  3 and 7, eight values of a 6-bit green) and only shifts other narrow channels (4-bit 15 is 240, 1-bit alpha 1 is 128); the
  tests pin each of those value by value, over every 16-bit pixel word, instead of allowing a tolerance.
- **`BmpEncoder`** (`EncodeRgb24`, `EncodeRgba32`, each with an optional input stride) writes 24-bit BGR with a 40-byte
  `BITMAPINFOHEADER`, and 32-bit BGRA with a 108-byte V4 header whose `BI_BITFIELDS` masks include `0xFF000000` for alpha,
  so alpha survives in readers that honour the masks. Rows are bottom-up and padded to four bytes. Width, height, stride
  and input length are validated before anything is allocated. ImageMagick reads the output back sample for sample, alpha
  included, at odd widths too, and `BmpDecoder` round-trips it exactly.
- **Facade:** `BmpImageDecoder : IImageDecoder` is registered in `ImageCodecs`. It sniffs `BM` plus a DIB header size this
  decoder knows and the zeroed reserved words (two letters alone are too weak a magic), and answers `TryReadInfo`,
  `TryDecode` (RGB8 or RGBA8, ICC passed through) and `TryDecodeIntoRgba8` (opaque images get alpha 255). Refused or
  malformed files answer false rather than throw.

## 3.29

**`SharpAstro.Radiance`: Radiance RGBE (`.hdr` / `.pic`) read and write to scene-linear float, registered with the facade.**
New package, the answer to Codecs #56 (stb_image HDR parity; `SharpAstro.StbImage` was removed in an earlier release and took
its HDR path with it).

- **`RadianceFile.Read` / `Write` / `ReadInfo`.** Magic `#?RADIANCE` or `#?RGBE`; `FORMAT=32-bit_rle_rgbe` and
  `32-bit_rle_xyze`; `EXPOSURE=` lines multiplied and *reported* (`RadianceImage.Exposure`), never applied; new-style RLE, old
  flat and old RLE scanlines on read; new-style RLE on write (flat below width 8 and above 32767). `ReadInfo` parses the header
  only and allocates no pixels. Anything else is refused by name with `InvalidDataException`: another `FORMAT`, an orientation
  other than `-Y h +X w`, a run or literal overflowing its scanline, a width that disagrees with the scanline's own, truncation,
  a repeat with no previous pixel. The pixel ceiling (2^28 by default, a parameter) is charged from the header before anything
  is allocated, because an RLE scanline can describe far more pixels than it has bytes.
- **Value convention, measured not assumed.** `mantissa * 2^(e - 136)`, no `+ 0.5` (Radiance's own `colr_color` adds one;
  ImageMagick and stb_image do not), exponent byte 0 is black. Decoding is exact in `float`, so it is lossless with respect to
  the RGBE bytes. Validated **value-exact in both directions against Magick.NET** with no skips: ImageMagick-written files
  decode to the same floats here, and files written here decode to the same floats in ImageMagick.
- **`RadianceImageDecoder` in the facade.** `ImageCodecs.TryDecode` gives `Float32` x 3 with `Transfer = Linear` and
  `Float = SceneReferred`. An XYZE file is tagged `ColorPrimaries.SMPTE428` (CIE XYZ) and is not converted, so its samples are
  never mistaken for RGB. `TryDecodeIntoRgba8` is always false (projecting scene-referred float to 8 bits is the consumer's
  tone/stretch policy, as for EXR). `TryReadInfo` reads the header only. The adapter refuses a picture over 2^26 pixels, tighter
  than `RadianceFile.Read`, because the facade's raster is a byte array with a copy of the floats; call `RadianceFile.Read`
  directly for larger ones.
- **Mutation-checked**, one deliberate bug at a time, all caught: exponent bias `e - 135`, no new-RLE run overflow check, old-RLE
  shift `+= 4`, no shift reset after a plain pixel, old-RLE shift wrapping mod 32, writer rounding instead of truncating, a
  zero-exponent pixel not advancing the output index.


**New package `SharpAstro.Pnm`: Netpbm (PBM / PGM / PPM / PAM) read and write.** Additive. Written from the Netpbm format
documents and validated against Magick.NET in both directions (stb_image parity: PNM and PAM were the common formats the
family could not read).

- **`PnmReader.Read` / `ReadHeader`** reads every magic `P1`-`P7`, plain and raw, `maxval` 1..65535 (two bytes, big-endian,
  above 255), `#` comments wherever whitespace is allowed, and PAM `BLACKANDWHITE` / `GRAYSCALE` / `RGB` and their `_ALPHA`
  forms, as the file's native samples. A sample above `maxval`, a truncated raster or an out-of-range header throws
  `InvalidDataException`; a declared size over 2^28 samples is refused before anything is allocated.
- **The facade reads it** (`ImageCodecs.TryDecode`, sniffed by `P1`-`P7` plus whitespace), scaling samples to fill UInt8 or
  UInt16 with ImageMagick's rounding. Where ImageMagick departs from the Netpbm documents (raw `maxval` 128..254 and
  32768..65534 read unscaled, a 1-bit grey+alpha PAM's alpha inverted) the documents win, and the tests say so.
- **`PnmWriter.Encode` / `Write`** (to `byte[]` or a `Stream`, from `byte` or `ushort` samples) writes raw `P5` / `P6` and `P7`
  PAM for grey, grey + alpha, RGB and RGB + alpha, at any `maxval` 1..65535, with Netpbm's own header layout. Alpha images are
  always PAM. Samples are native values, never scaled; a short buffer, an unwritable tuple type, a `maxval` out of range or a
  sample above it throws `ArgumentException`.
- **Validated by two readers**: Magick.NET reads our `maxval` 255 and 65535 output to the source pixels exactly for every
  writer variant, and `PnmReader` round-trips them (and odd maxvals such as 1, 15, 1023, 32768) exactly.
**New package `SharpAstro.Tga`: Truevision TGA read and write, and the facade recognises it by its TGA 2.0 footer** (issue #54,
the first step of stb_image parity: the stb port removed earlier took TGA with it).

- **Reader (`TgaReader`).** Image types 1/2/3 and their RLE forms 9/10/11; 8/15/16/24/32-bit pixels; 8/15/16/24/32-bit
  colour-map entries; alpha from 32-bit pixels or the descriptor's attribute bits; both vertical origins and the right-to-left
  bit; image ID skipped; output tightly packed top-down RGBA8. Every pixel is compared against Magick.NET's read of the same
  file. Refused with `InvalidDataException` rather than clamped: truncation, an RLE packet running past the end of the image, an
  RLE stream ending early, unknown types and depths, out-of-map colour indices and more than 2^26 declared pixels (checked
  before anything is allocated).
- **Writer (`TgaWriter`).** 24-bit and 32-bit true colour, raw (type 2) or RLE (type 10), either origin, always followed by the
  TGA 2.0 footer. Read back exactly by Magick.NET and by our reader.
- **Facade: footer only, never a header guess.** `TgaImageDecoder` is registered last in `ImageCodecs`. A TGA has no magic
  number, so its header cannot be told from arbitrary bytes. The adapter claims a stream only when the last 18 bytes are
  `TRUEVISION-XFILE.\0` **and** the header parses as a TGA the reader accepts. A TGA 1.0 file has no footer, so the facade does not
  recognise it (Magick.NET writes such files); it still decodes through `TgaReader.Decode`, and appending the 26-byte footer makes
  it a file the facade takes. A footered but corrupt file is claimed and then declined: the `Try` methods return false, never throw.
  Opaque TGAs decode as RGB8, those with alpha as RGBA8.
- `SharpAstro.Codecs` now references `SharpAstro.Tga`, so TGA is in its description and tags. 3.28 was already on nuget.org, so this
  ships as 3.29.

Tests: `TgaFacadeTests` (footered files through `TryDecode` / `TryReadInfo` / `TryDecodeIntoRgba8` equal `TgaReader`; footerless
files and random bytes are not recognised; corrupt footered files return false), alongside the reader, writer and refusal tests.

## 3.28

**`SharpAstro.Png`'s APNG compositing allocates a fraction of what it did, and is 6-16% faster.** No API change. The
flight that added `PngReader.EnumerateFrames` / `DecodeFrames` shipped no entry of its own, so this block carries its notes
(it was first written under a 3.25 heading, before Adam7, the float projection and the JPEG region decode took 3.25 to 3.27).
It ships with 3.28. An Adam7 interlaced APNG de-interlaces each frame as the IHDR says
(`PngImage.SourceInterlaceMethod`), through the same reused-buffer path.

Measured with BenchmarkDotNet (`ApngCompositeBenchmarks`, `MemoryDiagnoser`, whole decodes through the public API), old and
new code run back to back in one sitting. Eight 1080p frames (a canvas is 7.9 MB of RGBA8), sixty 320x180 sprite frames
on a 640x360 canvas (0.9 MB):

| Fixture | Stream, before | Stream, after | Materialise, before | Materialise, after |
|---|---|---|---|---|
| 1080p x8, OVER, mixed alpha | 361.7 ms, 134.5 MB | **308.3 ms, 23.7 MB** | 374.1 ms, 227.0 MB | 322.6 ms, 87.0 MB |
| 1080p x8, OVER, half partial alpha | 446.4 ms, 134.5 MB | **374.6 ms, 23.7 MB** | 476.5 ms, 235.8 MB | 385.5 ms, 87.0 MB |
| 1080p x8, SOURCE, RGBA8 | 342.4 ms, 134.5 MB | **304.9 ms, 23.7 MB** | 343.0 ms, 227.0 MB | 320.3 ms, 87.0 MB |
| 1080p x8, SOURCE, RGB8 | 432.8 ms (noisy), 118.7 MB | **297.8 ms, 19.8 MB** | 329.9 ms, 206.5 MB | 302.3 ms, 83.1 MB |
| 60 sprite frames, OVER, dispose cycling | 111.1 ms, 28.9 MB | **104.4 ms, 2.2 MB** | 119.3 ms, 88.2 MB | 106.5 ms, 55.0 MB |

What was done, and what each part is worth:

- **Buffers are reused, not allocated per frame.** The old compositor inflated each frame into a fresh sample array and expanded
  it to a second fresh RGBA8 array, per frame (two frame-sized arrays, zeroed, on the large-object heap, hence the Gen2
  collections in every row of the old table). Now one grow-only samples buffer is inflated into
  (`DecodeFrameSamplesInto`), the PREVIOUS snapshot and the row scratch are grow-only too, the unfilter's zero row is
  rented, and `DecodeFrames` drives the compositor from the caller's span instead of copying the input first. Streaming now
  holds the canvas, one samples buffer, the IDAT image `Decode` already produced and a snapshot when a frame disposes to
  PREVIOUS: 23.7 MB for 1080p (three 7.9 MB buffers: canvas, IDAT image, samples) against 134.5 MB, no Gen0/1/2 collection
  recorded, and the 60-frame sprite case is 2.2 MB, which is its four fixed buffers and nothing per frame that the
  allocator rounds up to a measurable figure (the inflater's own managed objects are the only per-frame allocation left).
  `DecodeFrames` costs its frames (canvas x count, exactly what the limit charges) plus the streaming footprint: 60 frames of
  0.88 MB is 55.0 MB where it was 88.2 MB.
- **RGBA8 frames are blended where they lie.** No expanded copy: the inflated samples are the pixels. Other colour types go
  through `PngImage.ExpandRowToRgba8`, one row at a time with the colour type decided once per row rather than once per pixel.
- **OVER over an opaque canvas takes an exact shortcut.** With the canvas alpha at 255 the rational formula reduces to
  `(af*cf + (255-af)*cb + 127) / 255` with the alpha staying 255, so the three divisions by the variable alpha numerator
  become divisions by a constant. `PngApngKernelTests` checks it against the general formula for all 255 x 256 x 256 inputs.
- **A `Vector128` OVER (no arch-specific intrinsics, scalar fallback kept) wins, modestly, and stays.** Four pixels at a
  time: a whole group of opaque source pixels is a copy, a whole group of transparent ones over a canvas with no
  transparent pixel is a no-op, a group over four opaque canvas pixels is blended in 16-bit lanes with an exact division by 255
  (`(t + 1 + (t >> 8)) >> 8`, exact for every t the blend produces), and any other group falls back to the scalar per-pixel
  code. Same file, same sitting, vector against `ForceScalar`: mixed alpha 321.5 -> 307.9 ms (-4.2%), half partial alpha
  410.8 -> 385.4 ms (-6.2%; its error bar overlaps, so read it as about 5%). A small win because a frame's time is
  almost entirely inflate and unfilter (see below); it stays because it is exactly equal to the scalar path and costs 60 lines.
  Over with partial alpha costs about 70 ms more than SOURCE for eight 1080p frames, down from about 104 ms.
- **Checked and left alone:** the SOURCE copy, the BACKGROUND clear and the PREVIOUS snapshot/restore are `Span.CopyTo` / `Clear`
  per row, already memmove; a vector loop cannot beat them. With SOURCE at 305 ms for eight frames, about 38 ms a frame,
  almost all of it is inflate plus unfilter, the part of `PngReader` that the still-image decode shares, not the compositing.
  The RGB, grey and palette expansion to RGBA8 (`ExpandRowToRgba8`) was checked the same way and left scalar: SOURCE RGB8 at 297.8 ms
  against RGBA8 at 304.9 ms for eight 1080p frames shows the expansion is negligible next to inflate.
  `PngPredictor`'s filters were not touched here: Up could be vectorized, but Sub, Average and Paeth are sequential, so it would
  be a still-image decode change with a smaller reach than its effort, and it is not measured.

Tests: `PngApngKernelTests` pins the vector OVER byte-equal to the scalar one (every alpha x colour on an opaque canvas, random rows
of every four-pixel-group class and tail length, random multi-frame animations with every dispose and blend op), runs every Pillow
fixture frame through both paths against the reference digests, checks `ExpandRowToRgba8` against a per-pixel reading of its mapping,
bounds steady-state streaming allocation below a third of a frame per frame, and keeps `DecodeFrames`' limit honoured.

## 3.27

**`SharpAstro.Jpeg` decodes a region, and decodes straight to a target size** (issue #28), and the decoder no longer
holds whole component planes to do either. A 5100x6600 scan used to cost three 64 MiB planes before its first pixel was
converted; every decode now holds about 0.19 MiB of sample memory, whatever the height.

- **`JpegDecoder.DecodeRegionTo(data, JpegRegion region, rgbaDestination, scale)`** decodes one rectangle of the output
  (in pixels at `scale`) into `region.Width * region.Height * 4` bytes. With restart markers (DRI) it entropy-decodes only
  the restart intervals holding the region's MCU rows; without them it decodes from the top of the scan to the region's
  last MCU row and stops; progressive reads every scan but transforms only the region.
- **`JpegDecoder.DecodeFittedTo(data, targetWidth, targetHeight, rgbaDestination, scale, region)`** box-filters to the
  target size inside the row loop, so the full-resolution RGBA never exists. A target larger than the source is an
  `ArgumentException`, not an approximation. With `region` it fits just that rectangle.
- **Exactness.** Both are byte for byte what `Decode` gives (cropped, or box-filtered), at every scale, not "within 1".
  The region is the same engine narrowed (`SetRegion`), not a second one; the fitted decode is a row sink on that engine.
  The filter is the Drawboard viewer's `RgbaBoxDownsample.Shrink`, line for line, with `long` sums where Shrink's are
  `int` (Shrink wraps for a box of more than about 8.4 million pixels, e.g. this scan fitted to 1x1).
- **How it was tested.** `JpegStripDecodeTests` demands byte equality with the retained whole-plane engine on 8 sampling
  layouts, odd sizes, every scale, baseline and progressive, grey, CMYK, hand-built restart streams, truncations and corrupted
  RSTn. `JpegRegionDecodeTests` compares every region against the crop of a full decode, and checks the cost through
  `JpegDecodeStats` (MCU rows entropy-decoded and transformed). `JpegFittedDecodeTests` transcribes `Shrink` and demands
  byte equality at integer and non-integer ratios, with a region, and bounds the allocation. The golden digests still pass
  unchanged. Deliberate bugs, one at a time, were all caught (see CLAUDE.md for the list).
- **Strip pipeline.** `JpegCore` IDCTs one MCU row (a block row for single-component scans) into per-component strips,
  assembles output rows and hands them to a sink. Plane rents no longer double: `ArrayPool` rounds a 33,660,000-byte plane up to
  64 MiB, so three planes held 192 MiB; strips are 0.19 MiB. Whole planes remain only where a stream cannot be streamed
  (a baseline first scan without every component, a second scan, or an Adobe/JFIF marker after the scan), allocated at exact size.
- **A caveat on `DecodeTo`.** The size check now runs against the frame header before the decode, and rows are written as
  they decode, so a stream that turns out corrupt part-way leaves the rows before the failure in the caller's buffer.

**Measured** on the issue's own shape: a 5100x6600 baseline 4:4:4 scan with DRI = 638 (one MCU row), 7.6 MiB (built in the
benchmark's setup from gradients plus noise by libjpeg through Magick.NET; the issue's real scans were 1.1-1.6 MB, so the
entropy-decode share is larger here). BenchmarkDotNet, one run, all cases in one sitting, Snapdragon X (arm64), .NET 10.0.12:

| Case | Mean | vs full | Allocated per call | Destination |
|---|--:|--:|--:|--:|
| `DecodeTo`, whole image | 333.8 ms | 1.00 | 52.6 KB | 128.4 MiB |
| `DecodeRegionTo`, middle third of the height | 111.1 ms | **0.33** | 33.4 KB | 42.8 MiB |
| `DecodeFittedTo`, whole image to 3165x4096 | 526.9 ms | 1.58 | 260.7 KB | 49.5 MiB |
| `DecodeFittedTo`, middle third to 3165x1365 | 151.8 ms | 0.45 | 80.6 KB | 16.5 MiB |
| `Decode` + box filter to 3165x4096 (the path it replaces) | 432.8 ms | 1.30 | 182,283 KB | 177.9 MiB |

- **The region costs one third.** Entropy-decoded and transformed MCU rows are 275 of 825, exactly one third, and its RGBA is
  exactly one third of the full one (42.8 of 128.4 MiB). The time ratio is 0.33 in BenchmarkDotNet; a separate
  five-run report (`--jpeg-scan-report`) on a busy machine read 0.34 to 0.44, so use the BenchmarkDotNet figure.
- **Sample memory is 0.19 MiB in every case** (`JpegDecodeStats.PeakPlaneBytes`), against 3 x 64 MiB = 192 MiB before.
  BenchmarkDotNet's `Allocated` column cannot show that: strips come from `ArrayPool`, so after the first iteration a decode
  "allocates" almost nothing however much it holds. Read the table's `Allocated` for the replaced path, which genuinely
  allocates 178 MiB per call (the full RGBA plus the target), and the strip figure for the rest.
- **The first cut of the fitted decode saved memory, not time.** Fitting the whole image took 527 ms against 433 ms for
  decode-then-shrink, 22% *slower*, and slower than a plain full decode. The performance review below fixed that: it is now
  faster than decode-then-shrink as well. What it removes is the 178 MiB per call: 49.5 MiB of destination is all that is held,
  in place of 128.4 MiB of RGBA plus the target. Fitting a region saves time too, because the cost is the region's.
- Byte equality, checked in the benchmark's report: `DecodeFittedTo` equals `Decode` followed by the box filter, byte for byte.

**Performance review** (same release, after the table above; BenchmarkDotNet `--job short`, so three iterations a case and
a large run-to-run spread on this machine: only same-sitting pairs are compared). It looked at every row loop of the strip
pipeline, `DecodeRegionTo` and the fitted decode for vectorization and for avoidable allocation, and kept two changes:

- **The fitted sink sums in 32-bit vector lanes and divides by multiply-high.** `JpegFittedSink` kept `long` sums per channel
  and did a 64-bit division per output channel. It now adds each source row into per-column `uint` sums with `Vector128<uint>`
  (portable, scalar fallback kept) and divides by a reciprocal multiply (Lemire fastdiv, with `d == 1` as the identity). That is
  exact only while a box sum fits 32 bits, so a box of more than 16,777,215 pixels takes the old 64-bit path; `forceWideSums`
  selects it for tests and for the A/B. Output is byte-identical (`JpegFittedDecodeTests`, 151 cases, now run on both paths and
  with a box just inside the narrow path's range). One sitting, 5100x6600 DRI = 638 scan, new sink against the old one:

  | Case | New sink | Old sink |
  |---|--:|--:|
  | Fitted whole image to 3165x4096 | 396.5 ms | 516.0 ms (**-23%**) |
  | Fitted middle third to 3165x1365 | 134.9 ms | 172.6 ms (**-22%**) |
  | Fitted whole image to 510x660 | 344.9 ms | 361.8 ms (-5%) |

  In the same sitting a full `DecodeTo` was 325.9 ms, `DecodeRegionTo` of the middle third 112.3 ms (0.34), and decode-then-shrink
  431.1 ms. The sink's overhead on a plain decode fell from about 190 ms to about 71 ms, and **the fitted decode (396.5 ms) now
  beats decode-then-shrink (431.1 ms) on time as well as memory.**
- **The chroma upsamplers V2, H2 and Hv2 have a `Vector128` form** (`JpegResample.RowSimd`; the stb transliteration stays as
  `RowScalar`, and is the whole of `Row` where there is no Vector128 hardware). They compute in 16-bit lanes, which the stb sums
  fit exactly (Hv2's `3*t + t' + 8` peaks at 4088), with the two edge samples and the tail done by the scalar formulas. One sitting,
  4:2:0 2048x1536 `DecodeTo`, scalar against vector upsample: **24.7 -> 21.7 ms (0.88)**; a second, much noisier 12-iteration run
  read 41.9 -> 30.6 ms (0.75). The direction agreed both times; trust the first for size. Per row of 2550 samples, 2000 rows,
  in isolation (which over-predicts): V2 3.53 -> 0.35 ms, H2 6.5 -> 0.61 ms, Hv2 6.1 -> 1.3 ms. `JpegResampleEquivalenceTests` pins the
  vector kernels byte-equal to the scalar ones at every width from 1 to 100 and at image widths, at the 16-bit lanes' limits, at non-zero
  row offsets inside a larger plane, and checks the bounds checks still throw.
- **Not vectorized, and why.** `YCbCrToRgbaRow` already has a measured rejection (store-bound; see CLAUDE.md). The grey, RGB-marked
  and CMYK interleave loops are the same shape (four byte stores a pixel, almost no arithmetic) and serve rare inputs, so no vector
  version was tried; they allocate nothing.
- **Memory, checked.** Strips are rented once per decode and reused (about 21 KB allocated per call, whatever the height); there is no full-height intermediate anywhere in the strip pipeline,
  `DecodeRegionTo` or the fitted decode. The peak sample memory on the issue's shape is 0.19 MiB against 3 x 64 MiB for
  whole planes. The narrow sink holds `sourceWidth * 4` `uint` column sums in place of the `long` accumulator.

## 3.26

**`SharpAstro.Png` decodes cICP PQ and HLG files to linear-light floats.** `PngImageDecoder.TryDecodeRgbaFloat` is the HDR
projection the 8-bit paths refuse to make: it starts from `PngReader.TryDecode` (so PQ and HLG are accepted, not refused)
and returns a fresh `float[]` of `width * height * 4` linear-light RGBA the caller owns, plus the `ColorEncoding` and the
`mDCv` / `cLLI` metadata. The transfer maths is the internal `PngHdr` helper, written from ST 2084, BT.2100, IEC 61966-2-1
and H.273.

- **Signature.** `TryDecodeRgbaFloat(data, out rgba, out width, out height, out encoding)`, and an overload with
  `out MdcvChunk? mdcv, out ClliChunk? clli` after `encoding` (the reader's own chunk types, carried as they are).
- **Which curve.** A `cICP` chunk is authoritative, narrow range (`VideoFullRangeFlag = 0`) included: codes expand per
  H.273 section 8.3 (16..235 at 8 bits, 4096..60160 at 16). Without one, `gAMA` 1.0 reads as linear and everything else
  (untagged, `sRGB`, other `gAMA`) as sRGB, the same assumption `IDecodedImage.ColorEncoding` makes. Supported transfers:
  PQ (16), HLG (18), sRGB (13), the BT.709 family (1, 6, 14, 15) and Linear (8).
- **Scale.** PQ and HLG: `1.0` is the 203 cd/m2 HDR reference white of BT.2408. PQ light is absolute (code 1.0 is 10 000
  cd/m2, so 49.26 here); HLG goes through the BT.2100 OOTF for the nominal 1000 cd/m2 display (system gamma 1.2, luminance
  from the cICP primaries), so a 75 % HLG signal is 1.0 and a 100 % one is 4.93. SDR transfers and Linear: `1.0` is SDR
  white, nothing rescaled to 203. Signals are clamped to [0, 1] before a curve (narrow-range footroom and headroom), except
  Linear, which passes through. Alpha is the file's straight 0..1 alpha, `1.0` when absent, and is never ranged or curved.
- **The returned `ColorEncoding`.** The file's primaries (cICP, else BT.709), `Transfer = Linear`, `FullRange = true` (the
  range is already expanded) and `Float = DisplayReferred`: `1.0` is the reference / SDR white and HDR highlights lie above
  it. A Linear-tagged file lands on `DisplayReferred` too, because its full-scale code is 1.0 (the 8-bit display path treats
  it the same way); a caller who knows it is an astro master may re-interpret it.
- **Every colour type and depth the reader reads.** Grey (replicated into R = G = B), grey+alpha, RGB, RGBA, indexed (the
  palette and its `tRNS` alpha linearised once as a table), 8 and 16 bit, and the 1/2/4-bit depths through the reader's
  unpacking to 8 bits.
- **Refused (false, never guessed).** Whatever `PngReader.TryDecode` refuses (not a PNG, corrupt, an unknown interlace method; an Adam7 file decodes since 3.25); a transfer outside
  the list above (BT.470 gammas, SMPTE 240M, the log curves, xvYCC, BT.1361, ST 428, unspecified); a cICP whose matrix
  coefficients are not Identity (PNG carries RGB); a narrow-range grey image under 8 bits (H.273's range equations start at
  8); an indexed pixel past the palette; and a float raster too big for an array.
- **A PNG-specific method, not a facade member (decision).** `IImageDecoder` has no float projection today, and widening a
  static-abstract interface breaks every codec that implements it, so this lives on `PngImageDecoder` beside
  `TryDecodeRgba8`. The facade still reaches the integer code values and the colour signalling through `TryDecode`. The
  `TryDecodeIntoRgba8` / `TryDecodeRgba8` remarks now point PQ / HLG callers at the new method; those two still refuse PQ
  and HLG, unchanged. Folding a float projection into the facade is a separate, interface-level decision.
- **Checked against.** An independent double-precision reference in the test, from the standards: every sample of every
  PQ and HLG fixture in `Fixtures/Cicp` (full and narrow range) within 1e-4 relative, BT.2111 / BT.2408 patches against
  their nominal levels (PQ 58 % grey is about 203 cd/m2, HLG 75 % grey 203, HLG 38 % grey 26, PQ 100 % 10 000 cd/m2, HLG
  100 % 1000 cd/m2), and each SDR fixture against its own `TryDecodeRgba8` output linearised, within 8-bit quantisation.
- **Speed and memory, measured (BenchmarkDotNet 0.15.8, `[MemoryDiagnoser]`, .NET 10.0.12 arm64, Snapdragon X Elite,
  1920x1080, medians of 6, one sitting; `benchmarks/SharpAstro.Codecs.Benchmarks/PngHdr*.cs`).** The float projection is a
  table lookup per colour sample, built with the reference's own arithmetic, so it is bit-identical to the scalar reference
  (`PngFloatFillEquivalenceTests`) rather than within a tolerance. What it costs above the shared inflate + unfilter:

  | 1080p | `PngReader.TryDecode` alone | fill, scalar reference | fill, shipped | whole `TryDecodeRgbaFloat` |
  |---|---|---|---|---|
  | PQ RGB16 | 87.7 ms | 151.4 ms | **5.8 ms** (26x) | 94.1 ms (+7 %) |
  | PQ RGB16 narrow range | same | 146.8 ms | 6.2 ms (24x) | n/a |
  | PQ RGBA16 | same | 158.3 ms | 5.6 ms (28x) | n/a |
  | HLG RGB16 | same | 45.5 ms | 16.3 ms (2.8x) | 105.6 ms (+20 %) |
  | sRGB RGBA8 | n/a | 79.9 ms | 3.1 ms (26x) | 56.3 ms |

  The whole-decode numbers add up (87.7 + 5.8 = 93.6 against 94.1 measured; 87.7 + 16.3 = 104.0 against 105.6), so the
  allocation and the large-object GCs it triggers cost about 0.5-1.5 ms. HLG is the slow one because its OOTF couples the
  three channels (one `pow` per pixel, about 5 ns) on top of a scene-light table. Narrow range is free per pixel: the
  expansion is folded into the table build.
- **Allocation check: exactly one raster plus the reader's pixels, nothing per pixel or per row.** 1080p PQ RGB16
  allocates 43.52 MB (BenchmarkDotNet's MB, 2^20 bytes) = 31.64 MB (the 33 177 600-byte `float[]`) + 11.88 MB (the reader's decoded samples, the same 11.88 MB
  `TryDecode` alone allocates); sRGB RGBA8 39.56 MB, likewise. At 4K UHD (3840x2160) it is 174.05 MB = 126.56 MB `float[]`
  + 47.48 MB pixels. The fill itself allocates 0 B for HLG, RGBA16 and sRGB; the 2-4 KB shown for PQ RGB16 is the 256 KiB
  pooled 16-bit table's first rent spread over 64/128 measured calls (262 144 B / 4096 B = 64), not something that grows
  with the image. 8-bit tables are `stackalloc`. Only the scalar reference (tiny images, and the tests' oracle) allocates a
  row scratch, 34.6 KB once per call. The 4K *timings* are not quoted: with four iterations and a 127 MiB array landing on
  the large-object heap, the run's standard deviation was larger than its mean.
- **In-place and strip conversion are not possible here, and that is measured rather than assumed.** The projection is
  2.7x larger than the pixels it reads (16 B per pixel out, 6 B in for RGB16), so it cannot overwrite the reader's buffer;
  and `PngReader` inflates and unfilters the whole image into one `Pixels` array before anything can be projected, so a
  strip pipeline would first need a streaming reader. The one avoidable cost is the large-object `float[]` itself (about
  0.7 Gen2 collections per 1080p decode, small here because the array holds no references): a caller-supplied destination
  overload would let a caller reuse it, a separate API decision that is not made here.
- **Vector candidates tried and dropped (all portable `Vector128`, all in `PngHdrCandidates`).**
  - *PQ curve in float over `Vector128`* (exp/log, four samples per step): 6.3 ms per 2^20 samples against 24.2 ms for the
    scalar double curve (3.8x), but a 65 536-entry table is **0.86 ms** (28x, 7x faster than the vector curve) and exact,
    where the vector form is 1.1e-4 off in relative terms. The table wins and stays. The only place a vector curve could
    still go is the table *build* (about 1.5 ms, 65 536 scalar evaluations at about 23 ns), and it would give up the
    bit-identical table for about 1 ms of a 94 ms decode.
  - *HLG OOTF gain four pixels at a time*: 17.9 ms against 16.3 ms for the shipped scalar `pow` per pixel (the same-sitting
    A/B in the candidate class read 0.86 +/- 0.14 against a noisy 21.3 ms baseline, so it is no better than noise), and 5.6e-7
    off the reference. Its ceiling is small even if the staging were free: a vector `pow` costs about 3.2 ns per lane in the
    curve benchmark against about 5 ns scalar here, so at most about 4 ms of a 105 ms HLG decode, for a path that is no
    longer bit-identical. Dropped.
  - *One `Vector128` store per pixel for the RGB16 table fill*: 4.97 ms against 5.62 ms, bit-identical, but the 0.65 ms is
    inside the shipped path's own error (+/- 1.8 ms) and is 0.7 % of a 94 ms decode, which no end-to-end run could see. The
    fill is a table-load and store stream, the same shape as the YCbCr row in `CLAUDE.md`. Dropped.
  - *Narrow-range expansion and the RGBA16 alpha division* have no candidate: narrow range is in the table build (6.2 ms
    against 5.8 ms full range, within noise), and RGBA16's per-sample `/ 65535f` costs nothing measurable (5.6 ms against
    5.8 ms for RGB16 with no alpha at all).

  Nothing vector ships, so there is no second kernel for a test to pin to the scalar one. The shipped path is the table, and
  `PngFloatFillEquivalenceTests` pins it bit-for-bit to `FillReference` for every colour type, depth, range and transfer
  (both sides of the table break-even, indexed, and the conformance files).

## 3.25

**`SharpAstro.Png` decodes Adam7 interlaced images.** `PngReader.Decode` (and so `TryDecode`, `PngImageDecoder.TryDecodeRgba8`
and `TryDecodeIntoRgba8`) used to refuse `InterlaceMethod = 1` with `NotSupportedException`, and the `Try` entry points said
false. An interlaced file now decodes into exactly the row-major `PngImage.Pixels` its non-interlaced encoding gives, for every
colour type and depth the reader covers (8/16-bit types 0/2/4/6, 1/2/4/8-bit grey and indexed).

- The seven passes are read in order from the one inflated stream. Each pass is a small image of its own: its rows are filtered
  against the row above them **in the same pass** (all zero for a pass's first row), padded to a whole byte, and their pixels
  scattered straight to where they belong in the result.
- A pass with no pixels (width under 5 has no pass 2, height under 5 no pass 3, and so on down to a 1x1 image, which has only
  pass 1) is not in the stream at all, not even a filter byte, and none is expected.
- The exact-length check stays: a stream a row short or a byte long is `InvalidDataException`, as for a non-interlaced file.
- An `InterlaceMethod` other than 0 or 1 is now `InvalidDataException` (a corrupt header), no longer `NotSupportedException`.
- The non-interlaced path is untouched. Tested against Magick.NET's `Interlace.Png` and `Interlace.NoInterlace` encodings of the
  same random raster at every colour type and depth, at sizes that leave passes empty (`PngAdam7Tests`).

## 3.23

**`SharpAstro.Png` decodes 1-, 2- and 4-bit greyscale and indexed images.** They are the sub-byte depths PNG allows
(colour types 0 and 3), and the reader refused them with `NotSupportedException`, so `TryDecode` and `TryDecodeRgba8` said
false. Emoji fonts store small CBDT bitmaps that way: Noto Color Emoji's U+1F6D1 is a 4-bit indexed PNG. SharpAstro.Fonts
then had no colour bitmap and fell through to an outline the font does not have, and pilot's GUI crashed on Linux.

- A packed row is unfiltered with a one-byte filter step, as PNG section 9.2 sets for depths under 8, then unpacked to one byte per
  sample, most significant bits first, with the row's padding bits skipped.
- An index stays as it is. A grey level is scaled to 0-255 (a 4-bit `0xF` is 255), so the image reads as 8-bit greyscale.
- `PngImage.BitDepth` is therefore 8 for such an image. The new `PngImage.SourceBitDepth` keeps IHDR's own depth, and
  equals `BitDepth` for an image not read from a file.
- Tested against ImageMagick at each depth and both colour types, with an odd width (`PngSubByteDepthTests`).
- Adam7 interlacing is still refused.

## 3.22

**`SharpAstro.Jpeg` writes EXIF and XMP.** `JpegEncodeOptions` gains two options, both off by default; with both
`null` the encoder's bytes are identical to 3.21's. Each is an APP1 segment written in this order: SOI, APP0 (JFIF),
the EXIF APP1, the XMP APP1, then DQT.

- **EXIF.** `SharpAstro.Exif`'s new `ExifWriter.Build(ExifWriteData)` makes the blob and `JpegEncodeOptions.Exif`
  (`ReadOnlyMemory<byte>?`) writes it behind `Exif\0\0`. It takes bytes, not the Exif package's types, so
  `SharpAstro.Jpeg` gains no dependency (Exif pulls in Tiff and Png). The blob is a little-endian TIFF structure with
  IFD0 `ImageDescription` (0x010E), `Software` (0x0131), `DateTime` (0x0132, to the second, no zone) and `Artist`
  (0x013B), plus an ExifIFD pointer (0x8769) to a sub-IFD holding `UserComment` (0x9286); every field is optional.
  IFD0 text is 7-bit ASCII, because that is what the TIFF/EXIF ASCII type is, so a character above U+007F or an
  embedded NUL there is an `ArgumentException` instead of bytes another reader would take for Latin-1.
  **`UserComment` is where text that needs Unicode goes**, with the character code EXIF 2.3 defines: plain 7-bit text
  is `ASCII\0\0\0` + the bytes, anything else `UNICODE\0` + UTF-16 in the blob's byte order (little-endian). An
  unpaired surrogate is refused.
- **XMP.** `JpegEncodeOptions.Xmp` takes a `JpegXmp`: `CreatorTool` (`xmp:CreatorTool`), `Description`
  (`dc:description`) and a caller-owned namespace (`CustomNamespaceUri` + `CustomPrefix`) with `CustomProperties`
  key/value pairs, written as attributes of one `rdf:Description` inside `x:xmpmeta`/`rdf:RDF`, behind
  `http://ns.adobe.com/xap/1.0/\0`. The packet is an escaped string template, the way `GainMapXmp`'s packets are:
  no `XmlSerializer`, no DTD, no namespace-aware writer, so **XMP was kept and the package stays AOT-clean** (a
  clean `--no-incremental` Release build adds no IL2xxx/IL3xxx warnings). `< > & " '` are escaped, tab / newline /
  carriage return are written as character references (a parser would otherwise normalise them to a space), and
  non-ASCII is UTF-8. `JpegXmp.BuildPacket()` returns the packet bytes. There is no `xpacket` wrapper.
- **Refusals**, all an `ArgumentException` (`ParamName` `options`) from `JpegEncoder.Encode` raised before the pixel
  buffer is looked at, so before any output. EXIF: a blob over `JpegEncodeOptions.MaxExifBytes` (65527, the APP1
  limit less the length field and signature), empty, or without a TIFF header (which includes one that already
  carries `Exif\0\0`). XMP: a packet over `JpegEncodeOptions.MaxXmpPacketBytes` (65504, counted in UTF-8 bytes;
  **Extended XMP is not written**, so a larger packet is refused rather than split); a custom key that is not an XML
  NCName (`XmlConvert.VerifyNCName`), is empty or is repeated; custom properties with no prefix or namespace; a
  prefix that is not an NCName, starts with `xml`, or is one the packet uses itself (`x`, `rdf`, `xmp`, `dc`); a
  namespace URI that is one of the packet's own; and text XML 1.0 cannot carry (C0 controls other than tab, LF and
  CR, U+FFFE / U+FFFF, an unpaired surrogate). Both limits are tested at exactly the limit and one over.
- **What external readers confirmed.** Magick.NET 14.13.1 (libjpeg for the container) reads the EXIF profile back:
  `ImageDescription`, `Software`, `Artist` and `DateTime` as written, `UserComment` as the raw UNDEFINED bytes with
  its character code, the `UNICODE\0` case byte for byte; and the XMP profile back as the packet written, whose
  attribute values parse back through `XDocument`, escaped and non-ASCII text included. Our own `ExifReader.FromJpeg`
  and `JpegSegmentScanner` + `XDocument.Parse` read both segments from one file, and the image still decodes.
- **Consumer.** TianWen's `DisplayRasterExport` (the save-as-displayed path, which already encodes a JPEG and splices
  in an sRGB ICC profile) is the intended user, so a saved JPEG can carry the app, target and exposure it came from
  the way a PNG can carry text since 3.21. Adopting it is a change in TianWen, not in this repo.

## 3.21

**`SharpAstro.Png` reads and writes text.** `PngWriteOptions.Text` takes a list of `PngText(Keyword, Text)`, each
written as an `iTXt` chunk after the image data: UTF-8 of any length, deflated past
`PngWriter.TextCompressionThreshold` (1 KiB). A keyword the PNG spec forbids (empty, over 79 characters, outside
Latin-1 printable, or with a leading, trailing or doubled space; `PngText.IsValidKeyword`) is an
`ArgumentException`. `PngImage.Text` reads `tEXt`, `zTXt` and `iTXt` alike, in file order, so text another tool
wrote reads the same way; a malformed text chunk is skipped, never a failed decode. Deflated text stops
inflating at `PngReader.MaxInflatedTextBytes` (8 MiB, libpng's default) and the chunk is skipped, so a few KB of
crafted text cannot ask for gigabytes; an `iCCP` profile gets its own cap, `MaxInflatedIccProfileBytes` (16 MiB),
and past it fails the decode as a malformed `iCCP` already does. For an application's own record
inside the image it saved (TianWen's save-as-displayed writes its rendition this way), checked against Pillow,
which reads the three chunks back, the deflated one included.

## 3.20

`SharpAstro.Jxl`'s lossy VarDCT path **works one 256-px group at a time** and no longer builds
whole-image planes (#39). Its output is **unchanged**: encoded bytes and decoded pixels are
identical to 3.19 across a sweep of sizes from 8x8 to 8192x8192, partial and multi-LF-group grids,
noise and photographic content, and every quantizer setting tried.

- **Why it could.** Every step after entropy coding is local to one 8x8 block: the DCT,
  quantization, chroma-from-luma, and XYB <-> sRGB. The encoder writes neither Gabor nor EPF, and a
  varblock never crosses a 256-px group. So only the LF DC (1/64 of the pixels), the HF symbol
  streams and the caller's output need to span the image. Before this change each stage
  materialized its own whole-image plane, about 58 bytes per pixel in each direction.
- **Measured at 8192x8192**, 3.19 against 3.20, alternating runs:

  | | 3.19 | 3.20 |
  |---|---|---|
  | Encode, allocated | 3,919 MB | **593 MB** |
  | Decode, allocated | 3,931 MB | **862 MB** (768 MB of it is the returned image) |
  | Encode + decode, peak working set | 4.08 GB | **2.08 GB** (1.5 GB of it is the caller's input and output) |
  | Encode / decode time | 7.3-8.0 s / 5.1-5.2 s | **5.6-5.7 s / 3.6-3.7 s** |

- **Visible effect.** The test suite's peak memory came from `JxlVarDctEncoderTests` alone (3.9 GB,
  enough to bring down a 4 GB WSL VM); `BigImage_SelfRoundTrip_8192Square` now peaks at 2.1 GB and
  `BigImage_DecodesInLibjxl_LowRmse` at 0.95 GB, from 4.0 and 2.2.
- `JxlImageCodec.EncodeRgb24Lossy` reads the caller's spans in place instead of copying all three
  planes first (another 768 MB at 8192x8192).

## 3.19

New package: **`SharpAstro.Webp`**, a pure-managed WebP decoder, spec-derived from the prose and tables
of RFC 6386 (VP8) and RFC 9649 (WebP). The RFC 6386 section 20 reference decoder source was not used.
libwebp is used as a binary oracle only.

- **Formats.** VP8 lossy, VP8L lossless, ALPH transparency and animation. ICC / EXIF / XMP payloads are
  preserved. Output is non-premultiplied RGBA8, byte-exact against `dwebp` / `anim_dump`. Encoding is
  not supported.
- **Animation.** `WebpAnimationDecoder` streams full-canvas frames through one canvas and supports random
  seeking. `WebpDecoder.DecodeAnimation` materializes every frame, up to
  `DefaultMaximumAnimationPixels` (canvas × frames, 2^28) unless the caller passes a limit. Each returned
  frame is a full-canvas copy, so without that limit a 3 KB file of one-pixel frames could ask for 4 GiB.
- **Facade.** `WebpImageDecoder` registers `RIFF....WEBP` with `ImageCodecs`. Lossy, lossless and the
  first frame of an animation decode through it.
- **Limits.** Canvases above 67,108,864 pixels are rejected before any raster is allocated. A still
  `VP8X` canvas that disagrees with its bitstream is refused, as libwebp refuses it. The residual cost
  at the limits is measured in `CLAUDE.md`: 1.25 GiB for `DecodeAnimation` at its default limit, and
  512 MiB for `Decode` of an 8192x8192 animation.

## 3.18

`SharpAstro.Png` decodes into the samples and nothing else.

- **`PngReader.Decode` allocates the image once.** It copied every IDAT payload out of the input,
  concatenated the copies, copied that again to wrap it in a stream, inflated into a growing
  `MemoryStream`, copied that out, and unfiltered the copy into a new array. Now the IDAT payloads are
  read where they lie in the input, each row is inflated straight into its place in the result, and
  unfiltered there against the row above (`PngPredictor.UnfilterRow`, internal). A 5100x6600 RGB page
  allocated 514.8 MB to decode and allocates 101.0 MB, its samples; the time is unchanged
  (1.04-1.36 s before, 1.04-1.24 s after, alternated).
- A stream a row short, or running past the last row, is refused as before, with the same
  `InvalidDataException`.

## 3.17

`SharpAstro.Png` reads a PNG into an RGBA8 array in one call (#32).

- **`PngImageDecoder.TryDecodeRgba8(data, out rgba, out width, out height)`** is the reading half
  `PngWriter.Encode(rgba, w, h)` lacked. Callers no longer chain `TryReadInfo`, an allocation and
  `TryDecodeIntoRgba8`, and need not know the size up front. The array is fresh, exactly
  `width * height * 4` bytes and referenced by nothing else, so it can be adopted rather than copied
  (DIR.Lib 11.27's `RgbaImage(width, height, pixels)`). An 8-bit RGBA file's decoded samples are
  returned as they are, with no expansion pass; other colour types, indexed included, expand once.
  It refuses what `TryDecodeIntoRgba8` refuses (cICP PQ/HLG), and returns false rather than throwing
  on a stream the reader cannot decode (Adam7 interlace, sub-byte depths, corrupt data).
- **`PngReader.TryDecode(data, out PngImage? image)`** is `Decode` without the exceptions, and the shared
  first step of every `PngImageDecoder` entry point. It passes cICP PQ/HLG files through untouched: the
  refusal belongs to the RGBA8 projections, so a later float projection starts from it.
- **Fix:** `TryDecodeIntoRgba8` threw `NotSupportedException` on an Adam7 or sub-byte-depth PNG instead of
  returning false. It now goes through `PngReader.TryDecode` like the rest.
- It returns an array, not DIR.Lib's `RgbaImage`, as #32 first asked: DIR.Lib depends on
  `SharpAstro.Fonts`, which depends on `SharpAstro.Png`, so a reference back would be a package cycle.

## 3.16

`SharpAstro.Jpeg2000` gets **faster, decodes at reduced resolution, and joins the facade.**

- **Faster.** On a photo-like 2048x1536 RGB image, against 3.15, alternating runs: lossless 5/3
  1750 → 1115 ms and 291 → 100 MB allocated; lossy 9/7 548 → 276 ms and 187 → 100 MB. The decoded
  samples are byte-identical. Tier-1 keeps a packed flags word per coefficient, so contexts are table
  lookups, and shares one state across code-blocks; the inverse wavelet filters columns a row at a
  time, vectorised, without per-line allocation.
- **Reduced resolution (rung 5).** `Jpeg2000Decoder.Decode(data, reduce)` leaves out the `reduce`
  finest resolution levels, which are never entropy-decoded, and `ReductionFor(width, height,
  minLongEdge)` picks the reduction for a wanted size. It is `opj_decompress -r` and matches it
  (exactly for 5/3, within 1 for 9/7). The 2048x1536 lossless image decodes in 488 / 174 / 79 ms
  at reductions 1 / 2 / 3.
- **Facade.** `Jpeg2000ImageDecoder` registers raw J2K and JP2 with `ImageCodecs`: one component as
  grey, three as RGB, ICC profiles passed through, samples scaled to fill UInt8 or UInt16. Two or
  four components and sYCC are refused rather than presented in the wrong colours.

**Public API additions:** `Jpeg2000Decoder.Decode(ReadOnlySpan<byte>, int)`,
`Jpeg2000Decoder.ReductionFor`, `Jpeg2000Decoder.ReadInfo` with `Jpeg2000Info` (size, components,
depth, the levels a reduction can leave out, the JP2 colour, and `SizeAt(reduce)`, all from the
headers, so a caller can price a decode before running it), `Jpeg2000ImageDecoder`. Nothing removed
or changed.

## 3.15

`SharpAstro.Jpeg2000` **decodes what real PDFs carry.** Rung 1 decoded one component in one layer,
and a probe over 235 PDFs found six `/JPXDecode` images, none of them inside that. All six decode
now: the four lossless ones exactly as `opj_decompress` does, the two lossy ones within 1 of it in
every sample.

- **Several components**, of one precision, interleaved in `Jpeg2000Image.Samples` with
  `Components` saying how many. `ToGray8` refuses more than one; `To8Bit` takes any number.
- **Quality layers**, brought forward from rung 3 because every real image had them. Tag-tree state
  carries across layers, and three layered fixtures now catch it when it does not (hazard 5, which
  rung 1's corpus could not see).
- **COC and QCC** in the main header, applied per component.
- **RCT and ICT**, between the wavelet and the level shift.
- **The 9/7 wavelet** in floats, with scalar dequantisation, expounded or derived, and coefficients
  reconstructed at the middle of their interval (T.800 E.1.1.2). The 5/3 path needed that rule too:
  one real image's last layer was truncated, and without it 1,007 samples came out one off.
- **The JP2 file format.** `Decode` takes a JP2 file as well as a raw codestream (`IsJp2` tells
  them apart) and reports the first colour specification on `Jpeg2000Image.Colour` without applying
  it, since a PDF's `/ColorSpace`, when present, overrides it (ISO 32000-2 8.9.5). Palette,
  component-mapping and channel-definition boxes are refused by name.

The 9/7 tests assert a tolerance measured against OpenJPEG rather than one from T.803: every sample
within 1, at most 1% differing (the fixtures reach 0.32%, the real images 0.15%). They live in a
file of their own, apart from the exact assertions.

**Small public API change:** `Jpeg2000Image` gained `Components` (defaulting to 1, so the old
constructor call still compiles) and `Colour`, and `ToGray8` now throws on a multi-component image
rather than returning something that is not one.

Still refused: tiles, precincts, the other progression orders, POC/PPM/PPT/SOP/EPH, code-block
style flags, subsampling, mixed precisions, signed components. Still not registered with the
`SharpAstro.Codecs` facade: mapping a JP2 colour onto `ColorEncoding` is its own change.

## 3.14

`SharpAstro.Png` can **encode across cores**, via `PngWriteOptions.ParallelFragments`. Deflate is
inherently serial and was the largest single cost in a big encode, so the only way to move it is to
run several of it: each fragment is ended with a SYNC FLUSH rather than a final block, which leaves
it byte-aligned and open-ended, so independently compressed fragments CONCATENATE into one perfectly
ordinary deflate stream. No reader needs to know; there is nothing unusual in the result.

Row filtering is split along the same seams, and that is what makes the whole encode scale instead of
just its compression. A PNG row is predicted from the UNFILTERED row above it, so a band depends on
the source image and not on anything the band before it produced -- no state, no ordering, one
re-read source row at each seam. It is a property of the format rather than an arrangement here.

31.3 MP of 16-bit RGB, same 96 MB file at every setting:

| fragments | time |
|---|---|
| 1 (default) | 5013 ms |
| 4 | 1718 ms |
| 8 | 968 ms |
| 12 | 825 ms |

**The count is the caller's and deliberately not derived from `Environment.ProcessorCount`**, because
a fragment can only back-reference its own data: output is deterministic for a given count and
differs between counts, so deriving it from the machine would make a file's bytes depend on which
machine wrote it, and no golden-file test could survive that. Asking for more fragments than there
are cores is free, so a fixed 8 gets most of the benefit on a big machine and byte-identical output
on a small one. `1` remains the default and remains byte-for-byte the old encoder.

The costs are a join's worth of unexploited redundancy (+0.1% size at 24 fragments) and holding the
compressed fragments in memory while they are assembled, about doubling peak footprint. A request
larger than the image can carry is reduced silently rather than refused.

`PngWriterParallelDeflateTests` pins this by MEANING rather than by bytes, since there are no bytes to
compare against. Each of the three ways the construction fails silently has its own case, because
each produces a file that looks entirely plausible until read: a fragment disposed rather than flushed
carries a final block and truncates the image at the first join; a missing terminating block leaves
the stream unfinished; and a mis-combined Adler-32 sails past our own reader, which does not check it.

`SharpAstro.Png`'s **writer can emit RGB (colour type 2)**, and its 16-bit paths no longer copy the
whole image to swap it. The reader has accepted colour type 2 since it was written; the writer could
only produce 4 and 6, so an opaque render paid for an alpha plane that was a constant `0xFF(FF)` all
the way through filtering, scoring, deflate and the file on disk.

Two ways in, because there are two kinds of caller. `PngWriter.EncodeRgb8` / `EncodeRgb16` take
packed three-channel input. `PngWriteOptions.DiscardAlpha` takes the RGBA a renderer already has and
drops the fourth channel during the per-row gather, so no repacking pass is needed to reach the same
file -- and it IS the same file, byte for byte, which is what `PngWriterRgbTests` pins.

**The whole-image byte-swap buffer is gone.** The 16-bit entry points used to hand the IDAT writer a
second copy of the picture, big-endian, built up front: 250 MB for a 31.3 MP RGBA16 frame, allocated
so it could be read exactly once, sequentially, and dropped. Rows are gathered and swapped one at a
time now, out of a pooled scratch buffer, which is also where dropping alpha becomes free.

**The filter score is computed while filtering, and without a branch.** Row selection used to filter
all five candidates and then re-read all five to total them; worse, it totalled them with
`s < 0 ? -s : s`, a data-dependent branch over essentially unpredictable bytes. `FilterRow` returns
its own score now, computed branchlessly. The branch was the larger half of that by some way.

`PngWriteOptions.CompressionLevel` exposes the deflate level, defaulting to `Optimal` exactly as
before. It is offered rather than re-defaulted because the trade belongs to the caller: an
interactive "save this frame" wants the seconds back, an archival write does not.

Measured on a 31.3 MP synthetic frame (smooth sky, 4000 stars, a noise floor -- random bytes would
have flattered every one of these changes, being incompressible):

| | time | file | allocated |
|---|---|---|---|
| RGBA16, 3.13 | 6462 ms | 107 MB | 603 MB |
| RGBA16, 3.14 (byte-identical output) | 5186 ms | 107 MB | 364 MB |
| RGB16 via `DiscardAlpha` | 4551 ms | 96 MB | 354 MB |
| RGB16 + `CompressionLevel.Fastest` | 3216 ms | 135 MB | 649 MB |

**What did NOT change, having been measured and rejected: the adaptive filter search.** On that
synthetic frame a fixed `Up` filter is 36% faster AND 5% smaller than the five-way minsum search,
which reads as a clear win and is not one -- on a real 9 MP display raster the search gives the
smallest file of any strategy at both depths (10.4 MB against 11.1-11.6 fixed at 8-bit; 12.2 against
12.9-13.5 at 16-bit). The synthetic scene's noise floor is what inverts it. A filter default is not
something to change on one image, and certainly not on a generated one.

## 3.13

`SharpAstro.Tiff` **reads tiled pages**. `TiffLayout.Tiled` had been write-only: the writer could
emit a TIFF this package could not open, which made a tiled export a one-way door and put every
tiled file from any other tool out of reach. `TiffReader` now decodes
`TileWidth`/`TileLength`/`TileOffsets`/`TileByteCounts`, and `TiffPage.TileWidth` / `TileHeight`
report which layout the file used (zero on a stripped page, where `RowsPerStrip` is the meaningful
one).

A tile holds part of a row while the sink's unit is whole rows, so the reader assembles one ROW of
tiles into a band and hands it over exactly as it hands over a strip. One contract for both layouts:
`Read`, `ReadInto` and `TiffImageDecoder` take a tiled page with no caller-visible difference. The
cost is one extra sequential pass over the image, which is the price of that contract; the
zero-copy hand-over of an uncompressed page stays a strip-only promise and now says so.

Strips and tiles are exclusive (TIFF 6.0 p.68), so a page carrying both sets of tags is refused
rather than read as whichever kind the reader happens to look for first, and a tile list too short
to cover the page is refused rather than decoded into a part-blank raster.

**The predictor is the trap this path is built around.** Horizontal differencing restarts at every
row of every TILE, and a tile's row is `TileWidth` wide, not the image's -- invert it at image width
and the picture decodes with no error, correct down its first tile column and drifting across the
rest. Nothing this package writes could catch that, because `TiffWriter` emits no predictor at all,
so the regression fixture is written by **libtiff** with Predictor 2 on and a width that is not a
multiple of the tile width. A round trip could not be the whole test either: writer and reader agree
about tile order by construction, so a consistent transpose would survive one. `TiffTiledReadTests`
therefore checks three independent things -- tiled and stripped writes of one raster decode
identically, a libtiff-written file reads pixel for pixel, and a tiled decode equals the PNG of the
same pixels, which is the golden-comparison shape the read path was wanted for.

Both buffers (the tile scratch and the band) are rented once per page rather than per tile, and both
are sized off `min(TileHeight, Height)`, so a page declaring a tile taller than itself sizes them
from the picture rather than from the declaration and the padding rows below the image are never
decoded.

## 3.12

**New package: `SharpAstro.Jpeg2000`** — a pure-managed, clean-room JPEG 2000 (ITU-T T.800 /
ISO/IEC 15444-1, **Part 1**) decoder, aimed at PDF's `/JPXDecode` filter. This is rung 1 of the five
in [`ROADMAP-jpx.md`](ROADMAP-jpx.md), and rung 1 here is unusually large on purpose: JPEG 2000 has
no early pipeline stage that decodes anything on its own, so the rungs stage along *feature* axes
rather than pipeline stages. Rung 1 is the whole pipeline — Annex A markers, tier-2 packet headers
with tag trees, tier-1 EBCOT over the Annex D context tables, the reversible 5/3 inverse DWT, and
the DC level shift — for the simplest legal configuration.

**Envelope:** one 8-to-16-bit unsigned component, one tile, one tile-part, one quality layer,
maximal precincts, LRCP, reversible 5/3, raw J2K. Everything outside it throws
`NotSupportedException` naming the feature **and the rung that owns it** — multiple components,
tiles or layers, the 9/7 irreversible filter and quantisation, COC/QCC/POC/PPM/PPT/SOP/EPH, custom
precincts, code-block style flags, RGN/MAXSHIFT, subsampling, signed components. Never a
plausible-looking wrong raster.

**Not registered with the `SharpAstro.Codecs` facade**, deliberately. Unlike JBIG2 it would fit
perfectly — JP2 and raw J2K both have magic bytes — but the facade would then advertise JPEG 2000
support for a format only this narrow slice of which decodes. Registration lands with colour at
rung 2. Use `Jpeg2000Decoder.Decode` directly until then.

**The reversible path is exact, and is asserted exact.** A lossless 5/3 codestream reconstructs its
encoder's input byte for byte, so each committed `Fixtures/jpeg2000/<name>.pgm` *is* the expected
output for the `<name>.j2k` beside it: 13 fixtures, byte equality, no tolerance and no oracle
process at test time. `Oracle/jpeg2000/make-fixtures.sh` verifies that claim with `opj_decompress`
per fixture before it will commit a pair. When rung 2's 9/7 path arrives it needs a tolerance from
T.803 — that tolerance must not be allowed to leak back over these cases, because an exact-match
test on the reversible path is the sharpest tool this format offers.

The suite was then **mutation-checked**, and all seven deliberate bugs were caught: dropped sign XOR
bit, raster scan instead of 4-row stripes, T.88's context initialisation instead of T.800's, HL
reading the LL/LH table unswapped, no `0xFF` bit-stuffing, truncation instead of floor in the
lifting steps, HOR/VER filter order swapped. An eighth was a control and behaved as predicted:
rebuilding the tag trees per packet is **not** caught, because tag-tree state spans quality layers
and this rung has one. Recorded rather than papered over — rung 3 needs a multi-layer fixture for it.

**Hardened at rung 1 rather than in a follow-up release**, which is the mistake `SharpAstro.Jbig2`
had to correct in 3.8. Declared geometry is charged against a 2^28-sample-per-tile-component
ceiling, a 2^22 code-block ceiling and a total budget scaled to the declared image, all *before*
anything is allocated — because T.800's MQ decoder, exactly like T.88's, reads every byte past the
end of its data as `0xFF` for ever, so running out of input is not a backstop. The limit tests found
a real overflow while being written: the tile-count ceiling division wrapped for an image declaring
an extent near `int.MaxValue`, so a decompression bomb was refused for the wrong reason and with the
wrong exception type.

**`MqDecoder` relocated to `SharpAstro.Codecs.Abstractions`** (still `internal`, now with
`InternalsVisibleTo` for `SharpAstro.Jbig2` and `SharpAstro.Jpeg2000`). T.88 Annex E and T.800
Annex C specify the same arithmetic coder with the same `Qe` table, and two copies could only drift;
`InternalsVisibleTo` cannot cross two shipping packages, and every codec already depends on the
abstractions package, so this adds no dependency edge. **No behaviour change to `SharpAstro.Jbig2`**
— the T.88 Annex H.2 conformance vector still pins the same table in both directions. The coder is
shared but its *initialisation* is not: T.800 Table D.7 seeds three of its nineteen contexts away
from zero where T.88 starts all of them at zero.

**A fourth CI oracle: OpenJPEG.** `Oracle/jpeg2000/fetch.sh` downloads the pinned v2.5.4 release
build, verified by SHA-256, and CI runs it — so `REQUIRE_ORACLES=1` now covers jbig2dec, jxrlib,
jpegenc and OpenJPEG with none able to go silently missing. It is the only oracle here that is
downloaded rather than compiled: OpenJPEG wants CMake, and upstream publishes official builds for
both platforms that matter, so the dev box and CI run the *same bytes* rather than merely the same
source. `apt-get install libopenjp2-tools` was rejected on evidence — it is 2.4.0 on jammy and 2.5.x
on noble, so the two would have quietly disagreed about what the reference is.

Also fixed: `Jbig2Oracle` resolved `wsl.exe -- jbig2dec` as *available* on a machine with no
jbig2dec in WSL, because it probed by searching the combined output for the tool's own name and a
shell says `jbig2dec: command not found`. The substring test matched the very message proving the
tool was absent, turning 75 honest skips into red failures. It checks the exit code first now.

## 3.11

`SharpAstro.Tiff` gains a streaming read: `TiffReader.ReadInto<TSink>(span, ref sink)` hands each
page's metadata over before any pixels are decoded, then each strip's samples, instead of returning
one assembled raster. A caller converting those bytes into its own representation no longer holds
both at once -- 354 MiB of intermediate for a 13228x9354 RGB page.

`Read` is now that same machinery with a sink that concatenates, so there is one decode path and the
existing round-trip suite covers it. `Read`'s behaviour is unchanged.

An uncompressed page that needs no normalisation (no endian swap, `Predictor.None`) is handed over as
a slice of the input, with nothing copied and no scratch rented. That is the case worth having: for
an uncompressed page the decode is a memcpy, so it previously cost a file buffer plus a raster for
bytes already in the required layout. Read from a memory-mapped file through the sink, neither
exists. Since writers emit `II` and hosts are little-endian, no swap is needed at any bit depth in
practice, so 16- and 32-bit uncompressed pages take that path too.

## 3.10

- **`SharpAstro.Tiff` reads LZW (compression 5)**, the other compression a real-world TIFF is likely
  to arrive in -- it is Photoshop's own default, paired with horizontal differencing. Codes are packed
  MSB-first (GIF's LZW packs them LSB-first) and the code width grows on the *early change* schedule,
  stepping up one code before each power of two. Both quirks decode plausibly for a few hundred codes
  and then turn to noise, so getting either wrong yields a picture that starts correct and degrades
  partway down rather than an error. Validated against a real Photoshop-written 16-bit RGB file whose
  2,160,000 bytes match an independent implementation exactly, and round-tripped across every width
  boundary in `TiffLzwTests`. Composes with the predictor below rather than implying it.

## 3.9

- **`SharpAstro.Tiff` applies Predictor 2** (horizontal differencing, tag 317) on read. A ZIP/Deflate
  TIFF written with a predictor stores the row *derivative*, so decoding without one produced a
  structure map rather than the image: 7 of 15 files in a real corpus. Applied after the endian swap,
  since the differencing is per sample, not per byte. **Predictor 3** (floating-point) is refused
  explicitly rather than decoded wrongly.
- Per-strip allocation removed from the read path: strip scratch comes from `ArrayPool<byte>` and the
  destination is pre-sized. Note the ordering this exposed -- `MemoryStream.SetLength` **zero-fills on
  growth**, so it must be called before the strip is copied in, never after; the reverse wipes the
  tail of every strip and is data-dependent enough to pass a smooth test fixture.

## 3.8

- **`SharpAstro.Jbig2` resource limits.** Not foldable into 3.7: the shipped 3.7.651 decoder accepts
  decompression bombs. A region's declared size is attacker-chosen and T.88's MQ decoder never runs
  dry (E.3.4 reads past the end of the data as `0xFF`), so 82 bytes could demand 2 GiB and 20+ seconds
  of CPU, and two symbol-dictionary loops could spin for ever on flat memory. Adds a per-bitmap pixel
  ceiling, a decode budget scaled to the caller's page, and progress guards on both loops.

## 3.7

- **New package `SharpAstro.Jbig2`** -- clean-room ITU-T T.88, for PDF's `/JBIG2Decode`. While 3.7 was
  unshipped it grew from its first rung to every region type T.88 defines (generic + MMR/T.6,
  refinement, symbol dictionary + text region, pattern dictionary + halftone); that all landed in the
  first 3.7, which shipped as 3.7.651, so no further bump was needed.

## 3.6

- **Baseline JPEG encoder** (`JpegEncoder`, a byte-exact `stb_image_write` port) joins
  `SharpAstro.Jpeg`.

## 3.5

- `ColorEncoding` / `ToFloats` plus the TIFF / JXR / EXR / JXL facade adapters. That work first shipped
  as **3.4.49** because only the csprojs were bumped and not the version variable -- the failure that
  motivated collapsing the number to one place.
- **New package `SharpAstro.Jpeg.GainMap`** (Ultra HDR gain-map read/write) joins the family.

## 3.3

- **New package `SharpAstro.Jxl`** -- clean-room JPEG XL: lossless Modular plus lossy VarDCT.

## 3.2

- **New package `SharpAstro.Exr`** -- OpenEXR read + write.

## 3.1

- **Break:** `SharpAstro.Png`'s `CicpChunk` positional arguments switch from raw bytes to H.273 enums.
- `SharpAstro.Color.Icc` gains the `IccProfiles.WithCicp` helper.

## 3.0

- **Break:** `SharpAstro.Tiff`'s `IccProfile` API moves from `byte[]?` to `ReadOnlyMemory<byte>`.
