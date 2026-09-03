# JPEG 2000 oracle — OpenJPEG's `opj_compress` / `opj_decompress`

The reference implementation `SharpAstro.Jpeg2000` is checked against. Two
scripts here:

| Script | What it does | When you run it |
|---|---|---|
| `fetch.sh` | Downloads the pinned OpenJPEG release build into `dist/` (git-ignored) and proves it runs. | Once per clone; CI runs it every build. |
| `make-fixtures.sh` | Regenerates `Fixtures/jpeg2000/*.{pgm,j2k}` and verifies each pair is lossless. | Only when adding or changing fixtures. |

## Licence: binary yes, source no

OpenJPEG is **BSD-2**, which is permissive but *notice-retaining*, and this repo
is Unlicense (public domain). Notice-retaining code cannot be relicensed into
it, so `openjpeg`'s C is **not** a port source — the decoder is clean-room from
ITU-T T.800. Running `opj_decompress` is not linking to it and its output is
just pixels, so as an oracle binary it is fine. Exactly the line already drawn
around jbig2dec (AGPL), jbig2enc (Apache-2.0) and libjpeg's `jidctred.c`.

## Why this one downloads instead of building

`Oracle/build.sh` (jxrlib) and `Oracle/jpegenc/build.sh` (stb) both compile from
source with clang. This one does not, and the reason is not laziness: OpenJPEG
wants CMake, and upstream publishes official per-platform builds covering both
platforms that matter here (Windows x64 dev box, `ubuntu-latest` CI). A verified
download is less machinery *and* a stronger guarantee — dev box and CI run the
same **bytes**, where two local CMake builds would only share the same source.

The version and both archive SHA-256s are pinned in `fetch.sh`, for the reason
`JXRLIB_COMMIT` and `STB_SHA256` are pinned: an oracle upstream can move is an
oracle that can redefine "correct" underneath a green build.

`apt-get install libopenjp2-tools` was the roadmap's guess and was rejected on
this evidence: it is OpenJPEG **2.4.0** on jammy and 2.5.x on noble, so the dev
box and CI would silently disagree about what the reference is.

## Probe results — what the tools actually do

Hazard 7 in `ROADMAP-jpx.md` says to check the oracle's output conventions
rather than assume them, because the JBIG2 work lost time to ImageMagick's
photometric tag. Checked, on `v2.5.4`:

- **`opj_decompress` picks its writer from the output file extension.** `.pgm`
  for one component, `.ppm` for three; `PBM|PGM|PPM|PNM|PAM|PGX|PNG|BMP|TIF|RAW|YUV|RAWL|TGA`
  are all accepted.
- **It stamps a comment line into the PNM header** — `P5\n#OpenJPEG-2.5.4\n…`.
  So a decoded `.pgm` is *never* byte-identical to the source `.pgm` even when
  every pixel matches. Compare parsed payloads, not files. Both the fixture
  script and `OpenJpegOracle` skip `#` comments when tokenising, and the
  version in that comment is why a committed *expected* PNM would have been a
  bad idea.
- **Above 8 bits it writes 16-bit big-endian samples** with `maxval 65535`,
  which is the PNM spec's own byte order. Round-trips exactly.
- **Reversible (5/3) really is exact**, verified per fixture rather than
  assumed: source payload and decoded payload are equal byte-for-byte at 8 and
  16 bits, greyscale and RGB.
- **`opj_compress` turns RCT on by itself** for three components (`COD` SGcod
  MCT byte = `01`) when the transform is reversible. Rung 2's problem, noted
  here so it is not a surprise.
- **`-h` exits non-zero.** It is the usage path. Any availability probe that
  pipes it under `set -o pipefail`, or that gates on exit code alone, gets the
  wrong answer.

## The fixtures need no oracle at test time

This is the payoff, and it is a better position than JBIG2 got. A reversible
codestream decodes to its source raster **exactly**, so the committed `.pgm`
*is* the expected output: the test asserts byte equality, with no tolerance and
no subprocess. `make-fixtures.sh` verifies that claim with `opj_decompress`
before it will commit a pair, so a fixture that is somehow not lossless is
refused rather than baked in as a wrong answer.

The live oracle stays for what committed bytes cannot cover — chiefly the lossy
9/7 path, whose expected output is not the input and is only ever "whatever
OpenJPEG computes", and which therefore needs a tolerance sourced from T.803
rather than invented. Keep those two assertions apart: **an exact-match test on
the reversible path is the sharpest tool this format offers, and a global
tolerance throws it away.**

## The fixture matrix

Everything in `Fixtures/jpeg2000/` stays inside rung 1's envelope — one 8-bit
unsigned component, one tile, one quality layer, maximal precincts, LRCP,
reversible 5/3, raw J2K — and varies one thing rung 1 must still get right.

| Fixture | Varies | Why it earns its place |
|---|---|---|
| `nodwt-struct32`, `nodwt-noise32` | `-n 1` (zero decomposition levels) | No DWT at all, so a failure is tier-1 or tier-2 and *cannot* be the wavelet. Debug against these first. |
| `dwt1-struct32`, `dwt5-struct64` | `-n 2`, default `-n 6` | Bisects DWT depth once the no-DWT case passes. |
| `flat64` | constant image | No code-block is ever included in a packet. A decoder written only against busy images gets this wrong. |
| `ramp64` | smooth gradient | Where a dropped sign-XOR bit (hazard 4) is visible; on detail it is not. |
| `noise64` | dense high frequency | Every coding pass on every bit-plane. |
| `cblk16-noise64`, `cblk4-struct64` | `-b 16,16`, `-b 4,4` | A *grid* of code-blocks per subband, which is what makes tag trees and inclusion signalling do real work. At the default 64x64 there is one per subband and the tag tree is trivial. |
| `odd37x23`, `odd5x64`, `odd64x5` | non-aligned dimensions | Partial code-blocks, odd-length lifting, and subband sizes that are not a plain halving. |
| `odd1x1` | 1x1 | The degenerate limit. |

Sources are regenerated by `gen-sources.py` from closed-form functions of
`(x, y)` and one fixed LCG — no library PRNG, no floating point — so a
regeneration on another machine or Python version reproduces the same bytes.
