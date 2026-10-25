# Changelog

Release notes for the whole family: every package in this repo (the `SharpAstro.Codecs` facade plus
`SharpAstro.Tiff` / `.Png` / `.Exif` / `.Color.Icc` / `.Jpeg` / `.Jpeg.GainMap` / `.Jxr` / `.Exr` /
`.Jxl` / `.Jbig2` / `.Jpeg2000` / `.Webp`) ships at the same `Major.Minor` with a shared run-number patch, so one section
covers them all.

The number itself lives in exactly one place, `<VersionMajorMinor>` in `Directory.Build.props`; CI
reads it back and appends the run number. Bumping a release is editing that one line and adding a
section here. Newest first.

This file was created at 3.10 by migrating the note trail out of the two comment blocks that carried
it -- the `env:` block in `.github/workflows/dotnet.yml` (35 lines of prose above a variable that no
longer holds the number) and the header comment in `Directory.Build.props`. Entries below 3.10 are
those notes, verbatim in substance. 3.4 has no recorded note; it was never written down.

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
