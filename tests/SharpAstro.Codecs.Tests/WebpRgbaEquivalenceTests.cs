using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class WebpRgbaEquivalenceTests
{
    [Fact]
    public void VectorKernelMatchesScalarForEveryYuvCombination()
    {
        Span<byte> y = stackalloc byte[16];
        Span<byte> u = stackalloc byte[16];
        Span<byte> v = stackalloc byte[16];
        Span<byte> scalar = stackalloc byte[16 * 4];
        Span<byte> vector = stackalloc byte[16 * 4];

        for (int yValue = 0; yValue < 256; yValue++)
        {
            y.Fill((byte)yValue);
            for (int uValue = 0; uValue < 256; uValue++)
            {
                u.Fill((byte)uValue);
                for (int firstV = 0; firstV < 256; firstV += 16)
                {
                    for (int column = 0; column < 16; column++)
                    {
                        v[column] = (byte)(firstV + column);
                    }

                    WebpDecoder.ConvertRowScalar(y, u, v, [], scalar);
                    WebpDecoder.ConvertRowVector128(y, u, v, [], vector);
                    if (!vector.SequenceEqual(scalar))
                    {
                        Assert.Fail(
                            $"YUV conversion differs at Y={yValue}, U={uValue}, V={firstV}..{firstV + 15}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void VectorAndScalarRowsMatchForOffsetsTailsAndExtremeSamples()
    {
        var random = new Random(0x6386);
        for (int width = 1; width <= 35; width++)
        {
            foreach (bool useAlpha in new[] { false, true })
            {
                byte[] y = new byte[width + 9];
                byte[] u = new byte[width + 9];
                byte[] v = new byte[width + 9];
                byte[] alpha = new byte[width + 9];
                int inputOffset = 4;
                for (int column = 0; column < width; column++)
                {
                    bool high = (column & 1) != 0;
                    y[inputOffset + column] = high ? byte.MaxValue : byte.MinValue;
                    u[inputOffset + column] = high ? byte.MinValue : byte.MaxValue;
                    v[inputOffset + column] = high ? byte.MaxValue : byte.MinValue;
                    alpha[inputOffset + column] = high ? (byte)31 : (byte)224;
                }

                AssertEquivalent(y, u, v, useAlpha ? alpha : [], inputOffset, width);

                for (int column = 0; column < width; column++)
                {
                    y[inputOffset + column] = (byte)random.Next(256);
                    u[inputOffset + column] = (byte)random.Next(256);
                    v[inputOffset + column] = (byte)random.Next(256);
                    alpha[inputOffset + column] = (byte)random.Next(256);
                }

                AssertEquivalent(y, u, v, useAlpha ? alpha : [], inputOffset, width);
            }
        }
    }

    private static void AssertEquivalent(
        byte[] y,
        byte[] u,
        byte[] v,
        byte[] alpha,
        int inputOffset,
        int width)
    {
        const int outputOffset = 7;
        var scalar = Enumerable.Repeat((byte)0xA5, outputOffset + width * 4 + 11).ToArray();
        var vector = (byte[])scalar.Clone();
        ReadOnlySpan<byte> alphaRow = alpha.Length == 0
            ? ReadOnlySpan<byte>.Empty
            : alpha.AsSpan(inputOffset, width);

        WebpDecoder.ConvertRowScalar(
            y.AsSpan(inputOffset, width),
            u.AsSpan(inputOffset, width),
            v.AsSpan(inputOffset, width),
            alphaRow,
            scalar.AsSpan(outputOffset, width * 4));
        WebpDecoder.ConvertRowVector128(
            y.AsSpan(inputOffset, width),
            u.AsSpan(inputOffset, width),
            v.AsSpan(inputOffset, width),
            alphaRow,
            vector.AsSpan(outputOffset, width * 4));

        Assert.Equal(scalar, vector);
    }
}
