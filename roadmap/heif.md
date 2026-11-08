# Roadmap: HEIF / HEVC stills (`SharpAstro.Heif`)

> **Status: written 2026-11-08; rungs 1 (container) and 2 (HEVC intra decode) landed on
> `heif/external-fixtures`.** Issue #66. The
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
- **A narrow slice of intra HEVC** (counted by rung 2's decode statistics): luma uses DC and every fourth
  angle only (modes 1, 6, 10, 14, 18, 22, 26, 30, 34), never planar; chroma uses modes 1, 10 and 26,
  which the 4:2:2 remap leaves unchanged. Decoding these files right proves little about the rest of
  intra HEVC, which is why rung 2 also has x265 fixtures.
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

### Rung 2: HEVC intra decode (done)

`HevcDecoder.Decode(hvcC, itemData)` (internal) decodes one IDR picture to sample planes at the coded
size (`HevcPicture`; the conformance window and the crop to `ispe` are rung 3's). Clean-room from H.265:

```
HevcDecoder          (parameter sets from hvcC, the envelope check, the size charge, slice dispatch)
  → HevcSliceHeader  (7.3.6.1 for IDR I slices; refuses SAO/deblocking on, dependent slices, tiles/WPP)
  → HevcCabac        (9.3: engine, decision/bypass/terminate; initType 0 contexts; over-read capped)
  → HevcPictureDecoder (7.3.8 coding quadtree / unit / transform tree / unit / residual_coding;
                       6.4.1 z-scan availability; 8.4.2 MPM; 8.4.3 + Table 8-3 chroma modes;
                       8.6.1 QP prediction per quantization group; reconstruction per transform block)
  → HevcIntraPrediction (8.4.4.2: substitution, [1 2 1] and strong filtering, planar, DC, 33 angles)
  → HevcTransform    (8.6.2-8.6.4: scaling with lists, DST 4x4, DCT 4-32, the two-stage shifts)
  HevcScalingList    (7.3.4 / 7.4.5: coded lists kept, defaults, ScalingFactor expansion with DC)
  HevcScan           (6.5.3-6.5.5 scan orders)
```

**The envelope as shipped** is wider than the Canon one, because the fixtures below validate more:
4:2:0 and 4:2:2; 8, 10 and 12 bits (luma and chroma equal); CTB 16-64; transforms 4x4-32x32; scaling
lists (default and coded); `cu_qp_delta` at any depth; PPS and slice chroma QP offsets; strong
intra smoothing; partial CTBs at the picture edge; several slices. Refused by name, before any sample is
allocated, with the rung that owns it: 4:4:4 and monochrome, mixed bit depths, PCM, every range-extension
tool, sign data hiding, transform skip, transquant bypass, tiles, WPP, dependent slices, cross-component
prediction, chroma QP offset lists, non-IDR pictures, SAO and deblocking when switched on.

**Validated by, strongest first:**

- **ffmpeg, live, plane by plane, no tolerance** (`HevcOracle`): HEVC is bit-exact by specification.
  On Windows, `Oracle/heif/fetch.sh` (pinned BtbN LGPL 9.0 build); on Linux and CI, `apt-get install
  ffmpeg`. Gated by `OracleGate`. All 25 Canon pictures match: the R7's thumbnail, preview and 20 grid
  tiles, and the R6 CR3's two previews (`HevcDecoderOracleTests`, skipped without the fetched files).
- **Seven committed x265 fixtures** (`Fixtures/hevc`, about 66 KB together, made by
  `Oracle/heif/make-fixtures.sh` in WSL): ffmpeg's decode is the expected output, written as a digest
  only when **libde265 agrees**, so they run with no tool at all, and are checked live against ffmpeg as
  well. Each crosses one axis: every mode at CTB 64 with 32x32 transforms; adaptive QP with the default
  lists and chroma offsets; 4:2:0 8-bit with a conformance window and partial edge CTBs; QP 2 at CTB 16;
  coded scaling lists with their own DC values and QPs across the whole 4:2:0 chroma table; 12-bit;
  8-bit 4:2:2 at CTB 16. `Fixture_CoversWhatItIsFor` checks each still does, from the decoder's own
  statistics (`HevcDecodeStatistics`): all 35 luma modes in every fixture, NxN units, remapped 4:2:2
  chroma modes, 32x32 blocks, non-zero QP deltas, every qPi of Table 8-10, distinct DC weights.
- **Damage**: 600 random byte changes and about 50 truncations of every fixture's slices
  (`Fixture_DamagedSlices_AreRefusedCleanly`) end in a decode or `InvalidDataException`, never an index
  out of range, an overflow or a hang.

**What the fixtures found.** The first cut matched ffmpeg on all 25 Canon pictures while its Table 8-3
(the 4:2:2 chroma mode remap) was wrong in two entries: mode 11 maps to 12, not 11, and mode 14 to 17,
not 16. The Canon pictures cannot see that (their chroma modes map to themselves); the x265 4:2:2
fixtures each failed until both were corrected, and each correction alone left one of them failing.
The corrected values are also what the geometry gives: a horizontal mode's slope doubles in a block
half as wide, and mode 14's -13 doubled is mode 17's -26 exactly.

**Speed, first cut, single-threaded:** about 300 ms for a 1792x960 R7 tile, so about 6 s for the whole
6960x4640 image; 200 ms for the 1620x1080 preview, under 20 ms for a 320x320 thumbnail. Rung 5.

**x265 notes for whoever regenerates the fixtures:** it is not byte-reproducible run to run even
single-threaded, so the fixtures and the digest file are rewritten together; 3.5 writes several slices
only with WPP (refused), so the Canon pictures cover slices; with identical scaling matrices it writes a
`scaling_list_pred_matrix_id_delta` ffmpeg refuses as out of range, so the list file makes every matrix
distinct; and it reads the list file in one pass, in HM's order (size outermost, then intra/inter).

### Rung 3: composition, HDR output and the facade

From planes to an image: the conformance window and the crop to `ispe` (Canon codes 320x320 for
320x214), the grid's tiles onto one canvas cropped to its output size, then `irot` / `imir` / `clap`.
Then chroma upsampled to 4:4:4 and YCbCr to R'G'B' by the `nclx` matrix (BT.2020 non-constant
luminance, full range, on the Canon files).

**Output is HDR, not tone-mapped.** The PQ signal goes through the facade's float tier, the one
`PngImageDecoder.TryDecodeRgbaFloat` already defines: linear light with **1.0 = 203 cd/m²** (BT.2408
reference white), BT.2020 primaries reported rather than converted. SdlVulkan.Renderer has shown HDR
present through an scRGB swapchain (`tools/HdrProbe`; `R16G16B16A16_SFLOAT`, 1.0 = 80 nits), so a
viewer multiplies by 203/80, converts BT.2020 to BT.709 primaries (scRGB keeps out-of-gamut colour as
negative values) and uploads; an SDR fallback is the viewer's tone map, with the display's SDR white and
headroom from SDL. The PQ curve and its table come from `PngHdr`, moved somewhere both packages can use
rather than copied. `TryDecode` (UInt16, 10 bits scaled to fill) and `TryReadInfo` register with the
facade; `TryDecodeIntoRgba8` stays false for PQ until a tone-map policy exists, as for the other HDR paths.

**The oracle is weaker here than in rung 2.** Upsampling and the matrix are filters and floating point,
not specified bit for bit: compare against Magick.NET's libheif within a measured tolerance, kept apart
from the exact plane comparison (the JPEG 2000 9/7 arrangement).

**Previews are the practical input.** The whole R7 image is 32 Mpixels, 512 MiB as RGBA floats; the
1620x1080 `thmb` item (and the CR3's `PRVW`) is what a viewer wants first, at about 28 MB and 200 ms.

### Rung 4: the rest of intra HEVC

Deblocking (8.7.2) and SAO (8.7.3), transform skip, sign data hiding, PCM, transquant bypass, tiles and
WPP entry points, dependent slices, 4:4:4 and monochrome, CRA/BLA pictures, the range-extension tools.
Each needs an oracle stream that uses it: x265 through `make-fixtures.sh` can make most of them (SAO,
deblocking, sign hiding, transform skip, WPP and so several slices, 4:4:4), the JCT-VC conformance
suite the rest.

### Rung 5: speed

The 20 R7 tiles are independent, so decode them in parallel; then SIMD for the inverse transforms.
Measured end to end through the public API, per the benchmark rules in CLAUDE.md.

## Hardening

Running out of input is not a backstop here either: CABAC, like the MQ coder, keeps producing
decisions from whatever it is fed. So in rung 2 bits past the end of a slice read as zero and more than
64 of them is `InvalidDataException`; every unbounded binarisation prefix (Exp-Golomb, the
`coeff_abs_level_remaining` prefix) is capped; a coefficient level outside 16 bits is refused; a slice
must end by the last CTB; the quadtree recursions halve a block each step; and the decode charges the
SPS's declared picture size against `HeifLimits` before allocating planes.
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

## Mutation check (rung 2)

Twenty-two deliberate bugs, one at a time, against the HEVC tests (`FullyQualifiedName~Hevc`). Twenty
were caught on the first run; the two that were not were closed by a new fixture, after which both fail.

| Deliberate bug | Caught by |
|---|---|
| CABAC MPS state capped at 63, not 62 | Canon (all four) and every fixture |
| context initialisation offset -15 for -16 | the same |
| Table 8-3 as first transcribed (11 and 16) | **the x265 4:2:2 fixtures only**; no Canon picture |
| MPM neighbour-angle candidate off by one | Canon (3) and the fixtures |
| `rem_intra_luma_pred_mode` adjusted with `>` for `>=` | Canon (all four) and the fixtures |
| planar's bottom-left weight (y+1) as (x+1) | **the fixtures only** (Canon never uses planar) |
| [1 2 1] filter threshold for 8x8 blocks 8 for 7 | the fixtures only |
| strong intra smoothing never applied | the fixtures only (it fires on two blocks of one fixture) |
| DC boundary smoothing left out | Canon and the fixtures |
| angular projection without its +128 rounding | the fixtures only |
| DCT for DST on intra 4x4 luma | Canon and the fixtures |
| first-stage transform rounding dropped | Canon and the fixtures |
| scaling-list DC value ignored | **nothing at first**: x265's default lists and Canon's coded ones have a DC equal to their first weight. `x265-420p10-chromaqp-lists` (coded lists with their own DC) was added and now catches it |
| QP prediction ignoring the same-CTB rule | Canon (2) and the fixtures |
| 4:2:0 chroma QP table, one entry off | **nothing at first**: no 4:2:0 fixture reached qPi 34. The same new fixture covers qPi 30..43 and now catches it |
| `sig_coeff_flag` +3 for later sub-blocks left out | Canon and the fixtures |
| greater1 context set never incremented | Canon and the fixtures |
| Rice parameter threshold 4x for 3x | Canon and the fixtures |
| 4:2:2 second chroma cbf only when not split | Canon and the fixtures |
| availability ignoring slice boundaries | **Canon only**: x265 3.5 writes several slices only with WPP |
| a quantization group keeping the previous group's prediction | Canon and the fixtures |
| `split_cu_flag` context ignoring the above neighbour | Canon and the fixtures |

The two halves need each other: six bugs only the fixtures see, one only Canon's multi-slice pictures do.

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
