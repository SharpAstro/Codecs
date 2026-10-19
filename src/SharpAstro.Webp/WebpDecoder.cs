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
    private static readonly Vector128<byte> ChromaPairShuffleIndices = Vector128.Create(
        (byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);

    public static bool TryReadInfo(ReadOnlySpan<byte> data, out WebpInfo info)
    {
        try
        {
            info = WebpContainer.Read(data).Info;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
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
        WebpContainer container = WebpContainer.Read(data);
        WebpInfo info = container.Info;
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
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
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
        ushort[] tRows = WebpBufferPool<ushort>.Rent(checked(chromaWidth * 2));
        byte[] chromaRows = WebpBufferPool<byte>.Rent(checked(width * 2));
        try
        {
            ref byte yBase = ref MemoryMarshal.GetArrayDataReference(yuv.Y);
            ref byte uBase = ref MemoryMarshal.GetArrayDataReference(yuv.U);
            ref byte vBase = ref MemoryMarshal.GetArrayDataReference(yuv.V);
            ref byte outputBase = ref MemoryMarshal.GetReference(output);
            ref byte alphaBase = ref MemoryMarshal.GetReference(alpha);
            bool hasAlpha = !alpha.IsEmpty;
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
                    Span<ushort> tU = tRows.AsSpan(0, chromaWidth);
                    Span<ushort> tV = tRows.AsSpan(chromaWidth, chromaWidth);
                    Span<byte> uUpsampled = chromaRows.AsSpan(0, width);
                    Span<byte> vUpsampled = chromaRows.AsSpan(width, width);
                    ReadOnlySpan<byte> currentU = MemoryMarshal.CreateReadOnlySpan(ref uRow, chromaWidth);
                    ReadOnlySpan<byte> currentV = MemoryMarshal.CreateReadOnlySpan(ref vRow, chromaWidth);
                    ReadOnlySpan<byte> adjacentUSpan = MemoryMarshal.CreateReadOnlySpan(ref adjacentU, chromaWidth);
                    ReadOnlySpan<byte> adjacentVSpan = MemoryMarshal.CreateReadOnlySpan(ref adjacentV, chromaWidth);
                    if (Vector128.IsHardwareAccelerated)
                    {
                        UpsampleFancyVector128(currentU, adjacentUSpan, tU, uUpsampled);
                        UpsampleFancyVector128(currentV, adjacentVSpan, tV, vUpsampled);
                    }
                    else
                    {
                        UpsampleFancyScalar(currentU, adjacentUSpan, tU, uUpsampled);
                        UpsampleFancyScalar(currentV, adjacentVSpan, tV, vUpsampled);
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

                if (Vector256.IsHardwareAccelerated)
                {
                    ConvertRowVector256Core(ref yRow, ref uScratchBase, ref vScratchBase,
                        ref alphaRow, hasAlpha, ref destinationRow, width);
                }
                else if (Vector128.IsHardwareAccelerated)
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
            WebpBufferPool<ushort>.Return(tRows);
            WebpBufferPool<byte>.Return(chromaRows);
        }
    }

    internal static void UpsampleFancyScalar(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> adjacent,
        Span<ushort> tRow,
        Span<byte> output)
    {
        ref byte currentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(current));
        ref byte adjacentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(adjacent));
        ref ushort tBase = ref MemoryMarshal.GetReference(tRow);
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        UpsampleFancyScalarCore(ref currentBase, ref adjacentBase, ref tBase,
            ref outputBase, current.Length, output.Length);
    }

    internal static void UpsampleFancyVector128(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> adjacent,
        Span<ushort> tRow,
        Span<byte> output)
    {
        ref byte currentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(current));
        ref byte adjacentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(adjacent));
        ref ushort tBase = ref MemoryMarshal.GetReference(tRow);
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        if (Vector128.IsHardwareAccelerated)
        {
            UpsampleFancyVector128Core(ref currentBase, ref adjacentBase, ref tBase,
                ref outputBase, current.Length, output.Length);
        }
        else
        {
            UpsampleFancyScalarCore(ref currentBase, ref adjacentBase, ref tBase,
                ref outputBase, current.Length, output.Length);
        }
    }

    private static void UpsampleFancyScalarCore(
        ref byte current,
        ref byte adjacent,
        ref ushort tRow,
        ref byte output,
        int chromaWidth,
        int width)
    {
        for (int column = 0; column < chromaWidth; column++)
        {
            Unsafe.Add(ref tRow, column) = (ushort)(
                3 * Unsafe.Add(ref current, column) + Unsafe.Add(ref adjacent, column));
        }

        ushort first = tRow;
        output = (byte)((4 * first + 8) >> 4);
        if (width > 1)
        {
            ushort next = chromaWidth > 1 ? Unsafe.Add(ref tRow, 1) : first;
            Unsafe.Add(ref output, 1) = (byte)((3 * first + next + 8) >> 4);
        }

        for (int column = 1; column < chromaWidth - 1; column++)
        {
            int currentT = Unsafe.Add(ref tRow, column);
            int outputColumn = column * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, column - 1) + 8) >> 4);
            Unsafe.Add(ref output, outputColumn + 1) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, column + 1) + 8) >> 4);
        }

        if (chromaWidth > 1)
        {
            int lastColumn = chromaWidth - 1;
            int last = Unsafe.Add(ref tRow, lastColumn);
            int outputColumn = lastColumn * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * last + Unsafe.Add(ref tRow, lastColumn - 1) + 8) >> 4);
            if (outputColumn + 1 < width)
            {
                Unsafe.Add(ref output, outputColumn + 1) = (byte)((4 * last + 8) >> 4);
            }
        }
    }

    private static void UpsampleFancyVector128Core(
        ref byte current,
        ref byte adjacent,
        ref ushort tRow,
        ref byte output,
        int chromaWidth,
        int width)
    {
        Vector128<ushort> three = Vector128.Create((ushort)3);
        int column = 0;
        for (; column <= chromaWidth - 16; column += 16)
        {
            Vector128<byte> currentBytes = Vector128.LoadUnsafe(ref current, (nuint)column);
            Vector128<byte> adjacentBytes = Vector128.LoadUnsafe(ref adjacent, (nuint)column);
            Vector128<ushort> currentLow = Vector128.WidenLower(currentBytes);
            Vector128<ushort> currentHigh = Vector128.WidenUpper(currentBytes);
            Vector128<ushort> adjacentLow = Vector128.WidenLower(adjacentBytes);
            Vector128<ushort> adjacentHigh = Vector128.WidenUpper(adjacentBytes);
            Vector128<ushort> tLow = Vector128.Add(Vector128.Multiply(currentLow, three), adjacentLow);
            Vector128<ushort> tHigh = Vector128.Add(Vector128.Multiply(currentHigh, three), adjacentHigh);
            ref ushort tChunk = ref Unsafe.Add(ref tRow, column);
            Vector128.StoreUnsafe(tLow, ref tChunk);
            Vector128.StoreUnsafe(tHigh, ref Unsafe.Add(ref tChunk, 8));
        }

        for (; column < chromaWidth; column++)
        {
            Unsafe.Add(ref tRow, column) = (ushort)(
                3 * Unsafe.Add(ref current, column) + Unsafe.Add(ref adjacent, column));
        }

        ushort first = tRow;
        output = (byte)((4 * first + 8) >> 4);
        if (width > 1)
        {
            ushort next = chromaWidth > 1 ? Unsafe.Add(ref tRow, 1) : first;
            Unsafe.Add(ref output, 1) = (byte)((3 * first + next + 8) >> 4);
        }

        int pair = 1;
        for (; pair <= chromaWidth - 9; pair += 8)
        {
            Vector128<ushort> currentT = Vector128.LoadUnsafe(ref tRow, (nuint)pair);
            Vector128<ushort> left = Vector128.LoadUnsafe(ref tRow, (nuint)(pair - 1));
            Vector128<ushort> right = Vector128.LoadUnsafe(ref tRow, (nuint)(pair + 1));
            Vector128<ushort> scaled = Vector128.Multiply(currentT, three);
            Vector128<ushort> even = Vector128.ShiftRightLogical(
                Vector128.Add(Vector128.Add(scaled, left), Vector128.Create((ushort)8)), 4);
            Vector128<ushort> odd = Vector128.ShiftRightLogical(
                Vector128.Add(Vector128.Add(scaled, right), Vector128.Create((ushort)8)), 4);
            Vector128<byte> separated = Vector128.Narrow(even, odd);
            Vector128<byte> interleaved = Vector128.Shuffle(separated, ChromaPairShuffleIndices);
            ref byte outputChunk = ref Unsafe.Add(ref output, pair * 2);
            Vector128.StoreUnsafe(interleaved, ref outputChunk);
        }

        for (; pair < chromaWidth - 1; pair++)
        {
            int currentT = Unsafe.Add(ref tRow, pair);
            int outputColumn = pair * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, pair - 1) + 8) >> 4);
            Unsafe.Add(ref output, outputColumn + 1) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, pair + 1) + 8) >> 4);
        }

        if (chromaWidth > 1)
        {
            int lastColumn = chromaWidth - 1;
            int last = Unsafe.Add(ref tRow, lastColumn);
            int outputColumn = lastColumn * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * last + Unsafe.Add(ref tRow, lastColumn - 1) + 8) >> 4);
            if (outputColumn + 1 < width)
            {
                Unsafe.Add(ref output, outputColumn + 1) = (byte)((4 * last + 8) >> 4);
            }
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

    internal static void ConvertRowVector256(
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
        ConvertRowVector256Core(ref yBase, ref uBase, ref vBase, ref alphaBase,
            !alpha.IsEmpty, ref outputBase, width);
    }

    private static void ConvertRowVector256Core(
        ref byte y,
        ref byte u,
        ref byte v,
        ref byte alpha,
        bool hasAlpha,
        ref byte output,
        int width)
    {
        int column = 0;
        for (; column <= width - 32; column += 32)
        {
            Vector256<byte> yBytes = Vector256.LoadUnsafe(ref y, (nuint)column);
            Vector256<byte> uBytes = Vector256.LoadUnsafe(ref u, (nuint)column);
            Vector256<byte> vBytes = Vector256.LoadUnsafe(ref v, (nuint)column);
            Vector256<byte> alphaBytes = hasAlpha
                ? Vector256.LoadUnsafe(ref alpha, (nuint)column)
                : Vector256.Create(byte.MaxValue);
            ConvertVector32(yBytes, uBytes, vBytes, alphaBytes, ref output, column * 4);
        }

        for (; column <= width - 16; column += 16)
        {
            Vector128<byte> yBytes = Vector128.LoadUnsafe(ref y, (nuint)column);
            Vector128<byte> uBytes = Vector128.LoadUnsafe(ref u, (nuint)column);
            Vector128<byte> vBytes = Vector128.LoadUnsafe(ref v, (nuint)column);
            Vector128<byte> alphaBytes = hasAlpha
                ? Vector128.LoadUnsafe(ref alpha, (nuint)column)
                : Vector128.Create(byte.MaxValue);
            ConvertVector16(yBytes, uBytes, vBytes, alphaBytes, ref output, column * 4);
        }

        for (; column < width; column++)
        {
            WritePixel(ref Unsafe.Add(ref output, column * 4),
                Unsafe.Add(ref y, column), Unsafe.Add(ref u, column), Unsafe.Add(ref v, column),
                hasAlpha ? Unsafe.Add(ref alpha, column) : (byte)255);
        }
    }

    private static void ConvertVector32(
        Vector256<byte> yBytes,
        Vector256<byte> uBytes,
        Vector256<byte> vBytes,
        Vector256<byte> alphaBytes,
        ref byte output,
        int outputOffset)
    {
        Vector256<ushort> yLow = Vector256.WidenLower(yBytes);
        Vector256<ushort> yHigh = Vector256.WidenUpper(yBytes);
        Vector256<ushort> uLow = Vector256.WidenLower(uBytes);
        Vector256<ushort> uHigh = Vector256.WidenUpper(uBytes);
        Vector256<ushort> vLow = Vector256.WidenLower(vBytes);
        Vector256<ushort> vHigh = Vector256.WidenUpper(vBytes);
        Vector256<ushort> alphaLow = Vector256.WidenLower(alphaBytes);
        Vector256<ushort> alphaHigh = Vector256.WidenUpper(alphaBytes);

        ConvertVector16Avx2(yLow, uLow, vLow, alphaLow, ref output, outputOffset);
        ConvertVector16Avx2(yHigh, uHigh, vHigh, alphaHigh, ref output, outputOffset + 64);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> MultiplyConstant(
        Vector256<ushort> value,
        ushort high,
        ushort low) =>
        Vector256.Add(
            Vector256.Multiply(value, Vector256.Create(high)),
            Vector256.ShiftRightLogical(
                Vector256.Multiply(value, Vector256.Create(low)), 8));

    private static void ConvertVector16Avx2(
        Vector256<ushort> y,
        Vector256<ushort> u,
        Vector256<ushort> v,
        Vector256<ushort> alpha,
        ref byte output,
        int outputOffset)
    {
        Vector256<ushort> yTerm = MultiplyConstant(y, 74, 133);
        Vector256<ushort> vRed = MultiplyConstant(v, 102, 37);
        Vector256<ushort> uGreen = MultiplyConstant(u, 25, 19);
        Vector256<ushort> vGreen = MultiplyConstant(v, 52, 8);
        Vector256<ushort> uBlue = MultiplyConstant(u, 129, 26);

        Vector256<ushort> redSum = Vector256.Add(yTerm, vRed);
        Vector256<ushort> red = Vector256.Min(
            Vector256.ShiftRightLogical(
                Vector256.Subtract(Vector256.Max(redSum, Vector256.Create((ushort)14234)),
                    Vector256.Create((ushort)14234)), 6),
            Vector256.Create((ushort)255));
        Vector256<ushort> greenNegative = Vector256.Add(uGreen, vGreen);
        Vector256<ushort> greenPositive = Vector256.Add(yTerm, Vector256.Create((ushort)8708));
        Vector256<ushort> green = Vector256.Min(
            Vector256.ShiftRightLogical(
                Vector256.Subtract(Vector256.Max(greenPositive, greenNegative), greenNegative), 6),
            Vector256.Create((ushort)255));
        Vector256<ushort> blueSum = Vector256.Add(yTerm, uBlue);
        Vector256<ushort> blue = Vector256.Min(
            Vector256.ShiftRightLogical(
                Vector256.Subtract(Vector256.Max(blueSum, Vector256.Create((ushort)17685)),
                    Vector256.Create((ushort)17685)), 6),
            Vector256.Create((ushort)255));

        Vector256<ushort> redGreen = Vector256.BitwiseOr(red, Vector256.ShiftLeft(green, 8));
        Vector256<ushort> blueAlpha = Vector256.BitwiseOr(blue, Vector256.ShiftLeft(alpha, 8));
        Vector256<uint> rgbaLow = Vector256.BitwiseOr(
            Vector256.WidenLower(redGreen),
            Vector256.ShiftLeft(Vector256.WidenLower(blueAlpha), 16));
        Vector256<uint> rgbaHigh = Vector256.BitwiseOr(
            Vector256.WidenUpper(redGreen),
            Vector256.ShiftLeft(Vector256.WidenUpper(blueAlpha), 16));

        ref uint output32 = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref output, outputOffset));
        Vector256.StoreUnsafe(rgbaLow, ref output32);
        Vector256.StoreUnsafe(rgbaHigh, ref Unsafe.Add(ref output32, 8));
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

        ConvertVector8(yLow, uLow, vLow, alphaLow, ref output, outputOffset);
        ConvertVector8(yHigh, uHigh, vHigh, alphaHigh, ref output, outputOffset + 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> MultiplyConstant(
        Vector128<ushort> value,
        ushort high,
        ushort low) =>
        Vector128.Add(
            Vector128.Multiply(value, Vector128.Create(high)),
            Vector128.ShiftRightLogical(
                Vector128.Multiply(value, Vector128.Create(low)), 8));

    private static void ConvertVector8(
        Vector128<ushort> y,
        Vector128<ushort> u,
        Vector128<ushort> v,
        Vector128<ushort> alpha,
        ref byte output,
        int outputOffset)
    {
        Vector128<ushort> yTerm = MultiplyConstant(y, 74, 133);
        Vector128<ushort> vRed = MultiplyConstant(v, 102, 37);
        Vector128<ushort> uGreen = MultiplyConstant(u, 25, 19);
        Vector128<ushort> vGreen = MultiplyConstant(v, 52, 8);
        Vector128<ushort> uBlue = MultiplyConstant(u, 129, 26);

        Vector128<ushort> redSum = Vector128.Add(yTerm, vRed);
        Vector128<ushort> red = Vector128.Min(
            Vector128.ShiftRightLogical(
                Vector128.Subtract(Vector128.Max(redSum, Vector128.Create((ushort)14234)),
                    Vector128.Create((ushort)14234)), 6),
            Vector128.Create((ushort)255));
        Vector128<ushort> greenNegative = Vector128.Add(uGreen, vGreen);
        Vector128<ushort> greenPositive = Vector128.Add(yTerm, Vector128.Create((ushort)8708));
        Vector128<ushort> green = Vector128.Min(
            Vector128.ShiftRightLogical(
                Vector128.Subtract(Vector128.Max(greenPositive, greenNegative), greenNegative), 6),
            Vector128.Create((ushort)255));
        Vector128<ushort> blueSum = Vector128.Add(yTerm, uBlue);
        Vector128<ushort> blue = Vector128.Min(
            Vector128.ShiftRightLogical(
                Vector128.Subtract(Vector128.Max(blueSum, Vector128.Create((ushort)17685)),
                    Vector128.Create((ushort)17685)), 6),
            Vector128.Create((ushort)255));

        Vector128<ushort> redGreen = Vector128.BitwiseOr(red, Vector128.ShiftLeft(green, 8));
        Vector128<ushort> blueAlpha = Vector128.BitwiseOr(blue, Vector128.ShiftLeft(alpha, 8));
        Vector128<uint> rgbaLow = Vector128.BitwiseOr(
            Vector128.WidenLower(redGreen),
            Vector128.ShiftLeft(Vector128.WidenLower(blueAlpha), 16));
        Vector128<uint> rgbaHigh = Vector128.BitwiseOr(
            Vector128.WidenUpper(redGreen),
            Vector128.ShiftLeft(Vector128.WidenUpper(blueAlpha), 16));

        ref uint output32 = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref output, outputOffset));
        Vector128.StoreUnsafe(rgbaLow, ref output32);
        Vector128.StoreUnsafe(rgbaHigh, ref Unsafe.Add(ref output32, 4));
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
