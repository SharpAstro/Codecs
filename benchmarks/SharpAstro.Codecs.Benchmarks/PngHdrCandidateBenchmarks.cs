using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Png;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// The vector kernels the PNG float-fill review tried, kept here so the numbers in CHANGELOG.md can be
/// reproduced. All portable <see cref="Vector128"/>; no architecture intrinsics.
/// </summary>
internal static class PngHdrCandidates
{
    // Vector float pow(x, y) = exp(y * ln x): the portable Vector128 transcendentals.
    public static Vector128<float> Pow(Vector128<float> x, float y) => Vector128.Exp(Vector128.Log(x) * Vector128.Create(y));

    // PQ EOTF on 4 signals at once in float (the reference computes in double). Candidate for "vector polynomial".
    public static Vector128<float> PqNits(Vector128<float> n)
    {
        n = Vector128.Min(Vector128.Max(n, Vector128<float>.Zero), Vector128.Create(1f));
        var p = Pow(n, 1f / 78.84375f);
        var num = Vector128.Max(p - Vector128.Create(0.8359375f), Vector128<float>.Zero);
        var den = Vector128.Create(18.8515625f) - Vector128.Create(18.6875f) * p;
        return Vector128.Create(10000f) * Pow(num / den, 1f / 0.1593017578125f);
    }

    // The HLG fill with only the OOTF gain (Ys^0.2) done four pixels at a time; the table lookups, the
    // channel multiplies and the stores stay scalar. 16-bit RGB only: this is a measurement, not a path.
    public static void FillHlgRgb16VectorGain(PngImage png, ColorEncoding enc, float[] dst, float[] scene)
    {
        int w = png.Width, h = png.Height;
        PngHdr.BuildHlgSceneLightTable(16, enc.FullRange, scene);
        var wts = LumaWeights.For(enc.Primaries);
        var px = png.Pixels;
        var wr = Vector128.Create(wts.R);
        var wg = Vector128.Create(wts.G);
        var wb = Vector128.Create(wts.B);
        var peak = Vector128.Create(PngHdr.HlgNominalPeakNits);
        Span<float> gains = stackalloc float[4];
        Span<float> er = stackalloc float[4], eg = stackalloc float[4], eb = stackalloc float[4];
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 6;
            int o = y * w * 4;
            for (int x = 0; x < w; x += 4)
            {
                int n = Math.Min(4, w - x);
                er.Clear(); eg.Clear(); eb.Clear();
                for (int i = 0; i < n; i++)
                {
                    int s = row + (x + i) * 6;
                    er[i] = scene[BinaryPrimitives.ReadUInt16BigEndian(px.AsSpan(s, 2))];
                    eg[i] = scene[BinaryPrimitives.ReadUInt16BigEndian(px.AsSpan(s + 2, 2))];
                    eb[i] = scene[BinaryPrimitives.ReadUInt16BigEndian(px.AsSpan(s + 4, 2))];
                }
                var vr = Vector128.Create(er[0], er[1], er[2], er[3]);
                var vg = Vector128.Create(eg[0], eg[1], eg[2], eg[3]);
                var vb = Vector128.Create(eb[0], eb[1], eb[2], eb[3]);
                var ys = wr * vr + wg * vg + wb * vb;
                (peak * Pow(ys, 0.2f)).CopyTo(gains);
                for (int i = 0; i < n; i++)
                {
                    int d = o + (x + i) * 4;
                    dst[d] = gains[i] * er[i] / PngHdr.ReferenceWhiteNits;
                    dst[d + 1] = gains[i] * eg[i] / PngHdr.ReferenceWhiteNits;
                    dst[d + 2] = gains[i] * eb[i] / PngHdr.ReferenceWhiteNits;
                    dst[d + 3] = 1f;
                }
            }
        }
    }

    // The shipped RGB16 table fill with each pixel stored as one Vector128 (r, g, b, 1) instead of four scalar stores.
    public static void FillRgb16VectorStore(PngImage png, ColorEncoding enc, float[] dst, float[] table)
    {
        int w = png.Width, h = png.Height;
        PngHdr.BuildLinearTable(enc.Transfer, enc.Primaries, 16, enc.FullRange, table);
        ref float t = ref MemoryMarshal.GetArrayDataReference(table);
        for (int y = 0; y < h; y++)
        {
            ref byte p = ref MemoryMarshal.GetReference(png.Pixels.AsSpan(y * w * 6, w * 6));
            ref float o = ref MemoryMarshal.GetReference(dst.AsSpan(y * w * 4, w * 4));
            for (int x = 0; x < w; x++)
            {
                float r = Unsafe.Add(ref t, Read(ref p, x * 3));
                float g = Unsafe.Add(ref t, Read(ref p, x * 3 + 1));
                float b = Unsafe.Add(ref t, Read(ref p, x * 3 + 2));
                Vector128.Create(r, g, b, 1f).StoreUnsafe(ref o, (nuint)(x * 4));
            }
        }
    }

    private static ushort Read(ref byte row, int i) =>
        BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref row, i * 2)));
}

/// <summary>
/// The candidates against what ships, on a decoded 1080p image into a preallocated destination, with the worst
/// relative error of each candidate printed at setup. (The candidates rebuild their table on every call, as the
/// shipped path does, so the comparison is like for like.)
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 6)]
public class PngHdrCandidateBenchmarks
{
    private PngImage _pq16 = null!, _hlg16 = null!;
    private ColorEncoding _pq16Enc = null!, _hlg16Enc = null!;
    private float[] _dst = [], _check = [], _table = [];

    [GlobalSetup]
    public void Setup()
    {
        _pq16 = PngReader.Decode(PngHdrInputs.PqRgb16(PngHdrInputs.Width, PngHdrInputs.Height));
        _pq16Enc = PngImageDecoder.MapColorEncoding(_pq16);
        _hlg16 = PngReader.Decode(PngHdrInputs.HlgRgb16(PngHdrInputs.Width, PngHdrInputs.Height));
        _hlg16Enc = PngImageDecoder.MapColorEncoding(_hlg16);
        _dst = new float[PngHdrInputs.Width * PngHdrInputs.Height * 4];
        _check = new float[_dst.Length];
        _table = new float[65536];

        PngFloatFill.FillReference(_hlg16, _hlg16Enc, _check);
        PngHdrCandidates.FillHlgRgb16VectorGain(_hlg16, _hlg16Enc, _dst, _table);
        Console.WriteLine($"// HLG vector gain: worst relative error vs the scalar reference = {WorstRelative(_dst, _check):E2}");

        PngFloatFill.FillReference(_pq16, _pq16Enc, _check);
        PngHdrCandidates.FillRgb16VectorStore(_pq16, _pq16Enc, _dst, _table);
        Console.WriteLine($"// PQ vector store: worst relative error vs the scalar reference = {WorstRelative(_dst, _check):E2}");
    }

    private static double WorstRelative(float[] a, float[] b)
    {
        double worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            worst = Math.Max(worst, Math.Abs((double)a[i] - b[i]) / Math.Max(Math.Abs((double)b[i]), 1e-30));
        }
        return worst;
    }

    [Benchmark(Baseline = true, Description = "HLG RGB16 shipped (scalar pow per pixel)")]
    public float[] HlgShipped() { PngFloatFill.Fill(_hlg16, _hlg16Enc, _dst); return _dst; }

    [Benchmark(Description = "HLG RGB16 vector gain candidate")]
    public float[] HlgVectorGain() { PngHdrCandidates.FillHlgRgb16VectorGain(_hlg16, _hlg16Enc, _dst, _table); return _dst; }

    [Benchmark(Description = "PQ RGB16 shipped (scalar stores)")]
    public float[] PqShipped() { PngFloatFill.Fill(_pq16, _pq16Enc, _dst); return _dst; }

    [Benchmark(Description = "PQ RGB16 Vector128 pixel store candidate")]
    public float[] PqVectorStore() { PngHdrCandidates.FillRgb16VectorStore(_pq16, _pq16Enc, _dst, _table); return _dst; }
}

/// <summary>
/// The PQ curve three ways over 2^20 signals: the scalar double curve the reference calls per sample, a float
/// <see cref="Vector128"/> evaluation of the same formula, and a 65536-entry table lookup per 16-bit code.
/// Table build is not in the lookup number; it costs one scalar evaluation per entry (65536 of them), which
/// the fill's break-even rule already charges.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 6)]
public class PngHdrCurveBenchmarks
{
    private const int N = 1 << 20;
    private float[] _signal = [], _out = [];
    private ushort[] _codes = [];
    private float[] _table = [];

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(3);
        _codes = new ushort[N];
        _signal = new float[N];
        _out = new float[N];
        for (int i = 0; i < N; i++)
        {
            _codes[i] = (ushort)rnd.Next(65536);
            _signal[i] = _codes[i] / 65535f;
        }
        _table = new float[65536];
        PngHdr.BuildLinearTable(TransferFunction.Pq, ColorPrimaries.BT2020, 16, true, _table);

        // Worst relative error of the vector form over the whole code range, against the scalar curve.
        double worst = 0;
        Span<float> four = stackalloc float[4];
        for (int c = 0; c < 65536; c += 4)
        {
            PngHdrCandidates.PqNits(Vector128.Create(c / 65535f, (c + 1) / 65535f, (c + 2) / 65535f, (c + 3) / 65535f)).CopyTo(four);
            for (int k = 0; k < 4; k++)
            {
                float exact = PngHdr.PqInverseEotf((c + k) / 65535f);
                if (exact > 0)
                    worst = Math.Max(worst, Math.Abs((double)four[k] - exact) / exact);
            }
        }
        Console.WriteLine($"// PQ float Vector128 curve: worst relative error vs the double scalar curve = {worst:E2}");
    }

    [Benchmark(Baseline = true, Description = "PQ scalar double curve, per sample")]
    public float[] Scalar()
    {
        for (int i = 0; i < N; i++)
            _out[i] = PngHdr.PqInverseEotf(_signal[i]) / PngHdr.ReferenceWhiteNits;
        return _out;
    }

    [Benchmark(Description = "PQ float Vector128 curve (exp/log), 4 per step")]
    public float[] Vector()
    {
        var white = Vector128.Create(PngHdr.ReferenceWhiteNits);
        for (int i = 0; i < N; i += 4)
            (PngHdrCandidates.PqNits(Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_signal), (nuint)i)) / white)
                .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_out), (nuint)i);
        return _out;
    }

    [Benchmark(Description = "PQ 65536-entry table lookup, per code")]
    public float[] Table()
    {
        ref float t = ref MemoryMarshal.GetArrayDataReference(_table);
        for (int i = 0; i < N; i++)
            _out[i] = Unsafe.Add(ref t, _codes[i]);
        return _out;
    }
}
