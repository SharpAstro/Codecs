# SharpAstro codec family

This repository hosts a family of pure-managed, AOT-compatible image codec
packages for .NET 10. They share infrastructure (CI, SourceLink, central
package versions) but each ships as an independent NuGet — consumers pick
exactly the formats they need.

## Package matrix

| Package | Decode | Encode | What it actually is |
|---|:---:|:---:|---|
| [`SharpAstro.StbImage`](src/StbImageSharp/) | PNG · JPG · BMP · TGA · PSD · GIF · HDR | — | Auto-generated C# port of Sean Barrett's `stb_image.h`. Decode-only, **8-bit precision through the managed API** (the internal 16-bit pipeline isn't exposed by `ImageResult`/`ImageResultFloat`). **Strips ancillary metadata** — iCCP / sRGB / gAMA / cHRM / eXIf / tEXt are all discarded. Use for "best-effort decode an arbitrary still image"; not for HDR or color-managed workflows. |
| [`SharpAstro.Png`](src/SharpAstro.Png/) | — | PNG | Pure-managed writer with libpng-style adaptive per-row filtering, 8/16-bit RGBA/Gray, iCCP/eXIf/sRGB chunks. Also exports `PngPredictor` as a reusable row-unfilter building block (TIFF Predictor=2, PDF FlateDecode). |
| [`SharpAstro.Jpeg.IccInjector`](src/SharpAstro.Jpeg.IccInjector/) | — | — | `JpegIccInjector` — splices an APP2 ICC segment into an already-encoded JPEG byte stream. Not a JPEG codec; the `SharpAstro.Jpeg` PackageId is held in reserve for a future full encoder/decoder. |
| [`SharpAstro.Tiff`](src/SharpAstro.Tiff/) | TIFF | TIFF | Full pure-managed TIFF reader/writer. Multi-page, 8/16/32-bit uint + IEEE-Float, Uncompressed / Deflate / Zlib, II + MM byte order, SampleFormat/SMin/SMax/ICC round-trip. |
| [`SharpAstro.Jxr`](src/SharpAstro.Jxr/) | JXR | JXR | Clean-room T.832 JPEG XR codec from the spec PDF. BD8/BD16/BD16F/BD32F × grayscale/RGB × spatial/frequency mode × multi-tile × INDEX_TABLE_TILES (with random-access tile decode) × POT × lossy quantization × alpha plane × full `.jxr` file container. |
| [`SharpAstro.Color.Icc`](src/SharpAstro.Color.Icc/) | — | — | Bundled sRGB v4 ICC blob (588 bytes, lazily loaded) for embedding into TIFF/PNG/JPEG via the codec packages above. Not a codec. |
| [`SharpAstro.Exif`](src/SharpAstro.Exif/) | EXIF | — | Pure-managed EXIF metadata reader. Parses EXIF blobs from JPEG (APP1), TIFF (sub-IFD), and PNG (eXIf chunk). |

## PNG decode/encode asymmetry — known caveat

If you encode a PNG with `SharpAstro.Png` and embed an ICC profile (iCCP
chunk), EXIF (eXIf), or sRGB declaration, then read it back through
`SharpAstro.StbImage`, the metadata silently disappears — stb_image only
keeps pixel data. Same for 16-bit-per-sample PNGs: encoder writes them
faithfully, decoder downsamples to 8-bit.

For a fully round-trippable PNG workflow today: encode with
`SharpAstro.Png`, decode with **`SharpAstro.Tiff`** instead (TIFF is
strictly more capable than PNG for color-managed 16-bit imagery and is
fully symmetric in this repo). Adding a clean-room PNG decoder to
`SharpAstro.Png` is the proper fix — see "Naming-convention status"
below.

## Picking what to consume

The packages are independent — pull only what your project actually needs:

```xml
<!-- Reading any common still image -->
<PackageReference Include="SharpAstro.StbImage" />

<!-- Writing PNGs -->
<PackageReference Include="SharpAstro.Png" />

<!-- Working with TIFFs (both directions) -->
<PackageReference Include="SharpAstro.Tiff" />

<!-- HDR-master JXR (the user's astrophotography pipeline) -->
<PackageReference Include="SharpAstro.Jxr" />

<!-- Embedding sRGB profiles -->
<PackageReference Include="SharpAstro.Color.Icc" />

<!-- Reading EXIF from any of the above -->
<PackageReference Include="SharpAstro.Exif" />
```

`DIR.Lib` (the rendering library that motivates much of this work) is
intentionally **codec-free**: `BoxRasterizer.RenderToRgba` returns a raw
`RgbaImage`, and the consumer decides which encoder to wrap it with.

## Naming-convention status

The package names follow three inconsistent patterns today:

1. **`StbImage`** — names the *source library* (the C header it ports). Doesn't say what formats or what direction.
2. **`Png` / `Tiff` / `Jxr`** — names the *format*. Tiff and Jxr happen to be symmetric (encode + decode); Png is currently encode-only.
3. **`Jpeg`** — looks like a codec, isn't. Just `JpegIccInjector`.
4. **`Color.Icc` / `Exif`** — names a *domain* (color management, metadata) rather than a codec.

This is tracked but not yet acted on. Likely future moves:

- ✅ **`SharpAstro.Jpeg` renamed to `SharpAstro.Jpeg.IccInjector`** — the `Jpeg` PackageId is now reserved for a future full codec.
- **Add a pure-managed PNG decoder to `SharpAstro.Png`** so it matches the `Tiff` / `Jxr` shape — at which point the PNG path can retire from `SharpAstro.StbImage`.
- **Shrink `SharpAstro.StbImage`** as each format gets a clean-room sibling, eventually leaving it as "the holdouts" (TGA / PSD / GIF / HDR / BMP).

None of this is urgent — the packages coexist fine and ship from the same CI.

## Building & testing

```bash
git clone https://github.com/SharpAstro/StbImageSharp
cd StbImageSharp
dotnet build StbImageSharp.JustTests.sln -c Release
dotnet test  StbImageSharp.JustTests.sln -c Release
```

Requires the .NET 10 SDK.

## License

All packages in this repository are released under the [Unlicense](UNLICENSE)
(public domain), matching the upstream `stb_image.h` and StbImageSharp terms.
