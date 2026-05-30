# OpenEXR reference source (port source-of-truth)

These files are the authoritative OpenEXR (OpenEXRCore) C source that `SharpAstro.Exr`'s
PIZ implementation was faithfully ported from — the EXR analogue of the JXR `jxrlib-src`:

- `internal_piz.c`  — PIZ orchestration, bitmap/LUT range compaction, 2D wavelet (wav_2D_encode/decode, wenc14/wdec14/wenc16/wdec16)
- `internal_huf.c`  — canonical Huffman (build/pack/unpack tables, encode/decode, RLE pseudo-symbol)
- `internal_huf.h`  — Huffman API

Fetched from https://github.com/AcademySoftwareFoundation/openexr (BSD-3-Clause; SPDX
headers preserved). Used for porting reference only; not compiled into the test project.
