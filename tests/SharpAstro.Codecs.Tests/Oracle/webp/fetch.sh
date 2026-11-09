#!/usr/bin/env bash
# Fetch libwebp's reference tools (dwebp, cwebp) for the WebP oracle tests.
# See README.md for context.
#
# Downloads the official libwebp release archive for this host, verifies its
# SHA-256, and installs ONLY dwebp and cwebp into the ignored bin/ directory.
# Nothing else from the archive is kept or run. libwebp is BSD-3
# (notice-retaining), so these are black-box oracle binaries: running a program
# is not linking to it, and its source is never read or copied into
# SharpAstro.Webp.
#
# The Windows x64 build also runs on win-arm64 under emulation.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BIN="$HERE/bin"

# Pinned, for the same reason the OpenJPEG and jxrlib pins are: an oracle that
# upstream can move can redefine "correct" underneath a green build. The
# archives carry no upstream checksum file, so these SHA-256s were taken from
# the downloaded assets at pin time and are the contract -- a mismatch means the
# release was re-cut or tampered with, and the right move is to stop.
VERSION=1.6.0
BASE="https://storage.googleapis.com/downloads.webmproject.org/releases/webp"

case "$(uname -s)" in
    Linux*)
        ASSET="libwebp-$VERSION-linux-x86-64.tar.gz"
        ASSET_SHA256=1c5ffab71efecefa0e3c23516c3a3a1dccb45cc310ae1095c6f14ae268e38067
        EXT=""
        ;;
    MINGW*|MSYS*|CYGWIN*)
        ASSET="libwebp-$VERSION-windows-x64.zip"
        ASSET_SHA256=48886f506b21f62e4661f0f4cbfca19800897c385128e8902542d29a950c93f1
        EXT=".exe"
        ;;
    *)
        echo "[webp oracle] no pinned libwebp build for $(uname -s)." >&2
        echo "[webp oracle] add the asset + SHA-256 here, or install dwebp/cwebp yourself" >&2
        echo "[webp oracle] and point DWEBP / CWEBP at them." >&2
        exit 1
        ;;
esac
TOP="${ASSET%.tar.gz}"
TOP="${TOP%.zip}"

# A fresh, empty directory per run: the archive and its unpacked members never
# touch the working tree, and nothing is run from inside it except the two
# binaries once they are installed.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "[webp oracle] downloading $ASSET"
curl -sSfL --retry 5 --retry-delay 15 --retry-all-errors "$BASE/$ASSET" -o "$WORK/$ASSET"
echo "$ASSET_SHA256 *$WORK/$ASSET" | sha256sum -c -

# Extract only the two tools, by exact member name.
mkdir "$WORK/unpacked"
MEMBERS=("$TOP/bin/dwebp$EXT" "$TOP/bin/cwebp$EXT")
case "$ASSET" in
    *.tar.gz) tar xzf "$WORK/$ASSET" -C "$WORK/unpacked" "${MEMBERS[@]}" ;;
    *.zip)    unzip -q "$WORK/$ASSET" "${MEMBERS[@]}" -d "$WORK/unpacked" ;;
esac

mkdir -p "$BIN"
for tool in dwebp cwebp; do
    cp "$WORK/unpacked/$TOP/bin/$tool$EXT" "$BIN/$tool$EXT"
    chmod +x "$BIN/$tool$EXT"
done

# Prove they run, and that they are the pinned version. `-version` prints the
# library version, e.g. `1.6.0`.
for tool in dwebp cwebp; do
    REPORTED="$("$BIN/$tool$EXT" -version 2>&1 | head -1 | tr -d '\r')"
    case "$REPORTED" in
        "$VERSION"*) ;;
        *)
            echo "[webp oracle] ERROR: $tool reported '$REPORTED', expected $VERSION." >&2
            exit 1
            ;;
    esac
done

echo "[webp oracle] ready: $BIN"
