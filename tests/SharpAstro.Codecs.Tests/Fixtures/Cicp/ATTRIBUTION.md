# PNG-3 cICP / mDCV / cLLI Conformance Fixtures

The PNG files in this directory are excerpts from Mike Pedersen's PNG-3
conformance test suite:

  https://github.com/MikePedersen/CICP-Test-Files-PNG3rdEd-TIFF-MOV-MXF-AVIF-also-mDCV-and-cLLI

The upstream repository describes itself as "Conformance Test Files for
specific Video Workflow Testing" and intentionally publishes these files
for downstream codecs to validate their cICP / mDCV / cLLI handling against
known-good reference data. The repository ships only a README, no formal
LICENSE file.

Used here strictly for `CicpExternalFixturesTests.cs` (verifying that
`SharpAstro.Png.PngReader` parses each variant to the expected
`(ColorPrimaries, TransferFunction, MatrixCoefficients, VideoFullRangeFlag)`
tuple plus the mDCV + cLLI chunks when present).

If the upstream author objects to redistribution, the files can be
re-fetched at test-build time via the original GitHub URL above without
losing any test coverage.

## What each file demonstrates

| File | cICP `{primaries, transfer, matrix, fullRange}` | Notes |
|---|---|---|
| `PNG-SDR-BT.709-...-cICP-FR.png` | `{1, 1, 0, 1}` | SDR Rec.709 / sRGB primaries, BT.709 transfer, full range |
| `PNG-SDR-BT.709-...-cICP-NR.png` | `{1, 1, 0, 0}` | Same as above, narrow (limited) range |
| `PNG-PQ-BT.2111-...-cICP-FR.png` | `{9, 16, 0, 1}` | HDR10: BT.2020 primaries + SMPTE 2084 PQ |
| `PNG-HLG-...-cICP-FR.png` | `{9, 18, 0, 1}` | Broadcast HDR: BT.2020 + Hybrid Log-Gamma |
| `PNG-HLG-...-cICP-NR.png` | `{9, 18, 0, 0}` | HLG, narrow range |
| `SDR-Color-Bars-...-12bitTest.png` | `{9, 15, 0, 1}` | BT.2020 12-bit SDR (transfer 15) |
| `PNG-PQ-...-max1Knit-...-cLLI-FR.png` | `{9, 16, 0, 1}` + `mDCV` + `cLLI(1000, 250)` | HDR10 with mastering display + content-light-level |
| `PNG-PQ-...-max4Knit-...-cLLI-FR.png` | `{9, 16, 0, 1}` + `mDCV` + `cLLI(4000, 250)` | Higher peak-luminance grade |
