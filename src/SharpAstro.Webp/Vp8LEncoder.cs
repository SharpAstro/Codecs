namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 only. No libwebp, libvpx or
// RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are used only as black-box
// test oracles.

/// <summary>
/// The VP8L encoder (RFC 9649 sections 3.3, 3.5-3.7) at its second rung: LZ77 backward references
/// with the 2D distance codes, and a colour cache, chosen per image. No transforms and no meta
/// prefix codes yet, so one prefix-code group of five codes is built from the whole image.
/// </summary>
/// <remarks>
/// Effort 0 is the literal-only encoder, the same code with nothing to search and nothing to try,
/// not a second encoder. At every higher effort the literal-only encoding is also a candidate, so a
/// stream is never larger than it. A candidate is "copies or not, at one cache size"; each is
/// costed exactly (header bits plus symbol bits plus extra bits) by walking the pixels into
/// histograms, and the cheapest is written.
/// </remarks>
internal static class Vp8LEncoder
{
    /// <summary>The VP8L signature byte (RFC 9649 section 3.2).</summary>
    public const byte Signature = 0x2f;

    // Green codes 256 literals, 24 length prefix codes and the colour cache indices; red, blue
    // and alpha code 256 literals; the distance code has 40 prefix symbols.
    private const int LiteralAlphabet = 256;
    private const int LengthSymbols = 24;
    private const int ChannelAlphabet = 256;
    private const int DistanceAlphabet = 40;

    /// <summary>
    /// Encodes <paramref name="argb"/> (one 0xAARRGGBB int per pixel, row-major, tightly packed) as
    /// the payload of a <c>VP8L</c> chunk. <paramref name="width"/> and <paramref name="height"/>
    /// must be 1 to 16384.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<int> argb, int width, int height, bool alphaIsUsed, int effort)
    {
        long pixelCount = (long)width * height;
        var plan = Vp8LEncoderPlan.Create(pixelCount, effort, WebpLimits.MaximumEncoderWorkingBytes);
        return Encode(argb, width, height, alphaIsUsed, plan);
    }

    /// <summary>Encodes with an explicit <paramref name="plan"/> (the tests make their own budgets).</summary>
    public static byte[] Encode(ReadOnlySpan<int> argb, int width, int height, bool alphaIsUsed, in Vp8LEncoderPlan plan)
    {
        long pixelCount = (long)width * height;
        if (argb.Length < pixelCount)
        {
            throw new ArgumentException("The pixel span is shorter than width * height.", nameof(argb));
        }

        argb = argb[..(int)pixelCount];
        var mapper = new Vp8LDistanceMapper(width);

        // The one large allocation of the search, sized by the plan that was charged for it.
        ulong[] copyBuffer = plan.UseBackwardReferences ? new ulong[plan.MaxCopies] : [];
        int copyCount = Vp8LMatchFinder.Find(argb, width, plan, mapper, copyBuffer);
        ReadOnlySpan<ulong> copies = copyBuffer.AsSpan(0, copyCount);

        int[] cache = new int[1 << Vp8LBackwardReferences.MaxCacheBits];

        // The literal-only encoding is always a candidate; with copies, every cache size the
        // effort asks for is another.
        Candidate best = Evaluate(argb, [], mapper, 0, cache);
        if (copyCount > 0)
        {
            foreach (int cacheBits in plan.CacheBitsToTry)
            {
                Candidate candidate = Evaluate(argb, copies, mapper, cacheBits, cache);
                if (candidate.Bits < best.Bits)
                {
                    best = candidate;
                }
            }
        }
        else if (plan.Effort > 0)
        {
            // No copies to be had (noise, say), but a cache can still pay.
            foreach (int cacheBits in plan.CacheBitsToTry)
            {
                if (cacheBits == 0)
                {
                    continue;
                }

                Candidate candidate = Evaluate(argb, [], mapper, cacheBits, cache);
                if (candidate.Bits < best.Bits)
                {
                    best = candidate;
                }
            }
        }

        return Write(argb, width, height, alphaIsUsed, best, best.UseCopies ? copies : [], mapper, cache);
    }

    /// <summary>
    /// Encodes with exactly one candidate instead of choosing the cheapest: the copies the plan's
    /// match finder finds (or none) and a cache of <paramref name="cacheBits"/> bits (0 for none).
    /// For tests, which need every cache size written, not just the ones that pay.
    /// </summary>
    internal static byte[] EncodeCandidate(
        ReadOnlySpan<int> argb,
        int width,
        int height,
        bool alphaIsUsed,
        in Vp8LEncoderPlan plan,
        int cacheBits,
        bool useCopies)
    {
        argb = argb[..checked(width * height)];
        var mapper = new Vp8LDistanceMapper(width);
        ulong[] copyBuffer = plan.UseBackwardReferences ? new ulong[plan.MaxCopies] : [];
        int copyCount = useCopies ? Vp8LMatchFinder.Find(argb, width, plan, mapper, copyBuffer) : 0;
        ReadOnlySpan<ulong> copies = copyBuffer.AsSpan(0, copyCount);
        int[] cache = new int[1 << Vp8LBackwardReferences.MaxCacheBits];
        Candidate candidate = Evaluate(argb, copies, mapper, cacheBits, cache);
        return Write(argb, width, height, alphaIsUsed, candidate, copies, mapper, cache);
    }

    private static byte[] Write(
        ReadOnlySpan<int> argb,
        int width,
        int height,
        bool alphaIsUsed,
        Candidate best,
        ReadOnlySpan<ulong> copies,
        Vp8LDistanceMapper mapper,
        int[] cache)
    {
        // Rough guess at the size, so a typical image does not regrow its buffer much.
        var writer = new Vp8LBitWriter((int)Math.Min(best.Bits / 8 + 256, 1 << 26));

        writer.WriteBits(Signature, 8);
        writer.WriteBits((uint)(width - 1), 14);
        writer.WriteBits((uint)(height - 1), 14);
        writer.WriteBits(alphaIsUsed ? 1u : 0u, 1);
        writer.WriteBits(0, 3); // version

        writer.WriteBits(0, 1); // no transform
        if (best.CacheBits == 0)
        {
            writer.WriteBits(0, 1); // no colour cache
        }
        else
        {
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)best.CacheBits, 4);
        }

        writer.WriteBits(0, 1); // no meta prefix image: one prefix code group for the whole image

        foreach (Vp8LPrefixCode code in best.Codes)
        {
            code.WriteHeader(writer);
        }

        var sink = new WriteSink(writer, best.Codes);
        Walk(argb, copies, mapper, best.CacheBits, cache, ref sink);
        return writer.Finish().ToArray();
    }

    private sealed record Candidate(long Bits, int CacheBits, bool UseCopies, Vp8LPrefixCode[] Codes);

    private static Candidate Evaluate(
        ReadOnlySpan<int> argb,
        ReadOnlySpan<ulong> copies,
        Vp8LDistanceMapper mapper,
        int cacheBits,
        int[] cache)
    {
        var sink = new HistogramSink(LiteralAlphabet + LengthSymbols + (cacheBits == 0 ? 0 : 1 << cacheBits));
        Walk(argb, copies, mapper, cacheBits, cache, ref sink);

        // Green, red, blue, alpha, distance: the order the stream carries them in.
        int[][] histograms = [sink.Green, sink.Red, sink.Blue, sink.Alpha, sink.Distance];
        var codes = new Vp8LPrefixCode[histograms.Length];
        long bits = sink.ExtraBits;
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = Vp8LPrefixCode.Build(histograms[i]);
            bits += codes[i].DataBits(histograms[i]) + codes[i].HeaderBits();
        }

        return new Candidate(bits, cacheBits, !copies.IsEmpty, codes);
    }

    private interface ISymbolSink
    {
        void Literal(uint argb);

        void CacheHit(int index);

        void Copy(int length, int distanceCode);
    }

    // Replays the stream the way the decoder will read it: a pixel is a copy if one starts here,
    // else a colour cache hit if its slot holds it, else a literal. The cache is updated with every
    // pixel exactly as RFC 9649 section 3.6.2 has the decoder do, so the two stay in step.
    private static void Walk<TSink>(
        ReadOnlySpan<int> argb,
        ReadOnlySpan<ulong> copies,
        Vp8LDistanceMapper mapper,
        int cacheBits,
        int[] cache,
        ref TSink sink)
        where TSink : struct, ISymbolSink
    {
        bool useCache = cacheBits != 0;
        if (useCache)
        {
            Array.Clear(cache, 0, 1 << cacheBits);
        }

        int position = 0;
        int copyIndex = 0;
        while (position < argb.Length)
        {
            if (copyIndex < copies.Length && Vp8LMatchFinder.Position(copies[copyIndex]) == position)
            {
                ulong copy = copies[copyIndex++];
                int length = Vp8LMatchFinder.Length(copy);
                sink.Copy(length, mapper.ToCode(Vp8LMatchFinder.Distance(copy)));
                if (useCache)
                {
                    for (int k = position; k < position + length; k++)
                    {
                        cache[Vp8LBackwardReferences.CacheHash(argb[k], cacheBits)] = argb[k];
                    }
                }

                position += length;
                continue;
            }

            int color = argb[position++];
            if (useCache)
            {
                int slot = Vp8LBackwardReferences.CacheHash(color, cacheBits);
                if (cache[slot] == color)
                {
                    sink.CacheHit(slot);
                    continue;
                }

                cache[slot] = color;
            }

            sink.Literal((uint)color);
        }
    }

    private struct HistogramSink : ISymbolSink
    {
        public readonly int[] Green;
        public readonly int[] Red = new int[ChannelAlphabet];
        public readonly int[] Blue = new int[ChannelAlphabet];
        public readonly int[] Alpha = new int[ChannelAlphabet];
        public readonly int[] Distance = new int[DistanceAlphabet];
        public long ExtraBits;

        public HistogramSink(int greenAlphabet) => Green = new int[greenAlphabet];

        public void Literal(uint argb)
        {
            Alpha[argb >> 24]++;
            Red[(argb >> 16) & 0xff]++;
            Green[(argb >> 8) & 0xff]++;
            Blue[argb & 0xff]++;
        }

        public void CacheHit(int index) => Green[LiteralAlphabet + LengthSymbols + index]++;

        public void Copy(int length, int distanceCode)
        {
            Vp8LBackwardReferences.PrefixEncode(length, out int lengthSymbol, out int lengthExtra, out _);
            Vp8LBackwardReferences.PrefixEncode(distanceCode, out int distanceSymbol, out int distanceExtra, out _);
            Green[LiteralAlphabet + lengthSymbol]++;
            Distance[distanceSymbol]++;
            ExtraBits += lengthExtra + distanceExtra;
        }
    }

    private readonly struct WriteSink(Vp8LBitWriter writer, Vp8LPrefixCode[] codes) : ISymbolSink
    {
        public void Literal(uint argb)
        {
            // Each literal pixel is its four channels in the order green, red, blue, alpha.
            codes[0].WriteSymbol(writer, (int)((argb >> 8) & 0xff));
            codes[1].WriteSymbol(writer, (int)((argb >> 16) & 0xff));
            codes[2].WriteSymbol(writer, (int)(argb & 0xff));
            codes[3].WriteSymbol(writer, (int)(argb >> 24));
        }

        public void CacheHit(int index) => codes[0].WriteSymbol(writer, LiteralAlphabet + LengthSymbols + index);

        public void Copy(int length, int distanceCode)
        {
            // The length prefix symbol (in the green code) and its extra bits, then the distance
            // prefix symbol and its extra bits.
            Vp8LBackwardReferences.PrefixEncode(length, out int lengthSymbol, out int lengthExtraBits, out int lengthExtra);
            codes[0].WriteSymbol(writer, LiteralAlphabet + lengthSymbol);
            writer.WriteBits((uint)lengthExtra, lengthExtraBits);
            Vp8LBackwardReferences.PrefixEncode(distanceCode, out int distanceSymbol, out int distanceExtraBits, out int distanceExtra);
            codes[4].WriteSymbol(writer, distanceSymbol);
            writer.WriteBits((uint)distanceExtra, distanceExtraBits);
        }
    }
}
