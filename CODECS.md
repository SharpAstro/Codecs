# SharpAstro codec family

This repository hosts a family of pure-managed, AOT-compatible image codec
packages for .NET 10. They share infrastructure (CI, SourceLink, central
package versions) but each ships as an independent NuGet — consumers pick
exactly the formats they need.

## Package matrix

| Package | Decode | Encode | What it actually is |
|---|:---:|:---:|---|
| [`SharpAstro.Codecs`](src/SharpAstro.Codecs/) | *(dispatches)* | — | **Facade.** Magic-byte sniff + dispatch over the codecs below via `ImageCodecs.TryReadInfo` / `TryDecode` / `TryDecodeIntoRgba8` (zero-copy into a caller RGBA buffer). Reference this one package to decode an arbitrary supported still image — **PNG, JPEG, TIFF, JXR, EXR, JXL**, plus standalone **`.jb2`** JBIG2 files — without cherry-picking individual codecs. The fidelity tier preserves native bit depth (incl. float32 HDR) and carries `ColorEncoding` colour signalling, with `ToFloats()` for a policy-free RGBA float32 view; `TryDecodeIntoRgba8` serves integer-sample content only — HDR float has no canonical 8-bit projection, so those return false rather than project wrongly. **Gain-map ("Ultra HDR") JPEGs decode transparently**: `TryDecode` + `ToFloats()` reconstruct the authored HDR (linear, display-referred, at full authored headroom), while `TryDecodeIntoRgba8` / `ToRgba8` return the SDR base rendition — the float path is HDR, the 8-bit path is the graceful SDR fallback. Plain JPEGs are unaffected. **JBIG2 is registered for standalone `.jb2` files only** — a PDF-embedded JBIG2 stream has no signature to sniff and needs out-of-band globals + dimensions, so it can't come through the facade at all; use `Jbig2Decoder.Decode(embedded, globals, w, h)`. |
| [`SharpAstro.Codecs.Abstractions`](src/SharpAstro.Codecs.Abstractions/) | — | — | **Base.** `IImageDecoder` (static-abstract magic-byte sniff + fidelity/zero-copy decode) plus `IDecodedImage` / `RasterImage` (a codec-neutral decoded raster). Also home (since 3.5) to the ITU-T H.273 colour vocabulary — `ColorPrimaries` / `TransferFunction` / `MatrixCoefficients`, relocated from `Color.Icc` — and `ColorEncoding`, `IDecodedImage`'s meaning tier: what the samples encode (sRGB, PQ/HLG HDR, linear light) as opposed to how they're laid out. Zero runtime dependencies; every codec package below implements it. |
| [`SharpAstro.Png`](src/SharpAstro.Png/) | PNG | PNG | Pure-managed encoder + decoder. Writer: libpng-style adaptive per-row filtering, 8/16-bit RGBA/Gray. Reader: chunk parsing with CRC validation, color types 0/2/4/6 at 8/16-bit. **Both sides handle**: `iCCP` (ICC profile), `sRGB`, `gAMA`, `cHRM`, `eXIf`, plus PNG-3 HDR chunks `cICP` / `mDCv` / `cLLI` (HDR10, HLG signaling); the `IImageDecoder` adapter maps `cICP` (and `gAMA` 1.0 → linear) onto `IDecodedImage.ColorEncoding`, so HDR signalling survives the `SharpAstro.Codecs` facade — and PQ/HLG-tagged files refuse the 8-bit display path (`TryDecodeIntoRgba8`) rather than project HDR code values wrongly. Also exports `PngPredictor` as a reusable row-unfilter building block (TIFF Predictor=2, PDF FlateDecode). Sub-byte bit depths (1/2/4), indexed-color (PLTE), and Adam7 interlacing are not yet supported. |
| [`SharpAstro.Jpeg`](src/SharpAstro.Jpeg/) | JPEG | JPEG | Pure-managed **clean-room** JPEG (ITU-T T.81) decoder. Baseline sequential + progressive DCT, restart intervals, 4:4:4 / 4:2:2 / 4:2:0 / arbitrary chroma subsampling, grayscale, YCbCr, RGB-marked, Adobe CMYK / YCCK (APP14). **Scaled decode at 1/2 / 1/4 / 1/8 via reduced inverse DCT** — a 33 MP scan decodes straight to LOD/thumbnail size without the full-resolution raster ever existing (the motivating use case: killing image-decode LOH churn in drawboard's pdf-viewer). Pooled internals + `DecodeTo` caller-buffer API: a 5100×6600 q85 4:2:0 source decodes at 1/4 scale in ~50 ms with ~1 MB allocated, vs ~380 ms / ~130 MB full-scale. **Full-scale output is pinned byte-exact to a committed golden baseline** — frozen from the stb_image (StbImageSharp) reference decoder before that port was removed from the repo (see `JpegDecoderOracleTests`) — with Magick.NET (libjpeg) as the independent tolerance oracle; scaled output is property-tested against flat-colour and box-downsample references. **Encoder shipped (3.6):** `JpegEncoder.Encode` — baseline sequential DCT, 4:4:4 / 4:2:0 (quality-derived under `JpegSubsampling.Auto`, or forced), quality 1..100, channels 1..4. A faithful port of the `stb_image_write` JPEG writer, validated **byte-for-byte identical** to that reference (`Oracle/jpegenc`, pinned by SHA) and frozen as a committed golden digest, with libjpeg (Magick.NET) + our own decoder as independent acceptance oracles. Progressive, restart intervals, optimized/grayscale-only Huffman are future rungs (see [`ROADMAP-jpeg-encoder.md`](ROADMAP-jpeg-encoder.md)). `JpegSegmentScanner` (3.5) surfaces the header's marker segments at the byte level — APPn payload discovery for ICC / EXIF / XMP / MPF (pair with `SharpAstro.Exif` for EXIF parsing). **Gain-map JPEG (Ultra HDR) shipped** as `SharpAstro.Jpeg.GainMap` (below); with the encoder here, Ultra HDR generation is now fully in-family. **Also ships `LosslessJpeg`** — a second, structurally unrelated decoder for lossless JPEG (ITU-T T.81 Annex H, SOF3): Huffman-coded sample-difference predictors 1–7, no quantisation, **up to 16-bit precision**, DRI/RSTn restarts, point transform. Shares no code with `JpegDecoder` (SOF0/1/2 only) and returns raw `ushort` samples — no demosaic, no Bayer interpretation, no CR2 slice stitching. Its motivating consumer is raw-camera decoding (Canon CR2's IFD3 strip is 14-bit Bayer as a 2-component interleaved lossless sub-frame). Arithmetic-coded JPEG is not supported by either decoder. |
| [`SharpAstro.Jpeg.IccInjector`](src/SharpAstro.Jpeg.IccInjector/) | — | — | `JpegIccInjector` — splices an APP2 ICC segment into an already-encoded JPEG byte stream. Not a JPEG codec; the decoder now ships as `SharpAstro.Jpeg`, and a future encoder slots in there too. |
| [`SharpAstro.Jpeg.GainMap`](src/SharpAstro.Jpeg.GainMap/) | Ultra HDR | Ultra HDR | Gain-map ("Ultra HDR" / Adobe hdrgm 1.0) JPEG, shipped 3.5. **Read:** `JpegGainMap.TryRead` / `TrySplit` locate the gain map via MPF (falling back to the GContainer XMP directory), decode both renditions through `SharpAstro.Jpeg`, and `GainMapImage.ReconstructHdr(headroom)` produces display-adaptive HDR linear floats (headroom 1.0 reproduces the base exactly; `HdrCapacityMax` gives the full authored HDR). **Write:** `Compute` fits a gain map + metadata from an aligned HDR-linear/SDR pair; `Assemble` splices GContainer XMP + MPF + the hdrgm-tagged gain-map JPEG around any encoder's baseline output (the `IccInjector` pattern — no JPEG encoder required, neither rendition is re-encoded). Emits the Android Ultra HDR v1 superset, satisfying **both** Chromium/Skia locator paths — **verified against Chromium**: headless Edge renders the assembled file byte-identically to the base under an SDR colour profile and applies the gain map under an HDR profile. **Facade-integrated:** `SharpAstro.Codecs` registers a `GainMapImageDecoder` ahead of the plain JPEG decoder, so `ImageCodecs.TryDecode(...).ToFloats()` reconstructs HDR while the 8-bit path stays the SDR base — no need to reference this package directly just to read Ultra HDR floats. ISO 21496-1 binary metadata (read), the Apple dialect, and the libultrahdr oracle harness remain roadmapped ([`ROADMAP-gain-map.md`](ROADMAP-gain-map.md)). |
| [`SharpAstro.Tiff`](src/SharpAstro.Tiff/) | TIFF | TIFF | Full pure-managed TIFF reader/writer. Multi-page, 8/16/32-bit uint + IEEE-Float, Uncompressed / Deflate / Zlib / **LZW** (MSB-first, early-change widths), **Predictor 2** (horizontal differencing) on read, II + MM byte order, SampleFormat/SMin/SMax/ICC round-trip. Predictor 3 (floating-point) is refused rather than decoded wrongly. |
| [`SharpAstro.Jxr`](src/SharpAstro.Jxr/) | JXR | JXR | Faithful, table-exact C# re-port of Microsoft's **jxrlib** C reference codec (the earlier spec-derived codec was retired). BD8/BD16/BD16F/BD32F × grayscale (Y-only) + RGB, plus **signed BD16S / BD32S** grayscale + RGB (native FITS BITPIX 16/32), **spatial + frequency** ordering — single-tile, plus **multi-tile soft tiling** (`INDEX_TABLE`, all formats — RGB + grayscale across every bit depth incl. signed) — Photo Overlap Transform (OL_NONE / OL_ONE / OL_TWO), lossy quantization, **arbitrary (non-16-aligned) dimensions** (pad-then-crop), full `.jxr` file container. RGB automatically uses YCoCg-R + InternalClrFmt=YUV444 internally for Windows Photo / WIC interop; BD32F is mono-only (T.832 has no Table A.6 GUID for BD32F RGB). **Validated bit-exact against the jxrlib reference binaries** — codestream byte-match vs `JxrEncApp` plus both decode directions. **YUV420/422 chroma subsampling now both encodes and decodes at every overlap level** (4:2:0 / 4:2:2, OL_NONE / OL_ONE / OL_TWO) — decode bit-exact vs `JxrDecApp`, encode **byte-for-byte identical to `JxrEncApp`** (5-tap `[1,4,6,4,1]/16` downsample in the YCoCg-R domain, scaled-arithmetic mode which jxrlib forces for subsampled chroma even at QP 1). **General lossy QP is byte-exact vs `JxrEncApp -q N`** for RGB 4:4:4 / 4:2:0 / 4:2:2 **and BD8 grayscale** across QP indices and overlap levels (the per-band UV-shift quantizer — chroma DC/LP at the half-step `SHIFTZERO-1` shift — plus the DC band's `iQP>>1` deadzone); the BD16-integer (gray/RGB) and HDR float (BD16F gray/RGB, BD32F gray) formats round-trip lossy QP **and** NO_FLEXBITS exactly vs `JxrDecApp` (BD16-integer uses a distinct scaled store rounding — `(1<<(s-1))` with no −1 — versus BD8/BD16F). **Signed BD16S/BD32S** (gray + RGB, native FITS) and **planar alpha** (32bppBGRA — colour + alpha codestreams byte-exact vs `JxrEncApp -a 2`) are supported, and the decoder also reads **per-channel (distinct Y/U/V) QP** from jxrlib quality-mode files. Planar alpha and **FREQUENCY mode** (jxrlib's default bitstream ordering — separate DC/LP/HP/FLEXBITS band packets, byte-exact vs `JxrEncApp` for RGB 4:4:4 BD8) are supported. Hard tiling and `WINDOWING_FLAG` stay out of scope (the reference `JxrEncApp` can't emit them, so they can't be byte-validated). See **[`JXR-FORMAT.md`](JXR-FORMAT.md)** for the full per-axis format-support breakdown (bit depths, channel layouts, internal colour formats, chroma subsampling, compression structure) with ticks. |
| [`SharpAstro.Exr`](src/SharpAstro.Exr/) | EXR | EXR | Pure-managed OpenEXR (`.exr`) reader/writer. Single-part scanline images, HALF/FLOAT/UINT channels, mono + RGB. Compression: NONE / RLE / ZIP / ZIPS / PIZ (the wavelet+Huffman default) — all lossless. `ExrImageCodec` façade for HDR float (mono FLOAT / RGB HALF, verbatim scene-linear values). **Validated value-exact against OpenEXR** via Magick.NET (self round-trip bit-exact; both decode directions). Tiled / multi-part / deep, and the lossy PXR24 / B44 / DWA schemes, are out of scope. |
| [`SharpAstro.Jxl`](src/SharpAstro.Jxl/) | JXL | JXL | Pure-managed **clean-room** JPEG XL (ISO/IEC 18181) — spec-as-judge + Magick.NET (libjxl) as the empirical oracle + jxl-oxide as a read-only bit-layout reference. **Lossless Modular** path: 8/16-bit integer **and IEEE-float (F16/F32)**, grey + RGB, single group (each dimension ≤ 1024); integer RGB decorrelated with the reversible YCoCg-R colour transform. `JxlImageCodec` façade — `EncodeRgb24`/`Gray8`/`Rgb48`/`Gray16` for integer, `EncodeGrayF32`/`GrayF16`/`RgbF16`/`RgbF32` (+ matching `Decode*`) for HDR float (values verbatim, not normalised), `EncodeRgb24Lossy` for **lossy VarDCT** (8-bit RGB, libjxl-style Butteraugli `distance` knob, dims multiples of 8 up to 16384), plus a general `Decode` (auto-detects Modular vs VarDCT) and `JxlFile.ReadInfo`. **Validated both directions** — our decode of real libjxl images, and libjxl/Magick decode of our output (pixel-exact for integer; bit-exact self round-trip + value-exact-vs-libjxl for float). Full hybrid-integer / ANS / prefix entropy stack, MA decision tree, 14 predictors (incl. the self-correcting weighted predictor), and RCT/Palette transforms on decode. **Lossy VarDCT** is supported for 8-bit RGB with a libjxl-style Butteraugli `distance` quality knob (DCT8, XYB, full-resolution chroma, multi-group / multi-LF-group up to 16384 px) — libjxl-validated both directions. Grayscale-lossy, alpha, and `do_ycbcr` are not yet supported. |
| [`SharpAstro.Color.Icc`](src/SharpAstro.Color.Icc/) | — | — | Bundled sRGB v4 ICC blob (588 bytes, lazily loaded) for embedding into TIFF/PNG/JPEG via the codec packages above. Not a codec. |
| [`SharpAstro.Jbig2`](src/SharpAstro.Jbig2/) | JBIG2 | — | Pure-managed **clean-room** JBIG2 (ITU-T T.88) bilevel decoder, written for PDF's `/JBIG2Decode` filter — spec-derived, because every reference implementation is licensed in a way an Unlicense repo can't vendor (`jbig2dec` is AGPL; pdf.js / PDFium / `jbig2enc` are notice-retaining). **The primary entry point is not the facade**: `Jbig2Decoder.Decode(embedded, globals, width, height)`. A PDF-embedded stream is not a file — T.88's embedded organization has no file header to sniff, its shared segment dictionaries live in a separate `/DecodeParms /JBIG2Globals` stream, and its dimensions come from the image dictionary, so all three are arguments. Standalone `.jb2` files (sequential **and** random-access organization, striped pages via end-of-stripe) go through `DecodeFile` and register with the facade by magic bytes as a courtesy. **Shipped in 3.7 — the complete arithmetic decoding path:** the MQ arithmetic decoder (Annex E); generic regions **both ways**, arithmetic (GBTEMPLATE 0–3, TPGDON, arbitrary AT pixels) and **MMR / ITU-T T.6** (§6.2.6, the Group 4 fax coding, which is also what PDF's `/CCITTFaxDecode` carries at `K < 0`); **generic refinement regions**; **symbol dictionaries + text regions** (every reference corner, transposed strips, multi-row strips, SBDSOFFSET, per-instance refinement, refine/aggregate symbols, and dictionaries shared across streams via `/JBIG2Globals`); **pattern dictionaries + halftone regions** (Gray-coded bitplanes over a sheared lattice); page information; and region composition (OR/AND/XOR/XNOR/REPLACE, clipped to the page). **Not implemented:** the Huffman-coded variants (SDHUFF/SBHUFF and custom table segments), MMR inside pattern dictionaries and halftone regions, HENABLESKIP, and TPGRON in a refinement region — each throws `NotSupportedException` naming the feature rather than returning a plausible-looking wrong page. **Validation:** the MQ coder is pinned to the published ITU-T T.88 Annex H.2 conformance vector **in both directions** (256 known decisions ⟷ the 30-byte codestream), and each context template cell-by-cell by one-hot tests against Figures 4–7. Beyond that it is third-party bytes throughout: **jbig2enc** generic and symbol-mode output as committed fixtures, **libtiff** Group 4 output via Magick.NET for MMR (with a sweep covering every run length T.4 defines — added after a deliberately mislabelled entry survived the picture-shaped cases), and the **jbig2dec** reference decoder for everything no encoder emits: refinement, halftone, and seven of the eight text-region corner combinations. Output is one byte per pixel, **1 = black (T.88 polarity)**, plus an 8-bit grey projection; `/Decode` inversion and `ImageMask` semantics stay in the caller's PDF layer. **Hardened for untrusted input (3.8):** a PDF-supplied codestream picks its own region dimensions, and T.88's MQ decoder deliberately reads past the end of its data as `0xFF` rather than failing (E.3.4) — so 82 bytes could demand 2 GiB and 20+ seconds, and two symbol-dictionary loops could spin for ever on flat memory. Decoding is now bounded by a **2^28-pixel ceiling per bitmap** (enforced at bitmap construction, since symbol dictionaries size glyphs from coded deltas and never pass through region info) plus a **total budget scaled to the caller's page**, and every loop is guaranteed to advance; malformed input raises `InvalidDataException` instead of exhausting memory or hanging, and the `Try*` methods answer `false` rather than throwing. Found and verified by mutation-fuzzing (~71,500 malformed inputs, zero unexpected exceptions); the residual is documented rather than implied away — a hostile *standalone* `.jb2` can still declare a large-but-legal page and cost ≈3.7 s / ≈475 MiB, which the PDF entry point cannot be made to do because its budget is anchored to the caller's dimensions. Remaining rungs: [`ROADMAP-pdf-codecs.md`](ROADMAP-pdf-codecs.md). |
| [`SharpAstro.Exif`](src/SharpAstro.Exif/) | EXIF | — | Pure-managed EXIF metadata reader. Parses EXIF blobs from JPEG (APP1), TIFF (sub-IFD), and PNG (eXIf chunk). |

## JPEG decode path — recommendation

Use **`SharpAstro.Jpeg`** (`JpegDecoder.Decode` / `DecodeTo`): baseline + progressive,
scaled 1/2–1/8 LOD decode, and pooled caller-buffer APIs. Full-scale output is pinned
byte-exact to the golden baseline (originally the stb_image reference decode). `JpegDecoder`
itself is the DCT path only — it caps at 8-bit precision and handles SOF0/1/2.

**Lossless JPEG (SOF3) is a separate entry point in the same package:** `LosslessJpeg.FromMemory`
/ `FromStream`, up to 16-bit precision, returning raw `ushort` samples (see the matrix row above).
Reach for it for raw-camera payloads, not for ordinary photographic JPEGs. **Arithmetic-coded
JPEG is not supported** by either path.

## PNG decode path — recommendation

For a round-trippable PNG workflow, use **`SharpAstro.Png`** on both
sides — `PngWriter` to encode, `PngReader` to decode. The reader
preserves iCCP / sRGB / gAMA / cHRM / eXIf metadata and decodes 16-bit
samples faithfully (returned in PNG's big-endian byte order; use
`PngImage.AsUInt16Samples()` for a host-endian view).

## Picking what to consume

The packages are independent — pull only what your project actually needs:

```xml
<!-- Reading any supported still image (facade: PNG / JPEG / TIFF / JXR / EXR / JXL sniff+dispatch) -->
<PackageReference Include="SharpAstro.Codecs" />

<!-- Writing PNGs -->
<PackageReference Include="SharpAstro.Png" />

<!-- Working with TIFFs (both directions) -->
<PackageReference Include="SharpAstro.Tiff" />

<!-- HDR-master JXR (the user's astrophotography pipeline) -->
<PackageReference Include="SharpAstro.Jxr" />

<!-- HDR-master OpenEXR (mono FLOAT / RGB HALF, lossless) -->
<PackageReference Include="SharpAstro.Exr" />

<!-- Lossless JPEG XL (8/16-bit RGB/grey) -->
<PackageReference Include="SharpAstro.Jxl" />

<!-- Decoding JPEG (incl. scaled 1/2–1/8 LOD decode) -->
<PackageReference Include="SharpAstro.Jpeg" />

<!-- Ultra HDR gain-map JPEGs: read/reconstruct HDR + assemble/generate -->
<PackageReference Include="SharpAstro.Jpeg.GainMap" />

<!-- Embedding sRGB profiles -->
<PackageReference Include="SharpAstro.Color.Icc" />

<!-- JBIG2 bilevel, for PDF /JBIG2Decode image streams -->
<PackageReference Include="SharpAstro.Jbig2" />

<!-- Reading EXIF from any of the above -->
<PackageReference Include="SharpAstro.Exif" />
```

`DIR.Lib` (the rendering library that motivates much of this work) is
intentionally **codec-free**: `BoxRasterizer.RenderToRgba` returns a raw
`RgbaImage`, and the consumer decides which encoder to wrap it with.

## Naming-convention status

The package names follow a few patterns today:

1. **`Codecs` / `Codecs.Abstractions`** — the facade + its base contract (sniff/dispatch, `IImageDecoder`).
2. **`Png` / `Tiff` / `Jxr` / `Exr` / `Jxl` / `Jbig2`** — names the *format*. Tiff / Jxr / Png / Exr / Jxl are
   symmetric (encode + decode); `Jbig2` is decode-only, and deliberately so — nothing in the family
   or its consumers needs to *produce* JBIG2.
3. **`Jpeg`** — a full codec since 3.6 (decode + encode), symmetric with `Png` / `Tiff` / `Jxr`;
   `Jpeg.IccInjector` and `Jpeg.GainMap` stay separate metadata-domain packages.
4. **`Color.Icc` / `Exif`** — names a *domain* (color management, metadata) rather than a codec.

Milestones:

- ✅ **`SharpAstro.Jpeg` renamed to `SharpAstro.Jpeg.IccInjector`** — the `Jpeg` PackageId is now reserved for a full codec.
- ✅ **Pure-managed JPEG decoder shipped as `SharpAstro.Jpeg`** (baseline + progressive, scaled decode, golden-baseline byte-exact).
- ✅ **Pure-managed baseline JPEG encoder shipped in `SharpAstro.Jpeg`** (3.6) — `JpegEncoder`, a byte-exact `stb_image_write` port (baseline, 4:4:4 / 4:2:0, quality 1..100). `SharpAstro.Jpeg` is now a full codec; Ultra HDR generation no longer needs an external encoder. Progressive/optimized-Huffman/grayscale-only output are the remaining rungs ([`ROADMAP-jpeg-encoder.md`](ROADMAP-jpeg-encoder.md)).
- ✅ **Pure-managed PNG decoder added to `SharpAstro.Png`** (`PngReader.Decode`) — now symmetric with `Tiff` / `Jxr`. Sub-byte depths / indexed-color / Adam7 are deferred follow-ups.
- ✅ **`SharpAstro.Codecs` facade shipped** — one package to sniff + decode (PNG + JPEG) instead of cherry-picking.
- ✅ **Removed `SharpAstro.StbImage`** (the auto-generated stb_image port) — it had no first-party consumers, and the JPEG byte-exact guarantee it anchored is now a committed golden baseline. **Trade-off:** the family no longer decodes the stb-only formats **BMP / TGA / PSD / GIF / HDR** (no clean-room sibling exists for them yet).
- ✅ **Gain-map JPEG (Ultra HDR) shipped as `SharpAstro.Jpeg.GainMap`** (3.5) — an `IccInjector`-style metadata-domain package: read/reconstruct HDR from the SDR base + gain map, assemble/generate on the write side — the publishing tier for HDR masters (one JPEG, correct on SDR browsers, HDR on capable displays). The gain map is the author-shipped answer to the tone-mapping question the facade deliberately refuses to answer itself. Write path verified against Chromium (headless differential check). Remaining rungs — ISO 21496-1 binary metadata, the Apple dialect, the libultrahdr oracle harness: [`ROADMAP-gain-map.md`](ROADMAP-gain-map.md).

- ✅ **`SharpAstro.Jbig2` shipped complete (3.7), all five rungs** — the MQ arithmetic decoder, generic regions in both codings (arithmetic templates 0–3 with TPGDON and arbitrary AT pixels, and MMR / ITU-T T.6), refinement regions, symbol dictionaries + text regions, pattern dictionaries + halftone regions, page information, and region composition. Clean-room from ITU-T T.88 and T.4/T.6 throughout. The contract prediction in the roadmap held: it ships an explicit `Jbig2Decoder.Decode(embedded, globals, w, h)` entry point, with `IImageDecoder` registration as a courtesy for standalone `.jb2` files only. What remains is the **Huffman-coded variants** (SDHUFF/SBHUFF and custom table segments), which no available encoder emits — so there would be nothing to validate an implementation against: [`ROADMAP-pdf-codecs.md`](ROADMAP-pdf-codecs.md).

- 📋 **JPX (JPEG 2000, PDF's `/JPXDecode`) assessed, not started** — the last image filter in the PDF spec the family can't decode. It fits the facade fine (JP2 and raw J2K both have magic bytes), but it is a `SharpAstro.Jxr`-scale project (8–15k LOC decode-only: EBCOT tier-1/tier-2, 5/3 + 9/7 DWT, the JP2 box container) and stays gated behind JBIG2 maturing. Must also be **clean-room from ITU-T T.800** — OpenJPEG / PDFium / pdf.js are all notice-retaining, so none can be a port source for an Unlicense repo, though all are fine as oracle *binaries* (`opj_decompress` is BSD-2 and clang-buildable, an ideal fit for the existing `Oracle/build.sh` pattern). Full scoping and licence matrix: [`ROADMAP-pdf-codecs.md`](ROADMAP-pdf-codecs.md).

None of this is urgent — the packages coexist fine and ship from the same CI.

## Building & testing

```bash
git clone https://github.com/SharpAstro/Codecs
cd Codecs
dotnet build Codecs.JustTests.sln -c Release
dotnet test  Codecs.JustTests.sln -c Release
```

Requires the .NET 10 SDK.

## License

All packages in this repository are released under the [Unlicense](UNLICENSE)
(public domain).
