using SharpAstro.Heif.Hevc;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The vector inverse transform against the scalar one, byte for byte. The dispatch in
/// <see cref="HevcTransform.Inverse"/> means a machine only ever runs one of them through the decoder, so the
/// digests and oracle tests alone cannot see the other rot; this drives both directly. The cases are the ones
/// the vector form's shortcuts could get wrong: blocks that are dense, sparse in the low corner (the usual
/// shape), with a lone coefficient in the last row or column (the bounds of the region it visits), empty, and
/// at the 16-bit rails where the stage 1 clamp fires.
/// </summary>
public sealed class HevcTransformEquivalenceTests
{
    public static TheoryData<int, bool, int> Shapes()
    {
        var data = new TheoryData<int, bool, int>();
        foreach (var bitDepth in new[] { 8, 10, 12 })
        {
            data.Add(2, true, bitDepth);
            for (var log2N = 2; log2N <= 5; log2N++) data.Add(log2N, false, bitDepth);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Vector_EqualsScalar(int log2N, bool dst, int bitDepth)
    {
        var n = 1 << log2N;
        var random = new Random(log2N * 100 + bitDepth + (dst ? 7 : 0));
        foreach (var block in Blocks(n, random))
            Check(block, log2N, dst, bitDepth);
    }

    [Fact]
    public void EveryLoneCoefficient_GivesTheSameResidual()
    {
        // One non-zero coefficient at each position of each size: every (last row, last group) the bounds
        // search can find, at both signs.
        for (var log2N = 2; log2N <= 5; log2N++)
        {
            var n = 1 << log2N;
            for (var i = 0; i < n * n; i++)
            {
                foreach (var value in new[] { 1, -1, 300, short.MaxValue, short.MinValue })
                {
                    var block = new int[n * n];
                    block[i] = value;
                    Check(block, log2N, dst: false, 10);
                    if (n == 4) Check(block, log2N, dst: true, 10);
                }
            }
        }
    }

    [Fact]
    public void LargerBuffers_OnlyTheBlockIsWritten()
    {
        // The decoder hands over 32x32 buffers for every size; the transform must use the first N x N and
        // leave the rest alone.
        var coefficients = new int[32 * 32];
        coefficients[0] = 500;
        coefficients[5] = -20;
        var scalar = Enumerable.Repeat(12345, 32 * 32).ToArray();
        var vector = Enumerable.Repeat(12345, 32 * 32).ToArray();
        HevcTransform.InverseScalar(coefficients, scalar, 3, false, 10);
        HevcTransform.InverseVector(coefficients, vector, 3, false, 10);
        vector.ShouldBe(scalar);
        vector[64].ShouldBe(12345);
    }

    private static void Check(int[] block, int log2N, bool dst, int bitDepth)
    {
        var n = 1 << log2N;
        var scalar = new int[n * n];
        var vector = Enumerable.Repeat(-999, n * n).ToArray(); // so a sample the vector form forgets is seen
        HevcTransform.InverseScalar(block, scalar, log2N, dst, bitDepth);
        HevcTransform.InverseVector(block, vector, log2N, dst, bitDepth);
        vector.ShouldBe(scalar, $"{n}x{n} {(dst ? "DST" : "DCT")} at {bitDepth} bits");
    }

    private static IEnumerable<int[]> Blocks(int n, Random random)
    {
        int[] Make(Func<int, int, int> value)
        {
            var block = new int[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    block[y * n + x] = value(x, y);
            return block;
        }

        yield return new int[n * n];
        for (var i = 0; i < 20; i++)
        {
            yield return Make((_, _) => random.Next(short.MinValue, short.MaxValue + 1));
            yield return Make((_, _) => random.Next(-64, 65));
            // Sparse, energy in the low corner, as residuals usually are.
            var reach = random.Next(1, n + 1);
            yield return Make((x, y) => x < reach && y < reach && random.Next(3) == 0 ? random.Next(-2000, 2001) : 0);
            var row = random.Next(n);
            var column = random.Next(n);
            yield return Make((x, y) => (y == row && x == column) || (x == 0 && y == 0) ? random.Next(-5000, 5001) : 0);
        }
        // The rails: every coefficient at one extreme or the other, so stage 1's clamp fires.
        yield return Make((_, _) => short.MaxValue);
        yield return Make((_, _) => short.MinValue);
        yield return Make((x, y) => ((x + y) & 1) == 0 ? short.MaxValue : short.MinValue);
    }
}
