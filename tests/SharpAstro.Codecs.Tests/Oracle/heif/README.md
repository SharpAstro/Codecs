# HEVC oracle — ffmpeg, with libde265 checking the fixtures

`SharpAstro.Heif`'s HEVC decoder is clean-room from ITU-T H.265. ffmpeg (LGPL/GPL), libde265 (LGPL)
and x265 (GPL) are used **as programs only**; none of their source is read.

| What | Tool | Where it runs |
|---|---|---|
| Live comparison, plane by plane, no tolerance (`HevcOracle`, `HevcDecoderOracleTests`, `HevcFixtureTests.Fixture_MatchesLiveFfmpeg`) | `ffmpeg`'s native `hevc` decoder | Windows: `bash fetch.sh` (pinned BtbN LGPL 9.0 build, SHA-256 checked) into `dist/`. Linux and CI: `apt-get install ffmpeg`. Or set `FFMPEG`. Gated by `OracleGate`, so `REQUIRE_ORACLES=1` makes a missing ffmpeg a failure. |
| Committed fixtures and their expected digests (`Fixtures/hevc/`) | x265 through ffmpeg to encode; ffmpeg **and** libde265 to decode | `make-fixtures.sh`, Linux only (WSL here): `apt-get install ffmpeg libde265-examples`. A fixture is written only if both decoders produce the same bytes. |

## Why the fixtures exist

The Canon pictures (fetched by `fetch-heif-external.sh`, not committed) are what rung 2 targets, but
their encoder uses DC and every fourth angle only, no planar, chroma modes 1/10/26 only, CTB 32 and
TBs up to 16. Decoding them right says little about the rest of intra HEVC: the first cut of the 4:2:2
mode remap (Table 8-3) had two wrong entries and matched ffmpeg on all 25 Canon pictures. The x265
fixtures use all 35 modes and found both. `HevcFixtureTests.Fixture_CoversWhatItIsFor` checks each
fixture still exercises what it is there for, so a regeneration cannot quietly empty one.

## Things learned

- **x265 is not byte-reproducible** from run to run even single-threaded; regenerating rewrites every
  fixture and the digest file together.
- **x265 3.5 writes several slices only with WPP**, which rung 2 refuses; the Canon pictures (one to
  four slices each) cover multiple slices instead.
- **`ffmpeg -encoders | grep -q` fails under `set -o pipefail`**: grep exits on its first match and
  ffmpeg dies of SIGPIPE. The scripts capture the list first.
