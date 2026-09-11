# Roadmap: JPEG XL (`SharpAstro.Jxl`)

> **Status: written 2026-09-11**, when a performance investigation into `JxlDct` produced a
> finding that had nowhere to live — this repo has issues disabled on GitHub, and the
> convention is a `ROADMAP-*.md` at the root. JXL was the one substantial codec here without
> one. The performance question below is the reason the file exists; the feature envelope is
> included because it was already knowable from the code and otherwise only discoverable by
> grepping for `NotSupportedException`.

## The open question: VarDCT decode allocates on the Large Object Heap

This is the live item. It is **a hypothesis with measured symptoms, not a diagnosis** — that
distinction is the point of writing it down rather than acting on it.

### What is measured

`benchmarks/SharpAstro.Codecs.Benchmarks`, `JxlDecodeBenchmarks`, 256×256 RGB:

| | time | allocated | Gen2 / 1k ops |
|---|---|---|---|
| VarDCT lossy decode | 12.01 ms | **10.14 MB** | ~1625 |
| Modular lossless decode | 4.48 ms | 1.15 MB | ~328 |
| VarDCT lossy encode | 16.43 ms | 11.22 MB | ~1359 |

A 256×256 RGB image is ~786 KB of pixels in any representation. The VarDCT path allocates
**roughly 13× the image size to decode it**, and runs gen2 collections continuously while
doing so. The Modular path, decoding the same image, allocates about a ninth as much.

### What the mechanism looks like

The VarDCT path holds several **full-plane** buffers, one per channel, allocated per decode:
`hfGrid` and `rgb` in `JxlVarDctFrame` (`int[width * height]`), `subCoeff` per group, plus
`coeff`, `xyb` and `srgb` in `JxlVarDctImage` (`float[width * height]`).

At 256×256 each of those is `65,536 × 4 = 262,144 bytes`. **The Large Object Heap threshold is
85,000 bytes**, so every one of them is an LOH allocation — and the LOH is collected only with
gen2. That arithmetic is certain; a dozen-plus LOH allocations per decode is consistent with
the gen2 counts above.

Note this gets *worse* with image size, not better: the buffers scale with `width × height`,
so they are over the threshold for every image larger than about 150×150.

### What is NOT established

Whether that allocation behaviour is what is *costing the time*. The correlation is suggestive
(the path that allocates 9× more takes 2.7× longer) but the two paths differ in much more than
allocation, so the comparison does not isolate anything. **Nobody has profiled where the 12 ms
goes.** Do that before changing anything.

### How to test it

Pool the full-plane buffers (`ArrayPool<int>.Shared` / `ArrayPool<float>.Shared`) and re-run
`JxlDecodeBenchmarks` back-to-back. `MemoryDiagnoser` is already enabled, so the Allocated and
Gen2 columns will say directly whether the churn went away, and the Mean column will say
whether that mattered. If allocation drops hard and the time does not move, the theory is
wrong and the next suspect is the entropy decode.

Note the encode path shows the same profile (11.22 MB) and would likely benefit from the same
change, but it is a separate measurement.

## Settled — do not re-derive these

- **Do not vectorize `JxlDct`.** This was investigated on 2026-09-11 specifically because the
  JPEG 8×8 IDCT had just been vectorized for −27% / −38%, and `JxlDct` looked structurally
  similar (separable, float, and validated at RMSE level rather than bit-exact, so it has a
  *freer* hand than the JPEG kernel had). It is not worth it: the float arithmetic measured
  ~1.06 ms per decode's worth of 8×8 transforms even in an isolated tight loop that flatters
  it, against a 12 ms decode. The JPEG IDCT was worth vectorizing because it was a genuine
  ~40% of decode. Structural similarity is not a share of runtime.
- **The `SecHalf` lock and the `Dct2d` per-call allocations are already gone** (`perf(jxl):
  take the lock and the allocations out of the DCT's inner loop`). Worth 12.73 → 12.01 ms,
  bit-identical. The same change measured 5.42 → 1.06 ms *in isolation*, i.e. it over-predicted
  the end-to-end result by about 6×; see the benchmarking notes in `CLAUDE.md`.

## Feature envelope

Not a wishlist — this is what the codec currently refuses, taken from the
`NotSupportedException` sites in `src/SharpAstro.Jxl/`. Anything here is a real gap a caller
can hit, and the messages are the authoritative list if this section drifts.

**Frame-level**

- Cropped frames; bitstream extensions; noise; patches; splines.
- VarDCT frame without a global tree.
- Custom `DequantMatrixSet`; custom coefficient orders.

**Modular**

- Extra channels; the Squeeze transform; delta-palette entries; `do_ycbcr`.
- Multi-group images on the **encode** side (dimension > 1024).

**Colour**

- Embedded ICC colour encoding; custom primaries; custom white point.

**VarDCT encoder shape**

- Width and height must be multiples of 8.
- Dimensions capped at 16384 — a memory / int-index sanity guard, not a format limit.
- Grayscale-lossy is not implemented (only RGB), per `JxlImageCodec`.

## Where the numbers come from

`benchmarks/SharpAstro.Codecs.Benchmarks` (`dotnet run -c Release --project
benchmarks/SharpAstro.Codecs.Benchmarks -- --filter '*Jxl*'`). Its filter matches
`namespace.type.method`, not the `[Benchmark(Description = …)]` text.

Every figure in this file is end-to-end through the public API. `CLAUDE.md` explains why that
is the only kind of number trusted here, and lists the two occasions in this repo where an
isolated kernel measurement pointed the wrong way.
