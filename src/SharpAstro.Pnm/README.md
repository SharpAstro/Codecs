# SharpAstro.Pnm

Pure-managed Netpbm reading and writing for .NET 10, written from the Netpbm format
documentation (`pbm`, `pgm`, `ppm`, `pam`). The facade (`SharpAstro.Codecs`) sniffs
`P1`..`P7` and decodes through it. Writing is `PnmWriter`.

Reads every member of the family:

- `P1`/`P4` PBM (bitmap, 1 = black), `P2`/`P5` PGM, `P3`/`P6` PPM, in plain (ASCII)
  and raw form; plain `P1` may pack its digits with no separators.
- `P7` PAM with `TUPLTYPE` `BLACKANDWHITE`, `GRAYSCALE`, `RGB` and their `_ALPHA`
  forms (or by `DEPTH` when absent). `CMYK` and other tuple types are refused.
- `maxval` 1..65535; above 255 raw samples are two bytes, big-endian.
- `#` comments wherever the format allows them.

A sample above `maxval`, a truncated raster or an out-of-range header throws
`InvalidDataException`. A declared size over 2^28 samples (a caller-chosen ceiling)
is refused before anything is allocated, and a raw raster must fit in the input.

```csharp
using SharpAstro.Pnm;

PnmImage image = PnmReader.Read(File.ReadAllBytes("image.pgm"));
Console.WriteLine($"{image.Width} x {image.Height}, {image.Channels} channel(s), maxval {image.MaxValue}");
ushort sample = image.Samples[0]; // native value, 0..MaxValue
```

## Read / write matrix

| Format | Magic | Read | Write |
|---|---|:---:|:---:|
| PBM bitmap | `P1` plain, `P4` raw | yes | no |
| PGM grey | `P2` plain, `P5` raw | yes | `P5` |
| PPM RGB | `P3` plain, `P6` raw | yes | `P6` |
| PAM `BLACKANDWHITE[_ALPHA]` | `P7` | yes | no |
| PAM `GRAYSCALE`, `RGB` | `P7` | yes | yes |
| PAM `GRAYSCALE_ALPHA`, `RGB_ALPHA` | `P7` | yes | yes |

`maxval` 1..65535 both ways; above 255 a raw sample is two bytes, big-endian.

## Writing

```csharp
using SharpAstro.Pnm;

byte[] file = PnmWriter.Encode(rgb8, width, height, PnmTupleType.Rgb);               // P6, maxval 255
byte[] wide = PnmWriter.Encode(gray16, width, height, PnmTupleType.Grayscale);       // P5, maxval 65535
byte[] pam  = PnmWriter.Encode(rgba8, width, height, PnmTupleType.RgbAlpha);         // P7 RGB_ALPHA
PnmWriter.Write(stream, gray16, width, height, PnmTupleType.Grayscale, maxValue: 1023, pam: true);
```

Grey and RGB go out as `P5` / `P6` unless `pam: true` asks for `P7`; anything with alpha is always
`P7`, since the PGM/PPM headers cannot carry it. Output is the raw form with Netpbm's own header
layout and no comment. A short buffer, a tuple type that cannot be written (`BlackAndWhite*`), a
`maxval` outside 1..65535 or a sample above it throws `ArgumentException`.

**The writer never scales.** Samples are the file's native values: with `maxValue: 1023` they are
0..1023 and are written as they are. Scaling is a decision for the caller, and the reader's
facade rule is the one to mirror (below): a `maxval` below the type's full range is stretched to
fill it, `round(v * full / maxval)`, which is what ImageMagick does too. Only `maxval` 255 and 65535
are written without any such question, and those are the two the tests compare against Magick.NET
pixel for pixel; every other `maxval` is verified by our own reader round trip.

ImageMagick agrees sample for sample, except where it departs from the Netpbm documents: a
binary raster with `maxval` 128..254 or 32768..65534 is read as full-scale samples, without dividing
by `maxval`, and a 1-bit grey+alpha PAM comes back with its alpha inverted. This reader follows the
documents there.

`PnmReader` returns the file's **native** samples. Through the facade
(`PnmImageDecoder`), they are scaled to fill UInt8 (`maxval` <= 255) or UInt16, with the
rounding ImageMagick uses, so a `maxval` 15 file decodes to the pixels ImageMagick gives.
