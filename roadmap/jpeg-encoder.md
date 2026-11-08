# Roadmap: JPEG encoder (`SharpAstro.Jpeg`, the write half)

**Status: baseline shipped in 3.6** — milestones 1–2 landed as
`SharpAstro.Jpeg.JpegEncoder`: a byte-exact `stb_image_write` port (baseline
sequential, 4:4:4 / 4:2:0, quality 1..100, channels 1..4), validated
byte-for-byte against the pinned reference (`Oracle/jpegenc`) and frozen as a
committed golden digest, with the gain-map suite dogfooding it end to end
(Compute → JpegEncoder → Assemble → Chromium-verified HDR). `SharpAstro.Jpeg` is
now a full codec. Milestones 3–6 (grayscale-only output, optimized Huffman,
convenience tiers, size/speed numbers) remain open; this document keeps the plan
for them.

## Why this feature

The family decodes JPEG but cannot produce one, which leaves three visible gaps:

1. **Gain-map JPEG (Ultra HDR) is not self-contained.** `JpegGainMap.Compute` /
   `Assemble` shipped in 3.5, but the SDR base and the gain map must be encoded
   by an *external* JPEG encoder before `Assemble` can splice them. With an
   in-family encoder, HDR-master → Ultra HDR `.jpg` becomes one dependency-free
   call chain — the full astro publishing tier.
2. **`SharpAstro.Tiff`'s JPEG compression is passthrough-only.**
   `JpegPassthroughSource` writes pre-compressed strips verbatim; it cannot
   compress pixels. An encoder turns TIFF/JPEG into a real write path.
3. **Symmetry.** PNG, TIFF, JXR, EXR, JXL all encode + decode; JPEG is the only
   asymmetric codec left in the matrix.

## Port source and license (decided up front)

**Faithful hand-port of the JPEG core of [`stb_image_write.h`](https://github.com/nothings/stb/blob/master/stb_image_write.h)**
(`stbi_write_jpg_core` + helpers, ~400 lines of C) — dual-licensed MIT / public
domain, i.e. Unlicense-compatible, unlike libjpeg (IJG license, the same reason
`jidctred.c` was never ported for scaled decode.

This mirrors how the decoder was built (faithful port of stb_image, validated
byte-exact, oracle then frozen). Note for the record: the pre-3.4 auto-generated
`StbImageSharp` port that used to live in this repo was **decode-only** — no
write half exists anywhere in git history — so this is a fresh port, not a
resurrection. The old `generation/` C-to-C# generator will not be revived; the
JPEG write core is small enough that a hand-written, idiomatic port (matching
`JpegCore`'s style) is both nicer and easier to verify.

Verified feature set of the reference (pinned from upstream source, 2026-07):

- Baseline sequential DCT only; no progressive, no restart intervals.
- Fixed Annex K Huffman tables (`YDC/YAC/UVDC/UVAC`), never optimized.
- Quality 1–100 → Annex K quantization tables scaled libjpeg-style
  (`q < 50 ? 5000/q : 200 − 2q`, then `(QT[i]*scale + 50)/100`).
- Subsampling picked from quality: **4:2:0 when `quality <= 90`, else 4:4:4**.
- Always writes 3-component YCbCr (comp 1/2 inputs are replicated; alpha ignored).
- Float forward DCT (AAN-style, scale factors folded into the quant tables),
  round-half-away-from-zero quantization.
- Edge replication for non-multiple-of-8/16 dimensions.
- Segment order: SOI · APP0 JFIF v1.1 · DQT×2 · SOF0 · DHT×4 · SOS · scan · EOI
  (APP0-first means `JpegIccInjector` / `JpegGainMap.Assemble` insertion points
  work unchanged on our own output).

## Where it lands

**Inside `SharpAstro.Jpeg`** — no new package. When it ships: update the
`Jpeg.IccInjector` `<Description>` ("NOT a JPEG codec … reserved for a future
full encoder/decoder") and the CODECS.md matrix row / naming-milestones list.

```csharp
public static class JpegEncoder
{
    /// pixels: interleaved 8-bit, channels ∈ {1 gray, 2 gray+alpha, 3 RGB, 4 RGBA};
    /// alpha is ignored (JPEG has none). Deterministic: same input+options ⇒ same bytes.
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height,
                                int channels, JpegEncodeOptions? options = null);
}

public sealed record JpegEncodeOptions
{
    public int Quality { get; init; } = 90;                 // libjpeg-style 1..100
    public JpegSubsampling Subsampling { get; init; }       // Auto (stbiw rule) | Chroma444 | Chroma420
        = JpegSubsampling.Auto;
    public bool ForceColor { get; init; }                   // M3: gray input → gray JPEG unless forced
    public bool OptimizeHuffman { get; init; }              // M4: two-pass canonical tables
}
```

`Stream`/`IBufferWriter<byte>` overloads and an encode-time APPn hook are
deliberately deferred — splicers already cover metadata, and `byte[]` matches
every other codec façade in the family.

## Validation discipline

Same philosophy as JXR and the decoder: an external reference oracle, byte-exact
where the reference is deterministic, frozen goldens so CI needs no oracle.

1. **Byte-exact vs native stb_image_write.** An `Oracle/`-style single-file CLI
   (clang, no CMake — the `tests/.../Oracle/build.sh` pattern) compiled from the
   pinned upstream header; binaries git-ignored; oracle tests skip gracefully
   when absent. Every (image × quality × subsampling) vector must match
   **byte-for-byte**. The optional managed cross-check (`StbImageWriteSharp`
   NuGet) can serve during development but the C header is the source of truth.
2. **Committed golden digests** — `jpeg-encoder-golden.tsv` (SHA-256 per vector),
   regenerated with `REGEN_JPEG_ENCODER_GOLDEN=1`; the `jpeg-oracle-golden.tsv`
   pattern. This is what CI enforces. Float determinism is a solved problem here:
   the decoder's float paths are already byte-exact across win-arm64 dev and
   ubuntu-x64 CI (IEEE semantics, no FMA contraction — keep `MathF` ops in the
   ported operation order, never `FusedMultiplyAdd`).
3. **Cross-decoder acceptance.** libjpeg (via Magick.NET) must decode every
   vector without error, and its pixels must sit within a small pinned tolerance
   of our own decoder's output of the same bytes (conformant IDCTs differ by
   ±1–2 codes; pin empirically and document).
4. **Self round-trip PSNR pins** per quality on the committed photo fixture
   (`DockPanes.jpg` crop) — e.g. q95/4:4:4 and q75/4:2:0 thresholds, measured
   then locked as regressions guards.
5. **M4 lossless-recode property**: optimized Huffman changes the entropy coding,
   never the coefficients — `Decode(optimized)` must equal `Decode(fixed)`
   **byte-exactly**. This single test catches nearly every optimizer bug.
6. **End-to-end acceptance (the payoff test):** an Ultra HDR JPEG produced 100 %
   in-family (`Compute` → `JpegEncoder` → `Assemble`) passes `JpegGainMap.TryRead`
   *and* the headless-Chromium differential check (SDR-profile render identical
   to base, HDR-profile render diverges). Plus a `JxrVisualConfirmation`-style
   artifact test (open-in-Photos smoke) and a TIFF round-trip where
   `JpegPassthroughSource` carries our own strips.

## Milestones (rung by rung)

1. ✅ **stbiw-exact core**: color baseline, 4:4:4 + 4:2:0, quality knob, float
   fDCT, fixed Annex K tables, bit-writer with byte stuffing, edge replication.
   Oracle byte-match (52-case matrix, `-ffp-contract=off` — see below) + golden
   TSV. The FMA-contraction gotcha the plan flagged was real: clang on aarch64
   fused the DCT/colour multiply-adds until the oracle build disabled it.
2. ✅ **Public API + integration**: `JpegEncoder` / `JpegEncodeOptions` /
   `JpegSubsampling`, package + CODECS.md updates, and the gain-map suite +
   in-family Ultra HDR demo dogfood the encoder (Magick.NET is now decode-oracle-only).
3. ⬜ **Grayscale output** (1-component JPEG) — clean-room extension beyond
   stbiw (which always writes YCbCr); halves typical gain-map bytes. Validated
   via libjpeg + our decoder + WIC smoke (no byte oracle exists for this path).
4. ⬜ **Optimized Huffman** — two-pass histogram → length-limited (≤16) canonical
   tables per T.81 Annex K.2. Gate: strictly smaller files on the photo vectors,
   lossless-recode property (see above).
5. ⬜ **Convenience tier**: one-call Ultra HDR
   (`JpegGainMap` overload: HDR floats + SDR pixels in → assembled `.jpg` out)
   and TIFF/JPEG strip compression wired to the encoder.
6. ⬜ Measure & record size/speed vs libjpeg q-equivalents on the fixtures
   (fixed-Huffman baseline is expected ~5–15 % larger; M4 closes most of it).
   Numbers go in CODECS.md, not promises.

## Non-goals / caveats

- **Progressive encode** — deferred until a consumer needs it (scan scripts +
  successive approximation are a decoder-scale effort on their own).
- **Restart intervals** — not emitted (reference doesn't); revisit only if a
  parallel-decode consumer appears.
- **Arithmetic coding, 12-bit, lossless-JPEG encode** — out of scope (ecosystem,
  ecosystem, and the lossless decoder exists for DNG reading, not writing).
- **Not a mozjpeg competitor.** Trellis quantization etc. are explicitly out;
  the bar is "correct, deterministic, oracle-pinned, good-enough size" —
  the same bar every other codec in the family clears.

## Versioning

Feature release ⇒ family minor bump **3.5 → 3.6**, in **both** places
(`Directory.Build.props` *and* `VERSION_PREFIX` in `.github/workflows/dotnet.yml`)
— the 3.4.49 lesson is institutionalized in both files' comments.
