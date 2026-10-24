using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class Vp8LPredictorEquivalenceTests
{
    [Fact]
    public void VectorPredictorMatchesScalarForAllModesAndEdges()
    {
        var random = new Random(0x9649);
        foreach (int bits in new[] { 2, 3, 5 })
        {
            foreach (int width in new[] { 1, 3, 7, 31, 33, 64, 65 })
            {
                int height = 7;
                int block = 1 << bits;
                int blockWidth = (width + block - 1) >> bits;
                int blockHeight = (height + block - 1) >> bits;
                var modeData = new int[blockWidth * blockHeight];
                for (int i = 0; i < modeData.Length; i++)
                {
                    modeData[i] = random.Next(14) << 8;
                }

                var transform = new Vp8LDecoder.Transform(
                    Type: 0, Width: width, Height: height, Bits: bits,
                    Data: modeData, Table: null, TableLength: 0);

                int[] source = new int[width * height];
                for (int i = 0; i < source.Length; i++)
                {
                    source[i] = random.Next();
                }

                int[] scalar = (int[])source.Clone();
                int[] vector = (int[])source.Clone();
                Vp8LDecoder.ApplyPredictorScalar(scalar, transform);
                Vp8LDecoder.ApplyPredictorVector(vector, transform);
                Assert.Equal(scalar, vector);
            }
        }
    }

    [Fact]
    public void VectorColorTransformMatchesScalarForAllCtesAndEdges()
    {
        var random = new Random(0x9649);
        foreach (int bits in new[] { 2, 3, 5 })
        {
            foreach (int width in new[] { 1, 3, 7, 9, 31, 33 })
            {
                int height = 5;
                int block = 1 << bits;
                int blockWidth = (width + block - 1) >> bits;
                int blockHeight = (height + block - 1) >> bits;
                var cteData = new int[blockWidth * blockHeight];
                for (int i = 0; i < cteData.Length; i++)
                {
                    cteData[i] = random.Next();
                }

                var transform = new Vp8LDecoder.Transform(
                    Type: 1, Width: width, Height: height, Bits: bits,
                    Data: cteData, Table: null, TableLength: 0);

                int[] source = new int[width * height];
                for (int i = 0; i < source.Length; i++)
                {
                    source[i] = random.Next();
                }

                int[] scalar = (int[])source.Clone();
                int[] vector = (int[])source.Clone();
                Vp8LDecoder.ApplyColorTransformScalar(scalar, transform);
                Vp8LDecoder.ApplyColorTransformVector(vector, transform);
                Assert.Equal(scalar, vector);
            }
        }
    }

    [Fact]
    public void VectorSubtractGreenMatchesScalarForAllLengths()
    {
        var random = new Random(0x9649);
        for (int width = 1; width <= 37; width++)
        {
            int height = 3;
            var transform = new Vp8LDecoder.Transform(
                Type: 2, Width: width, Height: height, Bits: 0,
                Data: null, Table: null, TableLength: 0);

            int[] source = new int[width * height];
            for (int i = 0; i < source.Length; i++)
            {
                source[i] = random.Next();
            }

            int[] scalar = (int[])source.Clone();
            int[] vector = (int[])source.Clone();
            Vp8LDecoder.ApplySubtractGreenScalar(scalar, transform);
            Vp8LDecoder.ApplySubtractGreenVector(vector, transform);
            Assert.Equal(scalar, vector);
        }
    }

    [Fact]
    public void VectorToRgbaMatchesScalarForAllWidthsAndStrides()
    {
        var random = new Random(0x9649);
        foreach (int width in new[] { 1, 3, 4, 7, 8, 9, 15, 31, 33 })
        {
            foreach (int extraStride in new[] { 0, 3 })
            {
                int height = 4;
                int stride = width * 4 + extraStride;
                int[] argb = new int[width * height];
                for (int i = 0; i < argb.Length; i++)
                {
                    argb[i] = random.Next();
                }

                var scalar = new byte[height * stride];
                var vector = new byte[height * stride];
                Vp8LDecoder.ToRgbaScalar(argb, scalar, width, height, stride);
                Vp8LDecoder.ToRgbaVector(argb, vector, width, height, stride);
                Assert.Equal(scalar, vector);
            }
        }
    }
}
