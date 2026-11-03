# WebP oracles

The live WebP comparisons run libwebp's `dwebp` (still images) and `anim_dump`
(animations) as black-box oracles. `WebpOracle` resolves each one from `DWEBP` /
`ANIM_DUMP`, then this directory's `bin/`, then `PATH`. The tests are gated
through `OracleGate`: they skip when a tool is missing, and fail when
`REQUIRE_ORACLES=1`. CI installs both tools with `apt-get install webp`.
Without either tool, the committed digests in `Fixtures/webp/*-golden.tsv` still
run.

`build.sh` downloads the official libwebp 1.6.0 release tarball and builds
`anim_dump` with CMake/Make into `bin/`, for anyone who wants the exact version
the digests were made with. The binary is only a black-box oracle. The downloaded
sources are not read, copied, or linked into SharpAstro.Webp. Downloads, build
products, and the oracle executable stay in this directory's ignored `src/`,
`build/`, and `bin/` directories.

## `fetch.sh`: the pinned release binaries (`dwebp`, `cwebp`)

`bash tests/SharpAstro.Codecs.Tests/Oracle/webp/fetch.sh` downloads the official
libwebp 1.6.0 release archive for the host (the `windows-x64` zip, which also
runs under emulation on win-arm64, or the `linux-x86-64` tarball), checks its
SHA-256 against the value pinned in the script, unpacks it into a throw-away
directory, and installs **only** `dwebp` and `cwebp` into the ignored `bin/`.
Nothing else from the archive is kept or run; it then runs each tool's
`-version` and refuses anything that does not report 1.6.0.

`cwebp` is the oracle for the lossless *encoder* leg: it produces reference
files the decoder must read, and its output is what the encoder's file sizes are
compared against. `dwebp` is the oracle for what the encoder writes, decoded to
PAM so all four channels can be compared. Both are black boxes -- libwebp's
source is never read. The encode report (`benchmarks/SharpAstro.Codecs.Benchmarks`,
`WebpEncodeReport`) resolves `cwebp` from `CWEBP`, then `bin/`, then `PATH`; the
test project does not call `cwebp`.
