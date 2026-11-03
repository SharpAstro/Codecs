using SharpAstro.Codecs.Abstractions;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The shared LZW decoder in its GIF configuration (LSB-first, no early change, root size 2-8), plus the
/// TIFF / PDF presets through the same code. <see cref="TiffLzwTests"/> still pins the TIFF wrapper.
///
/// Two independent things keep these honest. The encoder below is test-only and mirrors the decoder's
/// width rule, so a round trip alone could agree on a shared mistake; <see cref="TheClassicTwoBitGifExample_DecodesByHand"/>
/// is therefore a stream written by somebody else (the 10x10 example every GIF primer walks through),
/// whose codes and first bytes were checked by hand, and it crosses the 3-, 4- and 5-bit width steps
/// of the no-early-change schedule. Everything else here proves the decoder is complete: every root,
/// every width boundary up to 12 bits, KwKwK, a full table with no clear, a clear mid-stream, truncation
/// and a destination that fills.
/// </summary>
public sealed class LzwTests
{
    public static TheoryData<int> Roots() => new() { 2, 3, 4, 5, 6, 7, 8 };

    // ---- the by-hand vector -------------------------------------------------------------------------

    [Fact]
    public void TheClassicTwoBitGifExample_DecodesByHand()
    {
        // The 10x10 sample image's LZW data, root size 2 (clear = 4, EOI = 5, first free = 6, 3-bit codes).
        // Codes, LSB-first: first byte 0x8C = 10001100b -> bits 0-2 = 100b = 4 (clear), bits 3-5 = 001b = 1.
        // Full sequence: 4 1 6 6 2 9 9 7 8 10 2 12 1 14 15 6 0 21 0 10 7 22 23 18 26 7 10 29 13 24 12 18 16 36 12 5.
        byte[] data =
        [
            0x8C, 0x2D, 0x99, 0x87, 0x2A, 0x1C, 0xDC, 0x33, 0xA0, 0x02, 0x75,
            0xEC, 0x95, 0xFA, 0xA8, 0xDE, 0x60, 0x8C, 0x04, 0x91, 0x4C, 0x01,
        ];
        byte[] expected =
        [
            1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
            1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
            1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
            1, 1, 1, 0, 0, 0, 0, 2, 2, 2,
            1, 1, 1, 0, 0, 0, 0, 2, 2, 2,
            2, 2, 2, 0, 0, 0, 0, 1, 1, 1,
            2, 2, 2, 0, 0, 0, 0, 1, 1, 1,
            2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        ];

        var decoded = new byte[expected.Length];
        Lzw.Decode(LzwFormat.Gif(2), data, decoded).ShouldBe(expected.Length);
        decoded.ShouldBe(expected);
    }

    [Fact]
    public void AHandPackedStream_PinsBitOrderAndRootSize()
    {
        // Root 2: clear = 4, EOI = 5. Codes at 3 bits: 4, 1, 0, 3, 5. Packed by hand, each code's
        // bits written least-significant first:
        //   c0=4 (100b) c1=1 (001b) c2=0 (000b) c3=3 (011b) c4=5 (101b)
        //   bits (LSB first): 0,0,1, 1,0,0, 0,0,0, 1,1,0, 1,0,1
        //   byte0 = bits 0-7  = 0,0,1,1,0,0,0,0 -> 0b00001100 = 0x0C
        //   byte1 = bits 8-14 = 0,1,1,0,1,0,1   -> 0b01010110 = 0x56
        byte[] data = [0x0C, 0x56];
        var decoded = new byte[8];

        // Entries: 1 then 0 -> E6 = (1,0); 3 -> E7 = (0,3). Output is simply the three literals.
        Lzw.Decode(LzwFormat.Gif(2), data, decoded).ShouldBe(3);
        decoded[..3].ShouldBe(new byte[] { 1, 0, 3 });
    }

    // ---- round trips --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Roots))]
    public void Gif_RoundTrips_AtEveryRoot_AcrossEveryWidthBoundaryUpTo12Bits(int root)
    {
        var original = Data(root, 200_000);
        var (encoded, stats) = Encode(LzwFormat.Gif(root), original, EncoderMode.ClearWhenFull);

        // The fixture must actually reach every width from root+1 to 12, or it proves nothing.
        for (var w = root + 1; w <= 12; w++)
        {
            stats.WidthsUsed.ShouldContain(w);
        }

        RoundTrip(LzwFormat.Gif(root), original, encoded);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void Gif_HandlesTheKwKwKCase_AtEveryRoot(int root)
    {
        // A single repeated value makes the encoder use the entry it is defining right now.
        var original = new byte[3000];
        original.AsSpan().Fill((byte)((1 << root) - 1));
        var (encoded, _) = Encode(LzwFormat.Gif(root), original, EncoderMode.ClearWhenFull);

        RoundTrip(LzwFormat.Gif(root), original, encoded);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void Gif_AFullTableWithNoClear_KeepsDecodingAt12Bits_AndAddsNothing(int root)
    {
        // GIF's deferred clear: the table fills at 4096 and the encoder just keeps emitting existing
        // codes at 12 bits with no clear. The decoder must neither stop, widen past 12, nor add entries.
        var original = Data(root, 200_000);
        var (encoded, stats) = Encode(LzwFormat.Gif(root), original, EncoderMode.Deferred);

        stats.CodesAfterFull.ShouldBeGreaterThan(1000);
        stats.Clears.ShouldBe(1); // only the leading one
        RoundTrip(LzwFormat.Gif(root), original, encoded);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void Gif_AClearMidStream_ResetsTheTableAndTheWidth(int root)
    {
        var original = Data(root, 30_000);
        // A clear every 700 codes lands inside 10-, 11- and 12-bit territory, so the width reset is seen.
        var (encoded, stats) = Encode(LzwFormat.Gif(root), original, EncoderMode.ClearEvery700);

        stats.Clears.ShouldBeGreaterThan(3);
        RoundTrip(LzwFormat.Gif(root), original, encoded);
    }

    [Fact]
    public void AClearAsTheVeryFirstCodeAndRepeated_IsHarmless()
    {
        // clear, clear, 'a', EOI at root 8 / GIF: two clears in a row must not corrupt the state.
        var data = Pack(false, (256, 9), (256, 9), (65, 9), (257, 9));
        var decoded = new byte[4];
        Lzw.Decode(LzwFormat.Gif(8), data, decoded).ShouldBe(1);
        decoded[0].ShouldBe((byte)65);
    }

    // ---- the other presets through the same decoder -------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pdf_RoundTrips_WithAndWithoutEarlyChange(bool earlyChange)
    {
        var format = LzwFormat.Pdf(earlyChange);
        var original = Data(8, 40_000);
        var (encoded, _) = Encode(format, original, EncoderMode.ClearWhenFull);

        RoundTrip(format, original, encoded);
    }

    [Fact]
    public void ThePresets_DifferWhereTheyShould()
    {
        // Same MSB-first stream, early change on vs off: the width schedule diverges at 511, so decoding
        // an early-change stream as no-early-change must NOT reproduce the data.
        var original = Data(8, 20_000);
        var (encoded, _) = Encode(LzwFormat.Tiff, original, EncoderMode.ClearWhenFull);

        var wrong = new byte[original.Length];
        Lzw.Decode(LzwFormat.Pdf(earlyChange: false), encoded, wrong);
        wrong.ShouldNotBe(original);
    }

    // ---- truncation, a full destination, corruption, API --------------------------------------------

    [Theory]
    [MemberData(nameof(Roots))]
    public void Gif_TruncatedInput_ReturnsTheCleanPrefix(int root)
    {
        var original = Data(root, 20_000);
        var (encoded, _) = Encode(LzwFormat.Gif(root), original, EncoderMode.ClearWhenFull);

        foreach (var cut in new[] { 0, 1, 2, 3, encoded.Length / 3, encoded.Length / 2, encoded.Length - 1 })
        {
            var decoded = new byte[original.Length];
            var written = Lzw.Decode(LzwFormat.Gif(root), encoded.AsSpan(0, cut), decoded);

            written.ShouldBeLessThanOrEqualTo(original.Length);
            decoded.AsSpan(0, written).ToArray().ShouldBe(original[..written]);
        }
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void Gif_AFullDestination_StopsExactly_WithoutOverrunning(int root)
    {
        var original = Data(root, 5_000);
        var (encoded, _) = Encode(LzwFormat.Gif(root), original, EncoderMode.ClearWhenFull);

        // Sizes that land inside an entry, and one beyond the data.
        foreach (var size in new[] { 1, 2, 7, 100, 1234, 4999 })
        {
            var guarded = new byte[size + 8];
            guarded.AsSpan(size).Fill(0xEE);

            var written = Lzw.Decode(LzwFormat.Gif(root), encoded, guarded.AsSpan(0, size));

            written.ShouldBe(size);
            guarded.AsSpan(0, size).ToArray().ShouldBe(original[..size]);
            guarded.AsSpan(size).ToArray().ShouldAllBe(b => b == 0xEE);
        }
    }

    [Fact]
    public void Gif_ACodeBeyondTheNextFreeOne_StopsAndKeepsWhatWasWritten()
    {
        // Root 2: clear 4, then literal 1, then 7 where only 6 is next free -> corrupt after one byte.
        var data = Pack(false, (4, 3), (1, 3), (7, 3), (5, 3));
        var decoded = new byte[8];
        Lzw.Decode(LzwFormat.Gif(2), data, decoded).ShouldBe(1);
        decoded[0].ShouldBe((byte)1);
    }

    [Fact]
    public void Gif_ANonLiteralFirstCode_StopsWithNothingWritten()
    {
        var data = Pack(false, (4, 3), (6, 3), (5, 3));
        Lzw.Decode(LzwFormat.Gif(2), data, new byte[8]).ShouldBe(0);
    }

    [Fact]
    public void Gif_EmptyInput_WritesNothing() => Lzw.Decode(LzwFormat.Gif(8), [], new byte[8]).ShouldBe(0);

    [Fact]
    public void Gif_EndOfInformationStopsTheDecode_EvenWithInputLeft()
    {
        var data = Pack(false, (4, 3), (1, 3), (5, 3), (2, 3), (2, 3));
        var decoded = new byte[8];
        Lzw.Decode(LzwFormat.Gif(2), data, decoded).ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(12)]
    public void Gif_ARootSizeOutside2To8_IsRefused(int root)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => LzwFormat.Gif(root));
    }

    [Fact]
    public void ADefaultFormat_IsRefusedByDecode()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Lzw.Decode(default, [1, 2, 3], new byte[4]));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static void RoundTrip(LzwFormat format, byte[] original, byte[] encoded)
    {
        var decoded = new byte[original.Length];
        Lzw.Decode(format, encoded, decoded).ShouldBe(original.Length);
        decoded.ShouldBe(original);
    }

    /// <summary>Deterministic and compressible enough to grow the table; values stay inside the root.</summary>
    private static byte[] Data(int root, int length)
    {
        var alphabet = Math.Min(1 << root, 40);
        var data = new byte[length];
        var state = 0x12345678u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            data[i] = (byte)((state >> 16) % (uint)alphabet);
        }
        return data;
    }

    /// <summary>Pack (code, width) pairs in the given bit order, padding the last byte with zeros.</summary>
    private static byte[] Pack(bool msbFirst, params (int Code, int Width)[] codes)
    {
        var writer = new BitWriter(msbFirst);
        foreach (var (code, width) in codes)
        {
            writer.Write(code, width);
        }
        return writer.Finish();
    }

    private enum EncoderMode
    {
        /// <summary>Emit a clear when the table is full (what a TIFF/PDF encoder does).</summary>
        ClearWhenFull,
        /// <summary>Never clear: keep coding at 12 bits with no new entries (GIF's deferred clear).</summary>
        Deferred,
        /// <summary>Clear every 700 codes, and when full.</summary>
        ClearEvery700,
    }

    private sealed class EncodeStats
    {
        public HashSet<int> WidthsUsed { get; } = [];
        public int Clears { get; set; }
        public int CodesAfterFull { get; set; }
    }

    private sealed class BitWriter(bool msbFirst)
    {
        private readonly List<byte> _bytes = [];
        private ulong _acc;
        private int _count;

        public void Write(int code, int width)
        {
            if (msbFirst)
            {
                _acc = (_acc << width) | (uint)code;
                _count += width;
                while (_count >= 8)
                {
                    _bytes.Add((byte)(_acc >> (_count - 8)));
                    _count -= 8;
                }
            }
            else
            {
                _acc |= (ulong)code << _count;
                _count += width;
                while (_count >= 8)
                {
                    _bytes.Add((byte)_acc);
                    _acc >>= 8;
                    _count -= 8;
                }
            }
        }

        public byte[] Finish()
        {
            if (_count > 0)
            {
                _bytes.Add(msbFirst ? (byte)(_acc << (8 - _count)) : (byte)_acc);
            }
            return [.. _bytes];
        }
    }

    /// <summary>
    /// Test-only LZW encoder for any <see cref="LzwFormat"/>: Clear first, EOI last. The width is derived
    /// from the entry count the DECODER will have when it reads the next code, which is always one behind
    /// the encoder's own -- an encoder that widens off its own count runs one code ahead.
    /// </summary>
    private static (byte[] Bytes, EncodeStats Stats) Encode(LzwFormat format, ReadOnlySpan<byte> data, EncoderMode mode)
    {
        var clearCode = 1 << format.MinCodeSize;
        var eoiCode = clearCode + 1;
        var firstFree = clearCode + 2;
        var startWidth = format.MinCodeSize + 1;
        var early = format.EarlyChange ? 1 : 0;

        var stats = new EncodeStats();
        var writer = new BitWriter(format.MsbFirst);
        var table = new Dictionary<(int Prefix, byte Suffix), int>();
        var nextCode = firstFree;
        var width = startWidth;
        var codesSinceClear = 0;

        void Emit(int code)
        {
            stats.WidthsUsed.Add(width);
            writer.Write(code, width);
            if (nextCode >= 4096)
            {
                stats.CodesAfterFull++;
            }
        }

        int Widen(int basis) => width < 12 && basis + early >= (1 << width) ? width + 1 : width;

        void Reset()
        {
            table.Clear();
            nextCode = firstFree;
            width = startWidth;
            codesSinceClear = 0;
        }

        Emit(clearCode);
        stats.Clears++;
        if (data.Length > 0)
        {
            var omega = (int)data[0];
            for (var i = 1; i < data.Length; i++)
            {
                var k = data[i];
                if (table.TryGetValue((omega, k), out var combined))
                {
                    omega = combined;
                    continue;
                }

                Emit(omega);
                codesSinceClear++;
                if (nextCode < 4096)
                {
                    var basis = nextCode;
                    table[(omega, k)] = nextCode++;
                    width = Widen(basis);
                }
                else if (mode != EncoderMode.Deferred)
                {
                    // Table full: reset exactly as a decoder expects (clear at the current 12 bits).
                    Emit(clearCode);
                    stats.Clears++;
                    Reset();
                }

                if (mode == EncoderMode.ClearEvery700 && codesSinceClear >= 700)
                {
                    Emit(clearCode);
                    stats.Clears++;
                    Reset();
                }
                omega = k;
            }

            Emit(omega);
            if (nextCode < 4096)
            {
                // The decoder adds the entry for this last code before it reads EOI.
                width = Widen(nextCode);
            }
        }
        Emit(eoiCode);

        return (writer.Finish(), stats);
    }
}
