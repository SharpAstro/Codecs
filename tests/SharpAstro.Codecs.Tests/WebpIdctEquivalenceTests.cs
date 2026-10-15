using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class WebpIdctEquivalenceTests
{
    [Fact]
    public void DcOnlyFastPathMatchesGeneralTransform()
    {
        var random = new Random(0x6386);
        Span<int> coefficients = stackalloc int[16];
        short[] fast = new short[24];
        short[] general = new short[24];
        int[] dcValues = [-32768, -32767, -4096, -1, 0, 1, 4095, 32766, 32767];

        foreach (int dc in dcValues.Concat(Enumerable.Range(0, 256).Select(_ => random.Next(short.MinValue, short.MaxValue + 1))))
        {
            coefficients.Clear();
            coefficients[0] = dc;
            fast.AsSpan().Fill(short.MinValue);
            general.AsSpan().Fill(short.MinValue);

            Vp8Decoder.InverseDct(coefficients, fast.AsSpan(4, 16), hasAcCoefficients: false);
            Vp8Decoder.InverseDctGeneral(coefficients, general.AsSpan(4, 16));

            Assert.Equal(general, fast);
        }
    }
}
