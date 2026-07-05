# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

`src/SharpAstro.*/` — hand-written, clean-room / faithfully-ported image codec packages, each an
independent NuGet shipped in lockstep (shared Major.Minor + CI run-number patch):

- **`SharpAstro.Codecs`** — the facade: magic-byte sniff + dispatch over `IImageDecoder`.
  Consumers reference this one package instead of cherry-picking individual codecs.
- **`SharpAstro.Codecs.Abstractions`** — the base: `IImageDecoder` (static-abstract sniff +
  fidelity/zero-copy decode) plus `IDecodedImage` / `RasterImage`.
- **codecs** — `Tiff`, `Png`, `Jpeg`, `Jxr`, `Exr`, `Jxl`, `Exif`, `Color.Icc`, `Jpeg.IccInjector`.

`SharpAstro.Jpeg`'s full-scale decode was built as a faithful port of the stb_image (StbImageSharp)
JPEG path (IDCT constants, upsampling kernels, fixed-point colour convert) and validated
**byte-exact against that reference decoder**. The stb port has since been removed from the repo;
the guarantee is preserved as a committed golden digest baseline
(`tests/SharpAstro.Codecs.Tests/Fixtures/jpeg-oracle-golden.tsv`, driven by `JpegDecoderOracleTests`
— regenerate with `REGEN_JPEG_ORACLE=1`). The scaled-decode (1/2–1/8) reduced IDCT is clean-room
DCT-domain decimation — deliberately NOT ported from libjpeg's `jidctred.c`, which is IJG-licensed
(this repo is Unlicense).

`CODECS.md` documents the per-package decode/encode matrix (its `SharpAstro.Jxr` row reflects
the jxrlib re-port). See "JXR codec" below for the architecture and validation discipline.

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
PR #1 (merge commit `5b2f3f2`) and shipped to NuGet at **3.0.211**. It supports BD8/BD16/BD16F/
BD32F × grayscale (Y-only) + RGB, single-tile SPATIAL mode, POT (OL_NONE/ONE/TWO), lossy QP, and
arbitrary (non-16-aligned) dimensions (pad-then-crop, not WINDOWING_FLAG). Frequency mode,
multi-tile, and alpha plane remain out of scope.

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
