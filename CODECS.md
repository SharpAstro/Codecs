# SharpAstro codec family

This repository hosts a family of pure-managed, AOT-compatible image codec
packages for .NET 10. They share infrastructure (CI, SourceLink, central
package versions) but each ships as an independent NuGet — consumers pick
exactly the formats they need.

## Package matrix

| Package | Decode | Encode | What it actually is |
|---|:---:|:---:|---|
| [`SharpAstro.Codecs`](src/SharpAstro.Codecs/) | *(dispatches)* | — | **Facade.** Magic-byte sniff + dispatch over the codecs below via `ImageCodecs.TryReadInfo` / `TryDecode` / `TryDecodeIntoRgba8` (zero-copy into a caller RGBA buffer). Reference this one package to decode an arbitrary supported still image — **PNG, JPEG, TIFF, JXR, EXR, JXL** — without cherry-picking individual codecs. The fidelity tier preserves native bit depth (incl. float32 HDR) and carries `ColorEncoding` colour signalling, with `ToFloats()` for a policy-free RGBA float32 view; `TryDecodeIntoRgba8` serves integer-sample content only — HDR float has no canonical 8-bit projection, so those return false rather than project wrongly. |
| [`SharpAstro.Codecs.Abstractions`](src/SharpAstro.Codecs.Abstractions/) | — | — | **Base.** `IImageDecoder` (static-abstract magic-byte sniff + fidelity/zero-copy decode) plus `IDecodedImage` / `RasterImage` (a codec-neutral decoded raster). Also home (since 3.5) to the ITU-T H.273 colour vocabulary — `ColorPrimaries` / `TransferFunction` / `MatrixCoefficients`, relocated from `Color.Icc` — and `ColorEncoding`, `IDecodedImage`'s meaning tier: what the samples encode (sRGB, PQ/HLG HDR, linear light) as opposed to how they're laid out. Zero runtime dependencies; every codec package below implements it. |
| [`SharpAstro.Png`](src/SharpAstro.Png/) | PNG | PNG | Pure-managed encoder + decoder. Writer: libpng-style adaptive per-row filtering, 8/16-bit RGBA/Gray. Reader: chunk parsing with CRC validation, color types 0/2/4/6 at 8/16-bit. **Both sides handle**: `iCCP` (ICC profile), `sRGB`, `gAMA`, `cHRM`, `eXIf`, plus PNG-3 HDR chunks `cICP` / `mDCv` / `cLLI` (HDR10, HLG signaling); the `IImageDecoder` adapter maps `cICP` (and `gAMA` 1.0 → linear) onto `IDecodedImage.ColorEncoding`, so HDR signalling survives the `SharpAstro.Codecs` facade — and PQ/HLG-tagged files refuse the 8-bit display path (`TryDecodeIntoRgba8`) rather than project HDR code values wrongly. Also exports `PngPredictor` as a reusable row-unfilter building block (TIFF Predictor=2, PDF FlateDecode). Sub-byte bit depths (1/2/4), indexed-color (PLTE), and Adam7 interlacing are not yet supported. |
| [`SharpAstro.Jpeg`](src/SharpAstro.Jpeg/) | JPEG | JPEG | Pure-managed **clean-room** JPEG (ITU-T T.81) decoder. Baseline sequential + progressive DCT, restart intervals, 4:4:4 / 4:2:2 / 4:2:0 / arbitrary chroma subsampling, grayscale, YCbCr, RGB-marked, Adobe CMYK / YCCK (APP14). **Scaled decode at 1/2 / 1/4 / 1/8 via reduced inverse DCT** — a 33 MP scan decodes straight to LOD/thumbnail size without the full-resolution raster ever existing (the motivating use case: killing image-decode LOH churn in drawboard's pdf-viewer). Pooled internals + `DecodeTo` caller-buffer API: a 5100×6600 q85 4:2:0 source decodes at 1/4 scale in ~50 ms with ~1 MB allocated, vs ~380 ms / ~130 MB full-scale. **Full-scale output is pinned byte-exact to a committed golden baseline** — frozen from the stb_image (StbImageSharp) reference decoder before that port was removed from the repo (see `JpegDecoderOracleTests`) — with Magick.NET (libjpeg) as the independent tolerance oracle; scaled output is property-tested against flat-colour and box-downsample references. **Encoder shipped (3.6):** `JpegEncoder.Encode` — baseline sequential DCT, 4:4:4 / 4:2:0 (quality-derived under `JpegSubsampling.Auto`, or forced), quality 1..100, channels 1..4. A faithful port of the `stb_image_write` JPEG writer, validated **byte-for-byte identical** to that reference (`Oracle/jpegenc`, pinned by SHA) and frozen as a committed golden digest, with libjpeg (Magick.NET) + our own decoder as independent acceptance oracles. Progressive, restart intervals, optimized/grayscale-only Huffman are future rungs (see [`ROADMAP-jpeg-encoder.md`](ROADMAP-jpeg-encoder.md)). `JpegSegmentScanner` (3.5) surfaces the header's marker segments at the byte level — APPn payload discovery for ICC / EXIF / XMP / MPF (pair with `SharpAstro.Exif` for EXIF parsing). **Gain-map JPEG (Ultra HDR) shipped** as `SharpAstro.Jpeg.GainMap` (below); with the encoder here, Ultra HDR generation is now fully in-family. |
| [`SharpAstro.Jpeg.IccInjector`](src/SharpAstro.Jpeg.IccInjector/) | — | — | `JpegIccInjector` — splices an APP2 ICC segment into an already-encoded JPEG byte stream. Not a JPEG codec; the decoder now ships as `SharpAstro.Jpeg`, and a future encoder slots in there too. |
| [`SharpAstro.Jpeg.GainMap`](src/SharpAstro.Jpeg.GainMap/) | Ultra HDR | Ultra HDR | Gain-map ("Ultra HDR" / Adobe hdrgm 1.0) JPEG, shipped 3.5. **Read:** `JpegGainMap.TryRead` / `TrySplit` locate the gain map via MPF (falling back to the GContainer XMP directory), decode both renditions through `SharpAstro.Jpeg`, and `GainMapImage.ReconstructHdr(headroom)` produces display-adaptive HDR linear floats (headroom 1.0 reproduces the base exactly; `HdrCapacityMax` gives the full authored HDR). **Write:** `Compute` fits a gain map + metadata from an aligned HDR-linear/SDR pair; `Assemble` splices GContainer XMP + MPF + the hdrgm-tagged gain-map JPEG around any encoder's baseline output (the `IccInjector` pattern — no JPEG encoder required, neither rendition is re-encoded). Emits the Android Ultra HDR v1 superset, satisfying **both** Chromium/Skia locator paths — **verified against Chromium**: headless Edge renders the assembled file byte-identically to the base under an SDR colour profile and applies the gain map under an HDR profile. ISO 21496-1 binary metadata (read), the Apple dialect, and the libultrahdr oracle harness remain roadmapped ([`ROADMAP-gain-map.md`](ROADMAP-gain-map.md)). |
| [`SharpAstro.Tiff`](src/SharpAstro.Tiff/) | TIFF | TIFF | Full pure-managed TIFF reader/writer. Multi-page, 8/16/32-bit uint + IEEE-Float, Uncompressed / Deflate / Zlib, II + MM byte order, SampleFormat/SMin/SMax/ICC round-trip. |
| [`SharpAstro.Jxr`](src/SharpAstro.Jxr/) | JXR | JXR | Faithful, table-exact C# re-port of Microsoft's **jxrlib** C reference codec (the earlier spec-derived codec was retired). BD8/BD16/BD16F/BD32F × grayscale (Y-only) + RGB, plus **signed BD16S / BD32S** grayscale + RGB (native FITS BITPIX 16/32), **spatial + frequency** ordering — single-tile, plus **multi-tile soft tiling** (`INDEX_TABLE`, all formats — RGB + grayscale across every bit depth incl. signed) — Photo Overlap Transform (OL_NONE / OL_ONE / OL_TWO), lossy quantization, **arbitrary (non-16-aligned) dimensions** (pad-then-crop), full `.jxr` file container. RGB automatically uses YCoCg-R + InternalClrFmt=YUV444 internally for Windows Photo / WIC interop; BD32F is mono-only (T.832 has no Table A.6 GUID for BD32F RGB). **Validated bit-exact against the jxrlib reference binaries** — codestream byte-match vs `JxrEncApp` plus both decode directions. **YUV420/422 chroma subsampling now both encodes and decodes at every overlap level** (4:2:0 / 4:2:2, OL_NONE / OL_ONE / OL_TWO) — decode bit-exact vs `JxrDecApp`, encode **byte-for-byte identical to `JxrEncApp`** (5-tap `[1,4,6,4,1]/16` downsample in the YCoCg-R domain, scaled-arithmetic mode which jxrlib forces for subsampled chroma even at QP 1). **General lossy QP is byte-exact vs `JxrEncApp -q N`** for RGB 4:4:4 / 4:2:0 / 4:2:2 **and BD8 grayscale** across QP indices and overlap levels (the per-band UV-shift quantizer — chroma DC/LP at the half-step `SHIFTZERO-1` shift — plus the DC band's `iQP>>1` deadzone); the BD16-integer (gray/RGB) and HDR float (BD16F gray/RGB, BD32F gray) formats round-trip lossy QP **and** NO_FLEXBITS exactly vs `JxrDecApp` (BD16-integer uses a distinct scaled store rounding — `(1<<(s-1))` with no −1 — versus BD8/BD16F). **Signed BD16S/BD32S** (gray + RGB, native FITS) and **planar alpha** (32bppBGRA — colour + alpha codestreams byte-exact vs `JxrEncApp -a 2`) are supported, and the decoder also reads **per-channel (distinct Y/U/V) QP** from jxrlib quality-mode files. Planar alpha and **FREQUENCY mode** (jxrlib's default bitstream ordering — separate DC/LP/HP/FLEXBITS band packets, byte-exact vs `JxrEncApp` for RGB 4:4:4 BD8) are supported. Hard tiling and `WINDOWING_FLAG` stay out of scope (the reference `JxrEncApp` can't emit them, so they can't be byte-validated). See **[`JXR-FORMAT.md`](JXR-FORMAT.md)** for the full per-axis format-support breakdown (bit depths, channel layouts, internal colour formats, chroma subsampling, compression structure) with ticks. |
| [`SharpAstro.Exr`](src/SharpAstro.Exr/) | EXR | EXR | Pure-managed OpenEXR (`.exr`) reader/writer. Single-part scanline images, HALF/FLOAT/UINT channels, mono + RGB. Compression: NONE / RLE / ZIP / ZIPS / PIZ (the wavelet+Huffman default) — all lossless. `ExrImageCodec` façade for HDR float (mono FLOAT / RGB HALF, verbatim scene-linear values). **Validated value-exact against OpenEXR** via Magick.NET (self round-trip bit-exact; both decode directions). Tiled / multi-part / deep, and the lossy PXR24 / B44 / DWA schemes, are out of scope. |
| [`SharpAstro.Jxl`](src/SharpAstro.Jxl/) | JXL | JXL | Pure-managed **clean-room** JPEG XL (ISO/IEC 18181) — spec-as-judge + Magick.NET (libjxl) as the empirical oracle + jxl-oxide as a read-only bit-layout reference. **Lossless Modular** path: 8/16-bit integer **and IEEE-float (F16/F32)**, grey + RGB, single group (each dimension ≤ 1024); integer RGB decorrelated with the reversible YCoCg-R colour transform. `JxlImageCodec` façade — `EncodeRgb24`/`Gray8`/`Rgb48`/`Gray16` for integer, `EncodeGrayF32`/`GrayF16`/`RgbF16`/`RgbF32` (+ matching `Decode*`) for HDR float (values verbatim, not normalised), `EncodeRgb24Lossy` for **lossy VarDCT** (8-bit RGB, libjxl-style Butteraugli `distance` knob, dims multiples of 8 up to 16384), plus a general `Decode` (auto-detects Modular vs VarDCT) and `JxlFile.ReadInfo`. **Validated both directions** — our decode of real libjxl images, and libjxl/Magick decode of our output (pixel-exact for integer; bit-exact self round-trip + value-exact-vs-libjxl for float). Full hybrid-integer / ANS / prefix entropy stack, MA decision tree, 14 predictors (incl. the self-correcting weighted predictor), and RCT/Palette transforms on decode. **Lossy VarDCT** is supported for 8-bit RGB with a libjxl-style Butteraugli `distance` quality knob (DCT8, XYB, full-resolution chroma, multi-group / multi-LF-group up to 16384 px) — libjxl-validated both directions. Grayscale-lossy, alpha, and `do_ycbcr` are not yet supported. |
| [`SharpAstro.Color.Icc`](src/SharpAstro.Color.Icc/) | — | — | Bundled sRGB v4 ICC blob (588 bytes, lazily loaded) for embedding into TIFF/PNG/JPEG via the codec packages above. Not a codec. |
| [`SharpAstro.Exif`](src/SharpAstro.Exif/) | EXIF | — | Pure-managed EXIF metadata reader. Parses EXIF blobs from JPEG (APP1), TIFF (sub-IFD), and PNG (eXIf chunk). |

## JPEG decode path — recommendation

Use **`SharpAstro.Jpeg`** (`JpegDecoder.Decode` / `DecodeTo`): baseline + progressive,
scaled 1/2–1/8 LOD decode, and pooled caller-buffer APIs. Full-scale output is pinned
byte-exact to the golden baseline (originally the stb_image reference decode). It caps at
8-bit precision and does not do lossless/arithmetic JPEG.

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

<!-- Reading EXIF from any of the above -->
<PackageReference Include="SharpAstro.Exif" />
```

`DIR.Lib` (the rendering library that motivates much of this work) is
intentionally **codec-free**: `BoxRasterizer.RenderToRgba` returns a raw
`RgbaImage`, and the consumer decides which encoder to wrap it with.

## Naming-convention status

The package names follow a few patterns today:

1. **`Codecs` / `Codecs.Abstractions`** — the facade + its base contract (sniff/dispatch, `IImageDecoder`).
2. **`Png` / `Tiff` / `Jxr`** — names the *format*. Tiff / Jxr / Png are symmetric (encode + decode).
3. **`Jpeg`** — a (decode-only) codec; `Jpeg.IccInjector` and `Jpeg.GainMap` stay separate metadata-domain packages.
4. **`Color.Icc` / `Exif`** — names a *domain* (color management, metadata) rather than a codec.

Milestones:

- ✅ **`SharpAstro.Jpeg` renamed to `SharpAstro.Jpeg.IccInjector`** — the `Jpeg` PackageId is now reserved for a full codec.
- ✅ **Pure-managed JPEG decoder shipped as `SharpAstro.Jpeg`** (baseline + progressive, scaled decode, golden-baseline byte-exact).
- ✅ **Pure-managed baseline JPEG encoder shipped in `SharpAstro.Jpeg`** (3.6) — `JpegEncoder`, a byte-exact `stb_image_write` port (baseline, 4:4:4 / 4:2:0, quality 1..100). `SharpAstro.Jpeg` is now a full codec; Ultra HDR generation no longer needs an external encoder. Progressive/optimized-Huffman/grayscale-only output are the remaining rungs ([`ROADMAP-jpeg-encoder.md`](ROADMAP-jpeg-encoder.md)).
- ✅ **Pure-managed PNG decoder added to `SharpAstro.Png`** (`PngReader.Decode`) — now symmetric with `Tiff` / `Jxr`. Sub-byte depths / indexed-color / Adam7 are deferred follow-ups.
- ✅ **`SharpAstro.Codecs` facade shipped** — one package to sniff + decode (PNG + JPEG) instead of cherry-picking.
- ✅ **Removed `SharpAstro.StbImage`** (the auto-generated stb_image port) — it had no first-party consumers, and the JPEG byte-exact guarantee it anchored is now a committed golden baseline. **Trade-off:** the family no longer decodes the stb-only formats **BMP / TGA / PSD / GIF / HDR** (no clean-room sibling exists for them yet).
- ✅ **Gain-map JPEG (Ultra HDR) shipped as `SharpAstro.Jpeg.GainMap`** (3.5) — an `IccInjector`-style metadata-domain package: read/reconstruct HDR from the SDR base + gain map, assemble/generate on the write side — the publishing tier for HDR masters (one JPEG, correct on SDR browsers, HDR on capable displays). The gain map is the author-shipped answer to the tone-mapping question the facade deliberately refuses to answer itself. Write path verified against Chromium (headless differential check). Remaining rungs — ISO 21496-1 binary metadata, the Apple dialect, the libultrahdr oracle harness: [`ROADMAP-gain-map.md`](ROADMAP-gain-map.md).

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
