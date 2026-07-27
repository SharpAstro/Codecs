#!/bin/bash
# Regenerates the committed JBIG2 fixtures in tests/SharpAstro.Codecs.Tests/Fixtures/jbig2/.
#
# These are real jbig2enc output — bytes this repo did not produce — and they are
# committed (they are well under 130 bytes each) so Jbig2EncoderFixtureTests runs
# in CI with no tooling installed. You only need this script when changing the
# test pattern or refreshing against a newer jbig2enc.
#
# Requires jbig2enc's CLI, which is packaged as `jbig2`:
#     apt-get install -y jbig2
# On Windows, run this inside WSL:
#     wsl -- bash tests/SharpAstro.Codecs.Tests/Oracle/jbig2/make-fixtures.sh
#
# Licence: jbig2enc is Apache-2.0. Its *source* could not be a port source for
# this Unlicense repo; its *output* is data — a rendering of a pattern authored
# here — and carries no such constraint. Same distinction as every other
# reference implementation in ROADMAP-pdf-codecs.md.
set -euo pipefail

if ! command -v jbig2 >/dev/null; then
    echo "error: jbig2 (jbig2enc) not found. apt-get install -y jbig2" >&2
    exit 1
fi

here=$(cd "$(dirname "$0")" && pwd)
out="$here/../../Fixtures/jbig2"
mkdir -p "$out"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work"

# The test pattern: a bordered box over a diagonal brick tiling. Chosen for a mix
# of long runs, short runs and edges rather than to look like anything. Keep this
# in sync with the Expected grid in Jbig2EncoderFixtureTests.
python3 - <<'PY'
w, h = 32, 16
with open('t.pbm', 'w') as f:
    f.write('P1\n%d %d\n' % (w, h))
    for y in range(h):
        row = [1 if (y in (0, h - 1) or x in (0, w - 1) or (x // 3 + y // 2) % 2 == 0) else 0
               for x in range(w)]
        f.write(' '.join(map(str, row)) + '\n')
PY

# jbig2enc's default coder is the generic region one — exactly what rung 1
# implements. -d turns on TPGD (it calls it "duplicate line removal"); -p emits
# the PDF-ready embedded stream instead of a standalone .jb2 file.
jbig2         t.pbm > "$out/s.jb2"        # standalone
jbig2 -d      t.pbm > "$out/s_tpgd.jb2"   # standalone, TPGD
jbig2 -p      t.pbm > "$out/e.jb2"        # embedded (PDF-shaped)
jbig2 -p -d   t.pbm > "$out/e_tpgd.jb2"   # embedded, TPGD

echo "Regenerated:"
ls -l "$out"

# Sanity check against the reference decoder when it happens to be installed.
# Not the real check — Jbig2EncoderFixtureTests is — but it catches a broken
# jbig2enc here rather than as a confusing C# assertion failure later.
if command -v jbig2dec >/dev/null; then
    for f in s s_tpgd; do jbig2dec -q -t pbm -o "rt_$f.pbm" "$out/$f.jb2"; done
    for f in e e_tpgd; do jbig2dec -q -e -t pbm -o "rt_$f.pbm" "$out/$f.jb2"; done
    echo "jbig2dec decoded all four fixtures without error"
fi
