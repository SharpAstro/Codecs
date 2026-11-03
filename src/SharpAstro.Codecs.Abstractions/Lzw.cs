namespace SharpAstro.Codecs.Abstractions;

/// <summary>
/// The three knobs that separate the LZW dialects this family reads. Everything else (the prefix-chain
/// table, the KwKwK case, deferred clear at a full table) is the same in all of them.
/// <list type="bullet">
/// <item><b>Bit order.</b> TIFF 6.0 section 13 and PDF's /LZWDecode pack codes MSB-first; GIF packs
/// them LSB-first.</item>
/// <item><b>Early change.</b> TIFF (and PDF's default EarlyChange 1) widen the code ONE code before the
/// table fills -- at next-free-code 511 / 1023 / 2047; GIF (and PDF's EarlyChange 0) widen exactly when
/// the next free code reaches 512 / 1024 / 2048.</item>
/// <item><b>Root size.</b> The literals are the codes below <c>1 &lt;&lt; MinCodeSize</c>; clear is
/// that value, end-of-information is clear + 1, the first free code is clear + 2 and the first width is
/// <c>MinCodeSize + 1</c>. TIFF fixes it at 8; GIF carries 2-8 in its image data.</item>
/// </list>
/// Get any one wrong and the stream decodes plausibly for a few hundred codes, then turns to noise.
/// </summary>
internal readonly struct LzwFormat
{
    private LzwFormat(bool msbFirst, bool earlyChange, int minCodeSize)
    {
        MsbFirst = msbFirst;
        EarlyChange = earlyChange;
        MinCodeSize = minCodeSize;
    }

    /// <summary>True for TIFF / PDF packing, false for GIF's LSB-first packing.</summary>
    public bool MsbFirst { get; }

    /// <summary>True when the width steps up one code early (TIFF, PDF EarlyChange 1).</summary>
    public bool EarlyChange { get; }

    /// <summary>Literal width in bits, 2-8. Zero only for <c>default</c>, which <see cref="Lzw.Decode"/> refuses.</summary>
    public int MinCodeSize { get; }

    /// <summary>TIFF 6.0 section 13: MSB-first, early change, 8-bit literals.</summary>
    public static LzwFormat Tiff => new(true, true, 8);

    /// <summary>GIF: LSB-first, no early change, <paramref name="minCodeSize"/> (2-8) from the image data's first byte.</summary>
    public static LzwFormat Gif(int minCodeSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minCodeSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCodeSize, 8);
        return new(false, false, minCodeSize);
    }

    /// <summary>PDF /LZWDecode: MSB-first, 8-bit literals, with the filter's /EarlyChange parameter (default 1).</summary>
    public static LzwFormat Pdf(bool earlyChange = true) => new(true, earlyChange, 8);
}

/// <summary>
/// One LZW decoder for TIFF, GIF and (later) PDF, parameterised by <see cref="LzwFormat"/>. It lives in
/// the abstractions package for the same reason <c>MqDecoder</c> does: the variants share every line but
/// three parameters, and two copies could only drift. See the csproj's InternalsVisibleTo comment.
///
/// <para>The table is stored as a prefix chain -- <c>(prefix code, one byte)</c> per entry -- rather
/// than as materialised strings, so adding an entry is O(1) and needs no allocation. Expansion walks
/// the chain, which yields the bytes in reverse, hence the scratch buffer and the backwards fill.</para>
///
/// <para>When the table is full (4096 entries) decoding carries on at 12 bits and adds nothing until a
/// clear code arrives. GIF encoders rely on that ("deferred clear"); TIFF's encoders emit the clear
/// themselves, so the same rule costs it nothing.</para>
/// </summary>
internal static class Lzw
{
    private const int MaxCode = 4096;
    private const int MaxWidth = 12;

    /// <summary>
    /// Decode one LZW stream into <paramref name="dst"/>, returning the number of bytes written. Stops at
    /// end-of-information, at the end of the input, when the destination is full, or at the first code
    /// no conforming encoder could have produced -- in every case what was decoded so far is kept
    /// rather than thrown away, matching how an uncompressed strip clamps.
    /// </summary>
    public static int Decode(LzwFormat format, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var minCodeSize = format.MinCodeSize;
        if (minCodeSize is < 2 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(format), "LzwFormat is default or has a root size outside 2-8.");
        }

        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        var firstFreeCode = clearCode + 2;
        var startWidth = minCodeSize + 1;
        var early = format.EarlyChange ? 1 : 0;

        // prefix[c] is the code for everything but the last byte of entry c; suffix[c] is that byte.
        var prefix = new short[MaxCode];
        var suffix = new byte[MaxCode];
        // Longest possible entry, so the reversal scratch never needs bounds checks beyond this.
        var scratch = new byte[MaxCode];

        var bits = new BitReader(src, format.MsbFirst);
        var nextCode = firstFreeCode;
        var codeWidth = startWidth;
        var written = 0;
        var oldCode = -1;

        // Each iteration consumes one code from the reader, so the input is finite and the loop ends
        // when ReadCode runs dry (or earlier on EOI, corruption or a full destination).
        while (true)
        {
            var code = bits.ReadCode(codeWidth);
            if (code < 0 || code == eoiCode)
            {
                break;
            }

            if (code == clearCode)
            {
                nextCode = firstFreeCode;
                codeWidth = startWidth;
                oldCode = -1;
                continue;
            }

            if (oldCode < 0)
            {
                // First code after a clear must be a literal; anything else is a corrupt stream.
                if (code >= firstFreeCode)
                {
                    break;
                }
            }
            else if (code == nextCode)
            {
                // The KwKwK case: the encoder used an entry it defined with THIS code, which the decoder
                // has not built yet. The string is the previous one plus its own first byte.
                if (nextCode < MaxCode)
                {
                    prefix[nextCode] = (short)oldCode;
                    suffix[nextCode] = FirstByte(prefix, suffix, firstFreeCode, oldCode);
                    nextCode++;
                }
                written += Emit(prefix, suffix, scratch, firstFreeCode, code, dst, written);
                oldCode = code;
                codeWidth = WidthFor(nextCode, codeWidth, early);
                if (written >= dst.Length)
                {
                    break;
                }
                continue;
            }
            else if (code > nextCode)
            {
                // A code beyond the next free one cannot be produced by a conforming encoder.
                break;
            }

            written += Emit(prefix, suffix, scratch, firstFreeCode, code, dst, written);

            // A full table adds nothing (deferred clear): the width stays 12 and codes keep coming.
            if (oldCode >= 0 && nextCode < MaxCode)
            {
                prefix[nextCode] = (short)oldCode;
                suffix[nextCode] = FirstByte(prefix, suffix, firstFreeCode, code);
                nextCode++;
            }

            oldCode = code;
            codeWidth = WidthFor(nextCode, codeWidth, early);

            if (written >= dst.Length)
            {
                break;
            }
        }

        return written;
    }

    /// <summary>
    /// The width schedule: step up when the next free code (plus 1 under early change) reaches the next
    /// power of two, capped at 12. TIFF: 511 / 1023 / 2047. GIF: 512 / 1024 / 2048. Off by that one
    /// and a stream decodes correctly until the first boundary. The next free code grows by at most one
    /// per code, so a single step per call suffices.
    /// </summary>
    private static int WidthFor(int nextCode, int current, int early)
        => current < MaxWidth && nextCode + early >= (1 << current) ? current + 1 : current;

    private static byte FirstByte(short[] prefix, byte[] suffix, int firstFreeCode, int code)
    {
        // Each step moves to the entry's prefix, which was added earlier and so is a smaller code: the
        // walk strictly descends and ends at a literal below firstFreeCode.
        while (code >= firstFreeCode)
        {
            code = prefix[code];
        }
        return (byte)code;
    }

    /// <summary>
    /// Expand one code into <paramref name="dst"/> at <paramref name="at"/>, returning how many bytes
    /// were written (which may be fewer than the entry's length when the destination fills).
    /// </summary>
    private static int Emit(short[] prefix, byte[] suffix, byte[] scratch, int firstFreeCode, int code, Span<byte> dst, int at)
    {
        // The chain yields bytes last-to-first, so collect then reverse. Each step moves to a smaller
        // code (see FirstByte), and len is bounded by the scratch length besides.
        var len = 0;
        var c = code;
        while (c >= firstFreeCode && len < scratch.Length)
        {
            scratch[len++] = suffix[c];
            c = prefix[c];
        }
        if (len < scratch.Length)
        {
            scratch[len++] = (byte)c;
        }

        var room = dst.Length - at;
        var n = Math.Min(len, room);
        // i advances to n, which is at most the entry's length.
        for (var i = 0; i < n; i++)
        {
            dst[at + i] = scratch[len - 1 - i];
        }
        return n;
    }

    /// <summary>
    /// Bit reader over the input, MSB-first (TIFF, PDF) or LSB-first (GIF). Returns -1 when the next code
    /// would run past the input, so a trailing partial code is dropped.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _src;
        private readonly bool _msbFirst;
        private int _pos;
        private ulong _acc;
        private int _count;

        public BitReader(ReadOnlySpan<byte> src, bool msbFirst)
        {
            _src = src;
            _msbFirst = msbFirst;
            _pos = 0;
            _acc = 0;
            _count = 0;
        }

        public int ReadCode(int width)
        {
            // Each pass takes one input byte (_pos advances), so this ends when the width is met or the
            // input is exhausted.
            while (_count < width)
            {
                if (_pos >= _src.Length)
                {
                    return -1;
                }

                if (_msbFirst)
                {
                    _acc = (_acc << 8) | _src[_pos];
                }
                else
                {
                    _acc |= (ulong)_src[_pos] << _count;
                }
                _pos++;
                _count += 8;
            }

            int value;
            if (_msbFirst)
            {
                value = (int)((_acc >> (_count - width)) & ((1UL << width) - 1));
            }
            else
            {
                value = (int)(_acc & ((1UL << width) - 1));
                _acc >>= width;
            }
            _count -= width;
            return value;
        }
    }
}
