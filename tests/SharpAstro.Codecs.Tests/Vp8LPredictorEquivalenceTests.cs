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
}
