using System;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Jpeg2000;

/// <summary>
/// Tier-1: the EBCOT block decoder of T.800 Annex D. It turns one code-block's
/// coded bytes into signed coefficients, bit-plane by bit-plane, through three
/// coding passes driven by the MQ arithmetic decoder.
/// <para>
/// Two things here are worth reading before changing anything.
/// </para>
/// <para>
/// <b>The scan is not raster.</b> D.2: the block is walked in horizontal stripes
/// four rows tall, and within a stripe column by column, top to bottom in each
/// column. Writing a raster scan produces output that looks like structured
/// noise, which reads as a context-modelling bug and is not one.
/// </para>
/// <para>
/// <b>The contexts are indices, not values.</b> Every adaptive slot starts in the
/// same state bar three, so permuting the context <em>numbers</em> is a bijection
/// that changes nothing — the JBIG2 work established this by measurement, where
/// swapping two template bits left the reference decoder still agreeing. What
/// matters is <em>which neighbours a context reads</em>. So the tests for this
/// file pin the neighbourhood, not the number; and the numbering is nonetheless
/// written to match T.800's tables exactly, because the three exceptional initial
/// states (Table D.7) name specific contexts and a permutation would seat them on
/// the wrong ones.
/// </para>
/// </summary>
internal static class BlockDecoder
{
    // T.800 Table D.7. Nineteen adaptive contexts: nine for zero coding, five
    // for sign coding, three for magnitude refinement, plus run-length and
    // uniform.
    private const int ContextCount = 19;
    private const int RunLengthContext = 17;
    private const int UniformContext = 18;

    /// <summary>
    /// Decodes one code-block into <paramref name="band"/>'s coefficient array.
    /// </summary>
    /// <param name="band">The subband the block belongs to; its orientation picks the context table.</param>
    /// <param name="block">The block, with its inclusion state and coded byte ranges already read by tier-2.</param>
    /// <param name="tilePartData">The tile-part's coded data, which the block's segments index into.</param>
    public static void Decode(Subband band, CodeBlock block, ReadOnlySpan<byte> tilePartData)
    {
        var width = block.Bounds.Width;
        var height = block.Bounds.Height;
        if (width == 0 || height == 0 || block.PassCount == 0) return;

        // How many magnitude bit-planes actually carry data: the band's Mb, less
        // the leading all-zero planes the packet header declared.
        var planes = band.MagnitudeBits - block.ZeroBitPlanes;
        if (planes <= 0) return;

        var coded = Gather(block, tilePartData);

        var state = new BlockState(width, height, band.Kind);
        var mq = new MqDecoder(coded);

        Span<byte> contexts = stackalloc byte[ContextCount];
        contexts.Clear();

        // The three states T.800 Table D.7 seeds away from zero. This is the
        // roadmap's hazard 1: the coder is shared with JBIG2 but its
        // initialisation is not, and getting it wrong decodes the first
        // code-block plausibly and then drifts.
        contexts[0] = 4 << 1;                    // zero coding, all-insignificant neighbourhood
        contexts[RunLengthContext] = 3 << 1;
        contexts[UniformContext] = 46 << 1;

        var passesLeft = block.PassCount;
        var plane = planes - 1;

        // The first pass of a code-block is always a cleanup pass: there is
        // nothing significant yet for a significance-propagation pass to
        // propagate from, and nothing to refine.
        state.CleanupPass(ref mq, contexts, plane);
        passesLeft--;
        plane--;

        while (plane >= 0 && passesLeft > 0)
        {
            state.SignificancePropagationPass(ref mq, contexts, plane);
            if (--passesLeft == 0) break;

            state.MagnitudeRefinementPass(ref mq, contexts, plane);
            if (--passesLeft == 0) break;

            state.CleanupPass(ref mq, contexts, plane);
            passesLeft--;
            plane--;
        }

        state.WriteTo(band, block);
    }

    /// <summary>
    /// Concatenates the block's coded segments.
    /// <para>
    /// One layer means one segment, so this is a copy of a single range today.
    /// It is written as a gather anyway because the alternative — assuming one
    /// segment — would be an assumption with no assertion behind it, and rung 3
    /// makes it false.
    /// </para>
    /// </summary>
    private static byte[] Gather(CodeBlock block, ReadOnlySpan<byte> tilePartData)
    {
        var total = 0;
        foreach (var (_, length) in block.Segments) total += length;

        var coded = new byte[total];
        var offset = 0;
        foreach (var (start, length) in block.Segments)
        {
            tilePartData.Slice(start, length).CopyTo(coded.AsSpan(offset));
            offset += length;
        }

        return coded;
    }
}

/// <summary>
/// The per-coefficient state one code-block decode carries: significance, sign,
/// accumulated magnitude, and the two flags that sequence the passes.
/// <para>
/// Every array is padded by one on each side so that a coefficient's eight
/// neighbours can be read without a bounds test. The padding is never written,
/// so it stays insignificant — which is exactly what T.800 D.3 requires of
/// positions outside the code-block, rather than a convenient fiction.
/// </para>
/// </summary>
internal sealed class BlockState
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly BandKind _kind;

    private readonly byte[] _significant;
    private readonly byte[] _negative;
    private readonly byte[] _visited;
    private readonly byte[] _refined;
    private readonly int[] _magnitude;

    public BlockState(int width, int height, BandKind kind)
    {
        _width = width;
        _height = height;
        _kind = kind;
        _stride = width + 2;

        var cells = _stride * (height + 2);
        _significant = new byte[cells];
        _negative = new byte[cells];
        _visited = new byte[cells];
        _refined = new byte[cells];
        _magnitude = new int[cells];
    }

    private int Index(int x, int y) => (y + 1) * _stride + (x + 1);

    /// <summary>
    /// D.3.1: code every insignificant coefficient that has at least one
    /// significant neighbour, and remember which ones were visited so the other
    /// two passes skip them.
    /// </summary>
    public void SignificancePropagationPass(ref MqDecoder mq, scoped Span<byte> contexts, int plane)
    {
        for (var stripe = 0; stripe < _height; stripe += 4)
        {
            var stripeEnd = Math.Min(stripe + 4, _height);
            for (var x = 0; x < _width; x++)
            {
                for (var y = stripe; y < stripeEnd; y++)
                {
                    var i = Index(x, y);
                    if (_significant[i] != 0) continue;

                    var context = ZeroCodingContext(i);

                    // Context 0 means no significant neighbour at all, and such a
                    // coefficient is not coded in this pass — it is left to the
                    // cleanup pass. This test is what divides the two passes.
                    if (context == 0) continue;

                    if (mq.Decode(contexts, context) != 0) MakeSignificant(ref mq, contexts, i, plane);
                    _visited[i] = 1;
                }
            }
        }
    }

    /// <summary>
    /// D.3.3: refine every coefficient that was already significant when this
    /// bit-plane began. One that became significant during this plane's
    /// significance-propagation pass is skipped — its bit for this plane is
    /// already the one that made it significant.
    /// </summary>
    public void MagnitudeRefinementPass(ref MqDecoder mq, scoped Span<byte> contexts, int plane)
    {
        for (var stripe = 0; stripe < _height; stripe += 4)
        {
            var stripeEnd = Math.Min(stripe + 4, _height);
            for (var x = 0; x < _width; x++)
            {
                for (var y = stripe; y < stripeEnd; y++)
                {
                    var i = Index(x, y);
                    if (_significant[i] == 0 || _visited[i] != 0) continue;

                    // Table D.2: the first refinement of a coefficient is coded
                    // against whether its neighbourhood is quiet; every later one
                    // shares a single context.
                    int context;
                    if (_refined[i] != 0) context = 16;
                    else context = HasSignificantNeighbour(i) ? 15 : 14;

                    if (mq.Decode(contexts, context) != 0) _magnitude[i] |= 1 << plane;
                    _refined[i] = 1;
                }
            }
        }
    }

    /// <summary>
    /// D.3.4: code everything the first two passes left, with the run-length
    /// shortcut for a whole column of four that is quiet in every direction.
    /// </summary>
    public void CleanupPass(ref MqDecoder mq, scoped Span<byte> contexts, int plane)
    {
        for (var stripe = 0; stripe < _height; stripe += 4)
        {
            var full = stripe + 4 <= _height;
            var stripeEnd = Math.Min(stripe + 4, _height);

            for (var x = 0; x < _width; x++)
            {
                var y = stripe;

                // The run-length mode of D.3.4 applies only to a complete column
                // of four in which nothing is significant, nothing was coded in
                // the significance pass, and no coefficient has a significant
                // neighbour. One symbol then stands for all four.
                if (full && ColumnIsEntirelyQuiet(x, stripe))
                {
                    if (mq.Decode(contexts, 17) == 0) continue;

                    // Two bits, most significant first, in the uniform context:
                    // which of the four is the first significant one.
                    var first = (mq.Decode(contexts, 18) << 1) | mq.Decode(contexts, 18);
                    y = stripe + first;
                    MakeSignificant(ref mq, contexts, Index(x, y), plane);
                    y++;
                }

                for (; y < stripeEnd; y++)
                {
                    var i = Index(x, y);
                    if (_visited[i] == 0 && _significant[i] == 0)
                    {
                        if (mq.Decode(contexts, ZeroCodingContext(i)) != 0)
                            MakeSignificant(ref mq, contexts, i, plane);
                    }
                }
            }
        }

        // The visited flags scope to one bit-plane, and the cleanup pass is where
        // a bit-plane ends.
        Array.Clear(_visited);
    }

    /// <summary>
    /// A coefficient has just been found significant at <paramref name="plane"/>:
    /// set the bit, then decode its sign.
    /// </summary>
    private void MakeSignificant(ref MqDecoder mq, scoped Span<byte> contexts, int i, int plane)
    {
        _magnitude[i] |= 1 << plane;
        _significant[i] = 1;

        var (context, invert) = SignContext(i);

        // Hazard 4: the sign is the decoded bit XOR a bit from the SAME table
        // entry. Dropping the XOR yields correct magnitudes with wrong signs,
        // which is invisible on detail and unmistakable on a smooth gradient —
        // hence the ramp fixture.
        _negative[i] = (byte)(mq.Decode(contexts, context) ^ invert);
    }

    private bool ColumnIsEntirelyQuiet(int x, int stripe)
    {
        for (var k = 0; k < 4; k++)
        {
            var i = Index(x, stripe + k);
            if (_significant[i] != 0 || _visited[i] != 0) return false;
            if (ZeroCodingContext(i) != 0) return false;
        }

        return true;
    }

    private bool HasSignificantNeighbour(int i)
    {
        return _significant[i - 1] != 0 || _significant[i + 1] != 0 ||
               _significant[i - _stride] != 0 || _significant[i + _stride] != 0 ||
               _significant[i - _stride - 1] != 0 || _significant[i - _stride + 1] != 0 ||
               _significant[i + _stride - 1] != 0 || _significant[i + _stride + 1] != 0;
    }

    /// <summary>
    /// T.800 Table D.1: the zero-coding context, from how many of the eight
    /// neighbours are significant, split by direction.
    /// <para>
    /// The three column groups of the table are one function of (horizontal,
    /// vertical, diagonal) applied three ways: LL and LH read it as written, HL
    /// reads it with the horizontal and vertical counts <em>interchanged</em>, and
    /// HH has its own ordering that leads on the diagonals. Writing it as a
    /// decision rather than a 256-entry lookup keeps it checkable line by line
    /// against the published table.
    /// </para>
    /// </summary>
    private int ZeroCodingContext(int i)
    {
        var horizontal = _significant[i - 1] + _significant[i + 1];
        var vertical = _significant[i - _stride] + _significant[i + _stride];
        var diagonal = _significant[i - _stride - 1] + _significant[i - _stride + 1] +
                       _significant[i + _stride - 1] + _significant[i + _stride + 1];

        return ZeroCodingContext(horizontal, vertical, diagonal, _kind);
    }

    /// <summary>The context table itself, exposed so tests can drive it directly.</summary>
    internal static int ZeroCodingContext(int horizontal, int vertical, int diagonal, BandKind kind)
    {
        if (kind == BandKind.Hh)
        {
            var straight = horizontal + vertical;
            if (diagonal >= 3) return 8;
            if (diagonal == 2) return straight >= 1 ? 7 : 6;
            if (diagonal == 1) return straight >= 2 ? 5 : straight == 1 ? 4 : 3;
            return straight >= 2 ? 2 : straight;
        }

        // HL is the LL/LH table read with the two straight directions swapped.
        if (kind == BandKind.Hl) (horizontal, vertical) = (vertical, horizontal);

        if (horizontal == 2) return 8;
        if (horizontal == 1) return vertical >= 1 ? 7 : diagonal >= 1 ? 6 : 5;
        if (vertical == 2) return 4;
        if (vertical == 1) return 3;
        return diagonal >= 2 ? 2 : diagonal;
    }

    /// <summary>
    /// T.800 Tables D.3 and D.4: the sign context and the XOR bit that goes with
    /// it, from the two horizontal and two vertical neighbours' signed
    /// contributions.
    /// </summary>
    private (int Context, int Invert) SignContext(int i)
    {
        var horizontal = Contribution(i - 1) + Contribution(i + 1);
        var vertical = Contribution(i - _stride) + Contribution(i + _stride);

        return SignContext(Math.Clamp(horizontal, -1, 1), Math.Clamp(vertical, -1, 1));
    }

    /// <summary>The sign table itself, exposed so tests can drive it directly.</summary>
    internal static (int Context, int Invert) SignContext(int horizontal, int vertical)
    {
        // The table is antisymmetric: negating both contributions keeps the
        // context and flips the XOR bit, which is how five contexts cover nine
        // combinations.
        if (horizontal < 0) return (SignContext(-horizontal, -vertical).Context, 1);

        if (horizontal > 0)
            return vertical switch { > 0 => (13, 0), 0 => (12, 0), _ => (11, 0) };

        return vertical switch { > 0 => (10, 0), 0 => (9, 0), _ => (10, 1) };
    }

    /// <summary>A neighbour's signed contribution: 0 if insignificant, else +1 or -1.</summary>
    private int Contribution(int i) => _significant[i] == 0 ? 0 : _negative[i] != 0 ? -1 : 1;

    /// <summary>Copies the decoded magnitudes and signs into the subband's coefficient array.</summary>
    public void WriteTo(Subband band, CodeBlock block)
    {
        var bandWidth = band.Bounds.Width;
        var offsetX = block.Bounds.X0 - band.Bounds.X0;
        var offsetY = block.Bounds.Y0 - band.Bounds.Y0;

        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var i = Index(x, y);
                var magnitude = _magnitude[i];
                if (magnitude == 0) continue;

                band.Coefficients[(offsetY + y) * bandWidth + offsetX + x] =
                    _negative[i] != 0 ? -magnitude : magnitude;
            }
        }
    }
}
