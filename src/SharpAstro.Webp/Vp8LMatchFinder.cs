namespace SharpAstro.Webp;

// Provenance: a textbook hash-chain match finder over ARGB pixels. The bitstream rules it serves
// (length and distance limits, the 2D distance codes) are from RFC 9649 only; no libwebp source
// was read or transcribed.

/// <summary>
/// Finds LZ77 copies in an ARGB image with a hash chain over runs of <see cref="MinMatch"/> pixels.
/// The window, chain depth and the length at which a match is good enough come from the
/// <see cref="Vp8LEncoderPlan"/>; every array here is sized from it, which was charged up front.
/// A copy is stored as one <c>ulong</c>: the pixel position in the high 32 bits, then
/// <c>(length - 1) &lt;&lt; 20 | distance</c> (a length is at most 4096 and a distance under 2^20).
/// </summary>
internal static class Vp8LMatchFinder
{
    /// <summary>The shortest copy emitted. A shorter one costs about what the literals it replaces do.</summary>
    public const int MinMatch = 3;

    /// <summary>The position of a packed copy.</summary>
    public static int Position(ulong copy) => (int)(copy >> 32);

    /// <summary>The length of a packed copy, 1 to 4096.</summary>
    public static int Length(ulong copy) => (int)((uint)copy >> 20) + 1;

    /// <summary>The linear backward distance of a packed copy.</summary>
    public static int Distance(ulong copy) => (int)((uint)copy & 0xfffff);

    /// <summary>Packs one copy.</summary>
    public static ulong Pack(int position, int length, int distance) =>
        ((ulong)(uint)position << 32) | (uint)(((length - 1) << 20) | distance);

    /// <summary>
    /// Fills <paramref name="copies"/> with the non-overlapping copies, in position order, and
    /// returns how many. The span must hold <see cref="Vp8LEncoderPlan.MaxCopies"/> entries.
    /// </summary>
    public static int Find(
        ReadOnlySpan<int> argb,
        int width,
        in Vp8LEncoderPlan plan,
        Vp8LDistanceMapper mapper,
        Span<ulong> copies)
    {
        int n = argb.Length;
        if (!plan.UseBackwardReferences || n < MinMatch)
        {
            return 0;
        }

        var finder = new Finder(argb, width, plan, mapper);
        int count = 0;
        int position = 0;
        while (position + MinMatch <= n)
        {
            (int length, int distance) = finder.Best(position);
            finder.Insert(position);
            if (length == 0)
            {
                position++;
                continue;
            }

            // Lazy matching: if the next pixel starts a longer copy, this one is a literal.
            if (finder.Lazy && length < finder.NiceLength && position + 1 + MinMatch <= n && finder.Best(position + 1).Length > length)
            {
                position++;
                continue;
            }

            copies[count++] = Pack(position, length, distance);
            for (int k = position + 1; k < position + length; k++)
            {
                finder.Insert(k);
            }

            position += length;
        }

        return count;
    }

    private readonly ref struct Finder
    {
        private readonly ReadOnlySpan<int> _argb;
        private readonly int _width;
        private readonly Vp8LDistanceMapper _mapper;
        private readonly int _windowSize;
        private readonly int _mask;
        private readonly int _hashShift;
        private readonly int _chainDepth;
        // Positions are stored plus one, so the zero a fresh array holds means "none".
        private readonly int[] _head;
        private readonly int[] _prev;

        public readonly int NiceLength;
        public readonly bool Lazy;

        public Finder(ReadOnlySpan<int> argb, int width, in Vp8LEncoderPlan plan, Vp8LDistanceMapper mapper)
        {
            _argb = argb;
            _width = width;
            _mapper = mapper;
            _windowSize = plan.WindowSize;
            _mask = _windowSize - 1;
            _hashShift = 32 - plan.HashBits;
            _chainDepth = plan.ChainDepth;
            NiceLength = plan.NiceLength;
            Lazy = plan.Lazy;
            _head = new int[1 << plan.HashBits];
            _prev = new int[_windowSize];
        }

        private uint Hash(int p)
        {
            uint h = (uint)_argb[p] * 0x9e3779b1u;
            h = (h + (uint)_argb[p + 1]) * 0x85ebca6bu;
            h = (h + (uint)_argb[p + 2]) * 0xc2b2ae35u;
            return h >> _hashShift;
        }

        public void Insert(int p)
        {
            if (p + MinMatch <= _argb.Length)
            {
                uint h = Hash(p);
                _prev[p & _mask] = _head[h];
                _head[h] = p + 1;
            }
        }

        // Longest match at `pos`: tries distance 1 and one row up first (the commonest, and the
        // cheapest codes), then walks the chain newest-first, which is nearest-first.
        public (int Length, int Distance) Best(int pos)
        {
            int maxLength = Math.Min(Vp8LBackwardReferences.MaxLength, _argb.Length - pos);
            var best = new Candidate();
            ReadOnlySpan<int> here = _argb.Slice(pos, maxLength);

            Consider(1, pos, maxLength, here, ref best);
            Consider(_width, pos, maxLength, here, ref best);
            if (best.Length < maxLength)
            {
                int cand = _head[Hash(pos)] - 1;
                for (int steps = _chainDepth; cand >= 0 && steps > 0 && best.Length < NiceLength && best.Length < maxLength; steps--)
                {
                    int distance = pos - cand;
                    if (distance >= _windowSize || distance > Vp8LBackwardReferences.MaxDistance)
                    {
                        break;
                    }

                    Consider(distance, pos, maxLength, here, ref best);
                    int next = _prev[cand & _mask] - 1;
                    if (next >= cand)
                    {
                        break; // a slot overwritten by a newer position: the chain is no longer ours
                    }

                    cand = next;
                }
            }

            // A very short copy at a far distance costs more than the literals it replaces.
            if (best.Length < MinMatch || (best.Length <= 4 && best.Code > 4096))
            {
                return (0, 0);
            }

            return (best.Length, best.Distance);
        }

        private struct Candidate
        {
            public int Length;
            public int Distance;
            public int Code = int.MaxValue;

            public Candidate()
            {
            }
        }

        private void Consider(int distance, int pos, int maxLength, ReadOnlySpan<int> here, ref Candidate best)
        {
            if (distance < 1 || distance > pos || distance > Vp8LBackwardReferences.MaxDistance)
            {
                return;
            }

            // Only a copy longer than the best so far is wanted (ties go to distance 1 and one row
            // up, tried first): one that is no longer differs from `here` at index best.Length.
            if (best.Length > 0 && best.Length < maxLength && _argb[pos - distance + best.Length] != _argb[pos + best.Length])
            {
                return;
            }

            int length = _argb.Slice(pos - distance, maxLength).CommonPrefixLength(here);
            if (length < MinMatch)
            {
                return;
            }

            int code = _mapper.ToCode(distance);
            if (length > best.Length || (length == best.Length && code < best.Code))
            {
                best.Length = length;
                best.Distance = distance;
                best.Code = code;
            }
        }
    }
}
