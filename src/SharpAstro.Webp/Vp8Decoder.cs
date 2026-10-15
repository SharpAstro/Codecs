using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpAstro.Webp;

internal static class Vp8Decoder
{
    public static Vp8Yuv Decode(ReadOnlySpan<byte> frame, int width, int height, bool applyLoopFilter = true)
        => DecodeCore(frame, width, height, applyLoopFilter, pooledOutput: false);

    internal static Vp8Yuv DecodePooled(ReadOnlySpan<byte> frame, int width, int height, bool applyLoopFilter = true)
        => DecodeCore(frame, width, height, applyLoopFilter, pooledOutput: true);

    private static Vp8Yuv DecodeCore(
        ReadOnlySpan<byte> frame,
        int width,
        int height,
        bool applyLoopFilter,
        bool pooledOutput)
    {
        if (frame.Length < 10)
        {
            throw new InvalidDataException("The VP8 key-frame header is truncated.");
        }

        uint frameTag = (uint)(frame[0] | frame[1] << 8 | frame[2] << 16);
        if ((frameTag & 1) != 0)
        {
            throw new NotSupportedException("VP8 inter-frame decoding lands in a later rung.");
        }

        int partitionLength = (int)(frameTag >> 5);
        if (partitionLength > frame.Length - 10 ||
            frame[3] != 0x9d || frame[4] != 0x01 || frame[5] != 0x2a)
        {
            throw new InvalidDataException("The VP8 key-frame header or first partition is invalid.");
        }

        int codedWidth = BinaryPrimitives.ReadUInt16LittleEndian(frame[6..8]) & 0x3fff;
        int codedHeight = BinaryPrimitives.ReadUInt16LittleEndian(frame[8..10]) & 0x3fff;
        if (codedWidth != width || codedHeight != height)
        {
            throw new InvalidDataException("The WebP and VP8 dimensions do not match.");
        }

        ReadOnlySpan<byte> headerPartition = frame.Slice(10, partitionLength);
        var header = new Vp8BoolDecoder(headerPartition);
        if (header.ReadBit(128) != 0)
        {
            throw new NotSupportedException("Reserved VP8 key-frame color-space flags are unsupported.");
        }

        _ = header.ReadBit(128);

        SegmentFeatures segments = ReadSegments(ref header);
        FilterSettings filter;
        try
        {
            filter = ReadFilter(ref header);
        }
        catch
        {
            ReturnSegmentBuffers(segments);
            throw;
        }

        byte[] coefficientProbabilities = Array.Empty<byte>();
        bool coefficientProbabilitiesRented = false;
        int partitionCount;
        Quantizers quantizers;
        bool skipFlagPresent;
        byte skipProbability;
        PartitionSlices partitions;
        try
        {
            partitionCount = 1 << header.ReadLiteral(2);
            quantizers = ReadQuantizers(ref header);
            _ = header.ReadBit(128);
            coefficientProbabilities = WebpBufferPool<byte>.Rent(4 * 8 * 3 * 11);
            coefficientProbabilitiesRented = true;
            Vp8Tables.CopyDefaultCoefficientProbabilities(coefficientProbabilities);
            ReadCoefficientProbabilityUpdates(ref header, coefficientProbabilities);
            skipFlagPresent = header.ReadBit(128) != 0;
            skipProbability = skipFlagPresent ? (byte)header.ReadLiteral(8) : (byte)0;

            ReadOnlySpan<byte> tokenFrame = frame[(10 + partitionLength)..];
            partitions = ReadPartitions(tokenFrame, partitionCount);
        }
        catch
        {
            if (coefficientProbabilitiesRented)
            {
                WebpBufferPool<byte>.Return(coefficientProbabilities);
            }

            ReturnSegmentBuffers(segments);
            ReturnFilterBuffers(filter);
            throw;
        }

        var tokens = new Vp8TokenPartitions(
            partitions.P0, partitions.P1, partitions.P2, partitions.P3,
            partitions.P4, partitions.P5, partitions.P6, partitions.P7);

        int mbWidth = (width + 15) >> 4;
        int mbHeight = (height + 15) >> 4;
        int paddedWidth = mbWidth * 16;
        int paddedHeight = mbHeight * 16;
        int yStride = paddedWidth + 2;
        int uvStride = (paddedWidth >> 1) + 2;
        int yLength = checked(yStride * (paddedHeight + 2));
        int uvLength = checked(uvStride * ((paddedHeight >> 1) + 2));
        byte[] yBuffer = WebpBufferPool<byte>.Rent(yLength);
        byte[] uBuffer = WebpBufferPool<byte>.Rent(uvLength);
        byte[] vBuffer = WebpBufferPool<byte>.Rent(uvLength);
        int mbCount = checked(mbWidth * mbHeight);
        byte[] modes = WebpBufferPool<byte>.Rent(mbCount);
        byte[] levels = WebpBufferPool<byte>.Rent(mbCount);
        byte[] nonzeroCoefficients = WebpBufferPool<byte>.Rent(mbCount);
        byte[] aboveB = WebpBufferPool<byte>.Rent(mbWidth * 4);
        byte[] aboveY = WebpBufferPool<byte>.Rent(mbWidth * 4);
        byte[] aboveU = WebpBufferPool<byte>.Rent(mbWidth * 2);
        byte[] aboveV = WebpBufferPool<byte>.Rent(mbWidth * 2);
        byte[] aboveY2 = WebpBufferPool<byte>.Rent(mbWidth);
        Span<byte> blockModes = stackalloc byte[16];
        Span<byte> leftB = stackalloc byte[4];
        Span<byte> leftY = stackalloc byte[4];
        Span<byte> leftU = stackalloc byte[2];
        Span<byte> leftV = stackalloc byte[2];
        byte leftY2 = 0;
        Span<short> y2Dc = stackalloc short[16];
        Span<int> coefficients = stackalloc int[16];
        Span<short> residual = stackalloc short[16];

        try
        {
            Array.Clear(yBuffer, 0, yLength);
            Array.Clear(uBuffer, 0, uvLength);
            Array.Clear(vBuffer, 0, uvLength);
            Array.Clear(aboveB);
            Array.Clear(aboveY);
            Array.Clear(aboveU);
            Array.Clear(aboveV);
            Array.Clear(aboveY2);
            InitializeBorders(yBuffer, yStride, paddedWidth, paddedHeight, 127, 129);
            InitializeBorders(uBuffer, uvStride, paddedWidth >> 1, paddedHeight >> 1, 127, 129);
            InitializeBorders(vBuffer, uvStride, paddedWidth >> 1, paddedHeight >> 1, 127, 129);

            for (int mbY = 0; mbY < mbHeight; mbY++)
            {
                leftB.Clear();
                leftY.Clear();
                leftU.Clear();
                leftV.Clear();
                leftY2 = 0;
                for (int mbX = 0; mbX < mbWidth; mbX++)
                {
                    int mbIndex = mbY * mbWidth + mbX;
                    int tokenPartition = mbY & (partitionCount - 1);
                    int segment = segments.Enabled && segments.UpdateMap ? ReadSegmentId(ref header, segments.MapProbabilities) : 0;
                    bool skip = skipFlagPresent && header.ReadBit(skipProbability) != 0;
                    byte yMode = (byte)ReadYMode(ref header);
                    blockModes.Clear();
                    if (yMode == 4)
                    {
                        for (int row = 0; row < 4; row++)
                        {
                            for (int column = 0; column < 4; column++)
                            {
                                byte above = row == 0 ? aboveB[mbX * 4 + column] : blockModes[(row - 1) * 4 + column];
                                byte left = column == 0 ? leftB[row] : blockModes[row * 4 + column - 1];
                                blockModes[row * 4 + column] = (byte)ReadBMode(ref header, above, left);
                            }
                        }

                        for (int column = 0; column < 4; column++)
                        {
                            aboveB[mbX * 4 + column] = blockModes[12 + column];
                        }

                        for (int row = 0; row < 4; row++)
                        {
                            leftB[row] = blockModes[row * 4 + 3];
                        }
                    }
                    else
                    {
                        byte derived = yMode switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 };
                        for (int i = 0; i < 4; i++)
                        {
                            aboveB[mbX * 4 + i] = derived;
                            leftB[i] = derived;
                        }
                    }

                    byte uvMode = (byte)ReadUvMode(ref header);
                    modes[mbIndex] = yMode;
                    bool hasNonzeroCoefficients = false;
                    int segmentQuant = segments.Enabled ? segments.Quant[segment] : 0;
                    int qIndex = Math.Clamp(segments.Absolute ? segmentQuant : quantizers.Base + segmentQuant, 0, 127);

                    int segmentFilter = segments.Enabled ? segments.Filter[segment] : 0;
                    int filterLevel = segments.Enabled && segments.Absolute
                        ? segmentFilter
                        : filter.Level + segmentFilter;
                    if (filter.DeltaEnabled)
                    {
                        filterLevel += filter.RefDeltas[0] + (yMode == 4 ? filter.ModeDeltas[0] : 0);
                    }

                    levels[mbIndex] = (byte)Math.Clamp(filterLevel, 0, 63);
                    int yDc = Vp8Tables.DcQuantizers[Math.Clamp(qIndex + quantizers.YDcDelta, 0, 127)];
                    int yAc = Vp8Tables.AcQuantizers[qIndex];
                    int y2DcQ = 2 * Vp8Tables.DcQuantizers[Math.Clamp(qIndex + quantizers.Y2DcDelta, 0, 127)];
                    int y2AcQ = Math.Max(8, 155 * Vp8Tables.AcQuantizers[Math.Clamp(qIndex + quantizers.Y2AcDelta, 0, 127)] / 100);
                    int uvDc = Math.Min(132, (int)Vp8Tables.DcQuantizers[Math.Clamp(qIndex + quantizers.UvDcDelta, 0, 127)]);
                    int uvAc = Vp8Tables.AcQuantizers[Math.Clamp(qIndex + quantizers.UvAcDelta, 0, 127)];
                    int x = 1 + mbX * 16;
                    int y = 1 + mbY * 16;
                    if (yMode != 4)
                    {
                        PredictLuma(yBuffer, yStride, x, y, yMode, blockModes, mbX, mbY, width);
                    }
                    int uvX = 1 + mbX * 8;
                    int uvY = 1 + mbY * 8;
                    PredictChroma(uBuffer, uvStride, uvX, uvY, uvMode);
                    PredictChroma(vBuffer, uvStride, uvX, uvY, uvMode);
                    if (yMode != 4)
                    {
                        if (!skip)
                        {
                            coefficients.Clear();
                            int y2Context = aboveY2[mbX] + leftY2;
                            bool y2Nonzero = DecodeCoefficients(ref tokens, tokenPartition,
                                coefficientProbabilities, 1, 0, y2Context, coefficients);
                            hasNonzeroCoefficients |= y2Nonzero;
                            aboveY2[mbX] = (byte)(y2Nonzero ? 1 : 0);
                            leftY2 = y2Nonzero ? (byte)1 : (byte)0;
                            if (y2Nonzero)
                            {
                                Dequantize(coefficients, y2DcQ, y2AcQ);
                                InverseWalsh(coefficients, y2Dc);
                            }
                            else
                            {
                                y2Dc.Clear();
                            }
                        }
                        else
                        {
                            aboveY2[mbX] = 0;
                            leftY2 = 0;
                            y2Dc.Clear();
                        }
                    }
                    else
                    {
                        y2Dc.Clear();
                    }

                    for (int blockY = 0; blockY < 4; blockY++)
                    {
                        for (int blockX = 0; blockX < 4; blockX++)
                        {
                            if (yMode == 4)
                            {
                                PredictSubblock(yBuffer, yStride, x + blockX * 4, y + blockY * 4,
                                    blockModes[blockY * 4 + blockX], mbX, mbY, width);
                            }

                            int context = aboveY[mbX * 4 + blockX] + leftY[blockY];
                            coefficients.Clear();
                            bool hasCoefficients = !skip && DecodeCoefficients(
                                ref tokens,
                                tokenPartition,
                                coefficientProbabilities,
                                yMode == 4 ? 3 : 0,
                                yMode == 4 ? 0 : 1,
                                context,
                                coefficients);
                            hasNonzeroCoefficients |= hasCoefficients;
                            aboveY[mbX * 4 + blockX] = leftY[blockY] = hasCoefficients ? (byte)1 : (byte)0;
                            if (yMode != 4)
                            {
                                coefficients[0] = y2Dc[blockY * 4 + blockX];
                            }

                            if (hasCoefficients || (yMode != 4 && coefficients[0] != 0))
                            {
                                bool hasAc = Dequantize(coefficients, yMode == 4 ? yDc : 1, yAc);
                                InverseDct(coefficients, residual, hasAc);
                                AddResidual(yBuffer, yStride, x + blockX * 4, y + blockY * 4, residual);
                            }
                        }
                    }

                    for (int plane = 0; plane < 2; plane++)
                    {
                        byte[] chroma = plane == 0 ? uBuffer : vBuffer;
                        byte[] above = plane == 0 ? aboveU : aboveV;
                        Span<byte> left = plane == 0 ? leftU : leftV;
                        for (int blockY = 0; blockY < 2; blockY++)
                        {
                            for (int blockX = 0; blockX < 2; blockX++)
                            {
                                int context = above[mbX * 2 + blockX] + left[blockY];
                                coefficients.Clear();
                                bool hasCoefficients = !skip && DecodeCoefficients(
                                    ref tokens, tokenPartition, coefficientProbabilities,
                                    2, 0, context, coefficients);
                                hasNonzeroCoefficients |= hasCoefficients;
                                above[mbX * 2 + blockX] = left[blockY] = hasCoefficients ? (byte)1 : (byte)0;
                                if (hasCoefficients)
                                {
                                    bool hasAc = Dequantize(coefficients, uvDc, uvAc);
                                    InverseDct(coefficients, residual, hasAc);
                                    AddResidual(chroma, uvStride, uvX + blockX * 4, uvY + blockY * 4, residual);
                                }
                            }
                        }
                    }
                    nonzeroCoefficients[mbIndex] = hasNonzeroCoefficients ? (byte)1 : (byte)0;
                }
            }

            if (applyLoopFilter)
            {
                ApplyLoopFilter(yBuffer, yStride, uBuffer, vBuffer, uvStride, mbWidth, mbHeight,
                    filter, levels, nonzeroCoefficients, modes);
            }
            int yPlaneLength = checked(width * height);
            int chromaWidth = (width + 1) >> 1;
            int chromaHeight = (height + 1) >> 1;
            int chromaPlaneLength = checked(chromaWidth * chromaHeight);
            byte[]? yPlane = null;
            byte[]? uPlane = null;
            byte[]? vPlane = null;
            try
            {
                yPlane = pooledOutput
                    ? WebpBufferPool<byte>.Rent(yPlaneLength)
                    : new byte[yPlaneLength];
                uPlane = pooledOutput
                    ? WebpBufferPool<byte>.Rent(chromaPlaneLength)
                    : new byte[chromaPlaneLength];
                vPlane = pooledOutput
                    ? WebpBufferPool<byte>.Rent(chromaPlaneLength)
                    : new byte[chromaPlaneLength];
                for (int row = 0; row < height; row++)
                {
                    yBuffer.AsSpan((row + 1) * yStride + 1, width).CopyTo(yPlane.AsSpan(row * width));
                }

                for (int row = 0; row < chromaHeight; row++)
                {
                    uBuffer.AsSpan((row + 1) * uvStride + 1, chromaWidth).CopyTo(uPlane.AsSpan(row * chromaWidth));
                    vBuffer.AsSpan((row + 1) * uvStride + 1, chromaWidth).CopyTo(vPlane.AsSpan(row * chromaWidth));
                }

                var result = new Vp8Yuv(width, height, yPlane, uPlane, vPlane, pooledOutput);
                yPlane = null;
                uPlane = null;
                vPlane = null;
                return result;
            }
            finally
            {
                if (pooledOutput)
                {
                    if (yPlane is not null) WebpBufferPool<byte>.Return(yPlane);
                    if (uPlane is not null) WebpBufferPool<byte>.Return(uPlane);
                    if (vPlane is not null) WebpBufferPool<byte>.Return(vPlane);
                }
            }
        }
        finally
        {
            WebpBufferPool<byte>.Return(yBuffer);
            WebpBufferPool<byte>.Return(uBuffer);
            WebpBufferPool<byte>.Return(vBuffer);
            WebpBufferPool<byte>.Return(modes);
            WebpBufferPool<byte>.Return(levels);
            WebpBufferPool<byte>.Return(nonzeroCoefficients);
            WebpBufferPool<byte>.Return(aboveB);
            WebpBufferPool<byte>.Return(aboveY);
            WebpBufferPool<byte>.Return(aboveU);
            WebpBufferPool<byte>.Return(aboveV);
            WebpBufferPool<byte>.Return(aboveY2);
            WebpBufferPool<byte>.Return(coefficientProbabilities);
            ReturnSegmentBuffers(segments);
            ReturnFilterBuffers(filter);
        }
    }

    private static void ReturnSegmentBuffers(SegmentFeatures segments)
    {
        if (segments.Quant is not null)
        {
            WebpBufferPool<int>.Return(segments.Quant);
        }

        if (segments.Filter is not null)
        {
            WebpBufferPool<int>.Return(segments.Filter);
        }

        if (segments.MapProbabilities is not null)
        {
            WebpBufferPool<byte>.Return(segments.MapProbabilities);
        }
    }

    private static void ReturnFilterBuffers(FilterSettings filter)
    {
        if (filter.RefDeltas is not null)
        {
            WebpBufferPool<sbyte>.Return(filter.RefDeltas);
        }

        if (filter.ModeDeltas is not null)
        {
            WebpBufferPool<sbyte>.Return(filter.ModeDeltas);
        }
    }

    private static SegmentFeatures ReadSegments(ref Vp8BoolDecoder decoder)
    {
        bool enabled = decoder.ReadBit(128) != 0;
        if (!enabled)
        {
            return default;
        }

        bool updateMap = decoder.ReadBit(128) != 0;
        bool updateData = decoder.ReadBit(128) != 0;
        bool absolute = false;
        int[]? quant = null;
        int[]? filter = null;
        byte[]? mapProbabilities = null;
        try
        {
            quant = WebpBufferPool<int>.Rent(4);
            filter = WebpBufferPool<int>.Rent(4);
            Array.Clear(quant, 0, 4);
            Array.Clear(filter, 0, 4);
            if (updateData)
            {
                absolute = decoder.ReadBit(128) != 0;
                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        quant[i] = decoder.ReadSignedLiteral(7);
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        filter[i] = decoder.ReadSignedLiteral(6);
                    }
                }
            }

            mapProbabilities = WebpBufferPool<byte>.Rent(3);
            mapProbabilities.AsSpan(0, 3).Fill(255);
            if (updateMap)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        mapProbabilities[i] = (byte)decoder.ReadLiteral(8);
                    }
                }
            }

            return new SegmentFeatures(enabled, updateMap, absolute, quant, filter, mapProbabilities);
        }
        catch
        {
            if (quant is not null) WebpBufferPool<int>.Return(quant);
            if (filter is not null) WebpBufferPool<int>.Return(filter);
            if (mapProbabilities is not null) WebpBufferPool<byte>.Return(mapProbabilities);
            throw;
        }
    }

    private static FilterSettings ReadFilter(ref Vp8BoolDecoder decoder)
    {
        bool simple = decoder.ReadBit(128) != 0;
        int level = decoder.ReadLiteral(6);
        int sharpness = decoder.ReadLiteral(3);
        sbyte[]? refs = null;
        sbyte[]? modes = null;
        try
        {
            refs = WebpBufferPool<sbyte>.Rent(4);
            modes = WebpBufferPool<sbyte>.Rent(4);
            Array.Clear(refs, 0, 4);
            Array.Clear(modes, 0, 4);
            bool deltaEnabled = decoder.ReadBit(128) != 0;
            if (deltaEnabled && decoder.ReadBit(128) != 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        refs[i] = (sbyte)decoder.ReadSignedLiteral(6);
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        modes[i] = (sbyte)decoder.ReadSignedLiteral(6);
                    }
                }
            }

            return new FilterSettings(simple, level, sharpness, deltaEnabled, refs, modes);
        }
        catch
        {
            if (refs is not null) WebpBufferPool<sbyte>.Return(refs);
            if (modes is not null) WebpBufferPool<sbyte>.Return(modes);
            throw;
        }
    }

    private static Quantizers ReadQuantizers(ref Vp8BoolDecoder decoder)
    {
        int baseIndex = decoder.ReadLiteral(7);
        return new Quantizers(
            baseIndex,
            ReadDelta(ref decoder),
            ReadDelta(ref decoder),
            ReadDelta(ref decoder),
            ReadDelta(ref decoder),
            ReadDelta(ref decoder));
    }

    private static int ReadDelta(ref Vp8BoolDecoder decoder) =>
        decoder.ReadBit(128) == 0 ? 0 : decoder.ReadSignedLiteral(4);

    private static void ReadCoefficientProbabilityUpdates(ref Vp8BoolDecoder decoder, byte[] probabilities)
    {
        int updateIndex = 0;
        for (int type = 0; type < 4; type++)
        {
            for (int band = 0; band < 8; band++)
            {
                for (int context = 0; context < 3; context++)
                {
                    for (int node = 0; node < 11; node++)
                    {
                        if (decoder.ReadBit(Vp8Tables.CoefficientUpdateProbabilities[type][band][context][node]) != 0)
                        {
                            probabilities[updateIndex] = (byte)decoder.ReadLiteral(8);
                        }

                        updateIndex++;
                    }
                }
            }
        }
    }

    private static int ReadSegmentId(ref Vp8BoolDecoder decoder, byte[] probabilities) =>
        ReadTree(ref decoder, Vp8Tables.SegmentIdTree, probabilities);

    private static int ReadYMode(ref Vp8BoolDecoder decoder) =>
        ReadTree(ref decoder, Vp8Tables.KeyFrameYModeTree, Vp8Tables.KeyFrameYModeProbabilities);

    private static int ReadUvMode(ref Vp8BoolDecoder decoder) =>
        ReadTree(ref decoder, Vp8Tables.UvModeTree, Vp8Tables.KeyFrameUvModeProbabilities);

    private static int ReadBMode(ref Vp8BoolDecoder decoder, int above, int left) =>
        ReadTree(ref decoder, Vp8Tables.BlockModeTree, Vp8Tables.KeyFrameBlockModeProbabilities[above][left]);

    private static int ReadTree(ref Vp8BoolDecoder decoder, ReadOnlySpan<sbyte> tree,
        ReadOnlySpan<byte> probabilities, int firstNode = 0)
    {
        int node = firstNode;
        while (true)
        {
            int child = tree[node + decoder.ReadBit(probabilities[node >> 1])];
            if (child <= 0)
            {
                return -child;
            }

            node = child;
        }
    }

    private static PartitionSlices ReadPartitions(ReadOnlySpan<byte> data, int count)
    {
        ReadOnlySpan<byte> p0 = default, p1 = default, p2 = default, p3 = default;
        ReadOnlySpan<byte> p4 = default, p5 = default, p6 = default, p7 = default;
        int sizeCount = count - 1;
        int tableLength = sizeCount * 3;
        if (data.Length < tableLength)
        {
            throw new InvalidDataException("The VP8 token partition size table is truncated.");
        }

        Span<int> sizes = stackalloc int[7];
        for (int i = 0; i < sizeCount; i++)
        {
            int sizeOffset = i * 3;
            sizes[i] = data[sizeOffset] | data[sizeOffset + 1] << 8 | data[sizeOffset + 2] << 16;
        }

        int offset = tableLength;
        for (int i = 0; i < count; i++)
        {
            int size = i == sizeCount ? data.Length - offset : sizes[i];
            if (size > data.Length - offset)
            {
                throw new InvalidDataException("A VP8 token partition exceeds the frame.");
            }

            ReadOnlySpan<byte> part = data.Slice(offset, size);
            switch (i)
            {
                case 0: p0 = part; break;
                case 1: p1 = part; break;
                case 2: p2 = part; break;
                case 3: p3 = part; break;
                case 4: p4 = part; break;
                case 5: p5 = part; break;
                case 6: p6 = part; break;
                case 7: p7 = part; break;
            }

            offset += size;
        }

        return new PartitionSlices(p0, p1, p2, p3, p4, p5, p6, p7);
    }

    private static bool DecodeCoefficients(ref Vp8TokenPartitions tokens, int partition,
        byte[] probabilities, int type, int start, int context, scoped Span<int> coefficients)
    {
        int position = start;
        int nodeContext = Math.Clamp(context, 0, 2);
        bool any = false;
        bool previousWasZero = false;
        Vp8BoolDecoder decoder = tokens.GetPartition(partition);
        while (position < 16)
        {
            int band = Vp8Tables.CoefficientBands[position];
            int offset = (((type * 8 + band) * 3 + nodeContext) * 11);
            ReadOnlySpan<byte> nodeProbabilities = probabilities.AsSpan(offset, 11);
            int tokenType = ReadTree(ref decoder, Vp8Tables.CoefficientTree, nodeProbabilities, previousWasZero ? 2 : 0);
            if (tokenType == 1)
            {
                break;
            }

            if (tokenType == 0)
            {
                nodeContext = 0;
                previousWasZero = true;
                position++;
                continue;
            }

            int token = tokenType switch
            {
                2 => 1,
                3 => 2,
                4 => 3,
                5 => 4,
                6 => 5 + ReadExtra(ref decoder, Vp8Tables.Pcat1),
                7 => 7 + ReadExtra(ref decoder, Vp8Tables.Pcat2),
                8 => 11 + ReadExtra(ref decoder, Vp8Tables.Pcat3),
                9 => 19 + ReadExtra(ref decoder, Vp8Tables.Pcat4),
                10 => 35 + ReadExtra(ref decoder, Vp8Tables.Pcat5),
                11 => 67 + ReadExtra(ref decoder, Vp8Tables.Pcat6),
                _ => throw new InvalidDataException("The VP8 coefficient token is invalid."),
            };
            int signed = decoder.ReadBit(128) != 0 ? -token : token;
            coefficients[Vp8Tables.Zigzag[position]] = signed;
            any = true;
            nodeContext = token == 1 ? 1 : 2;
            previousWasZero = false;
            position++;
        }

        tokens.SetPartition(partition, decoder);
        return any;
    }

    private static int ReadExtra(ref Vp8BoolDecoder decoder, ReadOnlySpan<byte> probabilities)
    {
        int value = 0;
        for (int i = 0; i < probabilities.Length && probabilities[i] != 0; i++)
        {
            value = (value << 1) | decoder.ReadBit(probabilities[i]);
        }

        return value;
    }

    private static bool Dequantize(Span<int> coefficients, int dc, int ac)
    {
        coefficients[0] = unchecked((short)(coefficients[0] * dc));
        bool hasAc = false;
        for (int i = 1; i < 16; i++)
        {
            int value = unchecked((short)(coefficients[i] * ac));
            coefficients[i] = value;
            hasAc |= value != 0;
        }

        return hasAc;
    }

    private static void InverseWalsh(Span<int> input, Span<short> output)
    {
        Span<short> horizontal = stackalloc short[16];
        for (int column = 0; column < 4; column++)
        {
            int a = input[column] + input[12 + column];
            int b = input[4 + column] + input[8 + column];
            int c = input[4 + column] - input[8 + column];
            int d = input[column] - input[12 + column];
            horizontal[column] = unchecked((short)(a + b));
            horizontal[4 + column] = unchecked((short)(c + d));
            horizontal[8 + column] = unchecked((short)(a - b));
            horizontal[12 + column] = unchecked((short)(d - c));
        }

        for (int row = 0; row < 4; row++)
        {
            int i = row * 4;
            int a = horizontal[i] + horizontal[i + 3];
            int b = horizontal[i + 1] + horizontal[i + 2];
            int c = horizontal[i + 1] - horizontal[i + 2];
            int d = horizontal[i] - horizontal[i + 3];
            output[i] = (short)((a + b + 3) >> 3);
            output[i + 1] = (short)((c + d + 3) >> 3);
            output[i + 2] = (short)((a - b + 3) >> 3);
            output[i + 3] = (short)((d - c + 3) >> 3);
        }
    }

    internal static void InverseDct(Span<int> input, Span<short> output, bool hasAcCoefficients)
    {
        if (!hasAcCoefficients)
        {
            output.Fill((short)((input[0] + 4) >> 3));
            return;
        }

        InverseDctGeneral(input, output);
    }

    internal static void InverseDctGeneral(Span<int> input, Span<short> output)
    {
        Span<short> horizontal = stackalloc short[16];
        for (int column = 0; column < 4; column++)
        {
            Transform1D(input[column], input[8 + column], input[4 + column], input[12 + column],
                out int a, out int b, out int c, out int d);
            horizontal[column] = unchecked((short)a);
            horizontal[4 + column] = unchecked((short)b);
            horizontal[8 + column] = unchecked((short)c);
            horizontal[12 + column] = unchecked((short)d);
        }

        for (int row = 0; row < 4; row++)
        {
            int i = row * 4;
            Transform1D(horizontal[i], horizontal[i + 2], horizontal[i + 1], horizontal[i + 3],
                out int a, out int b, out int c, out int d);
            output[i] = (short)((a + 4) >> 3);
            output[i + 1] = (short)((b + 4) >> 3);
            output[i + 2] = (short)((c + 4) >> 3);
            output[i + 3] = (short)((d + 4) >> 3);
        }
    }

    private static void Transform1D(int p0, int p2, int p1, int p3,
        out int a, out int b, out int c, out int d)
    {
        int a0 = p0 + p2;
        int b0 = p0 - p2;
        int c0 = ((p1 * 35468) >> 16) - (p3 + ((p3 * 20091) >> 16));
        int d0 = p1 + ((p1 * 20091) >> 16) + ((p3 * 35468) >> 16);
        a = a0 + d0;
        b = b0 + c0;
        c = b0 - c0;
        d = a0 - d0;
    }

    private static void AddResidual(byte[] plane, int stride, int x, int y, ReadOnlySpan<short> residual)
    {
        for (int row = 0; row < 4; row++)
        {
            int offset = (y + row) * stride + x;
            for (int column = 0; column < 4; column++)
            {
                plane[offset + column] = (byte)Math.Clamp(plane[offset + column] + residual[row * 4 + column], 0, 255);
            }
        }
    }

    private static void InitializeBorders(byte[] plane, int stride, int width, int height, byte above, byte left)
    {
        plane.AsSpan(0, stride).Fill(above);
        for (int row = 1; row <= height; row++)
        {
            plane[row * stride] = left;
        }
    }

    private static void PredictLuma(byte[] plane, int stride, int x, int y, int mode, Span<byte> blockModes,
        int mbX, int mbY, int imageWidth)
    {
        PredictLargeBlock(plane, stride, x, y, 16, mode);
    }

    private static void PredictChroma(byte[] plane, int stride, int x, int y, int mode) =>
        PredictLargeBlock(plane, stride, x, y, 8, mode);

    private static void PredictLargeBlock(byte[] plane, int stride, int x, int y, int size, int mode)
    {
        int top = y * stride + x;
        int left = y * stride + x - 1;
        int topLeft = (y - 1) * stride + x - 1;
        if (mode == 1)
        {
            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++)
                {
                    plane[top + row * stride + column] = plane[top - stride + column];
                }
            }

            return;
        }

        if (mode == 2)
        {
            for (int row = 0; row < size; row++)
            {
                plane.AsSpan(top + row * stride, size).Fill(plane[left + row * stride]);
            }

            return;
        }

        if (mode == 3)
        {
            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++)
                {
                    plane[top + row * stride + column] = (byte)Math.Clamp(
                        plane[left + row * stride] + plane[top - stride + column] - plane[topLeft], 0, 255);
                }
            }

            return;
        }

        bool hasTop = y > 1;
        bool hasLeft = x > 1;
        int sum = 0;
        if (hasTop || hasLeft)
        {
            for (int i = 0; i < size; i++)
            {
                if (hasTop) sum += plane[top - stride + i];
                if (hasLeft) sum += plane[left + i * stride];
            }
        }

        int value = hasTop && hasLeft ? (sum + size) / (size * 2) :
            hasTop || hasLeft ? (sum + size / 2) / size : 128;
        plane.AsSpan(top, size).Fill((byte)value);
        for (int row = 1; row < size; row++)
        {
            plane.AsSpan(top + row * stride, size).Fill((byte)value);
        }
    }

    private static void PredictSubblock(byte[] plane, int stride, int x, int y, int mode,
        int mbX, int mbY, int imageWidth)
    {
        Span<byte> above = stackalloc byte[8];
        Span<byte> left = stackalloc byte[4];
        Span<byte> edge = stackalloc byte[9];
        int top = y * stride + x;
        for (int i = 0; i < 4; i++)
        {
            above[i] = plane[top - stride + i];
            left[i] = plane[top - 1 + i * stride];
        }

        bool rightEdge = (x - 1) % 16 >= 12;
        if (rightEdge)
        {
            int edgeY = mbY == 0 ? 0 : (mbY * 16) * stride + 1;
            int edgeX = 1 + mbX * 16 + 16;
            for (int i = 0; i < 4; i++)
            {
                int sourceX = Math.Min(edgeX + i, imageWidth);
                above[4 + i] = mbY == 0 ? (byte)127 : plane[edgeY - 1 + sourceX];
            }
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                above[4 + i] = plane[top - stride + 4 + i];
            }
        }

        byte p = plane[top - stride - 1];
        edge[0] = left[3];
        edge[1] = left[2];
        edge[2] = left[1];
        edge[3] = left[0];
        edge[4] = p;
        for (int i = 0; i < 4; i++)
        {
            edge[5 + i] = above[i];
        }

        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                int value = mode switch
                {
                    0 => SubblockDc(above, left),
                    1 => Clamp(left[row] + above[column] - p),
                    2 => Avg3(column == 0 ? p : above[column - 1], above[column], above[column + 1]),
                    3 => Avg3(row == 0 ? p : left[row - 1], left[row], left[Math.Min(row + 1, 3)]),
                    4 => LeftDown(above, row + column + 1),
                    5 => Avg3(edge[4 + column - row - 1], edge[4 + column - row], edge[4 + column - row + 1]),
                    6 => VerticalRight(edge, row, column),
                    7 => VerticalLeft(above, row, column),
                    8 => HorizontalDown(edge, row, column),
                    _ => HorizontalUp(left, row, column),
                };
                plane[top + row * stride + column] = (byte)value;
            }
        }
    }

    private static int SubblockDc(ReadOnlySpan<byte> above, ReadOnlySpan<byte> left)
    {
        int sum = 4;
        for (int i = 0; i < 4; i++) sum += above[i] + left[i];
        return sum >> 3;
    }

    private static int LeftDown(ReadOnlySpan<byte> above, int index) =>
        index < 7 ? Avg3(above[index - 1], above[index], above[index + 1]) : Avg3(above[6], above[7], above[7]);

    private static int VerticalRight(ReadOnlySpan<byte> edge, int row, int column)
    {
        if (row == 0) return Avg2(edge[4 + column], edge[5 + column]);
        if (row == 1) return Avg3(edge[3 + column], edge[4 + column], edge[5 + column]);
        if (row == 2) return column == 0 ? Avg3(edge[2], edge[3], edge[4]) : Avg2(edge[3 + column], edge[4 + column]);
        return Avg3(edge[column == 0 ? 1 : 2 + column], edge[column == 0 ? 2 : 3 + column], edge[column == 0 ? 3 : 4 + column]);
    }

    private static int VerticalLeft(ReadOnlySpan<byte> above, int row, int column) =>
        row switch
        {
            0 => Avg2(above[column], above[column + 1]),
            1 => Avg3(above[column], above[column + 1], above[column + 2]),
            2 when column < 3 => Avg2(above[column + 1], above[column + 2]),
            2 => Avg3(above[4], above[5], above[6]),
            3 => column == 3
                ? Avg3(above[5], above[6], above[7])
                : Avg3(above[column + 1], above[column + 2], above[column + 3]),
            _ => 0,
        };

    private static int HorizontalDown(ReadOnlySpan<byte> edge, int row, int column)
    {
        int index = row switch
        {
            0 => column switch { 0 => 3, 1 => 4, 2 => 5, _ => 6 },
            1 => column switch { 0 => 2, 1 => 3, 2 => 3, _ => 4 },
            2 => column switch { 0 => 1, 1 => 2, 2 => 2, _ => 3 },
            _ => column switch { 0 => 0, 1 => 1, 2 => 1, _ => 2 },
        };
        bool half = (row == 0 && column == 0) ||
                    (row == 1 && (column == 0 || column == 2)) ||
                    (row == 2 && (column == 0 || column == 2)) ||
                    (row == 3 && (column == 0 || column == 2));
        return half ? Avg2(edge[index], edge[index + 1]) : Avg3(edge[index - 1], edge[index], edge[index + 1]);
    }

    private static int HorizontalUp(ReadOnlySpan<byte> left, int row, int column)
    {
        if (row == 3 || (row == 2 && column >= 2)) return left[3];
        if (row == 0)
        {
            return column switch { 0 => Avg2(left[0], left[1]), 1 => Avg3(left[0], left[1], left[2]), 2 => Avg2(left[1], left[2]), _ => Avg3(left[1], left[2], left[3]) };
        }

        if (row == 1)
        {
            return column switch { 0 => Avg2(left[1], left[2]), 1 => Avg3(left[1], left[2], left[3]), 2 => Avg2(left[2], left[3]), _ => Avg3(left[2], left[3], left[3]) };
        }

        return column == 0 ? Avg2(left[2], left[3]) : Avg3(left[2], left[3], left[3]);
    }

    private static int Avg2(int a, int b) => (a + b + 1) >> 1;
    private static int Avg3(int a, int b, int c) => (a + 2 * b + c + 2) >> 2;
    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    private static void ApplyLoopFilter(byte[] y, int yStride, byte[] u, byte[] v, int uvStride,
        int mbWidth, int mbHeight, FilterSettings filter, byte[] levels, byte[] nonzeroCoefficients,
        byte[] modes)
    {
        for (int mbY = 0; mbY < mbHeight; mbY++)
        {
            for (int mbX = 0; mbX < mbWidth; mbX++)
            {
                int index = mbY * mbWidth + mbX;
                int level = levels[index];
                if (level == 0) continue;
                int interior = level;
                if (filter.Sharpness != 0)
                {
                    interior >>= filter.Sharpness > 4 ? 2 : 1;
                    interior = Math.Min(interior, 9 - filter.Sharpness);
                }

                interior = Math.Max(interior, 1);
                int edgeLimit = 2 * (level + 2) + interior;
                int subLimit = 2 * level + interior;
                int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                bool skipInner = nonzeroCoefficients[index] == 0 && modes[index] != 4;
                int x = 1 + mbX * 16;
                int yRow = 1 + mbY * 16;
                int uvX = 1 + mbX * 8;
                int uvY = 1 + mbY * 8;
                if (mbX > 0)
                {
                    FilterEdge(y, yStride, x, yRow, 16, true, true, filter.Simple, interior, edgeLimit, hev);
                    if (!filter.Simple)
                    {
                        FilterEdge(u, uvStride, uvX, uvY, 8, true, true, false, interior, edgeLimit, hev);
                        FilterEdge(v, uvStride, uvX, uvY, 8, true, true, false, interior, edgeLimit, hev);
                    }
                }

                if (!skipInner)
                {
                    for (int edge = 4; edge < 16; edge += 4)
                    {
                        FilterEdge(y, yStride, x + edge, yRow, 16, true, false, filter.Simple, interior, subLimit, hev);
                    }

                    if (!filter.Simple)
                    {
                        FilterEdge(u, uvStride, uvX + 4, uvY, 8, true, false, false, interior, subLimit, hev);
                        FilterEdge(v, uvStride, uvX + 4, uvY, 8, true, false, false, interior, subLimit, hev);
                    }
                }

                if (mbY > 0)
                {
                    FilterEdge(y, yStride, x, yRow, 16, false, true, filter.Simple, interior, edgeLimit, hev);
                    if (!filter.Simple)
                    {
                        FilterEdge(u, uvStride, uvX, uvY, 8, false, true, false, interior, edgeLimit, hev);
                        FilterEdge(v, uvStride, uvX, uvY, 8, false, true, false, interior, edgeLimit, hev);
                    }
                }

                if (!skipInner)
                {
                    for (int edge = 4; edge < 16; edge += 4)
                    {
                        FilterEdge(y, yStride, x, yRow + edge, 16, false, false, filter.Simple, interior, subLimit, hev);
                    }

                    if (!filter.Simple)
                    {
                        FilterEdge(u, uvStride, uvX, uvY + 4, 8, false, false, false, interior, subLimit, hev);
                        FilterEdge(v, uvStride, uvX, uvY + 4, 8, false, false, false, interior, subLimit, hev);
                    }
                }
            }
        }
    }

    private static void FilterEdge(byte[] plane, int stride, int x, int y, int length,
        bool vertical, bool macroblockEdge, bool simple, int interior, int edge, int hev)
    {
        ref byte center = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(plane), y * stride + x);
        if (vertical)
        {
            if (simple)
            {
                if (macroblockEdge) FilterVerticalSimpleMacroblock(ref center, stride, length, edge);
                else FilterVerticalSimpleInner(ref center, stride, length, edge);
            }
            else if (macroblockEdge)
            {
                FilterVerticalNormalMacroblock(ref center, stride, length, interior, edge, hev);
            }
            else
            {
                FilterVerticalNormalInner(ref center, stride, length, interior, edge, hev);
            }
        }
        else if (simple)
        {
            if (macroblockEdge) FilterHorizontalSimpleMacroblock(ref center, stride, length, edge);
            else FilterHorizontalSimpleInner(ref center, stride, length, edge);
        }
        else if (macroblockEdge)
        {
            FilterHorizontalNormalMacroblock(ref center, stride, length, interior, edge, hev);
        }
        else
        {
            FilterHorizontalNormalInner(ref center, stride, length, interior, edge, hev);
        }
    }

    private static void FilterHorizontalSimpleMacroblock(ref byte center, int stride, int length, int edge)
    {
        if (length == 16 && Vector128.IsHardwareAccelerated)
        {
            FilterHorizontalVector128(ref center, stride, simple: true, macroblockEdge: true,
                interior: 0, edge, hev: 0);
        }
        else
        {
            FilterSimpleScalar(ref center, stride, 1, length, edge);
        }
    }

    private static void FilterHorizontalSimpleInner(ref byte center, int stride, int length, int edge)
    {
        if (length == 16 && Vector128.IsHardwareAccelerated)
        {
            FilterHorizontalVector128(ref center, stride, simple: true, macroblockEdge: false,
                interior: 0, edge, hev: 0);
        }
        else
        {
            FilterSimpleScalar(ref center, stride, 1, length, edge);
        }
    }

    private static void FilterHorizontalNormalMacroblock(
        ref byte center, int stride, int length, int interior, int edge, int hev)
    {
        if (length == 16 && Vector128.IsHardwareAccelerated)
        {
            FilterHorizontalVector128(ref center, stride, simple: false, macroblockEdge: true,
                interior, edge, hev);
        }
        else
        {
            FilterNormalMacroblockScalar(ref center, stride, 1, length, interior, edge, hev);
        }
    }

    private static void FilterHorizontalNormalInner(
        ref byte center, int stride, int length, int interior, int edge, int hev)
    {
        if (length == 16 && Vector128.IsHardwareAccelerated)
        {
            FilterHorizontalVector128(ref center, stride, simple: false, macroblockEdge: false,
                interior, edge, hev);
        }
        else
        {
            FilterNormalInnerScalar(ref center, stride, 1, length, interior, edge, hev);
        }
    }

    private static void FilterVerticalSimpleMacroblock(ref byte center, int stride, int length, int edge) =>
        FilterSimpleScalar(ref center, 1, stride, length, edge);

    private static void FilterVerticalSimpleInner(ref byte center, int stride, int length, int edge) =>
        FilterSimpleScalar(ref center, 1, stride, length, edge);

    private static void FilterVerticalNormalMacroblock(
        ref byte center, int stride, int length, int interior, int edge, int hev) =>
        FilterNormalMacroblockScalar(ref center, 1, stride, length, interior, edge, hev);

    private static void FilterVerticalNormalInner(
        ref byte center, int stride, int length, int interior, int edge, int hev) =>
        FilterNormalInnerScalar(ref center, 1, stride, length, interior, edge, hev);

    internal static void FilterHorizontalScalar(
        byte[] plane, int stride, int x, int y, int length, bool simple,
        bool macroblockEdge, int interior, int edge, int hev)
    {
        ref byte center = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(plane), y * stride + x);
        if (simple)
        {
            FilterSimpleScalar(ref center, stride, 1, length, edge);
        }
        else if (macroblockEdge)
        {
            FilterNormalMacroblockScalar(ref center, stride, 1, length, interior, edge, hev);
        }
        else
        {
            FilterNormalInnerScalar(ref center, stride, 1, length, interior, edge, hev);
        }
    }

    internal static void FilterHorizontalVector128(
        byte[] plane, int stride, int x, int y, int length, bool simple,
        bool macroblockEdge, int interior, int edge, int hev)
    {
        ref byte center = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(plane), y * stride + x);
        if (length == 16)
        {
            FilterHorizontalVector128(ref center, stride, simple, macroblockEdge, interior, edge, hev);
        }
        else
        {
            FilterHorizontalScalar(plane, stride, x, y, length, simple,
                macroblockEdge, interior, edge, hev);
        }
    }

    private static void FilterSimpleScalar(
        ref byte center, int sampleStep, int pixelAdvance, int length, int edge)
    {
        for (int i = 0; i < length; i++)
        {
            ref byte sample = ref Unsafe.Add(ref center, i * pixelAdvance);
            int p1 = Unsafe.Add(ref sample, -2 * sampleStep);
            int p0 = Unsafe.Add(ref sample, -sampleStep);
            int q0 = sample;
            int q1 = Unsafe.Add(ref sample, sampleStep);
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > edge)
            {
                continue;
            }

            int adjustment = ClampSigned(ClampSigned(p1 - q1) + 3 * (q0 - p0));
            int qAdjustment = (adjustment + 4) >> 3;
            int pAdjustment = (adjustment + 3) >> 3;
            Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + pAdjustment, 0, 255);
            sample = (byte)Math.Clamp(q0 - qAdjustment, 0, 255);
        }
    }

    private static void FilterNormalMacroblockScalar(
        ref byte center, int sampleStep, int pixelAdvance, int length,
        int interior, int edge, int hev)
    {
        for (int i = 0; i < length; i++)
        {
            ref byte sample = ref Unsafe.Add(ref center, i * pixelAdvance);
            int p3 = Unsafe.Add(ref sample, -4 * sampleStep);
            int p2 = Unsafe.Add(ref sample, -3 * sampleStep);
            int p1 = Unsafe.Add(ref sample, -2 * sampleStep);
            int p0 = Unsafe.Add(ref sample, -sampleStep);
            int q0 = sample;
            int q1 = Unsafe.Add(ref sample, sampleStep);
            int q2 = Unsafe.Add(ref sample, 2 * sampleStep);
            int q3 = Unsafe.Add(ref sample, 3 * sampleStep);
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > edge ||
                Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior ||
                Math.Abs(p1 - p0) > interior || Math.Abs(q1 - q0) > interior ||
                Math.Abs(q2 - q1) > interior || Math.Abs(q3 - q2) > interior)
            {
                continue;
            }

            bool highVariance = Math.Abs(p1 - p0) > hev || Math.Abs(q1 - q0) > hev;
            int adjustment = ClampSigned(ClampSigned(p1 - q1) + 3 * (q0 - p0));
            if (highVariance)
            {
                int qAdjustment = (adjustment + 4) >> 3;
                int pAdjustment = (adjustment + 3) >> 3;
                Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + pAdjustment, 0, 255);
                sample = (byte)Math.Clamp(q0 - qAdjustment, 0, 255);
                continue;
            }

            int outer = (27 * adjustment + 63) >> 7;
            int middle = (18 * adjustment + 63) >> 7;
            int inner = (9 * adjustment + 63) >> 7;
            Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + outer, 0, 255);
            sample = (byte)Math.Clamp(q0 - outer, 0, 255);
            Unsafe.Add(ref sample, -2 * sampleStep) = (byte)Math.Clamp(p1 + middle, 0, 255);
            Unsafe.Add(ref sample, sampleStep) = (byte)Math.Clamp(q1 - middle, 0, 255);
            Unsafe.Add(ref sample, -3 * sampleStep) = (byte)Math.Clamp(p2 + inner, 0, 255);
            Unsafe.Add(ref sample, 2 * sampleStep) = (byte)Math.Clamp(q2 - inner, 0, 255);
        }
    }

    private static void FilterNormalInnerScalar(
        ref byte center, int sampleStep, int pixelAdvance, int length,
        int interior, int edge, int hev)
    {
        for (int i = 0; i < length; i++)
        {
            ref byte sample = ref Unsafe.Add(ref center, i * pixelAdvance);
            int p3 = Unsafe.Add(ref sample, -4 * sampleStep);
            int p2 = Unsafe.Add(ref sample, -3 * sampleStep);
            int p1 = Unsafe.Add(ref sample, -2 * sampleStep);
            int p0 = Unsafe.Add(ref sample, -sampleStep);
            int q0 = sample;
            int q1 = Unsafe.Add(ref sample, sampleStep);
            int q2 = Unsafe.Add(ref sample, 2 * sampleStep);
            int q3 = Unsafe.Add(ref sample, 3 * sampleStep);
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > edge ||
                Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior ||
                Math.Abs(p1 - p0) > interior || Math.Abs(q1 - q0) > interior ||
                Math.Abs(q2 - q1) > interior || Math.Abs(q3 - q2) > interior)
            {
                continue;
            }

            bool highVariance = Math.Abs(p1 - p0) > hev || Math.Abs(q1 - q0) > hev;
            int adjustment = highVariance
                ? ClampSigned(ClampSigned(p1 - q1) + 3 * (q0 - p0))
                : ClampSigned(3 * (q0 - p0));
            int qAdjustment = (adjustment + 4) >> 3;
            int pAdjustment = (adjustment + 3) >> 3;
            Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + pAdjustment, 0, 255);
            sample = (byte)Math.Clamp(q0 - qAdjustment, 0, 255);
            if (highVariance)
            {
                continue;
            }

            int inner = ((adjustment + 4 >> 3) + 1) >> 1;
            Unsafe.Add(ref sample, -2 * sampleStep) = (byte)Math.Clamp(p1 + inner, 0, 255);
            Unsafe.Add(ref sample, sampleStep) = (byte)Math.Clamp(q1 - inner, 0, 255);
        }
    }

    private static void FilterHorizontalVector128(
        ref byte center, int stride, bool simple, bool macroblockEdge,
        int interior, int edge, int hev)
    {
        Vector128<byte> p3Bytes = Vector128.LoadUnsafe(ref Unsafe.Add(ref center, -4 * stride));
        Vector128<byte> p2Bytes = Vector128.LoadUnsafe(ref Unsafe.Add(ref center, -3 * stride));
        Vector128<byte> p1Bytes = Vector128.LoadUnsafe(ref Unsafe.Add(ref center, -2 * stride));
        Vector128<byte> p0Bytes = Vector128.LoadUnsafe(ref Unsafe.Add(ref center, -stride));
        Vector128<byte> q0Bytes = Vector128.LoadUnsafe(ref center);
        Vector128<byte> q1Bytes = Vector128.LoadUnsafe(ref Unsafe.Add(ref center, stride));
        Vector128<byte> q2Bytes = Vector128.LoadUnsafe(ref Unsafe.Add(ref center, 2 * stride));
        Vector128<byte> q3Bytes = Vector128.LoadUnsafe(ref Unsafe.Add(ref center, 3 * stride));

        FilterHorizontalVector8(Vector128.WidenLower(p3Bytes).AsInt16(),
            Vector128.WidenLower(p2Bytes).AsInt16(), Vector128.WidenLower(p1Bytes).AsInt16(),
            Vector128.WidenLower(p0Bytes).AsInt16(), Vector128.WidenLower(q0Bytes).AsInt16(),
            Vector128.WidenLower(q1Bytes).AsInt16(), Vector128.WidenLower(q2Bytes).AsInt16(),
            Vector128.WidenLower(q3Bytes).AsInt16(), ref center, 0, stride,
            simple, macroblockEdge, interior, edge, hev);
        FilterHorizontalVector8(Vector128.WidenUpper(p3Bytes).AsInt16(),
            Vector128.WidenUpper(p2Bytes).AsInt16(), Vector128.WidenUpper(p1Bytes).AsInt16(),
            Vector128.WidenUpper(p0Bytes).AsInt16(), Vector128.WidenUpper(q0Bytes).AsInt16(),
            Vector128.WidenUpper(q1Bytes).AsInt16(), Vector128.WidenUpper(q2Bytes).AsInt16(),
            Vector128.WidenUpper(q3Bytes).AsInt16(), ref center, 8, stride,
            simple, macroblockEdge, interior, edge, hev);
    }

    private static void FilterHorizontalVector8(
        Vector128<short> p3, Vector128<short> p2, Vector128<short> p1, Vector128<short> p0,
        Vector128<short> q0, Vector128<short> q1, Vector128<short> q2, Vector128<short> q3,
        ref byte center, int offset, int stride,
        bool simple, bool macroblockEdge, int interior, int edge, int hev)
    {
        Vector128<short> p0q0 = Vector128.Abs(Vector128.Subtract(p0, q0));
        Vector128<short> p1q1 = Vector128.Abs(Vector128.Subtract(p1, q1));
        Vector128<short> edgeMetric = Vector128.Add(
            Vector128.Add(p0q0, p0q0),
            Vector128.ShiftRightLogical(p1q1.AsUInt16(), 1).AsInt16());
        Vector128<short> valid = Vector128.LessThanOrEqual(edgeMetric, Vector128.Create((short)edge));
        Vector128<short> highVariance = Vector128.BitwiseOr(
            Vector128.GreaterThan(Vector128.Abs(Vector128.Subtract(p1, p0)), Vector128.Create((short)hev)),
            Vector128.GreaterThan(Vector128.Abs(Vector128.Subtract(q1, q0)), Vector128.Create((short)hev)));
        Vector128<short> outerTap = ClampSignedVector(Vector128.Subtract(p1, q1));
        Vector128<short> threeDelta = Vector128.Multiply(
            Vector128.Subtract(q0, p0), Vector128.Create((short)3));
        Vector128<short> adjustment = simple || macroblockEdge
            ? ClampSignedVector(Vector128.Add(outerTap, threeDelta))
            : ClampSignedVector(threeDelta);

        if (simple)
        {
            Vector128<short> pAdjustment = Vector128.ShiftRightArithmetic(
                Vector128.Add(adjustment, Vector128.Create((short)3)), 3);
            Vector128<short> qAdjustment = Vector128.ShiftRightArithmetic(
                Vector128.Add(adjustment, Vector128.Create((short)4)), 3);
            Vector128<short> newP0 = ClampPixelVector(Vector128.Add(p0, pAdjustment));
            Vector128<short> newQ0 = ClampPixelVector(Vector128.Subtract(q0, qAdjustment));
            StoreFilterVector(ref Unsafe.Add(ref center, -stride), offset,
                Vector128.ConditionalSelect(valid, newP0, p0));
            StoreFilterVector(ref center, offset,
                Vector128.ConditionalSelect(valid, newQ0, q0));
            return;
        }

        Vector128<short> interiorValid = Vector128.LessThanOrEqual(
            Vector128.Abs(Vector128.Subtract(p3, p2)), Vector128.Create((short)interior));
        interiorValid = Vector128.BitwiseAnd(interiorValid, Vector128.LessThanOrEqual(
            Vector128.Abs(Vector128.Subtract(p2, p1)), Vector128.Create((short)interior)));
        interiorValid = Vector128.BitwiseAnd(interiorValid, Vector128.LessThanOrEqual(
            Vector128.Abs(Vector128.Subtract(p1, p0)), Vector128.Create((short)interior)));
        interiorValid = Vector128.BitwiseAnd(interiorValid, Vector128.LessThanOrEqual(
            Vector128.Abs(Vector128.Subtract(q1, q0)), Vector128.Create((short)interior)));
        interiorValid = Vector128.BitwiseAnd(interiorValid, Vector128.LessThanOrEqual(
            Vector128.Abs(Vector128.Subtract(q2, q1)), Vector128.Create((short)interior)));
        interiorValid = Vector128.BitwiseAnd(interiorValid, Vector128.LessThanOrEqual(
            Vector128.Abs(Vector128.Subtract(q3, q2)), Vector128.Create((short)interior)));
        valid = Vector128.BitwiseAnd(valid, interiorValid);

        Vector128<short> highAdjustment = ClampSignedVector(Vector128.Add(
            outerTap, threeDelta));
        Vector128<short> highPAdjustment = Vector128.ShiftRightArithmetic(
            Vector128.Add(highAdjustment, Vector128.Create((short)3)), 3);
        Vector128<short> highQAdjustment = Vector128.ShiftRightArithmetic(
            Vector128.Add(highAdjustment, Vector128.Create((short)4)), 3);
        Vector128<short> p0High = ClampPixelVector(Vector128.Add(p0, highPAdjustment));
        Vector128<short> q0High = ClampPixelVector(Vector128.Subtract(q0, highQAdjustment));
        Vector128<short> p1Low;
        Vector128<short> q1Low;
        Vector128<short> p2Low = p2;
        Vector128<short> q2Low = q2;
        Vector128<short> p0Low;
        Vector128<short> q0Low;
        if (macroblockEdge)
        {
            Vector128<short> outer = Vector128.ShiftRightArithmetic(
                Vector128.Add(Vector128.Multiply(highAdjustment, Vector128.Create((short)27)),
                    Vector128.Create((short)63)), 7);
            Vector128<short> middle = Vector128.ShiftRightArithmetic(
                Vector128.Add(Vector128.Multiply(highAdjustment, Vector128.Create((short)18)),
                    Vector128.Create((short)63)), 7);
            Vector128<short> inner = Vector128.ShiftRightArithmetic(
                Vector128.Add(Vector128.Multiply(highAdjustment, Vector128.Create((short)9)),
                    Vector128.Create((short)63)), 7);
            p0Low = ClampPixelVector(Vector128.Add(p0, outer));
            q0Low = ClampPixelVector(Vector128.Subtract(q0, outer));
            p1Low = ClampPixelVector(Vector128.Add(p1, middle));
            q1Low = ClampPixelVector(Vector128.Subtract(q1, middle));
            p2Low = ClampPixelVector(Vector128.Add(p2, inner));
            q2Low = ClampPixelVector(Vector128.Subtract(q2, inner));
        }
        else
        {
            Vector128<short> pAdjustment = Vector128.ShiftRightArithmetic(
                Vector128.Add(adjustment, Vector128.Create((short)3)), 3);
            Vector128<short> qAdjustment = Vector128.ShiftRightArithmetic(
                Vector128.Add(adjustment, Vector128.Create((short)4)), 3);
            p0Low = ClampPixelVector(Vector128.Add(p0, pAdjustment));
            q0Low = ClampPixelVector(Vector128.Subtract(q0, qAdjustment));
            Vector128<short> inner = Vector128.ShiftRightArithmetic(
                Vector128.Add(Vector128.ShiftRightArithmetic(
                    Vector128.Add(adjustment, Vector128.Create((short)4)), 3),
                    Vector128.Create((short)1)), 1);
            p1Low = ClampPixelVector(Vector128.Add(p1, inner));
            q1Low = ClampPixelVector(Vector128.Subtract(q1, inner));
        }

        Vector128<short> p0Filtered = Vector128.ConditionalSelect(highVariance, p0High, p0Low);
        Vector128<short> q0Filtered = Vector128.ConditionalSelect(highVariance, q0High, q0Low);
        Vector128<short> p1Filtered = Vector128.ConditionalSelect(highVariance, p1, p1Low);
        Vector128<short> q1Filtered = Vector128.ConditionalSelect(highVariance, q1, q1Low);
        StoreFilterVector(ref Unsafe.Add(ref center, -stride), offset,
            Vector128.ConditionalSelect(valid, p0Filtered, p0));
        StoreFilterVector(ref center, offset, Vector128.ConditionalSelect(valid, q0Filtered, q0));
        StoreFilterVector(ref Unsafe.Add(ref center, -2 * stride), offset,
            Vector128.ConditionalSelect(valid, p1Filtered, p1));
        StoreFilterVector(ref Unsafe.Add(ref center, stride), offset,
            Vector128.ConditionalSelect(valid, q1Filtered, q1));
        if (macroblockEdge)
        {
            StoreFilterVector(ref Unsafe.Add(ref center, -3 * stride), offset,
                Vector128.ConditionalSelect(valid, Vector128.ConditionalSelect(highVariance, p2, p2Low), p2));
            StoreFilterVector(ref Unsafe.Add(ref center, 2 * stride), offset,
                Vector128.ConditionalSelect(valid, Vector128.ConditionalSelect(highVariance, q2, q2Low), q2));
        }
    }

    private static Vector128<short> ClampSignedVector(Vector128<short> value) =>
        Vector128.Max(Vector128.Create((short)-128),
            Vector128.Min(value, Vector128.Create((short)127)));

    private static Vector128<short> ClampPixelVector(Vector128<short> value) =>
        Vector128.Max(Vector128<short>.Zero,
            Vector128.Min(value, Vector128.Create((short)255)));

    private static void StoreFilterVector(ref byte destination, int offset, Vector128<short> value)
    {
        Vector128<byte> packed = Vector128.Narrow(value.AsUInt16(), Vector128<ushort>.Zero);
        ulong bytes = packed.AsUInt64().GetElement(0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, offset), bytes);
    }

    private static int ClampSigned(int value) => Math.Clamp(value, -128, 127);

    private readonly record struct SegmentFeatures(bool Enabled, bool UpdateMap, bool Absolute,
        int[] Quant, int[] Filter, byte[] MapProbabilities);
    private readonly record struct FilterSettings(bool Simple, int Level, int Sharpness,
        bool DeltaEnabled, sbyte[] RefDeltas, sbyte[] ModeDeltas);
    private readonly record struct Quantizers(int Base, int YDcDelta, int Y2DcDelta,
        int Y2AcDelta, int UvDcDelta, int UvAcDelta);

    private readonly ref struct PartitionSlices
    {
        public PartitionSlices(ReadOnlySpan<byte> p0, ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2,
            ReadOnlySpan<byte> p3, ReadOnlySpan<byte> p4, ReadOnlySpan<byte> p5,
            ReadOnlySpan<byte> p6, ReadOnlySpan<byte> p7)
        {
            P0 = p0;
            P1 = p1;
            P2 = p2;
            P3 = p3;
            P4 = p4;
            P5 = p5;
            P6 = p6;
            P7 = p7;
        }

        public ReadOnlySpan<byte> P0 { get; }
        public ReadOnlySpan<byte> P1 { get; }
        public ReadOnlySpan<byte> P2 { get; }
        public ReadOnlySpan<byte> P3 { get; }
        public ReadOnlySpan<byte> P4 { get; }
        public ReadOnlySpan<byte> P5 { get; }
        public ReadOnlySpan<byte> P6 { get; }
        public ReadOnlySpan<byte> P7 { get; }
    }
}
