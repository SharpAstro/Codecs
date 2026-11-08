#!/usr/bin/env bash
# Generate the committed HEVC fixtures under Fixtures/hevc/ and their expected-output digests.
#
# Run this ONLY to add or regenerate fixtures; the tests need neither it nor any of its tools. It runs
# on Linux (WSL here: `wsl -e bash tests/SharpAstro.Codecs.Tests/Oracle/heif/make-fixtures.sh`) and
# needs `apt-get install ffmpeg libde265-examples`.
#
# Why these exist at all: the Canon pictures are what rung 2 is for, but their encoder uses DC and
# every fourth angle only (no planar), chroma modes 1/10/26 only (so the 4:2:2 remap of Table 8-3 is
# never exercised with a value it changes), one QP per slice, CTB 32, TBs up to 16 and no scaling-list
# DC. A decoder that got all of that wrong would still match ffmpeg on every Canon file. x265 with a
# full rate-distortion search uses every mode, and each fixture below crosses one more axis.
#
# Provenance: the inputs are ffmpeg's own synthetic sources, the encoder is x265 (GPL) run as a
# program, and its output is data. Same arrangement as the committed jbig2enc fixtures.
#
# x265 is not byte-reproducible from run to run even single-threaded (sizes move by a few bytes), so
# regenerating rewrites every fixture and its digest together; never one without the other.
#
# The expected output is ffmpeg's decode, and a fixture is committed only when libde265, an
# independent decoder, produces the same bytes: two decoders agreeing is the check that the expected
# output is the right one, not merely ffmpeg's.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../../Fixtures/hevc"
DIGESTS="$OUT/hevc-oracle-golden.tsv"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

command -v ffmpeg > /dev/null || { echo "[hevc fixtures] need ffmpeg (apt-get install ffmpeg)" >&2; exit 1; }
DEC265="$(command -v libde265-dec265 || command -v dec265 || true)"
[ -n "$DEC265" ] || { echo "[hevc fixtures] need dec265 (apt-get install libde265-examples)" >&2; exit 1; }
# Captured first: `| grep -q` would end the pipe early, and under pipefail ffmpeg's SIGPIPE fails the check.
ENCODERS="$(ffmpeg -hide_banner -encoders 2> /dev/null)"
case "$ENCODERS" in *libx265*) ;; *) echo "[hevc fixtures] this ffmpeg has no libx265" >&2; exit 1 ;; esac

mkdir -p "$OUT"
printf 'name\twidth\theight\tpix_fmt\tsha256_of_decoded_planes\n' > "$DIGESTS"

# Four sources in a 2x2 mosaic: fractal edges in every direction (all the angles), smooth gradients
# (planar, large blocks, strong smoothing), test bars with text (sharp horizontal and vertical edges),
# and the same bars under fixed-seed noise (big residuals, long coefficient codes).
mosaic() { # width height (each a multiple of 2)
    local w=$(( $1 / 2 )) h=$(( $2 / 2 ))
    echo "mandelbrot=s=${w}x${h}:end_scale=0.01[a];gradients=s=${w}x${h}:seed=7[b];testsrc2=s=${w}x${h}[c];testsrc2=s=${w}x${h},noise=alls=40:allf=t:all_seed=12345[d];[a][b][c][d]xstack=inputs=4:layout=0_0|w0_0|0_h0|w0_h0"
}

# An HM-format scaling list file (x265 --scaling-list): every matrix a ramp, so no two coefficients
# share a weight by accident, and DC values far from the first entries. Every matrix is also DISTINCT
# (its own offset): x265 3.5 predicts an identical list from an earlier one and writes a
# scaling_list_pred_matrix_id_delta that ffmpeg refuses as out of range ("Invalid delta ... 9").
lists() {
    local size kind comp n i offset=0
    # Size outermost, then intra/inter: x265 reads the file in one pass, in HM's order, and a matrix
    # written out of that order is never found ("can't read matrix").
    for size in 4 8 16 32; do
        for kind in INTRA INTER; do
            for comp in LUMA CHROMAU CHROMAV CHROMAU_FROM16x16_CHROMAU CHROMAV_FROM16x16_CHROMAV; do
                case "$size$comp" in 4CHROMA*_FROM*|8CHROMA*_FROM*|16CHROMA*_FROM*) continue ;; esac
                n=$(( size == 4 ? 16 : 64 ))
                offset=$(( offset + 1 ))
                echo "${kind}${size}X${size}_${comp} ="
                for ((i = 0; i < n; i++)); do printf '%d,' $(( 8 + offset + i * 3 / 2 + (size == 4 ? 0 : size / 4) )); [ $(( (i + 1) % 8 )) -eq 0 ] && echo; done
                [ "$n" -eq 16 ] && echo
                if [ "$size" -ge 16 ]; then echo "${kind}${size}X${size}_${comp}_DC ="; echo "$(( 40 + size + offset ))"; fi
            done
        done
    done
}

# One intra (IDR) picture, with everything rung 2 refuses switched off; per-fixture parameters follow.
BASE="log-level=error:keyint=1:no-open-gop=1:no-sao=1:no-deblock=1:no-signhide=1:no-wpp=1:no-info=1:rd=6:rdoq-level=2:frame-threads=1:pools=none"

fixture() { # name width height pix_fmt rate-control x265-params [source-size]
    local name="$1" width="$2" height="$3" format="$4" rate="$5" params="$6" source="${7:-$2x$3}"
    local sw="${source%x*}" sh="${source#*x}"
    ffmpeg -hide_banner -v error -y -filter_complex "$(mosaic "$sw" "$sh"),scale=${width}:${height}:flags=bicubic,format=$format" \
        -frames:v 1 -c:v libx265 $rate -x265-params "$BASE:$params" -f hevc "$WORK/$name.hevc"
    ffmpeg -hide_banner -v error -y -f hevc -i "$WORK/$name.hevc" -frames:v 1 -f rawvideo -pix_fmt "$format" "$WORK/$name.ffmpeg.yuv"
    "$DEC265" -q -o "$WORK/$name.de265.yuv" "$WORK/$name.hevc" > /dev/null 2>&1 || true
    if ! cmp -s "$WORK/$name.ffmpeg.yuv" "$WORK/$name.de265.yuv"; then
        echo "[hevc fixtures] ERROR: ffmpeg and libde265 disagree on $name; not committing it." >&2
        exit 1
    fi
    cp "$WORK/$name.hevc" "$OUT/$name.hevc"
    printf '%s\t%s\t%s\t%s\t%s\n' "$name" "$width" "$height" "$format" "$(sha256sum < "$WORK/$name.ffmpeg.yuv" | cut -d' ' -f1)" >> "$DIGESTS"
    echo "[hevc fixtures] $name: $(stat -c %s "$OUT/$name.hevc") bytes, ffmpeg and libde265 agree"
}

# 4:2:2 10-bit, the Canon format, with every mode, CTB 64, 32x32 transforms and a deep transform tree.
fixture x265-422p10-modes 256 192 yuv422p10le "-crf 22" "ctu=64:min-cu-size=8:max-tu-size=32:tu-intra-depth=3"
# Strong adaptive quantisation (cu_qp_delta in 16x16 groups), the default scaling lists, chroma QP offsets.
# (Several slices are covered by the Canon pictures: x265 3.5 writes them only with WPP, which is refused.)
fixture x265-422p10-aq-scaling 256 192 yuv422p10le "-crf 26" "ctu=32:aq-mode=2:aq-strength=1.5:qg-size=16:scaling-list=default:cbqpoffs=-3:crqpoffs=2"
# 4:2:0 8-bit at a size that is not a multiple of 8 (a conformance window) and codes as 232x168, so the
# right and bottom CTBs are partial (split inferred at the picture edge); without strong smoothing.
fixture x265-420p8-window 230 166 yuv420p "-crf 30" "ctu=32:no-strong-intra-smoothing=1" 232x168
# 4:2:0 10-bit at a very low QP: large levels, long coeff_abs_level_remaining codes, CTB 16, the deepest transform tree it allows.
fixture x265-420p10-lowqp 128 96 yuv420p10le "-qp 2" "ctu=16:min-cu-size=8:tu-intra-depth=3"
# 4:2:0 10-bit with QPs spread across the 4:2:0 chroma table (Table 8-10, qPi 30..43) by strong
# adaptive quantisation in 8x8 groups and chroma offsets, and CODED scaling lists whose 16x16 and 32x32
# DC values differ from the lists' first entries (with the default lists the DC is 16 either way, so
# a decoder that ignored it still matched; the mutation check found that).
lists > "$WORK/lists.txt"
fixture x265-420p10-chromaqp-lists 256 192 yuv420p10le "-crf 37" "ctu=32:min-cu-size=8:aq-mode=2:aq-strength=3:qg-size=8:cbqpoffs=7:crqpoffs=-3:scaling-list=$WORK/lists.txt"
# 4:2:2 12-bit: the widest bit depth rung 2 accepts (QpBdOffset 24, a 20 - 12 = 8 bit final shift).
fixture x265-422p12 128 96 yuv422p12le "-crf 20" "ctu=32:min-cu-size=8"
# 4:2:2 8-bit, CTB 16, mid QP.
fixture x265-422p8-ctu16 128 96 yuv422p "-crf 18" "ctu=16:min-cu-size=8:max-tu-size=16:tu-intra-depth=2"

echo "[hevc fixtures] wrote $(($(wc -l < "$DIGESTS") - 1)) fixtures and $DIGESTS"
