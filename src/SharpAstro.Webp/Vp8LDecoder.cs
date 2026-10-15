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

        int[] argb = DecodeImage(new BitReader(stream[5..]), width, height, allowTransforms: true);
        return ToRgba(argb);
    }

    public static byte[] DecodeAlpha(ReadOnlySpan<byte> stream, int width, int height)
    {
        if ((long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L alpha dimensions exceed the pixel limit.");
        }

        int[] argb = DecodeImage(new BitReader(stream), width, height, allowTransforms: true);
        byte[] alpha = new byte[argb.Length];
        for (int i = 0; i < argb.Length; i++)
        {
            alpha[i] = (byte)((argb[i] >> 8) & 0xff);
        }

        return alpha;
    }

    private static int[] DecodeImage(BitReader reader, int width, int height, bool allowTransforms)
    {
        var transforms = new List<Transform>();
        int encodedWidth = width;
        int encodedHeight = height;
        if (allowTransforms)
        {
            var seen = new bool[4];
            while (reader.ReadBit() != 0)
            {
                int type = reader.ReadBits(2);
                if (type > 3 || seen[type])
                {
                    throw new InvalidDataException("The VP8L transform sequence is invalid.");
                }

                seen[type] = true;
                transforms.Add(ReadTransform(ref reader, type, encodedWidth, encodedHeight));
                if (type == 3)
                {
                    encodedWidth = (encodedWidth + (1 << transforms[^1].Bits) - 1) >>
                        transforms[^1].Bits;
                }
            }
        }

        int[] pixels = DecodeImageStream(ref reader, encodedWidth, encodedHeight, hasMetaPrefix: true);
        for (int i = transforms.Count - 1; i >= 0; i--)
        {
            pixels = ApplyTransform(pixels, transforms[i]);
        }

        if (pixels.Length != width * height)
        {
            throw new InvalidDataException("The VP8L decoded image dimensions are inconsistent.");
        }

        return pixels;
    }

    private static Transform ReadTransform(ref BitReader reader, int type, int width, int height)
    {
        if (type == 0 || type == 1)
        {
            int bits = reader.ReadBits(3) + 2;
            int blockWidth = (width + (1 << bits) - 1) >> bits;
            int blockHeight = (height + (1 << bits) - 1) >> bits;
            int[] data = DecodeImageStream(ref reader, blockWidth, blockHeight, hasMetaPrefix: false);
            return new Transform(type, width, height, bits, data, null);
        }

        if (type == 2)
        {
            return new Transform(type, width, height, 0, null, null);
        }

        int tableSize = reader.ReadBits(8) + 1;
        int bitsPerPixel = tableSize <= 2 ? 3 : tableSize <= 4 ? 2 : tableSize <= 16 ? 1 : 0;
        int tableWidth = tableSize;
        int[] tableData = DecodeImageStream(ref reader, tableWidth, 1, hasMetaPrefix: false);
        var table = new int[tableSize];
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

        return new Transform(type, width, height, bitsPerPixel, null, table);
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

        int cacheBits = 0;
        if (reader.ReadBit() != 0)
        {
            cacheBits = reader.ReadBits(4);
            if (cacheBits is < 1 or > 11)
            {
                throw new InvalidDataException("The VP8L color cache size is invalid.");
            }
        }

        int[]? entropy = null;
        int entropyWidth = 0;
        int prefixBits = 0;
        if (hasMetaPrefix && reader.ReadBit() != 0)
        {
            prefixBits = reader.ReadBits(3) + 2;
            entropyWidth = (width + (1 << prefixBits) - 1) >> prefixBits;
            int entropyHeight = (height + (1 << prefixBits) - 1) >> prefixBits;
            entropy = DecodeImageStream(ref reader, entropyWidth, entropyHeight, hasMetaPrefix: false);
        }

        int groupCount = 1;
        if (entropy is not null)
        {
            int max = 0;
            for (int i = 0; i < entropy.Length; i++)
            {
                max = Math.Max(max, (entropy[i] >> 8) & 0xffff);
            }

            groupCount = max + 1;
        }

        var groups = new PrefixGroup[groupCount];
        int greenAlphabet = 256 + 24 + (cacheBits == 0 ? 0 : 1 << cacheBits);
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i] = new PrefixGroup(
                ReadPrefixCode(ref reader, greenAlphabet),
                ReadPrefixCode(ref reader, 256),
                ReadPrefixCode(ref reader, 256),
                ReadPrefixCode(ref reader, 256),
                ReadPrefixCode(ref reader, 40));
        }

        int[] result = new int[checked(width * height)];
        int[]? cache = cacheBits == 0 ? null : new int[1 << cacheBits];
        int position = 0;
        while (position < result.Length)
        {
            int x = position % width;
            int y = position / width;
            int group = entropy is null ? 0 :
                ((entropy[(y >> prefixBits) * entropyWidth + (x >> prefixBits)] >> 8) & 0xffff);
            if ((uint)group >= groups.Length)
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
                int distancePrefix = PrefixValue(ref reader, distanceCode);
                if (distanceCode < 0 || distanceCode >= 40)
                {
                    throw new InvalidDataException("The VP8L backward distance is invalid.");
                }

                int distance = Distance(distancePrefix, width);
                if (distance > position)
                {
                    throw new InvalidDataException("The VP8L backward distance is invalid.");
                }

                if (length > result.Length - position)
                {
                    throw new InvalidDataException("The VP8L backward copy exceeds the image.");
                }

                for (int i = 0; i < length; i++)
                {
                    int color = result[position - distance];
                    result[position++] = color;
                    AddCache(cache, color, cacheBits);
                }

                continue;
            }

            if (cache is null || symbol - 280 >= cache.Length)
            {
                throw new InvalidDataException("The VP8L color cache symbol is invalid.");
            }

            int cached = cache[symbol - 280];
            result[position++] = cached;
            AddCache(cache, cached, cacheBits);
        }

        return result;
    }

    private static PrefixCode ReadPrefixCode(ref BitReader reader, int alphabetSize)
    {
        int startBit = reader.BitPosition;
        var lengths = new int[alphabetSize];
        if (reader.ReadBit() != 0)
        {
            int count = reader.ReadBit() + 1;
            int firstWide = reader.ReadBit();
            int first = reader.ReadBits(1 + 7 * firstWide);
            if (first >= alphabetSize)
            {
                throw new InvalidDataException("The VP8L simple prefix symbol is out of range.");
            }

            lengths[first] = 1;
            if (count == 2)
            {
                int second = reader.ReadBits(8);
                if (second >= alphabetSize)
                {
                    throw new InvalidDataException("The VP8L simple prefix symbol is out of range.");
                }

                lengths[second] = 1;
            }

            return PrefixCode.Build(lengths);
        }

        int numberOfCodeLengths = 4 + reader.ReadBits(4);
        var codeLengthLengths = new int[19];
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
                $"{string.Join(',', codeLengthLengths)}.",
                exception);
        }
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
                if (index >= alphabetSize)
                {
                    throw new InvalidDataException(
                        $"The VP8L prefix alphabet {alphabetSize} starting at bit {startBit} " +
                        $"exceeds its output size at symbol {symbolsRead} (index {index}, " +
                        $"max_symbol {maxSymbol}).");
                }

                lengths[index++] = value;
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

                for (int i = 0; i < repeat; i++) lengths[index++] = previous;
            }
            else if (value == 17)
            {
                int repeat = 3 + reader.ReadBits(3);
                if (repeat > alphabetSize - index)
                {
                    throw new InvalidDataException(
                        $"The VP8L prefix alphabet {alphabetSize} starting at bit {startBit} " +
                        $"exceeds its output size at symbol {symbolsRead} (index {index}, " +
                        $"repeat {repeat}, max_symbol {maxSymbol}).");
                }

                for (int i = 0; i < repeat; i++) lengths[index++] = 0;
            }
            else if (value == 18)
            {
                int repeat = 11 + reader.ReadBits(7);
                if (repeat > alphabetSize - index)
                {
                    throw new InvalidDataException(
                        $"The VP8L prefix alphabet {alphabetSize} starting at bit {startBit} " +
                        $"exceeds its output size at symbol {symbolsRead} (index {index}, " +
                        $"repeat {repeat}, max_symbol {maxSymbol}).");
                }

                for (int i = 0; i < repeat; i++) lengths[index++] = 0;
            }
            else
            {
                throw new InvalidDataException("The VP8L code-length symbol is invalid.");
            }
        }

        return PrefixCode.Build(lengths);
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
            2 => ApplySubtractGreen(pixels),
            3 => ApplyColorIndexing(pixels, transform),
            _ => throw new InvalidDataException("Unknown VP8L transform."),
        };
    }

    private static int[] ApplyPredictor(int[] residual, Transform transform)
    {
        int width = transform.Width;
        int height = transform.Height;
        int[] output = (int[])residual.Clone();
        int blockWidth = (width + (1 << transform.Bits) - 1) >> transform.Bits;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                int left = x == 0 ? 0 : output[index - 1];
                int top = y == 0 ? 0 : output[index - width];
                int topLeft = x == 0 || y == 0 ? 0 : output[index - width - 1];
                int topRight = y == 0 ? 0 : x == width - 1
                    ? output[index - x]
                    : output[index - width + 1];
                int mode = (transform.Data![(y >> transform.Bits) * blockWidth + (x >> transform.Bits)] >> 8) & 0xff;
                int prediction = Predict(mode, x, y, width, left, top, topLeft, topRight);
                output[index] = AddArgb(residual[index], prediction);
            }
        }

        return output;
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

    private static int[] ApplyColorTransform(int[] pixels, Transform transform)
    {
        int[] output = (int[])pixels.Clone();
        int blockWidth = (transform.Width + (1 << transform.Bits) - 1) >> transform.Bits;
        for (int y = 0; y < transform.Height; y++)
        {
            for (int x = 0; x < transform.Width; x++)
            {
                int index = y * transform.Width + x;
                int cte = transform.Data![(y >> transform.Bits) * blockWidth + (x >> transform.Bits)];
                int green = (output[index] >> 8) & 0xff;
                int red = (output[index] >> 16) & 0xff;
                int blue = output[index] & 0xff;
                int redDelta = ColorDelta((cte >> 0) & 0xff, green);
                int blueDelta = ColorDelta((cte >> 8) & 0xff, green);
                int redForBlue = (red + redDelta) & 0xff;
                blue = (blue + blueDelta + ColorDelta((cte >> 16) & 0xff, redForBlue)) & 0xff;
                red = redForBlue;
                output[index] = (output[index] & unchecked((int)0xff00ff00)) | (red << 16) | blue;
            }
        }

        return output;
    }

    private static int[] ApplySubtractGreen(int[] pixels)
    {
        int[] output = (int[])pixels.Clone();
        for (int i = 0; i < output.Length; i++)
        {
            int green = (output[i] >> 8) & 0xff;
            int red = (((output[i] >> 16) & 0xff) + green) & 0xff;
            int blue = ((output[i] & 0xff) + green) & 0xff;
            output[i] = (output[i] & unchecked((int)0xff00ff00)) | (red << 16) | blue;
        }

        return output;
    }

    private static int[] ApplyColorIndexing(int[] pixels, Transform transform)
    {
        int packed = 1 << transform.Bits;
        int mask = (1 << (8 / packed)) - 1;
        int width = transform.Width;
        int encodedWidth = (width + packed - 1) / packed;
        var output = new int[checked(width * transform.Height)];
        for (int y = 0; y < transform.Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int packedPixel = pixels[y * encodedWidth + x / packed];
                int index = ((packedPixel >> 8) >> ((x % packed) * (8 / packed))) & mask;
                output[y * width + x] = index < transform.Table!.Length ? transform.Table[index] : 0;
            }
        }

        return output;
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

    private static byte[] ToRgba(int[] argb)
    {
        byte[] result = new byte[checked(argb.Length * 4)];
        for (int i = 0, j = 0; i < argb.Length; i++, j += 4)
        {
            result[j] = (byte)(argb[i] >> 16);
            result[j + 1] = (byte)(argb[i] >> 8);
            result[j + 2] = (byte)argb[i];
            result[j + 3] = (byte)(argb[i] >> 24);
        }

        return result;
    }

    private readonly record struct Transform(
        int Type,
        int Width,
        int Height,
        int Bits,
        int[]? Data,
        int[]? Table);

    private readonly record struct PrefixGroup(
        PrefixCode Green,
        PrefixCode Red,
        PrefixCode Blue,
        PrefixCode Alpha,
        PrefixCode Distance);

    private sealed class PrefixCode
    {
        private readonly Node[] _nodes;

        private PrefixCode(Node[] nodes) => _nodes = nodes;

        public static PrefixCode Build(int[] lengths)
        {
            var counts = new int[16];
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
                Node root = new();
                root.Zero = -1;
                root.One = -1;
                root.Symbol = onlySymbol;
                return new PrefixCode(new[] { root });
            }

            var next = new int[16];
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

            var nodes = new List<Node> { new() };
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
                        child = nodes.Count;
                        nodes.Add(new Node());
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

            return new PrefixCode(nodes.ToArray());
        }

        public int Decode(ref BitReader reader)
        {
            if (_nodes[0].Symbol >= 0)
            {
                return _nodes[0].Symbol;
            }

            int node = 0;
            for (int i = 0; i < 15; i++)
            {
                int bit = reader.ReadBit();
                node = bit == 0 ? _nodes[node].Zero : _nodes[node].One;
                if (node < 0)
                {
                    throw new InvalidDataException("The VP8L prefix code did not contain the input.");
                }

                if (_nodes[node].Symbol >= 0) return _nodes[node].Symbol;
            }

            throw new InvalidDataException("The VP8L prefix code is too deep.");
        }
    }

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
        private readonly ReadOnlySpan<byte> _data;
        private int _position;
        private uint _buffer;
        private int _bits;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
            _buffer = 0;
            _bits = 0;
        }

        public int BitPosition => _position * 8 - _bits;

        public int ReadBit() => ReadBits(1);

        public int ReadBits(int count)
        {
            if (count is < 0 or > 24)
            {
                throw new InvalidDataException("The VP8L bit count is invalid.");
            }

            while (_bits < count)
            {
                if (_position >= _data.Length)
                {
                    throw new InvalidDataException("The VP8L bitstream is truncated.");
                }

                _buffer |= (uint)_data[_position++] << _bits;
                _bits += 8;
            }

            int result = (int)(_buffer & ((1u << count) - 1));
            _buffer >>= count;
            _bits -= count;
            return result;
        }
    }
}
