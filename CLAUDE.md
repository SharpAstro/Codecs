# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

`src/SharpAstro.*/` — hand-written, clean-room / faithfully-ported image codec packages, each an
independent NuGet shipped in lockstep (shared Major.Minor + CI run-number patch):

- **`SharpAstro.Codecs`** — the facade: magic-byte sniff + dispatch over `IImageDecoder`.
  Consumers reference this one package instead of cherry-picking individual codecs.
- **`SharpAstro.Codecs.Abstractions`** — the base: `IImageDecoder` (static-abstract sniff +
  fidelity/zero-copy decode) plus `IDecodedImage` / `RasterImage`.
- **codecs** — `Tiff`, `Png`, `Jpeg`, `Jxr`, `Exr`, `Jxl`, `Jbig2`, `Exif`, `Color.Icc`,
  `Jpeg.IccInjector`, `Jpeg.GainMap` (Ultra HDR read/write; facade-registered *ahead of* the
  plain JPEG decoder).

`SharpAstro.Jpeg`'s full-scale decode was built as a faithful port of the stb_image (StbImageSharp)
JPEG path (IDCT constants, upsampling kernels, fixed-point colour convert) and validated
**byte-exact against that reference decoder**. The stb port has since been removed from the repo;
the guarantee is preserved as a committed golden digest baseline
(`tests/SharpAstro.Codecs.Tests/Fixtures/jpeg-oracle-golden.tsv`, driven by `JpegDecoderOracleTests`
— regenerate with `REGEN_JPEG_ORACLE=1`). The scaled-decode (1/2–1/8) reduced IDCT is clean-room
DCT-domain decimation — deliberately NOT ported from libjpeg's `jidctred.c`, which is IJG-licensed
(this repo is Unlicense).

`SharpAstro.Jpeg` also ships a **second, structurally unrelated decoder**: `LosslessJpeg`
(ITU-T T.81 Annex H, SOF3) — Huffman-coded sample-difference predictors 1–7, no quantisation,
**up to 16-bit precision**, DRI/RSTn restarts, point transform. It shares no code with
`JpegDecoder` (which handles SOF0/1/2 only). Its real consumer is outside this repo: the
**FC.SDK.Raw** repo decodes Canon CR2 IFD3 raw strips (14-bit Bayer as a 2-component interleaved
lossless sub-frame) through it. Keep it working — `LosslessJpegTests` uses hand-crafted minimal
bitstreams, because real CR2 payloads are too large to commit.

`SharpAstro.Jbig2` (3.7) is the odd one out in the family: its primary entry point is **not** the
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
Longer-horizon work lives in the root roadmap docs: [`ROADMAP-jpeg-encoder.md`](ROADMAP-jpeg-encoder.md),
[`ROADMAP-gain-map.md`](ROADMAP-gain-map.md), [`ROADMAP-pdf-codecs.md`](ROADMAP-pdf-codecs.md)
(JPX / JBIG2's remaining Huffman variants), plus [`JXR-FORMAT.md`](JXR-FORMAT.md) for the per-axis JXR support breakdown.

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

## Oracle harnesses (all codecs)

There are now **six** oracle mechanisms, with different acquisition stories. All of them
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
| JBIG2 **MMR** | Magick.NET (libtiff, CCITT Group 4) | Just a NuGet reference. `Group4Tiff` has libtiff encode the raster as Group 4 and unwraps the codestream — T.6 is exactly what T.88 §6.2.6 carries. Nothing to install, nothing to skip. |
| JBIG2 **decode** | committed jbig2enc fixtures + spec vectors | No external dependency, nothing to skip — `Fixtures/jbig2/*.jb2` (real jbig2enc output) plus the T.88 Annex H.2 MQ vector and one-hot template tests. Regenerate the fixtures with `Oracle/jbig2/make-fixtures.sh` (needs jbig2enc). |
| JBIG2 **conformance** | `jbig2dec` (Artifex, AGPL — **binary only**) | `apt-get install jbig2dec`, or on Windows `wsl -- sudo apt-get install -y jbig2dec` — `Jbig2Oracle` finds it on PATH or through WSL. CI installs it with a one-line apt step. |

Byte-exactness claims that depend on a *pinned* reference break if the pin moves; treat the
SHA-256 in `jpegenc/build.sh` and the `JXRLIB_COMMIT` in `Oracle/build.sh` as part of the
contract.

### `REQUIRE_ORACLES` — making a skipped oracle a red build

Graceful skipping keeps a clean clone green, and in CI it is a liability: a job that stops
installing its oracle looks exactly like a job that runs it. `REQUIRE_ORACLES=1` turns "oracle
unavailable" from a skip into a **failure**, and CI's test step sets it.

`OracleGate.RequireOrSkip(available, name, reason)` is the shared entry point. **Every external
oracle is now on it, and CI installs or builds all three** — so a CI run has no silently-absent
oracle left.

| Harness | Gated? | Installed/built in CI | Missing ⇒ |
|---|---|---|---|
| jbig2dec | yes | `apt-get install jbig2dec` | skip locally, **fail** in CI |
| JXR (jxrlib) | yes | `Oracle/build.sh`, ~20s | skip locally, **fail** in CI |
| jpegenc (stbiw) | yes | `Oracle/jpegenc/build.sh`, ~5s | skip locally, **fail** in CI |

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

Local and CI skip counts should now match at **4** (the `RegenerateGolden` opt-ins and two JXR
header tests). They previously differed — local 4, CI 56 — purely because a dev box has
`Oracle/bin/` populated and CI built nothing.

Related knob: `JBIG2DEC=<path>` overrides jbig2dec resolution — point it at a custom build, or at
a bogus path to exercise the failure branch. For the built oracles, temporarily renaming the
binary does the same job — but note `jpegenc.exe` resolves from **two** places (`Oracle/bin/` and
the copy in the test output directory), so hiding one is not enough to test the failure path.
