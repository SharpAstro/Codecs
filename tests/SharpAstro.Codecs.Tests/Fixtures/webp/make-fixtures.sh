#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cwebp="${CWEBP:-$(command -v cwebp)}"
ffmpeg="${FFMPEG:-$(command -v ffmpeg)}"
webpinfo="${WEBPINFO:-$(command -v webpinfo)}"

python3 - "$tmp" <<'PY'
import struct
import sys
import zlib
from pathlib import Path

directory = Path(sys.argv[1])

def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

def write_png(name, width, height, pixel):
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows.extend(pixel(x, y))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    (directory / f"{name}.png").write_bytes(data)

write_png("flat-gray-16x16", 16, 16, lambda _x, _y: (128, 128, 128))
write_png("gradient-16x16", 16, 16, lambda x, y: (x * 17, y * 17, (x + y) * 8))

seed = 0x12345678
def noise(_x, _y):
    global seed
    seed = (1664525 * seed + 1013904223) & 0xffffffff
    return seed >> 24, (seed >> 16) & 255, (seed >> 8) & 255
write_png("noise-32x32", 32, 32, noise)
seed = 0x12345678
write_png("noise-17x17", 17, 17, noise)
write_png("odd-33x19", 33, 19, lambda x, y: (
    (x * 23 + y * 7) & 255,
    (x * 5 + y * 19) & 255,
    (x * 11 + y * 13) & 255,
))
PY

cwebp -version > "$root/cwebp-version.txt" 2>&1
"$ffmpeg" -version > "$root/ffmpeg-version.txt" 2>&1
libvpx_version=""
if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists vpx; then
    libvpx_version="$(pkg-config --modversion vpx)"
elif command -v brew >/dev/null 2>&1 && brew list --versions libvpx >/dev/null 2>&1; then
    libvpx_version="$(brew list --versions libvpx | awk '{ print $2 }')"
fi
printf 'libvpx %s\n' "${libvpx_version:-unknown}" > "$root/libvpx-version.txt"

cwebp -quiet -q 75 -m 0 -f 0 -segments 1 "$tmp/flat-gray-16x16.png" -o "$root/flat-gray-16x16.webp"
cwebp -quiet -q 75 -m 0 -f 0 "$tmp/gradient-16x16.png" -o "$root/gradient-16x16.webp"
cwebp -quiet -q 75 -m 0 -f 0 -segments 1 "$tmp/noise-32x32.png" -o "$root/noise-32x32.webp"
cwebp -quiet -q 75 -m 0 -f 0 -segments 4 "$tmp/noise-32x32.png" -o "$root/noise-32x32-segments4.webp"
cwebp -quiet -q 75 -m 0 -f 0 -segments 1 "$tmp/noise-17x17.png" -o "$root/noise-17x17.webp"
cwebp -quiet -q 75 -m 0 -f 30 -strong -sharpness 0 -segments 1 \
    "$tmp/noise-17x17.png" -o "$root/noise-17x17-f30-strong-s0.webp"
cwebp -quiet -q 75 -m 0 -f 30 -nostrong -sharpness 0 -segments 1 \
    "$tmp/noise-32x32.png" -o "$root/noise-32x32-f30-simple-s0.webp"
cwebp -quiet -q 75 -m 0 -f 30 -strong -sharpness 0 -segments 1 \
    "$tmp/noise-32x32.png" -o "$root/noise-32x32-f30-strong-s0.webp"
cwebp -quiet -q 75 -m 0 -f 30 -strong -sharpness 7 -segments 1 \
    "$tmp/noise-32x32.png" -o "$root/noise-32x32-f30-strong-s7.webp"

"$ffmpeg" -hide_banner -loglevel error -y -f lavfi \
    -i "nullsrc=s=64x48,geq=lum='random(1)*255':cb=128:cr=128" \
    -frames:v 1 "$tmp/noise-64x48.png"
for partitions in 2 4 8; do
    ivf="$tmp/noise-64x48-partitions$partitions.ivf"
    output="$root/noise-64x48-vp8-partitions$partitions.webp"
    "$ffmpeg" -hide_banner -loglevel error -y -i "$tmp/noise-64x48.png" \
        -c:v libvpx -slices "$partitions" -b:v 4M -frames:v 1 -f ivf "$ivf"
    python3 "$root/wrap-vp8-ivf.py" "$ivf" "$output"
    actual_partitions="$("$webpinfo" -bitstream_info "$output" | awk '/Total partitions:/ { print $3 }')"
    if [[ "$actual_partitions" != "$partitions" ]]; then
        printf 'Expected %s token partitions, got %s in %s\n' \
            "$partitions" "${actual_partitions:-unknown}" "$output" >&2
        exit 1
    fi
done

"$ffmpeg" -hide_banner -loglevel error -y -i "$tmp/odd-33x19.png" \
    -frames:v 1 "$tmp/odd-33x19-ffmpeg.png"
"$ffmpeg" -hide_banner -loglevel error -y -i "$tmp/odd-33x19-ffmpeg.png" \
    -c:v libvpx -slices 1 -b:v 4M -frames:v 1 -f ivf "$tmp/odd-33x19.ivf"
python3 "$root/wrap-vp8-ivf.py" "$tmp/odd-33x19.ivf" \
    "$root/odd-33x19-libvpx.webp"

"$ffmpeg" -hide_banner -loglevel error -y -f lavfi \
    -i "testsrc2=size=320x240:rate=1" -frames:v 1 "$tmp/testsrc2-320x240.png"
cwebp -quiet "$tmp/testsrc2-320x240.png" -o "$root/testsrc2-320x240-q75.webp"
cwebp -quiet -q 75 -f 0 "$tmp/testsrc2-320x240.png" \
    -o "$root/testsrc2-320x240-f0.webp"
cwebp -quiet -q 75 -m 0 -f 100 -sharpness 3 -segments 1 \
    "$tmp/testsrc2-320x240.png" -o "$root/testsrc2-320x240-q75-f100-s3.webp"
cwebp -quiet -q 60 -segments 4 -sns 100 -f 60 \
    "$tmp/testsrc2-320x240.png" -o "$root/testsrc2-320x240-q60-segments4-sns100-f60.webp"

for quality in 0 5 50 95 100; do
    cwebp -quiet -q "$quality" -m 0 -f 0 -segments 1 \
        "$tmp/noise-32x32.png" -o "$root/noise-32x32-q$quality.webp"
done
