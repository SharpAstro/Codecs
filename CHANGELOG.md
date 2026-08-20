# Changelog

Release notes for the whole family: every package in this repo (the `SharpAstro.Codecs` facade plus
`SharpAstro.Tiff` / `.Png` / `.Exif` / `.Color.Icc` / `.Jpeg` / `.Jpeg.GainMap` / `.Jxr` / `.Exr` /
`.Jxl` / `.Jbig2`) ships at the same `Major.Minor` with a shared run-number patch, so one section
covers them all.

The number itself lives in exactly one place, `<VersionMajorMinor>` in `Directory.Build.props`; CI
reads it back and appends the run number. Bumping a release is editing that one line and adding a
section here. Newest first.

This file was created at 3.10 by migrating the note trail out of the two comment blocks that carried
it -- the `env:` block in `.github/workflows/dotnet.yml` (35 lines of prose above a variable that no
longer holds the number) and the header comment in `Directory.Build.props`. Entries below 3.10 are
those notes, verbatim in substance. 3.4 has no recorded note; it was never written down.

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
