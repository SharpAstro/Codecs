# SharpAstro.Pnm

Pure-managed Netpbm reading for .NET 10, written from the Netpbm format
documentation (`pbm`, `pgm`, `ppm`, `pam`). The facade (`SharpAstro.Codecs`) sniffs
`P1`..`P7` and decodes through it.

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

ImageMagick agrees sample for sample, except where it departs from the Netpbm documents: a
binary raster with `maxval` 128..254 or 32768..65534 is read as full-scale samples, without dividing
by `maxval`, and a 1-bit grey+alpha PAM comes back with its alpha inverted. This reader follows the
documents there.

`PnmReader` returns the file's **native** samples. Through the facade
(`PnmImageDecoder`), they are scaled to fill UInt8 (`maxval` <= 255) or UInt16, with the
rounding ImageMagick uses, so a `maxval` 15 file decodes to the pixels ImageMagick gives.
