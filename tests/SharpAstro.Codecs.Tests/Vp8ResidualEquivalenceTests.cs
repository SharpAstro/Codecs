using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class Vp8ResidualEquivalenceTests
{
    [Fact]
    public void AddResidualVectorMatchesScalar()
    {
        const int stride = 20;
        var random = new Random(0xD44);
        foreach (int x in new[] { 1, 5 })
        {
            foreach (int y in new[] { 1, 3 })
            {
                for (int pattern = 0; pattern < 3; pattern++)
                {
                    byte[] plane = new byte[stride * 12];
                    short[] residual = new short[16];
                    for (int i = 0; i < plane.Length; i++)
                    {
                        plane[i] = (byte)random.Next(256);
                    }

                    for (int i = 0; i < residual.Length; i++)
                    {
                        residual[i] = pattern switch
                        {
                            1 => (short)(random.Next(2) == 0 ? -255 : 255),
                            2 => (short)(random.Next(512) - 256),
                            _ => (short)(random.Next(41) - 20)
                        };
                    }

                    byte[] scalar = (byte[])plane.Clone();
                    byte[] vector = (byte[])plane.Clone();
                    Vp8Decoder.AddResidualScalar(scalar, stride, x, y, residual);
                    Vp8Decoder.AddResidualVector(vector, stride, x, y, residual);
                    Assert.Equal(scalar, vector);
                }
            }
        }
    }

    [Fact]
    public void DequantizeVectorMatchesScalarAndHasAcFlag()
    {
        var random = new Random(0xE55);
        foreach (int dc in new[] { 4, 17, 120 })
        {
            foreach (int ac in new[] { 4, 31, 155 })
            {
                for (int pattern = 0; pattern < 4; pattern++)
                {
                    int[] coefficients = new int[16];
                    for (int i = 0; i < 16; i++)
                    {
                        coefficients[i] = pattern switch
                        {
                            0 => random.Next(2048) - 1024,
                            1 => random.Next(131072) - 65536,
                            2 => i == 0 ? random.Next(256) - 128 : 0,
                            _ => i < 4 ? random.Next(64) - 32 : 0
                        };
                    }

                    int[] scalar = (int[])coefficients.Clone();
                    int[] vector = (int[])coefficients.Clone();
                    bool hasAcScalar = Vp8Decoder.DequantizeScalar(scalar, dc, ac);
                    bool hasAcVector = Vp8Decoder.DequantizeVector(vector, dc, ac);
                    Assert.Equal(hasAcScalar, hasAcVector);
                    Assert.Equal(scalar, vector);
                }
            }
        }
    }
}
