using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;

namespace SharpAstro.Png;

/// <summary>
/// Pure-managed PNG decoder — the dual of <see cref="PngWriter"/>. Parses
/// the PNG chunked container, validates CRC32 on every chunk, decompresses
/// the IDAT stream via <see cref="ZLibStream"/> a row at a time straight into
/// the samples, and defilters each row there with <see cref="PngPredictor"/>. Preserves
/// metadata that <c>SharpAstro.StbImage</c> discards: ICC profile, sRGB
/// declaration, gAMA / cHRM color hints, and EXIF.
/// </summary>
/// <remarks>
/// <para>Scope:</para>
/// <list type="bullet">
///   <item>Every bit depth PNG allows: 8 and 16 per sample, and 1, 2 and 4 for greyscale and
///   indexed, whose samples are unpacked to one byte each (see <see cref="PngImage.BitDepth"/>).</item>
///   <item>Color types 0 (Gray), 2 (RGB), 3 (Indexed, PLTE + tRNS), 4 (Gray + Alpha), 6 (RGBA).</item>
///   <item>Non-interlaced only (<c>InterlaceMethod = 0</c>).</item>
/// </list>
/// <para>Adam7 interlacing is deferred to a follow-up. PNG-3 HDR chunks (<c>cICP</c> / <c>mDCv</c> /
/// <c>cLLI</c>) are read and kept as they come.</para>
/// <para>APNG (animated PNG): the <c>acTL</c>, <c>fcTL</c> and <c>fdAT</c> chunks are parsed and validated, and
/// <see cref="PngImage.Animation"/> reports the frame count, play count, whether the <c>IDAT</c> image is the first
/// frame, and every frame's rectangle, delay, dispose and blend operation. Each frame's sub-image can be inflated and
/// unfiltered through the same path as the <c>IDAT</c> image (<see cref="PngImage.Pixels"/> stays the <c>IDAT</c>
/// image, which is what an APNG-unaware reader shows). <see cref="EnumerateFrames"/> and
/// <see cref="DecodeFrames(ReadOnlySpan{byte})"/> composite the frames onto the full canvas (rectangle offset,
/// <c>dispose_op</c> none/background/previous, <c>blend_op</c> source/over, delay as <c>delay_num</c>/<c>delay_den</c>)
/// and give RGBA8; <see cref="Decode"/> and <see cref="PngImageDecoder"/> still return the default image only. The
/// animation support is derived from the APNG specification's prose, and is checked against Pillow's encoder and
/// compositor (see <c>Fixtures/apng/README.md</c>).</para>
/// </remarks>
public static class PngReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The most a <c>zTXt</c> or compressed <c>iTXt</c> chunk may inflate to, in bytes (8 MiB, libpng's default
    /// chunk limit). A few kilobytes of deflate can claim gigabytes, so inflation stops here and the chunk is
    /// skipped, as a malformed text chunk is.
    /// </summary>
    public const int MaxInflatedTextBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The most an <c>iCCP</c> profile may inflate to, in bytes (16 MiB; real profiles are kilobytes to a few
    /// megabytes). Past it the decode fails with <see cref="InvalidDataException"/>, as a malformed <c>iCCP</c> does.
    /// </summary>
    public const int MaxInflatedIccProfileBytes = 16 * 1024 * 1024;

    /// <summary>
    /// <see cref="Decode"/> without the exceptions: false for a stream that is not a PNG, is corrupt (an
    /// animation whose chunks break the APNG rules included), or uses a feature this reader does not cover yet
    /// (Adam7 interlace). It makes no
    /// judgement about the colour signalling, so it is the shared first step of every projection — the
    /// RGBA8 display ones in <see cref="PngImageDecoder"/>, which refuse cICP PQ/HLG, and a float one
    /// that would decode those.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out PngImage? image)
    {
        try
        {
            image = Decode(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Parse a PNG byte stream into a <see cref="PngImage"/>. The returned
    /// <see cref="PngImage.Pixels"/> is unfiltered raw sample data in row-major
    /// order; 16-bit samples are returned in PNG's big-endian byte order to
    /// preserve bit patterns exactly (see <see cref="PngImage.AsUInt16Samples"/>
    /// for a native-endian view).
    /// </summary>
    /// <remarks>
    /// <para>For an APNG, <see cref="PngImage.Pixels"/> is the <c>IDAT</c> image, the default image, exactly as it was
    /// before animation chunks were read; the animation is reported in <see cref="PngImage.Animation"/>.</para>
    /// <para><b>A malformed animation is refused, not downgraded to its default image.</b> Once an <c>acTL</c> chunk
    /// stands in front of the <c>IDAT</c> the file claims to be an animation, and if its animation chunks then break
    /// the rules (a second or late <c>acTL</c>; sequence numbers that are not 0, 1, 2, ... in order across <c>fcTL</c>
    /// and <c>fdAT</c>; an empty frame or one outside the canvas; a dispose or blend operation that does not exist; a
    /// frame count that is not the number of <c>fcTL</c> chunks; a frame with no data; a first frame that does not
    /// cover the canvas when the <c>IDAT</c> is that frame) the decode throws <see cref="InvalidDataException"/> and
    /// <see cref="TryDecode"/> answers false. A still-image caller does not get the default image in that case,
    /// deliberately. The file's own animation metadata contradicts itself, so there is nothing to say which part to
    /// believe, and a silent fallback would hide the corruption from every caller that only ever asks for a still.
    /// The price is that a file with broken animation chunks, whose <c>IDAT</c> image used to decode, now fails; it is
    /// paid only by files that declare an animation with an <c>acTL</c>. <c>fcTL</c> and <c>fdAT</c> chunks with no
    /// <c>acTL</c> do not declare one, and are skipped as unknown ancillary chunks always were. A caller that wants the
    /// picture whatever the animation says can remove the <c>acTL</c> chunk and decode again.</para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The stream is not a PNG, is corrupt, or declares an animation that is malformed.</exception>
    public static PngImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("PNG signature missing — not a PNG file");

        int pos = Signature.Length;
        int? width = null, height = null, bitDepth = null, colorType = null,
             compressionMethod = null, filterMethod = null, interlaceMethod = null;
        // Where each IDAT's payload lies in the input: read in place, never copied out.
        var idatChunks = new List<(int Offset, int Length)>();
        // APNG. Everything below is only touched once an acTL has been seen in front of the IDAT: fcTL and fdAT
        // chunks without one do not declare an animation and are skipped like any unknown ancillary chunk.
        (int Frames, uint Plays)? actl = null;
        var frames = new List<PngFrameControl>();
        var frameData = new List<List<(int Offset, int Length)>>();     // per frame: payloads, fdAT's sequence number left out
        bool idatIsFirstFrame = false;      // the first fcTL came before the first IDAT
        uint nextSequence = 0;              // the number the next fcTL or fdAT must carry
        byte[]? palette = null;             // PLTE: 3 bytes (RGB) per entry
        byte[]? paletteAlpha = null;        // tRNS (indexed meaning): 1 alpha byte per entry
        byte[]? iccProfileRaw = null;       // zlib-deflated, as on disk
        string? iccProfileName = null;
        byte[]? rawExif = null;
        List<PngText>? text = null;
        bool sawSrgb = false;
        byte srgbRenderingIntent = 0;
        uint? gamma100k = null;
        ChromaticityChunk? chrm = null;
        CicpChunk? cicp = null;
        MdcvChunk? mdcv = null;
        ClliChunk? clli = null;

        while (pos < data.Length)
        {
            if (pos + 8 > data.Length)
                throw new InvalidDataException($"Truncated chunk header at byte {pos}");

            uint dataLen = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            // Spec says lengths are capped at 2^31-1; reject anything larger so we don't
            // wrap when adding to a position.
            if (dataLen > int.MaxValue)
                throw new InvalidDataException($"Chunk length {dataLen} exceeds int.MaxValue");
            int len = (int)dataLen;

            if (pos + 8 + len + 4 > data.Length)
                throw new InvalidDataException($"Chunk at byte {pos} extends past end of file");

            var typeBytes = data.Slice(pos + 4, 4);
            var chunkData = data.Slice(pos + 8, len);
            var declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 8 + len, 4));
            var computedCrc = Crc32(typeBytes, chunkData);
            if (declaredCrc != computedCrc)
                throw new InvalidDataException(
                    $"CRC mismatch on chunk '{TypeAscii(typeBytes)}' at byte {pos}: " +
                    $"declared 0x{declaredCrc:X8}, computed 0x{computedCrc:X8}");

            // --- chunk dispatch -----------------------------------------------------
            // Critical chunks (uppercase first letter) must be understood; ancillary
            // chunks (lowercase first letter) can be skipped if unrecognised.
            if (typeBytes.SequenceEqual("IHDR"u8))
            {
                if (width is not null)
                    throw new InvalidDataException("Multiple IHDR chunks");
                if (len != 13)
                    throw new InvalidDataException($"IHDR length must be 13, got {len}");
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                compressionMethod = chunkData[10];
                filterMethod = chunkData[11];
                interlaceMethod = chunkData[12];

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"IHDR dimensions must be positive, got {width}×{height}");
                if (compressionMethod != 0)
                    throw new InvalidDataException($"Unsupported compression method {compressionMethod} (PNG only defines 0)");
                if (filterMethod != 0)
                    throw new InvalidDataException($"Unsupported filter method {filterMethod} (PNG only defines 0)");
                if (interlaceMethod != 0)
                    throw new NotSupportedException("Adam7 interlacing not yet supported (InterlaceMethod=1)");
                ValidateColorTypeAndBitDepth(colorType.Value, bitDepth.Value);
            }
            else if (typeBytes.SequenceEqual("IDAT"u8))
            {
                if (width is null)
                    throw new InvalidDataException("IDAT before IHDR");
                idatChunks.Add((pos + 8, len));
                if (frames.Count > 0)
                {
                    // An fcTL already stands in front: the IDATs are its data only if it is the first frame's.
                    if (!idatIsFirstFrame || frames.Count > 1)
                        throw new InvalidDataException(
                            $"IDAT chunk at byte {pos} follows the fcTL of frame {frames.Count - 1}; only the first frame's data can be IDAT");
                    frameData[0].Add((pos + 8, len));
                }
            }
            else if (typeBytes.SequenceEqual("acTL"u8))
            {
                if (width is null)
                    throw new InvalidDataException("acTL before IHDR");
                if (actl is not null)
                    throw new InvalidDataException("Multiple acTL chunks");
                if (idatChunks.Count > 0)
                    throw new InvalidDataException($"acTL chunk at byte {pos} follows the first IDAT; it must come before");
                if (len != 8)
                    throw new InvalidDataException($"acTL length must be 8, got {len}");
                var frameCount = BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                if (frameCount is 0 or > int.MaxValue)
                    throw new InvalidDataException($"acTL frame count must be between 1 and {int.MaxValue}, got {frameCount}");
                actl = ((int)frameCount, BinaryPrimitives.ReadUInt32BigEndian(chunkData[4..]));
            }
            else if (typeBytes.SequenceEqual("fcTL"u8) && actl is { } declared)
            {
                if (len != 26)
                    throw new InvalidDataException($"fcTL length must be 26, got {len}");
                CheckSequenceNumber("fcTL", pos, chunkData, ref nextSequence);
                var frameWidth = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                var frameHeight = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(8, 4));
                var xOffset = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(12, 4));
                var yOffset = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(16, 4));
                var disposeOp = chunkData[24];
                var blendOp = chunkData[25];
                var n = frames.Count;
                if (n >= declared.Frames)
                    throw new InvalidDataException($"acTL declares {declared.Frames} frames but the file has more fcTL chunks");
                if (frameWidth == 0 || frameHeight == 0)
                    throw new InvalidDataException($"fcTL of frame {n} is empty ({frameWidth}×{frameHeight})");
                // The sums are in long: both offsets and sizes are 32-bit and an offset near the top would wrap an int.
                if ((long)xOffset + frameWidth > width!.Value || (long)yOffset + frameHeight > height!.Value)
                    throw new InvalidDataException(
                        $"Frame {n} ({frameWidth}×{frameHeight} at {xOffset},{yOffset}) does not fit the {width}×{height} canvas");
                if (disposeOp > (byte)PngDisposeOp.Previous)
                    throw new InvalidDataException($"Frame {n} has dispose_op {disposeOp}; only 0, 1 and 2 are defined");
                if (blendOp > (byte)PngBlendOp.Over)
                    throw new InvalidDataException($"Frame {n} has blend_op {blendOp}; only 0 and 1 are defined");
                if (n == 0)
                {
                    // An IDAT already seen means the default image is not part of the animation; otherwise the
                    // IDATs that follow this fcTL are its data, and the spec makes it the whole canvas.
                    idatIsFirstFrame = idatChunks.Count == 0;
                    if (idatIsFirstFrame && (xOffset != 0 || yOffset != 0 || frameWidth != width || frameHeight != height))
                        throw new InvalidDataException(
                            $"The first frame's IDAT is the default image, so its rectangle must be the whole canvas, " +
                            $"but it is {frameWidth}×{frameHeight} at {xOffset},{yOffset} on {width}×{height}");
                }
                frames.Add(new PngFrameControl(
                    SequenceNumber: (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]),
                    Width: (int)frameWidth,
                    Height: (int)frameHeight,
                    XOffset: (int)xOffset,
                    YOffset: (int)yOffset,
                    DelayNumerator: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(20, 2)),
                    DelayDenominator: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(22, 2)),
                    DisposeOp: (PngDisposeOp)disposeOp,
                    BlendOp: (PngBlendOp)blendOp));
                frameData.Add([]);
            }
            else if (typeBytes.SequenceEqual("fdAT"u8) && actl is not null)
            {
                if (len < 4)
                    throw new InvalidDataException($"fdAT length must be at least 4, got {len}");
                CheckSequenceNumber("fdAT", pos, chunkData, ref nextSequence);
                if (frames.Count == 0)
                    throw new InvalidDataException($"fdAT chunk at byte {pos} comes before any fcTL");
                if (idatIsFirstFrame && frames.Count == 1)
                    throw new InvalidDataException($"fdAT chunk at byte {pos} follows frame 0, whose data is the IDAT");
                // The first 4 bytes are the sequence number; the zlib data starts after them.
                frameData[^1].Add((pos + 8 + 4, len - 4));
            }
            else if (typeBytes.SequenceEqual("IEND"u8))
            {
                pos += 8 + len + 4;
                break;
            }
            else if (typeBytes.SequenceEqual("PLTE"u8))
            {
                if (len == 0 || len % 3 != 0)
                    throw new InvalidDataException($"PLTE length must be a positive multiple of 3, got {len}");
                palette = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("tRNS"u8))
            {
                // For indexed color (the only case this reader expands), tRNS is one
                // alpha byte per palette entry, in palette order; entries past its end
                // are fully opaque. For truecolor/gray tRNS carries a single transparent
                // sample — not handled here (only the indexed meaning is surfaced).
                paletteAlpha = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("iCCP"u8))
            {
                // iCCP layout: keyword (1..79 bytes) + null + compression method (1 byte) + compressed profile.
                var nullIdx = chunkData.IndexOf((byte)0);
                if (nullIdx <= 0)
                    throw new InvalidDataException("iCCP chunk missing keyword/profile separator");
                iccProfileName = System.Text.Encoding.Latin1.GetString(chunkData[..nullIdx]);
                if (nullIdx + 1 >= chunkData.Length)
                    throw new InvalidDataException("iCCP chunk truncated after keyword");
                if (chunkData[nullIdx + 1] != 0)
                    throw new InvalidDataException(
                        $"iCCP compression method must be 0, got {chunkData[nullIdx + 1]}");
                iccProfileRaw = DeflateInflate(chunkData[(nullIdx + 2)..], MaxInflatedIccProfileBytes)
                    ?? throw new InvalidDataException(
                        $"iCCP profile inflates past {MaxInflatedIccProfileBytes} bytes");
            }
            else if (typeBytes.SequenceEqual("sRGB"u8))
            {
                if (len != 1)
                    throw new InvalidDataException($"sRGB chunk length must be 1, got {len}");
                sawSrgb = true;
                srgbRenderingIntent = chunkData[0];
            }
            else if (typeBytes.SequenceEqual("gAMA"u8))
            {
                if (len != 4)
                    throw new InvalidDataException($"gAMA chunk length must be 4, got {len}");
                gamma100k = BinaryPrimitives.ReadUInt32BigEndian(chunkData);
            }
            else if (typeBytes.SequenceEqual("cHRM"u8))
            {
                if (len != 32)
                    throw new InvalidDataException($"cHRM chunk length must be 32, got {len}");
                chrm = ChromaticityChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("eXIf"u8))
            {
                rawExif = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("tEXt"u8) || typeBytes.SequenceEqual("zTXt"u8) || typeBytes.SequenceEqual("iTXt"u8))
            {
                // Text is never critical: a malformed text chunk is skipped, not a failed decode.
                if (TryReadText(typeBytes, chunkData) is { } entry)
                    (text ??= []).Add(entry);
            }
            else if (typeBytes.SequenceEqual("cICP"u8))
            {
                cicp = CicpChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("mDCV"u8)
                  || typeBytes.SequenceEqual("mDCv"u8))
            {
                // Canonical PNG-3 spec name is "mDCV" (uppercase V = not safe to copy).
                // Pre-final-spec drafts and some early implementations used the lowercase
                // "mDCv" form; accept both on read so we round-trip those legacy files
                // correctly. The writer emits the canonical "mDCV".
                mdcv = MdcvChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("cLLI"u8))
            {
                clli = ClliChunk.Read(chunkData);
            }
            // Any other chunk — silently skip. PNG is forwards-compatible by design:
            // ancillary unknown chunks are explicitly permitted, and critical unknown
            // chunks have a leading uppercase letter we'd need to reject. We defer the
            // critical-chunk rejection until we encounter a real one.

            pos += 8 + len + 4;
        }

        if (width is null) throw new InvalidDataException("No IHDR chunk found");
        if (idatChunks.Count == 0) throw new InvalidDataException("No IDAT chunks found");
        if (colorType == 3 && palette is null)
            throw new InvalidDataException("Indexed-color PNG missing required PLTE chunk");

        PngAnimation? animation = null;
        if (actl is { } counts)
        {
            if (frames.Count != counts.Frames)
                throw new InvalidDataException(
                    $"acTL declares {counts.Frames} frames but the file has {frames.Count} fcTL chunks");
            for (var i = 0; i < frames.Count; i++)
                if (frameData[i].Count == 0)
                    throw new InvalidDataException($"Frame {i} has no image data");
            animation = new PngAnimation(counts.Frames, counts.Plays, idatIsFirstFrame, frames.ToArray())
            {
                FrameData = frameData.ConvertAll(ranges => (IReadOnlyList<(int Offset, int Length)>)ranges.ToArray()),
            };
        }

        // The IDAT chunks are one zlib stream — the spec lets a producer split IDATs anywhere, even
        // mid-deflate-token — inflated straight into the samples a row at a time.
        var pixels = InflateAndUnfilter(data, idatChunks, width.Value, height!.Value, bitDepth!.Value, colorType!.Value);

        return new PngImage
        {
            Width = width.Value,
            Height = height.Value,
            // A sub-byte image's samples come back unpacked, one byte each: they are 8-bit samples now.
            BitDepth = bitDepth.Value < 8 ? 8 : bitDepth.Value,
            SourceBitDepth = bitDepth.Value,
            ColorType = colorType.Value,
            Pixels = pixels,
            Palette = palette,
            PaletteAlpha = paletteAlpha,
            IccProfile = iccProfileRaw,
            IccProfileName = iccProfileName,
            SrgbRenderingIntent = sawSrgb ? srgbRenderingIntent : null,
            Gamma = gamma100k.HasValue ? gamma100k.Value / 100_000.0 : null,
            Chromaticity = chrm,
            Exif = rawExif,
            Text = text is null ? [] : [.. text],
            Cicp = cicp,
            Mdcv = mdcv,
            Clli = clli,
            Animation = animation,
        };
    }

    /// <summary>
    /// The default ceiling on the total pixels <see cref="DecodeFrames(ReadOnlySpan{byte})"/> may materialise:
    /// canvas width × height × frame count, 2^28 (the same figure SharpAstro.Webp uses). That admits, say, 128 frames
    /// of 1080p, which hold 1 GiB of RGBA8 between them, and the working canvas comes on top. A caller with a tighter
    /// budget passes a smaller limit to <see cref="DecodeFrames(ReadOnlySpan{byte}, long)"/>, or streams with
    /// <see cref="EnumerateFrames"/>, which never holds more than one canvas.
    /// </summary>
    public const long DefaultMaximumAnimationPixels = 1L << 28;

    /// <summary>
    /// Decode every frame of an animated PNG to its full composited canvas, one at a time, through one working canvas.
    /// A PNG without an <c>acTL</c> yields one frame, its image, with a delay of 0.
    /// </summary>
    /// <remarks>
    /// <para>The yielded <see cref="PngFrame.Rgba"/> is that working canvas, so it is only valid until the enumerator is
    /// advanced: copy it to keep a frame. Nothing is materialised beyond the canvas, one frame's samples and the saved
    /// rectangle of a <see cref="PngDisposeOp.Previous"/> frame, so this is the way to handle animations
    /// <see cref="DecodeFrames(ReadOnlySpan{byte})"/> refuses. Each call to <c>GetEnumerator</c> starts over from a
    /// transparent canvas.</para>
    /// <para>The file is parsed and its animation chunks validated when this method is called (see <see cref="Decode"/>);
    /// a frame's own data is inflated as the enumerator reaches it, so a corrupt frame throws
    /// <see cref="InvalidDataException"/> from <c>MoveNext</c>. Compositing follows the APNG specification: see
    /// <see cref="PngCompositor"/>'s remarks for the dispose and blend rules; delays are
    /// <see cref="PngFrame.DelayNumerator"/> over <see cref="PngFrame.DelayDenominator"/>, a denominator of 0 meaning 100.</para>
    /// </remarks>
    /// <param name="data">The PNG file. It is read for as long as the sequence is enumerated, so it must not be changed meanwhile.</param>
    /// <exception cref="InvalidDataException">The stream is not a PNG, is corrupt, or declares a malformed animation.</exception>
    /// <exception cref="NotSupportedException">The file uses a feature this reader does not cover yet (Adam7 interlace).</exception>
    public static IEnumerable<PngFrame> EnumerateFrames(ReadOnlyMemory<byte> data) =>
        EnumerateDecoded(data, Decode(data.Span));

    private static IEnumerable<PngFrame> EnumerateDecoded(ReadOnlyMemory<byte> data, PngImage image)
    {
        if (image.Animation is null)
            return [new PngFrame(0, image.ToRgba8(), 0, 1)];
        CheckCanvasFits(image);
        return Composite(data, image);
    }

    // Checked before the iterator, so a canvas too big to hold throws at the call and not at MoveNext.
    private static void CheckCanvasFits(PngImage image)
    {
        if ((long)image.Width * image.Height * 4 > int.MaxValue)
            throw new InvalidDataException($"A {image.Width}×{image.Height} canvas does not fit a byte array as RGBA8");
    }

    private static IEnumerable<PngFrame> Composite(ReadOnlyMemory<byte> data, PngImage image)
    {
        var compositor = new PngCompositor(image);
        for (var i = 0; i < compositor.FrameCount; i++)
        {
            compositor.Draw(data.Span, i);
            var control = image.Animation!.Frames[i];
            yield return new PngFrame(i, compositor.Canvas, control.DelayNumerator, control.DelayDenominator);
        }
    }

    /// <summary><see cref="DecodeFrames(ReadOnlySpan{byte}, long)"/> with <see cref="DefaultMaximumAnimationPixels"/> as the limit.</summary>
    /// <param name="data">A still or animated PNG file.</param>
    /// <exception cref="InvalidDataException">The data is malformed, or canvas × frame count exceeds <see cref="DefaultMaximumAnimationPixels"/>.</exception>
    public static PngFrameSequence DecodeFrames(ReadOnlySpan<byte> data) =>
        DecodeFrames(data, DefaultMaximumAnimationPixels);

    /// <summary>
    /// Decode every frame of an animated PNG to its own full composited canvas, RGBA8, each frame owning its pixels.
    /// A PNG without an <c>acTL</c> gives one frame. Refuses an animation whose canvas × frame count is above
    /// <paramref name="maximumTotalPixels"/> before any canvas or frame is allocated.
    /// </summary>
    /// <remarks>
    /// The check runs once the file's chunks have been parsed and validated, so it sees the frame count the file really
    /// has (a count has to match the number of <c>fcTL</c> chunks, which bounds it by the input's length), and it comes
    /// before the working canvas, any frame's samples or any returned canvas exists. The <c>IDAT</c> image that
    /// <see cref="Decode"/> inflates is the one allocation made first, as for any PNG. The decoder's working canvas
    /// comes on top of the returned frames.
    /// </remarks>
    /// <param name="data">A still or animated PNG file.</param>
    /// <param name="maximumTotalPixels">The most pixels all the returned frames may hold together.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumTotalPixels"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">The data is malformed, or canvas × frame count exceeds <paramref name="maximumTotalPixels"/>.</exception>
    /// <exception cref="NotSupportedException">The file uses a feature this reader does not cover yet (Adam7 interlace).</exception>
    public static PngFrameSequence DecodeFrames(ReadOnlySpan<byte> data, long maximumTotalPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalPixels);
        var image = Decode(data);
        var frameCount = Math.Max(image.Animation?.Frames.Count ?? 1, 1);

        // Every returned frame is a full-canvas copy, so memory is canvas × frames. Width and height are each below
        // 2^31, so the canvas fits a long; dividing instead of multiplying keeps the product from wrapping.
        var canvasPixels = (long)image.Width * image.Height;
        if (canvasPixels > maximumTotalPixels / frameCount)
            throw new InvalidDataException(
                $"PNG: the animation would materialise {(double)canvasPixels * frameCount:N0} pixels " +
                $"({frameCount:N0} frames of {image.Width}x{image.Height}), above the limit of {maximumTotalPixels:N0}. " +
                "Use EnumerateFrames to stream it.");

        var frames = new List<PngFrame>(frameCount);
        if (image.Animation is null)
            frames.Add(new PngFrame(0, image.ToRgba8(), 0, 1));
        else
        {
            // Drawn straight from the caller's span: an iterator cannot hold one, and copying the whole file to a
            // Memory<byte> for it would keep a second copy of the compressed data for no reason.
            CheckCanvasFits(image);
            var compositor = new PngCompositor(image);
            for (var i = 0; i < compositor.FrameCount; i++)
            {
                compositor.Draw(data, i);
                var control = image.Animation.Frames[i];
                frames.Add(new PngFrame(i, (byte[])compositor.Canvas.Clone(), control.DelayNumerator, control.DelayDenominator));
            }
        }
        return new PngFrameSequence(image.Width, image.Height, image.Animation?.PlayCount ?? 1, frames.AsReadOnly());
    }

    /// <summary>
    /// The sequence number every <c>fcTL</c> and <c>fdAT</c> opens with. They share one count that runs 0, 1, 2, ...
    /// in file order with no gap and no repeat (APNG), so a chunk whose number is not the next one is out of order,
    /// repeated, or has a chunk missing before it.
    /// </summary>
    private static void CheckSequenceNumber(string chunk, int pos, ReadOnlySpan<byte> chunkData, ref uint next)
    {
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
        if (sequence != next)
            throw new InvalidDataException(
                $"{chunk} chunk at byte {pos} has sequence number {sequence} but {next} was expected; " +
                "the numbers of fcTL and fdAT chunks run 0, 1, 2, ... in file order with no gap or repeat");
        next++;
    }

    /// <summary>
    /// The unfiltered samples of one animation frame's own sub-image, found where <paramref name="image"/>'s
    /// <see cref="PngImage.Animation"/> says its data lies in <paramref name="data"/>, through the same inflate and
    /// unfilter path the <c>IDAT</c> image takes: <c>Frames[i].Width</c> × <c>Frames[i].Height</c> pixels in the
    /// layout of <see cref="PngImage.Pixels"/> (16-bit samples big-endian, 1-, 2- and 4-bit samples unpacked to one
    /// byte). No compositing: this is the frame as stored, not the canvas.
    /// </summary>
    /// <param name="data">The file <paramref name="image"/> was decoded from; the animation records offsets into it.</param>
    /// <exception cref="InvalidOperationException">The image has no animation.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameIndex"/> is not a frame of the animation.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> is shorter than where the frame's data lies, so it is not that file.</exception>
    /// <exception cref="InvalidDataException">The frame's zlib stream is corrupt or does not inflate to its size.</exception>
    internal static byte[] DecodeFrameSamples(ReadOnlySpan<byte> data, PngImage image, int frameIndex)
    {
        var animation = image.Animation ?? throw new InvalidOperationException("The image has no animation");
        if ((uint)frameIndex >= (uint)animation.Frames.Count)
            throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, $"The animation has {animation.Frames.Count} frames");
        var frame = animation.Frames[frameIndex];
        var ranges = animation.FrameData[frameIndex];
        foreach (var (offset, length) in ranges)
            if ((long)offset + length > data.Length)
                throw new ArgumentException("data is not the file the image was decoded from", nameof(data));
        return InflateAndUnfilter(data, ranges, frame.Width, frame.Height, image.SourceBitDepth, image.ColorType);
    }

    /// <summary>
    /// <see cref="DecodeFrameSamples"/> into <paramref name="destination"/>, which must hold at least
    /// <see cref="SampleBytes"/> bytes of the frame (<c>Frames[i].Width</c> x <c>Frames[i].Height</c> at the image's
    /// source depth and colour type): the compositor reuses one buffer for every frame instead of allocating one each.
    /// The bytes past the frame's samples are not touched.
    /// </summary>
    internal static void DecodeFrameSamplesInto(ReadOnlySpan<byte> data, PngImage image, int frameIndex, Span<byte> destination)
    {
        var animation = image.Animation ?? throw new InvalidOperationException("The image has no animation");
        if ((uint)frameIndex >= (uint)animation.Frames.Count)
            throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, $"The animation has {animation.Frames.Count} frames");
        var frame = animation.Frames[frameIndex];
        var ranges = animation.FrameData[frameIndex];
        foreach (var (offset, length) in ranges)
            if ((long)offset + length > data.Length)
                throw new ArgumentException("data is not the file the image was decoded from", nameof(data));
        InflateAndUnfilterInto(data, ranges, frame.Width, frame.Height, image.SourceBitDepth, image.ColorType, destination);
    }

    /// <summary>
    /// Validate that the (color type, bit depth) combo is one PNG allows.
    /// Spec table 11.1: not every depth is legal with every color type.
    /// </summary>
    private static void ValidateColorTypeAndBitDepth(int colorType, int bitDepth)
    {
        bool ok = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16, // Gray
            2 => bitDepth is 8 or 16,                // RGB
            3 => bitDepth is 1 or 2 or 4 or 8,       // Indexed (palette)
            4 => bitDepth is 8 or 16,                // Gray + Alpha
            6 => bitDepth is 8 or 16,                // RGBA
            _ => false
        };
        if (!ok)
            throw new InvalidDataException(
                $"Invalid (ColorType={colorType}, BitDepth={bitDepth}) combination per PNG spec");
    }

    /// <summary>
    /// Samples per pixel as a function of color type.
    /// Gray=1, RGB=3, Indexed=1, GrayAlpha=2, RGBA=4.
    /// </summary>
    internal static int SamplesPerPixel(int colorType) => colorType switch
    {
        0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
        _ => throw new InvalidDataException($"Unknown color type {colorType}")
    };

    /// <summary>
    /// Inflates an image's data stream (the IDATs, or one animation frame's fdATs) and runs the row-defilter,
    /// returning the raw sample bytes in row-major order. <paramref name="idat"/> lists where each chunk's zlib
    /// payload lies in <paramref name="data"/>; whatever is not zlib data, which is the 4-byte sequence number that
    /// opens every fdAT payload, is already left out of the range. <paramref name="width"/> and
    /// <paramref name="height"/> are those of the image or frame being decoded, not necessarily the canvas's. 16-bit samples stay in PNG's network (big-endian) byte order so the returned buffer is
    /// byte-identical to what the encoder fed in.
    /// <para>The samples are the only buffer the size of the image: the compressed bytes are read where
    /// they lie in <paramref name="data"/>, and each row is inflated straight into its place in the result
    /// and unfiltered there against the row above. It used to copy every IDAT out, concatenate the copies,
    /// copy that again into a stream, inflate into a growing stream, copy that out, and unfilter the copy
    /// into a new array: the image's bytes three or four times over before the samples existed.</para>
    /// </summary>
    private static byte[] InflateAndUnfilter(ReadOnlySpan<byte> data, IReadOnlyList<(int Offset, int Length)> idat,
        int width, int height, int bitDepth, int colorType)
    {
        var pixels = new byte[SampleBytes(width, height, bitDepth, colorType)];
        InflateAndUnfilterInto(data, idat, width, height, bitDepth, colorType, pixels);
        return pixels;
    }

    /// <summary>The size in bytes of the sample buffer <see cref="InflateAndUnfilterInto"/> fills: one byte per sample below 8 bits, else the samples' own width.</summary>
    internal static int SampleBytes(int width, int height, int bitDepth, int colorType) =>
        bitDepth < 8
            ? checked(width * height)
            : checked(width * SamplesPerPixel(colorType) * (bitDepth / 8) * height);

    /// <summary>
    /// <see cref="InflateAndUnfilter"/> into a buffer the caller owns, <paramref name="pixels"/>, which must be at least
    /// <see cref="SampleBytes"/> long (the leading bytes are written, the rest left alone): a caller that decodes many
    /// frames of one size keeps one buffer and reuses it instead of allocating, and zeroing, a new one per frame.
    /// </summary>
    private static unsafe void InflateAndUnfilterInto(ReadOnlySpan<byte> data, IReadOnlyList<(int Offset, int Length)> idat,
        int width, int height, int bitDepth, int colorType, Span<byte> pixels)
    {
        if (bitDepth < 8)
        {
            InflateAndUnfilterSubByte(data, idat, width, height, bitDepth, colorType, pixels);
            return;
        }
        var samplesPerPixel = SamplesPerPixel(colorType);
        var bytesPerSample = bitDepth / 8;
        var bytesPerPixel = samplesPerPixel * bytesPerSample;
        var rowBytes = checked(width * bytesPerPixel);
        var expected = ((long)rowBytes + 1) * height;
        if (pixels.Length < (long)rowBytes * height)
            throw new ArgumentException("The sample buffer is too small for the image", nameof(pixels));
        // The row above the first is all zero; rented, so cleared by hand.
        var firstRowAbove = System.Buffers.ArrayPool<byte>.Shared.Rent(rowBytes);
        firstRowAbove.AsSpan(0, rowBytes).Clear();

        try
        {
        fixed (byte* pinned = data)
        {
            // ZLibStream is the .NET 6+ wrapper that handles the 2-byte zlib header
            // + Adler32 trailer around the raw deflate bitstream.
            using var z = new ZLibStream(new ChunkPayloadStream(pinned, idat), CompressionMode.Decompress);
            Span<byte> filterType = stackalloc byte[1];
            long inflated = 0;
            for (var y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * rowBytes, rowBytes);
                inflated += ReadFully(z, filterType);
                inflated += ReadFully(z, row);
                if (inflated != (long)(y + 1) * (rowBytes + 1))
                    throw LengthMismatch(inflated, expected, width, height, bytesPerPixel);

                // PngPredictor's bpp argument is the "bytes per pixel" rounded up to at
                // least 1 — for 8-bit single-channel images that's 1; for 16-bit RGBA
                // it's 8. The filter formulas only need the "left neighbour" offset, so
                // bytesPerPixel here matches PNG spec §9.2.
                var above = y == 0 ? firstRowAbove.AsSpan(0, rowBytes) : pixels.Slice((y - 1) * rowBytes, rowBytes);
                PngPredictor.UnfilterRow(filterType[0], row, above, bytesPerPixel);
            }

            // A byte past the last row is as much a mismatch as a row short.
            if (z.Read(filterType) != 0)
                throw LengthMismatch(expected + 1, expected, width, height, bytesPerPixel);
        }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(firstRowAbove);
        }
    }

    /// <summary>
    /// <see cref="InflateAndUnfilter"/> for 1-, 2- and 4-bit greyscale and indexed images (one sample per pixel, packed
    /// most significant bits first, each row padded to a whole byte). A packed row is inflated and unfiltered against
    /// the packed row above, with a filter step of one byte as PNG §9.2 sets for depths under 8, then unpacked to one
    /// byte per sample: an index as it is, a grey level scaled to the full 0-255 range (a 4-bit 0xF is 255, a 1-bit 1 is
    /// 255) so the result reads as 8-bit greyscale. Emoji fonts' CBDT bitmaps use 4-bit indexed PNGs (Noto Color
    /// Emoji's U+1F6D1), which the reader refused.
    /// </summary>
    private static unsafe void InflateAndUnfilterSubByte(ReadOnlySpan<byte> data, IReadOnlyList<(int Offset, int Length)> idat,
        int width, int height, int bitDepth, int colorType, Span<byte> pixels)
    {
        var packedBytes = checked((int)(((long)width * bitDepth + 7) / 8));
        var expected = ((long)packedBytes + 1) * height;
        if (pixels.Length < (long)width * height)
            throw new ArgumentException("The sample buffer is too small for the image", nameof(pixels));
        var row = new byte[packedBytes];
        var above = new byte[packedBytes];
        var max = (1 << bitDepth) - 1;
        var gray = colorType == 0;

        fixed (byte* pinned = data)
        {
            using var z = new ZLibStream(new ChunkPayloadStream(pinned, idat), CompressionMode.Decompress);
            Span<byte> filterType = stackalloc byte[1];
            long inflated = 0;
            for (var y = 0; y < height; y++)
            {
                inflated += ReadFully(z, filterType);
                inflated += ReadFully(z, row);
                if (inflated != (long)(y + 1) * (packedBytes + 1))
                    throw LengthMismatch(inflated, expected, width, height, 1);
                PngPredictor.UnfilterRow(filterType[0], row, above, 1);

                var output = pixels.Slice(y * width, width);
                for (var x = 0; x < width; x++)
                {
                    var bit = x * bitDepth;
                    var sample = (row[bit >> 3] >> (8 - bitDepth - (bit & 7))) & max;
                    output[x] = (byte)(gray ? sample * 255 / max : sample);
                }
                (row, above) = (above, row);
            }

            if (z.Read(filterType) != 0)
                throw LengthMismatch(expected + 1, expected, width, height, 1);
        }
    }

    private static InvalidDataException LengthMismatch(long inflated, long expected, int width, int height, int bytesPerPixel) =>
        new((inflated > expected ? $"Inflated stream runs past the expected {expected} bytes " : $"Inflated stream length {inflated} doesn't match expected {expected} ") +
            $"({width}×{height} × {bytesPerPixel} bytes-per-pixel + 1 filter byte per row)");

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends; the count read.</summary>
    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// The chunk payloads of one image's data, in order (the IDATs, or one frame's fdATs without their sequence
    /// numbers), as one read-only stream over the pinned input, so the zlib stream they make up together is inflated
    /// without first being copied into one array. Only valid inside the <c>fixed</c> scope that pinned the input.
    /// </summary>
    private sealed unsafe class ChunkPayloadStream(byte* data, IReadOnlyList<(int Offset, int Length)> chunks) : Stream
    {
        private int _chunk;
        private int _offsetInChunk;

        public override int Read(Span<byte> buffer)
        {
            while (_chunk < chunks.Count)
            {
                var (offset, length) = chunks[_chunk];
                var left = length - _offsetInChunk;
                if (left == 0)
                {
                    _chunk++;
                    _offsetInChunk = 0;
                    continue;
                }

                var n = Math.Min(left, buffer.Length);
                new ReadOnlySpan<byte>(data + offset + _offsetInChunk, n).CopyTo(buffer);
                _offsetInChunk += n;
                return n;
            }
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// One text chunk as a <see cref="PngText"/> (PNG spec 11.3.4): <c>tEXt</c> keyword and Latin-1 text,
    /// <c>zTXt</c> keyword and deflated Latin-1 text, <c>iTXt</c> keyword, flags, language and translated keyword,
    /// then UTF-8 text, deflated when its flag says so. Null for a chunk that does not parse.
    /// </summary>
    private static PngText? TryReadText(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var keywordEnd = data.IndexOf((byte)0);
        if (keywordEnd is <= 0 or > PngText.MaxKeywordLength)
            return null;
        var keyword = System.Text.Encoding.Latin1.GetString(data[..keywordEnd]);
        var rest = data[(keywordEnd + 1)..];
        try
        {
            if (type.SequenceEqual("tEXt"u8))
                return new PngText(keyword, System.Text.Encoding.Latin1.GetString(rest));
            if (type.SequenceEqual("zTXt"u8))
                return rest.Length >= 1 && rest[0] == 0 && DeflateInflate(rest[1..], MaxInflatedTextBytes) is { } latin1
                    ? new PngText(keyword, System.Text.Encoding.Latin1.GetString(latin1))
                    : null;

            // iTXt: compression flag, compression method, language tag and null, translated keyword and null, text.
            // The flag is 0 or 1 and nothing else; any other value is malformed, not uncompressed.
            if (rest.Length < 2 || rest[0] > 1 || rest[1] != 0)
                return null;
            var compressed = rest[0] == 1;
            var afterFlags = rest[2..];
            var languageEnd = afterFlags.IndexOf((byte)0);
            if (languageEnd < 0)
                return null;
            var afterLanguage = afterFlags[(languageEnd + 1)..];
            var translatedEnd = afterLanguage.IndexOf((byte)0);
            if (translatedEnd < 0)
                return null;
            var payload = afterLanguage[(translatedEnd + 1)..];
            if (!compressed)
                return new PngText(keyword, System.Text.Encoding.UTF8.GetString(payload));
            return DeflateInflate(payload, MaxInflatedTextBytes) is { } utf8
                ? new PngText(keyword, System.Text.Encoding.UTF8.GetString(utf8))
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Inflate a deflate-compressed byte stream (zlib-wrapped). PNG IDAT
    /// payload is the concatenation of zero or more zlib streams — but the
    /// spec mandates exactly one logical zlib stream across all IDATs, so
    /// concat-then-inflate works.
    /// Null once the output would pass <paramref name="maxBytes"/>: reading stops there, so a
    /// small stream that claims gigabytes costs at most the cap.
    /// </summary>
    private static byte[]? DeflateInflate(ReadOnlySpan<byte> compressed, int maxBytes)
    {
        // Copy the span into a backing array so we can wrap it in a stream.
        // ZLibStream is the .NET 6+ wrapper that handles the 2-byte zlib header
        // + Adler32 trailer around the raw deflate bitstream.
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = z.Read(buffer)) > 0)
        {
            if (output.Length + read > maxBytes)
                return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string TypeAscii(ReadOnlySpan<byte> typeBytes) =>
        System.Text.Encoding.ASCII.GetString(typeBytes);

    // ---------------- CRC32 (same polynomial as PngWriter.Crc32) -----------------

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            t[n] = c;
        }
        return t;
    }
}

/// <summary>
/// Decoded PNG image — sample data plus the ancillary metadata
/// <see cref="PngReader"/> extracted from chunks like <c>iCCP</c> / <c>sRGB</c>
/// / <c>gAMA</c> / <c>cHRM</c> / <c>eXIf</c>.
/// </summary>
public sealed record PngImage
{
    /// <summary>Image width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>
    /// Bits per sample in <see cref="Pixels"/>: 8 or 16. A 1-, 2- or 4-bit image's samples are unpacked to one byte
    /// each (a grey level scaled to 0-255, an index as it is), so it reads 8 here; IHDR's own depth is
    /// <see cref="SourceBitDepth"/>.
    /// </summary>
    public required int BitDepth { get; init; }

    /// <summary>Bits per sample as declared in IHDR: 1, 2, 4, 8 or 16; <see cref="BitDepth"/> for an image not read from a file.</summary>
    public int SourceBitDepth { get => field == 0 ? BitDepth : field; init; }

    /// <summary>PNG color type: 0=Gray, 2=RGB, 3=Indexed, 4=Gray+Alpha, 6=RGBA.</summary>
    public required int ColorType { get; init; }

    /// <summary>
    /// Raw unfiltered sample bytes in row-major order. For 16-bit images the
    /// bytes are in PNG's big-endian network order — see <see cref="AsUInt16Samples"/>
    /// for a native-endian convenience view.
    /// </summary>
    public required byte[] Pixels { get; init; }

    /// <summary>
    /// Palette (<c>PLTE</c>) for indexed-color images (<see cref="ColorType"/> == 3):
    /// RGB triples, 3 bytes per entry, in palette-index order. Null for non-indexed
    /// images. Each byte in <see cref="Pixels"/> is a single index into this palette
    /// (the reader keeps indexed pixels un-expanded to stay faithful to the file's
    /// declared <see cref="ColorType"/> — expand via palette lookup at the consumer).
    /// </summary>
    public byte[]? Palette { get; init; }

    /// <summary>
    /// Per-palette-entry alpha (<c>tRNS</c>) for indexed-color images: one byte per
    /// entry, in palette order. May be shorter than <see cref="Palette"/> — entries
    /// past its end are fully opaque (255). Null when an indexed image has no tRNS
    /// chunk (all opaque) or when the image is not indexed.
    /// </summary>
    public byte[]? PaletteAlpha { get; init; }

    /// <summary>Decompressed ICC profile bytes from the <c>iCCP</c> chunk, or null if absent.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Keyword from the <c>iCCP</c> chunk (e.g. "ICC Profile" or a custom name).</summary>
    public string? IccProfileName { get; init; }

    /// <summary>Rendering intent from the <c>sRGB</c> chunk, or null if absent. 0..3 per PNG spec.</summary>
    public byte? SrgbRenderingIntent { get; init; }

    /// <summary>Decoded gamma (e.g. 0.45455 for sRGB) from the <c>gAMA</c> chunk, or null if absent.</summary>
    public double? Gamma { get; init; }

    /// <summary>Chromaticity values from the <c>cHRM</c> chunk, or null if absent.</summary>
    public ChromaticityChunk? Chromaticity { get; init; }

    /// <summary>Raw EXIF blob from the <c>eXIf</c> chunk, or null if absent.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>
    /// Every text chunk (<c>tEXt</c>, <c>zTXt</c>, <c>iTXt</c>) in file order, one <see cref="PngText"/> each;
    /// empty when there are none. A text chunk that does not parse is left out, never a failed decode.
    /// </summary>
    public IReadOnlyList<PngText> Text { get; init; } = [];

    /// <summary>PNG-3 <c>cICP</c> Coding-Independent Code Points (HDR color signaling), or null if absent.</summary>
    public CicpChunk? Cicp { get; init; }

    /// <summary>PNG-3 <c>mDCv</c> Mastering Display Color Volume, or null if absent.</summary>
    public MdcvChunk? Mdcv { get; init; }

    /// <summary>PNG-3 <c>cLLI</c> Content Light Level Information, or null if absent.</summary>
    public ClliChunk? Clli { get; init; }

    /// <summary>True if any PNG-3 HDR signaling chunk (cICP / mDCv / cLLI) is present.</summary>
    public bool HasHdrSignaling => Cicp is not null || Mdcv is not null || Clli is not null;

    /// <summary>
    /// The animation the file declares with <c>acTL</c> / <c>fcTL</c> / <c>fdAT</c> chunks (APNG), or null for a
    /// still PNG, which is every file without an <c>acTL</c>. <see cref="Pixels"/> is the <c>IDAT</c> image either
    /// way: the first frame when <see cref="PngAnimation.DefaultImageIsFirstFrame"/>, otherwise a default image that
    /// is not part of the animation.
    /// </summary>
    public PngAnimation? Animation { get; init; }

    /// <summary>
    /// True when the file declares an animation of more than one frame. A file whose <c>acTL</c> says 1 frame has an
    /// <see cref="Animation"/> but is a still image, and reads false here.
    /// </summary>
    public bool IsAnimated => Animation is { FrameCount: > 1 };

    /// <summary>
    /// Sample count per row (<see cref="Width"/> × samples-per-pixel for the color type).
    /// Useful for unpacking <see cref="Pixels"/> without re-deriving the layout.
    /// </summary>
    public int SamplesPerPixel => PngReader.SamplesPerPixel(ColorType);

    /// <summary>
    /// Returns <see cref="Pixels"/> as a <c>ushort[]</c> in host-native endian.
    /// Only valid for 16-bit images; throws otherwise.
    /// </summary>
    public ushort[] AsUInt16Samples()
    {
        if (BitDepth != 16)
            throw new InvalidOperationException($"AsUInt16Samples is only valid for 16-bit images; this is {BitDepth}-bit");
        var n = Pixels.Length / 2;
        var result = new ushort[n];
        for (var i = 0; i < n; i++)
            result[i] = BinaryPrimitives.ReadUInt16BigEndian(Pixels.AsSpan(i * 2, 2));
        return result;
    }

    /// <summary>
    /// Expand the raw <see cref="Pixels"/> samples into tightly-packed 8-bit RGBA
    /// (row-major, 4 bytes per pixel, top-down) — the layout most GPU / UI consumers
    /// want. Handles every color type the decoder produces: greyscale (0), RGB (2),
    /// indexed (3, via <see cref="Palette"/> + optional <see cref="PaletteAlpha"/>),
    /// greyscale + alpha (4), and RGBA (6). 16-bit samples are truncated to their high
    /// byte (<see cref="Pixels"/> holds 16-bit values big-endian, so the byte at the
    /// sample offset already IS the high byte).
    /// </summary>
    /// <remarks>
    /// The faithful-decode counterpart to the "give me display pixels" need:
    /// <see cref="PngReader.Decode"/> keeps samples in their declared color type to
    /// preserve the file exactly; this collapses them to RGBA8 on demand, so consumers
    /// don't each re-implement the big-endian truncation and palette lookup.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// <see cref="ColorType"/> is not one of the five PNG color types (0/2/3/4/6), the
    /// image is indexed but carries no <see cref="Palette"/>, or a pixel indexes past
    /// the palette's end.
    /// </exception>
    public byte[] ToRgba8()
    {
        var dst = new byte[Width * Height * 4];
        ExpandToRgba8(dst);
        return dst;
    }

    /// <summary>
    /// Expand the raw <see cref="Pixels"/> samples into a caller-provided 8-bit RGBA
    /// destination (row-major, 4 bytes per pixel, top-down) - identical mapping to
    /// <see cref="ToRgba8()"/> but writing into <paramref name="destination"/> instead
    /// of allocating a result, so a codec facade can decode straight into a pooled or
    /// UI-owned buffer. <paramref name="destination"/> must be at least
    /// <c>Width * Height * 4</c> bytes.
    /// </summary>
    public void ExpandToRgba8(Span<byte> destination)
    {
        var w = Width;
        var h = Height;
        var spp = SamplesPerPixel;
        var step = BitDepth == 16 ? 2 : 1; // bytes per sample (high byte first for 16-bit)
        var rowBytes = w * spp * step;
        if (destination.Length < w * h * 4)
            throw new ArgumentException($"Destination too small: {destination.Length} < {w * h * 4} (Width*Height*4).", nameof(destination));

        for (var y = 0; y < h; y++)
            ExpandRowToRgba8(ColorType, step, Palette, PaletteAlpha,
                Pixels.AsSpan(y * rowBytes, rowBytes), destination.Slice(y * w * 4, w * 4));
    }

    /// <summary>
    /// One row of <see cref="ExpandToRgba8(Span{byte})"/>: <paramref name="source"/> holds <c>width</c> pixels of the
    /// given colour type at <paramref name="step"/> bytes per sample, <paramref name="destination"/> receives
    /// <c>width * 4</c> bytes. The colour type is decided once per row rather than once per pixel, which is what
    /// the per-pixel switch this replaces cost. The mapping is the one <see cref="ExpandToRgba8(Span{byte})"/>
    /// documents, unchanged.
    /// </summary>
    internal static void ExpandRowToRgba8(int colorType, int step, byte[]? palette, byte[]? paletteAlpha,
        ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var width = destination.Length / 4;
        switch (colorType)
        {
            case 6: // RGBA
                if (step == 1)
                    source[..(width * 4)].CopyTo(destination);
                else
                    for (int x = 0, s = 0; x < width; x++, s += 8)
                    {
                        var d = destination.Slice(x * 4, 4);
                        d[0] = source[s]; d[1] = source[s + 2]; d[2] = source[s + 4]; d[3] = source[s + 6];
                    }
                break;
            case 2: // RGB
                for (int x = 0, s = 0, d = 0; x < width; x++, s += 3 * step, d += 4)
                {
                    destination[d] = source[s];
                    destination[d + 1] = source[s + step];
                    destination[d + 2] = source[s + 2 * step];
                    destination[d + 3] = 255;
                }
                break;
            case 0: // greyscale
                for (int x = 0, s = 0, d = 0; x < width; x++, s += step, d += 4)
                {
                    var g = source[s];
                    destination[d] = destination[d + 1] = destination[d + 2] = g;
                    destination[d + 3] = 255;
                }
                break;
            case 4: // greyscale + alpha
                for (int x = 0, s = 0, d = 0; x < width; x++, s += 2 * step, d += 4)
                {
                    var g = source[s];
                    destination[d] = destination[d + 1] = destination[d + 2] = g;
                    destination[d + 3] = source[s + step];
                }
                break;
            case 3: // indexed: a sample is an index into the palette (+ optional palette alpha)
                if (width == 0)
                    break;
                if (palette is not { } pal)
                    throw new InvalidDataException("Indexed PNG has no palette to expand to RGBA8");
                for (int x = 0, s = 0, d = 0; x < width; x++, s += step, d += 4)
                {
                    var idx = source[s];
                    var pi = idx * 3;
                    if (pi + 2 >= pal.Length)
                        throw new InvalidDataException(
                            $"Palette index {idx} out of range (palette has {pal.Length / 3} entries)");
                    destination[d] = pal[pi];
                    destination[d + 1] = pal[pi + 1];
                    destination[d + 2] = pal[pi + 2];
                    destination[d + 3] = paletteAlpha is { } pa && idx < pa.Length ? pa[idx] : (byte)255;
                }
                break;
            default:
                // Unreachable through the image: SamplesPerPixel already throws for a colour type outside
                // {0,2,3,4,6}. Kept as a backstop for the direct callers.
                throw new InvalidDataException($"Cannot expand PNG color type {colorType} to RGBA8");
        }
    }
}

/// <summary>
/// PNG <c>cHRM</c> chunk payload: chromaticity coordinates of the white point
/// and three primaries, encoded as fixed-point (×100000) per PNG spec §11.3.3.5.
/// </summary>
public sealed record ChromaticityChunk(
    double WhiteX, double WhiteY,
    double RedX, double RedY,
    double GreenX, double GreenY,
    double BlueX, double BlueY)
{
    internal static ChromaticityChunk Read(ReadOnlySpan<byte> chunkData)
    {
        // Local helper can't close over a ReadOnlySpan<byte>; inline the eight reads.
        var wx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(0, 4))  / 100_000.0;
        var wy = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4))  / 100_000.0;
        var rx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(8, 4))  / 100_000.0;
        var ry = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(12, 4)) / 100_000.0;
        var gx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(16, 4)) / 100_000.0;
        var gy = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(20, 4)) / 100_000.0;
        var bx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(24, 4)) / 100_000.0;
        var by = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(28, 4)) / 100_000.0;
        return new ChromaticityChunk(wx, wy, rx, ry, gx, gy, bx, by);
    }

    internal void Write(Span<byte> dst)
    {
        // dst must be exactly 32 bytes; caller writes the chunk wrapper.
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0, 4),  (uint)Math.Round(WhiteX * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4),  (uint)Math.Round(WhiteY * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(8, 4),  (uint)Math.Round(RedX   * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(12, 4), (uint)Math.Round(RedY   * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(16, 4), (uint)Math.Round(GreenX * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(20, 4), (uint)Math.Round(GreenY * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(24, 4), (uint)Math.Round(BlueX  * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(28, 4), (uint)Math.Round(BlueY  * 100_000.0));
    }
}

/// <summary>
/// PNG-3 <c>cICP</c> chunk — Coding-Independent Code Points (4 bytes).
/// Identifies the colour space + transfer function of the image's sample
/// values using H.273 / ITU CICP numbering. This is how PNG-3 declares
/// HDR — pixel data stays 16-bit integer, but cICP says e.g. "BT.2020
/// primaries + PQ transfer function" to flag it as HDR10.
/// </summary>
/// <param name="ColorPrimaries">H.273 §8.1 colour primaries codepoint.</param>
/// <param name="TransferFunction">H.273 §8.2 transfer characteristics codepoint.</param>
/// <param name="MatrixCoefficients">H.273 §8.3 matrix codepoint. PNG-3 §11.3.2.6 requires <see cref="SharpAstro.Codecs.Abstractions.MatrixCoefficients.Identity"/> (RGB) — PNG doesn't carry YCbCr.</param>
/// <param name="VideoFullRangeFlag">PNG-3 requires <c>true</c> (full range 0..2^N-1).</param>
public sealed record CicpChunk(
    SharpAstro.Codecs.Abstractions.ColorPrimaries ColorPrimaries,
    SharpAstro.Codecs.Abstractions.TransferFunction TransferFunction,
    SharpAstro.Codecs.Abstractions.MatrixCoefficients MatrixCoefficients,
    bool VideoFullRangeFlag)
{
    internal static CicpChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 4)
            throw new InvalidDataException($"cICP chunk length must be 4, got {chunkData.Length}");
        return new CicpChunk(
            ColorPrimaries:       (SharpAstro.Codecs.Abstractions.ColorPrimaries)chunkData[0],
            TransferFunction:     (SharpAstro.Codecs.Abstractions.TransferFunction)chunkData[1],
            MatrixCoefficients:   (SharpAstro.Codecs.Abstractions.MatrixCoefficients)chunkData[2],
            VideoFullRangeFlag:   chunkData[3] != 0);
    }

    internal void Write(Span<byte> dst)
    {
        dst[0] = (byte)ColorPrimaries;
        dst[1] = (byte)TransferFunction;
        dst[2] = (byte)MatrixCoefficients;
        dst[3] = (byte)(VideoFullRangeFlag ? 1 : 0);
    }

    /// <summary>BT.2020 + PQ (SMPTE 2084) — the standard "HDR10" signalling.</summary>
    public static CicpChunk Hdr10Pq => new(
        SharpAstro.Codecs.Abstractions.ColorPrimaries.BT2020,
        SharpAstro.Codecs.Abstractions.TransferFunction.Pq,
        SharpAstro.Codecs.Abstractions.MatrixCoefficients.Identity,
        true);

    /// <summary>BT.2020 + HLG (ARIB STD-B67) — broadcast HDR.</summary>
    public static CicpChunk Bt2020Hlg => new(
        SharpAstro.Codecs.Abstractions.ColorPrimaries.BT2020,
        SharpAstro.Codecs.Abstractions.TransferFunction.Hlg,
        SharpAstro.Codecs.Abstractions.MatrixCoefficients.Identity,
        true);

    /// <summary>
    /// sRGB primaries + PQ transfer — "narrow-gamut HDR". Non-canonical for
    /// HDR10 (which is BT.2020 + PQ) but valid PNG-3 / ICC v4.4; sidesteps
    /// the gamut-mismatch desaturation that consumer HDR displays sometimes
    /// produce on BT.2020-encoded content displayed without a correct
    /// inverse-gamut tonemap. Use when the source content is sRGB-saturation
    /// and you want PQ HDR luminance without re-mapping primaries.
    /// </summary>
    public static CicpChunk SrgbPq => new(
        SharpAstro.Codecs.Abstractions.ColorPrimaries.BT709,
        SharpAstro.Codecs.Abstractions.TransferFunction.Pq,
        SharpAstro.Codecs.Abstractions.MatrixCoefficients.Identity,
        true);

    /// <summary>sRGB primaries + sRGB transfer — explicit signal of SDR sRGB.</summary>
    public static CicpChunk Srgb => new(
        SharpAstro.Codecs.Abstractions.ColorPrimaries.BT709,
        SharpAstro.Codecs.Abstractions.TransferFunction.Srgb,
        SharpAstro.Codecs.Abstractions.MatrixCoefficients.Identity,
        true);
}

/// <summary>
/// PNG-3 <c>mDCv</c> chunk — Mastering Display Color Volume (24 bytes).
/// Tells the renderer what reference display the content was mastered on,
/// so playback can tone-map appropriately. Mirrors the SMPTE ST 2086 /
/// HEVC SEI "mastering display color volume" payload.
/// </summary>
/// <param name="RedX">Raw u16; primaries are in units of 0.00002 (so 35400 ≈ 0.708).</param>
/// <param name="MaxLuminanceUnits">Raw u32; units of 0.0001 cd/m² (10000000 = 1000 cd/m²).</param>
public sealed record MdcvChunk(
    ushort RedX, ushort RedY,
    ushort GreenX, ushort GreenY,
    ushort BlueX, ushort BlueY,
    ushort WhitePointX, ushort WhitePointY,
    uint MaxLuminanceUnits,
    uint MinLuminanceUnits)
{
    internal static MdcvChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 24)
            throw new InvalidDataException($"mDCv chunk length must be 24, got {chunkData.Length}");
        return new MdcvChunk(
            RedX:        BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(0, 2)),
            RedY:        BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(2, 2)),
            GreenX:      BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(4, 2)),
            GreenY:      BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(6, 2)),
            BlueX:       BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(8, 2)),
            BlueY:       BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(10, 2)),
            WhitePointX: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(12, 2)),
            WhitePointY: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(14, 2)),
            MaxLuminanceUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(16, 4)),
            MinLuminanceUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(20, 4)));
    }

    internal void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(0, 2),  RedX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2),  RedY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4, 2),  GreenX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6, 2),  GreenY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(8, 2),  BlueX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(10, 2), BlueY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(12, 2), WhitePointX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(14, 2), WhitePointY);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(16, 4), MaxLuminanceUnits);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(20, 4), MinLuminanceUnits);
    }
}

/// <summary>
/// PNG-3 <c>cLLI</c> chunk — Content Light Level Information (8 bytes).
/// Describes the brightest pixel (<c>MaxCll</c>) and brightest average frame
/// (<c>MaxFall</c>) the content actually contains, so the renderer can
/// pre-allocate tone-mapping headroom. Both values are raw u32 in units of
/// 0.0001 cd/m² (i.e., divide by 10000 to get nits).
/// </summary>
public sealed record ClliChunk(uint MaxCllUnits, uint MaxFallUnits)
{
    internal static ClliChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 8)
            throw new InvalidDataException($"cLLI chunk length must be 8, got {chunkData.Length}");
        return new ClliChunk(
            MaxCllUnits:  BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(0, 4)),
            MaxFallUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4)));
    }

    internal void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0, 4), MaxCllUnits);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), MaxFallUnits);
    }
}
