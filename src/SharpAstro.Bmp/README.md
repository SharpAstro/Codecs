# SharpAstro.Bmp

Pure-managed BMP decoding for .NET 10, written from the Microsoft `BITMAPCOREHEADER` /
`BITMAPINFOHEADER` / V4 / V5 documentation. Output is validated pixel by pixel against ImageMagick.

```csharp
using SharpAstro.Bmp;

byte[] data = File.ReadAllBytes("image.bmp");
BmpImage image = BmpDecoder.Decode(data);          // 8-bit, top row first, RGB or RGBA
Console.WriteLine($"{image.Width} × {image.Height}, {image.Channels} channels");

if (BmpDecoder.TryReadInfo(data, out BmpInfo info)) // headers only, no pixels
    Console.WriteLine($"{info.HeaderKind}, {info.BitsPerPixel} bpp, {info.Compression}, top-down: {info.TopDown}");
```

## What is read

| | |
|---|---|
| Headers | 12-byte OS/2 core, 16/64-byte OS/2 2.x, 40-byte `BITMAPINFOHEADER` (and the 52/56-byte Adobe variants), 108-byte V4, 124-byte V5 |
| Depths | 1-, 4-, 8-bit indexed; 16-bit (5-5-5, or any `BI_BITFIELDS` masks, 5-6-5 included); 24-bit; 32-bit (`BI_RGB` or `BI_BITFIELDS`, with or without an alpha mask) |
| Compression | `BI_RGB`, `BI_RLE8`, `BI_RLE4`, `BI_BITFIELDS`, `BI_ALPHABITFIELDS` |
| Layout | bottom-up and top-down (negative height), rows padded to four bytes |
| Colour | a V5 embedded ICC profile is reported on `BmpInfo.IccProfile` and never applied |

Colour masks of any width are scaled to 8 bits linearly, rounding to nearest (`(v * 255 + max / 2) / max`), so a
full channel is always 255 whatever its width. 8-bit channels and everything indexed, 24-bit, 32-bit 8-8-8-8 and
RLE decode to exactly ImageMagick's pixels. 16-bit pixels agree with ImageMagick to within one level (5- and
6-bit channels: ImageMagick lands one step off the rounded value on some inputs); narrower channels and odd
mask layouts differ more, because ImageMagick shifts them instead of scaling (a 4-bit 15 reads as 240, a 1-bit
alpha of 1 as 128). Those are ImageMagick's quirks and are not copied.

The output has an alpha channel when the file has one: a non-zero alpha mask under `BI_BITFIELDS`, or a 32-bit
`BI_RGB` file with a 40-byte-class header, whose fourth byte is alpha. If that byte is zero in every pixel the
image is opaque, because X8R8G8B8 writers leave it zero (stb_image makes the same choice, and ImageMagick finds no
alpha channel in such a file either). A V4/V5 header's masks apply only under `BI_BITFIELDS`, so a V4/V5 `BI_RGB`
file is opaque.

Pixels an RLE stream never writes (skipped by a delta, or left after an early end of bitmap) take palette entry 0.
An RLE run may overshoot the width into the row's 4-byte padding (ImageMagick's RLE writer encodes the padding as
pixels); the overshoot is dropped, anything beyond the padded row is an error.

## What is refused

`BI_JPEG`, `BI_PNG`, `BI_CMYK*`, OS/2 Huffman 1D and OS/2 RLE24 throw `NotSupportedException` naming the
feature. Malformed or truncated input throws `InvalidDataException`: offsets and declared sizes are checked
against the data, an RLE run that leaves its row is an error rather than a wrap, and a header declaring more
than 2^28 pixels is rejected before any raster is allocated. The `Try*` methods return false instead of throwing.

An RLE file can declare a large picture in a few bytes (a delta skips up to 255 × 255 pixels in four), so
for RLE the 2^28-pixel limit, not the input length, bounds the allocation: up to 768 MiB of RGB. A caller
with a tighter budget should check `ReadInfo` first.
