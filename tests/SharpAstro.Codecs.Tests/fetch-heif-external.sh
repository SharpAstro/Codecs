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
# listed name; the script still checks its hash. Such a file that is absent is reported and skipped,
# not an error: the script exits 0 on a clean fetch, and only a download failure or a hash mismatch
# exits non-zero.
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

    # ---- Phone HEICs (issue #73; the gain-map ones also serve #70) ----------------------------
    # Unlike the Canon files these have a stated licence, but only the source repository's: each
    # is a test image committed to a repository under an open licence, with no per-file statement
    # from the photographer. They are still kept out of git (sizes, and the Canon files' rule
    # that nothing third-party lives here), and are pinned by commit so the URL cannot move.

    # Apple iPhone 13 Pro HDR photo with Apple's auxiliary HDR gain map
    # (auxC urn:com:apple:photo:2020:aux:hdrgainmap), ftyp 'heic'. The sample of johncf/apple-hdr-heic
    # (tests/data/hdr-sample.heic), MIT, (c) 2024 John Charankattu; stored in Git LFS, so the URL is
    # the media host, not raw. 238,982 bytes.
    "apple-iphone13pro-hdr-sample.heic|https://media.githubusercontent.com/media/johncf/apple-hdr-heic/e64716c29abc91a3b40543d7c47fb0f526608982/tests/data/hdr-sample.heic|c690fa4ecc6ae71ee9a926de869a95835a5d41e764ccbc200647e2fb9d0800cc"

    # Apple iPhone 13 Pro portrait-mode photo ("pug"): HDR gain map aux image plus a depth aux
    # image (urn:mpeg:hevc:2015:auxid:2), ftyp 'heic'. pillow_heif tests/images/heif_other/pug.heic
    # (commit "pug portrait with built-in depth and aux image"), BSD-3-Clause repository
    # (Pillow-Heif contributors). 1,299,081 bytes.
    "apple-iphone13pro-portrait-pug.heic|https://raw.githubusercontent.com/bigcat88/pillow_heif/558ee2bfbe85a64b4371bcc1bc067883c5f9573d/tests/images/heif_other/pug.heic|1ddce96766efbfa0f6bdf634bdab26bdeff831393df8d67f4cdc7b7a7afebc67"

    # Samsung Galaxy 200 MP photo with Samsung's HDR gain map aux image
    # (urn:com:samsung:photo:2024:aux:hdrgainmap), ftyp 'heic'. pillow_heif
    # tests/images/heif_special/200MP.heic, BSD-3-Clause repository. 2,407,522 bytes.
    "samsung-200mp-hdrgainmap.heic|https://raw.githubusercontent.com/bigcat88/pillow_heif/558ee2bfbe85a64b4371bcc1bc067883c5f9573d/tests/images/heif_special/200MP.heic|4dd7209010986a50b1049e54de47ae2188faef6d548213a9799e11fae1a317c6"

    # Samsung Galaxy photo with a gain map aux image AND an ISO 21496-1 'tmap' item (the file's
    # strings name both the Samsung aux type and 'tmap'), ftyp 'heic'. pillow_heif
    # tests/images/heif_special/aux_YCbCr.heic (added for YCbCr aux images, PR #355), BSD-3-Clause
    # repository. 1,172,244 bytes.
    "samsung-tmap-aux-ycbcr.heic|https://raw.githubusercontent.com/bigcat88/pillow_heif/558ee2bfbe85a64b4371bcc1bc067883c5f9573d/tests/images/heif_special/aux_YCbCr.heic|8deae321c3b6424f97ab5d57d022e0a79ac35278149c0de7796261b8981f50b0"

    # Xiaomi (Android) HEIC, ftyp 'heic' with 'mif1' compatible. pillow_heif
    # tests/images/heif_special/xiaomi.heic (kept for the large Xiaomi Exif block, pillow_heif #92),
    # BSD-3-Clause repository. 896,013 bytes.
    "xiaomi-android.heic|https://raw.githubusercontent.com/bigcat88/pillow_heif/558ee2bfbe85a64b4371bcc1bc067883c5f9573d/tests/images/heif_special/xiaomi.heic|cb5c7d9131931df4abd9bfe1c3de47617b68d1fae54a1c367fc5981ffea6aeed"
)

status=0
for entry in "${FILES[@]}"; do
    IFS='|' read -r name url sha <<< "$entry"
    target="$OUT/$name"
    if [ ! -f "$target" ]; then
        if [ -z "$url" ]; then
            # Not a failure: the file cannot be fetched at all, and the tests that use it skip visibly when
            # it is absent, so a clean fetch must still exit 0. A file that IS present is still hash-checked.
            echo "[heif external] $name: no URL, skipped; copy it into $OUT by hand to run its tests (they skip without it)" >&2
            continue
        fi
        echo "[heif external] downloading $name"
        # A download takes its final name only once its .part has passed the pin, so a broken transfer is
        # never left behind as a cached file.
        rm -f "$target.part"
        curl -sSfL "$url" -o "$target.part"
        if ! echo "$sha *$target.part" | sha256sum -c - > /dev/null 2>&1; then
            rm -f "$target.part"
            echo "[heif external] SHA-256 mismatch for the download of $name; refusing to use it." >&2
            exit 1
        fi
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
exit "$status"
