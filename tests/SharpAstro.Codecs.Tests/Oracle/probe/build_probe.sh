#!/usr/bin/env bash
# Build a golden-vector probe against jxrlib and run it. The probe (<name>.c)
# calls jxrlib's real transform / overlap / quant functions on fixed inputs and
# prints the outputs, which get baked into the C# tests as known-answer vectors.
#
# Usage:  bash build_probe.sh [transform_probe.c]
#
# Requires the jxrlib source (Oracle/jxrlib-src/, fetched by ../build.sh) and a
# clang on PATH. Throwaway dev tool; the .exe is git-ignored.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$HERE/../jxrlib-src"
PROBE_SRC="${1:-$HERE/transform_probe.c}"
OUT="$HERE/$(basename "${PROBE_SRC%.c}").exe"

if [ ! -d "$SRC_DIR" ]; then
    echo "[probe] jxrlib-src missing — run ../build.sh first" >&2
    exit 1
fi

CFLAGS="-DWIN32 -include wmsal.h -I. -Icommon/include -Iimage/sys -Ijxrgluelib -Ijxrtestlib -D__ANSI__ -DDISABLE_PERF_MEASUREMENT -w -O2"

# Same library translation units as ../build.sh (minus the app mains) so any
# non-static jxrlib function links cleanly.
SRCS=(
    image/sys/adapthuff.c image/sys/image.c image/sys/strcodec.c
    image/sys/strPredQuant.c image/sys/strTransform.c image/sys/perfTimerANSI.c
    image/decode/decode.c image/decode/postprocess.c image/decode/segdec.c
    image/decode/strdec.c image/decode/strInvTransform.c image/decode/strPredQuantDec.c
    image/decode/JXRTranscode.c
    image/encode/encode.c image/encode/segenc.c image/encode/strenc.c
    image/encode/strFwdTransform.c image/encode/strPredQuantEnc.c
    jxrgluelib/JXRGlue.c jxrgluelib/JXRMeta.c jxrgluelib/JXRGluePFC.c
    jxrgluelib/JXRGlueJxr.c
    jxrtestlib/JXRTest.c jxrtestlib/JXRTestBmp.c jxrtestlib/JXRTestHdr.c
    jxrtestlib/JXRTestPnm.c jxrtestlib/JXRTestTif.c jxrtestlib/JXRTestYUV.c
)

cd "$SRC_DIR"
echo "[probe] building $(basename "$PROBE_SRC") -> $OUT"
clang $CFLAGS -o "$OUT" "${SRCS[@]}" "$PROBE_SRC"
echo "[probe] running:"
"$OUT"
