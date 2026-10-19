using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpAstro.Webp;

/// <summary>
/// RFC 9649 VP8L entropy and transform decoder. The implementation follows
/// Sections 3.5-3.8; no implementation source from an existing WebP codec is
/// used.
/// </summary>
internal static class Vp8LDecoder
{
    private static readonly int[] CodeLengthCodeOrder =
        { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    private static readonly (int X, int Y)[] DistanceMap =
    {
        (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0), (1, 2), (-1, 2),
        (2, 1), (-2, 1), (2, 2), (-2, 2), (0, 3), (3, 0), (1, 3), (-1, 3),
        (3, 1), (-3, 1), (2, 3), (-2, 3), (3, 2), (-3, 2), (0, 4), (4, 0),
        (1, 4), (-1, 4), (4, 1), (-4, 1), (3, 3), (-3, 3), (2, 4), (-2, 4),
        (4, 2), (-4, 2), (0, 5), (3, 4), (-3, 4), (4, 3), (-4, 3), (5, 0),
        (1, 5), (-1, 5), (5, 1), (-5, 1), (2, 5), (-2, 5), (5, 2), (-5, 2),
        (4, 4), (-4, 4), (3, 5), (-3, 5), (5, 3), (-5, 3), (0, 6), (6, 0),
        (1, 6), (-1, 6), (6, 1), (-6, 1), (2, 6), (-2, 6), (6, 2), (-6, 2),
        (4, 5), (-4, 5), (5, 4), (-5, 4), (3, 6), (-3, 6), (6, 3), (-6, 3),
        (0, 7), (7, 0), (1, 7), (-1, 7), (5, 5), (-5, 5), (7, 1), (-7, 1),
        (4, 6), (-4, 6), (6, 4), (-6, 4), (2, 7), (-2, 7), (7, 2), (-7, 2),
        (3, 7), (-3, 7), (7, 3), (-7, 3), (5, 6), (-5, 6), (6, 5), (-6, 5),
        (8, 0), (4, 7), (-4, 7), (7, 4), (-7, 4), (8, 1), (8, 2), (6, 6),
        (-6, 6), (8, 3), (5, 7), (-5, 7), (7, 5), (-7, 5), (8, 4), (6, 7),
        (-6, 7), (7, 6), (-7, 6), (8, 5), (7, 7), (-7, 7), (8, 6), (8, 7)
    };

    public static byte[] DecodeRgba(ReadOnlySpan<byte> stream)
    {
        (int width, int height, int[] argb) = DecodeArgb(stream);
        try
        {
            byte[] result = new byte[checked(width * height * 4)];
            ToRgba(argb, result, width, height, checked(width * 4));
            return result;
        }
        finally
        {
            WebpBufferPool<int>.Return(argb);
        }
    }

    public static void DecodeRgbaTo(ReadOnlySpan<byte> stream, Span<byte> output, int stride)
    {
        (int width, int height, int[] argb) = DecodeArgb(stream);
        int rowLength = checked(width * 4);
        try
        {
            if (stride < rowLength || output.Length < checked((height - 1) * stride + rowLength))
            {
                throw new ArgumentException("The destination is too small for the decoded VP8L image.", nameof(output));
            }

            ToRgba(argb, output, width, height, stride);
        }
        finally
        {
            WebpBufferPool<int>.Return(argb);
        }
    }

    public static byte[] DecodeAlpha(ReadOnlySpan<byte> stream, int width, int height)
    {
        if ((long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L alpha dimensions exceed the pixel limit.");
        }

        int[] argb = DecodeImage(new BitReader(stream), width, height, allowTransforms: true);
        try
        {
            int pixelCount = checked(width * height);
            byte[] alpha = new byte[pixelCount];
            CopyAlpha(argb, alpha, pixelCount);
            return alpha;
        }
        finally
        {
            WebpBufferPool<int>.Return(argb);
        }
    }

    public static void DecodeAlphaTo(ReadOnlySpan<byte> stream, int width, int height, Span<byte> alpha)
    {
        if ((long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L alpha dimensions exceed the pixel limit.");
        }

        int pixelCount = checked(width * height);
        if (alpha.Length < pixelCount)
        {
            throw new ArgumentException("The alpha destination is too small.", nameof(alpha));
        }

        int[] argb = DecodeImage(new BitReader(stream), width, height, allowTransforms: true);
        try
        {
            CopyAlpha(argb, alpha, pixelCount);
        }
        finally
        {
            WebpBufferPool<int>.Return(argb);
        }
    }

    private static void CopyAlpha(ReadOnlySpan<int> argb, Span<byte> alpha, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            alpha[i] = (byte)((argb[i] >> 8) & 0xff);
        }
    }

    private static int[] DecodeImage(BitReader reader, int width, int height, bool allowTransforms)
    {
        if (width <= 0 || height <= 0 || (long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L image dimensions are invalid.");
        }

        var transforms = new List<Transform>();
        int encodedWidth = width;
        int encodedHeight = height;
        int[]? pixels = null;
        try
        {
            if (allowTransforms)
            {
                Span<bool> seen = stackalloc bool[4];
                seen.Clear();
                while (reader.ReadBit() != 0)
                {
                    int type = reader.ReadBits(2);
                    if (type > 3 || seen[type])
                    {
                        throw new InvalidDataException("The VP8L transform sequence is invalid.");
                    }

                    seen[type] = true;
                    Transform transform = ReadTransform(ref reader, type, encodedWidth, encodedHeight);
                    transforms.Add(transform);
                    if (type == 3)
                    {
                        encodedWidth = (encodedWidth + (1 << transform.Bits) - 1) >> transform.Bits;
                    }
                }
            }

            pixels = DecodeImageStream(ref reader, encodedWidth, encodedHeight, hasMetaPrefix: true);
            for (int i = transforms.Count - 1; i >= 0; i--)
            {
                int[] previous = pixels;
                pixels = ApplyTransform(previous, transforms[i]);
                if (!ReferenceEquals(previous, pixels))
                {
                    WebpBufferPool<int>.Return(previous);
                }
            }

            if (pixels.Length < checked(width * height))
            {
                throw new InvalidDataException("The VP8L decoded image dimensions are inconsistent.");
            }

            int[] result = pixels;
            pixels = null;
            return result;
        }
        finally
        {
            if (pixels is not null)
            {
                WebpBufferPool<int>.Return(pixels);
            }

            foreach (Transform transform in transforms)
            {
                if (transform.Data is not null)
                {
                    WebpBufferPool<int>.Return(transform.Data);
                }

                if (transform.Table is not null)
                {
                    WebpBufferPool<int>.Return(transform.Table);
                }
            }
        }
    }

    private static Transform ReadTransform(ref BitReader reader, int type, int width, int height)
    {
        if (type == 0 || type == 1)
        {
            int bits = reader.ReadBits(3) + 2;
            int blockWidth = (width + (1 << bits) - 1) >> bits;
            int blockHeight = (height + (1 << bits) - 1) >> bits;
            int[] data = DecodeImageStream(ref reader, blockWidth, blockHeight, hasMetaPrefix: false);
            return new Transform(type, width, height, bits, data, null, 0);
        }

        if (type == 2)
        {
            return new Transform(type, width, height, 0, null, null, 0);
        }

        int tableSize = reader.ReadBits(8) + 1;
        int bitsPerPixel = tableSize <= 2 ? 3 : tableSize <= 4 ? 2 : tableSize <= 16 ? 1 : 0;
        int tableWidth = tableSize;
        int[] tableData = DecodeImageStream(ref reader, tableWidth, 1, hasMetaPrefix: false);
        int[] table = WebpBufferPool<int>.Rent(tableSize);
        try
        {
            int previous = 0;
            for (int i = 0; i < tableWidth; i++)
            {
                int packed = tableData[i];
                int alpha = ((packed >> 24) & 0xff) + ((previous >> 24) & 0xff);
                int red = ((packed >> 16) & 0xff) + ((previous >> 16) & 0xff);
                int green = ((packed >> 8) & 0xff) + ((previous >> 8) & 0xff);
                int blue = (packed & 0xff) + (previous & 0xff);
                previous = ((alpha & 0xff) << 24) | ((red & 0xff) << 16) |
                    ((green & 0xff) << 8) | (blue & 0xff);
                table[i] = previous;
            }

            return new Transform(type, width, height, bitsPerPixel, null, table, tableSize);
        }
        catch
        {
            WebpBufferPool<int>.Return(table);
            throw;
        }
        finally
        {
            WebpBufferPool<int>.Return(tableData);
        }
    }

    private static int[] DecodeImageStream(
        ref BitReader reader,
        int width,
        int height,
        bool hasMetaPrefix)
    {
        if (width <= 0 || height <= 0 || (long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L subimage dimensions are invalid.");
        }

        int[]? entropy = null;
        PrefixGroup[]? groups = null;
        int[]? cache = null;
        int[]? result = null;
        int entropyCount = 0;
        int groupCount = 1;
        int entropyWidth = 0;
        int prefixBits = 0;
        int cacheBits = 0;
        int pixelCount = checked(width * height);
        try
        {
            if (reader.ReadBit() != 0)
            {
                cacheBits = reader.ReadBits(4);
                if (cacheBits is < 1 or > 11)
                {
                    throw new InvalidDataException("The VP8L color cache size is invalid.");
                }
            }

            if (hasMetaPrefix && reader.ReadBit() != 0)
            {
                prefixBits = reader.ReadBits(3) + 2;
                entropyWidth = (width + (1 << prefixBits) - 1) >> prefixBits;
                int entropyHeight = (height + (1 << prefixBits) - 1) >> prefixBits;
                entropyCount = checked(entropyWidth * entropyHeight);
                entropy = DecodeImageStream(ref reader, entropyWidth, entropyHeight, hasMetaPrefix: false);
                int max = 0;
                for (int i = 0; i < entropyCount; i++)
                {
                    max = Math.Max(max, (entropy[i] >> 8) & 0xffff);
                }

                groupCount = max + 1;
            }

            groups = WebpBufferPool<PrefixGroup>.Rent(groupCount);
            int greenAlphabet = 256 + 24 + (cacheBits == 0 ? 0 : 1 << cacheBits);
            for (int i = 0; i < groupCount; i++)
            {
                groups[i] = ReadPrefixGroup(ref reader, greenAlphabet);
            }

            result = WebpBufferPool<int>.Rent(pixelCount);
            int cacheLength = cacheBits == 0 ? 0 : 1 << cacheBits;
            if (cacheLength != 0)
            {
                cache = WebpBufferPool<int>.Rent(cacheLength);
                Array.Clear(cache, 0, cacheLength);
            }

            int position = 0;
            while (position < pixelCount)
            {
                int x = position % width;
                int y = position / width;
                int group = entropy is null ? 0 :
                    ((entropy[(y >> prefixBits) * entropyWidth + (x >> prefixBits)] >> 8) & 0xffff);
                if ((uint)group >= groupCount)
                {
                    throw new InvalidDataException("The VP8L meta prefix code is out of range.");
                }

                PrefixGroup prefix = groups[group];
                int symbol = prefix.Green.Decode(ref reader);
                if (symbol < 256)
                {
                    int red = prefix.Red.Decode(ref reader);
                    int blue = prefix.Blue.Decode(ref reader);
                    int alpha = prefix.Alpha.Decode(ref reader);
                    if ((red | blue | alpha) >= 256)
                    {
                        throw new InvalidDataException("A VP8L literal channel is out of range.");
                    }

                    int color = (alpha << 24) | (red << 16) | (symbol << 8) | blue;
                    result[position++] = color;
                    AddCache(cache, color, cacheBits);
                    continue;
                }

                if (symbol < 280)
                {
                    int length = PrefixValue(ref reader, symbol - 256);
                    int distanceCode = prefix.Distance.Decode(ref reader);
                    if (distanceCode < 0 || distanceCode >= 40)
                    {
                        throw new InvalidDataException("The VP8L backward distance is invalid.");
                    }

                    int distancePrefix = PrefixValue(ref reader, distanceCode);
                    int distance = Distance(distancePrefix, width);
                    if (distance > position)
                    {
                        throw new InvalidDataException("The VP8L backward distance is invalid.");
                    }

                    if (length > pixelCount - position)
                    {
                        throw new InvalidDataException("The VP8L backward copy exceeds the image.");
                    }

                    if (distance >= length)
                    {
                        Array.Copy(result, position - distance, result, position, length);
                        int end = position + length;
                        for (int i = position; i < end; i++)
                        {
                            AddCache(cache, result[i], cacheBits);
                        }

                        position = end;
                    }
                    else
                    {
                        for (int i = 0; i < length; i++)
                        {
                            int color = result[position - distance];
                            result[position++] = color;
                            AddCache(cache, color, cacheBits);
                        }
                    }

                    continue;
                }

                if (cache is null || symbol - 280 >= cacheLength)
                {
                    throw new InvalidDataException("The VP8L color cache symbol is invalid.");
                }

                int cached = cache[symbol - 280];
                result[position++] = cached;
                AddCache(cache, cached, cacheBits);
            }

            int[] decoded = result;
            result = null;
            return decoded;
        }
        finally
        {
            if (result is not null)
            {
                WebpBufferPool<int>.Return(result);
            }

            if (cache is not null)
            {
                WebpBufferPool<int>.Return(cache);
            }

            if (entropy is not null)
            {
                WebpBufferPool<int>.Return(entropy);
            }

            if (groups is not null)
            {
                for (int i = 0; i < groupCount; i++)
                {
                    groups[i].ReturnBuffers();
                }

                Array.Clear(groups, 0, groupCount);
                WebpBufferPool<PrefixGroup>.Return(groups);
            }
        }
    }

    private static PrefixGroup ReadPrefixGroup(ref BitReader reader, int greenAlphabet)
    {
        PrefixCode green = default;
        PrefixCode red = default;
        PrefixCode blue = default;
        PrefixCode alpha = default;
        bool hasGreen = false;
        bool hasRed = false;
        bool hasBlue = false;
        bool hasAlpha = false;
        try
        {
            green = ReadPrefixCode(ref reader, greenAlphabet);
            hasGreen = true;
            red = ReadPrefixCode(ref reader, 256);
            hasRed = true;
            blue = ReadPrefixCode(ref reader, 256);
            hasBlue = true;
            alpha = ReadPrefixCode(ref reader, 256);
            hasAlpha = true;
            PrefixCode distance = ReadPrefixCode(ref reader, 40);
            return new PrefixGroup(green, red, blue, alpha, distance);
        }
        catch
        {
            if (hasGreen) green.ReturnBuffers();
            if (hasRed) red.ReturnBuffers();
            if (hasBlue) blue.ReturnBuffers();
            if (hasAlpha) alpha.ReturnBuffers();
            throw;
        }
    }

    private static PrefixCode ReadPrefixCode(ref BitReader reader, int alphabetSize)
    {
        int startBit = reader.BitPosition;
        int[] lengths = WebpBufferPool<int>.Rent(alphabetSize);
        try
        {
            Span<int> lengthsSpan = lengths.AsSpan(0, alphabetSize);
            lengthsSpan.Clear();
            if (reader.ReadBit() != 0)
            {
                int count = reader.ReadBit() + 1;
                int firstWide = reader.ReadBit();
                int first = reader.ReadBits(1 + 7 * firstWide);
                if (first >= alphabetSize)
                {
                    throw new InvalidDataException("The VP8L simple prefix symbol is out of range.");
                }

                lengthsSpan[first] = 1;
                if (count == 2)
                {
                    int second = reader.ReadBits(8);
                    if (second >= alphabetSize)
                    {
                        throw new InvalidDataException("The VP8L simple prefix symbol is out of range.");
                    }

                    lengthsSpan[second] = 1;
                }

                return PrefixCode.Build(lengthsSpan);
            }

            Span<int> codeLengthLengths = stackalloc int[19];
            codeLengthLengths.Clear();
            int numberOfCodeLengths = 4 + reader.ReadBits(4);
            for (int i = 0; i < numberOfCodeLengths; i++)
            {
                codeLengthLengths[CodeLengthCodeOrder[i]] = reader.ReadBits(3);
            }

            PrefixCode codeLengthCode;
            try
            {
                codeLengthCode = PrefixCode.Build(codeLengthLengths);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"The VP8L code-length tree for alphabet {alphabetSize}, starting at bit {startBit}, " +
                    $"is invalid at bit {reader.BitPosition} ({numberOfCodeLengths} lengths): " +
                    $"{string.Join(',', codeLengthLengths.ToArray())}.",
                    exception);
            }

            try
            {
                int lengthNBits = reader.ReadBit() == 0 ? 0 : 2 + 2 * reader.ReadBits(3);
                int maxSymbol = lengthNBits == 0 ? alphabetSize : 2 + reader.ReadBits(lengthNBits);
                if (maxSymbol > alphabetSize)
                {
                    throw new InvalidDataException("The VP8L prefix alphabet is too large.");
                }

                int index = 0;
                int symbolsRead = 0;
                int previous = 8;
                while (symbolsRead < maxSymbol && index < alphabetSize)
                {
                    int value = codeLengthCode.Decode(ref reader);
                    symbolsRead++;
                    if (value < 16)
                    {
                        lengthsSpan[index++] = value;
                        if (value != 0) previous = value;
                    }
                    else if (value == 16)
                    {
                        int repeat = 3 + reader.ReadBits(2);
                        if (repeat > alphabetSize - index)
                        {
                            throw new InvalidDataException(
                                $"The VP8L prefix alphabet {alphabetSize} starting at bit {startBit} " +
                                $"exceeds its output size at symbol {symbolsRead} (index {index}, " +
                                $"repeat {repeat}, max_symbol {maxSymbol}).");
                        }

                        lengthsSpan.Slice(index, repeat).Fill(previous);
                        index += repeat;
                    }
                    else if (value is 17 or 18)
                    {
                        int repeat = value == 17 ? 3 + reader.ReadBits(3) : 11 + reader.ReadBits(7);
                        if (repeat > alphabetSize - index)
                        {
                            throw new InvalidDataException(
                                $"The VP8L prefix alphabet {alphabetSize} starting at bit {startBit} " +
                                $"exceeds its output size at symbol {symbolsRead} (index {index}, " +
                                $"repeat {repeat}, max_symbol {maxSymbol}).");
                        }

                        lengthsSpan.Slice(index, repeat).Clear();
                        index += repeat;
                    }
                    else
                    {
                        throw new InvalidDataException("The VP8L code-length symbol is invalid.");
                    }
                }

                return PrefixCode.Build(lengthsSpan);
            }
            finally
            {
                codeLengthCode.ReturnBuffers();
            }
        }
        finally
        {
            WebpBufferPool<int>.Return(lengths);
        }
    }

    private static int PrefixValue(ref BitReader reader, int code)
    {
        if (code < 4) return code + 1;
        int extraBits = (code - 2) >> 1;
        int offset = (2 + (code & 1)) << extraBits;
        return offset + reader.ReadBits(extraBits) + 1;
    }

    private static int Distance(int distanceCode, int width)
    {
        if (distanceCode > 120)
        {
            return distanceCode - 120;
        }

        if (distanceCode < 1 || distanceCode > DistanceMap.Length)
        {
            throw new InvalidDataException("The VP8L distance code is invalid.");
        }

        (int x, int y) = DistanceMap[distanceCode - 1];
        return Math.Max(1, x + y * width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddCache(int[]? cache, int color, int cacheBits)
    {
        if (cache is null) return;
        uint hash = unchecked(0x1e35a7bdu * (uint)color);
        cache[hash >> (32 - cacheBits)] = color;
    }

    private static int[] ApplyTransform(int[] pixels, Transform transform)
    {
        return transform.Type switch
        {
            0 => ApplyPredictor(pixels, transform),
            1 => ApplyColorTransform(pixels, transform),
            2 => ApplySubtractGreen(pixels, transform),
            3 => ApplyColorIndexing(pixels, transform),
            _ => throw new InvalidDataException("Unknown VP8L transform."),
        };
    }

    private static int[] ApplyPredictor(int[] pixels, Transform transform) =>
        Vector.IsHardwareAccelerated
            ? ApplyPredictorVector(pixels, transform)
            : ApplyPredictorScalar(pixels, transform);

    internal static int[] ApplyPredictorScalar(int[] pixels, Transform transform)
    {
        int width = transform.Width;
        int height = transform.Height;
        int blockWidth = (width + (1 << transform.Bits) - 1) >> transform.Bits;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                int left = x == 0 ? 0 : pixels[index - 1];
                int top = y == 0 ? 0 : pixels[index - width];
                int topLeft = x == 0 || y == 0 ? 0 : pixels[index - width - 1];
                int topRight = y == 0 ? 0 : x == width - 1
                    ? pixels[index - x]
                    : pixels[index - width + 1];
                int mode = (transform.Data![(y >> transform.Bits) * blockWidth + (x >> transform.Bits)] >> 8) & 0xff;
                int prediction = Predict(mode, x, y, width, left, top, topLeft, topRight);
                pixels[index] = AddArgb(pixels[index], prediction);
            }
        }

        return pixels;
    }

    // Per-block-segment dispatch: the mode is constant across a (1 << bits)-wide
    // run of pixels, so the per-pixel mode fetch + switch of the scalar path is
    // paid once per segment instead. Modes that only read the row above are
    // bytewise-vectorizable (AddArgb/Average are per-byte-lane operations);
    // modes that read the just-written left pixel stay scalar per segment.
    internal static int[] ApplyPredictorVector(int[] pixels, Transform transform)
    {
        int width = transform.Width;
        int height = transform.Height;
        int bits = transform.Bits;
        int blockWidth = (width + (1 << bits) - 1) >> bits;
        int[] data = transform.Data!;
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            int blockRow = (y >> bits) * blockWidth;
            if (y == 0)
            {
                // First pixel uses the black predictor; the rest of the top row
                // is left-predicted no matter what the block modes say.
                pixels[index] = AddArgb(pixels[index], unchecked((int)0xff000000));
                index++;
                for (int column = 1; column < width; column++, index++)
                {
                    pixels[index] = AddArgb(pixels[index], pixels[index - 1]);
                }

                continue;
            }

            // x == 0 always predicts top, whatever the segment mode is.
            pixels[index] = AddArgb(pixels[index], pixels[index - width]);
            index++;
            int x = 1;
            while (x < width)
            {
                int mode = (data[blockRow + (x >> bits)] >> 8) & 0xff;
                int remaining = Math.Min(((x >> bits) + 1) << bits, width) - x;
                switch (mode)
                {
                    case 0:
                        AddConstant(pixels, index, remaining, unchecked((int)0xff000000));
                        break;
                    case 1:
                        for (int i = 0; i < remaining; i++)
                        {
                            pixels[index + i] = AddArgb(pixels[index + i], pixels[index + i - 1]);
                        }

                        break;
                    case 2:
                        AddAbove(pixels, index, remaining, -width);
                        break;
                    case 3:
                        AddAbove(pixels, index, remaining, 1 - width);
                        break;
                    case 4:
                        AddAbove(pixels, index, remaining, -width - 1);
                        break;
                    case 5:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                Average(Average(pixels[j - 1], pixels[j - width + 1]), pixels[j - width]));
                        }

                        break;
                    case 6:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j], Average(pixels[j - 1], pixels[j - width - 1]));
                        }

                        break;
                    case 7:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j], Average(pixels[j - 1], pixels[j - width]));
                        }

                        break;
                    case 8:
                        AddAverageAbove(pixels, index, remaining, -width - 1, -width);
                        break;
                    case 9:
                        AddAverageAbove(pixels, index, remaining, -width, 1 - width);
                        break;
                    case 10:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                Average(Average(pixels[j - 1], pixels[j - width - 1]),
                                    Average(pixels[j - width], pixels[j - width + 1])));
                        }

                        break;
                    case 11:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                Select(pixels[j - 1], pixels[j - width], pixels[j - width - 1]));
                        }

                        break;
                    case 12:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                ClampAdd(pixels[j - 1], pixels[j - width], pixels[j - width - 1]));
                        }

                        break;
                    case 13:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                ClampHalf(Average(pixels[j - 1], pixels[j - width]), pixels[j - width - 1]));
                        }

                        break;
                    default:
                        throw new InvalidDataException("The VP8L predictor mode is invalid.");
                }

                x += remaining;
                index += remaining;
            }
        }

        return pixels;
    }

    private static void AddAbove(int[] pixels, int index, int count, int offset)
    {
        int i = 0;
        int byteCount = count * 4;
        if (Vector.IsHardwareAccelerated && byteCount >= Vector<byte>.Count)
        {
            ref byte baseRef = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<int, byte>(pixels.AsSpan()));
            ref byte destination = ref Unsafe.Add(ref baseRef, index * 4);
            ref byte source = ref Unsafe.Add(ref baseRef, (index + offset) * 4);
            for (; i + Vector<byte>.Count <= byteCount; i += Vector<byte>.Count)
            {
                Vector<byte> current = Vector.LoadUnsafe(ref destination, (nuint)i);
                Vector<byte> above = Vector.LoadUnsafe(ref source, (nuint)i);
                (current + above).StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (int pixel = i / 4; pixel < count; pixel++)
        {
            pixels[index + pixel] = AddArgb(pixels[index + pixel], pixels[index + pixel + offset]);
        }
    }

    private static void AddAverageAbove(int[] pixels, int index, int count, int offsetA, int offsetB)
    {
        int i = 0;
        int byteCount = count * 4;
        if (Vector.IsHardwareAccelerated && byteCount >= Vector<byte>.Count)
        {
            ref byte baseRef = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<int, byte>(pixels.AsSpan()));
            ref byte destination = ref Unsafe.Add(ref baseRef, index * 4);
            ref byte sourceA = ref Unsafe.Add(ref baseRef, (index + offsetA) * 4);
            ref byte sourceB = ref Unsafe.Add(ref baseRef, (index + offsetB) * 4);
            for (; i + Vector<byte>.Count <= byteCount; i += Vector<byte>.Count)
            {
                Vector<byte> a = Vector.LoadUnsafe(ref sourceA, (nuint)i);
                Vector<byte> b = Vector.LoadUnsafe(ref sourceB, (nuint)i);
                // (a + b) >> 1 per byte lane, no carry between lanes.
                Vector<byte> average = (a & b) + Vector.ShiftRightLogical(a ^ b, 1);
                Vector<byte> current = Vector.LoadUnsafe(ref destination, (nuint)i);
                (current + average).StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (int pixel = i / 4; pixel < count; pixel++)
        {
            pixels[index + pixel] = AddArgb(pixels[index + pixel],
                Average(pixels[index + pixel + offsetA], pixels[index + pixel + offsetB]));
        }
    }

    private static void AddConstant(int[] pixels, int index, int count, int value)
    {
        int i = 0;
        int byteCount = count * 4;
        if (Vector.IsHardwareAccelerated && byteCount >= Vector<byte>.Count)
        {
            Vector<int> pattern = new(value);
            Vector<byte> constant = Unsafe.As<Vector<int>, Vector<byte>>(ref pattern);
            ref byte baseRef = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<int, byte>(pixels.AsSpan()));
            ref byte destination = ref Unsafe.Add(ref baseRef, index * 4);
            for (; i + Vector<byte>.Count <= byteCount; i += Vector<byte>.Count)
            {
                Vector<byte> current = Vector.LoadUnsafe(ref destination, (nuint)i);
                (current + constant).StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (int pixel = i / 4; pixel < count; pixel++)
        {
            pixels[index + pixel] = AddArgb(pixels[index + pixel], value);
        }
    }

    private static int Predict(int mode, int x, int y, int width, int left, int top, int topLeft, int topRight)
    {
        if (x == 0 && y == 0) return unchecked((int)0xff000000);
        if (y == 0) return left;
        if (x == 0) return top;
        return mode switch
        {
            0 => unchecked((int)0xff000000),
            1 => left,
            2 => top,
            3 => topRight,
            4 => topLeft,
            5 => Average(Average(left, topRight), top),
            6 => Average(left, topLeft),
            7 => Average(left, top),
            8 => Average(topLeft, top),
            9 => Average(top, topRight),
            10 => Average(Average(left, topLeft), Average(top, topRight)),
            11 => Select(left, top, topLeft),
            12 => ClampAdd(left, top, topLeft),
            13 => ClampHalf(Average(left, top), topLeft),
            _ => throw new InvalidDataException("The VP8L predictor mode is invalid."),
        };
    }

    private static int[] ApplyColorTransform(int[] pixels, Transform transform) =>
        Vector.IsHardwareAccelerated
            ? ApplyColorTransformVector(pixels, transform)
            : ApplyColorTransformScalar(pixels, transform);

    internal static int[] ApplyColorTransformScalar(int[] pixels, Transform transform)
    {
        int blockWidth = (transform.Width + (1 << transform.Bits) - 1) >> transform.Bits;
        for (int y = 0; y < transform.Height; y++)
        {
            for (int x = 0; x < transform.Width; x++)
            {
                int index = y * transform.Width + x;
                int cte = transform.Data![(y >> transform.Bits) * blockWidth + (x >> transform.Bits)];
                int green = (pixels[index] >> 8) & 0xff;
                int red = (pixels[index] >> 16) & 0xff;
                int blue = pixels[index] & 0xff;
                int redDelta = ColorDelta((cte >> 0) & 0xff, green);
                int blueDelta = ColorDelta((cte >> 8) & 0xff, green);
                int redForBlue = (red + redDelta) & 0xff;
                blue = (blue + blueDelta + ColorDelta((cte >> 16) & 0xff, redForBlue)) & 0xff;
                red = redForBlue;
                pixels[index] = (pixels[index] & unchecked((int)0xff00ff00)) | (red << 16) | blue;
            }
        }

        return pixels;
    }

    // Per-block-segment dispatch again: the three multiplier bytes are constant
    // across a (1 << bits)-wide run, so they broadcast into int lanes. All the
    // arithmetic is exact int math (products fit, shifts match the scalar >>
    // semantics), so vector lanes are bit-identical to the scalar path.
    internal static int[] ApplyColorTransformVector(int[] pixels, Transform transform)
    {
        int width = transform.Width;
        int height = transform.Height;
        int bits = transform.Bits;
        int blockWidth = (width + (1 << bits) - 1) >> bits;
        int[] data = transform.Data!;
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            int blockRow = (y >> bits) * blockWidth;
            for (int bx = 0; bx < blockWidth; bx++)
            {
                int x = bx << bits;
                if (x >= width)
                {
                    break;
                }

                int cte = data[blockRow + bx];
                int count = Math.Min((bx + 1) << bits, width) - x;
                ColorTransformSegment(pixels, index, count, cte);
                index += count;
            }
        }

        return pixels;
    }

    private static void ColorTransformSegment(int[] pixels, int index, int count, int cte)
    {
        int c0 = (sbyte)cte;
        int c1 = (sbyte)(cte >> 8);
        int c2 = (sbyte)(cte >> 16);
        int i = 0;
        if (Vector.IsHardwareAccelerated && count >= Vector<int>.Count)
        {
            Vector<int> c0v = new(c0);
            Vector<int> c1v = new(c1);
            Vector<int> c2v = new(c2);
            Vector<int> ff = new(0xff);
            Vector<int> keep = new(unchecked((int)0xff00ff00));
            ref int baseRef = ref MemoryMarshal.GetArrayDataReference(pixels);
            ref int at = ref Unsafe.Add(ref baseRef, index);
            for (; i + Vector<int>.Count <= count; i += Vector<int>.Count)
            {
                Vector<int> v = Vector.LoadUnsafe(ref at, (nuint)i);
                Vector<int> green = (v >> 8) & ff;
                Vector<int> red = (v >> 16) & ff;
                Vector<int> blue = v & ff;
                Vector<int> signedGreen = (green << 24) >> 24;
                Vector<int> newRed = red + ((signedGreen * c0v) >> 5);
                Vector<int> red8 = newRed & ff;
                Vector<int> signedRed = (red8 << 24) >> 24;
                Vector<int> newBlue = (blue + ((signedGreen * c1v) >> 5) +
                    ((signedRed * c2v) >> 5)) & ff;
                ((v & keep) | (red8 << 16) | newBlue).StoreUnsafe(ref at, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            int p = pixels[index + i];
            int green = (p >> 8) & 0xff;
            int red = (p >> 16) & 0xff;
            int blue = p & 0xff;
            int redDelta = c0 * (sbyte)green >> 5;
            int blueDelta = c1 * (sbyte)green >> 5;
            int redForBlue = (red + redDelta) & 0xff;
            blue = (blue + blueDelta + (c2 * (sbyte)redForBlue >> 5)) & 0xff;
            pixels[index + i] = (p & unchecked((int)0xff00ff00)) | (redForBlue << 16) | blue;
        }
    }

    // Byte positions 1 of each little-endian int, spread into positions 0 and 2:
    // the shuffle produces the addend {green, 0, green, 0} per pixel so one
    // vector add implements (blue += green; red += green) per byte lane.
    private static readonly Vector128<byte> SubtractGreenSpread = Vector128.Create(
        (byte)1, 0x80, 1, 0x80, 5, 0x80, 5, 0x80, 9, 0x80, 9, 0x80, 13, 0x80, 13, 0x80);
    // Vector256.Shuffle takes indices absolute to the whole vector, not per lane.
    private static readonly Vector256<byte> SubtractGreenSpread256 = Vector256.Create(
        (byte)1, 0x80, 1, 0x80, 5, 0x80, 5, 0x80, 9, 0x80, 9, 0x80, 13, 0x80, 13, 0x80,
        17, 0x80, 17, 0x80, 21, 0x80, 21, 0x80, 25, 0x80, 25, 0x80, 29, 0x80, 29, 0x80);

    private static int[] ApplySubtractGreen(int[] pixels, Transform transform) =>
        Vector128.IsHardwareAccelerated
            ? ApplySubtractGreenVector(pixels, transform)
            : ApplySubtractGreenScalar(pixels, transform);

    internal static int[] ApplySubtractGreenVector(int[] pixels, Transform transform)
    {
        int pixelCount = checked(transform.Width * transform.Height);
        ref int baseRef = ref MemoryMarshal.GetArrayDataReference(pixels);
        ref byte bytes = ref Unsafe.As<int, byte>(ref baseRef);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= pixelCount; i += 8)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref bytes, (nuint)(i * 4));
                v += Vector256.Shuffle(v, SubtractGreenSpread256);
                v.StoreUnsafe(ref bytes, (nuint)(i * 4));
            }
        }
        else
        {
            for (; i + 4 <= pixelCount; i += 4)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref bytes, (nuint)(i * 4));
                v += Vector128.Shuffle(v, SubtractGreenSpread);
                v.StoreUnsafe(ref bytes, (nuint)(i * 4));
            }
        }

        return ApplySubtractGreenTail(pixels, transform, i);
    }

    internal static int[] ApplySubtractGreenScalar(int[] pixels, Transform transform) =>
        ApplySubtractGreenTail(pixels, transform, 0);

    private static int[] ApplySubtractGreenTail(int[] pixels, Transform transform, int i)
    {
        int pixelCount = checked(transform.Width * transform.Height);
        for (; i < pixelCount; i++)
        {
            int green = (pixels[i] >> 8) & 0xff;
            int red = (((pixels[i] >> 16) & 0xff) + green) & 0xff;
            int blue = ((pixels[i] & 0xff) + green) & 0xff;
            pixels[i] = (pixels[i] & unchecked((int)0xff00ff00)) | (red << 16) | blue;
        }

        return pixels;
    }

    private static int[] ApplyColorIndexing(int[] pixels, Transform transform)
    {
        int packed = 1 << transform.Bits;
        int bitsPerIndex = 8 / packed;
        int mask = (1 << bitsPerIndex) - 1;
        int width = transform.Width;
        int encodedWidth = (width + packed - 1) / packed;
        int outputCount = checked(width * transform.Height);
        int[] output = WebpBufferPool<int>.Rent(outputCount);
        int[] table = transform.Table!;
        int tableLength = transform.TableLength;
        try
        {
            for (int y = 0; y < transform.Height; y++)
            {
                int inIndex = y * encodedWidth;
                int outIndex = y * width;
                int x = 0;
                while (x < width)
                {
                    // One packed pixel carries `packed` indices; walk the group
                    // so the per-pixel cost is a shift + table load instead of a
                    // divide/mod pair.
                    int indices = (pixels[inIndex++] >> 8) & 0xff;
                    int groupEnd = Math.Min(x + packed, width);
                    for (int shift = 0; x < groupEnd; x++, shift += bitsPerIndex)
                    {
                        int index = (indices >> shift) & mask;
                        output[outIndex++] = index < tableLength ? table[index] : 0;
                    }
                }
            }

            return output;
        }
        catch
        {
            WebpBufferPool<int>.Return(output);
            throw;
        }
    }

    private static int AddArgb(int a, int b)
    {
        return ((((a >> 24) & 0xff) + ((b >> 24) & 0xff)) & 0xff) << 24 |
            ((((a >> 16) & 0xff) + ((b >> 16) & 0xff)) & 0xff) << 16 |
            ((((a >> 8) & 0xff) + ((b >> 8) & 0xff)) & 0xff) << 8 |
            (((a & 0xff) + (b & 0xff)) & 0xff);
    }

    private static int ColorDelta(int transform, int color)
    {
        int signedTransform = (sbyte)transform;
        int signedColor = (sbyte)color;
        return signedTransform * signedColor >> 5;
    }

    private static int Average(int a, int b) =>
        (((a >> 24 & 0xff) + (b >> 24 & 0xff)) >> 1) << 24 |
        (((a >> 16 & 0xff) + (b >> 16 & 0xff)) >> 1) << 16 |
        (((a >> 8 & 0xff) + (b >> 8 & 0xff)) >> 1) << 8 |
        ((a & 0xff) + (b & 0xff)) >> 1;

    private static int ClampAdd(int a, int b, int c) =>
        PackClamp((a >> 24 & 0xff) + (b >> 24 & 0xff) - (c >> 24 & 0xff),
            (a >> 16 & 0xff) + (b >> 16 & 0xff) - (c >> 16 & 0xff),
            (a >> 8 & 0xff) + (b >> 8 & 0xff) - (c >> 8 & 0xff),
            (a & 0xff) + (b & 0xff) - (c & 0xff));

    private static int ClampHalf(int a, int b) =>
        PackClamp((a >> 24 & 0xff) + ((a >> 24 & 0xff) - (b >> 24 & 0xff)) / 2,
            (a >> 16 & 0xff) + ((a >> 16 & 0xff) - (b >> 16 & 0xff)) / 2,
            (a >> 8 & 0xff) + ((a >> 8 & 0xff) - (b >> 8 & 0xff)) / 2,
            (a & 0xff) + ((a & 0xff) - (b & 0xff)) / 2);

    private static int Select(int left, int top, int topLeft)
    {
        int leftAlpha = left >> 24 & 0xff;
        int leftRed = left >> 16 & 0xff;
        int leftGreen = left >> 8 & 0xff;
        int leftBlue = left & 0xff;
        int topAlpha = top >> 24 & 0xff;
        int topRed = top >> 16 & 0xff;
        int topGreen = top >> 8 & 0xff;
        int topBlue = top & 0xff;
        int topLeftAlpha = topLeft >> 24 & 0xff;
        int topLeftRed = topLeft >> 16 & 0xff;
        int topLeftGreen = topLeft >> 8 & 0xff;
        int topLeftBlue = topLeft & 0xff;
        int dl = Math.Abs(topAlpha - topLeftAlpha) + Math.Abs(topRed - topLeftRed) +
            Math.Abs(topGreen - topLeftGreen) + Math.Abs(topBlue - topLeftBlue);
        int dt = Math.Abs(leftAlpha - topLeftAlpha) + Math.Abs(leftRed - topLeftRed) +
            Math.Abs(leftGreen - topLeftGreen) + Math.Abs(leftBlue - topLeftBlue);

        return dl < dt ? left : top;
    }

    private static int PackClamp(int a, int r, int g, int b) =>
        (Math.Clamp(a, 0, 255) << 24) | (Math.Clamp(r, 0, 255) << 16) |
        (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    private static (int Width, int Height, int[] Pixels) DecodeArgb(ReadOnlySpan<byte> stream)
    {
        if (stream.Length < 5 || stream[0] != 0x2f)
        {
            throw new InvalidDataException("The VP8L stream header is truncated.");
        }

        uint header = (uint)stream[1] | ((uint)stream[2] << 8) |
            ((uint)stream[3] << 16) | ((uint)stream[4] << 24);
        int width = 1 + (int)(header & 0x3fff);
        int height = 1 + (int)((header >> 14) & 0x3fff);
        if ((header >> 29) != 0 || (long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L dimensions or version are invalid.");
        }

        int[] pixels = DecodeImage(new BitReader(stream[5..]), width, height, allowTransforms: true);
        return (width, height, pixels);
    }

    // Little-endian ARGB ints are [B,G,R,A] in memory; RGBA wants bytes 0 and 2
    // swapped, which is a single byte shuffle per vector.
    private static readonly Vector128<byte> ToRgbaSwap = Vector128.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    private static readonly Vector256<byte> ToRgbaSwap256 = Vector256.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
        18, 17, 16, 19, 22, 21, 20, 23, 26, 25, 24, 27, 30, 29, 28, 31);

    private static void ToRgba(int[] argb, Span<byte> output, int width, int height, int stride)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            ToRgbaVector(argb, output, width, height, stride);
            return;
        }

        ToRgbaScalar(argb, output, width, height, stride);
    }

    internal static void ToRgbaScalar(int[] argb, Span<byte> output, int width, int height, int stride)
    {
        for (int row = 0; row < height; row++)
        {
            int sourceIndex = row * width;
            int outputIndex = row * stride;
            for (int column = 0; column < width; column++, sourceIndex++, outputIndex += 4)
            {
                output[outputIndex] = (byte)(argb[sourceIndex] >> 16);
                output[outputIndex + 1] = (byte)(argb[sourceIndex] >> 8);
                output[outputIndex + 2] = (byte)argb[sourceIndex];
                output[outputIndex + 3] = (byte)(argb[sourceIndex] >> 24);
            }
        }

    }

    internal static void ToRgbaVector(int[] argb, Span<byte> output, int width, int height, int stride)
    {
        ref int sourceBase = ref MemoryMarshal.GetArrayDataReference(argb);
        ref byte sourceBytes = ref Unsafe.As<int, byte>(ref sourceBase);
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        for (int row = 0; row < height; row++)
        {
            ref byte source = ref Unsafe.Add(ref sourceBytes, row * width * 4);
            ref byte target = ref Unsafe.Add(ref outputBase, row * stride);
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; x + 8 <= width; x += 8)
                {
                    Vector256<byte> v = Vector256.LoadUnsafe(ref source, (nuint)(x * 4));
                    Vector256.Shuffle(v, ToRgbaSwap256).StoreUnsafe(ref target, (nuint)(x * 4));
                }
            }
            else
            {
                for (; x + 4 <= width; x += 4)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref source, (nuint)(x * 4));
                    Vector128.Shuffle(v, ToRgbaSwap).StoreUnsafe(ref target, (nuint)(x * 4));
                }
            }

            for (; x < width; x++)
            {
                int p = argb[row * width + x];
                int outputIndex = row * stride + x * 4;
                output[outputIndex] = (byte)(p >> 16);
                output[outputIndex + 1] = (byte)(p >> 8);
                output[outputIndex + 2] = (byte)p;
                output[outputIndex + 3] = (byte)(p >> 24);
            }
        }
    }

    internal readonly record struct Transform(
        int Type,
        int Width,
        int Height,
        int Bits,
        int[]? Data,
        int[]? Table,
        int TableLength);

    private readonly record struct PrefixGroup(
        PrefixCode Green,
        PrefixCode Red,
        PrefixCode Blue,
        PrefixCode Alpha,
        PrefixCode Distance)
    {
        public void ReturnBuffers()
        {
            Green.ReturnBuffers();
            Red.ReturnBuffers();
            Blue.ReturnBuffers();
            Alpha.ReturnBuffers();
            Distance.ReturnBuffers();
        }
    }

    private struct PrefixCode
    {
        private readonly Node[]? _nodes;
        private readonly LookupEntry[]? _lookup;

        private PrefixCode(Node[] nodes, LookupEntry[]? lookup)
        {
            _nodes = nodes;
            _lookup = lookup;
        }

        public static PrefixCode Build(Span<int> lengths)
        {
            Span<int> counts = stackalloc int[16];
            counts.Clear();
            int maxLength = 0;
            int symbolCount = 0;
            int onlySymbol = -1;
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] is < 0 or > 15)
                {
                    throw new InvalidDataException("The VP8L code length is invalid.");
                }

                if (lengths[i] != 0)
                {
                    counts[lengths[i]]++;
                    maxLength = Math.Max(maxLength, lengths[i]);
                    symbolCount++;
                    onlySymbol = i;
                }
            }

            if (maxLength == 0)
            {
                lengths[0] = 1;
                counts[1] = 1;
                maxLength = 1;
                symbolCount = 1;
                onlySymbol = 0;
            }

            if (symbolCount == 1)
            {
                Node[] singleNode = WebpBufferPool<Node>.Rent(1);
                Node root = new();
                root.Symbol = onlySymbol;
                singleNode[0] = root;
                return new PrefixCode(singleNode, null);
            }

            Span<int> next = stackalloc int[16];
            next.Clear();
            int code = 0;
            for (int bits = 1; bits <= maxLength; bits++)
            {
                code = (code + counts[bits - 1]) << 1;
                next[bits] = code;
            }

            if (code + counts[maxLength] != 1 << maxLength)
            {
                throw new InvalidDataException("The VP8L prefix code is incomplete.");
            }

            int nodeCapacity = 1;
            for (int i = 0; i < lengths.Length; i++)
            {
                nodeCapacity = checked(nodeCapacity + lengths[i]);
            }

            Node[] nodes = WebpBufferPool<Node>.Rent(nodeCapacity);
            LookupEntry[]? lookup = null;
            bool keepNodes = false;
            bool keepLookup = false;
            int nodeCount = 1;
            nodes[0] = new Node();
            try
            {
                for (int symbol = 0; symbol < lengths.Length; symbol++)
                {
                    int length = lengths[symbol];
                    if (length == 0) continue;
                    int canonical = next[length]++;
                    int node = 0;
                    for (int bit = 0; bit < length; bit++)
                    {
                        if (nodes[node].Symbol >= 0)
                        {
                            throw new InvalidDataException("The VP8L prefix code is not canonical.");
                        }

                        int direction = canonical >> (length - 1 - bit) & 1;
                        int child = direction == 0 ? nodes[node].Zero : nodes[node].One;
                        if (child < 0)
                        {
                            child = nodeCount++;
                            nodes[child] = new Node();
                            Node current = nodes[node];
                            if (direction == 0) current.Zero = child;
                            else current.One = child;
                            nodes[node] = current;
                        }

                        node = child;
                    }

                    if (nodes[node].Symbol >= 0 || nodes[node].Zero >= 0 || nodes[node].One >= 0)
                    {
                        throw new InvalidDataException("The VP8L prefix code is not canonical.");
                    }

                    Node leaf = nodes[node];
                    leaf.Symbol = symbol;
                    nodes[node] = leaf;
                }

                lookup = BuildLookup(nodes, nodeCount);
                PrefixCode result = new(nodes, lookup);
                keepNodes = true;
                keepLookup = true;
                return result;
            }
            finally
            {
                if (!keepNodes)
                {
                    WebpBufferPool<Node>.Return(nodes);
                }

                if (lookup is not null && !keepLookup)
                {
                    WebpBufferPool<LookupEntry>.Return(lookup);
                }
            }
        }

        public int Decode(ref BitReader reader)
        {
            Node[] nodes = _nodes!;
            if (nodes[0].Symbol >= 0)
            {
                return nodes[0].Symbol;
            }

            int node = 0;
            if (reader.TryPeekBits(8, out int prefix))
            {
                LookupEntry entry = _lookup![prefix];
                if (entry.Bits == 0)
                {
                    throw new InvalidDataException("The VP8L prefix code did not contain the input.");
                }

                reader.DropBits(entry.Bits);
                if (entry.IsLeaf)
                {
                    return entry.Index;
                }

                node = entry.Index;
                for (int i = 8; i < 15; i++)
                {
                    int bit = reader.ReadBit();
                    node = bit == 0 ? nodes[node].Zero : nodes[node].One;
                    if (node < 0)
                    {
                        throw new InvalidDataException("The VP8L prefix code did not contain the input.");
                    }

                    if (nodes[node].Symbol >= 0) return nodes[node].Symbol;
                }

                throw new InvalidDataException("The VP8L prefix code is too deep.");
            }

            for (int i = 0; i < 15; i++)
            {
                int bit = reader.ReadBit();
                node = bit == 0 ? nodes[node].Zero : nodes[node].One;
                if (node < 0)
                {
                    throw new InvalidDataException("The VP8L prefix code did not contain the input.");
                }

                if (nodes[node].Symbol >= 0) return nodes[node].Symbol;
            }

            throw new InvalidDataException("The VP8L prefix code is too deep.");
        }

        public void ReturnBuffers()
        {
            if (_nodes is not null)
            {
                WebpBufferPool<Node>.Return(_nodes);
            }

            if (_lookup is not null)
            {
                WebpBufferPool<LookupEntry>.Return(_lookup);
            }
        }

        private static LookupEntry[] BuildLookup(Node[] nodes, int nodeCount)
        {
            LookupEntry[] lookup = WebpBufferPool<LookupEntry>.Rent(256);
            for (int prefix = 0; prefix < 256; prefix++)
            {
                int node = 0;
                for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    int bit = prefix >> bitIndex & 1;
                    node = bit == 0 ? nodes[node].Zero : nodes[node].One;
                    if (node < 0 || node >= nodeCount)
                    {
                        lookup[prefix] = default;
                        break;
                    }

                    if (nodes[node].Symbol >= 0)
                    {
                        lookup[prefix] = new LookupEntry((ushort)nodes[node].Symbol, (byte)(bitIndex + 1), true);
                        break;
                    }

                    if (bitIndex == 7)
                    {
                        lookup[prefix] = new LookupEntry((ushort)node, 8, false);
                    }
                }
            }

            return lookup;
        }
    }

    private readonly record struct LookupEntry(ushort Index, byte Bits, bool IsLeaf);

    private struct Node
    {
        public int Zero;
        public int One;
        public int Symbol;

        public Node()
        {
            Zero = -1;
            One = -1;
            Symbol = -1;
        }
    }

    private ref struct BitReader
    {
        // Cursor + remaining count instead of a Span (same shape as
        // Vp8BoolDecoder): bounds checks were showing up in the refill loop.
        private ref byte _next;
        private int _remaining;
        private int _position;
        private ulong _buffer;
        private int _bits;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _next = ref Unsafe.AsRef(in MemoryMarshal.GetReference(data));
            _remaining = data.Length;
            _position = 0;
            _buffer = 0;
            _bits = 0;
        }

        public int BitPosition => _position * 8 - _bits;

        public int ReadBit() => ReadBits(1);

        public bool TryPeekBits(int count, out int value)
        {
            EnsureBits(count);
            if (_bits < count)
            {
                value = 0;
                return false;
            }

            value = (int)(_buffer & ((1UL << count) - 1));
            return true;
        }

        public void DropBits(int count)
        {
            _buffer >>= count;
            _bits -= count;
        }

        public int ReadBits(int count)
        {
            if (count is < 0 or > 24)
            {
                throw new InvalidDataException("The VP8L bit count is invalid.");
            }

            EnsureBits(count);
            if (_bits < count)
            {
                throw new InvalidDataException("The VP8L bitstream is truncated.");
            }

            int result = (int)(_buffer & ((1UL << count) - 1));
            _buffer >>= count;
            _bits -= count;
            return result;
        }

        private void EnsureBits(int count)
        {
            while (_bits < count && _remaining > 0)
            {
                int take = Math.Min((64 - _bits) >> 3, _remaining);
                if (take >= 4 && _remaining >= 8)
                {
                    // One unaligned grab covers the common refill; mask off the
                    // bytes the 64-bit buffer cannot hold yet.
                    ulong chunk = Unsafe.ReadUnaligned<ulong>(ref _next);
                    _buffer |= take == 8
                        ? chunk << _bits
                        : (chunk & ((1UL << (take * 8)) - 1)) << _bits;
                }
                else
                {
                    for (int i = 0; i < take; i++)
                    {
                        _buffer |= (ulong)Unsafe.Add(ref _next, i) << (_bits + i * 8);
                    }
                }

                _next = ref Unsafe.Add(ref _next, take);
                _remaining -= take;
                _position += take;
                _bits += take * 8;
            }
        }
    }
}
