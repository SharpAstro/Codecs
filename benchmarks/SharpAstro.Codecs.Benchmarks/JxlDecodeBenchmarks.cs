using BenchmarkDotNet.Attributes;
using SharpAstro.Jxl;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// JPEG XL through the public API, split by frame encoding so the DCT's share is bracketed
/// rather than assumed.
///
/// <para><b>Lossy VarDCT</b> runs <c>JxlDct.Dct2d</c> once per 8×8 block per channel;
/// <b>lossless Modular</b> never touches it. The gap between them bounds what vectorizing the
/// DCT could buy — a transform that is a small share of decode cannot be worth much however
/// fast it gets, which is the trap that already caught the colour-convert kernel in
/// SharpAstro.Jpeg.</para>
///
/// <para><see cref="MemoryDiagnoser"/> earns its place here: <c>Dct2d</c> allocates its
/// scratch, column and column-scratch buffers on every call, and it is called per block per
/// channel, so that churn should show up in the Allocated column.</para>
/// </summary>
[MemoryDiagnoser]
public class JxlDecodeBenchmarks
{
    // VarDCT needs dimensions that are multiples of 8; this keeps Modular single-group.
    private const int Width = 256;
    private const int Height = 256;

    private readonly int[] _r = new int[Width * Height];
    private readonly int[] _g = new int[Width * Height];
    private readonly int[] _b = new int[Width * Height];
    private byte[] _lossy = [];
    private byte[] _lossless = [];

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(7);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = y * Width + x;
                // Gradients plus mild noise. A flat image would leave almost every block
                // DC-only and make the transform look cheaper than it is on real content.
                _r[i] = Math.Clamp(x * 255 / (Width - 1) + rnd.Next(-8, 9), 0, 255);
                _g[i] = Math.Clamp(y * 255 / (Height - 1) + rnd.Next(-8, 9), 0, 255);
                _b[i] = Math.Clamp((int)(128 + 90 * Math.Sin(x * 0.05) * Math.Cos(y * 0.06)) + rnd.Next(-8, 9), 0, 255);
            }
        }

        _lossy = JxlImageCodec.EncodeRgb24Lossy(_r, _g, _b, Width, Height);
        _lossless = JxlImageCodec.EncodeRgb24(_r, _g, _b, Width, Height);
    }

    [Benchmark(Baseline = true, Description = "VarDCT lossy decode")]
    public JxlImage DecodeLossy() => JxlImageCodec.Decode(_lossy);

    [Benchmark(Description = "Modular lossless decode")]
    public JxlImage DecodeLossless() => JxlImageCodec.Decode(_lossless);

    [Benchmark(Description = "VarDCT lossy encode")]
    public int EncodeLossy() => JxlImageCodec.EncodeRgb24Lossy(_r, _g, _b, Width, Height).Length;
}
