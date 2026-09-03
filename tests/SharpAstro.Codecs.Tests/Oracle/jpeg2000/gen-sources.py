"""Deterministic source rasters for the JPEG 2000 rung-1 fixtures.

Committed alongside make-fixtures.sh so a regeneration reproduces the exact
same bytes: every pattern here is a closed-form function of (x, y) or a fixed
linear-congruential sequence, with no library PRNG and no floating point, so
the fixtures do not drift with a Python version.

Written as PGM (P5, 8-bit) because opj_compress infers component count and bit
depth from the input file, and one 8-bit component is the whole of rung 1.
"""
import os
import sys


def clamp(v):
    return 0 if v < 0 else 255 if v > 255 else v


def flat(x, y):
    """Constant. Every bit-plane above zero is empty, so the packet says
    'code-block not included' and tier-1 is never entered -- the degenerate
    case that a decoder written only against busy images gets wrong."""
    return 128


def ramp(x, y):
    """Smooth horizontal gradient. Sign coding is the point: a wrong sign
    context or a dropped XOR bit (hazard 4) is invisible on detail and
    glaring on a gradient."""
    return clamp(x * 4)


def structure(x, y):
    """Blocks, edges and a diagonal: every subband gets real energy, and the
    edges land off code-block boundaries."""
    v = (x * 3 + y * 2) % 256
    if 16 <= x < 32 and 20 <= y < 44:
        v = 255 - v
    if (x // 8 + y // 8) % 2 == 0:
        v = (v + 97) % 256
    return v


def noise(x, y):
    """A fixed LCG over the pixel index -- dense high-frequency data, so every
    coding pass runs on every bit-plane and no pass stays untested."""
    s = (x + y * 733) * 1103515245 + 12345
    return (s >> 16) & 0xFF


PATTERNS = {
    "flat": flat,
    "ramp": ramp,
    "struct": structure,
    "noise": noise,
}


def write_pgm(path, pattern, w, h):
    f = PATTERNS[pattern]
    px = bytearray(w * h)
    i = 0
    for y in range(h):
        for x in range(w):
            px[i] = f(x, y)
            i += 1
    with open(path, "wb") as fh:
        fh.write(b"P5\n%d %d\n255\n" % (w, h))
        fh.write(bytes(px))


if __name__ == "__main__":
    out, pattern, w, h = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
    write_pgm(out, pattern, w, h)
    print("  %-24s %s %dx%d" % (os.path.basename(out), pattern, w, h))
