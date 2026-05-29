# Icebox

Parked code that is **not part of the build** (removed from `StbImageSharp.sln`,
`StbImageSharp.JustTests.sln`, and the test project's `ProjectReference`s). It
compiles against nothing and runs nowhere — it's here as a reference, not a
dependency.

## `SharpAstro.Jxr/` — spec-derived JPEG XR codec (superseded)

The original `SharpAstro.Jxr` was a clean-room implementation read directly from
the T.832 spec PDF. It got far: the TIFF-style container and all codestream
headers are correct (files open in Windows Photo when encoded with
`useYUV444: true`), and DC-only / solid-colour content round-trips. But the
coefficient-level pipeline drifts from the reference — encoded images turn to
garbage after the first block, and the decoder produces near-zero pixels on
spec-encoded (WIC/jxrlib) files. Both directions diverge once content leaves the
DC band.

It is being replaced by a fresh `src/SharpAstro.Jxr` that ports Microsoft's
jxrlib C reference codec faithfully (idiomatic C#, but algorithm- and
table-faithful), validated bit-exactly against the `JxrEncApp`/`JxrDecApp`
oracle binaries.

### What's worth salvaging (carried into the new project)

- **Container / file format** (correct, reused): `JxrContainer`, `JxrTag`,
  `JxrPixelFormat`, `JxrTileLayout`, `ImageHeader`, `ImagePlaneHeader`,
  `ProfileLevelInfo`, `IndexTableTiles`, `TileBandHeaders`, `TileHeader*`,
  `QpTable`, `QpIndex`, the enums, and `CodedImage`'s header-side parsing.
- **Bit I/O** (correct, reused): `BitReader`, `BitWriter`.

### What's being re-ported from jxrlib C (the suspect core)

Transforms (FCT4x4/ICT4x4), POT overlap filters, DC/LP/HP/CBP prediction,
quantization, adaptive scan, adaptive Huffman/VLC, run/level coding, the `Mb*`
band coders, `Tile{Spatial,Frequency}`, and `YCoCgTransform`.

## `SharpAstro.Jxr.LegacyTests/` — the old unit + oracle tests

The 44 test files that exercised the superseded codec's internals and public
surface. Parked here (not compiled). The oracle-cross-check tests
(`JxrEncoderOracleProbe`, `JxrOracleTests`, `JxrWicOracleTests`,
`JxrWicInteropTests`) target the public API the new codec will re-provide, so
they'll be resurrected into the active test project in Phase 1.

The reusable oracle *infrastructure* — `Oracle/` (jxrlib source, `build.sh`,
built `JxrEncApp`/`JxrDecApp` binaries), `Fixtures/`, and `WicOracle.cs` — stays
in the active test project.
