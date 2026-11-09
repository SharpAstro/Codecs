#!/usr/bin/env bash
# Build the jpegenc oracle for the SharpAstro.Jpeg encoder byte-exactness tests.
# Downloads a PINNED stb_image_write.h (byte-exactness depends on the exact
# reference version) and compiles jpegenc.exe with clang — no CMake/MSVC needed.
# See README.md for context.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BIN="$HERE/../bin"
mkdir -p "$BIN"

# Pinned to the exact revision JpegEncoder was ported against. If upstream ever
# changes the JPEG writer, bump both and re-freeze the golden.
STB_SHA=1ee679ca2ef753a528db5ba6801e1067b40481b8
STB_SHA256=cbd5f0ad7a9cf4468affb36354a1d2338034f2c12473cf1a8e32053cb6914a05
HDR="$HERE/stb_image_write.h"

# A cached header that fails its pin (a transfer that broke part-way, or an error page an older version of this
# script saved) would otherwise be skipped by the download and fail the hash for ever, so it is removed and
# fetched again.
if [ -f "$HDR" ] && ! echo "$STB_SHA256 *$HDR" | sha256sum -c - > /dev/null 2>&1; then
    echo "[jpegenc oracle] stb_image_write.h is stale or corrupt (SHA-256 mismatch); deleting it and downloading again"
    rm -f "$HDR"
fi
if [ ! -f "$HDR" ]; then
    echo "[jpegenc oracle] downloading pinned stb_image_write.h ($STB_SHA)"
    # The download goes to $HDR.part and only takes its final name once it has passed the pin, so a broken
    # transfer can never be mistaken for a cached header on the next run.
    rm -f "$HDR.part"
    # --fail and --retry: a server error is a download failure, retried, not a page that fails the hash.
    curl -fsSL --retry 5 --retry-delay 15 --retry-all-errors "https://raw.githubusercontent.com/nothings/stb/$STB_SHA/stb_image_write.h" -o "$HDR.part"
    if ! echo "$STB_SHA256 *$HDR.part" | sha256sum -c -; then
        rm -f "$HDR.part"
        echo "[jpegenc oracle] ERROR: the download of stb_image_write.h does not match its pinned SHA-256" >&2
        exit 1
    fi
    mv "$HDR.part" "$HDR"
fi
echo "$STB_SHA256 *$HDR" | sha256sum -c -

echo "[jpegenc oracle] building jpegenc.exe"
# -ffp-contract=off is load-bearing: clang on aarch64 (and any FMA target) would
# otherwise fuse the DCT/colour multiply-adds into single fma() ops with different
# rounding than the managed encoder's separate multiply+add. Off = the canonical,
# platform-independent stbiw result that JpegEncoder reproduces bit-for-bit.
clang -O2 -w -ffp-contract=off -I"$HERE" -o "$BIN/jpegenc.exe" "$HERE/jpegenc.c"

ls -la "$BIN/jpegenc.exe"
echo "[jpegenc oracle] done"
