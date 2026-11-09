# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

`src/SharpAstro.*/` — hand-written, clean-room / faithfully-ported image codec packages, each an
independent NuGet shipped in lockstep (shared Major.Minor + CI run-number patch):

- **`SharpAstro.Codecs`** — the facade: magic-byte sniff + dispatch over `IImageDecoder`.
  Consumers reference this one package instead of cherry-picking individual codecs.
- **`SharpAstro.Codecs.Abstractions`** — the base: `IImageDecoder` (static-abstract sniff +
  fidelity/zero-copy decode) plus `IDecodedImage` / `RasterImage`.
- **codecs** — `Tiff`, `Png`, `Jpeg`, `Jxr`, `Exr`, `Jxl`, `Jbig2`, `Jpeg2000`, `Webp`, `Radiance`, `Gif` (decode only), `Heif` (HEIF/HEVC stills: grids, transforms, PQ as linear floats; see `roadmap/heif.md`), `Exif`,
  `Color.Icc`, `Jpeg.IccInjector`, `Jpeg.GainMap` (Ultra HDR read/write; facade-registered
  *ahead of* the plain JPEG decoder).

`SharpAstro.Jpeg`'s full-scale decode was built as a faithful port of the stb_image (StbImageSharp)
JPEG path (IDCT constants, upsampling kernels, fixed-point colour convert) and validated
**byte-exact against that reference decoder**. The stb port has since been removed from the repo;
the guarantee is preserved as a committed golden digest baseline
(`tests/SharpAstro.Codecs.Tests/Fixtures/jpeg-oracle-golden.tsv`, driven by `JpegDecoderOracleTests`
— regenerate with `REGEN_JPEG_ORACLE=1`). The scaled-decode (1/2–1/8) reduced IDCT is clean-room
DCT-domain decimation — deliberately NOT ported from libjpeg's `jidctred.c`, which is IJG-licensed
(this repo is Unlicense).

The full 8×8 IDCT ships **two kernels**: `Idct8x8Scalar` (the stb transliteration) and
`Idct8x8Simd`, a four-lane `Vector128<int>` form that `Idct8x8` dispatches to wherever the
hardware has SSE2/NEON — i.e. essentially always. It is worth **−27% on a 4:2:0 decode and
−38% on 4:4:4** (measured; see "Benchmarks" below). Three things about it are load-bearing:

- **It is derived from our own scalar kernel, not read from libjpeg-turbo's SIMD IDCT.** Same
  line already drawn around `jidctred.c` — libjpeg-turbo is BSD-ish but notice-retaining, which
  Unlicense cannot absorb.
- **It stays in int32 lanes.** libjpeg-turbo packs eight 16-bit lanes per register, which needs
  extra descaling and *changes the rounding*. Int32 lanes are only four wide, but they make every
  lane bit-identical to the scalar path by construction — and byte-exactness against the digests
  is the entire point. A faster-but-differently-rounded IDCT would be worthless here.
- **The all-AC-zero shortcut is diluted, deliberately.** The scalar test is per column; the vector
  one is per group of four, so it only fires when all four columns are DC-only. That was the main
  risk — but measured against zigzag-clustered coefficients the vector path still wins ~2× even on
  very sparse blocks, because AC energy concentrates in the low columns and the high group keeps
  hitting the shortcut. Benchmark against *uniformly scattered* AC and you will flatter it by
  hiding exactly this effect.

Because the dispatch means any one machine only ever exercises **one** of the two kernels, the
golden digests alone cannot catch the other one rotting. `JpegIdctEquivalenceTests` drives both
directly and demands byte equality — including at the clamp rails, at `short` extremes where the
int32 intermediates overflow (both must overflow the *same* way), and with a non-trivial
offset/stride, which is where the SIMD row pass's scatter-by-stride would show an indexing slip.

**Decode runs through strips, not planes** (issue #28). stb IDCTs every block into whole-image
component planes and only then upsamples; `JpegCore` now IDCTs one *unit* (an MCU row of an interleaved
scan, a block row of a single-component one) into a per-component strip of `unit rows + 2`, assembles every
output row that completes, hands it to an `IJpegRowSink` (`JpegDestinationSink` for `DecodeTo`,
`JpegAllocatingSink` for `Decode`) and moves on. Sample memory is a function of the image's *width* and
sampling, not its height; progressive keeps its coefficients whole (every scan is needed) and streams the
IDCT from them in `Finish`. `JpegCore.Run` owns the lifetime. Things that are not obvious from the code:

- **The row assembly is stb's, verbatim.** The `YStep`/`Line0`/`Line1` state machine is kept; `Line0`/`Line1`
  are now plane *row indices* and the strip maps them to offsets. A row is emitted when the deepest plane row
  it reads (`Line1`, or `Line0` for a near-only kernel on the upper half) is below the component's `Avail`. The
  strip slides up to `Line0` before each unit. Two rows of margin are exact, not slack: a frame whose
  components need different far rows (Magick's CMYK is YCCK with 2×2 Y and K over 1×1 chroma) retains two.
  `BeginUnit` throws rather than overrun, and margin 1 trips it on 4:2:0, 4:4:0, the mixed layouts and CMYK.
- **The reference for the strip engine is the old engine, kept.** `JpegDecoder.DecodeWithStats(forceWholePlanes:
  true)` runs whole planes, which is the pre-strip pipeline, and `JpegStripDecodeTests` demands byte equality
  with it on 8 sampling layouts (mixed kernels and vertical factors up to 4 included), odd sizes, every scale,
  baseline and progressive, grey, CMYK, RGB-marked, hand-built restart streams, truncations and corrupted RSTn.
  The golden digests cannot see the salvage paths (scan ends early, no scan, bad marker): leaving out the
  zeroing of the unfinished unit passes every golden digest and fails exactly two of these tests.
- **When it cannot stream it keeps whole planes, allocated at exact size** (a rent rounds a 33,660,000-byte
  plane up to 64 MiB): baseline whose first scan does not carry every component, and the
  **restart**. A baseline stream with a *second* scan, or with an Adobe/JFIF marker *after* the scan (stb
  converts with the colour state at the end of the stream; the streamed rows used the state at the start of the
  scan), is decoded again with whole planes. So an `IJpegRowSink` must accept **rows from 0 again**, each
  overwriting the last; a sink that accumulates across rows resets on row 0. Not valid files, but today's
  decoder decodes them one specific way and the restart keeps it.
- **`DecodeTo` is checked against the frame header, then written as it decodes.** The size check moved from
  after the decode to before it. A stream that turns out corrupt part-way leaves the rows before the failure
  in the caller's buffer, where it used to leave it untouched.
- `JpegDecodeStats.PeakPlaneBytes` counts the strip/plane arrays held (rented length, so pool rounding is
  in it); line buffers and progressive coefficients are not. `Run` returns it; the public API discards it.
  `EntropyUnits` / `TransformedUnits` count MCU rows entropy-decoded and IDCT'd; the region cost tests read them.

**`DecodeRegionTo` is the same engine narrowed, not a second one** (issue #28 ask 1). `SetRegion` leaves every
full-decode default (`_unitStart/_unitEnd`, `_cropX/_cropW`, `_outEnd`, each component's `WinA/WinN/BlkLo/BlkHi`) at its
whole-image value, so there is one `EmitRow` and one `StreamScan`. What is not obvious:

- **Rows are seeded in closed form.** The `YStep/YPos/Line0/Line1` loop has no data dependence, so `SeedRows` jumps it
  to the region's first row; the units the first and last row read (`PlaneRowsRead`) give the margin for V2/Hv2's far
  row with no special case. A kernel that reads only the near row (H2, generic) does not pull the unit above in.
- **Columns run the resample kernels unchanged over a window**, widened one sample each side for H2/Hv2 and clipped to
  the row, then crop. The kernels treat the first/last sample they are given as the image edge, so the window's interior
  edge spoils exactly the margin that is cropped away. Note stb's *right* edge is not the mirror of its left
  (`3*in[w-2] + in[w-1]`), so an "interior-span variant" of the arithmetic would have to reproduce that; running the
  real kernel avoids copying it. A region's strips hold only those block columns (`Stride`/`ColBase`).
- **Restart skip-ahead counts markers, and trusts neither their numbers nor the interval sizes.** It requires RST0..7 in
  sequence and falls back to the top of the scan otherwise (a deleted marker shifts every later one, and only the
  sequence check sees it). It cannot check that each skipped interval really holds DRI MCUs; a stream whose intervals do
  not is corrupt in a way only decoding them would show. The region also never reads past its last unit, so a second
  scan or an Adobe/JFIF marker *after* the scan (both already restart the full decode) goes unseen unless the region
  reaches the end of the scan. Documented on `DecodeRegionTo`; both are invalid files.
- **Mutation-checked** (deliberate bugs, one at a time): no horizontal margin (31 tests fail), no far-row margin (35),
  RST numbers unchecked (the damaged-marker test), the partial-unit clear ignoring `ColBase` (the two salvage tests,
  the only ones that reach it), and the skip-ahead disabled (exactly the two cost tests: the pixels are still right,
  so nothing else can see it).

**`DecodeFittedTo` is a sink, not a third engine** (issue #28 ask 2). `JpegFittedSink` is an `IJpegRowSink` that
box-filters the rows `JpegCore` hands it, so everything above (strips, region, restarts, whole-plane fallback) applies to
it unchanged, and the full-resolution RGBA never exists: the engine converts a row into the sink's one scratch row, the
sink adds it into the sums of its target row, and writes the target row when that box's last source row arrives. Held:
one source row, `targetWidth * 4` sums, `targetWidth + 1` column bounds; allocated after a warm-up, about 21 KB
whatever the height (measured: byte-identical at 1300 and 2600 rows). What is not obvious:

- **The filter is the viewer's `RgbaBoxDownsample.Shrink`, line for line, and that is a claim about partitions.** Shrink
  writes the box as `[dx*w/newW, min(w, max(lower+1, (dx+1)*w/newW)))`. With `newW <= w` the `max`/`min` never fire (the
  upper bound is the next box's lower bound), so the boxes tile the source and one pass over the rows equals "convert
  everything, then filter". With `newW > w` they would overlap, so a larger target is an `ArgumentException`, not an
  approximation. The test file transcribes Shrink (with its `int` sums) and demands byte equality, not "within 1".
- **The sums are `long`, Shrink's are `int`.** Shrink wraps for a box over 2^31/255 (about 8.4 M pixels): a 5100x6600
  scan fitted to 1x1 is 33.7 M. Everything Shrink computes without overflow is identical; where it wraps this gives the
  mean. `ABoxOfMoreThanEightMillionPixels_IsTheMean_NotAWrappedSum` is the only test that sees `int` sums (the mutation
  that wraps them fails that and nothing else), so keep it.
- **A region's rows are the image's row numbers.** The sink is told the image's size in `Begin` but its source is the
  region's, and its first row is `region.Y`, so a restart "from row 0" is "from the first row" there. The start-over on
  the first row is defensive (a restart delivers every row first, so the sums are already empty); only
  `TheSink_BeginsOverWhenTheRowsComeAgainFromTheFirst`, which abandons an attempt part-way, can see it.
- **No `ThrowIfTooLarge`.** The destination sinks refuse a raster over `int.MaxValue` bytes; this one never holds one, so
  a source of any size the frame allows can be fitted. The destination check is the target's size.
- **The cost is the region's.** The same `PlanRows`/restart skip-ahead apply, so a fitted decode of a region of a
  row-restart scan entropy-decodes about that region's MCU rows (`AFittedDecodeOfARegion_CostsWhatTheRegionCosts`).
- **Mutation-checked** (deliberate bugs, one at a time, all caught): no `+ n/2` rounding (140 tests fail), column bounds
  rounded up (140), row bounds rounded up (140), region rows not offset by `region.Y` (71), a full-height RGBA allocated
  in `Begin` (the two allocation tests, with 20.8 MB against the 260 KB bound), `int`-wrapping sums (the overflow test
  alone) and no start-over on the first row (the sink restart test alone).

**Measured on the issue's scan shape** (3.27; `JpegDecodeScanBenchmarks`, 5100x6600 baseline 4:4:4, DRI = 638, built in
`GlobalSetup` through Magick.NET because `JpegEncoder` writes no restart markers; the setup throws unless the stream really is
SOF0, 1x1 x3 and DRI 638, since a silently missing DRI would turn every case into plain baseline). Full 333.8 ms; region of the
middle third 111.1 ms (**0.33**; 275 of 825 MCU rows, a third of the RGBA); fitted 3165x4096 526.9 ms; fitted region 151.8 ms;
decode-then-shrink 432.8 ms and 178 MiB per call. Things the next person would trip over:

- **The fitted decode is a memory win and, since the sink's perf review, a time win.** The figures above are the first cut
  (527 ms against 433 ms for decode-then-shrink). The cost was `JpegFittedSink`'s per-channel 64-bit division in `WriteTargetRow`
  and its scalar adds in `Accumulate`. It now sums each source row into per-column `uint` sums in `Vector128<uint>` lanes
  (`AddRow`) and divides by a reciprocal multiply-high (`WriteTargetRowNarrow`; Lemire fastdiv, `d == 1` the identity). In one sitting:
  fitted 3165x4096 396.5 ms against 516.0 ms for the old sink (-23%) and 431.1 ms for decode-then-shrink; fitted middle third
  134.9 against 172.6 ms (-22%); fitted 510x660 344.9 against 361.8 ms (-5%, the thumbnail ratio has little to add). Three things to keep:
  - **The 32-bit sums are exact only up to a box of 16,777,215 pixels** (`MaxNarrowBox`, 2^24 - 1: 255 x that is below 2^32). Above it
    the sink takes the old `long` path (`Accumulate`/`WriteTargetRow`), which is also the reference: `forceWideSums` on
    `DecodeFittedToWithStats` and the sink's constructor selects it. `TheWideAndTheNarrowPath_AgreeWithTheReferenceAndEachOther`
    and `ABoxJustInsideTheNarrowPathsRange_IsTheMean` pin both and the boundary; the 5100x6600 -> 1x1 test stays on the wide path.
  - **The A/B is `forceWideSums`**, kept as a parameter and not a static, with the three `*Wide` cases in `JpegDecodeScanBenchmarks`.
  - **A BenchmarkDotNet run is `--job short`** (3 iterations) here, so single cases swing by 10-30% between sittings (the same
    whole-image fit read 452 and 396 ms). Only compare cases from one run.
- **BenchmarkDotNet's `Allocated` cannot show strip memory**: strips are pooled, so after the first iteration a decode allocates
  next to nothing. `dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks -- --jpeg-scan-report` prints
  `PeakPlaneBytes`, MCU rows entropy-decoded and IDCT'd and the byte-equality check against Decode plus a box filter, through
  `InternalsVisibleTo` from `SharpAstro.Jpeg` to the benchmarks project. Strips are 0.19 MiB in every case against 3 x 64 MiB before.
- **The benchmark JPEG is 7.6 MiB, the issue's were 1.1-1.6 MB** (noise on every channel), so entropy decoding is a bigger share
  of a full decode here than on a real scan. The ratios that matter (a third of the rows, a third of the bytes) do not depend on that.
- **`Magick.NET` is referenced by the benchmarks project** with the NU1901-NU1904 audit warnings silenced (it only encodes a
  self-generated raster here). The `--filter` still matches the namespace and type, so `*JpegDecode*` runs both the small cases
  and the 33.7 MP ones; they are separate classes so the small ones do not pay for that setup.

`SharpAstro.Jpeg` also ships a **second, structurally unrelated decoder**: `LosslessJpeg`
(ITU-T T.81 Annex H, SOF3) — Huffman-coded sample-difference predictors 1–7, no quantisation,
**up to 16-bit precision**, DRI/RSTn restarts, point transform. It shares no code with
`JpegDecoder` (which handles SOF0/1/2 only). Its real consumer is outside this repo: the
**FC.SDK.Raw** repo decodes Canon CR2 IFD3 raw strips (14-bit Bayer as a 2-component interleaved
lossless sub-frame) through it. Keep it working — `LosslessJpegTests` uses hand-crafted minimal
bitstreams, because real CR2 payloads are too large to commit.

`SharpAstro.Jbig2` (3.8) is the odd one out in the family: its primary entry point is **not** the
facade. PDF's `/JBIG2Decode` filter hands over an *embedded stream* (T.88 §D.3) that has no file
header to sniff, keeps its shared segment dictionaries in a separate `/JBIG2Globals` stream, and
takes its dimensions from the image dictionary — none of which fits `(bytes) -> image`. So the API
is `Jbig2Decoder.Decode(embedded, globals, width, height)`, and `Jbig2ImageDecoder : IImageDecoder`
is registered only as a courtesy for standalone `.jb2` files. **All five rungs have shipped** — the
MQ arithmetic decoder, generic regions in both codings (arithmetic GBTEMPLATE 0–3 with TPGDON and
arbitrary AT pixels, **and MMR / ITU-T T.6**), refinement regions, symbol dictionaries + text
regions, pattern dictionaries + halftone regions, page info, and composition. What is left is the
**Huffman-coded variants** (SDHUFF/SBHUFF + custom table segments) and a few flags — see "JBIG2
codec" below, which lists them and why each is refused rather than guessed.

`CODECS.md` documents the per-package decode/encode matrix (its `SharpAstro.Jxr` row reflects
the jxrlib re-port). See "JXR codec" below for the architecture and validation discipline.
Longer-horizon work lives in the roadmap docs under `roadmap/`: [`roadmap/jpeg-encoder.md`](roadmap/jpeg-encoder.md),
[`roadmap/gain-map.md`](roadmap/gain-map.md), [`roadmap/pdf-codecs.md`](roadmap/pdf-codecs.md)
(JBIG2's remaining Huffman variants), [`roadmap/jpx.md`](roadmap/jpx.md) (the JPEG 2000 rung
table, rungs 1, 2 and 5 shipped), [`roadmap/heif.md`](roadmap/heif.md) (HEIF/HEVC rungs and the measured Canon envelope),
[`roadmap/jxl.md`](roadmap/jxl.md) (the VarDCT decode's LOH
allocation question, and the feature envelope the codec currently refuses), plus
[`JXR-FORMAT.md`](JXR-FORMAT.md) for the per-axis JXR support breakdown.

## Build & test

Requires the **.NET 10 SDK**. There is no separate lint step (CI is build + test + pack).

```bash
# Canonical solution for development — CI builds/tests/packs this one.
dotnet build Codecs.JustTests.sln -c Release
dotnet test  Codecs.JustTests.sln -c Release
```

**Solution gotcha:** the xunit codec test project `SharpAstro.Codecs.Tests` is in
`Codecs.JustTests.sln` (the CI build/test/pack target), not in `Codecs.sln` (which carries
only the library projects). When working on the codecs, use `Codecs.JustTests.sln` or the
individual project — `Codecs.sln` won't see the tests.

```bash
# Iterate on one project (fast):
dotnet test tests/SharpAstro.Codecs.Tests/SharpAstro.Codecs.Tests.csproj

# Run a subset by name (xunit FullyQualifiedName filter):
dotnet test tests/SharpAstro.Codecs.Tests/SharpAstro.Codecs.Tests.csproj --filter "FullyQualifiedName~Jxr"
dotnet test tests/SharpAstro.Codecs.Tests/SharpAstro.Codecs.Tests.csproj --filter "FullyQualifiedName~JxrGrayscaleOracle"
```

Tests: `SharpAstro.Codecs.Tests` uses **xunit v3 + Shouldly** (+ Magick.NET for visual
diffing and deterministic input encoding) and covers the whole codec family.

### Benchmarks

`benchmarks/SharpAstro.Codecs.Benchmarks` (BenchmarkDotNet, pinned to the same 0.15.8 the
sibling `tianwen` repo uses). It is in `Codecs.sln` but **deliberately not in
`Codecs.JustTests.sln`**, so CI's build/test/pack contract is untouched and CI does not restore
BenchmarkDotNet to run an exe it would never use.

```bash
dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks -- --filter '*'
```

Note the filter matches `namespace.type.method`, **not** the `[Benchmark(Description = ...)]`
text — `--filter '*full scale*'` silently returns zero benchmarks.

It measures **whole decodes through the public API**, not internal kernels, and that is the
point rather than laziness: a kernel that gets faster in isolation but does not move this number
has not made decoding faster. That mistake has already been made here once — a `Vector<T>`
colour-convert kernel was written, reasoned to be an improvement, and measured afterwards at
**42% slower** than the scalar loop it replaced. The lesson generalises past that one kernel:

- **`YCbCrToRgbaRow` is store-bound, not compute-bound.** Writing 4 bytes/pixel is ~6.2 ms of a
  ~7.7 ms kernel; all the fixed-point colour math is the other ~1.4 ms. So the ceiling for
  vectorizing it is ~1.5% of decode, and the best attempt (`Vector128` + a real shuffle-based
  interleaved store) captured about a third of that. `JpegResample` was assumed to be the same
  shape, worse — one `(3*a+b+2)>>2` per output byte — and **was measured to be the opposite** (issue #28's
  perf review): it writes one byte plane with contiguous vector stores (H2/Hv2 interleave even and odd outputs in-lane as `even | odd<<8`), not a 4-byte-per-pixel store. `RowSimd` (V2/H2/Hv2 in 16-bit
  `Vector128` lanes, the stb sums fit exactly; edges and tail scalar; `RowScalar` is the stb transliteration and
  the reference) took a 4:2:0 2048x1536 `DecodeTo` from 24.7 to 21.7 ms (**-12%**; a noisier second run
  41.9 -> 30.6 ms), where an isolated row loop predicts about 10x. `JpegResampleEquivalenceTests` drives both
  forms (widths 1-100 through two vector bodies and the tail, 16-bit lane limits, row offsets inside a larger
  plane, short destination and plane still throw). The A/B was a temporary static switch, removed: only
  `JpegUpsampleBenchmarks`' per-row pairs and a whole-decode case remain. Not tried: the grey, RGB-marked and
  CMYK interleave loops in `JpegCore.EmitRow`, which are store-bound like `YCbCrToRgbaRow` and serve rare inputs.
- **`Vector<T>` has no portable interleaved store.** Extracting lanes to scratch and then running
  the same per-pixel byte loop adds the vector work *on top of* the scalar work instead of
  replacing it. Where an interleaved store is needed, `Vector128.Shuffle` (pshufb / tbl) is the
  tool; `Vector<T>` suits planar, elementwise, same-width work like tianwen's float kernels.
- **A/B back-to-back, in one sitting.** Run-to-run machine state moved these numbers by >10% here
  (one early baseline read 29.1 ms for a decode that measured 33.6 ms half an hour later), which
  is more than most wins being chased. A number from a previous session is not a baseline.
- **An isolated kernel measurement over-predicts, sometimes by a lot.** Removing a lock and three
  per-call allocations from `JxlDct` measured 5.42 ms → 1.06 ms on "one decode's worth" of calls
  in a tight loop, which projected to ~34% off a JXL decode. The decode moved **5.7%**. A kernel
  timed in a loop with nothing else competing for cache or memory bandwidth is not the same kernel
  running inside a decoder. Micro-measurements are for *deciding what to try*; only the end-to-end
  number decides whether it worked.

The WebP A/B suite compares public `WebpDecoder.DecodeTo` against libwebp's
`WebPDecodeRGBAInto` in one run for 1080p lossy opaque, lossy alpha, and
lossless images. Its four generated WebP inputs are checked in under
`benchmarks/SharpAstro.Codecs.Benchmarks/webp-inputs/`; each is below 200 KB.
Regenerate them with
`bash benchmarks/SharpAstro.Codecs.Benchmarks/generate-webp-inputs.sh`, then
run `dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks
-- --filter '*Webp*'`. The encoder has its own: `--filter '*WebpEncodeBenchmarks*'` is encode time per
Effort on 1080p, and `-- --webp-encode-report` (options `--efforts=0,3,6,9`, `--only=name`, `--no-corpus`,
`--no-1080p`, `--out=file`) prints size and wall time against `cwebp -lossless` at its default and `-z 9`
and checks every output decodes exactly (ours and `dwebp`); it needs `cwebp` / `dwebp` from `Oracle/webp/bin`
or PATH and takes about 11 minutes in full. libwebp is found through `LIBWEBP_PATH`, or else the
platform's usual shared-library names (`libwebp.so.7`, Homebrew's
`libwebp.dylib`, `libwebp.dll`). On Windows, set `LIBWEBP_PATH` to a webp DLL
(e.g. vcpkg's); the official release archive ships only a static library. The animation case measures full
`DecodeAnimation` only. `--profile-webp-lossy` runs a single-thread decode loop
for CPU sampling.

`WebpExternalAnimationBenchmarks` adds a real-world animation (Google's gallery
`1.webp`: 300x225, 100 frames, mostly partial frames alpha-blended over the
previous one), measured both streamed per frame through `WebpAnimationDecoder`
and as a whole `DecodeAnimation`. It is third-party with no stated licence and
380 KB, so it is **fetched, not committed**:
`bash benchmarks/SharpAstro.Codecs.Benchmarks/fetch-webp-external.sh` downloads
it into the git-ignored `webp-inputs/external/` and checks a pinned SHA-256.
Without it the class contributes no benchmarks.

**BenchmarkDotNet fails while pilot flights exist** ("Found more than one
matching project file for SharpAstro.Codecs.Benchmarks"): it locates its project
by name under the repo root and also finds the copies in `.claude/worktrees/*`.
Pass `--inProcess`, or run from a checkout with no worktrees beneath it.

Package versions are **centrally managed** in `Directory.Packages.props` — add a
`<PackageVersion>` there and reference it without a version in the `.csproj`. All packages
ship in lockstep (shared Major.Minor + CI run-number patch). Project conventions: `net10.0`,
`IsAotCompatible=true`, SourceLink/embedded debug, `Nullable=enable` + `ImplicitUsings=enable`.

## JXR codec — jxrlib re-port (the most involved subsystem)

`SharpAstro.Jxr` is a **faithful, table-exact C# re-port of Microsoft's jxrlib C** (the earlier
spec-derived codec produced "garbage after the first block" and was retired). The re-port was
built up incrementally and validated bit-exact at each step; it landed on **`master`** via
PR #1 (merge commit `5b2f3f2`) and first shipped to NuGet at **3.0.211**.

Support has widened well past that first landing. Current state (see [`JXR-FORMAT.md`](JXR-FORMAT.md)
for the per-axis breakdown with ticks):

- **Bit depths** — BD8 / BD16 / BD16F / BD32F, plus **signed BD16S / BD32S** (native FITS BITPIX 16/32).
- **Channels** — grayscale (Y-only) + RGB, plus **planar alpha** (32bppBGRA, byte-exact vs `JxrEncApp -a 2`).
- **Chroma** — YUV444, plus **YUV420 / YUV422** subsampling encode *and* decode at every overlap level.
- **Structure** — SPATIAL **and FREQUENCY** ordering; single-tile **and multi-tile soft tiling**
  (`INDEX_TABLE`, all formats); POT (OL_NONE/ONE/TWO); arbitrary (non-16-aligned) dimensions
  (pad-then-crop, not WINDOWING_FLAG); lossy QP byte-exact vs `JxrEncApp -q N`, incl. per-channel QP on read.

Still **out of scope**: hard tiling and `WINDOWING_FLAG` — the reference `JxrEncApp` can't emit
them, so they can't be byte-validated, and byte-validation is the whole point of this port.

Architecture (encode pipeline; decode mirrors it):

```
JxrImageCodec (facade: Encode/Decode Rgb24 / Gray8)
  → JxrContainer        (.jxr TIFF-like container: IFD + PixelFormat GUID + codestream blob)
  → JxrCodestream       (IMAGE/PLANE headers + SPATIAL band multiplex; the codestream layout)
  → SignalTransform     (pixels ⟷ YUV/Y planes: color transform + level shift + idxCC layout)
  → OverlapTransform    (whole-image POT overlap + 2-stage Photo Core Transform across the MB grid)
  → TileCoder           (per-MB DC/AD/AC + CBP neighbor prediction; CWMIPredInfo row buffers)
  → MacroblockCoder     (per-MB DC/LP/HP band entropy coding + CBP, over a shared CodingContext)
  → adaptive primitives (CodingContext, AdaptiveHuffman, AdaptiveScan, CoefficientSyntax,
                         BlockCoder, CbpPrediction, ModelBits, VlcTables, Quantization)
```

Color-format support is widening rung by rung: the YUV444 RGB path and the Y-only grayscale
path coexist. The entropy/transform classes branch internally on `CodingContext.ColorFormat`
/ `Channels` (mirroring jxrlib's `cf` / `cNumChannels`) — **keep the validated YUV444 path
byte-identical when adding a new format; the existing tests are the regression guard.**

### Validation discipline (this is what makes the port trustworthy)

The codec is checked against the **jxrlib reference binaries**, not just for self-consistency
(a self-consistent-but-wrong codec passes a round-trip). Three layers, strongest last:

1. **Golden-vector** component tests — fixed inputs run through real jxrlib C functions
   (`Oracle/probe/`) baked into unit tests.
2. **Self round-trip** — our-encode → our-decode is lossless.
3. **Oracle byte-match** — our codestream must be **byte-for-byte identical** to what
   `JxrEncApp` emits for the same image/settings, and decode both directions against
   `JxrEncApp`/`JxrDecApp`.

The oracle binaries (`tests/SharpAstro.Codecs.Tests/Oracle/bin/JxrEncApp.exe`,
`JxrDecApp.exe`) are **git-ignored**; build them once with
`bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh` (clang, no MSVC/CMake). Oracle tests
**skip gracefully** when the binaries are absent. The jxrlib C source under
`Oracle/jxrlib-src/` is the **port source-of-truth** — when porting a path, read the
corresponding `segenc.c` / `segdec.c` / `strenc.c` function and match it exactly.
`JXRLIB_TRACE` (env var on the prebuilt apps) + our `Trace.cs` give per-MB diffs for
debugging divergences.

## JBIG2 codec — clean-room from T.88, staged in rungs

`SharpAstro.Jbig2` is **clean-room from ITU-T T.88** (and T.4/T.6 for the MMR path), and that is
not a stylistic preference —
it is forced. This repo is **Unlicense (public domain)**, which is stricter than "permissive":
notice-retaining code cannot be relicensed into it. `jbig2dec` is **AGPL** (unusable as a port
source, full stop); pdf.js, PDFium, and `jbig2enc` are Apache-2.0 / BSD-3, which still means
notice retention. All four are fine as oracle *binaries* — running a program is not linking to
it — but none may be read-and-transcribed. Same line already drawn for libjpeg's `jidctred.c`.

Structure (`src/SharpAstro.Jbig2/`):

```
Jbig2Decoder        (public: Decode(embedded, globals, w, h) / DecodeFile / TryReadFileInfo)
  → Jbig2Segment    (T.88 §7.2 segment headers + §7.4.1 region info)
  → DecodingState   (nested: results keyed by segment number, so segments can refer
                     to each other -- symbol/pattern dictionaries, intermediate regions)
  → GenericRegionDecoder  (§6.2.5: GBTEMPLATE 0-3 context templates, TPGDON, AT pixels)
  →   MqDecoder     (Annex E arithmetic decoder; a ref struct over the coded span)
  → MmrDecoder      (§6.2.6 = ITU-T T.6: changing elements, pass/vertical/horizontal modes)
  →   MmrCodes      (T.4 Tables 2-4 as literal bit strings, expanded to peek lookups)
  → RefinementRegionDecoder (§6.3: GRTEMPLATE 0/1 over a reference bitmap + dx/dy)
  → SymbolDictionaryDecoder (§6.5: height classes, refine/aggregate symbols, export runs)
  → TextRegionDecoder       (§6.4: strip layout, REFCORNER/TRANSPOSED, per-instance refine)
  →   ArithIntDecoder       (Annex A: the integer + symbol-ID coders every number uses)
  → HalftoneDecoder         (§6.6/§6.7: pattern dictionary + Gray-coded bitplanes)
  → Jbig2Bitmap     (byte-per-pixel bilevel + OR/AND/XOR/XNOR/REPLACE composition)
Jbig2Image          (public result: Bits = 1 byte/pixel, 1 = black; ToGray8 / ToRaster)
Jbig2ImageDecoder   (IImageDecoder courtesy adapter, standalone .jb2 only)
```

Two invariants worth not breaking:

- **Polarity.** `Jbig2Image.Bits` is T.88's own: **1 = black**. PDF's `/Decode` and `ImageMask`
  invert that conditionally, but that is image-dictionary policy, not codestream meaning, so the
  codec emits one documented polarity and the PDF layer decides. Same refusal the facade makes
  about tone-mapping HDR float.
- **Context templates.** What conformance turns on is **which pixels a template reads** (the
  set, AT offsets included) — *not* which bit each one lands in. A context value is only an
  index into adaptive-state slots that all start identical, so permuting the bit positions is a
  bijection: it preserves which pixels share a slot and the coded bytes come out unchanged.
  Established empirically, not by argument — swapping two template bits leaves jbig2dec still
  agreeing with us; changing one template pixel's *coordinate* makes it disagree at once.
  The numbering is still written to match T.88 Figures 4-7 exactly, because TPGDON's SLTP
  decision uses **hard-coded** contexts (0x9B25 and friends) that name a specific neighbourhood
  in the spec's numbering — permute the real contexts and a different neighbourhood collides
  with that slot.

Validation, weakest to strongest. Layers 1-3 need nothing installed and run in CI
unconditionally; layer 4 skips **visibly** (`Assert.SkipUnless`, so it is reported as a skip
rather than a silent pass) when jbig2dec is absent:

1. **T.88 Annex H.2 conformance vector, both directions** — the published 256-decision sequence
   and its 30-byte codestream. `Jbig2MqEncoder` (test-only; encoding is a shipped non-goal) reads
   the shipped `Qe` table rather than duplicating it, so the vector validates what actually ships.
2. **One-hot template tests** — each template cell set alone must produce exactly its own power
   of two, plus a sweep asserting non-template neighbours contribute nothing. Pins the literal
   T.88 numbering; see the caveat above about what that does and does not buy.
3. **Round-trip through synthetic streams** — `Jbig2StreamBuilder` builds real segment streams and
   `.jb2` files. Validates the MQ integration, scan order, TPGDON, and the segment layer; it is
   structurally blind to the templates, since its encoder calls the decoder's own `Context`.
4. **Third-party bytes, both directions** — the ones that actually catch template errors:
   - **jbig2enc → us**: committed `Fixtures/jbig2/*.jb2` produced by jbig2enc, decoded and
     compared. No tooling needed at test time. Covers GBTEMPLATE 0 / nominal AT only — that is
     all jbig2enc emits (asserted, so a fixture regeneration can't quietly change it).
   - **us → jbig2dec**: `Jbig2Oracle` shells out to the reference decoder for every template,
     moved AT pixels, and TPGDON on/off — the cases jbig2enc can't produce. Resolves `jbig2dec`
     on PATH, else through WSL (`wsl.exe -- jbig2dec`; there is no native Windows build). Install
     with `apt-get install jbig2dec`, or `wsl -- sudo apt-get install -y jbig2dec`.

Both jbig2dec (AGPL) and jbig2enc (Apache-2.0) are **binaries only** here — running a program is
not linking to it, and encoder output is data. Neither may ever be read as a port source.

### MMR (rung 2) — validated only against foreign bytes

The arithmetic path has three layers before an external oracle is needed. **MMR has none of
them**: encoding is a non-goal, so there is no round-trip, and the T.4 run-length tables have no
self-consistency to check — a mistyped entry yields a wrong run length and nothing internal
notices. So MMR is validated entirely on third-party bytes, and the harness is the cheapest in the
repo: `Group4Tiff` (test-only) has **Magick.NET/libtiff** encode a raster as CCITT Group 4, strips
the TIFF wrapper off, and feeds the codestream in. Magick.NET is already referenced for the EXR and
JXL harnesses, so this needs no install, no build step, and no skip — it runs everywhere including
CI. `Jbig2MmrOracleTests` then pushes the same bytes on through the segment layer and past
jbig2dec.

Two things learned building it, both worth keeping:

- **Coverage has to be per table entry.** Mislabelling white run 24 as 25 left every
  picture-shaped pattern green, because none happens to contain a white run of exactly 24.
  `EveryRunLength_DecodesToItself` now sweeps every length T.4 defines, using rows shaped so the
  encoder has no choice but to spell the run out (past vertical mode's ±3 reach, with an all-white
  reference line so pass mode would swallow the row). Slope patterns were added for the same
  reason — VR2/VR3/VL2/VL3 were barely exercised by pictures. Against eight deliberate bugs the
  layers score 7/8 (pictures), 6/8 (run sweep — its rows are all horizontal mode, so mode-logic
  bugs walk straight through) and 8/8 (hand vectors, two of them structurally rather than by
  decoding). Together they are 8/8; **none of them is 8/8 for the right reasons alone**, which is
  why all three stay. The matrix is in `Oracle/jbig2/README.md`.
- **ImageMagick writes `PhotometricInterpretation = 1`** (BlackIsZero) for bilevel, and libtiff's
  fax coder ignores that tag — it codes bit 0 as a *white* run either way. So the coded runs come
  out inverted with respect to T.88, and `Group4Tiff` flips its input to cancel it. The photometric
  is probed once rather than assumed.

### Rungs 3-5 — the rest of T.88, and how each was validated

All five rungs have landed. The later three each needed a different oracle, because no single
tool emits all of them:

| Rung | What validates it |
|---|---|
| 3 — symbol dictionary + text region | **jbig2enc `-s`**, whose *primary* mode this is. `Fixtures/jbig2/sym*.jb2` are real symbol-coded bytes, committed with jbig2dec's raster as the expected result (symbol matching is lossy by default, so there is no hand-writable grid). Covers only jbig2enc's one shape — arithmetic, bottom-left corner, one strip, no refinement — so the other seven corner/transposed combinations, multi-row strips, SBDSOFFSET, SBREFINE and SDREFAGG go through `Jbig2SymbolBuilder` and **jbig2dec**. |
| 4 — generic refinement | Nothing third-party emits refinement (jbig2enc's `-r` writes an empty file), so it is synthetic streams through **jbig2dec** only. |
| 5 — pattern dictionary + halftone | Same: no encoder available, so synthetic through **jbig2dec**. Two things there have no other check — the Gray coding of Annex C.5, which a round-trip cannot see, and the §6.6.5.1 lattice, where a cell's position mixes both grid-vector components in 1/256 pixel units. |

Three findings from building them, all established by measurement:

- **A page refinement composites with its declared operator, not a forced REPLACE.** Forcing it
  looks right — under OR a refinement can only ever *add* black, so it cannot clear a pixel — and
  jbig2dec disagreed on every case that clears one. An encoder that wants replacement says so in
  the region info.
- **jbig2dec cannot be the oracle for intermediate regions**: it rejects intermediate generic
  regions outright ("NYI"). The refinement oracle streams therefore refine the *page*, which
  exercises the same §6.3 procedure; the intermediate route is covered against our own decoder,
  which is all that is available for it.
- **TPGRON in a refinement region is refused**, and that was narrowed rather than assumed. For
  GRTEMPLATE 1 a sweep of *all 1024* possible SLTP context values found no candidate that makes
  jbig2dec agree, so the constant is not the cause; for GRTEMPLATE 0 five of six
  reference/target relationships agree and the sixth desynchronises part-way down. jbig2dec emits
  no diagnostic either way and there is no second oracle to break the tie. "Agrees on five of six"
  is a decoder that silently corrupts the sixth.

What is still refused, and why each is a refusal rather than a guess:

| Feature | Reason |
|---|---|
| SDHUFF / SBHUFF + custom table segments (§7.4.13, Annex B) | Not implemented. No available encoder emits them, so there would be nothing to check an implementation against. |
| MMR inside pattern dictionaries and halftone regions | Not implemented; the plumbing exists (`MmrDecoder`) but nothing emits these to check against. |
| HENABLESKIP | Changes *what gets coded*, not just speed — skipped pixels are absent from the stream, so ignoring the flag would desynchronise rather than merely run slow. |
| TPGRON in a refinement region | See above. |
| Symbol dictionaries importing/retaining arithmetic contexts (§7.4.3.1.1 bits 8-9) | A dictionary decoded with the wrong initial contexts yields plausible but wrong glyphs. |

The deliberate *non*-refusal is worth knowing too: an MMR region that also sets TPGDON violates
§7.4.6.2 but still decodes to the right pixels, so it is tolerated. Loud failure is for streams
this decoder would otherwise get **wrong**, not for cosmetic flag violations.

### Resource limits — `Jbig2Limits` and the threaded pixel budget

Every number this codec reads is attacker-chosen (PDF's `/JBIG2Decode` carries whatever the
document says), and two of them size work directly: a region's width and height. **Running out of
input is not a backstop** — T.88 E.3.4 has the MQ decoder read every byte past the end of its data
as `0xFF`, deliberately, so a truncated segment keeps producing decisions forever. Mutation-fuzzing
the committed fixtures found this immediately: flipping four bytes of a region's height made a
127-byte file declare 536 million pixels, and a hand-built worst case reached **2 GiB and 20+
seconds of CPU from 82 input bytes** — while the page it composited onto was 32×16, so every one of
those pixels was clipped away. The old ceiling of `1L << 31` was also off by one: an area of exactly
2^31 passed a `>` test and then overflowed the `checked` multiply in `Jbig2Bitmap`, so a malformed
stream surfaced as `OverflowException` — and `Jbig2ImageDecoder.TryDecode` did not catch that, nor
`OutOfMemoryException`, so both escaped a `bool`-returning `Try` method and the facade
(`ImageCodecs.TryDecode` delegates with no catch of its own).

**Compression ratio cannot be the bound.** A blank 1200 dpi page with TPGDON costs about one
decision per row, so "pixels must be proportional to coded bytes" would reject ordinary faxes.
What works instead is two limits in `Jbig2Limits.cs`:

- **`MaxBitmapPixels` (2^28)** — any single bitmap, enforced in the `Jbig2Bitmap` constructor. That
  placement is load-bearing: **symbol dictionaries never touch region info**, since §6.5 accumulates
  a glyph's width and height from coded deltas with nothing but `> 0` on them, so a one-symbol
  dictionary could otherwise name a 46000×46000 glyph. 2^28 admits a 1200 dpi A4 (≈139 Mpixels)
  with headroom and keeps `width * height` an order of magnitude clear of int overflow.
- **`Jbig2PixelBudget`** — total pixels one decode may produce, `BudgetFor(page) = max(2^26,
  4 × page)`, held on `DecodingState` and **threaded into every decoder that produces pixels**
  (generic, refinement, MMR, text, halftone), charged *before* allocating. It is shared across
  segments precisely so a stream cannot get around it by splitting work into many individually
  plausible regions. Unit tests that drive one decoder directly pass `Jbig2PixelBudget.Unmetered()`.
  The parameter is **required, not optional-with-a-default**, so a new call site cannot silently
  opt out of metering.

The asymmetry worth knowing: in the **PDF path the budget is anchored to the caller's dimensions**
(from the image dictionary), so no codestream can talk its way past it — that is
`EmbeddedPath_BudgetsAgainstTheCallersPageNotTheCodestreams`. The **standalone `.jb2` path has no
such anchor**, because the page size is itself read from the page-information segment. So a hostile
`.jb2` can still declare a large-but-legal page and get a proportionate budget: measured residual is
**≈3.7 s and ≈475 MiB from 127 bytes** (a 16711712×16 page — an absurd shape, but its *area* is
inside 1200-dpi-scan territory). That floor is inherent to supporting large scans at all; a caller
wanting a harder ceiling should pass smaller dimensions or impose its own timeout. Down from
unbounded, not down to zero — `Jbig2ResourceLimitTests` states it as measured rather than implying
the problem is gone.

**Non-termination is a separate trap, and no allocation ceiling can catch it.** Fuzzing also found a
277-byte mutation of `sym.jb2` that spun for ever with **flat memory**: §6.5's outer loop opens a
height class, the first IADW comes back out-of-band, the class closes having coded no symbol, and
nothing has advanced — so with a never-dry MQ decoder it repeats for ever, allocating nothing for a
budget to notice. `SelectExported` had the same shape (an export run of length zero advances no
index, and a *leading* zero run is legitimate, so the guard there is a ceiling on the number of runs
rather than a ban on empty ones). Both now enforce progress explicitly, which is the discipline
`MmrDecoder` already applied to its own row loop — `if (next <= a0) throw`, commented "Standing
still would loop forever". The witness is committed as
`Fixtures/jbig2/nonterminating-symbol-dict.jb2` and its test is **bounded by a timeout**, so a
regression fails the run instead of hanging it. Worth remembering when auditing any other loop here:
the audit question is not "can this overflow" but **"is every iteration guaranteed to advance
something"**, because T.88's decoder will happily feed it `0xFF` for ever.

`Jbig2ResourceLimitTests` is the only place here that builds streams **lying about their size** —
every other builder in the test project encodes a bitmap it actually holds, and so can only declare
dimensions it told the truth about. It also carries the counterweight: A4 at 300/600/1200 dpi must
stay comfortably inside every limit, because a ceiling that rejects real scans is also a bug.

## JPEG 2000 codec — clean-room from T.800, staged by feature not by stage

`SharpAstro.Jpeg2000` is **clean-room from ITU-T T.800** (ISO/IEC 15444-1, Part 1), forced by the
same licence arithmetic as JBIG2: this repo is Unlicense, and OpenJPEG (BSD-2), PDFium and pdf.js
(Apache-2.0 / BSD-3) are all notice-retaining. Binaries as oracles, yes; read-and-transcribe, never.

**On the name.** `SharpAstro.Jpx` would have matched the family convention and PDF's `/JPXDecode`
filter. It is not what shipped, because "JPX" also names **Part 2** — the extended format with
arbitrary colour spaces and multiple codestreams — and Part 2 is precisely what this package
refuses. A package id naming the thing it does not do is a trap for whoever reads the NuGet listing
instead of the csproj. The reasoning is recorded in the csproj too.

### The staging is by feature axis, and that is the whole design

JBIG2's rung 1 was "MQ decoder plus generic region" and it decoded real PDFs on its own, because
T.88's region types are genuinely independent. **JPEG 2000 has no such slice.** Producing one
correct pixel needs marker parsing, tier-2 packet headers, tier-1 EBCOT, the inverse DWT and the DC
level shift all working together; there is no ordering in which an early stop yields an image. So
rung 1 is the *entire pipeline for the simplest legal configuration*, and later rungs widen what it
accepts. Do not try to reproduce JBIG2's shape here — it gives four rungs that each decode nothing,
with no way to tell which one is wrong.

The envelope as of 3.16 (rungs 1, 2 and 5, plus the parts of rungs 3 and 4 that real PDFs carry), and
everything outside it throws `NotSupportedException` naming the feature *and the rung that owns it*:
any number of 1-to-16-bit unsigned components of one precision with no subsampling, one tile, one
tile-part, any number of quality layers, maximal precincts, LRCP, no code-block style flags; 5/3 with
no quantization or 9/7 with scalar quantization (derived or expounded), RCT or ICT when COD asks;
COC/QCC in the main header; a raw codestream, or a JP2 file with no `pclr`, `cmap` or `cdef` box.

```
Jpeg2000Decoder      (public: Decode(codestream or JP2[, reduce]) / ReductionFor / IsCodestream / IsJp2;
                      Jpeg2000ImageDecoder is the facade adapter over it)
  → Jp2Reader        (Annex I boxes: signature, ftyp, jp2h/ihdr/colr, jp2c; the first colr box is
                      REPORTED on Jpeg2000Image.Colour, never applied; pclr/cmap/cdef refused)
  → CodestreamReader (Annex A markers, COC/QCC resolved per component; ALSO where every
                      out-of-envelope refusal lives, by name, before any of the pipeline runs)
  → TileComponent    (Annex B geometry, one per component: resolutions, subbands with their E-3
                      step sizes, code-block grid -- computed from DECLARED numbers, so this is
                      where the ceilings are charged)
  → Tier2            (B.9/B.10 packet headers, LRCP over layer/resolution/component: inclusion,
                      zero bit-planes, pass counts, lengths -- state carried ACROSS layers)
  →   TagTree        (B.10.2) + PacketBitReader (B.10.1, the 0xFF bit-stuffing rule)
  → BlockDecoder     (Annex D EBCOT: three passes per bit-plane over the Table D.1/D.2/D.3 contexts,
                      a block's segments from every layer read as one codeword; E.1.1.2's midpoint
                      reconstruction; dequantisation for a 9/7 band)
  →   MqDecoder      (shared with SharpAstro.Jbig2 -- see below)
  → InverseWavelet   (Annex F: 2D interleave, then HOR_SR then VER_SR; 5/3 lifting in ints, 9/7 in
                      floats)
  → ComponentTransform (G.2.2 RCT / G.3.2 ICT on components 0-2, between the wavelet and the shift)
  → DC level shift   (G.1.2) + rounding (9/7 only, to nearest, ties to even) + clamp, interleaved
```

**Colour is reported, not applied, and that is the PDF rule, not caution.** The component transform
is how the samples were coded, so the decoder undoes it. The JP2 `colr` box is what the samples mean,
and ISO 32000-2 8.9.5 says a PDF image dictionary's `/ColorSpace`, when present, makes the JPEG 2000
colour specification be ignored; only without one is it used. Only the caller holds the dictionary.
The roadmap used to state that rule the other way round.

### The MQ coder moved, and its initialisation did not come with it

`MqDecoder` now lives in **`SharpAstro.Codecs.Abstractions`** (still `internal`, with
`InternalsVisibleTo` for `SharpAstro.Jbig2` and `SharpAstro.Jpeg2000`). T.88 Annex E and T.800
Annex C specify the same coder with the same `Qe` table, so two copies could only drift.
`InternalsVisibleTo` cannot cross two separately shipped packages, and every codec already depends
on the abstractions package, so this placement adds **no** dependency edge — it costs two lines in a
csproj. The alternative, a linked `<Compile Include>` into both projects, was rejected because two
assemblies would then export the same type name to the test assembly, and CS0433 is not fixable by
qualifying, only by `extern alias`.

**The coder is shared; the initialisation is not.** T.800 Table D.7 seeds three of its nineteen
contexts away from zero — zero-coding context 0 at index 4, run-length at 3, uniform at 46 — where
T.88 starts everything at 0. Inheriting JBIG2's initialisation decodes the first code-block
plausibly and then drifts, which is the worst failure signature available. It is pinned in
`Jpeg2000ComponentTests`.

### Validation: the reversible path is exact, and that is the sharpest tool the format offers

A reversible (5/3) codestream reconstructs its encoder's input **byte for byte**. So the committed
`Fixtures/jpeg2000/<name>.pgm` *is* the expected output for `<name>.j2k` beside it: the assertion is
byte equality, with **no tolerance and no oracle process at test time**. That is a better position
than JBIG2 got, where symbol matching is lossy and the expected raster had to come from jbig2dec.

`Oracle/jpeg2000/make-fixtures.sh` verifies the lossless claim with `opj_decompress` per fixture
*before it will commit a pair*, so a fixture that is somehow not lossless is refused rather than
baked in as a wrong answer.

**Keep the exact and the tolerant assertions apart.** The 9/7 irreversible path is
irrational-coefficient lifting and needs a tolerance, so it lives in its own file
(`Jpeg2000IrreversibleTests`) over its own fixtures (`Fixtures/jpeg2000-lossy/lossy97-*`). The
tolerance is **measured against OpenJPEG, not taken from T.803** (not to hand), and the test says so:
every sample within 1, at most 1% of samples differing, where the fixtures reach 0.32% and the two
real 9/7 images 0.15%. The share is the half with teeth: a wrong rounding rule can stay inside a peak
of 1 and move a third of the samples. Letting that tolerance leak back over the exact cases would
throw away the strongest claim here.

**Lossy is not the same as inexact.** A 5/3 codestream whose rate cut its code-blocks short
(`lossy53-*`) has an expected output that is not its source, but nothing irrational happens on the
way, so it is asserted EXACT against `opj_decompress` at test time. That is what caught the midpoint
rule: T.800 E.1.1.2 reconstructs a coefficient whose low bits were cut at the middle of the range they
leave open, reversible path included, and a real PDF image decoded 1,007 samples one off OpenJPEG's
without it.

### What the mutation check measured

Seven deliberate bugs were introduced one at a time and the suite re-run. All seven were caught:
dropped sign XOR bit; raster scan instead of 4-row stripes; T.88's context initialisation instead of
T.800's; HL reading the LL/LH table unswapped; no `0xFF` bit-stuffing in the packet header reader;
`/ 4` and `/ 2` instead of arithmetic shift in the lifting steps; HOR_SR and VER_SR swapped.

The eighth was a **control**, and it behaved as the roadmap predicted: rebuilding the tag trees on
every packet instead of keeping them across layers is **not** caught — the whole corpus still
passes. Tag-tree state spans the packets of successive quality layers, and rung 1 has one layer, so
no fixture here can see the difference. That is hazard 5, confirmed by measurement rather than
argument, and rung 3 must bring a multi-layer fixture built specifically for it.

Bit-stuffing is worth a second note: it was caught by only **2** of the 13 fixtures, because it can
only bite a packet header that happens to contain an `0xFF`. It survives small test images and fails
on real ones.

**3.15 closed the control.** Layers brought three layered fixtures, each with a grid of code-blocks so
the trees have state to carry, and the same deliberate rebuild now fails all three. Eighteen more
deliberate bugs were tried against the new code, one at a time; seventeen fail the suite. Components
and layers: later layers' segments dropped, RCT left out, divided instead of shifted, U and V swapped,
the midpoint reconstruction dropped, packets nested component-outside-resolution, COC or QCC ignored.
9/7: K and 1/K swapped, r = 0, the wrong HH gain, the mantissa ignored, ICT's chroma coefficients
swapped, truncation instead of rounding, the lifting steps misordered, a margin of 2, the derived
exponent rule inverted. The one that passes is rounding halves away from zero instead of to even,
which no fixture can see because float arithmetic all but never lands on an exact half.

### Hardened at rung 1, not in a follow-up release

`Jbig2Limits` / `Jbig2PixelBudget` needed a 3.8 release to add after shipping; `Jpeg2000Limits` /
`Jpeg2000SampleBudget` are here from the start. The reason is the same and it is worth restating:
**running out of input is not a backstop.** T.800 C.3.4, exactly like T.88 E.3.4, has the MQ decoder
read every byte past the end of its data as `0xFF`, deliberately and for ever.

So the ceilings are charged against **declared** geometry, up front, in `TileComponent.Build` —
before a coded byte is touched. 2^28 samples per tile-component (a 1200 dpi A4 is ≈139 Mpixels, so
this admits the largest plausible scan while staying clear of `int` overflow), 2^22 code-blocks
total (a separate limit, because `COD` picks the code-block size independently of the image size and
4x4 is legal, so block count is not proportional to sample count in the direction that matters),
plus a total sample budget scaled to the declared image.

One real bug the limit tests found rather than merely confirmed: `SizMarker`'s tile-count ceiling
division used `(numerator + denominator - 1) / denominator` on two 32-bit codestream fields, which
**overflows** for an image declaring an extent near `int.MaxValue` — so a decompression bomb was
refused for the wrong reason and with the wrong exception type. It computes in `long` now.

The asymmetry JBIG2 documents applies here too, mirrored. JBIG2's *PDF* path anchors its budget to
the caller's dimensions; a raw J2K codestream carries its own, so rung 1's budget is a multiple of
what the stream itself declares. That bounds amplification without bounding absolute size. Rung 4's
PDF entry point gets the tighter anchor.

### Reduced resolution (rung 5) is a decode of less

`Decode(data, reduce)` leaves out the `reduce` finest resolution levels (`opj_decompress -r`), and
`ReductionFor(width, height, minLongEdge)` picks one for a wanted size. Tier-2 still reads every
packet header, because in LRCP the levels left out are interleaved with the ones kept; tier-1 and
the wavelet never touch those levels, and their subbands get no coefficient storage. That last part
is invisible in any output, so `Jpeg2000ReducedResolutionTests.TheLevelsLeftOut_AreNeverDecoded`
overwrites every coded byte of the finest level and requires a full decode to change and a reduced
one not to. The reduction is clamped to the fewest decomposition levels of any component.

### Speed: where a decode's time goes

Measured on a photo-like 2048x1536 RGB image (3.16 against 3.15, alternating, medians of 7):
lossless 5/3 1750 → 1115 ms and 291 → 100 MB, lossy 9/7 548 → 276 ms and 187 → 100 MB, byte-identical
output. Three changes did it, and their order of size is worth knowing before the next attempt:
tier-1 keeps one packed flags word per coefficient (its own state, its eight neighbours'
significance, the straight neighbours' signs), so a context is a table lookup rather than eight
reads, and one `BlockState` serves the whole decode; the inverse wavelet filters columns a whole row
at a time with `Vector<T>`, reading the mirror row at the edges (exact, because both filters are
symmetric, so a lifted symmetric signal stays symmetric); and the passes decode through a local copy
of the MQ coder (about 6%). After them a lossless decode is about 90% tier-1, essentially MQ
decisions, and a lossy one about half and half.

### Registered with the facade in 3.16

JP2 and raw J2K both have magic bytes (`00 00 00 0C 6A 50 20 20 0D 0A 87 0A` and `FF 4F FF 51`), and
`Jpeg2000ImageDecoder` presents what the facade's contract can carry: one component as grey, three
as RGB, a JP2 restricted ICC profile as the image's profile, samples scaled to fill UInt8 or UInt16
(the facade divides by the format's maximum, so a 12-bit 4095 must become 65535). It refuses two or
four components, whose extra channel is alpha or black with nothing to say which (`cdef` is not
read), and sYCC, whose samples are still YCbCr: `TryDecode` answers false rather than wrong colours,
and `Jpeg2000Decoder.Decode` still hands back every component with the colour reported.

## WebP codec — spec-derived from RFC 6386 and RFC 9649

`SharpAstro.Webp` decodes VP8 lossy, VP8L lossless, ALPH transparency, and
animated WebP. Animation frames are full-canvas RGBA8; both materialized
decoding and seeking are supported. ICC, EXIF, and XMP payloads are preserved.
The only encoder is lossless VP8L (`WebpEncoder`, rung 3: the four transforms, LZ77 backward references, colour cache and meta prefix codes, each chosen per image by exact cost, the untransformed image always a candidate from Effort 3; `plan with { Transforms = false }` is the previous rung's configuration for size comparisons; Effort 0 is the literal-only setting of the same encoder, spec-derived from RFC 9649 only; `cwebp`/`dwebp` are black-box oracles fetched by `Oracle/webp/fetch.sh`). The facade returns frame 0 for animated input.
Declared canvases above 67,108,864 pixels are rejected before raster allocation.

This decoder is spec-derived from the **prose and tables** of RFC 6386 / RFC 9649.
**The reference decoder source in RFC 6386 section 20 was not used**: it carries its own
BSD-style licence, which makes it notice-retaining like libwebp. libwebp/libvpx source
was not consulted either; their binaries are used only as test oracles. The author confirmed
this for the contribution (PR #31). Written with AI assistance, whose training data may include
libwebp. Do not read or transcribe RFC 6386 section 20, libwebp, libvpx, or ffmpeg source. The
command-line tools (`cwebp`, `dwebp`, `webpinfo`, `webpmux`, `img2webp`, `gif2webp`, and ffmpeg
with libvpx) generate fixtures or serve as black-box oracles.

**Loop filter: clamp before the shift.** RFC 6386 15.2 computes `c(a + 4) >> 3` and
`c(a + 3) >> 3`, where `c()` clamps to int8. `a` is already in [-128, 127], so leaving out the
clamp only matters for `a >= 124`, where the unclamped result is 16 instead of 15. That takes a
strong edge under a large edge limit, which means a high filter level, which means low quality.
The first review caught this as up to 818 bytes off libwebp on `cwebp -q 10` photos. No q75
fixture reached it. The SIMD-versus-scalar tests could not see it either, because both kernels
had the same bug. Two guards now exist. `AllKernelsMatchRfc6386ArithmeticOnStrongEdges` checks
every kernel against a direct transcription of 15.2-15.3, on edges where it asserts the clamp
actually fires. The `astronaut-192-q{0..20}-{normal,simple}.webp` fixtures (public-domain NASA
photo, via scikit-image) also hit it: 9 of the 10 decode wrong without the clamp.

**Animation memory is charged up front.** Every frame `DecodeAnimation` returns is a full-canvas
copy, so it costs canvas × frames, and a one-pixel `ANMF` frame is about 50 input bytes. Without a
limit, 3 KB declared 64 frames of 4096x4096, which is 4 GiB. The per-canvas `MaximumPixels`
cannot see that. `DecodeAnimation` refuses canvas × frames above `DefaultMaximumAnimationPixels`
(2^28), or above a caller-given limit, before it allocates anything.
`WebpAnimationDecoder` streams through one canvas and is the way to handle larger animations.

**What is left, measured rather than implied.** These numbers are within the limits by design, but
they are larger than "1 GiB of RGBA" suggests. Allocations were counted with
`GC.GetTotalAllocatedBytes` on x64 and agree with the reviewer's arm64 measurement:

| Input | Path | Allocated | Time |
|---|---|---|---|
| 244 bytes: 8192x8192 canvas, 4 one-pixel frames (exactly at the default limit) | `DecodeAnimation` | **1.25 GiB** | 0.13-0.34 s x64 |
| 94 bytes: 8192x8192 canvas, 1 one-pixel frame | `Decode`, and so the facade's `TryDecode` | **512 MiB** | ~55 ms x64 |
| same | `DecodeTo`, tightly packed | **256 MiB** on top of the caller's buffer | ~45 ms x64 |

- **`DecodeAnimation`:** the 1.25 GiB is 1 GiB of returned frames plus the decoder's one working
  canvas. Before round two it was 1.5 GiB, because a second working copy was made and then copied
  from; frames now go straight from the decoder's canvas into the arrays that are returned. The
  reviewer measured 1.4 s for the old 1.5 GiB on an arm64 VM, mostly page-faulting fresh memory.
- **Animated `Decode`:** it needs the working canvas plus the result, two canvases.
- **Animated `DecodeTo`:** it copies the working canvas straight into a tightly packed caller
  buffer. With a padded stride it needs one more canvas to restride from.

A canvas costs about 50 bytes of input however large it is, so this is the floor for anyone who
accepts 2^26-pixel canvases at all. That is the same position the JBIG2 section describes for a
standalone `.jb2`. A caller with a tighter memory budget should pass a smaller
`maximumTotalPixels`, check `ReadInfo` before decoding, or stream.

**A still VP8X canvas must equal its bitstream.** libwebp refuses a mismatch with
`BITSTREAM_ERROR`, and so does this decoder (`InvalidDataException`). Accepting it padded the
bitstream into the top-left corner of whatever canvas the header claimed. That gave plausible-looking
wrong output, and a 56-byte file could turn a 1x1 bitstream into a 256 MiB image through the
facade. Animation frames already had the equivalent check against their `ANMF` rectangle.

The VP8 YUV-to-RGB conversion was fitted black-box against `dwebp`; the
reproducible fit helper is
[`tests/SharpAstro.Codecs.Tests/Fixtures/webp/fit-yuv-to-rgb.py`](tests/SharpAstro.Codecs.Tests/Fixtures/webp/fit-yuv-to-rgb.py).
The per-term truncation is intentional:

```text
mh(x, k) = (x * k) >> 8
R = clamp((mh(Y,19077) + mh(V,26149) - 14234) >> 6, 0, 255)
G = clamp((mh(Y,19077) - mh(U,6419) - mh(V,13320) + 8708) >> 6, 0, 255)
B = clamp((mh(Y,19077) + mh(U,33050) - 17685) >> 6, 0, 255)
```

Still-image and animation frame digests are committed in
`Fixtures/webp/webp-oracle-golden.tsv` and `Fixtures/webp/webp-animation-oracle-golden.tsv`. They
come from libwebp 1.6.0 and run without external tools. Regenerate them with `REGEN_WEBP_ORACLE=1`
and `REGEN_ANIMATION_WEBP_ORACLE=1`.

The live comparisons against `dwebp` and `anim_dump` are separate tests (`*MatchesLiveDwebp`,
`AnimationFramesMatchAnimDump`), gated through `OracleGate`. They skip locally when the tool is
missing and fail under `REQUIRE_ORACLES`. `WebpOracle` resolves each tool from `DWEBP` /
`ANIM_DUMP`, then `Oracle/webp/bin/`, then `PATH`. CI installs the apt `webp` package, which ships
both tools. Ubuntu noble's build is 1.3.2, and it agrees with the 1.6.0 digests on every fixture.
`Oracle/webp/build.sh` builds a 1.6.0 `anim_dump` from the official release archive for anyone
who wants the exact version. Its source files are not read or used by the decoder.

Known oracle divergence: `anim-vp8l-alpha-hint.webp` contains a VP8L animation
frame whose `alpha_is_used` hint is cleared despite transparent decoded pixels.
The frame's RFC 9649 blend flag requires alpha blending; this decoder follows
that rule, while `anim_dump` 1.6.0 emits this fixture's frame uncomposited.
The fixture is excluded from that oracle comparison and remains covered by
compositing and seek-versus-sequential tests.

### The lossless encoder (3.32): provenance, rungs and what was measured

`WebpEncoder.EncodeLossless` / `EncodeLosslessRgb` / `EncodeLosslessGray` write VP8L. **Provenance:** spec-derived from RFC 9649
alone, same line as the decoder: no libwebp, libvpx or RFC 6386 §20 source was read, and `cwebp` / `dwebp` 1.6.0 are black-box oracles
(`Oracle/webp/fetch.sh` puts them in `Oracle/webp/bin`; `WebpOracle` resolves `DWEBP` / `ANIM_DUMP`, then `bin`, then PATH; only the encode report in the benchmarks project looks up `cwebp`, from `CWEBP`, then `bin`, then PATH). `cwebp` is run
with `-lossless -exact`, because our output keeps the colour under transparent pixels. The rungs, each a superset of the last and each
decided **by exact cost** (the real bit count of the candidate, not an estimate):

1. literal-only prefix codes (this is Effort 0);
2. LZ77 backward references with the 2D distance codes, and a colour cache (`Vp8LMatchFinder`, hash chains);
3. the four transforms (subtract green, predictor, cross colour, colour indexing) and meta prefix codes (an entropy image). The pipeline
   is chosen by cost, the untransformed image is always a candidate from Effort 3, and `plan with { Transforms = false }` is rung 2's
   configuration, kept for size comparisons.

`WebpEncodeOptions.Effort` 0-9 (default 6) is a table in `Vp8LEncoderPlan`: window 2^14-2^20, chain depth 2-512, nice length 16-4096,
1-8 finalists (above 2^22 pixels only one), predictor tile sizes, colour cache sizes, lazy matching from 4. **Two caps bound pathological
input, and neither depends on the pixels:** the chain depth and nice length per effort (a one-colour 1024x1024 image costs the chain
depth per position and no more), and `WebpLimits.MaximumEncoderWorkingBytes` (1 GiB), which the plan charges up front and meets by shrinking
the window and then dropping backward references. Dimensions over 16384 or 2^26 pixels are refused before anything is allocated.
`WebpLosslessEncoderPathologicalInputTests` pins all of it: refusal allocates under 64 KiB, and an all-distinct, a one-colour and a
short-period 1024x1024 image encode and round-trip at efforts 0, 6 and 9 within a timeout and a bytes-allocated-per-pixel bound
(measured 14.6 / 859 / 1,375 bytes a pixel for effort 0 / 6 / 9 on the all-distinct image, bounds 32 / 1,300 / 2,100). **That bound is on
bytes allocated over a whole encode, which is cumulative and several times what is live at once**; the live figure is the plan's
charge. A photo allocates more than those images (2.7 KB a pixel at effort 9), so the bound is for those shapes, not for every input.

**Measured** (3.32, one sitting: `--webp-encode-report` and `WebpEncodeBenchmarks`; the full tables are in `CODECS.md` and the CHANGELOG):
at 1080p effort 6 is 1.01x `cwebp`'s default size on the generated photo, 0.84x on the screenshot, 1.13x on a lossless-sourced image and
1.04-1.08x on lossy-sourced ones; effort 9 is 0.99x / 0.80x / 1.01x / 1.03x. Time at effort 6 is 4.8-6.5 s steady-state (8.5-13.8 s cold in the
report) against `cwebp`'s 0.6-1.1 s, so **6-13x slower**; effort 9 is 13-31 s. Effort 0 is 28-45 ms. Things the next person would trip over:

- **Size is not monotone in effort.** The photo is 1.39x at effort 3, 1.29x at 4, 1.02x at 5; `lossless-1080p` is 1.06x at 5 and 1.13x at 6.
  Do not assume a higher effort is smaller, and do not "fix" a test that expects it.
- **The report's first image pays JIT and tiering**, so its times are about 2x the BenchmarkDotNet steady state at efforts 5-7
  (photo effort 6: 13.8 s against 6.5 s). Use BenchmarkDotNet for time, the report for size, and say which one a number came from.
  BenchmarkDotNet's `Error` at effort 6 and above is wider than the gap between neighbouring efforts (3 iterations, one encode each,
  `RunStrategy.Monitoring`), so those rows are good to a factor of about 1.5.
- **cwebp's PNG reader is the only input that works on Windows**: the Windows build fails on PAM through WIC ("IWICImagingFactory_CreateDecoderFromStream failed"),
  so the report writes a PNG with `SharpAstro.Png` and gives that to `cwebp`.
- **Is another stage worth it?** For size, mostly not: the remaining gap is 0-8% on photographic and lossy-sourced content and we are
  ahead on screenshots, palette and alpha images. For **speed** a stage would pay. It has not been profiled, so this is a hypothesis: the
  6-13x is the exact-cost sweep over predictor tiles, cache sizes and finalists, which runs the whole pipeline to count bits. Ranking those
  with a cheap estimate and paying the exact cost only for the winner is the lever to try first, and it should also cut the 1-5 GB of
  cumulative allocation at the top efforts. It is not scheduled (no consumer here has asked for faster encodes); profile first, and
  measure with the two tools above before and after.

## APNG (animated PNG) — spec-derived, Pillow as the independent reference

`SharpAstro.Png` composites APNG frames (`PngReader.EnumerateFrames` streams one shared canvas;
`DecodeFrames` materialises them, refusing canvas x frames above `DefaultMaximumAnimationPixels`, 2^28,
before it allocates). It is written from the **prose of the APNG specification and the PNG spec's alpha
channel processing**, not from any reader's source. The fixtures under `Fixtures/apng` are written by **Pillow**
(an independent encoder, `make-fixtures.py` re-reads its own output to check each case covers what its name
says), and `apng-reference.tsv` holds SHA-256 digests of **Pillow's composited canvases**, which every frame of
every fixture must match. Two rules that are easy to get wrong: a first frame with `dispose_op` PREVIOUS is treated as
BACKGROUND, and a frame's disposal is applied at the start of the *next* frame, never after the last.

**Pillow's OVER is not the spec's.** For frames after the first it pastes the frame with itself as the mask,
a lerp of *every* band including alpha, so over a partly transparent canvas the alpha stays 50% where the spec
gives 75%. The two agree wherever the canvas is opaque (all fixtures but one). `PngCompositor.Over` implements
the spec's formula in exact integers (round to nearest, halves up); `PngApngTests` checks it against a plain
floating-point transcription for every pair of alphas, reproduces Pillow's digests with a model of Pillow's own
paste, and pins how far apart the two are on `apng-blend-over-partial-alpha.png` (up to 91 of 255 in alpha). Do not
"fix" the reader to match Pillow there.

**Speed and memory, measured** (`benchmarks/.../ApngCompositeBenchmarks.cs`, `ApngOverKernelBenchmarks`; table in the 3.28
CHANGELOG entry (APNG block)). The compositor keeps one grow-only samples buffer, snapshot and row scratch for the whole animation and
inflates into them (`DecodeFrameSamplesInto`), so streaming 8 frames of 1080p went 134.5 MB -> 23.7 MB (canvas, IDAT image, samples)
and 362 -> 308 ms; a frame's time is otherwise inflate plus unfilter, which the compositing code does not touch. The OVER blend has
two kernels, `OverRowScalar` (the reference, per pixel through `Over`) and `OverRowVector` (`Vector128`, four pixels a group: copy /
no-op / exact 16-bit blend over an opaque canvas / scalar fallback). The vector one is the Idct8x8Simd arrangement again: byte-equal
to the scalar by construction and by test (`PngApngKernelTests`, with `PngCompositor.ForceScalar` to drive either), kept because it
measured -4% to -6% end to end on 1080p OVER, no more, since the blend is a small share of a frame. Do not expect more from
vectorizing the SOURCE copy, BACKGROUND clear or PREVIOUS snapshot: those are already `Span.CopyTo`/`Clear`. The exact opaque-canvas
form `(af*cf + (255-af)*cb + 127)/255` and `(t + 1 + (t>>8))>>8 == t/255` for t <= 65152 are what make the vector path exact; both are
tested exhaustively.

Refused rather than guessed: a malformed animation (bad sequence numbers, a frame outside the canvas, a missing
frame, a first frame that does not cover the canvas when the IDAT is that frame) throws `InvalidDataException` from
`Decode`, not a silent fall-back to the default image. `Decode` and the `SharpAstro.Codecs` facade return the default
(`IDAT`) image unchanged. There is no 16-bit APNG fixture (Pillow cannot write one); 16-bit frames go through the same
high-byte mapping as any 16-bit PNG.

## PNG cICP float projection — a table beat every vector form tried

`PngImageDecoder.TryDecodeRgbaFloat` (PQ / HLG / sRGB to linear-light RGBA floats) fills its raster from
`PngFloatFill`. Each colour sample is a lookup in a 256- or 65536-entry table that `HdrTransfer` (in Abstractions since HEIF rung 3; it was `PngHdr`) builds with the
**reference's own arithmetic**, so the shipped fill is **bit-identical** to `FillReference` (the scalar spec the tests
and tiny images use) and there is no second kernel to keep equal; `PngFloatFillEquivalenceTests` is the guard. The
flight's numbers are in the `3.26` CHANGELOG entry; the ones worth keeping in mind when touching it:

- **Measured at 1080p** (arm64, BenchmarkDotNet, same sitting): the table fill is **26x** the scalar reference on PQ
  (151.4 → 5.8 ms), 28x on PQ RGBA16, 26x on sRGB RGBA8, and 2.8x on HLG (45.5 → 16.3 ms; its per-pixel OOTF `pow`
  couples the channels, so it cannot be a pure table). Above the shared inflate + unfilter (87.7 ms) a whole PQ decode
  costs +7 %, HLG +20 %. Allocation is one `float[]` plus the reader's pixels and nothing per pixel or per row.
- **Three `Vector128` candidates were measured and dropped**, kept in `benchmarks/.../PngHdrCandidateBenchmarks.cs` so
  the numbers can be reproduced. A float `Vector128` PQ curve is 3.8x the scalar *double* curve but **7x slower than the
  table** and 1.1e-4 off; a vector HLG gain (`Ys^0.2` four pixels at a time) measured 17.9 ms against the shipped 16.3 ms
  and is 5.6e-7 off; a one-`Vector128`-store-per-pixel RGB16 fill is exact but 0.65 ms (0.7 % of a decode) inside the
  shipped path's own noise. The fill is a table-load and store stream, the same store-bound shape as the YCbCr row
  above, so the ceiling for vectorising it is small, and the table already took the 26x.
- **Narrow range is free per pixel** (the expansion lives in the table build) and RGBA16's alpha division costs nothing
  measurable, so neither has a candidate. In-place and strip conversion are not available: the float raster is 2.7x the
  decoded pixels and `PngReader` decodes the whole image first.
- **If you change this, A/B against the shipped table path on the whole `TryDecodeRgbaFloat` as well as the fill.** The
  `PngHdrFloat4kBenchmarks` *timings* are too noisy to decide anything (the 127 MiB array lands on the large-object
  heap); its allocated-bytes column is what it is for.

`SharpAstro.Radiance` (3.29) is the Radiance RGBE (`.hdr` / `.pic`) codec, written for stb_image HDR parity (Codecs #56). stb_image is
public domain, so unlike the notice-retaining sources above it may be *read* for behaviour; the validation does not rest on it,
though. **Magick.NET is the oracle both ways** (`RadianceOracleTests`): ImageMagick-written files must decode to our floats and ours
must decode to ImageMagick's, decoded-to-decoded bit-exact. Things that are not obvious:

- **The value convention is `mantissa * 2^(e - 136)`, with no `+ 0.5`** and `e == 0` black. Radiance's own `colr_color` adds
  the 0.5; ImageMagick (and stb_image) do not. Measured against Magick.NET, and a bias of `e - 135` fails 24 tests.
- **`EXPOSURE=` is reported on `RadianceImage.Exposure`, not applied**, and XYZE is reported (`RadianceImage.Format`), not
  converted: the facade adapter tags it `ColorPrimaries.SMPTE428` so it cannot be read as RGB. Only `-Y h +X w` is read.
- **Pixel ceilings are charged from the header.** `RadianceFile.Read` defaults to 2^28 pixels; `RadianceImageDecoder` uses
  2^26 (`MaximumPixels`) because the facade raster is a byte copy of the floats. A run-length scanline can describe far more
  pixels than it has bytes, so input size is no bound (the same lesson as the JBIG2 and JPEG 2000 sections).
- **The facade adapter is float-tier only**: `TryDecodeIntoRgba8` is always false, like EXR.

## GIF codec — spec-derived, Magick.NET as the oracle, decode only

`SharpAstro.Gif` reads GIF87a/GIF89a: a still (`Decode`/`DecodeTo`, frame 0 on the logical screen, which is what the facade
returns), every frame materialised (`DecodeAnimation`) or streamed through one canvas (`GifAnimationDecoder`). There is no
encoder and a GIF encoder is not to be proposed unless asked. It is written from the GIF89a specification, and where that is open
the answer is **what Magick.NET's `Coalesce()` does**, found by probing, because the tests compare every frame with it byte for byte
(`GifStillTests`, `GifAnimationTests`; Magick is already a test reference, so no install and no skip). Structure:
`GifContainer` (blocks parsed once to `GifInfo` plus file offsets; a file that ends early keeps its frames and says so, only "no
usable header or no image" throws) → `GifPixels` (palette, LZW, interlace, drawing one frame) → `GifCompositor` (disposal, one canvas).

- **LZW is the shared decoder in `SharpAstro.Codecs.Abstractions`** (`Lzw.Decode(LzwFormat.Gif(minCodeSize), ...)`), the same one
  TIFF uses, parameterised by bit order (GIF is LSB-first, TIFF MSB-first), early change (GIF: none, TIFF: yes) and root size
  (min code size 2-8; clear = 2^root, EOI = clear + 1). TIFF's behaviour did not change.
- **What Magick's policy is, and why it is matched rather than "fixed"** (all observed with hand-built files; the numbers are
  in the tests): the canvas before frame 0 is the global table's background-index colour (clamped to the table), opaque, or alpha 0
  with the same RGB when frame 0 has a transparent index, white with no global table. Frame 0 is *copied* (a transparent index keeps
  its RGB, alpha 0); later frames are drawn *over*: opaque replaces, transparent leaves an opaque pixel alone and turns an already
  alpha-0 pixel into 0,0,0,0. Disposal 2 sets the rectangle's alpha to 0 and **keeps its RGB**; disposal 3 restores the rectangle's earlier
  content and on frame 0 that is the initial canvas, not "background"; 4-7 behave as 0. Nothing here is a browser's rule (browsers
  ignore the background colour; `GifInfo` reports the index and the policy is the caller's). The RGB of an alpha-0 pixel is thus quirky
  but deterministic, and matching it exactly is what lets the oracle be a byte comparison.
- **Delays are raw** (1/100 s, 0 stays 0); the browser "0 or 1 means 10 ms/100 ms" clamp is the caller's.
- **Limits are charged up front**, as for WebP and APNG: a screen over 2^26 pixels is refused while the 4 header bytes are read;
  `DecodeAnimation` refuses screen × frames above `DefaultMaximumAnimationPixels` (2^28) or a caller's limit from the parsed
  frame headers, before any canvas exists (a 1x1 frame costs ~15 bytes, so a few hundred bytes can declare 10 GiB). The streaming
  decoder holds one canvas, a grow-only index scratch and a snapshot of one rectangle, only for a frame with disposal 3;
  `GifAnimationTests.Streaming_HoldsOneCanvas_WhereMaterialisingHoldsOnePerFrame` measures it.
- **Truncation:** a frame whose data the file or its LZW stream cut short is drawn partly, flagged (`GifAnimationFrame.IsTruncated`,
  `GifAnimation.IsTruncated`), and the frames before it are exactly what a whole file gives. The unreached pixels keep the canvas.
- **Known unmatched:** an index past its colour table (Magick refuses the file; here it is opaque black) and a missing colour table
  (all black) have no meaningful oracle beyond "Magick reads no-table files as black", see `GifStillTests`.

## TGA — spec-derived, Magick.NET as the two-way oracle, facade by footer only

`SharpAstro.Tga` reads (`TgaReader`, types 1/2/3 and RLE 9/10/11) and writes (`TgaWriter`, 24/32-bit raw or RLE, always with the
TGA 2.0 footer) Truevision TGA, written from the Truevision TGA File Format Specification 2.0. Things that are not obvious:

- **A TGA has no magic number, so the facade recognises it by its footer only.** `TgaImageDecoder.CanDecode` needs the last 18
  bytes to be `TRUEVISION-XFILE.\0` **and** a header `TgaReader` accepts; it never guesses from the header, which would claim
  arbitrary bytes (zeros, noise). It relies on the facade passing the whole stream (`SignatureLength` is the smallest accepted
  file, 18 + 26, not a prefix length) and is registered **last**. A footerless TGA 1.0 file is not recognised by the facade but
  still decodes through `TgaReader.Decode`; Magick.NET writes footerless files, so the tests append the footer to them.
  A footered but corrupt file is claimed, then declined: the `Try` methods return false and never throw.
- **Magick.NET is the oracle in both directions.** Every pixel `TgaReader` produces is compared byte for byte with Magick.NET's read
  of the same file (hand-built, Magick-written and writer-written), and `TgaWriter`'s output is read back by Magick.NET. Magick.NET
  needs `MagickFormat.Tga` stated, because there is no magic number to sniff.
- **An RLE packet running past the end of the image is refused** (`InvalidDataException`), where Magick.NET clips the run and returns
  an image. The same goes for an RLE stream ending early, truncation, out-of-map indices and more than 2^26 declared pixels. Refusing is
  deliberate: loud failure for data the decoder would otherwise get wrong.

## BMP — spec-derived from the DIB headers, Magick.NET as the oracle, stb_image's mask expansion

`SharpAstro.Bmp` reads (`BmpDecoder`: OS/2 core and 2.x, 40/52/56/108/124-byte headers, 1-32 bpp, RLE4/RLE8, bit-field masks,
V5 ICC reported) and writes (`BmpEncoder`: 24-bit BGR with a 40-byte header, 32-bit BGRA with a V4 header and an `0xFF000000`
alpha mask) BMP, written from Microsoft's header documentation, not from stb_image's source. Things that are not obvious:

- **Mask channels of 8 bits or fewer are bit-replicated, as stb_image does; wider ones round from their top 16 bits, as
  ImageMagick does.** So every 8-bit and wider channel equals ImageMagick exactly, and a full channel is 255 at any width.
  ImageMagick's own narrow-channel rules were measured value by value and are NOT copied: 5-bit replicated except 3 -> 25 and
  7 -> 58, a 6-bit *green* replicated except eight values (a 6-bit red or blue is only shifted), every other narrow channel in
  a 16-bit pixel shifted (4-bit 15 -> 240, 1-bit alpha 1 -> 128), and nothing replicated in 32-bit pixels. `MagickExpansion` in
  `BmpDecoderTests` pins each of those, and the masked tests compare both readers EXACTLY, ours against `OurExpansion` and
  Magick's against `MagickExpansion`, over every 16-bit pixel word for 555/565/1555/4444. There is no tolerance anywhere: an
  earlier cut scaled linearly (`(v*255 + max/2) / max`) and hid a one-level disagreement with both stb and Magick behind one.
- **A 32-bit `BI_RGB` file whose fourth byte is zero in every pixel is opaque** (X8R8G8B8 writers leave it zero; stb_image and
  ImageMagick agree). Any non-zero byte makes the whole fourth byte alpha.
- **ImageMagick's `BMP3` with `bmp:subtype` RGB555/RGB565 is malformed:** it says `BI_BITFIELDS` but writes no mask words, so the
  pixels start where the masks should be (ImageMagick misreads its own file). It is refused, and
  `Bmp3_16Bit_FromImageMagick_IsMalformed_AndRefused` keeps that from being "fixed" by guessing.
- **ImageMagick's RLE8 writer encodes each row's padding bytes as pixels**, so a run may overshoot the width into the row's
  4-byte padding; the overshoot is dropped and anything past the padded row is an error. One pixel wide, ImageMagick misreads its
  own RLE file, so there the source raster alone is the oracle.
- **The facade sniff also requires the reserved words at offset 6 to be zero**, deliberately: `BM` alone is a weak magic. stb_image
  does not check them, so such a file still decodes through `BmpDecoder.Decode`, just not through `ImageCodecs`.
- **RLE is the residual amplification:** a few bytes of RLE can declare a large image (up to the 2^28-pixel ceiling, 768 MiB of
  RGB). That is documented in the package README rather than limited further.

## HEIF / HEVC — clean-room from H.265, ffmpeg as the oracle, x265 fixtures for coverage

`SharpAstro.Heif` reads the HEIF container (rung 1), decodes HEVC intra pictures to sample planes (rung 2,
`HevcDecoder`, internal) and composes them into R'G'B' with an HDR float projection, registered with the facade
(rung 3, `HeifDecoder` / `HeifImageDecoder`). Clean-room from ISO/IEC 14496-12,
23008-12, 14496-15 and ITU-T H.265: libheif, libde265, ffmpeg and x265 are programs here, never sources.
[`roadmap/heif.md`](roadmap/heif.md) has the envelope, the rungs and both mutation tables. What a person
touching it should know:

- **The Canon files are not enough, and the decoder can tell you so.** Their encoder uses DC and every fourth
  angle, never planar, and chroma modes the 4:2:2 remap leaves alone; a Table 8-3 with two wrong entries
  matched ffmpeg on all 25 Canon pictures. The x265 fixtures (`Fixtures/hevc`, `Oracle/heif/make-fixtures.sh`,
  WSL) found it. `HevcDecodeStatistics` counts what a decode exercised, and `Fixture_CoversWhatItIsFor`
  pins each fixture's purpose, so a regenerated fixture cannot quietly stop testing something.
- **A fixture is committed only when ffmpeg and libde265 decode it to the same bytes.** Its digest then runs
  with no tool installed; the live ffmpeg comparison runs where ffmpeg is (CI installs it). The one
  exception is named in `make-fixtures.sh`'s `DE265_GAPS` with the experiment that narrowed it:
  `x265-444p10-lists`, where libde265 does not weight 4:4:4 32x32 chroma by the 16x16 list (7.4.5).
- **x265 is not byte-reproducible** between runs, so regenerating rewrites every fixture and the digest
  file together. It writes several slices only with WPP (refused), so slices are covered by Canon alone.
- **The mutation check needs both halves**: six of 22 deliberate bugs are seen only by the fixtures,
  one (slice-boundary availability) only by the Canon multi-slice pictures.
- **Every refusal names the feature and its rung**, before planes are allocated; damaged slices end in
  `InvalidDataException` (over-read capped at 64 bits, every unary prefix capped, levels kept to 16 bits).
- **HDR output, not a tone map**: PQ to the facade's float tier (1.0 = 203 cd/m², as
  `PngImageDecoder.TryDecodeRgbaFloat`), from `HdrTransfer`, which now lives in Abstractions for PNG and HEIF
  both. The display side is SdlVulkan.Renderer #165 (an scRGB swapchain, no SDR cost).
- **The YCbCr conversion is H.273's and libheif agrees exactly for full-range 4:2:2** (every Canon picture,
  the whole R7 grid). Narrow range and 4:2:0 at 8 bits are within 2 codes of libheif, which takes integer
  shortcuts there; those tests are a tolerance, kept apart from the exact ones. libheif also transforms the
  Y'CbCr planes (so a rotated 4:2:2 image resamples chroma) and converts with the VUI when it rotates; the
  transform tests are built so neither matters. `imir` axis 0 swaps top and bottom.
- **Memory from declared sizes (review of PR #69)**: the canvas and each picture's planes are allocated
  before a slice byte is read, so a few-KB file declaring sizes at `HeifLimits` gets proportionate memory.
  Checked first from the SPS (`HeifDecoder.CheckCodedSize`): a picture must hold what the file declares, and
  be at most twice the output area it covers; a grid keeps at most `LivePictureBytes` (1 GiB) of decoded
  tiles alive. The residual (canvas plus one picture, 2.6 GiB measured, about 3.2 GiB worst case) is in
  `HeifLimits`' remarks, as JBIG2's is in its section.
- **Deblocking and SAO (rung 4, first part)** run over the finished picture in `HevcPictureDecoder.LoopFilter.cs`.
  A deblocking table is checked only as far as the fixtures reach into it: the first fixtures reached half
  of Table 8-12 and a wrong tC' at Q 47 went unseen, so `Fixtures_TogetherReachEveryDeblockingTableEntry`
  now requires every entry, and three adaptive-quantisation fixtures supply them. x265 picks SAO "off"
  for small or high-CRF pictures; check `Fixture_CoversWhatItIsFor` after regenerating. Name fixtures on
  `make-fixtures.sh`'s command line to write only those; x265 is not reproducible, so a full run rewrites all.
- **Speed (rung 5) is bit-identical and has a reference for every shortcut.** A grid's tiles decode in
  parallel (`HeifDecoder.Decode`'s `maxDegreeOfParallelism`; the lowest-numbered failing tile's error is the
  one thrown, however scheduled); the inverse transform runs in `Vector128<int>` lanes against
  `InverseScalar`; CABAC reads from a 64-bit cache and decodes n bypass bins as one long division against
  `BypassBinByBin`. The R7 went from 4.0 s to 0.27 s. Time the public decode in a lean process:
  `--heif-report` (benchmarks) holds about 1 GB of other arrays and reads the parallel decode at 0.6 s.

## Oracle harnesses (all codecs)

There are multiple oracle mechanisms, with different acquisition stories. All of them
**skip gracefully** when their dependency is missing, so a clean clone still builds and tests
green — which also means *a silently skipped oracle is not a passing oracle*. Check the skip
messages when a change should have been caught, and see `REQUIRE_ORACLES` below for how CI
refuses to accept that silence.

| Codec | Oracle | How to get it |
|---|---|---|
| JXR | `JxrEncApp.exe` / `JxrDecApp.exe` (jxrlib, BSD-2) | `bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh` — clones at a **pinned commit** + clang-builds into `Oracle/bin/` in ~10s. Git-ignored. Works on Windows *and* Linux (different flags — see the script), and **CI runs it**. |
| JPEG **encode** | `jpegenc.exe` (stb_image_write wrapper) | `bash tests/SharpAstro.Codecs.Tests/Oracle/jpegenc/build.sh` — downloads the header **pinned by commit SHA + SHA-256**, clang-builds into `Oracle/bin/`. Git-ignored; `jpegenc.c` + `build.sh` are the committed source of truth. **CI runs it.** |
| JPEG **decode** | committed golden digests | No external dependency — `Fixtures/jpeg-oracle-golden.tsv` (decode) and `jpeg-encoder-golden.tsv` (encode) run in CI unconditionally. Regenerate with `REGEN_JPEG_ORACLE=1`. |
| JXL, EXR | Magick.NET (libjxl / OpenEXR) | Just a NuGet reference — no build step. |
| Radiance **both ways** | Magick.NET (ImageMagick) | Just a NuGet reference. `RadianceOracleTests`: ImageMagick-written files decode to our floats, ours decode to ImageMagick's, bit-exact. Nothing to install, nothing to skip. |
| JBIG2 **MMR** | Magick.NET (libtiff, CCITT Group 4) | Just a NuGet reference. `Group4Tiff` has libtiff encode the raster as Group 4 and unwraps the codestream — T.6 is exactly what T.88 §6.2.6 carries. Nothing to install, nothing to skip. |
| TIFF **tiled write** | Magick.NET (libtiff) | Just a NuGet reference. `TiffWriterTiledOracleTests` has libtiff read back what `TiffWriter` emits in `TiffLayout.Tiled`. Nothing to install, nothing to skip. Kept as an oracle even though `TiffReader` reads tiles now: writer and reader agree about tile order by construction, so a round trip cannot see a consistent transpose. |
| TIFF **tiled read** | Magick.NET (libtiff) | The same reference, run the other way: `TiffTiledReadTests` has libtiff WRITE the tiled file (`tile-geometry` + `predictor` defines) and reads it back with `TiffReader`. **The predictor is why it must be a foreign encoder** -- `TiffWriter` emits none, so nothing we write exercises Predictor 2, and inverting it at image width rather than TILE width decodes with no error into a picture that is right in its first tile column and drifts across the rest. That sabotage fails exactly the two libtiff cases and nothing else in the suite. |
| GIF **decode** | Magick.NET (`Coalesce()`) | Just a NuGet reference. ImageMagick writes the animations (and `GifBuilder` hand-builds what it will not write); every frame must equal the coalesced frame byte for byte, alpha-0 RGB included. Nothing to install, nothing to skip. |
| JBIG2 **decode** | committed jbig2enc fixtures + spec vectors | No external dependency, nothing to skip — `Fixtures/jbig2/*.jb2` (real jbig2enc output) plus the T.88 Annex H.2 MQ vector and one-hot template tests. Regenerate the fixtures with `Oracle/jbig2/make-fixtures.sh` (needs jbig2enc). |
| JPEG 2000 **decode** | committed OpenJPEG fixtures | No external dependency, nothing to skip. A lossless 5/3 codestream reproduces its source raster **exactly**, so `Fixtures/jpeg2000/<name>.pgm` (or `.ppm`) is the expected output for `<name>.j2k` (or `.jp2`) — byte equality, no tolerance, no subprocess. Regenerate with `Oracle/jpeg2000/make-fixtures.sh`, which verifies each pair lossless before it will commit it. The lossy fixtures in `Fixtures/jpeg2000-lossy/` have no committed expected output and are compared against `opj_decompress` at run time: exactly for `lossy53-*`, within the measured tolerance for `lossy97-*`. |
| JPEG 2000 **conformance** | `opj_decompress` / `opj_compress` (OpenJPEG, BSD-2 — **binary only**) | `bash tests/SharpAstro.Codecs.Tests/Oracle/jpeg2000/fetch.sh` — **downloads** the pinned v2.5.4 release build (verified by SHA-256) into `dist/`, git-ignored. The only oracle here that is not compiled from source: OpenJPEG wants CMake, and upstream ships official builds for both platforms that matter, so this dev box and CI run the *same bytes* rather than merely the same source. **CI fetches it.** |
| JBIG2 **conformance** | `jbig2dec` (Artifex, AGPL — **binary only**) | `apt-get install jbig2dec`, or on Windows `wsl -- sudo apt-get install -y jbig2dec` — `Jbig2Oracle` finds it on PATH or through WSL. CI installs it with a one-line apt step. |
| WebP **decode** | `dwebp` / `anim_dump` (libwebp, BSD-3 — **binary only**) + committed digests | The digests in `Fixtures/webp/*-golden.tsv` run everywhere. The live comparisons need `apt-get install webp`, the libwebp release binaries on `PATH`, or `DWEBP` / `ANIM_DUMP`. **CI installs the apt package.** |
| HEVC **conformance** (HEIF) | JCT-VC H.265.1 conformance bitstreams (third-party data, not committed) vs live ffmpeg | `bash tests/SharpAstro.Codecs.Tests/fetch-hevc-conformance.sh` downloads 45 streams (each zip pinned by SHA-256) into the git-ignored `Fixtures/hevc-conformance/`. `HevcConformanceTests` skips without them, in CI too. |
| HEVC **decode** (HEIF) | `ffmpeg`'s native `hevc` decoder (LGPL/GPL — **binary only**) + committed x265 fixtures and digests | The digests in `Fixtures/hevc/hevc-oracle-golden.tsv` run everywhere. The live comparisons need ffmpeg: `apt-get install ffmpeg` (**CI installs it**), `Oracle/heif/fetch.sh` on Windows (pinned BtbN LGPL build, SHA-256 checked), or `FFMPEG`. The fixtures come from `Oracle/heif/make-fixtures.sh` (Linux/WSL, x265 through ffmpeg), which writes one only when ffmpeg and libde265 decode it to the same bytes. The Canon pictures are compared live and skip without the fetched files. |

Byte-exactness claims that depend on a *pinned* reference break if the pin moves; treat the
SHA-256 in `jpegenc/build.sh`, the `JXRLIB_COMMIT` in `Oracle/build.sh`, and the `OPJ_VERSION` +
archive SHA-256s in `jpeg2000/fetch.sh` as part of the contract. The JPEG 2000 pin is the one that
matters least on the reversible path (lossless output cannot drift) and most on the lossy one
(rung 2's expected pixels are whatever that build computes).

### `REQUIRE_ORACLES` — making a skipped oracle a red build

Graceful skipping keeps a clean clone green, and in CI it is a liability: a job that stops
installing its oracle looks exactly like a job that runs it. `REQUIRE_ORACLES=1` turns "oracle
unavailable" from a skip into a **failure**, and CI's test step sets it.

`OracleGate.RequireOrSkip(available, name, reason)` is the shared entry point. **Every external
oracle is now on it, and CI installs, builds or fetches all six** — so a CI run has no
silently-absent oracle left.

| Harness | Gated? | Installed/built in CI | Missing ⇒ |
|---|---|---|---|
| jbig2dec | yes | `apt-get install jbig2dec` | skip locally, **fail** in CI |
| JXR (jxrlib) | yes | `Oracle/build.sh`, ~20s | skip locally, **fail** in CI |
| jpegenc (stbiw) | yes | `Oracle/jpegenc/build.sh`, ~5s | skip locally, **fail** in CI |
| OpenJPEG | yes | `Oracle/jpeg2000/fetch.sh`, ~5s | skip locally, **fail** in CI |
| libwebp (dwebp, anim_dump) | yes | `apt-get install webp` | skip locally, **fail** in CI |
| ffmpeg (HEVC) | yes | `apt-get install ffmpeg` | skip locally, **fail** in CI |

**Why JXR mattered most.** Its guards used to be `if (encApp is null) { _out.WriteLine(...);
return; }` — which makes the test **pass**. On a dev box with `Oracle/bin/` populated that is
invisible; in CI it meant **447 test cases** (57 methods, expanded over their `InlineData`)
reported success while executing no assertions at all, and byte-exactness against `JxrEncApp` is
the strongest claim this repo makes about the JXR port. It was inert and indistinguishable from
green. Gating alone would have exposed it; building jxrlib in CI is what actually turned those
447 into real checks.

**Cross-platform check, done rather than assumed.** CI builds jxrlib and jpegenc on Linux while
the dev box builds them on Windows, so "the oracle" is now two binaries and they had better agree.
For the same inputs: jxrlib emits byte-identical `.jxr` at overlap 0/1/2 (and Linux `JxrDecApp`
reproduces the source pixels exactly), and jpegenc emits byte-identical JPEG at q1/25/75/90/100.
The jpegenc agreement is not luck — `-ffp-contract=off` in its build script is load-bearing, since
otherwise clang fuses the DCT multiply-adds into FMAs that round differently per target.

Local and CI skip counts should now match at **6** (the four `RegenerateGolden` opt-ins -- JPEG
decode/encode and WebP still/animation -- and two JXR header tests), given a dev box with every
oracle present. They previously differed — local 4, CI 56 — purely because a dev box has
`Oracle/bin/` populated and CI built nothing.

The Canon HEIF/CR3 tests (`HeifCanonFileTests`, `HevcDecoderOracleTests`) are a separate case: their
INPUT is fetched, not their oracle (`fetch-heif-external.sh`; third-party files with no licence), so
they `Assert.SkipUnless` the file and skip in CI under `REQUIRE_ORACLES` too. CI's skip count is the 6
above plus those.

Related knobs: `OPJ_HOME=<prefix>` points the JPEG 2000 oracle at a prefix containing
`bin/opj_decompress` — a local build, or a bogus path to exercise the failure branch.
`DWEBP=<path>` / `ANIM_DUMP=<path>` do the same for the WebP oracles, `FFMPEG=<path>` for the HEVC one.
`JBIG2DEC=<path>` overrides jbig2dec resolution — point it at a custom build, or at
a bogus path to exercise the failure branch. For the built oracles, temporarily renaming the
binary does the same job — but note `jpegenc.exe` resolves from **two** places (`Oracle/bin/` and
the copy in the test output directory), so hiding one is not enough to test the failure path.
