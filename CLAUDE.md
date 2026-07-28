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
is registered only as a courtesy for standalone `.jb2` files. **Rung 1 of 5 has shipped** — the MQ
arithmetic decoder, generic regions (GBTEMPLATE 0–3, TPGDON, arbitrary AT pixels), page info, and
composition; MMR, symbol dictionary + text region, refinement, and halftone all throw
`NotSupportedException` naming the missing feature. See "JBIG2 codec" below.

`CODECS.md` documents the per-package decode/encode matrix (its `SharpAstro.Jxr` row reflects
the jxrlib re-port). See "JXR codec" below for the architecture and validation discipline.
Longer-horizon work lives in the root roadmap docs: [`ROADMAP-jpeg-encoder.md`](ROADMAP-jpeg-encoder.md),
[`ROADMAP-gain-map.md`](ROADMAP-gain-map.md), [`ROADMAP-pdf-codecs.md`](ROADMAP-pdf-codecs.md)
(JBIG2 rungs 2–5 / JPX), plus [`JXR-FORMAT.md`](JXR-FORMAT.md) for the per-axis JXR support breakdown.

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

`SharpAstro.Jbig2` is **clean-room from ITU-T T.88**, and that is not a stylistic preference —
it is forced. This repo is **Unlicense (public domain)**, which is stricter than "permissive":
notice-retaining code cannot be relicensed into it. `jbig2dec` is **AGPL** (unusable as a port
source, full stop); pdf.js, PDFium, and `jbig2enc` are Apache-2.0 / BSD-3, which still means
notice retention. All four are fine as oracle *binaries* — running a program is not linking to
it — but none may be read-and-transcribed. Same line already drawn for libjpeg's `jidctred.c`.

Structure (`src/SharpAstro.Jbig2/`):

```
Jbig2Decoder        (public: Decode(embedded, globals, w, h) / DecodeFile / TryReadFileInfo)
  → Jbig2Segment    (T.88 §7.2 segment headers + §7.4.1 region info)
  → GenericRegionDecoder  (§6.2: GBTEMPLATE 0-3 context templates, TPGDON, AT pixels)
  → MqDecoder       (Annex E arithmetic decoder; a ref struct over the coded span)
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

When adding a rung (MMR, symbol dictionary + text region, refinement, halftone), the missing-feature
`NotSupportedException` it replaces is the thing to grep for. Keep the existing refusals loud —
a stream this decoder cannot fully reconstruct must fail rather than return a plausible page.

## Oracle harnesses (all codecs)

There are now **five** oracle mechanisms, with different acquisition stories. All of them
**skip gracefully** when their dependency is missing, so a clean clone still builds and tests
green — which also means *a silently skipped oracle is not a passing oracle*. Check the skip
messages when a change should have been caught.

| Codec | Oracle | How to get it |
|---|---|---|
| JXR | `JxrEncApp.exe` / `JxrDecApp.exe` (jxrlib, BSD-2) | `bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh` — clones + clang-builds into `Oracle/bin/`. Git-ignored. |
| JPEG **encode** | `jpegenc.exe` (stb_image_write wrapper) | `bash tests/SharpAstro.Codecs.Tests/Oracle/jpegenc/build.sh` — downloads the header **pinned by commit SHA + SHA-256**, clang-builds into `Oracle/bin/`. Git-ignored; `jpegenc.c` + `build.sh` are the committed source of truth. |
| JPEG **decode** | committed golden digests | No external dependency — `Fixtures/jpeg-oracle-golden.tsv` (decode) and `jpeg-encoder-golden.tsv` (encode) run in CI unconditionally. Regenerate with `REGEN_JPEG_ORACLE=1`. |
| JXL, EXR | Magick.NET (libjxl / OpenEXR) | Just a NuGet reference — no build step. |
| JBIG2 **decode** | committed jbig2enc fixtures + spec vectors | No external dependency, nothing to skip — `Fixtures/jbig2/*.jb2` (real jbig2enc output) plus the T.88 Annex H.2 MQ vector and one-hot template tests. Regenerate the fixtures with `Oracle/jbig2/make-fixtures.sh` (needs jbig2enc). |
| JBIG2 **conformance** | `jbig2dec` (Artifex, AGPL — **binary only**) | `apt-get install jbig2dec`, or on Windows `wsl -- sudo apt-get install -y jbig2dec` — `Jbig2Oracle` finds it on PATH or through WSL. Unlike the JXR/jpegenc oracles this one **reports its skip** (`Assert.SkipUnless`) instead of passing silently, and it works in CI with a one-line apt step. |

Byte-exactness claims that depend on a *pinned* reference (the stb JPEG writer) break if the pin
moves; treat the SHA in `jpegenc/build.sh` as part of the contract.

### `REQUIRE_ORACLES` — making a skipped oracle a red build

Graceful skipping keeps a clean clone green, and in CI it is a liability: a job that stops
installing its oracle looks exactly like a job that runs it. `REQUIRE_ORACLES=1` turns "oracle
unavailable" from a skip into a **failure**, and CI's test step sets it.

`OracleGate.RequireOrSkip(available, name, reason)` is the shared entry point. **JBIG2 is the
first harness wired to it, and the only oracle CI installs** (`apt-get install -y jbig2dec`) —
the JXR and jpegenc harnesses are local clang builds under `Oracle/bin/` that no workflow step
produces, so they have only ever run on a dev box. Porting them is the obvious follow-on now
that the pattern exists.

Those two are **not** in the same state, and the difference matters when reading a CI log:

- **jpegenc** already reports xunit skips (`Assert.Skip`), just without the gate — so its 52
  `JpegEncoderOracleTests` cases show up honestly as skips in every CI run. Wiring it to
  `OracleGate` is a one-line change; getting it to actually *run* means building `jpegenc.exe`
  in the workflow.
- **JXR** still uses the older idiom — `if (encApp is null) { _out.WriteLine(...); return; }`,
  which **passes**. ~38 oracle test methods across `Jxr*OracleTests` therefore contribute zero
  skips to a CI run while validating nothing. The strongest layer this repo has (byte-exact vs
  `JxrEncApp`) is, in CI, entirely inert and indistinguishable from success. Converting those
  guards to `OracleGate.RequireOrSkip` is worth doing *before* the workflow learns to build the
  binaries, because it costs nothing and makes the gap visible in the meantime.

A corollary for anyone predicting a skip count: a local run and a CI run legitimately differ,
because a dev box has `Oracle/bin/` populated. Local is currently 4 skips, CI 56.

Related knob: `JBIG2DEC=<path>` overrides oracle resolution — point it at a custom build, or at
a bogus path to exercise the failure branch.

Local behaviour is unchanged: without `REQUIRE_ORACLES`, a missing jbig2dec yields reported
xunit *skips* (`Assert.Skip`), which at least show in the run summary rather than passing mutely.
