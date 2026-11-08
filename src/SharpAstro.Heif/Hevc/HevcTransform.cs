using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    /// (<c>[y * N + x]</c>) into residuals in the same layout. <see cref="InverseVector"/> wherever the
    /// hardware has 128-bit integer vectors (SSE2, NEON: essentially always), else <see cref="InverseScalar"/>;
    /// the two are equal bit for bit (<c>HevcTransformEquivalenceTests</c>).
    /// </summary>
    public static void Inverse(ReadOnlySpan<int> coefficients, Span<int> residual, int log2N, bool dst, int bitDepth)
    {
        if (Vector128.IsHardwareAccelerated) InverseVector(coefficients, residual, log2N, dst, bitDepth);
        else InverseScalar(coefficients, residual, log2N, dst, bitDepth);
    }

    /// <summary>
    /// The inverse transform as 8.6.4.2 writes it, one output sample at a time: the reference the vector form
    /// is held to.
    /// </summary>
    internal static void InverseScalar(ReadOnlySpan<int> coefficients, Span<int> residual, int log2N, bool dst, int bitDepth)
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

    /// <summary>
    /// The same sums, four outputs at a time in <see cref="Vector128{T}"/> int lanes, over only the part of the
    /// block that can be non-zero. Both stages are a row of inputs times one basis value, added into a row of
    /// accumulators, so every lane does exactly the scalar form's integer arithmetic (in another order, which
    /// integer addition does not notice: no sum here comes near 2^31, as coefficients and intermediates are
    /// 16-bit and the basis below 91 in magnitude). Rows of coefficients below the last non-zero one, and
    /// column groups right of the last non-zero column, contribute nothing and are not visited; a stage 1
    /// column group with no coefficient is zero in the intermediate, exactly as <c>(0 + 64) &gt;&gt; 7</c> is.
    /// </summary>
    internal static void InverseVector(ReadOnlySpan<int> coefficients, Span<int> residual, int log2N, bool dst, int bitDepth)
    {
        var n = 1 << log2N;
        var size = n * n;
        if (coefficients.Length < size || residual.Length < size) throw new ArgumentException("The block is larger than its buffers.");
        ReadOnlySpan<int> basis = dst ? Dst : Dct[log2N - 2];

        // The last row with a non-zero coefficient, and the last column group of four with one.
        int lastRow = -1, lastGroup = -1;
        ref var c = ref MemoryMarshal.GetReference(coefficients);
        for (var k = 0; k < n; k++)
        {
            for (var g = n / 4 - 1; g > lastGroup; g--)
            {
                if (Vector128.LoadUnsafe(ref c, (nuint)(k * n + 4 * g)) != Vector128<int>.Zero)
                {
                    lastGroup = g;
                    break;
                }
            }
            if (lastGroup >= 0 && lastRow < k && RowHasNonZero(ref c, k * n, lastGroup)) lastRow = k;
        }
        if (lastRow < 0)
        {
            // (0 + 64) >> 7 is 0, and (0 + round) >> shift is 0: an all-zero block has an all-zero residual.
            residual[..size].Clear();
            return;
        }

        Span<int> intermediate = stackalloc int[size];
        ref var b = ref MemoryMarshal.GetReference(basis);
        ref var m = ref MemoryMarshal.GetReference(intermediate);
        var groups = lastGroup + 1;
        var low = Vector128.Create((int)short.MinValue);
        var high = Vector128.Create((int)short.MaxValue);
        var r64 = Vector128.Create(64);

        // Stage 1: intermediate[y][x] = clip16((sum over k of c[k][x] * basis[k][y] + 64) >> 7).
        for (var y = 0; y < n; y++)
        {
            for (var g = 0; g < groups; g++)
            {
                var acc = Vector128<int>.Zero;
                for (var k = 0; k <= lastRow; k++)
                    acc += Vector128.LoadUnsafe(ref c, (nuint)(k * n + 4 * g)) * Vector128.Create(Unsafe.Add(ref b, k * n + y));
                acc = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(acc + r64, 7), low), high);
                acc.StoreUnsafe(ref m, (nuint)(y * n + 4 * g));
            }
            // Column groups with no coefficient stay zero; stage 2 never reads past 4 * groups.
        }

        // Stage 2: residual[y][x] = (sum over k of intermediate[y][k] * basis[k][x] + round) >> (20 - bitDepth).
        var shift = 20 - bitDepth;
        var round = Vector128.Create(1 << (shift - 1));
        var width = 4 * groups;
        ref var r = ref MemoryMarshal.GetReference(residual);
        Span<Vector128<int>> row = stackalloc Vector128<int>[8];
        var outGroups = n / 4;
        for (var y = 0; y < n; y++)
        {
            row[..outGroups].Clear();
            for (var k = 0; k < width; k++)
            {
                var v = Unsafe.Add(ref m, y * n + k);
                if (v == 0) continue;
                var s = Vector128.Create(v);
                for (var g = 0; g < outGroups; g++)
                    row[g] += s * Vector128.LoadUnsafe(ref b, (nuint)(k * n + 4 * g));
            }
            for (var g = 0; g < outGroups; g++)
                Vector128.ShiftRightArithmetic(row[g] + round, shift).StoreUnsafe(ref r, (nuint)(y * n + 4 * g));
        }
    }

    private static bool RowHasNonZero(ref int c, int start, int lastGroup)
    {
        for (var g = 0; g <= lastGroup; g++)
            if (Vector128.LoadUnsafe(ref c, (nuint)(start + 4 * g)) != Vector128<int>.Zero) return true;
        return false;
    }
}
