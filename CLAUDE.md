# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Two distinct bodies of code share one repo and one CI/versioning pipeline:

1. **`src/StbImageSharp/`** — a pure-managed **decode-only** port of Sean Barrett's
   `stb_image.h` (PNG/JPG/BMP/TGA/PSD/GIF/HDR). The `StbImage.Generated.*.cs` files are
   **machine-transpiled** (via Hebron, see `generation/`) and unchanged from upstream — do
   not hand-edit them. 8-bit precision through the managed API; strips all metadata.
2. **`src/SharpAstro.*/`** — hand-written, clean-room / faithfully-ported codec packages
   (`Tiff`, `Png`, `Jxr`, `Exif`, `Color.Icc`, `Jpeg.IccInjector`). Each ships as an
   independent NuGet. This is where almost all active development happens.

`CODECS.md` documents the per-package decode/encode matrix. **Caveat: its `SharpAstro.Jxr`
row is stale** — it describes the original spec-derived codec that was iceboxed and deleted
(commit `48b0432`). See "JXR codec" below for the real current state.

## Build & test

Requires the **.NET 10 SDK**. There is no separate lint step (CI is build + test + pack).

```bash
# Canonical solution for development — CI builds/tests/packs this one.
dotnet build StbImageSharp.JustTests.sln -c Release
dotnet test  StbImageSharp.JustTests.sln -c Release
```

**Solution gotcha:** the xunit codec test project `SharpAstro.Codecs.Tests` is **only in
`StbImageSharp.JustTests.sln`**, not in `StbImageSharp.sln` (which instead carries the
MonoGame `Viewer` / `Stb.Native` / `Testing` projects). When working on the SharpAstro
codecs, use `JustTests.sln` or the individual project — the main `.sln` won't see those tests.

```bash
# Iterate on one project (fast):
dotnet test tests/SharpAstro.Codecs.Tests/SharpAstro.Codecs.Tests.csproj

# Run a subset by name (xunit FullyQualifiedName filter):
dotnet test tests/SharpAstro.Codecs.Tests/SharpAstro.Codecs.Tests.csproj --filter "FullyQualifiedName~Jxr"
dotnet test tests/SharpAstro.Codecs.Tests/SharpAstro.Codecs.Tests.csproj --filter "FullyQualifiedName~JxrGrayscaleOracle"
```

**Two test frameworks** (don't mix conventions): `StbImageSharp.Tests` uses **NUnit**
(covers the stb port); `SharpAstro.Codecs.Tests` uses **xunit v3 + Shouldly** (+ Magick.NET
for visual diffing) and covers the SharpAstro codec family.

Package versions are **centrally managed** in `Directory.Packages.props` — add a
`<PackageVersion>` there and reference it without a version in the `.csproj`. All packages
ship in lockstep (shared Major.Minor + CI run-number patch). Project conventions: `net10.0`,
`IsAotCompatible=true`, SourceLink/embedded debug. Note `StbImageSharp` is `Nullable=disable`
(transpiled), while the `SharpAstro.*` codecs are `Nullable=enable` + `ImplicitUsings=enable`.

## JXR codec — active re-port (the most involved subsystem)

`SharpAstro.Jxr` is being rewritten as a **faithful, table-exact C# re-port of Microsoft's
jxrlib C** (the earlier spec-derived codec produced "garbage after the first block" and was
retired). Development happens on the **`jxr-reimpl`** branch; **`master` is kept clean** until
the re-port is proven. It is built up incrementally and validated bit-exact at each step.

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
