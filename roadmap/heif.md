# Roadmap: HEIF / HEVC stills (`SharpAstro.Heif`)

> **Status: written 2026-11-08; rungs 1 (container), 2 (HEVC intra decode) and 3 (composition, colour,
> HDR float output, facade) landed on `heif/external-fixtures`.** Issue #66. The
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
  → HevcSliceHeader  (7.3.6.1 for IDR I slices, loop-filter settings included; refuses dependent slices, tiles/WPP)
  → HevcCabac        (9.3: engine, decision/bypass/terminate; initType 0 contexts; over-read capped)
  → HevcPictureDecoder (7.3.8 coding quadtree / unit / transform tree / unit / residual_coding;
                       6.4.1 z-scan availability; 8.4.2 MPM; 8.4.3 + Table 8-3 chroma modes;
                       8.6.1 QP prediction per quantization group; reconstruction per transform block)
  → HevcIntraPrediction (8.4.4.2: substitution, [1 2 1] and strong filtering, planar, DC, 33 angles)
  → HevcTransform    (8.6.2-8.6.4: scaling with lists, DST 4x4, DCT 4-32, the two-stage shifts)
  HevcScalingList    (7.3.4 / 7.4.5: coded lists kept, defaults, ScalingFactor expansion with DC)
  HevcScan           (6.5.3-6.5.5 scan orders)
  HevcPictureDecoder.LoopFilter (rung 4: 7.3.8.3 sao() per CTB; 8.7.2 deblocking and 8.7.3 SAO over the
                       finished picture)
```

**The envelope as shipped** is wider than the Canon one, because the fixtures below validate more:
4:2:0 and 4:2:2; 8, 10 and 12 bits (luma and chroma equal); CTB 16-64; transforms 4x4-32x32; scaling
lists (default and coded); `cu_qp_delta` at any depth; PPS and slice chroma QP offsets; strong
intra smoothing; partial CTBs at the picture edge; several slices. Refused by name, before any sample is
allocated, with the rung that owns it: 4:4:4 and monochrome, mixed bit depths, PCM, every range-extension
tool, sign data hiding, transform skip, transquant bypass, tiles, WPP, dependent slices, cross-component
prediction, chroma QP offset lists, non-IDR pictures. (Rung 4 has since added deblocking and SAO; see
there for what of them is still refused.)

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
6960x4640 image; 200 ms for the 1620x1080 preview, under 20 ms for a 320x320 thumbnail. Rung 5 took the
whole image to 0.27 s (tiles in parallel) and a picture's decode to about half.

**x265 notes for whoever regenerates the fixtures:** it is not byte-reproducible run to run even
single-threaded, so the fixtures and the digest file are rewritten together; 3.5 writes several slices
only with WPP (refused), so the Canon pictures cover slices; with identical scaling matrices it writes a
`scaling_list_pred_matrix_id_delta` ffmpeg refuses as out of range, so the list file makes every matrix
distinct; and it reads the list file in one pass, in HM's order (size outermost, then intra/inter).

### Rung 3: composition, HDR output and the facade (done)

`HeifDecoder.Decode(file, item = null)` gives a `HeifRgbImage`: full-range R'G'B' codes at the image's
own bit depth, with the file's primaries and transfer. Each coded picture is cropped by its conformance
window and then to the size the file declares (Canon codes 320x320 for 320x214 and says so only in
`ispe`); a grid's tiles are placed row by row and cropped to the output size; then `clap`, `irot` and
`imir` are applied in the order the item lists them (23008-12 requires clap, irot, imir; libheif follows
the file, so this does too). An essential property this reader does not know is refused. Pass an item from
`HeifFile.ThumbnailsOf` to decode a preview without touching the full image.

**The YCbCr conversion was fitted to libheif, and it is H.273's.** Nearest-neighbour chroma (each sample
repeated; bilinear differed by up to 479 codes), the matrix in double precision in code units (normalising
to [0, 1] and back cost exactness on some samples), rounded half up. Against libheif through Magick.NET:

| Case | Result |
|---|---|
| Canon: both CR3 previews, both R7 thumbnails, **the whole 6960x4640 R7 grid** | **exact**, every sample |
| x265 full-range 4:2:2 at 8, 10, 12 bits under BT.601, BT.709, BT.2020 | **exact** |
| narrow range (4:2:0 and 4:2:2), 4:2:0 at 8 bits, no colour box (the VUI, narrow here) | within 2 codes on under half the samples (measured 1.2-45 %, max 1 except BT.709 narrow 10-bit at 2): libheif's own integer shortcuts; tested as a tolerance, apart from the exact cases |
| `irot` 90/180/270, `imir` 0/1, `clap`, and combinations, on 4:2:0 | the same geometry as libheif; only the narrow-range residual differs |

Things learned from libheif, by measurement, all documented on `HeifDecoder`:

- `imir` axis 0 swaps **top and bottom** and 1 left and right; the rung 1 model had them the other way.
- libheif applies the transforms to the Y'CbCr planes. Rotating a 4:2:2 image therefore leaves it with
  vertically subsampled chroma, which it then upsamples with a filter (up to 117 codes from this decoder's
  replicate-then-rotate); and whenever it rotates it converts with the stream's VUI, not the file's `nclx`.
  The transform tests use an `nclx` equal to the VUI and 4:2:0 so that both paths convert alike.
- A `clap` whose edge falls on a half pixel is rounded by libheif one way alone and the other way when a
  rotation follows; the combined tests use a whole-pixel window. No camera file sets `clap`.
- Magick.NET stores 10- and 12-bit codes shifted into its 16-bit quantum (not rescaled) and 8-bit ones
  times 257; its Q16-HDRI build hands back floats.

**Output is HDR, not tone-mapped.** `HeifRgbImage.ToLinearRgbaFloat` and `HeifImageDecoder.TryDecodeRgbaFloat`
use the float tier `PngImageDecoder.TryDecodeRgbaFloat` defines: linear light, **1.0 = 203 cd/m²** for PQ
and HLG, BT.2020 primaries reported, not converted. The curves are `HdrTransfer`, which moved from
`SharpAstro.Png` (`PngHdr`) into `SharpAstro.Codecs.Abstractions` (internal, `InternalsVisibleTo` Png and
Heif), so a PQ PNG and a PQ HEIF reach a viewer on the same scale from the same table.

**The facade** (`HeifImageDecoder`, registered after WebP): `CanDecode` is the `ftyp` brand check;
`TryReadInfo` gives the size after `clap` and `irot`; `TryDecode` gives three-channel UInt16, codes scaled
to fill; `TryDecodeIntoRgba8` refuses PQ and HLG (no canonical 8-bit projection) and scales SDR images.
Alpha (`auxl`) and depth items are not composed.

**Speed and size, single-threaded:** the R7 grid in about 6 s (HEVC is nearly all of it); its R'G'B' is
194 MB and as RGBA floats 517 MB. The 1620x1080 thumbnail is the practical preview, about 200 ms.
SdlVulkan.Renderer issue #165 (flight SDL165) is the display side: an opt-in scRGB swapchain and float
textures, with no cost to the SDR path.

### Rung 4: the rest of intra HEVC (in progress)

In the order real files need them: deblocking and SAO (almost every HEIC outside Canon's has both on),
then sign data hiding and transform skip (x265 defaults), then 4:4:4 and monochrome, then the rest
(tiles and WPP entry points, so several x265 slices; dependent slices, PCM, transquant bypass, CRA/BLA
pictures, the range-extension tools). Each needs an oracle stream that uses it: x265 through
`make-fixtures.sh` can make most of them, the JCT-VC conformance suite the rest.

**Deblocking and SAO (done).** `HevcPictureDecoder.LoopFilter.cs`, from 8.7.2, 8.7.3 and the `sao()`
syntax of 7.3.8.3, applied to the whole picture after its last slice (8.7 has every intra prediction read
unfiltered samples): vertical edges of the whole picture, then horizontal edges over the result, then SAO
over a copy of the deblocked planes. In an intra picture the boundary strength of every transform edge on
the 8x8 grid is 2, and those are all the edges (an NxN prediction split forces the transform split), so the
decoder records transform edges as it goes (two bits per 4x4 block) and nothing else. Each edge belongs to
the coding unit on its right or below, whose slice gives the beta and tC offsets; the chroma filter's QP
is the luma QPs' mean plus the **PPS** chroma offset alone, through Table 8-10 for 4:2:0.

Validated by ten more x265 fixtures, each committed only when ffmpeg and libde265 agree, all matching on
the first run:

| Fixture | What it is for |
|---|---|
| `x265-420p8-deblock` | deblocking alone at a high QP: strong, weak (and p1/q1) luma, skipped segments, chroma |
| `x265-422p10-deblock-offsets` | 4:2:2 10-bit, CTB 64, beta -2 and tC +2 offsets, PPS chroma QP offsets -4 / +3 |
| `x265-420p10-sao` | SAO alone: band and edge offsets, merges left and up, offsets past 8-bit's cMax |
| `x265-422p10-loopfilters` | both, in the Canon format, CTB 16 (many CTB edges), SAO in 4:2:2 chroma |
| `x265-420p12-loopfilters` | both at 12 bits (SAO offsets to 22 of 31, tC and beta scaled by 16) |
| `x265-420p8-loopfilters-window` | both, a conformance window, every edge class, 8-bit's largest offset 7 |
| `x265-420p8-deblock-highqp`, `-highqp-offsets`, `-lowqp` | QPs spread by strong adaptive quantisation, offsets at +6: between them every Q of Table 8-12 |

`Fixture_CoversWhatItIsFor` pins each fixture's purpose from the decoder's statistics, and
`Fixtures_TogetherReachEveryDeblockingTableEntry` requires every beta' (Q 16-51) and tC' (Q 18-53) entry
to be looked up by some fixture. That test exists because the first mutation check put a wrong tC' at Q 47
and nothing noticed: the first fixtures reached beta Q 19-35 and tC Q 23-39 only. x265 needed coaxing to
use SAO at all: at 128x96, or at a high CRF, it chose "off" in every CTB.

**Still refused or unverified, and why:**

- **A slice after the first that keeps the filters off its boundary**
  (`slice_loop_filter_across_slices_enabled_flag` 0): refused. It changes which samples both filters may
  read and change there, and x265 writes several slices only with WPP, which is not decoded yet.
- **Scaled SAO offsets** (`log2_sao_offset_scale`, a range-extension PPS field): refused; x265 never writes
  one.
- **A SAO band position of 29-31**, whose four bands wrap past band 31 to band 0: implemented as 8.7.3.2
  says, and in no fixture. x265 searches band positions 0-28 only, even for a picture near white (tried).
- **Slice chroma QP offsets in the chroma deblocking QP**: the spec takes the PPS offset alone; x265 writes
  no slice chroma offsets, so no fixture can tell the two apart.

### Rung 5: speed (done)

**Measured end to end through the public API**, base (9e430f1, rung 3) against head, alternating, three
rounds of five in one quiet sitting (arm64, 12 cores), on the Canon R7 file:

| | base | head |
|---|---|---|
| `HeifDecoder.Decode`, the 6960x4640 grid | 4.00-4.05 s | **0.27-0.29 s** (about 15x) |
| `ToLinearRgbaFloat` on it | 197 ms | **31 ms** |
| both thumbnails (320x214 and 1620x1080, one picture each, so no tiles to share out) | 193-198 ms | **102-112 ms** |

All of it is bit-identical: every digest, ffmpeg comparison and libheif comparison is unchanged. Five
changes, in order of what they bought (the single-threaded HEVC decode of the 20 tiles in turn, from the
`--heif-report` phase split, went from 4.07 s to 2.15 s):

1. **Tiles in parallel** (`HeifDecoder.Decode`'s `maxDegreeOfParallelism`, default all cores). The tiles are
   independent pictures writing disjoint rectangles of the canvas, so each worker decodes and converts its
   own. Everything that needs the file's bytes, and every refusal a tile's parameter sets carry, happens
   first on the calling thread (a span cannot cross into the workers; an unsupported tile 19 should not cost
   the decode of 0 to 18), and the grid's colour comes from tile 0's SPS. A failure `Break`s the loop, which
   still runs every tile before it, and the **lowest-numbered** tile's exception is thrown, so a damaged file
   fails the same way however the work was scheduled. One GC pause of under 10 ms per decode: allocation is
   not what limits it.
2. **The inverse transform in `Vector128<int>` lanes** (`HevcTransform.InverseVector`; `InverseScalar` is the
   8.6.4.2 transcription, kept as the reference and for hardware without 128-bit vectors). Both stages are a
   row of inputs times one basis value added into a row of accumulators, so the lanes do exactly the scalar
   integer arithmetic, and only the rows and column groups that can hold a non-zero coefficient are visited.
   It was 40% of a tile's profile and is 12% after. `HevcTransformEquivalenceTests` drives both forms (every
   size, DST, 8/10/12 bits, dense, sparse, a lone coefficient at every position, the 16-bit rails, larger
   buffers): the dispatch means one machine only ever runs one of them, as with the JPEG IDCT.
3. **CABAC reads bits from a 64-bit cache** and renormalises in one shift (`LeadingZeroCount`) rather than a
   bit at a time. The over-read limit means what it meant (more than 64 bits *consumed* past the end), and
   `HevcCabacTests` pins it at the boundary.
4. **n bypass bins in one division.** A bypass bin is a step of binary long division of the offset by the
   range (the offset stays below the range), so n of them are the quotient of the offset with n bits shifted
   in, and the remainder is the new offset. `HevcCabac.BypassBinByBin` is the spec's form, and the tests hold
   the division to it over random streams interleaved with context-coded decisions.
5. **Lookups hoisted out of residual coding**: the scan orders are fetched once per block instead of a
   triple-indexed read per coefficient, and `sig_coeff_flag`'s context comes from a table built at start-up
   *from the existing `SigContext` function*, indexed by everything it depends on (size, component, scan, the
   right and below flags, first sub-block or not, position in the sub-block).

Plus the float projection, filled in parallel bands of 2^18 pixels (each pixel depends only on its own
samples; a thumbnail stays on the calling thread). It is a table look-up and store stream into a fresh
16-bytes-a-pixel array, so most of its time is first touches of that array, which threads share well.

Things the next person would trip over:

- **`--heif-report` reads 0.6 s for the parallel decode where a lean process reads 0.27 s.** The report's
  process also holds the 20 decoded pictures, two canvases and a 516 MB float array (about 1 GB live), and
  its 2/4/8-thread figures scale flatly for the same reason. Use it for the single-threaded phase split and
  for A/B of one phase; time the public decode in a process that does only that.
- **Run-to-run noise here is larger than most wins.** Another session compiling on the same machine moved
  the single-threaded decode from 2.2 s to 3.5 s between two runs minutes apart. A/B in one sitting,
  alternating, and check the machine's load first.
- **What is left in a tile:** about 63% residual coding (CABAC decisions, now inlined into it), 24%
  reconstruction (half of that the transform), 10% the coding quadtree. The bin-by-bin arithmetic decoder is
  inherently serial; WPP or tiles inside a picture would parallelise it, but the Canon files use neither.
- Not done: SIMD for the YCbCr conversion (now spread over the workers, 186 ms of CPU in all) and for
  prediction and the residual add.

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

## Mutation check (rung 3)

Twelve deliberate bugs against `HeifDecodeTests` and the PNG float tests. Eleven were caught on the first
run; the twelfth exposed a gap a new test closed.

| Deliberate bug | Caught by |
|---|---|
| chroma taken from the next sample | Canon (both CR3 previews, the R7 thumbnails and grid), the exact x265 cases, the transforms |
| BT.2020 Kb 0.0593 for 0.0594 | Canon and the exact BT.2020 fixture case (one coefficient digit) |
| truncation for round-half-up | Canon, the fixtures, the transforms |
| full-range chroma centred on 2^(n-1) - 1 | Canon and the exact cases |
| narrow-range luma offset 15 for 16 | the tolerant cases and the transforms (beyond 2 codes) |
| grid tiles placed column-major | **the whole R7 grid only**, the one grid there is |
| conformance window's left offset ignored | **nothing at first**: x265 pads only right and bottom, Canon sets no window. `ConformanceWindow_EverySide_SelectsTheRightSamples` (a synthetic picture, expected samples computed by hand) was added and now catches it |
| rotation clockwise | the transforms |
| mirror axes swapped | the transforms |
| clap centred without the -1 terms | the transforms (the half-pixel `clap` case) |
| float scale 200 for 203 cd/m² | the HEIF float test and eight PNG float tests (the shared `HdrTransfer`) |
| facade UInt16 by shift, not scaled to fill | the facade test |

## Mutation check (rung 5)

Thirteen deliberate bugs against the HEIF and HEVC tests. Nine were caught on the first run; three exposed
tests too weak to see them (two compared the new code with itself), which were strengthened and now catch
them; one is unreachable.

| Deliberate bug | Caught by |
|---|---|
| stage 1 skips the last coefficient row | 58 tests (Canon, fixtures, libheif cases) |
| the last column group dropped | 52 tests |
| stage 2 reads one intermediate column short | 57 tests |
| stage 1's 16-bit clamp left out | **only the equivalence tests' rail cases** (15): no real picture reaches the rails |
| renormalisation one bit short | 62 tests |
| bypass remainder replaced | 62 tests |
| the over-read limit a byte early | **only** `ReadingPastTheEnd_IsAllowedFor64BitsAndNoMore` |
| the significance table's first-sub-block flag inverted | 58 tests |
| the significance table's right and below flags swapped | 58 tests |
| the last failing tile's error thrown, not the first | **nothing at first**: the damaged tile failed at once, so `Break` stopped the loop before the second damaged tile started. The test now cuts the earlier tile's slice in half (it fails after decoding half a picture) and gives the later one no slice (it fails at once) |
| float bands one pixel short; HLG bands a pixel late | **nothing at first**: the test compared the parallel fill with the same banded code on one thread, which shares any banding bug. It now compares with the per-pixel loop written out in the test, at 1, 3 and all threads |
| `ReadBits` with a single shift, so a count of 0 reads 64 bits | not caught, and cannot be: nothing reads 0 bits (renormalisation shifts by at least 1, `Bypass(0)` returns first). The two-step shift is a guard, kept |

## Mutation check (rung 4: deblocking and SAO)

Twenty-three deliberate bugs against the HEIF and HEVC tests. Sixteen of the first 21 were caught at once;
the misses led to the table fixtures and to two refusals; four cannot be caught by any stream here.

| Deliberate bug | Caught by |
|---|---|
| beta' at Q 29 21 for 20 | the three fixtures with both filters (6 tests) |
| tC' at Q 47 12 for 13 | **nothing at first**: no fixture reached Q 47. Now both high-QP fixtures |
| beta' at Q 16 7 for 6; tC' at Q 18 2 for 1 | the low-QP fixture alone, each |
| strong filter's p2 with 3p3 + 2p2; flatness test against beta >> 2; weak step limit 8 tC; dEp/dEq threshold beta >> 3; weak p1 step with -delta; luma QP from q alone; tC without the 2(bS - 1) term; horizontal edges before vertical; chroma vertical edges every 4 chroma samples | every deblocking fixture (10 tests each) |
| chroma QP without Table 8-10 | the two 4:2:0 8-bit fixtures with deblocking |
| Cb and Cr PPS offsets swapped | only `x265-422p10-deblock-offsets`, the one fixture with different offsets |
| SAO edge classes 2 and 3 swapped | the SAO fixtures (6 tests) |
| SAO edgeIdx 0 and 1 left unmapped | the SAO fixtures (8 tests) |
| SAO in place, reading samples it has already changed | the SAO fixtures (8 tests) |
| edge offsets 3 and 4 positive | only `x265-420p8-loopfilters-window`, the one fixture that uses class 3 enough |
| SAO offsets without `log2_sao_offset_scale` | cannot be: x265 writes no scale. Now refused instead |
| SAO band position not wrapped past band 31 | cannot be: x265 never writes positions 29-31 |
| chroma deblocking QP with the slice offset added | cannot be: x265 writes no slice chroma offsets |
| transform edges recorded off the 8x8 grid | cannot be, and is not a bug: the filters visit the 8x8 grid only, so the extra marks are never read |

## Code shared with the other codecs

Checked against what the family already has, 2026-11-08:

| Piece | Verdict |
|---|---|
| ISOBMFF box walking | **Share.** `Jp2Reader.ReadBox` (JPEG 2000), `JxlContainer` and now `IsoBoxes` each walk the same 14496-12 box header. Candidate: move `IsoBoxes` + `BigEndianReader` into `SharpAstro.Codecs.Abstractions` as `internal` with `InternalsVisibleTo`, the arrangement `MqDecoder` and `Lzw` already use, and point the other two at it. Worth doing when AVIF (the same HEIF container, AV1 inside) is next. |
| Exif item | **Reuse as is.** A HEIF Exif item is a 4-byte offset then TIFF data; `SharpAstro.Exif`'s `ExifReader.FromTiff` takes it from there. |
| PQ / HLG transfer, BT.2020 | **Shared (rung 3).** `PngHdr` moved to `SharpAstro.Codecs.Abstractions` as `HdrTransfer` (internal, `InternalsVisibleTo` Png and Heif); HEIF's float projection uses its tables. |
| 4:2:2 chroma upsampling | **Probably not.** `JpegResample`'s H2 kernel is stb's, chosen for byte-exactness with JPEG; what HEIF needs is set by the chroma siting and by whatever the pixel oracle (libheif) does. Measure before reusing. |
| Entropy coding | **No.** CABAC (adaptive binary arithmetic coding with a 64-state machine) shares only the idea with VP8's boolean decoder (fixed probabilities) and the MQ coder (a different state table and renormalisation); JXL uses ANS. |
| Transforms and intra prediction | **No.** HEVC's integer DCT and DST constants are its own, and every codec here is held to bit-exactness against its own reference, so a shared "IDCT" would serve none of them. VP8's intra modes are cousins of HEVC's but differ in every detail. |
| Limits / budgets | **Pattern, not code.** Same 2^28 ceiling as JBIG2 and JPEG 2000; a shared budget type could come if a third codec wants the threaded form. |
