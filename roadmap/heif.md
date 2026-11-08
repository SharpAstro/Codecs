# Roadmap: HEIF / HEVC stills (`SharpAstro.Heif`)

> **Status: written 2026-11-08, rung 1 landed on `heif/external-fixtures`.** Issue #66. The
> consumer is FC.SDK: the viewer cannot preview the `.HIF` files a Canon R6 writes with HDR PQ on,
> and the camera's own thumbnail for such a capture is HEVC too, so there is no JPEG to fall back on.

## Provenance: clean-room, oracles as binaries

Same licence arithmetic as JBIG2 and JPEG 2000: this repo is Unlicense, so nothing notice-retaining
or copyleft can be absorbed. Written from the specifications only:

- **ISO/IEC 14496-12** (ISOBMFF boxes) and **ISO/IEC 23008-12** (HEIF items, properties, grids);
- **ISO/IEC 14496-15** (the `hvcC` record);
- **ITU-T H.265** (HEVC), which ITU publishes free of charge.

**libheif and libde265 (LGPL), ffmpeg (LGPL/GPL) and the HM reference decoder (BSD, notice-retaining)
are oracle binaries only, never port sources.** Magick.NET, already a test reference, bundles libheif
and decodes these files, so the first oracle needs no install.

The patent question in the issue (HEVC has pools) was weighed and the decision is to go ahead clean-room.

## The files, measured

Two real Canon files, fetched rather than committed (`tests/SharpAstro.Codecs.Tests/fetch-heif-external.sh`;
third-party, no licence, far over the fixture size budget):

| File | What it holds |
|---|---|
| `canon-r7-hdrpq.hif` (13.6 MB) | `heix`; primary item a `grid`, 5 rows x 4 columns of hidden 1792x960 HEVC tiles cropped to 6960x4640; `irot` (0, essential) and `pixi` on the grid; two HEVC thumbnails (320x214, 1620x1080), Exif, XMP |
| `canon-r6-hdrpq.cr3` (18.9 MB) | a CR3 whose raw is CRX as usual but whose `THMB` (320x214) and `PRVW` (1620x1080) previews are single HEVC pictures (`hvcC` + an `IMGD` of length-prefixed NAL units), and whose full-size preview track is itself a grid |

`HevcSurvey` (internal) reports every coded picture's parameter sets, NAL types, SEI and slices.
Every one of the 24 pictures in the two files says the same thing, and
`HeifCanonFileTests.AssertInsideEnvelope` pins it:

- **Profile 4 (Format Range Extensions), 4:2:2, 10-bit luma and chroma**, general intra constraint
  set. Not Main 10: 4:2:2 is why.
- **One IDR picture (NAL type 19) per item, all I slices**, cut into 1-4 slices on CTB-row boundaries.
  No dependent slices, tiles or WPP.
- **No in-loop filters**: `pps_deblocking_filter_disabled_flag` with no override, and SAO off in the SPS.
- **No range-extension coding tools**: the SPS and PPS range extensions are absent (no RDPCM,
  transform skip, cross-component prediction, extended precision, persistent Rice adaptation...).
- **Transforms 4x4 to 16x16 only**, CTB 32 (16 on the small pictures), min CB 8.
- **Scaling lists on**, with data in the SPS. `cu_qp_delta` on. No sign hiding, PCM, transquant bypass
  or strong intra smoothing.
- **No SEI at all**, so in particular no decoded-picture-hash SEI: the files cannot check a decode
  themselves, and the oracle has to be external.
- **The coded picture is larger than the image.** The thumbnails are coded 320x320 and 1664x1088
  (CR3: 1632x1088) for 320x214 and 1620x1080, with no conformance window and no `clap`: only `ispe`
  says the real size. The R7's tiles are coded exactly at their `ispe`. libheif as Magick.NET 14.13.1
  bundles it does not crop to `ispe` and returns the coded size; this reader will crop.

## Rungs

Staged as JPEG 2000 was, by feature axis: one correct pixel needs the whole pipeline, so the first
decoding rung is the whole pipeline for the narrowest configuration the files use, and later rungs widen it.

### Rung 1: container and parameter sets (done)

`HeifReader.IsHeif` / `Read` / `HeifFile.Describe` / `ReadItemData`. Boxes, `ftyp` (only HEVC brands
claim a file; AVIF and CR3 are refused), `meta`/`hdlr`/`pitm`/`iinf`/`infe` (v0-3)/`iloc` (v0-2,
file and `idat` construction)/`iref` (v0-1)/`iprp`/`ipco`/`ipma` (both widths), properties `ispe`,
`colr` (nclx, prof, rICC), `pixi`, `irot`, `imir`, `clap`, `hvcC`, and the `grid` item. `Describe`
resolves a grid (tile order, equal tile sizes and coding, coverage) and charges the declared size
against `HeifLimits` (2^28 pixels for an image, 16888 a side for a coded picture, the most any HEVC
level allows). The HEVC side parses NAL units, emulation prevention, Exp-Golomb, the full SPS (VUI,
HRD, short-term RPS, scaling lists, range extension) and PPS (tiles, deblocking, range extension).

Not registered with the facade yet: a facade `TryReadInfo` that answers for a file `TryDecode` cannot
decode would invite callers to size a buffer for nothing. Registration comes with rung 3.

Validated by: hand-built files (`HeifBuilder`, every field-width variant, malformed structure, limits,
every truncation, 20,000 random byte-flip trials on a synthetic file and 5,000 on the R7's real
`meta` box, in which only `InvalidDataException` and `NotSupportedException` may escape); hand-built
parameter sets (`HevcBitWriter`) with a known value placed after every variable-length structure; the
Canon files against Magick.NET for size and depth. Mutation-checked: see the table below.

### Rung 2: HEVC intra decode, the measured envelope

Exactly the configuration above, and every other value refused by name (`NotSupportedException`
naming the feature), the JPEG 2000 discipline:

1. Slice segment header (I slices of an IDR picture; `slice_qp_delta`, the address, entry points).
2. CABAC (9.3): context initialisation for I slices, the arithmetic decoding engine, bypass and
   terminate, and the binarisations the syntax below uses.
3. Coding quadtree and coding units (7.3.8): split flags, `part_mode` (2Nx2N / NxN for intra),
   `cu_qp_delta`, the transform tree and its `cbf` flags.
4. Intra prediction (8.4.4.2): reference sample substitution and filtering, planar, DC, the 33 angular
   modes, the most-probable-mode derivation, and chroma mode derivation **for 4:2:2**, which remaps
   the angle (Table 8-3) because a 4:2:2 chroma block is half as wide as it is tall. That remap is the
   4:2:2-specific risk.
5. Residual coding (7.3.8.11): sub-blocks, last position, significance maps, greater-1/2 flags,
   remaining levels with Rice parameters.
6. Dequantisation with **scaling lists** (default and SPS-coded, 7.3.4 / 7.4.5), the chroma QP
   mapping (for 4:2:2, ChromaArrayType != 1, the table is the identity clipped to 51), and the inverse
   DST 4x4 / DCT 4-16. A 4:2:2 chroma transform block is two square blocks stacked, each with its own cbf.
7. Reconstruction into 10-bit planes, then the crop to `ispe`.

No deblocking and no SAO: none of the files uses either. They are rung 4.

**Oracles for rung 2, in order of strength:**

- **libde265 / ffmpeg decoding to raw YUV**, compared plane by plane with **no tolerance**: an HEVC
  decoder is bit-exact by specification, which JPEG's IDCT is not. Gated with `OracleGate` like the
  other external tools, installed in CI the same way (`apt-get install` a decoder binary).
- **Magick.NET** for the end-to-end picture: available everywhere, but its output is RGB after
  libheif's YCbCr conversion, so either model that conversion or compare within a measured tolerance.
  `R6_CrPreviewsDecodeThroughMagickOnceWrappedAsHeif` already shows it decodes the CR3 previews once
  wrapped as one-item HEIFs, at the coded size.
- **The JCT-VC conformance bitstreams** (ITU-T H.265.1), each published with an MD5 of its decoded
  output. Fetched with a pinned hash like OpenJPEG's release. The intra-only RExt 4:2:2 10-bit ones
  are the match; the rest wait for rung 4.

Fixture plan: the 320x214 thumbnails are the first target (11-43 KB of slice data, one or four
slices), then the 1620x1080 previews, then the 20 R7 tiles. Synthetic streams cannot be written
without an HEVC encoder; if Magick.NET's libheif can encode (needs x265 or kvazaar built in), it can
produce small fixtures for committing, since our own output would be ours to license.

### Rung 3: composition and the facade

Grid tiles to one canvas, `irot` / `imir` / `clap` applied, chroma upsampled and YCbCr to RGB by the
`nclx` matrix (BT.2020 non-constant luminance here). `TryDecode` gives native 10-bit samples (as
UInt16, scaled to fill the format, as JPEG 2000's adapter does) with PQ `ColorEncoding`;
`TryDecodeIntoRgba8` returns false, like the other HDR float paths, until a tone-map policy is agreed.
A thumbnail decode (`thmb` items) is the cheap preview FC.SDK wants: 1620x1080 through the same decoder.

### Rung 4: the rest of intra HEVC

Deblocking (8.7.2) and SAO (8.7.3), 32x32 transforms, transform skip, sign data hiding, PCM, tiles and
WPP entry points, 4:2:0 and 4:4:4, 8 and 12 bits, the range-extension tools. Each needs an oracle stream
that uses it: the conformance suite carries most of them.

### Rung 5: speed

The 20 R7 tiles are independent, so decode them in parallel; then SIMD for the inverse transforms.
Measured end to end through the public API, per the benchmark rules in CLAUDE.md.

## Hardening

Running out of input is not a backstop here either: CABAC, like the MQ coder, keeps producing
decisions from whatever it is fed. So every loop in rung 2 must advance something each iteration, and
the decode charges the SPS's declared picture size against `HeifLimits` before allocating planes.
The container already does: `Describe` refuses declared sizes before any allocation, measured under
256 KiB of allocation for a refusal.

## Mutation check (rung 1)

Ten deliberate bugs, one at a time, against the HEIF and HEVC tests. Nine were caught on the first run;
the tenth exposed a gap that a new test then closed.

| Deliberate bug | Caught by |
|---|---|
| `ipma` 15-bit property indices read as 8-bit | the field-width variants (2 cases) and the 529-tile grid |
| `iref` version 1 IDs read as 16-bit | the field-width variants (2 cases) |
| grid rows and columns swapped | 10 tests, the R7 5x4 grid and Magick's size among them |
| `iloc` base offset ignored | **nothing at first**: neither the builder nor Canon writes a non-zero base offset. `ItemData_ThroughBaseOffsetsAndSeveralExtents_ReadsBackWhole` was added and now catches it (its two base-offset cases) |
| HRD `cpb_size_du_scale` not read | the VUI+HRD test and the truncation sweep |
| inter-predicted RPS counting only "used" entries | the inter-RPS test alone |
| 4:2:2 conformance window scaled vertically too | the conformance-window theory (its 4:2:2 case) |
| emulation prevention not resetting its zero count | the emulation-prevention round trip |
| VUI default display window not read | the VUI+HRD test, the inter-RPS test, the truncation sweep |
| the intra constraint read from the bit before it | the envelope tests on both Canon files and the SPS test |

The pattern behind the one gap is the JBIG2 MMR lesson again: a reader is only tested on the field
values its fixtures happen to contain. Both writers (`HeifBuilder`, `HevcBitWriter`) put a known value
after every variable-length structure for that reason.

**A trap in the mutation harness itself.** Restoring a file by moving its backup back restores the
backup's older timestamp, so MSBuild judged the last mutant's DLL up to date and the next run tested
the mutant. Touch the sources (or rebuild) after a mutation run.

## Code shared with the other codecs

Checked against what the family already has, 2026-11-08:

| Piece | Verdict |
|---|---|
| ISOBMFF box walking | **Share.** `Jp2Reader.ReadBox` (JPEG 2000), `JxlContainer` and now `IsoBoxes` each walk the same 14496-12 box header. Candidate: move `IsoBoxes` + `BigEndianReader` into `SharpAstro.Codecs.Abstractions` as `internal` with `InternalsVisibleTo`, the arrangement `MqDecoder` and `Lzw` already use, and point the other two at it. Worth doing when AVIF (the same HEIF container, AV1 inside) is next. |
| Exif item | **Reuse as is.** A HEIF Exif item is a 4-byte offset then TIFF data; `SharpAstro.Exif`'s `ExifReader.FromTiff` takes it from there. |
| PQ / HLG transfer, BT.2020 | **Share at rung 3.** `PngHdr` (`PqInverseEotf`, the HLG OOTF, the 65536-entry table build) is exactly what a PQ HEIF needs for a float projection. Lift it out of `SharpAstro.Png` rather than copy it. |
| 4:2:2 chroma upsampling | **Probably not.** `JpegResample`'s H2 kernel is stb's, chosen for byte-exactness with JPEG; what HEIF needs is set by the chroma siting and by whatever the pixel oracle (libheif) does. Measure before reusing. |
| Entropy coding | **No.** CABAC (adaptive binary arithmetic coding with a 64-state machine) shares only the idea with VP8's boolean decoder (fixed probabilities) and the MQ coder (a different state table and renormalisation); JXL uses ANS. |
| Transforms and intra prediction | **No.** HEVC's integer DCT and DST constants are its own, and every codec here is held to bit-exactness against its own reference, so a shared "IDCT" would serve none of them. VP8's intra modes are cousins of HEVC's but differ in every detail. |
| Limits / budgets | **Pattern, not code.** Same 2^28 ceiling as JBIG2 and JPEG 2000; a shared budget type could come if a third codec wants the threaded form. |
