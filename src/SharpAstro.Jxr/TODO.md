# SharpAstro.Jxr — TODO

Spec features and infrastructure that are not yet implemented. The HDR-master
pipeline (BD16 / BD16F / BD32F × Grayscale / RGB × spatial / frequency mode ×
tiled × POT × INDEX_TABLE_TILES × alpha) is complete; everything below is
"would be nice for full T.832 coverage" rather than blocking the user's
astrophotography workflow.

## Spec features

### Not implemented — encode + decode both missing

- **AllBands / FlexBits refinement layer.** The fourth band sub-stream that
  carries the missing LSBs of HP coefficients for lossless > NoFlexbits.
  `TileSpatial.{Write,Read}` and `TileFrequency.{Write,Read}` both reject
  `JxrBandsPresent.AllBands`. Needed for full-precision lossless above what
  `NoFlexbits` alone gives.

- **YUV420 / YUV422 chroma subsampling.** `ImagePlaneHeader.Write` rejects
  these formats outright (`NotSupportedException`). The `CBPLP_YUV1` /
  `CBPLP_YUV2` joint-VLC path in `MbLp` is also unwired. Needed to decode
  external multi-tile fixtures that use chroma subsampling (e.g. the seagull
  nebula fixture is NComponent BD8, but plenty of real-world JXR producers
  emit YUV).

- **Windowing (margins).** `WINDOWING_FLAG=true` throws in
  `ImageHeader.{Write,Read}`. T.832 §8.3 lets the codestream carry inset
  margins so the decoded image isn't a flat multiple of 16; we currently
  pad-and-clamp at encode time and ignore margins on decode.

- **NComponent extension (NumComponents > 16).** `ImagePlaneHeader.Write`
  caps at 16 components (the `NUM_COMPONENTS_MINUS1 == 0xF` extension path
  isn't wired). Rare in practice.

- **Lossless BD32F at LEN_MANTISSA=23 (full 23-bit mantissa).** The
  `Span<long>` overloads of `FCT4x4`/`ICT4x4` exist in `Transforms.cs` and
  round-trip past int32 range, but `Macroblock` storage and the prediction
  layers (`MbDc`, `MbLp`, `MbHp`, `DcPrediction`, `LpPrediction`,
  `HpPrediction`) are all `int[]` — they need to plumb `long[]` through
  before BD32F can use the full mantissa losslessly. Big refactor (10+
  files); the current LEN_MANTISSA = 8 default suits HDR pipelines that
  already half-float in advance.

## Random-access decoder API

- **INDEX_TABLE_TILES random access in frequency mode.**
  `CodedImage.DecodeTile` works for spatial mode (uses the single offset per
  tile) but rejects frequency mode — there the index table has one entry
  per (tile × band) and the decoder would need to seek to each band
  sub-stream independently. Refactor: split `TileFrequency.Read` so the
  caller provides three pre-positioned `BitReader` snapshots (one per band)
  instead of reading consecutively from a single stream.

## Real-fixture validation

- **Seagull / HDR-float fixture decode.** `JxrRealFixtureProbeTests`
  documents the header shape of `seagull_nebula.jxr` (NComponent BD8,
  12×12 tiles, frequency mode) and `hdr_128bpp_float_sample.jxr`
  (1000×1000, BD32F, 4×4 tiles, frequency mode). With frequency mode now
  in, the next blocker is likely YUV (if the seagull's internal format
  turns out to be YUV) or BD32F at non-default LEN_MANTISSA. Worth running
  a full-decode attempt and recording what blows up first.

## Packaging

- **Flip `<IsPackable>false</IsPackable>` to `true`.** The csproj has a
  comment "Flip to true once Phase 1 (T.833 container round-trip) lands."
  We're well past that — full container + codestream + tiling + alpha is
  in. The package is ready to ship.

## Known-completed (for reference — these were the major gaps before)

Spatial mode, frequency mode, multi-tile, INDEX_TABLE_TILES read+write,
alpha plane, edge padding, lossy quantization, POT (OverlapMode=1), BD8 /
BD16 / BD16F / BD32F facades, file-level `.jxr` Save/Load via `JxrContainer`.
