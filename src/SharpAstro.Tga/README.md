# SharpAstro.Tga

Pure-managed Truevision TGA 2.0 reading for .NET 10, written from the Truevision TGA File Format
Specification. There is no encoder.

Supported: colour-mapped, true-colour and greyscale images (types 1, 2, 3), raw or run-length
coded (types 9, 10, 11); 8, 15, 16, 24 and 32-bit pixels; 8, 15, 16, 24 and 32-bit colour-map
entries; alpha from 32-bit pixels or from the descriptor's attribute bit on 15/16-bit ones; both
vertical origins and both horizontal directions. The image ID is skipped. Output is always
tightly packed, top-down RGBA8.

```csharp
using SharpAstro.Tga;

byte[] data = File.ReadAllBytes("image.tga");
TgaImage image = TgaReader.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}, alpha: {image.HasAlpha}");
byte[] rgba = image.Rgba;
```

`TgaReader.TryReadInfo` checks the header without decoding.

The reader refuses with `InvalidDataException` rather than guessing: a truncated file, an RLE
packet running past the end of the image, an unknown image type or depth, a colour index outside
the map, a non-zero interleave field, and more than 67,108,864 declared pixels (checked before
anything is allocated). Not read: the extension and developer areas, so the "attributes type"
byte is not consulted. 16-bit greyscale (grey + alpha) is refused, because the specification does
not define it and ImageMagick reads it differently from stb_image.

Every pixel the reader produces is compared byte for byte against ImageMagick's read of the same
file in the test suite.
