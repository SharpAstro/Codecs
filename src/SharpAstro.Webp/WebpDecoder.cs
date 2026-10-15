using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpAstro.Webp;

/// <summary>
/// Decodes still and animated WebP images. Spec-derived from RFC 6386 / RFC 9649;
/// libwebp/libvpx source was not consulted — their binaries are used only as test
/// oracles. Written with AI assistance, whose training data may include libwebp.
/// </summary>
public static class WebpDecoder
{
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out WebpInfo info)
    {
        try
        {
            info = WebpContainer.Read(data).Info;
            return true;
        }
        catch (Exception)
        {
            info = default;
            return false;
        }
    }

    public static WebpInfo ReadInfo(ReadOnlySpan<byte> data) => WebpContainer.Read(data).Info;

    /// <summary>Decodes every frame, returning the full composited canvas for each one.</summary>
    /// <param name="data">A still or animated WebP image.</param>
    public static WebpAnimation DecodeAnimation(ReadOnlySpan<byte> data)
    {
        WebpContainer container = WebpContainer.Read(data);
        var decoder = new WebpAnimationDecoder(data.ToArray());
        byte[] canvas = new byte[checked(decoder.CanvasWidth * decoder.CanvasHeight * 4)];
        var frames = new List<WebpFrame>(decoder.FrameCount);
        while (decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo frame))
        {
            frames.Add(new WebpFrame(canvas.ToArray(), frame.TimestampMs, frame.DurationMs));
        }

        return new WebpAnimation(
            decoder.CanvasWidth,
            decoder.CanvasHeight,
            decoder.LoopCount,
            decoder.BackgroundColor,
            frames.AsReadOnly())
        {
            IccProfile = container.HasIccProfile ? container.IccProfile.ToArray() : null,
            Exif = container.HasExif ? container.Exif.ToArray() : null,
            Xmp = container.HasXmp ? container.Xmp.ToArray() : null,
        };
    }

    public static WebpImage Decode(ReadOnlySpan<byte> data)
    {
        WebpContainer container = WebpContainer.Read(data);
        WebpInfo info = container.Info;
        byte[] rgba;
        if (info.Format == WebpFormat.Animated)
        {
            rgba = DecodeFirstAnimationFrame(data, info.Width, info.Height);
        }
        else if (info.Format == WebpFormat.Lossless)
        {
            rgba = Vp8LDecoder.DecodeRgba(container.Vp8Bitstream);
        }
        else
        {
            rgba = new byte[checked(info.Width * info.Height * 4)];
            byte[]? alpha = container.HasAlphaChunk
                ? DecodeAndUnfilterAlpha(container.AlphaData, info.Width, info.Height)
                : null;
            try
            {
                using Vp8Yuv yuv = Vp8Decoder.DecodePooled(container.Vp8Bitstream, info.Width, info.Height);
                ReadOnlySpan<byte> alphaSpan = alpha is null
                    ? ReadOnlySpan<byte>.Empty
                    : alpha.AsSpan(0, checked(info.Width * info.Height));
                WriteRgba(yuv, rgba, checked(info.Width * 4), fancy: true, alphaSpan);
            }
            finally
            {
                if (alpha is not null)
                {
                    WebpBufferPool<byte>.Return(alpha);
                }
            }
        }

        byte[]? icc = container.HasIccProfile ? container.IccProfile.ToArray() : null;
        byte[]? exif = container.HasExif ? container.Exif.ToArray() : null;
        byte[]? xmp = container.HasXmp ? container.Xmp.ToArray() : null;
        return new WebpImage(info.Width, info.Height, info.HasAlpha, rgba, icc, exif, xmp);
    }

    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride = 0)
    {
        DecodeTo(data, rgba, stride, fancy: true);
    }

    private static void DecodeTo(
        ReadOnlySpan<byte> data,
        Span<byte> rgba,
        int stride,
        bool fancy)
    {
        WebpInfo info = ReadInfo(data);
        int minimumStride = checked(info.Width * 4);
        if (stride == 0)
        {
            stride = minimumStride;
        }

        if (stride < minimumStride)
        {
            throw new ArgumentException("The stride is smaller than one RGBA row.", nameof(stride));
        }

        int requiredLength = checked((info.Height - 1) * stride + minimumStride);
        if (rgba.Length < requiredLength)
        {
            throw new ArgumentException("The destination is too small for the decoded image.", nameof(rgba));
        }

        WebpContainer container = WebpContainer.Read(data);
        if (info.Format == WebpFormat.Animated)
        {
            byte[] firstFrame = DecodeFirstAnimationFrame(data, info.Width, info.Height, fancy);
            for (int row = 0; row < info.Height; row++)
            {
                firstFrame.AsSpan(row * minimumStride, minimumStride)
                    .CopyTo(rgba.Slice(row * stride, minimumStride));
            }

            return;
        }

        if (info.Format == WebpFormat.Lossless)
        {
            Vp8LDecoder.DecodeRgbaTo(container.Vp8Bitstream, rgba, stride);
        }
        else
        {
            byte[]? alpha = container.HasAlphaChunk
                ? DecodeAndUnfilterAlpha(container.AlphaData, info.Width, info.Height)
                : null;
            try
            {
                using Vp8Yuv yuv = Vp8Decoder.DecodePooled(container.Vp8Bitstream, info.Width, info.Height);
                ReadOnlySpan<byte> alphaSpan = alpha is null
                    ? ReadOnlySpan<byte>.Empty
                    : alpha.AsSpan(0, checked(info.Width * info.Height));
                WriteRgba(yuv, rgba, stride, fancy, alphaSpan);
            }
            finally
            {
                if (alpha is not null)
                {
                    WebpBufferPool<byte>.Return(alpha);
                }
            }
        }
    }

    public static bool TryDecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride = 0)
    {
        try
        {
            DecodeTo(data, rgba, stride);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static Vp8Yuv DecodeYuv(ReadOnlySpan<byte> data, bool applyLoopFilter = true)
    {
        WebpContainer container = WebpContainer.Read(data);
        if (container.Info.Format != WebpFormat.Lossy)
        {
            throw new NotSupportedException("VP8 YUV output is unavailable for lossless WebP.");
        }

        return Vp8Decoder.Decode(container.Vp8Bitstream, container.Info.Width, container.Info.Height, applyLoopFilter);
    }

    internal static void DecodeToNoFancy(ReadOnlySpan<byte> data, Span<byte> rgba)
    {
        DecodeTo(data, rgba, 0, fancy: false);
    }

    internal static byte[] DecodeFrameRgba(
        ReadOnlySpan<byte> bitstream,
        WebpFormat format,
        ReadOnlySpan<byte> alphaData,
        int width,
        int height,
        bool fancy = true)
    {
        if (format == WebpFormat.Lossless)
        {
            if (!alphaData.IsEmpty)
            {
                throw new InvalidDataException("A VP8L animation frame cannot have a separate ALPH chunk.");
            }

            return Vp8LDecoder.DecodeRgba(bitstream);
        }

        Vp8Yuv yuv = Vp8Decoder.Decode(bitstream, width, height);
        byte[] rgba = new byte[checked(width * height * 4)];
        byte[]? alpha = alphaData.IsEmpty
            ? null
            : DecodeAndUnfilterAlpha(alphaData, width, height);
        try
        {
            ReadOnlySpan<byte> alphaSpan = alpha is null
                ? ReadOnlySpan<byte>.Empty
                : alpha.AsSpan(0, checked(width * height));
            WriteRgba(yuv, rgba, checked(width * 4), fancy, alphaSpan);
        }
        finally
        {
            if (alpha is not null)
            {
                WebpBufferPool<byte>.Return(alpha);
            }
        }

        return rgba;
    }

    private static byte[] DecodeFirstAnimationFrame(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        bool fancy = true)
    {
        var decoder = new WebpAnimationDecoder(data.ToArray(), fancy);
        byte[] rgba = new byte[checked(width * height * 4)];
        if (!decoder.TryDecodeNextFrame(rgba, out _))
        {
            throw new InvalidDataException("The WebP animation has no frames.");
        }

        return rgba;
    }

    private static void WriteRgba(
        Vp8Yuv yuv,
        Span<byte> output,
        int stride,
        bool fancy,
        ReadOnlySpan<byte> alpha = default)
    {
        int width = yuv.Width;
        int height = yuv.Height;
        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        int[] tRows = WebpBufferPool<int>.Rent(checked(chromaWidth * 2));
        byte[] chromaRows = WebpBufferPool<byte>.Rent(checked(width * 2));
        try
        {
            ref byte yBase = ref MemoryMarshal.GetArrayDataReference(yuv.Y);
            ref byte uBase = ref MemoryMarshal.GetArrayDataReference(yuv.U);
            ref byte vBase = ref MemoryMarshal.GetArrayDataReference(yuv.V);
            ref byte outputBase = ref MemoryMarshal.GetReference(output);
            ref byte alphaBase = ref MemoryMarshal.GetReference(alpha);
            bool hasAlpha = !alpha.IsEmpty;
            ref int tUBase = ref MemoryMarshal.GetArrayDataReference(tRows);
            ref int tVBase = ref Unsafe.Add(ref tUBase, chromaWidth);
            ref byte uScratchBase = ref MemoryMarshal.GetArrayDataReference(chromaRows);
            ref byte vScratchBase = ref Unsafe.Add(ref uScratchBase, width);

            for (int row = 0; row < height; row++)
            {
                int chromaRow = row >> 1;
                int currentChromaIndex = chromaRow * chromaWidth;
                ref byte uRow = ref Unsafe.Add(ref uBase, currentChromaIndex);
                ref byte vRow = ref Unsafe.Add(ref vBase, currentChromaIndex);
                ref byte yRow = ref Unsafe.Add(ref yBase, row * width);
                ref byte destinationRow = ref Unsafe.Add(ref outputBase, row * stride);
                ref byte alphaRow = ref Unsafe.NullRef<byte>();
                if (hasAlpha)
                {
                    alphaRow = ref Unsafe.Add(ref alphaBase, row * width);
                }

                if (fancy)
                {
                    int adjacentRow = row % 2 == 0
                        ? (chromaRow == 0 ? 0 : chromaRow - 1)
                        : (chromaRow == chromaHeight - 1 ? chromaRow : chromaRow + 1);
                    int adjacentChromaIndex = adjacentRow * chromaWidth;
                    ref byte adjacentU = ref Unsafe.Add(ref uBase, adjacentChromaIndex);
                    ref byte adjacentV = ref Unsafe.Add(ref vBase, adjacentChromaIndex);
                    for (int column = 0; column < chromaWidth; column++)
                    {
                        int current = Unsafe.Add(ref uRow, column);
                        int adjacent = Unsafe.Add(ref adjacentU, column);
                        Unsafe.Add(ref tUBase, column) = 3 * current + adjacent;
                        current = Unsafe.Add(ref vRow, column);
                        adjacent = Unsafe.Add(ref adjacentV, column);
                        Unsafe.Add(ref tVBase, column) = 3 * current + adjacent;
                    }

                    Unsafe.Add(ref uScratchBase, 0) = (byte)((4 * tUBase + 8) >> 4);
                    Unsafe.Add(ref vScratchBase, 0) = (byte)((4 * tVBase + 8) >> 4);
                    int pairs = chromaWidth - 1;
                    for (int column = 0; column < pairs; column++)
                    {
                        int currentU = Unsafe.Add(ref tUBase, column);
                        int nextU = Unsafe.Add(ref tUBase, column + 1);
                        int currentV = Unsafe.Add(ref tVBase, column);
                        int nextV = Unsafe.Add(ref tVBase, column + 1);
                        int oddColumn = column * 2 + 1;
                        int evenColumn = oddColumn + 1;
                        Unsafe.Add(ref uScratchBase, oddColumn) = (byte)((3 * currentU + nextU + 8) >> 4);
                        Unsafe.Add(ref vScratchBase, oddColumn) = (byte)((3 * currentV + nextV + 8) >> 4);
                        Unsafe.Add(ref uScratchBase, evenColumn) = (byte)((3 * nextU + currentU + 8) >> 4);
                        Unsafe.Add(ref vScratchBase, evenColumn) = (byte)((3 * nextV + currentV + 8) >> 4);
                    }

                    if ((width & 1) == 0)
                    {
                        int lastColumn = width - 1;
                        Unsafe.Add(ref uScratchBase, lastColumn) =
                            (byte)((4 * Unsafe.Add(ref tUBase, chromaWidth - 1) + 8) >> 4);
                        Unsafe.Add(ref vScratchBase, lastColumn) =
                            (byte)((4 * Unsafe.Add(ref tVBase, chromaWidth - 1) + 8) >> 4);
                    }
                }
                else
                {
                    for (int chromaColumn = 0; chromaColumn < chromaWidth; chromaColumn++)
                    {
                        int u = Unsafe.Add(ref uRow, chromaColumn);
                        int v = Unsafe.Add(ref vRow, chromaColumn);
                        int column = chromaColumn * 2;
                        Unsafe.Add(ref uScratchBase, column) = (byte)u;
                        Unsafe.Add(ref vScratchBase, column) = (byte)v;
                        int nextColumn = column + 1;
                        if (nextColumn < width)
                        {
                            Unsafe.Add(ref uScratchBase, nextColumn) = (byte)u;
                            Unsafe.Add(ref vScratchBase, nextColumn) = (byte)v;
                        }
                    }
                }

                if (Vector128.IsHardwareAccelerated)
                {
                    ConvertRowVector128Core(ref yRow, ref uScratchBase, ref vScratchBase,
                        ref alphaRow, hasAlpha, ref destinationRow, width);
                }
                else
                {
                    ConvertRowScalarCore(ref yRow, ref uScratchBase, ref vScratchBase,
                        ref alphaRow, hasAlpha, ref destinationRow, width);
                }
            }
        }
        finally
        {
            WebpBufferPool<int>.Return(tRows);
            WebpBufferPool<byte>.Return(chromaRows);
        }
    }

    internal static void ConvertRowScalar(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha,
        Span<byte> output)
    {
        int width = y.Length;
        ref byte yBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(y));
        ref byte uBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(u));
        ref byte vBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(v));
        ref byte alphaBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(alpha));
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        ConvertRowScalarCore(ref yBase, ref uBase, ref vBase, ref alphaBase,
            !alpha.IsEmpty, ref outputBase, width);
    }

    internal static void ConvertRowVector128(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha,
        Span<byte> output)
    {
        int width = y.Length;
        ref byte yBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(y));
        ref byte uBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(u));
        ref byte vBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(v));
        ref byte alphaBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(alpha));
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        ConvertRowVector128Core(ref yBase, ref uBase, ref vBase, ref alphaBase,
            !alpha.IsEmpty, ref outputBase, width);
    }

    private static void ConvertRowScalarCore(
        ref byte y,
        ref byte u,
        ref byte v,
        ref byte alpha,
        bool hasAlpha,
        ref byte output,
        int width)
    {
        for (int column = 0; column < width; column++)
        {
            WritePixel(ref Unsafe.Add(ref output, column * 4),
                Unsafe.Add(ref y, column), Unsafe.Add(ref u, column), Unsafe.Add(ref v, column),
                hasAlpha ? Unsafe.Add(ref alpha, column) : (byte)255);
        }
    }

    private static void ConvertRowVector128Core(
        ref byte y,
        ref byte u,
        ref byte v,
        ref byte alpha,
        bool hasAlpha,
        ref byte output,
        int width)
    {
        int column = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; column <= width - 16; column += 16)
            {
                Vector128<byte> yBytes = Vector128.LoadUnsafe(ref y, (nuint)column);
                Vector128<byte> uBytes = Vector128.LoadUnsafe(ref u, (nuint)column);
                Vector128<byte> vBytes = Vector128.LoadUnsafe(ref v, (nuint)column);
                Vector128<byte> alphaBytes = hasAlpha
                    ? Vector128.LoadUnsafe(ref alpha, (nuint)column)
                    : Vector128.Create(byte.MaxValue);
                ConvertVector16(yBytes, uBytes, vBytes, alphaBytes,
                    ref output, column * 4);
            }
        }

        for (; column < width; column++)
        {
            WritePixel(ref Unsafe.Add(ref output, column * 4),
                Unsafe.Add(ref y, column), Unsafe.Add(ref u, column), Unsafe.Add(ref v, column),
                hasAlpha ? Unsafe.Add(ref alpha, column) : (byte)255);
        }
    }

    private static void ConvertVector16(
        Vector128<byte> yBytes,
        Vector128<byte> uBytes,
        Vector128<byte> vBytes,
        Vector128<byte> alphaBytes,
        ref byte output,
        int outputOffset)
    {
        Vector128<ushort> yLow = Vector128.WidenLower(yBytes);
        Vector128<ushort> yHigh = Vector128.WidenUpper(yBytes);
        Vector128<ushort> uLow = Vector128.WidenLower(uBytes);
        Vector128<ushort> uHigh = Vector128.WidenUpper(uBytes);
        Vector128<ushort> vLow = Vector128.WidenLower(vBytes);
        Vector128<ushort> vHigh = Vector128.WidenUpper(vBytes);
        Vector128<ushort> alphaLow = Vector128.WidenLower(alphaBytes);
        Vector128<ushort> alphaHigh = Vector128.WidenUpper(alphaBytes);

        ConvertVector4(Vector128.WidenLower(yLow), Vector128.WidenLower(uLow),
            Vector128.WidenLower(vLow), Vector128.WidenLower(alphaLow), ref output, outputOffset);
        ConvertVector4(Vector128.WidenUpper(yLow), Vector128.WidenUpper(uLow),
            Vector128.WidenUpper(vLow), Vector128.WidenUpper(alphaLow), ref output, outputOffset + 16);
        ConvertVector4(Vector128.WidenLower(yHigh), Vector128.WidenLower(uHigh),
            Vector128.WidenLower(vHigh), Vector128.WidenLower(alphaHigh), ref output, outputOffset + 32);
        ConvertVector4(Vector128.WidenUpper(yHigh), Vector128.WidenUpper(uHigh),
            Vector128.WidenUpper(vHigh), Vector128.WidenUpper(alphaHigh), ref output, outputOffset + 48);
    }

    private static void ConvertVector4(
        Vector128<uint> y,
        Vector128<uint> u,
        Vector128<uint> v,
        Vector128<uint> alpha,
        ref byte output,
        int outputOffset)
    {
        Vector128<uint> yTerm = Vector128.ShiftRightLogical(
            Vector128.Multiply(y, Vector128.Create(19077u)), 8);
        Vector128<uint> vRed = Vector128.ShiftRightLogical(
            Vector128.Multiply(v, Vector128.Create(26149u)), 8);
        Vector128<uint> uGreen = Vector128.ShiftRightLogical(
            Vector128.Multiply(u, Vector128.Create(6419u)), 8);
        Vector128<uint> vGreen = Vector128.ShiftRightLogical(
            Vector128.Multiply(v, Vector128.Create(13320u)), 8);
        Vector128<uint> uBlue = Vector128.ShiftRightLogical(
            Vector128.Multiply(u, Vector128.Create(33050u)), 8);

        Vector128<int> red = Vector128.ShiftRightArithmetic(
            Vector128.Subtract(Vector128.Add(yTerm, vRed).AsInt32(),
                Vector128.Create(14234)), 6);
        Vector128<int> green = Vector128.ShiftRightArithmetic(
            Vector128.Add(Vector128.Subtract(Vector128.Subtract(yTerm.AsInt32(), uGreen.AsInt32()),
                vGreen.AsInt32()), Vector128.Create(8708)), 6);
        Vector128<int> blue = Vector128.ShiftRightArithmetic(
            Vector128.Subtract(Vector128.Add(yTerm, uBlue).AsInt32(),
                Vector128.Create(17685)), 6);

        Vector128<uint> redClamped = Vector128.Max(Vector128<int>.Zero,
            Vector128.Min(red, Vector128.Create(255))).AsUInt32();
        Vector128<uint> greenClamped = Vector128.Max(Vector128<int>.Zero,
            Vector128.Min(green, Vector128.Create(255))).AsUInt32();
        Vector128<uint> blueClamped = Vector128.Max(Vector128<int>.Zero,
            Vector128.Min(blue, Vector128.Create(255))).AsUInt32();
        Vector128<uint> alphaClamped = Vector128.Min(alpha, Vector128.Create(255u));

        Vector128<ushort> red16 = Vector128.Narrow(redClamped, Vector128<uint>.Zero);
        Vector128<ushort> green16 = Vector128.Narrow(greenClamped, Vector128<uint>.Zero);
        Vector128<ushort> blue16 = Vector128.Narrow(blueClamped, Vector128<uint>.Zero);
        Vector128<ushort> alpha16 = Vector128.Narrow(alphaClamped, Vector128<uint>.Zero);
        Vector128<ushort> rg16 = Vector128.BitwiseOr(red16, Vector128.ShiftLeft(green16, 8));
        Vector128<ushort> ba16 = Vector128.BitwiseOr(blue16, Vector128.ShiftLeft(alpha16, 8));
        Vector128<uint> rg32 = Vector128.WidenLower(rg16);
        Vector128<uint> ba32 = Vector128.WidenLower(ba16);
        Vector128<uint> rgba = Vector128.BitwiseOr(rg32, Vector128.ShiftLeft(ba32, 16));
        ref uint output32 = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref output, outputOffset));
        Vector128.StoreUnsafe(rgba, ref output32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePixel(ref byte destination, int y, int u, int v, byte alpha)
    {
        int yTerm = y * 19077 >> 8;
        int red = (yTerm + (v * 26149 >> 8) - 14234) >> 6;
        int green = (yTerm - (u * 6419 >> 8) - (v * 13320 >> 8) + 8708) >> 6;
        int blue = (yTerm + (u * 33050 >> 8) - 17685) >> 6;
        Unsafe.Add(ref destination, 0) = (byte)(red < 0 ? 0 : red > 255 ? 255 : red);
        Unsafe.Add(ref destination, 1) = (byte)(green < 0 ? 0 : green > 255 ? 255 : green);
        Unsafe.Add(ref destination, 2) = (byte)(blue < 0 ? 0 : blue > 255 ? 255 : blue);
        Unsafe.Add(ref destination, 3) = alpha;
    }

    private static byte[] DecodeAndUnfilterAlpha(
        ReadOnlySpan<byte> alphaChunk,
        int width,
        int height)
    {
        if (alphaChunk.Length < 1)
        {
            throw new InvalidDataException("The WebP ALPH chunk is empty.");
        }

        int header = alphaChunk[0];
        int compression = header & 3;
        int filter = (header >> 2) & 3;
        int pixelCount = checked(width * height);
        byte[] values = WebpBufferPool<byte>.Rent(pixelCount);
        try
        {
            if (compression == 0)
            {
                if (alphaChunk.Length - 1 != pixelCount)
                {
                    throw new InvalidDataException("The raw WebP alpha stream has the wrong size.");
                }

                alphaChunk[1..].CopyTo(values);
            }
            else if (compression == 1)
            {
                Vp8LDecoder.DecodeAlphaTo(alphaChunk[1..], width, height, values.AsSpan(0, pixelCount));
            }
            else
            {
                throw new InvalidDataException("The WebP alpha compression method is unsupported.");
            }

            if (filter == 1)
            {
                for (int row = 0; row < height; row++)
                {
                    int offset = row * width;
                    int above = row == 0 ? 0 : values[offset - width];
                    values[offset] = (byte)(values[offset] + above);
                    for (int column = 1; column < width; column++)
                    {
                        values[offset + column] = (byte)(values[offset + column] + values[offset + column - 1]);
                    }
                }
            }
            else if (filter == 2)
            {
                for (int row = 0; row < height; row++)
                {
                    int offset = row * width;
                    if (row == 0)
                    {
                        values[offset] = (byte)(values[offset] + (byte)0);
                        for (int column = 1; column < width; column++)
                        {
                            values[offset + column] = (byte)(values[offset + column] + values[offset + column - 1]);
                        }
                    }
                    else
                    {
                        int aboveOffset = offset - width;
                        for (int column = 0; column < width; column++)
                        {
                            values[offset + column] = (byte)(values[offset + column] + values[aboveOffset + column]);
                        }
                    }
                }
            }
            else if (filter == 3)
            {
                for (int row = 0; row < height; row++)
                {
                    int offset = row * width;
                    if (row == 0)
                    {
                        for (int column = 1; column < width; column++)
                        {
                            values[offset + column] = (byte)(values[offset + column] + values[offset + column - 1]);
                        }
                    }
                    else
                    {
                        int aboveOffset = offset - width;
                        int above = values[aboveOffset];
                        values[offset] = (byte)(values[offset] + above);
                        for (int column = 1; column < width; column++)
                        {
                            int left = values[offset + column - 1];
                            int top = values[aboveOffset + column];
                            int topLeft = above;
                            int predictor = Math.Clamp(left + top - topLeft, 0, 255);
                            values[offset + column] = (byte)(values[offset + column] + predictor);
                            above = top;
                        }
                    }
                }
            }

            return values;
        }
        catch
        {
            WebpBufferPool<byte>.Return(values);
            throw;
        }
    }
}
