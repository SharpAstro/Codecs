#!/usr/bin/env bash
# Fetch one photograph in several formats, HDR and SDR, from aaron-rust/hdr-sample-images
# (different_file_formats_in_sdr_and_hdr, CC0-1.0). Two 16-bit PNGs exported from Lightroom Classic are
# the masters (1A: BT.2020 PQ with a "Rec. 2020 PQ" ICC profile and a cICP chunk; 1B: BT.709 sRGB); every
# other file was encoded from them with the stock tools, per the folder's "Encoding Commands.txt":
#   AVIF   avifenc -q 90 / -q 85         HEIC   heif-enc (libheif), ICC profile only, no nclx
#   JXL    cjxl                          JPEG   magick (SDR only)
#   TIFF   magick, the PNG's samples and ICC profile unchanged
# So every decode of the same letter (A HDR, B SDR) must agree with its PNG: exactly where the encoder is
# lossless, closely where it is not, and always on the colour it reports.
#
# Kept out of git for size alone (CC0 permits committing them): downloaded into Fixtures/hdr-samples/
# (git-ignored, copied to the test output by the Fixtures/** content glob). Tests that use them skip when
# a file is absent.
#
# The files sit in Git LFS, so every download spends the owner's LFS bandwidth. CI therefore restores
# Fixtures/hdr-samples/ from actions/cache keyed on this script's hash and runs this only on a miss; a
# file already present is hash-checked and never fetched again. Change the pins only on purpose.
#
# Default set (what CI caches, ~460 MB): the two PNG masters, the two HEICs, the two JXLs (header and
# permuted TOC; their VarDCT frames do not decode here yet), the JPEG. --all adds the TIFFs (lossless
# copies of the PNGs, 545 MB, their colour also covered by a synthetic test) and the AVIFs (no decoder
# here). Move a pair into the default set once a test uses it.
#
# Each file is pinned by commit and SHA-256. A mismatch means upstream changed, and the script stops.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/Fixtures/hdr-samples"
mkdir -p "$OUT"

BASE="https://media.githubusercontent.com/media/aaron-rust/hdr-sample-images/df0d5df406ad0dbe2124d6f2a3700fe73dd3ead7/different_file_formats_in_sdr_and_hdr"

# name | sha256
FILES=(
    "1A_PNG_HDR.png|d6bb2746a7e885b5c8e7ec8bbfe59b848e2dfa266fa59afae8429ac0b188eef6"
    "1B_PNG_SDR.png|0a232fbf353f118874604eaafe6816636ee4a6b0f1177c67cfd9b3b7e1fe033a"
    "3A_HEIC_HDR.heic|28bd0e388fad6c695550e6201dcee3cf3a453810f19a2fbed434940bd4f7433c"
    "3B_HEIC_SDR.heic|0e366a983db397b1c1fbcb638e51ecbb6f2163f03f1e652f62ec8e192d96ce6e"
    "4A_JPEG-XL_HDR.jxl|40e765108a520fa63f5d7553d39e59ee38a5033dcd30cb2004730207051cdfd0"
    "4B_JPEG-XL_SDR.jxl|56c09ec507b7bb431ecd9b0c9b6d0e051e3d389d6046ebaa9c575c5dc33126e0"
    "5B_JPEG_SDR.jpg|b4d013555225a431fb744d44863db529c9b34ba90843842a4b7aa450857bd6c9"
)
EXTRA=(
    "6A_TIFF_HDR.tiff|00331a5f21df33877585b9a5614bcb01694e2acb99ebca5730ef08ed7706cacd"
    "6B_TIFF_SDR.tiff|9f31b99a8fac9b851f23066e626bd2ff9c1879dcf41c78cfa4296ceb7b0a25ee"
    "2A_AVIF_HDR.avif|97d0dd6e29c565db6bfc21a4cdfdfd9af7b5d46c1538d15e11e715f9c4bc3522"
    "2B_AVIF_SDR.avif|541e4ff0404ae616d765734869eaecb88923fd74a0faf80e34bdc0955a0e436f"
)
if [ "${1:-}" = "--all" ]; then FILES+=("${EXTRA[@]}"); fi

for entry in "${FILES[@]}"; do
    IFS='|' read -r name sha <<< "$entry"
    target="$OUT/$name"
    if [ ! -f "$target" ]; then
        echo "[hdr samples] downloading $name"
        # A download takes its final name only once its .part has passed the pin.
        rm -f "$target.part"
        curl -sSfL "$BASE/$name" -o "$target.part"
        if ! echo "$sha *$target.part" | sha256sum -c - > /dev/null 2>&1; then
            rm -f "$target.part"
            echo "[hdr samples] SHA-256 mismatch for the download of $name; refusing to use it." >&2
            exit 1
        fi
        mv "$target.part" "$target"
    fi
    if ! echo "$sha *$target" | sha256sum -c - > /dev/null 2>&1; then
        rm -f "$target"
        echo "[hdr samples] SHA-256 mismatch for $name; removed, run again to fetch it." >&2
        exit 1
    fi
    echo "[hdr samples] $name ok"
done
exit 0
