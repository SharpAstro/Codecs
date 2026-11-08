#!/usr/bin/env bash
# Fetch real Canon HEIF/HEVC test files that are NOT committed to this repo (issue #66).
#
# These are camera files from third parties whose licence is not stated, and this repo is
# Unlicense, so like webp-inputs/external/ they are kept out of git: downloaded on demand into
# Fixtures/heif-external/ (git-ignored, and copied to the test output by the Fixtures/** content
# glob). They are also far over the 200 KB a committed fixture is kept under. Tests that use them
# must skip, not fail, when a file is absent.
#
# Each file is pinned by SHA-256, taken at pin time. A mismatch means the upstream file changed,
# and a test whose input can change underneath it proves nothing, so the script stops.
#
# A file with no URL cannot be fetched: copy it into Fixtures/heif-external/ by hand under the
# listed name; the script still checks its hash.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/Fixtures/heif-external"
mkdir -p "$OUT"

# name | url (empty = copy by hand) | sha256
FILES=(
    # Canon EOS R7 (DIGIC X), HEIF with HDR PQ: ftyp 'heix', primary item a 'grid' of 20 HEVC
    # tiles of 1792x960 composing 6960x4640, nclx BT.2020 / PQ / full range, 10-bit. Two HEVC
    # thumbnail items (320x214, 1620x1080), Exif and XMP. No JPEG anywhere in the file.
    # Shared by a user on lclevy/canon_cr3#26 (credit by username was asked and given; no licence).
    # 13,648,732 bytes.
    "canon-r7-hdrpq.hif|https://drive.usercontent.google.com/download?id=1tBMZB5qwwQ_M0k4p_f7WF5z9VgSUaqzR&export=download&confirm=t|e333f5d7c83d21fe7670909a2f316fe2d9f770d2072d39a9152399c4bdf06eb4"

    # Canon EOS R6 (DIGIC X) CR3 shot with HDR PQ on: the raw is Canon's CRX codec as usual, but
    # all three embedded previews (THMB 320 wide, PRVW ~1620x1080, the full-size preview track)
    # are HEVC with an hvcC box instead of JPEG. The smallest real Canon HEVC stills available,
    # one picture each, no grid. From DPReview's Canon EOS R6 sample gallery, original download of
    # photo 1108665959 (https://www.dpreview.com/samples/2123927289/canon-eos-r6-sample-gallery);
    # DPReview blocks scripted downloads, so no URL here. 18,885,714 bytes.
    "canon-r6-hdrpq.cr3||71346bedb7211061d785aac237fe529753fec51b87b7c83b5b518291b3339519"
)

status=0
for entry in "${FILES[@]}"; do
    IFS='|' read -r name url sha <<< "$entry"
    target="$OUT/$name"
    if [ ! -f "$target" ]; then
        if [ -z "$url" ]; then
            echo "[heif external] $name: no URL, copy it into $OUT by hand" >&2
            status=1
            continue
        fi
        echo "[heif external] downloading $name"
        curl -sSfL "$url" -o "$target.part"
        mv "$target.part" "$target"
    fi
    if ! echo "$sha *$target" | sha256sum -c - > /dev/null 2>&1; then
        echo "[heif external] SHA-256 mismatch for $name; refusing to use it." >&2
        # Only remove what can be fetched again: a hand-copied file has no URL to restore it from.
        if [ -n "$url" ]; then rm -f "$target"; fi
        exit 1
    fi
    echo "[heif external] $name ok"
done
exit $status
