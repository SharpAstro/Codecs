using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class Vp8LoopFilterEquivalenceTests
{
    [Fact]
    public void HorizontalVectorFilterMatchesScalarForAllVariantsAndThresholds()
    {
        const int stride = 40;
        const int x = 4;
        const int y = 8;
        const int length = 16;
        var random = new Random(0x6386);
        int[] levels = [0, 1, 14, 15, 39, 40, 63];
        int[] sharpnessValues = [0, 1, 4, 7];

        foreach (bool simple in new[] { false, true })
        {
            foreach (bool macroblockEdge in new[] { false, true })
            {
                foreach (int level in levels)
                {
                    foreach (int sharpness in sharpnessValues)
                    {
                        int interior = level;
                        if (sharpness != 0)
                        {
                            interior >>= sharpness > 4 ? 2 : 1;
                            interior = Math.Min(interior, 9 - sharpness);
                        }

                        interior = Math.Max(interior, 1);
                        int edge = 2 * (level + 2) + interior;
                        int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;

                        for (int pattern = 0; pattern < 4; pattern++)
                        {
                            byte[] plane = new byte[stride * 24];
                            FillPlane(plane, stride, y, pattern, random);
                            byte[] scalar = (byte[])plane.Clone();
                            byte[] vector = (byte[])plane.Clone();

                            Vp8Decoder.FilterHorizontalScalar(
                                scalar, stride, x, y, length, simple, macroblockEdge,
                                interior, edge, hev);
                            Vp8Decoder.FilterHorizontalVector128(
                                vector, stride, x, y, length, simple, macroblockEdge,
                                interior, edge, hev);

                            Assert.Equal(scalar, vector);
                        }
                    }
                }
            }
        }
    }

    private static void FillPlane(byte[] plane, int stride, int edgeRow, int pattern, Random random)
    {
        for (int row = 0; row < plane.Length / stride; row++)
        {
            for (int column = 0; column < stride; column++)
            {
                plane[row * stride + column] = pattern switch
                {
                    0 => (byte)random.Next(256),
                    1 => row < edgeRow ? (byte)(72 + random.Next(9)) : (byte)(80 + random.Next(9)),
                    2 => row < edgeRow ? (byte)0 : (byte)255,
                    _ => (byte)Math.Clamp(16 + row * 8 + random.Next(5), 0, 255)
                };
            }
        }
    }
}
