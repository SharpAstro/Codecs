# JXR oracle — `JxrDecApp` from jxrlib

This directory hosts a tiny build wrapper around Microsoft's BSD-2 reference
JPEG XR codec ([jxrlib](https://github.com/4creators/jxrlib)). Tests in
`SharpAstro.Codecs.Tests` that cross-check our decoder against the reference
implementation shell out to `JxrDecApp.exe` (and `JxrEncApp.exe`).

Both binaries are git-ignored; build them once and they get cached in
`bin/` (this directory) plus copied to the test project's output via
`<None CopyToOutputDirectory>` glob.

## Build (Windows, one-time)

```bash
bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh
```

The script:
1. Clones `4creators/jxrlib` into `tests/SharpAstro.Codecs.Tests/Oracle/jxrlib-src/`
   (also git-ignored).
2. Patches `image/sys/ansi.h` so the static-asserted `UINTPTR_T` is the
   right size on win-x64 / win-arm64 (the upstream `#if __LP64__` only
   covers UNIX 64-bit).
3. Compiles `JxrDecApp.exe` + `JxrEncApp.exe` with `clang --target=...`
   into `bin/`. No CMake, no MSVC toolset install required — uses the
   clang that ships with the Swift for Windows toolchain (or any clang
   on PATH).

If `bin/JxrDecApp.exe` is absent at test run time, the oracle tests
skip with a clear message rather than failing.

## What gets tested

- `JxrOracleTests.SeagullDecodesViaJxrDecApp` — invokes `JxrDecApp -i
  seagull_nebula.jxr -o seagull.tif`, opens the TIFF via
  `SharpAstro.Tiff.TiffReader`, asserts shape (2963×2991×4) and a few
  sample-value invariants. Smoke test that the reference decoder runs
  cleanly on the bundled fixture — a sanity check on the oracle itself
  more than on us.

- (Future) pixel-level comparison against our `JxrFileFormatter.LoadBd*`
  output once a Bgra32 decode path is available.
