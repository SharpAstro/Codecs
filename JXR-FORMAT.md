# JPEG XR format support (`SharpAstro.Jxr`)

JPEG XR (ITU-T **T.832** / ISO-IEC 29199-2, originally Microsoft *HD Photo*) has a
reputation for being confusing. It isn't really — it just stacks **four independent
axes**, and people conflate them. This document lays out each axis and ticks off what
`SharpAstro.Jxr` supports.

> **Status legend:** ✅ supported & validated · 🚧 in progress · ⬜ not yet / out of scope · 📖 recognised/parsed only (the on-disk tag is defined so foreign files are identified, but pixels aren't yet round-tripped).
>
> Support below tracks the codec on the **active development line**. The narrower
> subset currently **shipped to NuGet** is summarised in [`CODECS.md`](CODECS.md)
> (single-tile, YUV444 / Y-only).

## The one idea that un-confuses JXR: external ≠ internal

JXR cleanly separates two things people mash together:

- **Pixel format** — how samples are laid out *on disk* / handed to the codec. A 16-byte
  GUID (T.832 Annex A, Table A.6). There are **~64** of these.
- **Internal colour format** — what the codec *transforms to* and entropy-codes. Only **8**.

The encoder maps external → internal (e.g. `Rgb24` on disk → `YUV444` internally, via a
reversible YCoCg-R colour transform), codes that, and the decoder reverses it. So the
huge GUID list isn't 64 codecs — it's 64 ways to describe sample layout that collapse
onto a handful of internal representations.

A `.jxr` file is therefore one point in:

```
(Axis 1: sample type  ×  Axis 2: channel layout)   →  on-disk PIXEL_FORMAT GUID
                       ↓  (colour transform + optional chroma subsample)
 Axis 3: internal colour format
                       ×
 Axis 4: compression structure (ordering / tiles / overlap / bands / quant)
```

Each axis is simple on its own; the product is what looks intimidating.

---

## Axis 1 — Sample numeric type ("bit depth" / BD code)

How a single number is stored. Orthogonal to colour.

| BD code | Meaning | Support |
|---|---|:---:|
| BD8 | 8-bit unsigned int | ✅ |
| BD16 | 16-bit unsigned int | ✅ |
| BD16F | 16-bit half-float | ✅ |
| BD32F | 32-bit float | ✅ (mono only — see note) |
| BD16S | 16-bit **signed** int | ⬜ (queued — native signed FITS) |
| BD32S | 32-bit signed int | ⬜ (queued) |
| BD1 (black/white) | 1 bit | ⬜ |
| BD5 / BD10 / BD565 | packed 5 / 10 / 5-6-5 bit | ⬜ |

> **BD32F is mono-only by design:** T.832 Table A.6 defines no GUID for 32-bit-float
> *RGB*, so float HDR colour goes through BD16F (half) instead.

## Axis 2 — Channel layout

| Layout | Channels | Support |
|---|---|:---:|
| Grayscale | 1 | ✅ |
| RGB | 3 | ✅ |
| RGBA (+ alpha plane) | 4 | ⬜ |
| CMYK | 4 | ⬜ |
| CMYK + alpha | 5 | ⬜ |
| N-channel | up to 8+ | ⬜ |
| RGBE (radiance HDR, shared exponent) | 4 (3+E) | ⬜ |
| Pre-subsampled YCC | 3 | ⬜ |

## On-disk PIXEL_FORMAT GUIDs (Axis 1 × Axis 2)

Every sensible (sample-type × channel-layout) pairing gets a GUID — that's why there
are so many. `SharpAstro.Jxr` **defines all ~64** (`JxrPixelFormat`, so foreign files are
recognised), but only the grayscale + RGB subset across our four bit depths is actually
**wired to encode/decode**:

| Group | Examples | Support |
|---|---|:---:|
| **Grayscale** | `Gray8`, `Gray16`, `GrayHalf16` (BD16F), `GrayFloat32` (BD32F) | ✅ |
| **RGB** | `Rgb24` (BD8), `Rgb48` (BD16), `RgbHalf48` (BD16F) | ✅ |
| Grayscale fixed-point | `GrayFixedPoint16/32`, `BlackWhite` | 📖 |
| RGB(A) packed / 32bpp | `Bgr24`, `Bgr32`, `Bgra32`, `Pbgra32`, `Bgr555/565`, `Bgr101010` | 📖 |
| RGB(A) deep / HDR | `Rgba64`, `RgbaHalf64`, `RgbFloat128`, `RgbaFloat128`, `…FixedPoint…` (scRGB) | 📖 |
| CMYK | `Cmyk32/64`, `CmykAlpha40/80`, `CmykDirect…` | 📖 |
| N-channel | `Channels3..8` (+ `Alpha` variants) | 📖 |
| HDR special | `Rgbe32` (radiance) | 📖 |
| Pre-subsampled YCC | `Ycc420/422/444…` (chroma already subsampled on disk) | 📖 |

## Axis 3 — Internal colour format + chroma subsampling

What the codec actually encodes. **Chroma subsampling (4:2:0 / 4:2:2 / 4:4:4) lives
entirely on this axis** — independent of your on-disk bit depth or channel layout.

| Internal format | Used for | Support |
|---|---|:---:|
| `YOnly` | grayscale | ✅ |
| `YUV444` | colour, full chroma (4:4:4) | ✅ |
| `YUV422` | colour, 4:2:2 (½ chroma) | ✅ encode + decode (OL_NONE / OL_ONE / OL_TWO), lossless |
| `YUV420` | colour, 4:2:0 (¼ chroma) | ✅ encode + decode (OL_NONE / OL_ONE / OL_TWO), lossless |
| `YUVK` | CMYK | ⬜ |
| `NComponent` | arbitrary N channels, no colour transform | ⬜ |
| `Rgb` | RGB without the YCoCg transform | ⬜ |
| `Rgbe` | radiance | ⬜ |

> RGB input is converted with the reversible **YCoCg-R** transform to `YUV444` (the
> internal format Windows Photo / WIC expects), then optionally chroma-subsampled to
> 4:2:2 / 4:2:0.

## Axis 4 — Compression structure (orthogonal to colour entirely)

| Knob | Options | Support |
|---|---|:---:|
| **Frequency ordering** | SPATIAL | ✅ |
| | FREQUENCY | ⬜ |
| **Overlap (POT)** | OL_NONE / OL_ONE / OL_TWO | ✅ |
| **Tiling** | single-tile | ✅ |
| | multi-tile, **soft** (+ `INDEX_TABLE`) | ✅ (RGB) |
| | multi-tile, **hard** (overlap stops at tile edge) | ⬜ |
| **Bands present** | all bands | ✅ |
| | `NO_FLEXBITS` / `NO_HIGHPASS` / `DC_ONLY` (progressive truncation) | ⬜ (`NoFlexbits` queued) |
| **Quantization** | lossless (QP 0) | ✅ |
| | uniform lossy QP | ✅ |
| | non-uniform / per-band-reuse QP | ⬜ |
| **Dimensions** | arbitrary, non-16-aligned (pad-then-crop) | ✅ |
| | hard `WINDOWING_FLAG` | ⬜ |
| **Alpha** | separate alpha plane | ⬜ |
| **Container** | full `.jxr` TIFF-like file (IFD + pixel-format GUID + codestream) | ✅ |

---

## Worked example: "a 16-bit RGB photo saved by a Windows app"

| Layer | Value |
|---|---|
| On-disk PIXEL_FORMAT | `Rgb48` GUID (Axis 1 = BD16, Axis 2 = RGB) |
| Internal colour format | `YUV444` (or `YUV420` if the app subsampled) — Axis 3 |
| Structure | SPATIAL, soft-tiled, overlap OL_ONE, all bands, lossy QP — Axis 4 |

`SharpAstro.Jxr` round-trips this today whether it's 4:4:4 or **4:2:0 / 4:2:2 at any overlap
level (OL_NONE / OL_ONE / OL_TWO)** — decode bit-exact vs `JxrDecApp`, encode byte-for-byte
identical to `JxrEncApp` (5-tap `[1,4,6,4,1]/16` chroma downsample; jxrlib runs all subsampled
chroma in scaled-arithmetic mode, even at QP 1). Lossy subsampled chroma (the per-band UV-shift
quantizer) is the one remaining chroma item.

## Validation discipline

Everything ticked ✅ is checked **bit-exact against the jxrlib reference binaries**, not
just for self-consistency: our codestream is byte-matched against `JxrEncApp`, and both
decode directions are verified against `JxrEncApp` / `JxrDecApp`. See the "JXR codec"
section of [`CLAUDE.md`](CLAUDE.md) for the three validation layers.

## The short version

The **spec** is enormous (~64 pixel formats × float / signed / HDR sample types ×
4:2:0 / 4:2:2 / 4:4:4 × spatial / frequency × tiles × band subsets). `SharpAstro.Jxr`
supports the **practical photographic + scientific core**: 8/16-bit and HDR-float,
grayscale + RGB, full-chroma, lossless + lossy, overlap, single + multi-tile — the
astro-imaging contract plus the most common real-world files, validated bit-exact.
