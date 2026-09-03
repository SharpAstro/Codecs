# Changelog

Release notes for the whole family: every package in this repo (the `SharpAstro.Codecs` facade plus
`SharpAstro.Tiff` / `.Png` / `.Exif` / `.Color.Icc` / `.Jpeg` / `.Jpeg.GainMap` / `.Jxr` / `.Exr` /
`.Jxl` / `.Jbig2` / `.Jpeg2000`) ships at the same `Major.Minor` with a shared run-number patch, so one section
covers them all.

The number itself lives in exactly one place, `<VersionMajorMinor>` in `Directory.Build.props`; CI
reads it back and appends the run number. Bumping a release is editing that one line and adding a
section here. Newest first.

This file was created at 3.10 by migrating the note trail out of the two comment blocks that carried
it -- the `env:` block in `.github/workflows/dotnet.yml` (35 lines of prose above a variable that no
longer holds the number) and the header comment in `Directory.Build.props`. Entries below 3.10 are
those notes, verbatim in substance. 3.4 has no recorded note; it was never written down.

## 3.12

**New package: `SharpAstro.Jpeg2000`** — a pure-managed, clean-room JPEG 2000 (ITU-T T.800 /
ISO/IEC 15444-1, **Part 1**) decoder, aimed at PDF's `/JPXDecode` filter. This is rung 1 of the five
in [`ROADMAP-jpx.md`](ROADMAP-jpx.md), and rung 1 here is unusually large on purpose: JPEG 2000 has
no early pipeline stage that decodes anything on its own, so the rungs stage along *feature* axes
rather than pipeline stages. Rung 1 is the whole pipeline — Annex A markers, tier-2 packet headers
with tag trees, tier-1 EBCOT over the Annex D context tables, the reversible 5/3 inverse DWT, and
the DC level shift — for the simplest legal configuration.

**Envelope:** one 8-to-16-bit unsigned component, one tile, one tile-part, one quality layer,
maximal precincts, LRCP, reversible 5/3, raw J2K. Everything outside it throws
`NotSupportedException` naming the feature **and the rung that owns it** — multiple components,
tiles or layers, the 9/7 irreversible filter and quantisation, COC/QCC/POC/PPM/PPT/SOP/EPH, custom
precincts, code-block style flags, RGN/MAXSHIFT, subsampling, signed components. Never a
plausible-looking wrong raster.

**Not registered with the `SharpAstro.Codecs` facade**, deliberately. Unlike JBIG2 it would fit
perfectly — JP2 and raw J2K both have magic bytes — but the facade would then advertise JPEG 2000
support for a format only this narrow slice of which decodes. Registration lands with colour at
rung 2. Use `Jpeg2000Decoder.Decode` directly until then.

**The reversible path is exact, and is asserted exact.** A lossless 5/3 codestream reconstructs its
encoder's input byte for byte, so each committed `Fixtures/jpeg2000/<name>.pgm` *is* the expected
output for the `<name>.j2k` beside it: 13 fixtures, byte equality, no tolerance and no oracle
process at test time. `Oracle/jpeg2000/make-fixtures.sh` verifies that claim with `opj_decompress`
per fixture before it will commit a pair. When rung 2's 9/7 path arrives it needs a tolerance from
T.803 — that tolerance must not be allowed to leak back over these cases, because an exact-match
test on the reversible path is the sharpest tool this format offers.

The suite was then **mutation-checked**, and all seven deliberate bugs were caught: dropped sign XOR
bit, raster scan instead of 4-row stripes, T.88's context initialisation instead of T.800's, HL
reading the LL/LH table unswapped, no `0xFF` bit-stuffing, truncation instead of floor in the
lifting steps, HOR/VER filter order swapped. An eighth was a control and behaved as predicted:
rebuilding the tag trees per packet is **not** caught, because tag-tree state spans quality layers
and this rung has one. Recorded rather than papered over — rung 3 needs a multi-layer fixture for it.

**Hardened at rung 1 rather than in a follow-up release**, which is the mistake `SharpAstro.Jbig2`
had to correct in 3.8. Declared geometry is charged against a 2^28-sample-per-tile-component
ceiling, a 2^22 code-block ceiling and a total budget scaled to the declared image, all *before*
anything is allocated — because T.800's MQ decoder, exactly like T.88's, reads every byte past the
end of its data as `0xFF` for ever, so running out of input is not a backstop. The limit tests found
a real overflow while being written: the tile-count ceiling division wrapped for an image declaring
an extent near `int.MaxValue`, so a decompression bomb was refused for the wrong reason and with the
wrong exception type.

**`MqDecoder` relocated to `SharpAstro.Codecs.Abstractions`** (still `internal`, now with
`InternalsVisibleTo` for `SharpAstro.Jbig2` and `SharpAstro.Jpeg2000`). T.88 Annex E and T.800
Annex C specify the same arithmetic coder with the same `Qe` table, and two copies could only drift;
`InternalsVisibleTo` cannot cross two shipping packages, and every codec already depends on the
abstractions package, so this adds no dependency edge. **No behaviour change to `SharpAstro.Jbig2`**
— the T.88 Annex H.2 conformance vector still pins the same table in both directions. The coder is
shared but its *initialisation* is not: T.800 Table D.7 seeds three of its nineteen contexts away
from zero where T.88 starts all of them at zero.

**A fourth CI oracle: OpenJPEG.** `Oracle/jpeg2000/fetch.sh` downloads the pinned v2.5.4 release
build, verified by SHA-256, and CI runs it — so `REQUIRE_ORACLES=1` now covers jbig2dec, jxrlib,
jpegenc and OpenJPEG with none able to go silently missing. It is the only oracle here that is
downloaded rather than compiled: OpenJPEG wants CMake, and upstream publishes official builds for
both platforms that matter, so the dev box and CI run the *same bytes* rather than merely the same
source. `apt-get install libopenjp2-tools` was rejected on evidence — it is 2.4.0 on jammy and 2.5.x
on noble, so the two would have quietly disagreed about what the reference is.

Also fixed: `Jbig2Oracle` resolved `wsl.exe -- jbig2dec` as *available* on a machine with no
jbig2dec in WSL, because it probed by searching the combined output for the tool's own name and a
shell says `jbig2dec: command not found`. The substring test matched the very message proving the
tool was absent, turning 75 honest skips into red failures. It checks the exit code first now.

## 3.11

`SharpAstro.Tiff` gains a streaming read: `TiffReader.ReadInto<TSink>(span, ref sink)` hands each
page's metadata over before any pixels are decoded, then each strip's samples, instead of returning
one assembled raster. A caller converting those bytes into its own representation no longer holds
both at once -- 354 MiB of intermediate for a 13228x9354 RGB page.

`Read` is now that same machinery with a sink that concatenates, so there is one decode path and the
existing round-trip suite covers it. `Read`'s behaviour is unchanged.

An uncompressed page that needs no normalisation (no endian swap, `Predictor.None`) is handed over as
a slice of the input, with nothing copied and no scratch rented. That is the case worth having: for
an uncompressed page the decode is a memcpy, so it previously cost a file buffer plus a raster for
bytes already in the required layout. Read from a memory-mapped file through the sink, neither
exists. Since writers emit `II` and hosts are little-endian, no swap is needed at any bit depth in
practice, so 16- and 32-bit uncompressed pages take that path too.

## 3.10

- **`SharpAstro.Tiff` reads LZW (compression 5)**, the other compression a real-world TIFF is likely
  to arrive in -- it is Photoshop's own default, paired with horizontal differencing. Codes are packed
  MSB-first (GIF's LZW packs them LSB-first) and the code width grows on the *early change* schedule,
  stepping up one code before each power of two. Both quirks decode plausibly for a few hundred codes
  and then turn to noise, so getting either wrong yields a picture that starts correct and degrades
  partway down rather than an error. Validated against a real Photoshop-written 16-bit RGB file whose
  2,160,000 bytes match an independent implementation exactly, and round-tripped across every width
  boundary in `TiffLzwTests`. Composes with the predictor below rather than implying it.

## 3.9

- **`SharpAstro.Tiff` applies Predictor 2** (horizontal differencing, tag 317) on read. A ZIP/Deflate
  TIFF written with a predictor stores the row *derivative*, so decoding without one produced a
  structure map rather than the image: 7 of 15 files in a real corpus. Applied after the endian swap,
  since the differencing is per sample, not per byte. **Predictor 3** (floating-point) is refused
  explicitly rather than decoded wrongly.
- Per-strip allocation removed from the read path: strip scratch comes from `ArrayPool<byte>` and the
  destination is pre-sized. Note the ordering this exposed -- `MemoryStream.SetLength` **zero-fills on
  growth**, so it must be called before the strip is copied in, never after; the reverse wipes the
  tail of every strip and is data-dependent enough to pass a smooth test fixture.

## 3.8

- **`SharpAstro.Jbig2` resource limits.** Not foldable into 3.7: the shipped 3.7.651 decoder accepts
  decompression bombs. A region's declared size is attacker-chosen and T.88's MQ decoder never runs
  dry (E.3.4 reads past the end of the data as `0xFF`), so 82 bytes could demand 2 GiB and 20+ seconds
  of CPU, and two symbol-dictionary loops could spin for ever on flat memory. Adds a per-bitmap pixel
  ceiling, a decode budget scaled to the caller's page, and progress guards on both loops.

## 3.7

- **New package `SharpAstro.Jbig2`** -- clean-room ITU-T T.88, for PDF's `/JBIG2Decode`. While 3.7 was
  unshipped it grew from its first rung to every region type T.88 defines (generic + MMR/T.6,
  refinement, symbol dictionary + text region, pattern dictionary + halftone); that all landed in the
  first 3.7, which shipped as 3.7.651, so no further bump was needed.

## 3.6

- **Baseline JPEG encoder** (`JpegEncoder`, a byte-exact `stb_image_write` port) joins
  `SharpAstro.Jpeg`.

## 3.5

- `ColorEncoding` / `ToFloats` plus the TIFF / JXR / EXR / JXL facade adapters. That work first shipped
  as **3.4.49** because only the csprojs were bumped and not the version variable -- the failure that
  motivated collapsing the number to one place.
- **New package `SharpAstro.Jpeg.GainMap`** (Ultra HDR gain-map read/write) joins the family.

## 3.3

- **New package `SharpAstro.Jxl`** -- clean-room JPEG XL: lossless Modular plus lossy VarDCT.

## 3.2

- **New package `SharpAstro.Exr`** -- OpenEXR read + write.

## 3.1

- **Break:** `SharpAstro.Png`'s `CicpChunk` positional arguments switch from raw bytes to H.273 enums.
- `SharpAstro.Color.Icc` gains the `IccProfiles.WithCicp` helper.

## 3.0

- **Break:** `SharpAstro.Tiff`'s `IccProfile` API moves from `byte[]?` to `ReadOnlyMemory<byte>`.
