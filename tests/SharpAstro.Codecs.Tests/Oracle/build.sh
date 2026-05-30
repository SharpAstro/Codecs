#!/usr/bin/env bash
# Build JxrDecApp / JxrEncApp from jxrlib for the oracle tests.
# See README.md for context.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/jxrlib-src"
BIN="$HERE/bin"
mkdir -p "$BIN"

if [ ! -d "$SRC" ]; then
    echo "[oracle] cloning jxrlib into $SRC"
    git clone --depth 1 https://github.com/4creators/jxrlib.git "$SRC"
fi

# Patch image/sys/ansi.h so UINTPTR_T sizes correctly on Windows x64 / arm64.
# Upstream only checks __LP64__ which is UNIX-only; without this, strcodec.h's
# CT_ASSERT(sizeof(UINTPTR_T) == sizeof(void*)) fails to compile on Windows.
ANSI="$SRC/image/sys/ansi.h"
if grep -q '^#if __LP64__$' "$ANSI"; then
    echo "[oracle] patching $ANSI for Windows 64-bit"
    # Replace the LP64-only guard with one that also catches Windows / aarch64.
    python -c "
import sys
p = sys.argv[1]
src = open(p, 'r', encoding='utf-8', errors='replace').read()
src = src.replace(
    '#if __LP64__\n#define UINTPTR_T unsigned long long\n#define INTPTR_T long long\n#else\n#define UINTPTR_T unsigned int\n#define INTPTR_T int\n#endif',
    '#if defined(__LP64__) || defined(_WIN64) || defined(__aarch64__) || defined(_M_X64) || defined(_M_ARM64)\n#define UINTPTR_T unsigned long long\n#define INTPTR_T long long\n#else\n#define UINTPTR_T unsigned int\n#define INTPTR_T int\n#endif'
)
open(p, 'w', encoding='utf-8').write(src)
" "$ANSI"
fi

CFLAGS="-DWIN32 -include wmsal.h -I. -Icommon/include -Iimage/sys -Ijxrgluelib -Ijxrtestlib -D__ANSI__ -DDISABLE_PERF_MEASUREMENT -w -O2"

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

cd "$SRC"

echo "[oracle] building JxrDecApp.exe"
clang $CFLAGS -o "$BIN/JxrDecApp.exe" "${SRCS[@]}" jxrencoderdecoder/JxrDecApp.c

echo "[oracle] building JxrEncApp.exe"
clang $CFLAGS -o "$BIN/JxrEncApp.exe" "${SRCS[@]}" jxrencoderdecoder/JxrEncApp.c

ls -la "$BIN"/Jxr*App.exe
echo "[oracle] done"
