# Roadmap: gain-map JPEG (Ultra HDR / ISO 21496-1)

**Status: core shipped in 3.5, facade-integrated in 3.6** — milestones 1–3, 5–6
and 8 landed (read + reconstruct + assemble + generate as
`SharpAstro.Jpeg.GainMap`, now decoded transparently through the `SharpAstro.Codecs`
facade), verified against Chromium via a headless differential check: the assembled
file renders identically to the base under an SDR colour profile and diverges under
an HDR one. Milestone 4 (the libultrahdr oracle harness + committed golden vectors)
and milestone 7 (ISO 21496-1 / Apple dialects) remain open. This document keeps
the original design + validation plan for those remaining rungs.

**The core is its own package (COD70, Codecs #70).** The metadata, the per-channel parameters,
the reconstruction math and the sRGB transfer functions now live in **`SharpAstro.GainMap`**, which
depends only on `SharpAstro.Codecs.Abstractions`, so the HEIF carrier (`SharpAstro.Heif`: ISO 21496-1
`tmap` items and Apple auxiliary maps) can use them without pulling in JPEG.
`SharpAstro.Jpeg.GainMap` references it and forwards `GainMapMetadata` by full name (`TypeForwardedTo`), so
its public API is unchanged and `GainMapImage.ReconstructHdr` is byte-identical. What the core adds:
per-channel min/max/gamma/offsets (`GainMapChannel`; the scalar metadata is the one-channel case), a
reconstruction entry point over a base given as **linear floats** with the map as normalised codes of any bit
depth (so a 10-bit HEIF base works), the `UseBaseColourSpace` flag recorded without converting, and
`Iso21496Metadata.Parse`, a clean-room reader for the ISO/TS 21496-1 binary metadata structure (version
fields, both fraction encodings, 1 or 3 channels, backward direction; `InvalidDataException` for truncated,
zero-denominator, unknown-minimum-version and non-finite or out-of-range payloads). The writer is test-only
(`Iso21496MetadataTests`). Its layout is pinned by hand-written byte vectors, but no third-party ISO payload
is committed yet: validating against libultrahdr/Android output is part of milestone 4 and 7.

## Why this feature

The 3.5 facade enforces a deliberate contract: HDR content (float samples, PQ/HLG-tagged
integers) **refuses** the 8-bit display path (`TryDecodeIntoRgba8`), because projecting
HDR to SDR needs a tone/stretch policy and that policy is a consumer decision, not a
codec one.

A gain-map JPEG is the *author's answer to exactly that question, shipped with the
image*. The file carries:

- an ordinary baseline **SDR JPEG** — the authored tone-mapped rendition, correct on
  every legacy viewer with zero policy; and
- a second, small **gain-map JPEG** — an invertible, per-pixel record of what the tone
  mapping removed, plus scalar metadata describing how to undo it.

That unlocks three things nothing in the family can do today:

1. **HDR recoverable from an 8-bit file.** Reconstruct a `Float32` linear raster
   (display-referred, known headroom) from a plain JPEG.
2. **Display-adaptive rendering.** Continuously interpolate between the SDR rendition
   and full HDR to match the *actual* display's headroom — a dial, not a binary switch.
   This is the capability that made the format win (Android 14 Ultra HDR, Adobe
   Camera Raw, Apple Photos).
3. **The publishing tier for HDR masters.** The astro pipeline's BD32F JXR / EXR
   masters currently cannot be shared displayably. HDR master → tone-map once at
   export → SDR base + gain map → **one JPEG** that renders correctly on every SDR
   browser *and* reconstructs HDR on capable displays.

Both embedded images are plain baseline JPEGs — `SharpAstro.Jpeg` already decodes them.
No new entropy coding is required anywhere; this feature is metadata plumbing plus
~30 lines of reconstruction math.

## The format family (dialects)

| Dialect | Metadata carrier | Status | Our target |
|---|---|---|---|
| **Adobe gain map 1.0** (2023) | XMP, `hdrgm:` namespace (`http://ns.adobe.com/hdr-gain-map/1.0/`) on the gain-map image; MPF locates it | de-facto standard; what ACR/Lightroom emit | **read + write** |
| **Android Ultra HDR v1** | same `hdrgm` XMP + `GContainer` XMP directory on the primary; MPF APP2 | what Android 14+ cameras emit; libultrahdr is the reference | **read + write** (superset of Adobe handling) |
| **ISO/TS 21496-1** (2024) | binary metadata payload in an APP2 segment tagged with the ISO URN | the standardized unification; Android 15 writes ISO + XMP side by side | **read** first, write later |
| Apple HDR gain map | maker-note-based, headroom in `MakerApple`; different math | proprietary, widely present in iPhone photos | **read-only, best-effort, last** |

File anatomy (Adobe/Android form): primary JPEG (SDR base, optionally with `GContainer`
XMP + MPF APP2 pointing past its `EOI`) → immediately followed by the gain-map JPEG
(usually ¼ resolution, gray or RGB) whose XMP carries the `hdrgm` parameters.

## The math (Adobe hdrgm 1.0 / ISO 21496-1)

Scalar metadata: `GainMapMin`, `GainMapMax` (log2 stops), `Gamma`, `OffsetSDR`,
`OffsetHDR`, `HDRCapacityMin`, `HDRCapacityMax` (log2 headroom bounds),
`BaseRenditionIsHDR` (direction flag; we target the SDR-base form).

Reconstruction, per pixel (per channel if the map is RGB):

```
recovery     = gainmap_code / max_code                      # [0,1], after upsampling the ¼-scale map
logRecovery  = lerp(GainMapMin, GainMapMax, recovery^(1/Gamma))   # log2 stops
W            = clamp((log2(H) - log2(HDRCapacityMin))
                   / (log2(HDRCapacityMax) - log2(HDRCapacityMin)), 0, 1)
                                                            # H = target display headroom (1.0 = SDR)
HDR_linear   = (SDR_linear + OffsetSDR) · 2^(logRecovery · W) - OffsetHDR
```

where `SDR_linear` is the base image after the sRGB EOTF (the base is ordinary
display-referred sRGB). Boundary properties that become the spine of the property
tests: `W = 0` reproduces the base exactly; `W = 1` is the full authored HDR;
reconstruction is monotone in `W`.

The generation direction (`Compute`) is the inverse: given aligned HDR-linear and
SDR renditions, `logRecovery = log2((HDR + OffsetHDR) / (SDR + OffsetSDR))`, normalize
to `[GainMapMin, GainMapMax]`, gamma-encode, downsample, encode as JPEG.

## Where it lands in the package family

Follows the `SharpAstro.Jpeg.IccInjector` precedent: a **metadata-domain package**, not
a codec — the JPEGs inside are decoded/encoded by whatever codec the caller has.

- **`SharpAstro.Jpeg`** (prerequisite): surface APP segments from the decoder. This is
  the already-flagged gap ("does not yet surface APP2 ICC / EXIF blobs") — fixing it
  unlocks EXIF pairing and ICC extraction too, independent of gain maps.
- **`SharpAstro.Exif`** (or the new package): **MPF reader** — APP2 `MPF\0` is an
  Exif-style IFD; the existing IFD plumbing applies. Plus a **minimal XMP parse** for
  the `hdrgm`/`GContainer` subset (targeted string/XML scanning, not a full XMP engine —
  AOT stays trivial).
- **`SharpAstro.Jpeg.GainMap`** (new): the feature itself —

```csharp
public static class JpegGainMap
{
    /// Detect + split a gain-map JPEG: base rendition, gain map, metadata.
    public static bool TryRead(ReadOnlySpan<byte> jpeg, out GainMapImage image);

    /// Splice MPF + XMP + the gain-map JPEG into any encoder's baseline output
    /// (the IccInjector pattern - no JPEG encoder required).
    public static byte[] Assemble(ReadOnlySpan<byte> sdrBaseJpeg,
                                  ReadOnlySpan<byte> gainMapJpeg,
                                  GainMapMetadata meta);

    /// Generation: aligned HDR-linear + SDR renditions in, gain-map pixels +
    /// fitted metadata out. The astro publishing tier.
    public static (byte[] gainMapGray8, GainMapMetadata meta) Compute(
        ReadOnlySpan<float> hdrLinearRgb, ReadOnlySpan<byte> sdrRgb8,
        int width, int height);
}

public sealed class GainMapImage
{
    public RasterImage Base { get; }        // SDR, ColorEncoding.AssumedSrgb
    public RasterImage GainMap { get; }     // gray/RGB, typically quarter scale
    public GainMapMetadata Metadata { get; }

    /// Float32 linear RGB, ColorEncoding = Linear + DisplayReferred.
    /// displayHeadroom 1.0 → the base exactly; Metadata.HdrCapacityMax → full HDR.
    public RasterImage ReconstructHdr(double displayHeadroom);
}
```

- **`SharpAstro.Codecs.Abstractions`**: **no change.** `ColorEncoding` stays pure
  H.273 + `FloatSemantics`; headroom and gain-map parameters live on
  `GainMapMetadata`, not on `IDecodedImage`.
- **`SharpAstro.Codecs`** (facade): **now gain-map aware** (revised from the original
  "SDR base only" plan). It references `SharpAstro.Jpeg.GainMap` and registers a
  `GainMapImageDecoder` ahead of the plain JPEG decoder, split by tier:
  `TryDecode` + `ToFloats()` reconstruct the authored HDR (linear, display-referred,
  at `HdrCapacityMax`), while `TryDecodeIntoRgba8` / `ToRgba8` keep returning the SDR
  base (the authored display rendition, and the graceful fallback a gain-map-unaware
  viewer shows). So "ask the facade for floats" transparently yields HDR; a display
  headroom other than the full authored one is reached through the returned
  `GainMapDecodedImage.Source`. Plain JPEGs are untouched (the gain-map decoder's
  `CanDecode` only claims JPEGs that carry a locatable gain map).

## Validation discipline

Same philosophy as the JXR port: an external reference oracle, not just
self-consistency.

1. **Oracle: [libultrahdr](https://github.com/google/libultrahdr)** (Google's
   reference implementation, what Android ships). Build its `ultrahdr_app` CLI via an
   `Oracle/`-style `build.sh` (note: needs CMake, unlike the clang-only JXR oracle);
   binaries git-ignored; tests skip gracefully when absent.
   - *Their* encode → *our* `TryRead`/`ReconstructHdr`: reconstruction matches their
     decode within a stated float tolerance (their pipeline has fixed-point stages, so
     bit-exactness is not the bar — document the tolerance and pin it).
   - *Our* `Assemble`/`Compute` → *their* decode: accepted and reconstructs.
2. **Committed golden vectors**: real ACR-/Pixel-produced samples (small crops) +
   digest baselines, the `jpeg-oracle-golden.tsv` pattern, so CI validates without the
   oracle binaries.
3. **Property tests** (no oracle needed): `W=0` ⇒ base exactly; `W=1` ⇒ capacity max;
   monotone in `W`; `Compute → Assemble → TryRead → ReconstructHdr` round-trips the
   HDR input within the gain map's 8-bit quantization bound.
4. **Metadata round-trip**: `Assemble → TryRead` preserves `GainMapMetadata` exactly;
   MPF offsets remain valid after splicing (the IccInjector offset-fixup lesson).

## Milestones (rung by rung)

1. ✅ `SharpAstro.Jpeg`: `JpegSegmentScanner` — byte-level marker-segment
   enumeration + APPn payload discovery (the ICC/EXIF gap in the Jpeg row of
   `CODECS.md` is now byte-level closed; decoded-property surfacing can follow).
2. ✅ MPF reader/writer (`MpfSegment`) + targeted `hdrgm`/`GContainer` XMP
   (`GainMapXmp`). *Deviation from plan:* these landed inside
   `SharpAstro.Jpeg.GainMap` rather than `SharpAstro.Exif` — they sit next to
   their only consumer, and `Exif` keeps its dependency profile unchanged.
3. ✅ `SharpAstro.Jpeg.GainMap` read path: `TryRead`/`TrySplit` +
   `ReconstructHdr` + the property-test spine (W=0 ⇒ base exactly, W=1 ⇒
   capacity max, monotone in W).
4. ⬜ Oracle validation vs libultrahdr (both directions) + committed golden
   vectors. *Interim substitute already in place:* Skia-parser-mirroring
   structural tests (MPF offset math, Item:Length, required hdrgm fields) plus
   a live headless-Chromium differential check of the write path.
5. ✅ Write path: `Assemble` (splice; works with any encoder's baseline JPEGs —
   emits GContainer XMP **and** MPF, the Ultra HDR v1 superset).
6. ✅ `Compute`: gain-map generation from an HDR/SDR pair — the astro publishing
   tier. Ships without a bundled tone map (the SDR rendition is the caller's
   policy); a labelled Reinhard convenience remains an option for later.
7. ⬜ ISO 21496-1 binary metadata (read alongside XMP; write once Android 15-era
   readers are the baseline). Apple dialect read-only, best-effort, last.
8. ✅ Facade integration (`SharpAstro.Codecs`): `GainMapImageDecoder` +
   `GainMapDecodedImage` — `ImageCodecs.TryDecode(...).ToFloats()` reconstructs HDR,
   the 8-bit path stays the SDR base. Transparent for consumers referencing only the
   `Codecs` facade; plain JPEGs unaffected (`GainMapFacadeTests`).

## Apple's HDR gain map in HEIC (read-only, best-effort)

`SharpAstro.Heif` reads Apple's older carrier when a file has no `tmap`: an auxiliary image item with an `auxl`
reference to the image and an `auxC` property of `urn:com:apple:photo:2020:aux:hdrgainmap`
(`HeifFile.GainMapOf`, dialect `HeifGainMapDialect.AppleAux`; a file with both carriers gives the `tmap`).
Alpha (`urn:mpeg:hevc:2015:auxid:1`) and depth (`:2`) auxiliary items and other vendors' URNs are not gain maps and are
ignored. Every constant below is from **Apple's public article**, "Applying Apple HDR effect to your photos"
(`developer.apple.com/documentation/appkit/applying-apple-hdr-effect-to-your-photos`); nothing is transcribed from
libheif, libultrahdr or exiftool.

- **The map**: 8-bit, single-channel, a quarter of the image's resolution, "untagged and formatted as linear data ...
  encoded using the Rec.709 transfer function": so its codes are divided by 255, resized to the image, and linearised by
  inverting BT.709 (`Rec709Transfer.ToLinear` in the core).
- **Headroom** (a linear ratio to SDR white), from the image's Exif `MakerNote` keys 33 and 48 (32-bit floats):
  `maker33 < 1`: `stops = maker48 <= 0.01 ? -20*maker48 + 1.8 : -0.101*maker48 + 1.601`; otherwise
  `stops = maker48 <= 0.01 ? -70*maker48 + 3.0 : -0.303*maker48 + 2.303`; `headroom = 2^max(stops, 0)`
  (`HeifAppleGainMap.HeadroomFromStops`). The article also names an `HDRGainMapVersion` metadata key as the validity check;
  it is **not required** here (a map with a headroom is reconstructed).
- **Where the keys are**: Exif `MakerNote` (0x927C): `Apple iOS `, a 2-byte version, a byte-order mark, an IFD at offset 14,
  value offsets counted from the start of the note. The article says "32-bit float"; the raw Exif type is not stated, so
  RATIONAL, SRATIONAL, FLOAT and DOUBLE are all read. As a fallback the aux item's XMP `hdrgm:HDRCapacityMax` (log2 of the
  headroom, the Adobe vocabulary) is accepted; the MakerNote wins when both are present.
- **The formula**, on linear values: `hdr_rgb = sdr_rgb * (1.0 + (headroom - 1.0) * gainmap)`, the result peaking at the
  headroom. It is *linear* in the map, which the ISO/Adobe form (`2^(log-gain)`) is not, so it needs its own core entry
  point (`GainMapReconstructor.ReconstructAppleFromLinear`), not a metadata mapping onto the existing one.
- **Not in the article, chosen here**: a display with less headroom than the image uses `H' = clamp(display, 1, headroom)`
  in place of the headroom (display 1 gives the base exactly, display at or above the authored the full effect);
  `HeifGainMapImage.Weight` is `(H' - 1) / (headroom - 1)`. `HeifGainMapInfo.Metadata` for the Apple dialect is a summary
  (gain range 1 to the headroom, no offsets).
- **Missing or absurd headroom**: no headroom anywhere, or one of 1 or less (no HDR effect): not a gain map (`GainMapOf` is
  null, the item is its SDR picture). Not finite or over 256 (8 stops; phones write under 4): `InvalidDataException`.
- **Caveat**: checked against files built to the article's description (x265 pictures, hand-built Exif), not against an
  iPhone file; none is in the repository. Whether Apple's own renderers agree with the article is not established (a
  third-party report says macOS Photos may not).

## Non-goals / caveats

- **Not a substitute for scene-referred stretch.** Gain maps record a *display-referred
  photographic* tone mapping. Astro masters still need the consumer's
  percentile/asinh stretch to produce the SDR rendition in the first place — `Compute`
  consumes that result; it does not invent it.
- **HEIC carriers** of the same gain-map payload are no longer out of scope: `SharpAstro.Heif` reads the
  container, and the shared core is `SharpAstro.GainMap` (see the status note above). The HEIF-side wiring
  is Codecs #70's later legs. **AVIF** remains out of scope (no AV1 decoder in the family).
- **Full XMP engine**: out of scope — targeted parsing of the two namespaces only.
- **JPEG XT** (ISO 18477 layered HDR JPEG): effectively undeployed; not pursued.
