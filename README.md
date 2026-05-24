# SharpAstro.StbImage

[![NuGet](https://img.shields.io/nuget/v/SharpAstro.StbImage)](https://www.nuget.org/packages/SharpAstro.StbImage/)
[![CI/CD](https://github.com/SharpAstro/StbImageSharp/actions/workflows/dotnet.yml/badge.svg)](https://github.com/SharpAstro/StbImageSharp/actions/workflows/dotnet.yml)

SharpAstro fork of [StbSharp/StbImageSharp](https://github.com/StbSharp/StbImageSharp) — a pure-managed C# port of Sean Barrett's [`stb_image.h`](https://github.com/nothings/stb). Decodes **JPG (baseline)**, **PNG**, **BMP**, **TGA**, **PSD**, **GIF**, and **HDR** without any native binaries.

This repository also hosts a family of sibling pure-managed codec packages (`SharpAstro.Png`, `SharpAstro.Tiff`, `SharpAstro.Jxr`, `SharpAstro.Color.Icc`, `SharpAstro.Exif`, `SharpAstro.Jpeg`). See **[CODECS.md](CODECS.md)** for the full matrix of what each package decodes / encodes and how to pick the right one.

Why a fork? The SharpAstro family of libraries (`DIR.Lib`, `Fonts.Lib`, `SdlVulkan.Renderer`, …) targets .NET 10, ships AOT-compatible NuGet packages, and is wired into a CI/CD pipeline. This fork brings StbImageSharp into the same convention: `net10.0`, `<IsAotCompatible>true</IsAotCompatible>`, centrally-managed package versions, SourceLink debugging, and automated publishing.

The C# source is unchanged from upstream — it's still the Hebron-transpiled stb_image port. Only project scaffolding (csproj, CI, packaging metadata) has been modernised.

## NuGet

```
dotnet add package SharpAstro.StbImage
```

Namespace is still `StbImageSharp` to keep call sites identical when migrating off the upstream package.

## Usage

```csharp
using StbImageSharp;

using var stream = File.OpenRead(path);
var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
// image.Width, image.Height, image.Data (byte[])
```

Other entry points: `ImageResult.FromMemory(byte[])`, `ImageInfo.FromStream` (header peek), `ImageResultFloat.FromStream` (HDR), `ImageResult.AnimatedGifFramesFromStream`.

See the [upstream README](https://github.com/StbSharp/StbImageSharp) for richer examples (MonoGame Texture2D, WinForms Bitmap, etc.).

## Building from source

```
git clone https://github.com/SharpAstro/StbImageSharp
cd StbImageSharp
dotnet build StbImageSharp.JustTests.sln -c Release
dotnet test  StbImageSharp.JustTests.sln -c Release
```

Requires the .NET 10 SDK.

## License

[Unlicense](UNLICENSE) (public domain). Same terms as the upstream port and the original stb library.

## Credits

* [stb](https://github.com/nothings/stb) — Sean Barrett's original C image-loader header.
* [StbSharp/StbImageSharp](https://github.com/StbSharp/StbImageSharp) — Roman Shapiro's C# port (via [Hebron](https://github.com/rds1983/Hebron) C-to-C# transpiler).
