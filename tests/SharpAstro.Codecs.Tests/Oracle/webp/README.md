# WebP animation oracle

`build.sh` downloads the official libwebp 1.6.0 release tarball and builds
`anim_dump` with CMake/Make. The binary is used only as a black-box test oracle
for animation output; the downloaded sources are not read, copied, or linked
into SharpAstro.Webp. Downloads, build products, and the oracle executable stay
in this directory's ignored `src/`, `build/`, and `bin/` directories.

Run `bash tests/SharpAstro.Codecs.Tests/Oracle/webp/build.sh`, then either set
`ANIM_DUMP` to the resulting executable or add `Oracle/webp/bin` to `PATH`.
