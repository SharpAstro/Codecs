namespace SharpAstro.Heif.Hevc;

/// <summary>
/// Scaling (dequantisation, H.265 8.6.2-8.6.3) and the inverse transforms (8.6.4): the 4x4 DST for intra
/// luma and the DCT at 4, 8, 16 and 32 points, all in integers so every conforming decoder computes the
/// same residual bit for bit.
/// </summary>
internal static class HevcTransform
{
    private static ReadOnlySpan<byte> LevelScale => [40, 45, 51, 57, 64, 72];

    /// <summary>The 4x4 DST basis (8.6.4.2): row k is basis function k at positions 0..3.</summary>
    private static readonly int[] Dst =
    [
        29, 55, 74, 84,
        74, 74, 0, -74,
        84, -29, -74, 55,
        55, -84, 74, -29,
    ];

    /// <summary>
    /// The DCT basis for N = 4, 8, 16, 32, indexed <c>[log2N - 2][k * N + n]</c>: basis function k at position n.
    /// </summary>
    private static readonly int[][] Dct = [BuildDct(4), BuildDct(8), BuildDct(16), BuildDct(32)];

    /// <summary>
    /// The integer DCT of 8.6.4.2. Each entry of the 32-point matrix is a fixed integer approximation of
    /// 64 sqrt(2) cos(pi a / 64) with a = (2n + 1) k mod 128; the integers are the spec's, not rounded cosines
    /// (a = 21 gives 46 where rounding would give 47), so they come from this table of the 33 distinct
    /// magnitudes, a = 0..32. Row 0 is 64 throughout.
    /// </summary>
    private static ReadOnlySpan<byte> CosineMagnitudes =>
    [
        64, 90, 90, 90, 89, 88, 87, 85, 83, 82, 80, 78, 75, 73, 70, 67,
        64, 61, 57, 54, 50, 46, 43, 38, 36, 31, 25, 22, 18, 13, 9, 4, 0,
    ];

    private static int[] BuildDct(int n)
    {
        var matrix = new int[n * n];
        var step = 32 / n;
        for (var k = 0; k < n; k++)
        {
            for (var position = 0; position < n; position++)
            {
                if (k == 0)
                {
                    matrix[position] = 64;
                    continue;
                }
                var a = (2 * position + 1) * k * step % 128;
                if (a > 64) a = 128 - a;               // cos is even about pi
                matrix[k * n + position] = a > 32 ? -CosineMagnitudes[64 - a] : CosineMagnitudes[a];
            }
        }
        return matrix;
    }

    /// <summary>
    /// 8.6.3: scales the transform coefficient levels in place. <paramref name="factors"/> is the
    /// ScalingFactor matrix, or null for the flat 16 used without scaling lists.
    /// </summary>
    public static void Scale(Span<int> coefficients, int log2N, int qp, int bitDepth, int[]? factors)
    {
        var shift = bitDepth + log2N - 5;
        var round = 1L << (shift - 1);
        long scale = (long)LevelScale[qp % 6] << (qp / 6);
        for (var i = 0; i < coefficients.Length; i++)
        {
            var level = coefficients[i];
            if (level == 0) continue;
            var m = factors?[i] ?? 16;
            var value = (level * m * scale + round) >> shift;
            coefficients[i] = (int)Math.Clamp(value, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// 8.6.4.2: the two-stage inverse transform, columns then rows, of the scaled coefficients
    /// (<c>[y * N + x]</c>) into residuals in the same layout.
    /// </summary>
    public static void Inverse(Span<int> coefficients, Span<int> residual, int log2N, bool dst, int bitDepth)
    {
        var n = 1 << log2N;
        var basis = dst ? Dst : Dct[log2N - 2];
        Span<int> intermediate = stackalloc int[n * n];

        // Stage 1: each column, over vertical frequency k, then (e + 64) >> 7 clipped to 16 bits.
        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++)
            {
                var sum = 0;
                for (var k = 0; k < n; k++)
                {
                    var c = coefficients[k * n + x];
                    if (c != 0) sum += c * basis[k * n + y];
                }
                intermediate[y * n + x] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        // Stage 2: each row, then the final shift of 20 - bitDepth (8.6.2).
        var shift = 20 - bitDepth;
        var round = 1 << (shift - 1);
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var sum = 0;
                for (var k = 0; k < n; k++)
                {
                    var g = intermediate[y * n + k];
                    if (g != 0) sum += g * basis[k * n + x];
                }
                residual[y * n + x] = (sum + round) >> shift;
            }
        }
    }
}
