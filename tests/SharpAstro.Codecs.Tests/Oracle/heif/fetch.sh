#!/usr/bin/env bash
# Fetch ffmpeg for the HEVC oracle tests on WINDOWS (HevcOracle resolves FFMPEG, then
# Oracle/heif/dist/bin, then PATH). On Linux, CI included, install the distribution's package
# instead: `apt-get install ffmpeg`. See README.md beside this script.
#
# Binary only: ffmpeg is LGPL/GPL and its source is never read; running it is fine, the line drawn
# around jbig2dec, OpenJPEG and libwebp. This is the LGPL build (no x265 encoder): the decoder is all
# the tests run. make-fixtures.sh encodes with x265 and runs on Linux.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DIST="$HERE/dist"

# Pinned to one dated BtbN autobuild, verified by SHA-256 (the digests GitHub publishes for these
# assets; the winarm64 one was also hashed locally and agrees). An HEVC decode is bit-exact by
# specification, so a newer ffmpeg cannot change a correct answer the way a newer OpenJPEG can move
# the lossy JPEG 2000 path; the pin is about reproducibility. BtbN prunes old autobuilds, so if the
# download 404s, move both the tag and the hashes to a current build together.
TAG=autobuild-2026-11-07-12-47
BASE="https://github.com/BtbN/FFmpeg-Builds/releases/download/$TAG"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) ;;
    *)
        echo "[hevc oracle] this script fetches the Windows build; on Linux use 'apt-get install ffmpeg'." >&2
        exit 1
        ;;
esac
case "$(uname -m)" in
    aarch64|arm64)
        ASSET=ffmpeg-n9.0.2-48-gd4070a14ce-winarm64-lgpl-9.0.zip
        ASSET_SHA256=239fc4c934ef0350928f68cc3d570668c3f9e70b40a31781b3789d7fa1df8e2d
        ;;
    *)
        ASSET=ffmpeg-n9.0.2-48-gd4070a14ce-win64-lgpl-9.0.zip
        ASSET_SHA256=7a347fb3220968b63fc7e025ed78370588726e0d0bf2bd0445d3f8f2387c36db
        ;;
esac

ARCHIVE="$HERE/$ASSET"
if [ ! -f "$ARCHIVE" ]; then
    echo "[hevc oracle] downloading $ASSET"
    curl -sSfL "$BASE/$ASSET" -o "$ARCHIVE"
fi
echo "$ASSET_SHA256 *$ARCHIVE" | sha256sum -c -

rm -rf "$DIST"
TMP="$HERE/.unzip-tmp"
rm -rf "$TMP"
unzip -q "$ARCHIVE" -d "$TMP"
mkdir -p "$DIST"
mv "$TMP"/*/* "$DIST/"
rm -rf "$TMP"

# Prove it runs and has the native HEVC decoder before declaring success.
DECODERS="$("$DIST/bin/ffmpeg.exe" -hide_banner -decoders 2>&1 || true)"
case "$DECODERS" in
    *" hevc "*) ;;
    *) echo "[hevc oracle] ERROR: $DIST/bin/ffmpeg.exe lists no hevc decoder." >&2; exit 1 ;;
esac
echo "[hevc oracle] ready: $DIST/bin"
