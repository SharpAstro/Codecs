# SharpAstro Codecs

[![NuGet](https://img.shields.io/nuget/v/SharpAstro.Codecs)](https://www.nuget.org/packages/SharpAstro.Codecs/)
[![CI/CD](https://github.com/SharpAstro/Codecs/actions/workflows/dotnet.yml/badge.svg)](https://github.com/SharpAstro/Codecs/actions/workflows/dotnet.yml)

A family of **pure-managed, AOT-compatible** image-codec packages for .NET 10 — no native binaries.
Each format ships as an independent NuGet, and **`SharpAstro.Codecs`** is a thin facade that sniffs a
byte stream by its magic bytes and dispatches to the right decoder, so a consumer can reference one
package instead of cherry-picking codecs.

Formats: **PNG** (read/write), **JPEG** (baseline + progressive decode, incl. scaled 1/2–1/8 LOD;
baseline encode), **TIFF** (read/write), **JPEG XR** (read/write, jxrlib-exact), **OpenEXR**
(read/write), **JPEG XL** (read/write), **JBIG2** (bilevel decode, for PDF's `/JBIG2Decode`), plus
**EXIF** reading and a bundled **sRGB ICC** profile. See **[CODECS.md](CODECS.md)** for the full
per-package decode/encode matrix and how to pick the right one.

All packages target `net10.0`, are `IsAotCompatible`, ship SourceLink debugging, and publish in
lockstep (shared Major.Minor + CI run-number patch).

## NuGet

```
# One facade for sniff-and-decode (PNG, JPEG incl. Ultra HDR, TIFF, JXR, EXR, JXL, .jb2):
dotnet add package SharpAstro.Codecs

# ...or reference just the format(s) you need:
dotnet add package SharpAstro.Png
dotnet add package SharpAstro.Jxr
```

## Usage

Decode any supported still image through the facade — sniff the header, size a buffer, decode into it:

```csharp
using SharpAstro.Codecs;

var bytes = File.ReadAllBytes(path);
if (ImageCodecs.TryReadInfo(bytes, out var info))
{
    var rgba = new byte[info.Width * info.Height * 4];
    ImageCodecs.TryDecodeIntoRgba8(bytes, rgba);      // zero-copy into your buffer
    // ...or ImageCodecs.TryDecode(bytes, out IDecodedImage img) for the full-fidelity raster.
}
```

Each codec is also usable directly — e.g. `PngReader` / `PngWriter`, `JpegDecoder.Decode` / `DecodeTo`,
`TiffReader` / `TiffWriter`, `JxrImageCodec`, `ExrImageCodec`, `JxlImageCodec`. See CODECS.md.

JBIG2 is the one format whose main entry point is *not* the facade. A PDF-embedded JBIG2 stream has
no file header to sniff, keeps its shared segment dictionaries in a separate `/JBIG2Globals` stream,
and takes its dimensions from the image dictionary — so those callers pass all three explicitly:

```csharp
using SharpAstro.Jbig2;

// `embedded` / `globals` are the PDF streams; width/height come from the image dictionary.
var page = Jbig2Decoder.Decode(embedded, globals, width, height);
var bits = page.Bits;        // one byte per pixel, 1 = black (T.88 polarity)
var gray = page.ToGray8();   // ...or the 8-bit projection, black 0 / white 255
```

Standalone `.jb2` files do have a signature, and decode through the facade like anything else.

## Building from source

```
git clone https://github.com/SharpAstro/Codecs
cd Codecs
dotnet build Codecs.JustTests.sln -c Release
dotnet test  Codecs.JustTests.sln -c Release
```

Requires the .NET 10 SDK.

## License

[Unlicense](UNLICENSE) (public domain).

## Credits

This repository began as a fork of [StbSharp/StbImageSharp](https://github.com/StbSharp/StbImageSharp)
(Roman Shapiro's C# port of Sean Barrett's [`stb_image.h`](https://github.com/nothings/stb), via the
[Hebron](https://github.com/rds1983/Hebron) C-to-C# transpiler). `SharpAstro.Jpeg`'s decoder was
ported from and validated byte-exact against that reference decoder before the stb port itself was
retired from the repo.
