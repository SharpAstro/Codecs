# Roadmap: PDF-embedded codecs (JBIG2 / JPX)

**Status: not started — assessment + plan only.** Nothing in this document is
implemented. It exists to record the scoping decisions (especially the *contract*
problem and the *licence* constraints) before any code is written, because both are
easy to get wrong late and expensive to unwind.

Recommended order: **JBIG2 first, JPX deferred.** JBIG2 is a well-scoped, staged
project that also validates the new API shape; JPX is a `SharpAstro.Jxr`-scale
undertaking that should only start once that shape is proven.

## Why these two

They are the last two image filters in the PDF specification that the family cannot
decode. A PDF page's image XObjects arrive through a `/Filter`, and today:

| PDF filter | Format | Family status |
|---|---|---|
| `/DCTDecode` | JPEG | ✅ `SharpAstro.Jpeg` (incl. scaled 1/2–1/8 LOD decode) |
| `/FlateDecode` + predictor | raw samples | ✅ `PngPredictor` exposes the row-unfilter |
| `/CCITTFaxDecode` | Group 3/4 fax | ❌ (see non-goals) |
| **`/JBIG2Decode`** | **JBIG2 (ITU-T T.88)** | ❌ **this document** |
| **`/JPXDecode`** | **JPEG 2000 (ISO/IEC 15444)** | ❌ **this document** |

The motivating consumer is the same one that drove `SharpAstro.Jpeg`'s scaled decode:
drawboard's pdf-viewer. Scanned documents are overwhelmingly JBIG2 (bilevel text) and,
less often, JPX.

## The contract problem — read this before designing the API

This is the single most important constraint, and it does **not** affect both formats
equally.

### JPX fits `IImageDecoder` fine

JPEG 2000 is self-describing and has magic bytes, so it registers in the
`ImageCodecs` facade like every other codec:

- JP2 container signature box: `00 00 00 0C 6A 50 20 20 0D 0A 87 0A`
- Raw codestream (SOC + SIZ): `FF 4F FF 51`

### JBIG2 does not

PDF embeds the **"embedded stream" organization** of T.88, which is *not* a file:

- **No file header.** The standalone `.jb2` magic `97 4A 42 32 0D 0A 1A 0A` is absent,
  so `IImageDecoder.CanDecode(header)` has nothing to match against.
- **Out-of-band data.** Segment dictionaries live in a *separate* PDF stream referenced
  by `/DecodeParms /JBIG2Globals`. `TryDecode(data)` has nowhere to put them.
- **Out-of-band geometry.** Width/height/`BitsPerComponent` come from the image
  dictionary, not the codestream.

So JBIG2 needs an explicit entry point next to (not instead of) the interface:

```csharp
// The PDF-shaped API — the primary one.
Jbig2Decoder.Decode(
    ReadOnlySpan<byte> embedded,
    ReadOnlySpan<byte> globals,   // empty when the PDF has no /JBIG2Globals
    int width,
    int height);
```

`IImageDecoder` registration stays a *courtesy* for standalone `.jb2` files. This is
the `Jpeg.IccInjector` / `Jpeg.GainMap` precedent: a package that is useful without
being purely facade-driven.

Two consequences worth fixing in the design rather than discovering later:

- **Bilevel has no `SampleFormat`.** There is no 1-bit member, and adding one would
  ripple through every codec's `Pixels` stride contract. Expand to `UInt8` gray
  (0 / 255) on output — what pdf.js does, and what every consumer wants anyway.
- **Polarity must be documented and fixed.** JBIG2's convention is **1 = black**.
  PDF's `/Decode [1 0]` and `ImageMask` semantics invert that conditionally — but
  that is *PDF-layer* policy, not codec policy. The codec emits one documented
  polarity and refuses to guess; the same "we don't apply consumer policy" line the
  facade already draws for HDR tone mapping.

## Licence constraints (decided up front)

This repo is **Unlicense (public domain)**. That is stricter than "permissive" — code
carrying an attribution/notice requirement *cannot* be relicensed into it.

| Reference | Licence | Usable as **port source**? | Usable as **oracle binary**? |
|---|---|---|---|
| `jbig2dec` (Artifex) | **AGPL** | ❌ **absolutely not** | ✅ yes — running a binary is not linking |
| `jbig2enc` | Apache-2.0 | ❌ notice retention | ✅ yes — and it generates fixtures |
| pdf.js JBIG2 | Apache-2.0 | ❌ notice retention | ✅ yes (via node) |
| PDFium | BSD-3 | ❌ notice retention | ✅ yes |
| OpenJPEG | BSD-2 | ❌ notice retention | ✅ **ideal** — `opj_compress` / `opj_decompress` |
| ITU-T T.88 / T.800 specs | — | ✅ **clean-room from spec** | — |

**Therefore: clean-room from the ITU-T specifications**, with permissively-licensed
*binaries* as oracles. This is exactly the discipline already used for `SharpAstro.Jpeg`
(clean-room decoder, stb as byte-exact oracle) and `SharpAstro.Jxl` (spec-as-judge +
libjxl as empirical oracle) — see the oracle-harness table in `CLAUDE.md`.

Patent position is clear: the MQ-coder patents (IBM / Mitsubishi, early-1990s filings)
have long expired.

## Shared component: the MQ arithmetic coder

T.88 Annex E and T.800 Annex C specify the **same** MQ arithmetic coder with the
**same `Qe` table**. Build it once as internal shared source rather than twice — but do
not over-engineer it into a public package on day one; it is an implementation detail
until there are two real consumers.

## JBIG2 — the plan

Scale, for calibration against the existing packages (`SharpAstro.Exif` is 558 LOC,
`SharpAstro.Png` 1554, `SharpAstro.Jxr` 10949):

| Rung | Scope | Est. LOC | Why this order |
|---|---|---|---|
| 1 | MQ decoder + **generic region** (templates 0–3, TPGDON) | ~800–1200 | Smallest slice that decodes real PDFs. Also builds the component JPX needs. |
| 2 | **MMR** variant of generic region (T.6 / Group 4 coding) | ~400 | Shares the region plumbing; incidentally unlocks `/CCITTFaxDecode` later. |
| 3 | **Symbol dictionary + text region** | ~1200–1500 | Where JBIG2's real compression on scanned text lives. The rung that makes it genuinely useful. |
| 4 | **Generic refinement region** | ~400 | Needed by lossy-refine encoders. |
| 5 | **Pattern dictionary + halftone region** | ~500 | Rare in practice. Last. |

Rung 1 alone already covers a meaningful share of real-world PDFs.

### Validation plan

Mirrors the three-layer discipline documented in `CLAUDE.md` for JXR:

1. **Golden-vector component tests** — the T.88 specification ships worked examples
   (Annex H test sequences); bake those in as unit tests. Spec-derived, so no licence
   contamination.
2. **Self round-trip** is *not* available (decode-only) — so this layer is replaced by
   property tests on synthetic bitstreams, the `LosslessJpegTests` pattern.
3. **Oracle pixel-match** — decode the same stream with `jbig2dec` and compare rasters
   exactly. Bilevel output means **exact match, no tolerance** — a much sharper oracle
   than the tolerance-based ones the lossy codecs need.

Fixtures come from `jbig2enc` (encode known bitmaps) plus real scanned PDFs. Follow the
established harness conventions: oracle binaries git-ignored under `Oracle/bin/`, a
`build.sh` that clang-builds them, and **graceful skip** when absent.

## JPX — the plan (deferred)

Feasible, but be honest about the size. A decode-only implementation needs:

- **EBCOT tier-1** — bit-plane coding, three passes per plane, context modelling
- **EBCOT tier-2** — packet headers, tag trees, five progression orders
  (LRCP/RLCP/RPCL/PCRL/CPRL), precincts, quality layers
- **DWT** — 5/3 reversible + 9/7 irreversible, N decomposition levels
- Tiles, RCT/ICT component transforms, subsampling, ROI
- The full marker set (SIZ/COD/QCD/COC/QCC/POC/TLM/PLT/…)
- The **JP2 box container** — `colr`, `pclr` (palette), `cdef` (channel definition)
- **PDF-specific rules** — `SMaskInData`, and the notorious "JPX may override the
  PDF colorspace" behaviour

Realistically **8–15k LOC decode-only**, i.e. `SharpAstro.Jxr` (10.9k) territory or
larger. An encoder is a separate mountain and is explicitly not planned.

Two things genuinely favour it when the time comes:

1. **`opj_decompress` / `opj_compress` are BSD-2** and clang-buildable — ideal oracle
   binaries, and they slot straight into the existing `Oracle/build.sh` pattern.
2. **Resolution levels give free 1/2ⁿ decode.** This is strictly better than the
   reduced-IDCT trick that motivated `SharpAstro.Jpeg`'s scaled path — the LOD comes
   out of the codestream structure rather than being bolted onto the transform. For
   the pdf-viewer use case that is the single most attractive property of the format.

**Gate:** do not start JPX until JBIG2 has shipped and the PDF-embedded-stream API
shape has survived contact with a real consumer.

## Non-goals

- **Encoding** either format. Nothing in the family or its consumers needs to *produce*
  JBIG2 or JPEG 2000.
- **A PDF parser.** These packages decode image streams handed to them. Object
  resolution, filter chains, `/Decode` arrays, and `ImageMask` polarity stay in the
  consumer's PDF layer.
- **`/CCITTFaxDecode` as a separate package** — if rung 2 lands, the MMR decoder can be
  exposed for it, but it is not a driver on its own.
- Arithmetic-coded JPEG, still out of scope (unrelated to JBIG2's MQ coder despite both
  being "arithmetic JPEG-family coding").
