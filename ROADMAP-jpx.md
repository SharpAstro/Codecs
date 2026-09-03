# Roadmap: JPEG 2000 (PDF's `/JPXDecode`)

> **Status: rung 1 of 5 shipped, in 3.12.** This file was written on 2026-09-03 as an
> uncommitted working note, offering itself to be committed, folded into
> [`ROADMAP-pdf-codecs.md`](ROADMAP-pdf-codecs.md) or deleted once the work started.
> The work started, so it is committed, and it is now kept current: the rung table
> below carries what landed, and the hazard list records which hazards the
> implementation actually met and which the tests are measurably blind to.
>
> The package is **`SharpAstro.Jpeg2000`**, not `SharpAstro.Jpx` — see "Open decisions"
> below, all four of which are now settled or narrowed.

## Where the gate actually stands

`ROADMAP-pdf-codecs.md` sets the gate as: JBIG2 shipped, **and** the PDF-embedded-stream
API shape has survived contact with a real consumer. The first half is met. The second is
not, and it is worth knowing precisely why, because closing it is small:

- The wiring exists. `PdfLibDocumentView.cs:897` in drawboard/pdf-viewer calls
  `Jbig2ImageDecode.TryDecode` on the draw path, and `PDF.Lib` carries the out-of-band
  pieces (`PdfObjectResolver.GetJbig2Globals` into `ImageData.Jbig2Globals`).
- No document has ever gone through it. `src/PDF.Lib.Tests/Assets/` holds no JBIG2
  fixture, and the only JBIG2 tests there are negative ones (non-JBIG2 bytes return null)
  plus a disk-cache round trip. So the API has been exercised by synthetic byte arrays
  and never by a scanned page.

**Closing it: put one real scanned JBIG2 PDF in `PDF.Lib.Tests/Assets/`.** That directory
auto-enrols into the render corpus, so the fixture pays for itself twice. It is hours of
work, and it is the only remaining evidence that the embedded-stream contract this whole
family now depends on is right.

> **Still open.** The note said to do this *before* JPEG 2000 rather than alongside it, and
> that is not what happened — rungs 0 and 1 landed first, by explicit decision, and this
> gate is recorded as open rather than quietly treated as closed. It lives in a different
> repository (`drawboard/pdf-viewer`) with a different commit identity, which is most of why
> it slipped. It is still worth doing before rung 4, which is where this decoder grows its
> own PDF entry point and would otherwise be the *second* unvalidated embedded-stream
> contract in the family.

## What already exists and transfers

Four things are done that were open when JBIG2 started, and they are most of the reason
JPX is a smaller standing start than its LOC count suggests.

- **The MQ coder is built, and it is the right one.** T.88 Annex E and T.800 Annex C
  specify the same coder with the same `Qe` table, pinned to the published T.88 Annex H.2
  conformance vector in both directions. Its own comment used to say it was kept `internal`
  "until a second consumer"; JPEG 2000 is that consumer, so it now lives in
  **`SharpAstro.Codecs.Abstractions/MqDecoder.cs`**, shared by both codecs. See hazard 2:
  sharing the coder is not the same as sharing its initialisation.
- **The oracle pattern is established and CI-proven.** `tests/SharpAstro.Codecs.Tests/Oracle/`
  clones an upstream reference at a pinned commit and clang-builds it, with no CMake and
  no MSVC toolset (`build.sh` does this for jxrlib). Three sub-harnesses sit beside it
  already: `jbig2`, `jpegenc`, `probe`. `OracleGate` skips visibly when a binary is
  missing, and CI sets `REQUIRE_ORACLES=1` so a missing oracle reddens the build instead
  of quietly passing.
- **The licence question is settled.** Clean-room from ITU-T T.800. OpenJPEG is BSD-2, so
  it is a notice-retaining licence and cannot be a port source into an Unlicense repo,
  but `opj_compress` / `opj_decompress` are ideal oracle binaries. PDFium and pdf.js are
  the same: binaries yes, source no. The MQ patents expired long ago.
- **The API contract question does not recur.** It dominated JBIG2 planning and JPEG 2000
  is the easy case: JP2 and raw J2K both have magic bytes, so it *can* register in the
  `ImageCodecs` facade like any other codec. JP2 signature box
  `00 00 00 0C 6A 50 20 20 0D 0A 87 0A`; raw codestream SOC+SIZ `FF 4F FF 51`.
  (Rung 1 nonetheless does **not** register — being able to and being ready to are
  different questions, and a facade entry would advertise more than one rung delivers.)

## The thing that does not transfer: JBIG2's staging strategy

This is the most important paragraph here. JBIG2's rung 1 was "MQ decoder plus generic
region", and it decoded real PDFs on its own, because JBIG2's region types are genuinely
independent of one another. **JPEG 2000 has no such slice.** Producing a single correct
pixel requires marker parsing, tier-2 packet headers, tier-1 EBCOT block decoding, the
inverse DWT and the DC level shift, all working together. There is no ordering of the
pipeline stages in which an early stop produces an image.

So the rungs below stage along **feature axes, not pipeline stages**: rung 1 builds the
entire pipeline but only for the simplest legal configuration, and later rungs widen what
that pipeline accepts. Do not try to reproduce JBIG2's shape here. It leads to four rungs
that each decode nothing, with no way to tell which of them is wrong.

The practical consequence: rung 1 is large and its first passing end-to-end test comes
late. Budget for that, and lean harder on layer-1 component tests (below) than JBIG2
needed to, because they are the only signal available until the whole core closes.

## The rungs

Calibration against what this repo has actually shipped: `SharpAstro.Exif` 558 LOC,
`SharpAstro.Png` 1554, `SharpAstro.Jbig2` **3354** (against an estimate of 3300 to 4000
across its five rungs, so the estimating method here is calibrated), `SharpAstro.Jxr`
10949.

| Rung | Scope | Est. LOC | Actual |
|---|---|---|---|
| 0 | **Oracle and corpus, no decoder code.** Get `opj_decompress` and `opj_compress` running and committed fixtures generated, before a line of decoder exists. | ~150 (scripts + harness) | ✅ **~370** — `Oracle/jpeg2000/{fetch.sh,make-fixtures.sh,gen-sources.py,README.md}`, `OpenJpegOracle`, `Pnm`, 13 fixtures. Over estimate because the fixtures verify themselves lossless before committing, and because the README records the probe answers. |
| 1 | **The irreducible core, constrained.** Markers SOC/SIZ/COD/QCD/SOT/SOD/EOC; tier-2 for one tile, one quality layer, maximal precincts, LRCP, with tag trees; tier-1 EBCOT (three passes per bit-plane, the context tables, MQ); 5/3 reversible inverse DWT; DC level shift. Restricted to: single tile, single layer, no subsampling, one component, 8-bit unsigned, raw J2K with no JP2 box. | ~3000-4500 | ✅ **2400** — under estimate, and the reason is worth knowing: the constrained envelope removes more than it looks like. One tile, one layer and one precinct means no precinct iteration, no progression-order machinery and no cross-layer state, which is most of what makes tier-2 big. Rung 3 gets that back. 16-bit came free, so the envelope is 8-**to-16**-bit. |
| 2 | **Colour and the lossy path.** RCT and ICT component transforms, the 9/7 irreversible DWT, dequantisation from QCD (exponent/mantissa, guard bits), multiple components. Most JPX inside real PDFs is 9/7 plus ICT, so this is the rung that makes the package useful rather than merely correct. | ~800-1200 | |
| 3 | **Tier-2 in full.** Multiple tiles and tile-parts, precincts, multiple quality layers, all five progression orders (LRCP/RLCP/RPCL/PCRL/CPRL), POC, packed headers (PPM/PPT), SOP/EPH, and the COC/QCC per-component overrides. This is where real encoder output stops resembling rung 1's constrained case. | ~1200-1800 | |
| 4 | **JP2 container and the PDF rules.** Box parsing (`jP  `, `ftyp`, `jp2h` with `ihdr`/`colr`/`pclr`/`cmap`/`cdef`, `jp2c`), palette, channel definition and alpha. Then PDF's own two: `SMaskInData`, and the rule that a JPX codestream may override the PDF `/ColorSpace`. | ~500-800 | |
| 5 | **Resolution-level decode (free 1/2^n LOD).** Stop tier-2 at resolution level *r* and run that many DWT levels. Mostly a matter of not doing work. | ~200-400 | |

Refused, and say so in the package the way `SharpAstro.Jbig2` refuses SDHUFF: **encoding**
(a non-goal for the whole family), **ROI, the RGN marker and MAXSHIFT**, **JPEG 2000
part 2 (JPX proper, JPM)**, and **component bit depths above 16 or signed components**
until something asks. PDF requires only part 1. Throw `NotSupportedException` naming the
feature rather than returning a plausible raster, which is the discipline that has held
across this repo.

Note the sum (about 5700 to 8700) lands below the 8 to 15k headline in
`ROADMAP-pdf-codecs.md`. The difference is the refusals above, mostly ROI and part 2. If
those come back, so does the headline number.

## Rung 5 deserves a note

`ROADMAP-pdf-codecs.md` calls the LOD property "the single most attractive property of the
format" for the pdf-viewer use case, and it is right: resolution levels fall out of the
codestream structure, where `SharpAstro.Jpeg`'s scaled decode had to be bolted onto the
transform as DCT-domain decimation. But it is last in the table on purpose. Tier-2 already
iterates resolution levels for rung 3; rung 5 is the flag that makes it stop early, and
implementing it before tier-2 is complete means writing the iteration twice.

## Validation plan

The repo's three-layer discipline (`CLAUDE.md`), with one change forced by the format and
one opportunity JBIG2 did not have.

1. **Golden-vector component tests.** T.800 ships worked examples; bake them in, the way
   the T.88 Annex H.2 sequence is baked in today. Spec-derived, so no licence
   contamination. This layer matters more here than it did for JBIG2, because it is the
   only signal that exists before rung 1's core closes. Target at minimum: the MQ context
   initialisation table, the tier-1 context assignment for each of the three passes, the
   tag-tree decoder, and one hand-computed 5/3 lifting row.
   → **Done, all four**, in `Jpeg2000ComponentTests`. One refinement learned in the doing:
   the context tests pin *which neighbours a context reads*, and deliberately not *what
   number it is given* — contexts are indices into adaptive slots that all start identical
   bar three, so a permutation of the numbering is invisible to the coded bytes. That is
   the JBIG2 template-numbering finding, transferred.
2. **Self round-trip is unavailable** (decode-only), so property tests on synthetic
   codestreams stand in, the `Jbig2StreamBuilder` / `LosslessJpegTests` pattern. Carry
   forward the caveat learned there: a synthetic builder that forms its contexts using the
   decoder's own code validates integration, scan order and the packet layer, and never
   validates the tables.
3. **Oracle pixel-match against `opj_decompress`.** Here is the change from JBIG2: that
   package's bilevel output allowed exact match with no tolerance, and JPEG 2000 does not —
   though rung 1 turned out to get the exact case for free, because a lossless codestream's
   expected output is the encoder's own input and needs no oracle at test time at all.
   **5/3 reversible is exact and must be asserted exact.** 9/7 irreversible is
   irrational-coefficient lifting, so it is a tolerance, and the tolerance should come
   from T.803 (part 4) conformance rather than being invented. Splitting the assertion
   this way is not a detail: an exact-match test on the reversible path is the sharpest
   tool available on this format, and burying it under a global tolerance throws it away.
4. **Foreign-encoder bytes, and this time they are plentiful.** JBIG2 was constrained by
   jbig2enc emitting only GBTEMPLATE 0 with nominal AT, which is why a fourth layer had
   to be invented out of libtiff. `opj_compress` has no such limit: it takes progression
   order (`-p`), tiles (`-t`), precincts (`-c`), code-block size (`-b`), decomposition
   levels (`-n`), quality layers (`-r`) and reversible/irreversible (`-I`) as options.
   Generate the matrix rather than hoping fixtures cover it. This is the single biggest
   validation advantage JPX has over JBIG2.

**Remember what the JBIG2 oracle actually taught**, because it applies directly. Breaking
a context template's bit *numbering* left jbig2dec still agreeing, while moving one
template *pixel coordinate* diverged immediately: contexts are only indices into adaptive
slots that all start identical, so a permutation is invisible. Expect the same here, and
design the tier-1 tests to pin which coefficients a context reads rather than what number
the context is given.

## Hazards, recorded before they cost a day each — and what they actually cost

Each was written down before rung 1 began. The verdicts are what happened.

1. **The MQ coder is shared; its initialisation is not.** T.800 uses 19 contexts with its
   own initial-index table (context 0 starts at index 4, RUNLENGTH at 3, UNIFORM at 46),
   which is not T.88's setup. Same `Qe` table, different starting state. Getting this
   wrong decodes the first code-block plausibly and then drifts, which is the worst
   possible failure signature.
   → **Paid off.** Implemented right first time *because* it was written down here, and
   now pinned by `Jpeg2000ComponentTests.MqTable_IsSharedBetweenT88AndT800`. The mutation
   check confirms the suite catches the T.88 initialisation: 12 fixtures fail.

2. **Decide where `MqDecoder` lives before rung 1, not during.**
   → **Settled first, as instructed.** It moved to `SharpAstro.Codecs.Abstractions`,
   `internal`, with `InternalsVisibleTo` for the two codec assemblies. The third option
   the note did not consider — a linked `<Compile Include>` into both csprojs — turns out
   to be worse than it looks: two assemblies would export the same type name to the test
   assembly, and CS0433 cannot be fixed by qualifying, only by `extern alias`.

3. **Code-block scan order is stripes of four rows, column-major within the stripe.**
   → **Paid off**, and the suite has teeth on it: forcing a raster scan fails 10 fixtures.

4. **Sign decoding needs a context and an XOR bit**, both from the same lookup.
   → **Paid off.** Dropping the XOR fails 11 fixtures. The `ramp64` fixture exists
   specifically because the note predicted this would be invisible on detail and obvious
   on a gradient.

5. **Tag trees are stateful across packets of the same precinct.** Re-initialising per
   packet decodes quality layer 0 correctly and everything after it wrongly, so a
   single-layer fixture cannot catch it.
   → **Confirmed exactly, by measurement.** Rebuilding the trees on every packet was
   introduced deliberately and **the entire corpus still passes**. This is the one hazard
   rung 1 is provably blind to. Rung 3 must add a multi-layer fixture *for this specific
   reason*, and must not treat multi-layer support as merely "more of the same".

6. **Harden from day one, not in a follow-up release.**
   → **Done at rung 1** (`Jpeg2000Limits`, `Jpeg2000SampleBudget`), and it earned its keep
   immediately: writing the limit tests exposed a genuine overflow in `SizMarker`'s
   tile-count ceiling division, which wrapped for a declared extent near `int.MaxValue` and
   so refused a decompression bomb for the wrong reason with the wrong exception type.
   The residual JBIG2 documents applies here mirrored: a raw J2K codestream carries its own
   dimensions, so the budget is anchored to what the stream declares. Rung 4's PDF entry
   point gets the tighter anchor, from the image dictionary.

7. **Probe the oracle's output format, do not assume it.**
   → **Paid off, and there was something to find.** `opj_decompress` stamps a comment line
   into the PNM header it writes (`P5\n#OpenJPEG-2.5.4\n…`), so a decoded `.pgm` is *never*
   byte-identical to the source `.pgm` even when every pixel matches — comparisons are on
   parsed payloads. Also: `-h` exits **non-zero** (it is the usage path), so an availability
   probe gating on the exit code alone reports a working oracle as missing, and one piping
   it under `set -o pipefail` fails outright. Full probe results are recorded beside the
   harness in `Oracle/jpeg2000/README.md`, as the note asked.

An eighth, not anticipated here, is worth adding for whoever writes the next oracle:
**never probe for a tool by searching its output for its own name.** A shell that cannot
find a program says `<name>: command not found`, so the substring test matches the very
message proving absence. This was found in the existing `Jbig2Oracle` while cloning its
pattern, and it had been turning 75 honest skips into red failures on any machine without
jbig2dec in WSL. Check the exit code first.

## Open decisions — all four now settled or narrowed

- **Package name.** → **`SharpAstro.Jpeg2000`.** The short-name convention (`Jxr`, `Jxl`,
  `Jbig2`) argued for `SharpAstro.Jpx`, and PDF's filter name agreed. The decider was that
  "JPX" *also* names Part 2, which is exactly what the package refuses: a package id naming
  the thing it does not do is a trap for whoever reads the NuGet listing rather than the
  csproj. Uglier, unambiguous, and it stays honest while Part 2 keeps not arriving. The
  reasoning is in the csproj, as the note asked.

- **Facade plus a PDF entry point, or facade alone.** → **Narrowed, not closed.** Rung 1
  registers with the facade *not at all*, which the note did not offer as an option: the
  facade would otherwise advertise JPEG 2000 support for a format only this slice of which
  decodes. Registration lands with colour at rung 2. The real question underneath — whether
  the decoder should *report* what the codestream said about colour rather than silently
  applying or ignoring it — is untouched and still must be settled before rung 4.

- **Is `opj_decompress` apt-installable?** → **Yes, and it was the wrong answer.**
  `libopenjp2-tools` is in the Ubuntu repo, but it is OpenJPEG **2.4.0** on jammy and 2.5.x
  on noble, so an apt line would have left this dev box and CI quietly disagreeing about
  what the reference is. What shipped instead is better than either option the note
  considered: upstream publishes **official prebuilt binaries** for linux-x86_64 and
  windows-x64, so `Oracle/jpeg2000/fetch.sh` downloads v2.5.4 pinned by version *and*
  SHA-256. No CMake, no sudo, and the dev box and CI execute the *same bytes* rather than
  merely the same source — which is a stronger guarantee than the clang-build pattern gives
  for jxrlib.

- **Are the T.803 conformance codestreams committable?** → **Still open, and now less
  urgent.** The reversible path turned out to need no external fixture at all: a lossless
  5/3 codestream reconstructs its encoder's input exactly, so the committed source raster
  *is* the expected output. T.803 matters for rung 2, where the 9/7 tolerance has to come
  from somewhere principled.

## What rung 2 should do first

Not the 9/7 filter. Take the two cheap wins that widen the envelope without new maths:

1. **Multiple components without a component transform.** The plumbing is one loop; what
   stops it today is that `TileComponent.Build` and the decoder assume component 0. This
   makes the package useful for multi-band scientific data before any colour question is
   answered.
2. **RCT** (the reversible colour transform), which is integer and therefore still *exact* —
   so it extends the no-tolerance assertion to colour rather than surrendering it. Generate
   the fixtures with `opj_compress` on a `.ppm`; it turns RCT on by itself for three
   components when the transform is reversible (`COD` SGcod MCT byte = `01`), verified
   during rung 0's probe.

Only then the 9/7 filter and dequantisation, which is where the tolerance arrives and where
the test suite stops being able to say "exact". Keep those tests in their own file.
