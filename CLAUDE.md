# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

`src/SharpAstro.*/` — hand-written, clean-room / faithfully-ported image codec packages, each an
independent NuGet shipped in lockstep (shared Major.Minor + CI run-number patch):

- **`SharpAstro.Codecs`** — the facade: magic-byte sniff + dispatch over `IImageDecoder`.
  Consumers reference this one package instead of cherry-picking individual codecs.
- **`SharpAstro.Codecs.Abstractions`** — the base: `IImageDecoder` (static-abstract sniff +
  fidelity/zero-copy decode) plus `IDecodedImage` / `RasterImage`.
- **codecs** — `Tiff`, `Png`, `Jpeg`, `Jxr`, `Exr`, `Jxl`, `Exif`, `Color.Icc`, `Jpeg.IccInjector`,
  `Jpeg.GainMap` (Ultra HDR read/write; facade-registered *ahead of* the plain JPEG decoder).

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

`CODECS.md` documents the per-package decode/encode matrix (its `SharpAstro.Jxr` row reflects
the jxrlib re-port). See "JXR codec" below for the architecture and validation discipline.
Longer-horizon work lives in the root roadmap docs: [`ROADMAP-jpeg-encoder.md`](ROADMAP-jpeg-encoder.md),
[`ROADMAP-gain-map.md`](ROADMAP-gain-map.md), [`ROADMAP-pdf-codecs.md`](ROADMAP-pdf-codecs.md)
(JBIG2 / JPX), plus [`JXR-FORMAT.md`](JXR-FORMAT.md) for the per-axis JXR support breakdown.

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

## Oracle harnesses (all codecs)

There are now **four** oracle mechanisms, with different acquisition stories. All of them
**skip gracefully** when their dependency is missing, so a clean clone still builds and tests
green — which also means *a silently skipped oracle is not a passing oracle*. Check the skip
messages when a change should have been caught.

| Codec | Oracle | How to get it |
|---|---|---|
| JXR | `JxrEncApp.exe` / `JxrDecApp.exe` (jxrlib, BSD-2) | `bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh` — clones + clang-builds into `Oracle/bin/`. Git-ignored. |
| JPEG **encode** | `jpegenc.exe` (stb_image_write wrapper) | `bash tests/SharpAstro.Codecs.Tests/Oracle/jpegenc/build.sh` — downloads the header **pinned by commit SHA + SHA-256**, clang-builds into `Oracle/bin/`. Git-ignored; `jpegenc.c` + `build.sh` are the committed source of truth. |
| JPEG **decode** | committed golden digests | No external dependency — `Fixtures/jpeg-oracle-golden.tsv` (decode) and `jpeg-encoder-golden.tsv` (encode) run in CI unconditionally. Regenerate with `REGEN_JPEG_ORACLE=1`. |
| JXL, EXR | Magick.NET (libjxl / OpenEXR) | Just a NuGet reference — no build step. |

Byte-exactness claims that depend on a *pinned* reference (the stb JPEG writer) break if the pin
moves; treat the SHA in `jpegenc/build.sh` as part of the contract.
