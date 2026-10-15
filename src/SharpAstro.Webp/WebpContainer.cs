using System.Buffers.Binary;

namespace SharpAstro.Webp;

internal readonly ref struct WebpContainer
{
    private const long MaximumPixels = 1L << 28;

    private WebpContainer(
        WebpInfo info,
        ReadOnlySpan<byte> vp8Bitstream,
        ReadOnlySpan<byte> iccProfile,
        ReadOnlySpan<byte> alphaData,
        bool hasAlphaChunk,
        WebpChunkLocation[] metadataChunks)
    {
        Info = info;
        Vp8Bitstream = vp8Bitstream;
        IccProfile = iccProfile;
        AlphaData = alphaData;
        HasAlphaChunk = hasAlphaChunk;
        MetadataChunks = metadataChunks;
    }

    public WebpInfo Info { get; }
    public ReadOnlySpan<byte> Vp8Bitstream { get; }
    public ReadOnlySpan<byte> IccProfile { get; }
    public ReadOnlySpan<byte> AlphaData { get; }
    public bool HasAlphaChunk { get; }
    public WebpChunkLocation[] MetadataChunks { get; }

    public static WebpContainer Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("The data does not have a WebP RIFF header.");
        }

        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        long riffEndLong = 8L + riffLength;
        if (riffLength < 4 || riffEndLong > data.Length)
        {
            throw new InvalidDataException("The WebP RIFF size exceeds the supplied data.");
        }

        // dwebp accepts bytes after the RIFF-declared end, but rejects a RIFF size that
        // truncates a file's chunks or extends beyond the supplied data.
        int riffEnd = (int)riffEndLong;
        bool extended = false;
        bool animationFlag = false;
        bool alphaFlag = false;
        bool hasAlphaChunk = false;
        bool hasAnimChunk = false;
        int frameCount = 0;
        int canvasWidth = 0;
        int canvasHeight = 0;
        int imageWidth = 0;
        int imageHeight = 0;
        int primaryOffset = -1;
        int primaryLength = 0;
        bool primaryLossless = false;
        int iccOffset = -1;
        int iccLength = 0;
        int alphaOffset = -1;
        int alphaLength = 0;
        var metadataChunks = new List<WebpChunkLocation>();
        int offset = 12;

        while (offset < riffEnd)
        {
            if (riffEnd - offset < 8)
            {
                throw new InvalidDataException("The WebP RIFF chunk header is truncated.");
            }

            ReadOnlySpan<byte> type = data.Slice(offset, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            long payloadEndLong = (long)offset + 8 + chunkLength;
            long nextChunkLong = payloadEndLong + (chunkLength & 1);
            if (payloadEndLong > riffEnd || nextChunkLong > riffEnd)
            {
                throw new InvalidDataException("A WebP RIFF chunk exceeds the declared container size.");
            }

            int payloadOffset = offset + 8;
            int payloadLength = checked((int)chunkLength);
            ReadOnlySpan<byte> payload = data.Slice(payloadOffset, payloadLength);

            if (type.SequenceEqual("VP8X"u8))
            {
                if (extended || payloadLength < 10)
                {
                    throw new InvalidDataException("The WebP VP8X chunk is invalid.");
                }

                extended = true;
                byte flags = payload[0];
                animationFlag = (flags & 0x02) != 0;
                alphaFlag = (flags & 0x10) != 0;
                canvasWidth = 1 + ReadUInt24(payload[4..7]);
                canvasHeight = 1 + ReadUInt24(payload[7..10]);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (primaryOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one image bitstream.");
                }

                (imageWidth, imageHeight) = ReadVp8Dimensions(payload);
                if (!extended)
                {
                    canvasWidth = imageWidth;
                    canvasHeight = imageHeight;
                }
                primaryOffset = payloadOffset;
                primaryLength = payloadLength;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (primaryOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one image bitstream.");
                }

                (imageWidth, imageHeight, bool hasAlpha) = ReadVp8LDimensions(payload);
                if (!extended)
                {
                    canvasWidth = imageWidth;
                    canvasHeight = imageHeight;
                }
                alphaFlag |= hasAlpha;
                primaryOffset = payloadOffset;
                primaryLength = payloadLength;
                primaryLossless = true;
            }
            else if (type.SequenceEqual("ALPH"u8))
            {
                if (hasAlphaChunk)
                {
                    throw new InvalidDataException("The WebP contains more than one ALPH chunk.");
                }

                hasAlphaChunk = true;
                alphaOffset = payloadOffset;
                alphaLength = payloadLength;
                metadataChunks.Add(new WebpChunkLocation("ALPH", payloadOffset, payloadLength));
            }
            else if (type.SequenceEqual("ICCP"u8))
            {
                if (iccOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one ICC profile.");
                }

                iccOffset = payloadOffset;
                iccLength = payloadLength;
                metadataChunks.Add(new WebpChunkLocation("ICCP", payloadOffset, payloadLength));
            }
            else if (type.SequenceEqual("EXIF"u8) || type.SequenceEqual("XMP "u8))
            {
                metadataChunks.Add(new WebpChunkLocation(
                    type.SequenceEqual("EXIF"u8) ? "EXIF" : "XMP ",
                    payloadOffset,
                    payloadLength));
            }
            else if (type.SequenceEqual("ANIM"u8))
            {
                hasAnimChunk = true;
                metadataChunks.Add(new WebpChunkLocation("ANIM", payloadOffset, payloadLength));
            }
            else if (type.SequenceEqual("ANMF"u8))
            {
                frameCount++;
                metadataChunks.Add(new WebpChunkLocation("ANMF", payloadOffset, payloadLength));
            }

            offset = checked((int)nextChunkLong);
        }

        if (offset != riffEnd || canvasWidth <= 0 || canvasHeight <= 0 || primaryOffset < 0)
        {
            throw new InvalidDataException("The WebP container has no valid image bitstream.");
        }

        if ((long)canvasWidth * canvasHeight > MaximumPixels)
        {
            throw new InvalidDataException("The WebP dimensions exceed the supported pixel limit.");
        }

        bool animated = animationFlag || hasAnimChunk || frameCount != 0;
        if (animated && (!hasAnimChunk || frameCount == 0))
        {
            throw new InvalidDataException("The WebP animation chunks are incomplete.");
        }

        if (frameCount == 0)
        {
            frameCount = 1;
        }

        var info = new WebpInfo(
            canvasWidth,
            canvasHeight,
            alphaFlag || hasAlphaChunk,
            animated ? WebpFormat.Animated : primaryLossless ? WebpFormat.Lossless : WebpFormat.Lossy,
            frameCount);
        return new WebpContainer(
            info,
            data.Slice(primaryOffset, primaryLength),
            iccOffset < 0 ? ReadOnlySpan<byte>.Empty : data.Slice(iccOffset, iccLength),
            alphaOffset < 0 ? ReadOnlySpan<byte>.Empty : data.Slice(alphaOffset, alphaLength),
            hasAlphaChunk,
            metadataChunks.ToArray());
    }

    private static (int Width, int Height) ReadVp8Dimensions(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10 || (payload[0] & 1) != 0 ||
            payload[3] != 0x9d || payload[4] != 0x01 || payload[5] != 0x2a)
        {
            throw new InvalidDataException("The VP8 key-frame header is invalid or truncated.");
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3fff;
        int height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3fff;
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("The VP8 dimensions must be nonzero.");
        }

        uint firstPartitionLength = (uint)(payload[0] | payload[1] << 8 | payload[2] << 16) >> 5;
        if (firstPartitionLength > payload.Length - 10)
        {
            throw new InvalidDataException("The VP8 first partition exceeds the frame.");
        }

        return (width, height);
    }

    private static (int Width, int Height, bool HasAlpha) ReadVp8LDimensions(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 0x2f)
        {
            throw new InvalidDataException("The VP8L header is invalid or truncated.");
        }

        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]);
        int width = 1 + (int)(bits & 0x3fff);
        int height = 1 + (int)((bits >> 14) & 0x3fff);
        bool hasAlpha = ((bits >> 28) & 1) != 0;
        if ((bits >> 29) != 0)
        {
            throw new InvalidDataException("The VP8L version is unsupported.");
        }

        return (width, height, hasAlpha);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data) =>
        data[0] | (data[1] << 8) | (data[2] << 16);
}

internal readonly record struct WebpChunkLocation(string FourCc, int Offset, int Length);
