# SharpAstro.Bmp

Pure-managed BMP decoding and encoding for .NET 10, written from the Microsoft `BITMAPCOREHEADER` /
`BITMAPINFOHEADER` / V4 / V5 documentation. Output is validated pixel by pixel against ImageMagick.

```csharp
using SharpAstro.Bmp;

byte[] data = File.ReadAllBytes("image.bmp");
BmpImage image = BmpDecoder.Decode(data);          // 8-bit, top row first, RGB or RGBA
Console.WriteLine($"{image.Width} × {image.Height}, {image.Channels} channels");

if (BmpDecoder.TryReadInfo(data, out BmpInfo info)) // headers only, no pixels
    Console.WriteLine($"{info.HeaderKind}, {info.BitsPerPixel} bpp, {info.Compression}, top-down: {info.TopDown}");
```

## Writing

```csharp
byte[] bmp24 = BmpEncoder.EncodeRgb24(rgb, width, height);    // 24-bit BGR, 40-byte header
byte[] bmp32 = BmpEncoder.EncodeRgba32(rgba, width, height);  // 32-bit BGRA, V4 header, alpha mask 0xFF000000
```

Input is 8-bit, top row first, non-premultiplied; an optional `stride` argument skips padding between input rows.
Output is uncompressed, bottom-up, rows padded to four bytes. The 32-bit file uses `BI_BITFIELDS` with an explicit
alpha mask, so alpha survives in ImageMagick, stb_image and this package's decoder. Bad dimensions, strides or a too
short input throw `ArgumentException` before anything is allocated.

The facade (`SharpAstro.Codecs`) registers `BmpImageDecoder`, so `ImageCodecs.TryDecode` and friends read BMP too.

## What is read

| | |
|---|---|
| Headers | 12-byte OS/2 core, 16/64-byte OS/2 2.x, 40-byte `BITMAPINFOHEADER` (and the 52/56-byte Adobe variants), 108-byte V4, 124-byte V5 |
| Depths | 1-, 4-, 8-bit indexed; 16-bit (5-5-5, or any `BI_BITFIELDS` masks, 5-6-5 included); 24-bit; 32-bit (`BI_RGB` or `BI_BITFIELDS`, with or without an alpha mask) |
| Compression | `BI_RGB`, `BI_RLE8`, `BI_RLE4`, `BI_BITFIELDS`, `BI_ALPHABITFIELDS` |
| Layout | bottom-up and top-down (negative height), rows padded to four bytes |
| Colour | a V5 embedded ICC profile is reported on `BmpInfo.IccProfile` and never applied |

A colour mask of 8 bits or fewer is expanded to 8 bits by bit replication, as stb_image does: the value goes in the
top bits and its own top bits repeat below it, so a 5-bit 24 (`11000`) reads as 198 (`11000110`) and a full channel
of any width is 255. A mask wider than 8 bits is rounded to nearest from its top 16 bits (`(x + 128) / 257`, `x` the
value left-aligned in 16 bits), which is ImageMagick's arithmetic there. Everything indexed, 24-bit, every 8-bit
and every wider channel, and RLE decode to exactly ImageMagick's pixels. Below 8 bits ImageMagick has its own rules,
and they are not copied: it replicates 5-bit channels except 3 and 7 (25 and 58, not 24 and 57), replicates a 6-bit
green channel except on eight values, and only shifts every other narrow channel (a 4-bit 15 reads as 240, a 1-bit
alpha of 1 as 128, a 6-bit red as `v << 2`), and in 32-bit pixels it replicates nothing. The tests pin each of these
value by value; on a 5-5-5 or 5-6-5 file our pixels and ImageMagick's differ by one level on exactly those values.

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
