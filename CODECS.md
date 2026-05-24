# SharpAstro codec family

This repository hosts a family of pure-managed, AOT-compatible image codec
packages for .NET 10. They share infrastructure (CI, SourceLink, central
package versions) but each ships as an independent NuGet — consumers pick
exactly the formats they need.

## Package matrix

| Package | Decode | Encode | What it actually is |
|---|:---:|:---:|---|
| [`SharpAstro.StbImage`](src/StbImageSharp/) | PNG · JPG · BMP · TGA · PSD · GIF · HDR | — | Auto-generated C# port of Sean Barrett's `stb_image.h`. Decode-only, **8-bit precision through the managed API** (the internal 16-bit pipeline isn't exposed by `ImageResult`/`ImageResultFloat`). **Strips ancillary metadata** — iCCP / sRGB / gAMA / cHRM / eXIf / tEXt are all discarded. Use for "best-effort decode an arbitrary still image"; not for HDR or color-managed workflows. |
| [`SharpAstro.Png`](src/SharpAstro.Png/) | PNG | PNG | Pure-managed encoder + decoder. Writer: libpng-style adaptive per-row filtering, 8/16-bit RGBA/Gray. Reader: chunk parsing with CRC validation, color types 0/2/4/6 at 8/16-bit. **Both sides handle**: `iCCP` (ICC profile), `sRGB`, `gAMA`, `cHRM`, `eXIf`, plus PNG-3 HDR chunks `cICP` / `mDCv` / `cLLI` (HDR10, HLG signaling). Also exports `PngPredictor` as a reusable row-unfilter building block (TIFF Predictor=2, PDF FlateDecode). Sub-byte bit depths (1/2/4), indexed-color (PLTE), and Adam7 interlacing are not yet supported. |
| [`SharpAstro.Jpeg.IccInjector`](src/SharpAstro.Jpeg.IccInjector/) | — | — | `JpegIccInjector` — splices an APP2 ICC segment into an already-encoded JPEG byte stream. Not a JPEG codec; the `SharpAstro.Jpeg` PackageId is held in reserve for a future full encoder/decoder. |
| [`SharpAstro.Tiff`](src/SharpAstro.Tiff/) | TIFF | TIFF | Full pure-managed TIFF reader/writer. Multi-page, 8/16/32-bit uint + IEEE-Float, Uncompressed / Deflate / Zlib, II + MM byte order, SampleFormat/SMin/SMax/ICC round-trip. |
| [`SharpAstro.Jxr`](src/SharpAstro.Jxr/) | JXR | JXR | Clean-room T.832 JPEG XR codec from the spec PDF. BD8/BD16/BD16F/BD32F × grayscale/RGB × spatial/frequency mode × multi-tile × INDEX_TABLE_TILES (with random-access tile decode) × POT × lossy quantization × alpha plane × full `.jxr` file container. |
| [`SharpAstro.Color.Icc`](src/SharpAstro.Color.Icc/) | — | — | Bundled sRGB v4 ICC blob (588 bytes, lazily loaded) for embedding into TIFF/PNG/JPEG via the codec packages above. Not a codec. |
| [`SharpAstro.Exif`](src/SharpAstro.Exif/) | EXIF | — | Pure-managed EXIF metadata reader. Parses EXIF blobs from JPEG (APP1), TIFF (sub-IFD), and PNG (eXIf chunk). |

## PNG decode path — recommendation

For a round-trippable PNG workflow, use **`SharpAstro.Png`** on both
sides — `PngWriter` to encode, `PngReader` to decode. The reader
preserves iCCP / sRGB / gAMA / cHRM / eXIf metadata and decodes 16-bit
samples faithfully (returned in PNG's big-endian byte order; use
`PngImage.AsUInt16Samples()` for a host-endian view).

`SharpAstro.StbImage` is still the right tool for "best-effort decode an
arbitrary still image" (BMP/TGA/PSD/GIF/HDR have no clean-room sibling
yet), but for PNG specifically it discards all metadata and downsamples
16-bit to 8-bit through the managed API — prefer `SharpAstro.Png`.

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
- ✅ **Pure-managed PNG decoder added to `SharpAstro.Png`** (`PngReader.Decode`) — now symmetric with `Tiff` / `Jxr`. Sub-byte depths / indexed-color / Adam7 are deferred follow-ups.
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
