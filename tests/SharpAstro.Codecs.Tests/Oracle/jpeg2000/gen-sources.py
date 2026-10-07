"""Deterministic source rasters for the JPEG 2000 rung-1 fixtures.

Committed alongside make-fixtures.sh so a regeneration reproduces the exact
same bytes: every pattern here is a closed-form function of (x, y) or a fixed
linear-congruential sequence, with no library PRNG and no floating point, so
the fixtures do not drift with a Python version.

Written as PGM (P5, 8-bit) or, for the rgb* patterns, PPM (P6, 8-bit), because
opj_compress infers component count and bit depth from the input file. A PPM is
how a fixture gets three components, and with them the component transform:
opj_compress turns RCT on by itself for three reversibly coded components.
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


def rgb_structure(x, y):
    """Three channels that move together, as a picture's do: the component
    transform has something to decorrelate, and a wrong RCT shows up as a
    colour cast across the whole image rather than in one corner."""
    v = structure(x, y)
    return v, (v + 64) % 256, clamp(255 - v // 2 - y)


def rgb_noise(x, y):
    """Three independent LCG streams: no correlation for the transform to
    exploit, so every bit-plane of every component is busy."""
    return noise(x, y), noise(x + 17, y + 3), noise(y, x)


RGB_PATTERNS = {
    "rgbstruct": rgb_structure,
    "rgbnoise": rgb_noise,
}


def write_ppm(path, pattern, w, h):
    f = RGB_PATTERNS[pattern]
    px = bytearray(w * h * 3)
    i = 0
    for y in range(h):
        for x in range(w):
            px[i:i + 3] = bytes(f(x, y))
            i += 3
    with open(path, "wb") as fh:
        fh.write(b"P6\n%d %d\n255\n" % (w, h))
        fh.write(bytes(px))


if __name__ == "__main__":
    out, pattern, w, h = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
    if pattern in RGB_PATTERNS:
        write_ppm(out, pattern, w, h)
    else:
        write_pgm(out, pattern, w, h)
    print("  %-24s %s %dx%d" % (os.path.basename(out), pattern, w, h))
