# APNG fixtures

Animated PNGs for `SharpAstro.Png`, written and decoded by an **independent** implementation
(Pillow's `PngImagePlugin`), never by our own code. Regenerate with:

```
python -I make-fixtures.py [output-dir]      # default: this folder
```

Generated with **Pillow 12.1.1**, CPython 3.14.0 (win-arm64). The compressed bytes depend on the
zlib Pillow was built against, so a regenerated `.png` may differ byte for byte; the decoded
pixels and every number in `apng-reference.tsv` do not (the script was re-run and the TSV was
identical).

## Why the script re-reads its own output

Pillow decides each frame's rectangle itself (it crops every frame to where it differs from the
canvas it models) and merges frames that come out equal. So the script does not trust what it
asked for: it parses every file back chunk by chunk (CRCs, sequence numbers 0,1,2,... across
`fcTL`/`fdAT`, `acTL.num_frames == #fcTL`) and asserts that every frame has the **intended
rectangle, dispose_op, blend_op and delay**, that the IDAT / default-image placement is the
intended one, and that Pillow's own decoder reports the same per-frame values. A case that does
not exercise what its name says fails the run; nothing is committed for it.

## Files

Canvas 48x32 for all of them. Frame 0 is the full canvas; frames 1-3 sit on sub-rectangles
`(6,4,24x14)`, `(18,10,26x18)` and `(2,20,14x10)`, so offsets matter; the first two overlap
(the indexed file has its own, odd-width ones).
Patches have an opaque outer ring and a lattice of transparent-black holes (alpha 0) inside, which
is what tells `blend_op` SOURCE (a hole replaces what is beneath) from OVER (it shows through), and
what `dispose_op` left behind where frames overlap. Delays are 1/10, 1/5, 3/20, 1/4 s.

| File | What it covers |
|---|---|
| `apng-dispose-{none,background,previous}-blend-{source,over}.png` | All six `dispose_op` x `blend_op` pairs, 4 RGBA8 frames, alpha only 0 or 255. dispose_op is set on the interior frames (none: all frames; background: frames 1-2; previous: frame 1), so each pair is visible in the result. The six files decode to six distinct canvases at frames 2 and 3. Their encoded sub-images are the same bytes; only the `fcTL` ops differ. |
| `apng-blend-over-partial-alpha.png` | `blend_op` OVER with partial alpha in the source (alpha ramp 40-240) **and** in the destination (the background's lower-right corner fades). See the caveat below. |
| `apng-default-image.png` | `acTL` then `IDAT` then `fcTL`/`fdAT`: the IDAT image is a cover that is **not** part of the animation (4 animation frames, all in `fdAT`). dispose/blend mixed per frame. |
| `apng-indexed-trns.png` | Colour type 3 at **4 bits** (Pillow picks 4-bit for 8 colours): PLTE + tRNS `[0, 255, 255, 128]` (transparent, opaque, half-transparent entries), 3 frames, dispose none/previous, blend source. Frames 1 and 2 are 23 and 25 pixels wide, so their 4-bit rows are padded to whole bytes. |
| `apng-rgb8.png` | Colour type 2, 8-bit, opaque, sub-rectangle frames, `num_plays = 2`. |
| `apng-gray-alpha.png` | Colour type 4 (gray + alpha), 8-bit, dispose none/background, blend source/over. |

`num_plays` varies between files (0 = infinite, 2, 3, 5) so the parsed value is not a constant.

There is **no 16-bit fixture**: Pillow cannot write one. Its multi-frame writer computes each
frame's delta through an 8-bit RGBA conversion, which for `I;16` clips every sample above 255, so
two different 16-bit frames compare equal and are merged into a still image (checked). The palette
case stands in for the "unusual depth" requirement.

## `apng-reference.tsv`

Tab separated, `#` lines are comments, then a header row, then **one row per animation frame**:

`file frame num_frames num_plays idat_is_frame0 canvas_w canvas_h sequence x_offset y_offset width
height delay_num delay_den dispose_op blend_op canvas_sha256 idat_sha256`

* `fcTL` fields (`sequence` .. `blend_op`) and `num_frames`/`num_plays` are read from the bytes of the
  file with `struct`, not from Pillow's bookkeeping. `sequence` is the `fcTL`'s own number.
* `canvas_sha256` is the SHA-256 of **Pillow's own composited canvas** after that frame
  (`seek(frame)` + `convert('RGBA')`, RGBA8 bytes, row-major, `Image.tobytes()`). For
  `apng-default-image.png` the frames are the animation frames only (Pillow's frame 0 there is the
  cover, reported through `idat_sha256`).
* `idat_sha256` (same on every row of a file) is the SHA-256 of Pillow's decode of **the IDAT image
  alone**, with `acTL`/`fcTL`/`fdAT` stripped: what a reader that knows nothing about APNG sees.
  For files whose IDAT is frame 0 it equals frame 0's canvas.
* `idat_is_frame0` is 1 when an `fcTL` precedes the first `IDAT`.

### Caveat: Pillow's OVER is not the spec formula

`apng-blend-over-partial-alpha.png` is composited by Pillow with `blend_op` OVER. Measured (when the reader's
compositor landed): for every frame after the first, Pillow does not apply the specification's alpha-over but
`Image.paste` with the frame as its own mask, a lerp of **every band, alpha included**, by the frame's alpha
(`DIV255(bg * (255 - a) + fg * a)`). Where the canvas is opaque that is exactly the spec's formula, so the other ten
files (alpha only 0 or 255, or the indexed one, which uses SOURCE) agree with any sensible reading. Where the canvas
is itself partly transparent it is not: this file's frames 1-3 differ from the spec's canvases in 264, 571 and 667
of 1536 pixels, by up to 64, 91 and 91 in alpha (and 48 in a colour channel). So for that **one** file
`canvas_sha256` is Pillow's answer, not the spec's; `PngApngTests` reproduces those digests with a model of Pillow's
paste, checks the reader against the spec formula, and pins the difference.
