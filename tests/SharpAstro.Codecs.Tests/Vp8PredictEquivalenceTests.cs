using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class Vp8PredictEquivalenceTests
{
    [Fact]
    public void VerticalPredictVectorMatchesScalar()
    {
        var random = new Random(0xA11);
        foreach (int size in new[] { 8, 16 })
        {
            int stride = size + 8;
            int height = size + 8;
            for (int y = 1; y <= height - size; y += size)
            {
                byte[] plane = new byte[stride * height];
                FillPredictPlane(plane, random);
                byte[] scalar = (byte[])plane.Clone();
                byte[] vector = (byte[])plane.Clone();
                const int x = 4;

                Vp8Decoder.PredictLargeBlockScalar(scalar, stride, x, y, size, 1);
                Vp8Decoder.PredictVerticalVector(vector, stride, x, y, size);

                Assert.Equal(scalar, vector);
            }
        }
    }

    [Fact]
    public void TrueMotionPredictVectorMatchesScalar()
    {
        var random = new Random(0xB22);
        foreach (int size in new[] { 8, 16 })
        {
            int stride = size + 8;
            int height = size + 8;
            for (int y = 1; y <= height - size; y += size)
            {
                byte[] plane = new byte[stride * height];
                FillPredictPlane(plane, random);
                byte[] scalar = (byte[])plane.Clone();
                byte[] vector = (byte[])plane.Clone();
                const int x = 4;

                Vp8Decoder.PredictLargeBlockScalar(scalar, stride, x, y, size, 3);
                Vp8Decoder.PredictTrueMotionVector(vector, stride, x, y, size);

                Assert.Equal(scalar, vector);
            }
        }
    }

    [Fact]
    public void DcPredictVectorMatchesScalarForAllBorderCombinations()
    {
        var random = new Random(0xC33);
        foreach (int size in new[] { 8, 16 })
        {
            int stride = size + 8;
            int height = size + 8;
            // y/x == 1 exercises the no-above / no-left DC branches.
            foreach (int y in new[] { 1, 4 })
            {
                foreach (int x in new[] { 1, 4 })
                {
                    for (int pattern = 0; pattern < 3; pattern++)
                    {
                        byte[] plane = new byte[stride * height];
                        FillPredictPlane(plane, random, pattern);
                        byte[] scalar = (byte[])plane.Clone();
                        byte[] vector = (byte[])plane.Clone();

                        Vp8Decoder.PredictLargeBlockScalar(scalar, stride, x, y, size, 0);
                        Vp8Decoder.PredictDcVector(vector, stride, x, y, size);

                        Assert.Equal(scalar, vector);
                    }
                }
            }
        }
    }

    private static void FillPredictPlane(byte[] plane, Random random, int pattern = 0)
    {
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = pattern switch
            {
                1 => (byte)(i % 255),
                2 => (byte)(255 - i % 128),
                _ => (byte)random.Next(256)
            };
        }
    }
}
