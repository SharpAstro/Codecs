# JXR oracle — `JxrDecApp` from jxrlib

This directory hosts a tiny build wrapper around Microsoft's BSD-2 reference
JPEG XR codec ([jxrlib](https://github.com/4creators/jxrlib)). Tests in
`SharpAstro.Codecs.Tests` that cross-check our decoder against the reference
implementation shell out to `JxrDecApp.exe` (and `JxrEncApp.exe`).

Both binaries are git-ignored; build them once and they get cached in
`bin/` (this directory) plus copied to the test project's output via
`<None CopyToOutputDirectory>` glob.

## Build (Windows or Linux, one-time)

```bash
bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh
```

The script:
1. Clones `4creators/jxrlib` into `tests/SharpAstro.Codecs.Tests/Oracle/jxrlib-src/`
   (also git-ignored) at a **pinned commit**. The oracle's whole value is that
   our codestream matches *this* reference byte-for-byte, so upstream must not
   be free to redefine "correct" underneath us — same reasoning as the SHA-256
   pin in `jpegenc/build.sh`.
2. Patches `image/sys/ansi.h` so the static-asserted `UINTPTR_T` is the
   right size on win-x64 / win-arm64 (the upstream `#if __LP64__` only
   covers UNIX 64-bit). Applied on Linux too, where it is a no-op, so both
   platforms compile identical sources.
3. Compiles `JxrDecApp.exe` + `JxrEncApp.exe` with `clang` into `bin/`. No CMake,
   no MSVC toolset install required — uses the clang that ships with the Swift
   for Windows toolchain, or any clang on PATH.

### Windows vs Linux

CI (`ubuntu-latest`) runs this script, so the Linux path is not a courtesy — it
is what makes the JXR oracle a real gate instead of a dev-box nicety. The flags
differ, and the difference is not cosmetic:

- **Windows** wants `-DWIN32` and a force-included `wmsal.h`.
- **Linux** wants neither. Force-including `wmsal.h` drags in `guiddef.h` before
  `JXRGlue.c` reaches its `#define INITGUID`, which quietly degrades every
  `DEFINE_GUID` to a declaration and blows up at link with a wall of undefined
  `GUID_PKPixelFormat*` symbols. Upstream's own Makefile flags work; two
  declarations must be supplied on top (`wcslen`, and `_byteswap_ulong` — an
  MSVC name that jxrlib nonetheless *defines itself* in `strcodec.c` for
  non-Windows targets, so it is declared rather than mapped to a builtin, and
  the oracle keeps running jxrlib's own code).

The two builds were checked against each other rather than assumed equivalent:
for the same BMP they emit **byte-identical** `.jxr` at overlap 0/1/2, and Linux
`JxrDecApp` reproduces the source pixels exactly.

### When the binaries are missing

Oracle tests **skip** with a reported reason (they used to `return` early, which
counted as a pass — see `JxrOracle`). Under `REQUIRE_ORACLES=1`, which CI sets,
a missing binary is a **failure** instead.

## What gets tested

- `JxrOracleTests.SeagullDecodesViaJxrDecApp` — invokes `JxrDecApp -i
  seagull_nebula.jxr -o seagull.tif`, opens the TIFF via
  `SharpAstro.Tiff.TiffReader`, asserts shape (2963×2991×4) and a few
  sample-value invariants. Smoke test that the reference decoder runs
  cleanly on the bundled fixture — a sanity check on the oracle itself
  more than on us.

- (Future) pixel-level comparison against our `JxrFileFormatter.LoadBd*`
  output once a Bgra32 decode path is available.
