#!/usr/bin/env python
"""Generates the APNG fixtures and their reference data with Pillow.

Run:   python -I make-fixtures.py [output-dir]      (default: the directory of this script)

Written against Pillow 12.1.1 on CPython 3.14.0 (win-arm64). The versions are recorded in
README.md too; a different Pillow may crop or merge frames differently, which is why this
script does not trust the encoder: it parses every file it wrote back, chunk by chunk, and
asserts that the dispose_op / blend_op pair and the sub-rectangle of every frame are the ones
the file's name promises. A case that does not exercise what its name says fails the run
instead of being committed.

What is independent here (nothing in this folder is produced by SharpAstro.Png):
  * the APNG files are WRITTEN by Pillow's PngImagePlugin;
  * the reference composites are Pillow's own DECODE of every frame (seek + convert('RGBA'));
  * the still decode of the IDAT image is Pillow's decode of the file with acTL/fcTL/fdAT
    stripped (what any APNG-unaware PNG reader sees);
  * the fcTL / acTL fields are read back from the bytes with struct, not taken from Pillow's
    bookkeeping, and cross-checked against what Pillow's decoder reports per frame.

Pillow's OVER (alpha_composite) rounding can differ from the APNG spec's formula on partial
alpha, so the one fixture with partial alpha (apng-blend-over-partial-alpha) is NOT a byte-exact
oracle for the spec formula; every other file only has alpha 0 or 255 where it composites
(the indexed one also has a 128 palette entry but uses blend_op SOURCE, which copies).

Outputs, beside this script:
  apng-*.png          the fixtures
  apng-reference.tsv  one row per animation frame: fcTL fields + SHA-256 of Pillow's composited
                      canvas (RGBA8 bytes), plus the SHA-256 of Pillow's decode of the IDAT image
"""
import hashlib
import io
import struct
import sys
import zlib
from pathlib import Path

import PIL
from PIL import Image
from PIL.PngImagePlugin import Blend, Disposal

SIGNATURE = b"\x89PNG\r\n\x1a\n"
NONE, BACKGROUND, PREVIOUS = int(Disposal.OP_NONE), int(Disposal.OP_BACKGROUND), int(Disposal.OP_PREVIOUS)
SOURCE, OVER = int(Blend.OP_SOURCE), int(Blend.OP_OVER)
DISPOSE_NAME = {NONE: "none", BACKGROUND: "background", PREVIOUS: "previous"}
BLEND_NAME = {SOURCE: "source", OVER: "over"}

W, H = 48, 32
TRANSPARENT = (0, 0, 0, 0)

# Frame rectangles (x, y, w, h). R1 and R2 overlap, so a hole in the later frame's patch shows
# what blend_op does over earlier content, and what dispose_op left behind there.
R1 = (6, 4, 24, 14)
R2 = (18, 10, 26, 18)
R3 = (2, 20, 14, 10)
RECTS = [None, R1, R2, R3]  # frame 0 is the full canvas


# ---------------------------------------------------------------- pixel content

def base_pixel(x, y):
    """The opaque background of frame 0: cool colours (R < 90, G < 80), so every patch differs."""
    return (30 + (x * 3) % 60, 20 + (y * 4) % 60, 120 + (x + y) % 60, 255)


def patch_pixel_for(frame):
    """Warm / light colours per frame, all differing from the background and from each other."""
    if frame == 1:
        return lambda x, y: (220 + x % 30, 40 + (y * 7) % 60, 30, 255)
    if frame == 2:
        return lambda x, y: (40, 220 + (x + y) % 30, 60 + (x * 5) % 40, 255)
    return lambda x, y: (200 + y % 40, 200 + x % 40, 230, 255)


def make_patch(rect, colour_of, holes, hole_phase):
    """
    An RGBA patch for rect. Its outermost ring is always opaque, so the pixels that differ from
    the canvas underneath reach all four edges and Pillow's delta bounding box is exactly rect.
    Interior pixels on a (x + 2y + phase) % 5 lattice are transparent black holes (alpha 0): they
    are what tells blend_op SOURCE (the hole replaces what is beneath) from OVER (it shows through).
    """
    x0, y0, w, h = rect
    img = Image.new("RGBA", (w, h))
    for j in range(h):
        for i in range(w):
            on_ring = i in (0, w - 1) or j in (0, h - 1)
            if holes and not on_ring and (x0 + i + 2 * (y0 + j) + hole_phase) % 5 == 0:
                img.putpixel((i, j), TRANSPARENT)
            else:
                img.putpixel((i, j), colour_of(x0 + i, y0 + j))
    return img


def make_base():
    img = Image.new("RGBA", (W, H))
    for y in range(H):
        for x in range(W):
            img.putpixel((x, y), base_pixel(x, y))
    return img


# ---------------------------------------------------------------- the canvas model

def simulate(patches, disposals):
    """
    The canvas the animation should show after each frame, built the way Pillow's writer models it
    (patch REPLACES the pixels under it; the previous frame's dispose_op decides what is underneath).
    Pillow crops each frame to where it differs from this model, so the frames below must differ
    from it exactly inside their rectangles. patches[0] is a full-canvas image.
    """
    targets = []
    base = Image.new("RGBA", (W, H), TRANSPARENT)
    for k, patch in enumerate(patches):
        if k == 0:
            target = patch.copy()
            rect = (0, 0, W, H)
        else:
            rect = RECTS[k]
            target = base.copy()
            target.paste(patch, rect[:2])
        targets.append(target)

        dispose = disposals[k]
        if k == 0 and dispose == PREVIOUS:
            dispose = BACKGROUND  # APNG: the first frame's PREVIOUS is treated as BACKGROUND
        before = base
        if dispose == NONE:
            base = target
        elif dispose == BACKGROUND:
            base = target.copy()
            base.paste(Image.new("RGBA", (rect[2], rect[3]), TRANSPARENT), rect[:2])
        else:  # PREVIOUS
            # Pillow rebuilds this from the frame before the previous one, which is the real canvas
            # only when that frame left it untouched.
            assert k >= 1 and disposals[k - 1] == NONE, "PREVIOUS after a non-NONE frame is mis-modelled by Pillow"
            base = before
    return targets


# ---------------------------------------------------------------- reading the bytes back

def read_chunks(data):
    assert data[:8] == SIGNATURE
    pos = 8
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        (crc,) = struct.unpack(">I", data[pos + 8 + length:pos + 12 + length])
        assert crc == zlib.crc32(ctype + body), f"bad CRC on {ctype!r}"
        yield pos, ctype, body
        pos += 12 + length


def parse_apng(data):
    """acTL / fcTL / fdAT read straight from the bytes."""
    ihdr = None
    actl = None
    fctl = []
    idat_before_first_fctl = None
    saw_idat = False
    seqs = []
    for _, ctype, body in read_chunks(data):
        if ctype == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif ctype == b"acTL":
            assert actl is None and not saw_idat, "acTL must precede IDAT"
            actl = struct.unpack(">II", body)
        elif ctype == b"fcTL":
            seq, w, h, x, y, dn, dd, disp, blend = struct.unpack(">IIIIIHHBB", body)
            if not fctl:
                idat_before_first_fctl = saw_idat is False  # fcTL before IDAT <=> IDAT is frame 0
            fctl.append(dict(seq=seq, w=w, h=h, x=x, y=y, delay_num=dn, delay_den=dd, dispose=disp, blend=blend))
            seqs.append(seq)
        elif ctype == b"IDAT":
            saw_idat = True
        elif ctype == b"fdAT":
            (seq,) = struct.unpack(">I", body[:4])
            seqs.append(seq)
    assert actl is not None and fctl
    assert seqs == list(range(len(seqs))), f"sequence numbers must run 0,1,2,... got {seqs}"
    assert actl[0] == len(fctl), f"acTL says {actl[0]} frames, file has {len(fctl)} fcTL"
    return dict(ihdr=ihdr, num_frames=actl[0], num_plays=actl[1], idat_is_frame0=bool(idat_before_first_fctl),
                frames=fctl)


def strip_animation(data):
    """The file as an APNG-unaware reader sees it: acTL, fcTL and fdAT removed."""
    out = bytearray(SIGNATURE)
    for pos, ctype, body in read_chunks(data):
        if ctype in (b"acTL", b"fcTL", b"fdAT"):
            continue
        out += struct.pack(">I", len(body)) + ctype + body + struct.pack(">I", zlib.crc32(ctype + body))
    return bytes(out)


def sha256(b):
    return hashlib.sha256(b).hexdigest()


# ---------------------------------------------------------------- the cases

class Case:
    def __init__(self, name, frames, disposals, blends, durations, loop=0, default_image=None, note=""):
        self.name, self.frames, self.disposals, self.blends = name, frames, disposals, blends
        self.durations, self.loop, self.default_image, self.note = durations, loop, default_image, note


DURATIONS = [100, 200, 150, 250]  # ms; Pillow writes them as 1/10, 1/5, 3/20, 1/4 s


def rgba_case(name, disposals, blends, holes=True, partial=False, loop=0, note=""):
    patches = [make_base()]
    for k in (1, 2, 3):
        colour_of = patch_pixel_for(k)
        patch = make_patch(RECTS[k], colour_of, holes=holes, hole_phase=k)
        if partial:
            # An alpha ramp across x: 40 .. 240, never 0, never 255 on the interior.
            x0, y0, w, h = RECTS[k]
            for j in range(h):
                for i in range(w):
                    r, g, b, a = patch.getpixel((i, j))
                    if a == 255 and not (i in (0, w - 1) or j in (0, h - 1)):
                        patch.putpixel((i, j), (r, g, b, 40 + (200 * i) // max(1, w - 1)))
        patches.append(patch)
    if partial:
        # partial alpha in the destination too: a corner of the background fades out
        base = patches[0]
        for y in range(H):
            for x in range(W):
                if x >= W - 14 and y >= H - 12:
                    r, g, b, _ = base.getpixel((x, y))
                    base.putpixel((x, y), (r, g, b, 60 + 12 * (x - (W - 14))))
    targets = simulate(patches, disposals)
    return Case(name, targets, disposals, blends, DURATIONS, loop=loop, note=note)


def pair_cases():
    combos = {
        NONE: [NONE, NONE, NONE, NONE],
        BACKGROUND: [NONE, BACKGROUND, BACKGROUND, NONE],
        PREVIOUS: [NONE, PREVIOUS, NONE, NONE],
    }
    for dispose, disposals in combos.items():
        for blend in (SOURCE, OVER):
            yield rgba_case(f"apng-dispose-{DISPOSE_NAME[dispose]}-blend-{BLEND_NAME[blend]}",
                            disposals, [blend] * 4,
                            note=f"dispose_op {dispose} ({DISPOSE_NAME[dispose]}) x blend_op {blend} ({BLEND_NAME[blend]}); alpha 0/255 only")


def default_image_case():
    patches = [make_base()]
    for k in (1, 2, 3):
        patches.append(make_patch(RECTS[k], patch_pixel_for(k), holes=True, hole_phase=k))
    disposals = [NONE, BACKGROUND, NONE, NONE]
    targets = simulate(patches, disposals)
    cover = Image.new("RGBA", (W, H))
    for y in range(H):
        for x in range(W):
            on_diag = abs(x * H - y * W) < W * 2 or abs((W - 1 - x) * H - y * W) < W * 2
            cover.putpixel((x, y), (250, 250, 250, 255) if on_diag else (10 + x * 2, 10 + y * 3, 10, 255))
    return Case("apng-default-image", targets, disposals, [SOURCE, OVER, OVER, SOURCE], DURATIONS,
                loop=3, default_image=cover,
                note="IDAT is a cover image that is not part of the animation (first fcTL precedes the first fdAT)")


def rgb_case():
    # RGB (colour type 2, no alpha): opaque, dispose none / blend source, sub-rectangle frames.
    patches = [make_base().convert("RGB").convert("RGBA")]
    for k in (1, 2, 3):
        p = make_patch(RECTS[k], patch_pixel_for(k), holes=False, hole_phase=0)
        patches.append(p)
    targets = [t.convert("RGB") for t in simulate(patches, [NONE] * 4)]
    return Case("apng-rgb8", targets, [NONE] * 4, [SOURCE] * 4, DURATIONS, loop=2,
                note="colour type 2 (RGB 8-bit), opaque, sub-rectangle frames")


def gray_alpha_case():
    def gray(x, y):
        return (40 + (x * 5) % 120, 40 + (x * 5) % 120, 40 + (x * 5) % 120, 255)
    patches = [Image.new("RGBA", (W, H))]
    for y in range(H):
        for x in range(W):
            patches[0].putpixel((x, y), gray(x, y))
    levels = {1: 250, 2: 20, 3: 230}
    for k in (1, 2, 3):
        patches.append(make_patch(RECTS[k], lambda x, y, v=levels[k]: (v, v, v, 255), holes=True, hole_phase=k))
    disposals = [NONE, BACKGROUND, NONE, NONE]
    targets = [t.convert("LA") for t in simulate(patches, disposals)]
    return Case("apng-gray-alpha", targets, disposals, [SOURCE, OVER, SOURCE, OVER], DURATIONS, loop=0,
                note="colour type 4 (gray + alpha 8-bit), alpha 0/255, dispose none/background, blend source/over")


PALETTE = [
    (0, 0, 0),        # 0: transparent (tRNS 0)
    (30, 60, 150),    # 1
    (40, 130, 70),    # 2
    (230, 200, 40),   # 3: half transparent (tRNS 128)
    (220, 50, 50),    # 4
    (240, 240, 240),  # 5
    (90, 20, 140),    # 6
    (20, 20, 20),     # 7
]
TRNS = bytes([0, 255, 255, 128])  # entries past the end are opaque


def indexed_case():
    # 8 colours -> Pillow writes 4-bit indexed, so frames are sub-byte packed rows of odd widths.
    def paletted(fn):
        img = Image.new("P", (W, H))
        img.putpalette([c for rgb in PALETTE for c in rgb])
        for y in range(H):
            for x in range(W):
                img.putpixel((x, y), fn(x, y))
        img.info["transparency"] = TRNS
        return img

    frames = [paletted(lambda x, y: 1 + ((x // 4 + y // 4) % 2))]  # blue / green checker
    cur = [[frames[0].getpixel((x, y)) for x in range(W)] for y in range(H)]

    def with_patch(prev, rect, fn):
        grid = [row[:] for row in prev]
        x0, y0, w, h = rect
        for j in range(h):
            for i in range(w):
                grid[y0 + j][x0 + i] = fn(i, j, w, h)
        return grid

    def to_img(grid):
        return paletted(lambda x, y: grid[y][x])

    def patch_a(i, j, w, h):
        if i in (0, w - 1) or j in (0, h - 1):
            return 4
        return 0 if (i + j) % 5 == 0 else (3 if (i + j) % 5 == 1 else 5)

    def patch_b(i, j, w, h):
        if i in (0, w - 1) or j in (0, h - 1):
            return 6
        return 0 if (i + 2 * j) % 5 == 0 else 7

    g1 = with_patch(cur, R1, patch_a)
    frames.append(to_img(g1))
    # frame 2 follows a dispose_op PREVIOUS frame, so it is drawn over the canvas of frame 0
    g2 = with_patch(cur, R2, patch_b)
    frames.append(to_img(g2))
    disposals = [NONE, PREVIOUS, NONE]
    return Case("apng-indexed-trns", frames, disposals, [SOURCE] * 3, DURATIONS[:3], loop=5,
                note="colour type 3 at 4 bits (PLTE + tRNS incl. a half-transparent entry), dispose none/previous, blend source")


def all_cases():
    yield from pair_cases()
    yield rgba_case("apng-blend-over-partial-alpha", [NONE, NONE, NONE, NONE], [OVER] * 4,
                    holes=False, partial=True, loop=0,
                    note="blend_op over with partial alpha in source AND destination; Pillow's rounding is the reference, not the spec formula")
    yield default_image_case()
    yield rgb_case()
    yield gray_alpha_case()
    yield indexed_case()


# ---------------------------------------------------------------- write + verify

def write_case(case, out_dir):
    path = out_dir / f"{case.name}.png"
    first, rest = case.frames[0], case.frames[1:]
    kwargs = dict(save_all=True, append_images=rest, duration=case.durations, loop=case.loop,
                  disposal=case.disposals, blend=case.blends)
    if case.default_image is not None:
        # Pillow: `im` itself is the default image (IDAT); every appended image is an animation frame.
        kwargs["default_image"] = True
        kwargs["append_images"] = case.frames
        first = case.default_image
        if case.frames[0].mode != "RGBA":
            first = first.convert(case.frames[0].mode)
    buf = io.BytesIO()
    first.save(buf, "PNG", **kwargs)
    path.write_bytes(buf.getvalue())
    return path


def expected_rects(case):
    return [(0, 0, W, H) if k == 0 else RECTS[k] for k in range(len(case.frames))]


def verify_and_reference(case, path):
    data = path.read_bytes()
    meta = parse_apng(data)
    n = len(case.frames)

    assert meta["num_frames"] == n, f"{case.name}: Pillow merged frames: {meta['num_frames']} != {n}"
    assert meta["num_plays"] == case.loop, case.name
    assert meta["idat_is_frame0"] == (case.default_image is None), f"{case.name}: IDAT/default image mismatch"
    assert (meta["ihdr"][0], meta["ihdr"][1]) == (W, H), case.name

    for k, (frame, rect) in enumerate(zip(meta["frames"], expected_rects(case))):
        got = (frame["x"], frame["y"], frame["w"], frame["h"])
        assert got == rect, f"{case.name} frame {k}: rectangle {got} is not the intended {rect}"
        assert frame["dispose"] == case.disposals[k], f"{case.name} frame {k}: dispose_op {frame['dispose']} != {case.disposals[k]}"
        assert frame["blend"] == case.blends[k], f"{case.name} frame {k}: blend_op {frame['blend']} != {case.blends[k]}"
        assert frame["delay_den"] != 0
        ms = 1000 * frame["delay_num"] / frame["delay_den"]
        assert abs(ms - case.durations[k]) < 0.5, f"{case.name} frame {k}: delay {ms} ms != {case.durations[k]}"

    # name promises (name -> every dispose/blend pair it advertises is really there, on a frame that is visible)
    if case.name.startswith("apng-dispose-"):
        _, _, d, _, b = case.name.split("-")
        want = {v: k for k, v in DISPOSE_NAME.items()}[d], {v: k for k, v in BLEND_NAME.items()}[b]
        assert any((f["dispose"], f["blend"]) == want for f in meta["frames"][1:-1]), \
            f"{case.name}: no interior frame with the pair {want}"
        # and a sub-rectangle that is not the canvas, so the offsets matter
        assert any((f["x"], f["y"]) != (0, 0) for f in meta["frames"])

    # Pillow's own decode of each frame: the reference composites
    rows = []
    with Image.open(io.BytesIO(data)) as im:
        shift = 1 if case.default_image is not None else 0
        assert im.n_frames == n + shift, f"{case.name}: Pillow sees {im.n_frames} frames, expected {n + shift}"
        for k in range(n):
            im.seek(k + shift)
            canvas = im.convert("RGBA")
            assert canvas.size == (W, H)
            # cross-check the bytes against Pillow's decoder state for this frame
            decoded_ms = im.info.get("duration")
            expected_ms = 1000 * meta["frames"][k]["delay_num"] / meta["frames"][k]["delay_den"]
            assert decoded_ms is not None and abs(decoded_ms - expected_ms) <= 1, \
                f"{case.name} frame {k}: Pillow decodes duration {decoded_ms}, fcTL says {expected_ms}"
            fc = meta["frames"][k]
            assert int(im.dispose_op) == fc["dispose"], f"{case.name} frame {k}: Pillow decodes dispose_op {im.dispose_op}"
            assert int(im.blend_op) == fc["blend"], f"{case.name} frame {k}: Pillow decodes blend_op {im.blend_op}"
            assert tuple(im.dispose_extent) == (fc["x"], fc["y"], fc["x"] + fc["w"], fc["y"] + fc["h"]),                 f"{case.name} frame {k}: Pillow decodes extent {im.dispose_extent}"
            rows.append(sha256(canvas.tobytes()))

    # the IDAT image as an APNG-unaware reader decodes it
    with Image.open(io.BytesIO(strip_animation(data))) as still:
        idat_sha = sha256(still.convert("RGBA").tobytes())
    return meta, rows, idat_sha


def main():
    out_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent
    out_dir.mkdir(parents=True, exist_ok=True)
    tsv = [
        f"# Written by make-fixtures.py with Pillow {PIL.__version__}. One row per animation frame.",
        "# fcTL fields are parsed back from the bytes of the file; canvas_sha256 is the SHA-256 of Pillow's own composited canvas",
        "# (seek(frame) + convert('RGBA'), RGBA8 bytes, row-major); idat_sha256 is Pillow's decode of the IDAT image alone, with acTL/fcTL/fdAT stripped.",
        "# apng-blend-over-partial-alpha: Pillow's OVER rounding is not guaranteed to equal the APNG spec formula; see README.md.",
        "\t".join(["file", "frame", "num_frames", "num_plays", "idat_is_frame0", "canvas_w", "canvas_h", "sequence",
                  "x_offset", "y_offset", "width", "height", "delay_num", "delay_den", "dispose_op", "blend_op",
                  "canvas_sha256", "idat_sha256"]),
    ]
    for case in all_cases():
        path = write_case(case, out_dir)
        meta, rows, idat_sha = verify_and_reference(case, path)
        for k, (frame, sha) in enumerate(zip(meta["frames"], rows)):
            tsv.append("\t".join(map(str, [
                path.name, k, meta["num_frames"], meta["num_plays"], int(meta["idat_is_frame0"]), W, H, frame["seq"],
                frame["x"], frame["y"], frame["w"], frame["h"], frame["delay_num"], frame["delay_den"],
                frame["dispose"], frame["blend"], sha, idat_sha])))
        print(f"{path.name}: {path.stat().st_size:5d} bytes, {meta['num_frames']} frames"
              f"{'' if meta['idat_is_frame0'] else ', IDAT not in the animation'}")
    (out_dir / "apng-reference.tsv").write_text("\n".join(tsv) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote apng-reference.tsv ({len(tsv) - 5} frames)")


if __name__ == "__main__":
    main()
