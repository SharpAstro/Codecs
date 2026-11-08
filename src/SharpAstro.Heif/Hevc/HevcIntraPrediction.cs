namespace SharpAstro.Heif.Hevc;

/// <summary>
/// Intra sample prediction (H.265 8.4.4.2) for one square block, from its reference samples.
/// <para>
/// The reference samples p[-1][2N-1] .. p[-1][-1] .. p[2N-1][-1] are held as ONE line, <c>L</c>, in the
/// order the substitution process (8.4.4.2.2) walks them: up the left column from its bottom, through the
/// corner, then along the top row. <c>L[0]</c> is p[-1][2N-1], <c>L[2N]</c> the corner p[-1][-1] and
/// <c>L[2N+1+x]</c> is p[x][-1]. In that order both substitution and the [1 2 1] filter (8.4.4.2.3) are
/// plain operations on neighbours in the line.
/// </para>
/// </summary>
internal static class HevcIntraPrediction
{
    public const int Planar = 0, Dc = 1, Horizontal = 10, Vertical = 26;

    /// <summary>intraPredAngle (Table 8-5) for modes 0..34; 0 and 1 are not angular.</summary>
    private static ReadOnlySpan<sbyte> Angle =>
    [
        0, 0, 32, 26, 21, 17, 13, 9, 5, 2, 0, -2, -5, -9, -13, -17, -21, -26,
        -32, -26, -21, -17, -13, -9, -5, -2, 0, 2, 5, 9, 13, 17, 21, 26, 32,
    ];

    /// <summary>invAngle (Table 8-6) for modes 11..25, indexed by mode - 11.</summary>
    private static ReadOnlySpan<short> InverseAngle =>
    [
        -4096, -1638, -910, -630, -482, -390, -315, -256, -315, -390, -482, -630, -910, -1638, -4096,
    ];

    /// <summary>
    /// 8.4.4.2.2: fills unavailable entries of <paramref name="line"/> (length 4N+1). With none available
    /// every entry is the mid-grey 1 &lt;&lt; (bitDepth - 1); otherwise the first entry takes the first
    /// available value in walking order, and each later unavailable entry copies the one before it.
    /// </summary>
    public static void Substitute(Span<int> line, ReadOnlySpan<bool> available, int bitDepth)
    {
        var first = available.IndexOf(true);
        if (first < 0)
        {
            line.Fill(1 << (bitDepth - 1));
            return;
        }
        if (!available[0]) line[0] = line[first];
        for (var i = 1; i < line.Length; i++)
            if (!available[i]) line[i] = line[i - 1];
    }

    /// <summary>
    /// Predicts an N x N block into <paramref name="plane"/> at (<paramref name="x"/>, <paramref name="y"/>).
    /// <paramref name="line"/> holds the substituted reference samples and may be filtered in place.
    /// <paramref name="filterReferences"/> is cIdx == 0 or ChromaArrayType == 3 (8.4.4.2.3), and
    /// <paramref name="edgeFilters"/> is cIdx == 0 (the DC and pure horizontal / vertical boundary
    /// smoothing of 8.4.4.2.5 and 8.4.4.2.6). Returns whether the bilinear (strong) smoothing was used.
    /// </summary>
    public static bool Predict(
        Span<int> line, int log2N, int mode, bool filterReferences, bool edgeFilters, bool strongSmoothing, int bitDepth,
        ushort[] plane, int stride, int x, int y)
    {
        var n = 1 << log2N;
        var corner = 2 * n;

        var strong = false;
        if (filterReferences && mode != Dc && n != 4)
        {
            var distance = Math.Min(Math.Abs(mode - Vertical), Math.Abs(mode - Horizontal));
            var threshold = n switch { 8 => 7, 16 => 1, _ => 0 };
            if (distance > threshold) strong = Filter(line, n, strongSmoothing && n == 32, bitDepth);
        }

        var origin = y * stride + x;
        if (mode == Planar)
        {
            var topRight = line[corner + 1 + n];  // p[N][-1]
            var bottomLeft = line[corner - 1 - n]; // p[-1][N]
            for (var j = 0; j < n; j++)
            {
                var left = line[corner - 1 - j];
                var row = origin + j * stride;
                for (var i = 0; i < n; i++)
                {
                    var top = line[corner + 1 + i];
                    plane[row + i] = (ushort)(((n - 1 - i) * left + (i + 1) * topRight + (n - 1 - j) * top + (j + 1) * bottomLeft + n) >> (log2N + 1));
                }
            }
            return strong;
        }

        if (mode == Dc)
        {
            var sum = n;
            for (var i = 0; i < n; i++) sum += line[corner + 1 + i] + line[corner - 1 - i];
            var dc = sum >> (log2N + 1);
            for (var j = 0; j < n; j++) plane.AsSpan(origin + j * stride, n).Fill((ushort)dc);
            if (edgeFilters && n < 32)
            {
                plane[origin] = (ushort)((line[corner - 1] + 2 * dc + line[corner + 1] + 2) >> 2);
                for (var i = 1; i < n; i++) plane[origin + i] = (ushort)((line[corner + 1 + i] + 3 * dc + 2) >> 2);
                for (var j = 1; j < n; j++) plane[origin + j * stride] = (ushort)((line[corner - 1 - j] + 3 * dc + 2) >> 2);
            }
            return strong;
        }

        Angular(line, n, mode, edgeFilters, bitDepth, plane, stride, origin);
        return strong;
    }

    /// <summary>8.4.4.2.3: the [1 2 1] smoothing, or for a 32x32 luma block with flat edges the bilinear one.</summary>
    private static bool Filter(Span<int> line, int n, bool strongAllowed, int bitDepth)
    {
        var corner = 2 * n;
        var last = 4 * n;
        if (strongAllowed)
        {
            var threshold = 1 << (bitDepth - 5);
            // p[-1][-1] + p[2N-1][-1] - 2 p[N-1][-1], and the same down the left column.
            var flatTop = Math.Abs(line[corner] + line[last] - 2 * line[corner + n]) < threshold;
            var flatLeft = Math.Abs(line[corner] + line[0] - 2 * line[corner - n]) < threshold;
            if (flatTop && flatLeft)
            {
                int c = line[corner], bottom = line[0], right = line[last];
                for (var i = 0; i < 63; i++)
                {
                    line[corner - 1 - i] = ((63 - i) * c + (i + 1) * bottom + 32) >> 6;
                    line[corner + 1 + i] = ((63 - i) * c + (i + 1) * right + 32) >> 6;
                }
                return true;
            }
        }

        var previous = line[0];
        for (var i = 1; i < last; i++)
        {
            var current = line[i];
            line[i] = (previous + 2 * current + line[i + 1] + 2) >> 2;
            previous = current;
        }
        return false;
    }

    /// <summary>8.4.4.2.6: the 33 angular modes.</summary>
    private static void Angular(Span<int> line, int n, int mode, bool edgeFilters, int bitDepth, ushort[] plane, int stride, int origin)
    {
        var corner = 2 * n;
        var angle = Angle[mode];
        var max = (1 << bitDepth) - 1;

        // ref[] runs from -N to 2N; refBase is the index of ref[0].
        Span<int> reference = stackalloc int[3 * n + 1];
        var refBase = n;
        var vertical = mode >= 18;

        // Main side: the top row for vertical modes, the left column for horizontal ones, starting at the corner.
        for (var k = 0; k <= n; k++)
            reference[refBase + k] = vertical ? line[corner + k] : line[corner - k];

        if (angle < 0)
        {
            var inverse = InverseAngle[mode - 11];
            var lowest = (n * angle) >> 5;
            if (lowest < -1)
            {
                // Project the other side onto the extension of the main one.
                for (var k = lowest; k <= -1; k++)
                {
                    var t = -1 + ((k * inverse + 128) >> 8); // index along the other side, -1 being the corner
                    reference[refBase + k] = vertical ? line[corner - 1 - t] : line[corner + 1 + t];
                }
            }
        }
        else
        {
            for (var k = n + 1; k <= 2 * n; k++)
                reference[refBase + k] = vertical ? line[corner + k] : line[corner - k];
        }

        for (var j = 0; j < n; j++)
        {
            for (var i = 0; i < n; i++)
            {
                // Vertical modes step along x per row y; horizontal modes are the transpose.
                var (along, across) = vertical ? (i, j) : (j, i);
                var position = (across + 1) * angle;
                var index = position >> 5;
                var fraction = position & 31;
                var a = reference[refBase + along + index + 1];
                var value = fraction == 0 ? a : ((32 - fraction) * a + fraction * reference[refBase + along + index + 2] + 16) >> 5;
                plane[origin + j * stride + i] = (ushort)value;
            }
        }

        if (!edgeFilters || n >= 32) return;
        if (mode == Vertical)
        {
            // predSamples[0][y] = Clip1(p[0][-1] + ((p[-1][y] - p[-1][-1]) >> 1))
            for (var j = 0; j < n; j++)
                plane[origin + j * stride] = (ushort)Math.Clamp(line[corner + 1] + ((line[corner - 1 - j] - line[corner]) >> 1), 0, max);
        }
        else if (mode == Horizontal)
        {
            // predSamples[x][0] = Clip1(p[-1][0] + ((p[x][-1] - p[-1][-1]) >> 1))
            for (var i = 0; i < n; i++)
                plane[origin + i] = (ushort)Math.Clamp(line[corner - 1] + ((line[corner + 1 + i] - line[corner]) >> 1), 0, max);
        }
    }
}
