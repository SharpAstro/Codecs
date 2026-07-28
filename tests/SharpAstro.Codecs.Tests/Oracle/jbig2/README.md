# JBIG2 oracles

Two independent implementations check `SharpAstro.Jbig2`, in opposite directions.
Neither is ever read as a port source — see the licence note at the bottom.

| Direction | Tool | Where it lives | Needs installing? |
|---|---|---|---|
| **jbig2enc → us** | `jbig2` (jbig2enc, Apache-2.0) | committed `Fixtures/jbig2/*.jb2`, driven by `Jbig2EncoderFixtureTests` | **No** — the fixtures are in the repo |
| **us → jbig2dec** | `jbig2dec` (Artifex, AGPL) | `Jbig2Oracle` + `Jbig2OracleTests` | Yes, else the tests skip |

They cover different things, which is the point of having both.

- jbig2enc only ever emits **GBTEMPLATE 0 with nominal AT pixels** (asserted by
  `JbigEncFixtures_UseTemplate0WithNominalAtPixels`, so a fixture refresh can't
  quietly change it). Real third-party bytes, but a narrow slice of the format.
- jbig2dec is driven with **our own** streams, so it reaches every template,
  moved AT pixels, and TPGDON either way — the cases jbig2enc cannot produce.

## Installing jbig2dec

```bash
apt-get install -y jbig2dec                      # Linux / CI
wsl -- sudo apt-get install -y jbig2dec          # Windows (no native build exists)
```

`Jbig2Oracle` looks for `jbig2dec` on PATH first, then falls back to
`wsl.exe -- jbig2dec`. `JBIG2DEC=<path>` overrides both — useful for a custom
build, and for exercising the failure branch below.

### Skip vs. fail

| Environment | jbig2dec present | jbig2dec missing |
|---|---|---|
| local (default) | tests run | **reported skips** (`Assert.Skip`) |
| CI (`REQUIRE_ORACLES=1`) | tests run | **build fails** |

Graceful skipping keeps a clean clone green, and in CI it is a liability — a job
that stops installing its oracle looks exactly like one that runs it. CI's test
step sets `REQUIRE_ORACLES=1` so that silence becomes a red build. The shared
entry point is `OracleGate.RequireOrSkip`; JBIG2 was the first harness on it.

All three external oracles (jbig2dec, jxrlib, jpegenc) are now gated and built
or installed in CI. JXR was the one that mattered most: its guards used to
`return` early, which counts as a **pass**, so 447 test cases had been reporting
success in CI while asserting nothing.

## Regenerating the committed fixtures

Only needed when changing the test pattern or refreshing against a newer
jbig2enc:

```bash
apt-get install -y jbig2
bash tests/SharpAstro.Codecs.Tests/Oracle/jbig2/make-fixtures.sh
```

The expected pattern is duplicated as an ASCII grid in
`Jbig2EncoderFixtureTests`; keep the two in sync.

## What the oracle actually catches

Worth being precise, because it is not what you might assume. Established by
deliberately breaking the decoder and watching which layer fails:

| Deliberate bug | Round-trip tests | One-hot template tests | jbig2dec oracle |
|---|:---:|:---:|:---:|
| Template reads a different **pixel** | pass | **fail** | **fail** |
| Two template **bits swapped** (same pixels) | pass | **fail** | pass |

A context value is only an index into adaptive-state slots that all start
identical, so permuting the bit positions preserves which pixels share a slot and
the coded bytes come out unchanged — a permuted decoder is still conformant.
What conformance really rests on is **which pixels the template reads**, and that
is what jbig2dec catches.

The literal numbering is still pinned to T.88 Figures 4-7 by the one-hot tests,
for one substantive reason: TPGDON's SLTP decision uses **hard-coded** contexts
(`0x9B25` and friends) that name a specific neighbourhood in the spec's
numbering, so permuting the real contexts changes which neighbourhood collides
with that slot.

Note also that the round-trip tests are structurally blind to all of this — the
test encoder forms contexts by calling the shipped decoder's own `Context`, so
both sides share any mistake. That is precisely why this directory exists.

## Licence

This repo is **Unlicense** (public domain), which is stricter than "permissive":
notice-retaining code cannot be relicensed into it, and AGPL code cannot go
anywhere near it.

- **jbig2dec** — AGPL. Usable **only** as an executable. Never read its source.
- **jbig2enc** — Apache-2.0. Same rule for its source; its *output* is data (a
  rendering of a pattern authored here) and carries no constraint, which is why
  the fixtures can be committed.

Running a program is not linking to it. `SharpAstro.Jbig2` itself is clean-room
from ITU-T T.88. See [`ROADMAP-pdf-codecs.md`](../../../../ROADMAP-pdf-codecs.md)
for the full licence matrix.
