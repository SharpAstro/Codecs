using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpAstro.Jpeg;

/// <summary>
/// Chroma (and generally, any subsampled component) row upsamplers — 1:1 ports
/// of stb_image's <c>stbi__resample_row_*</c> kernels, which themselves match
/// libjpeg's "fancy" triangular (3/4–1/4) interpolation. Each call produces one
/// output-resolution row into <paramref name="dst"/> from one or two
/// plane-resolution rows.
/// </summary>
internal static class JpegResample
{
    public const int OneToOne = 0;
    public const int V2 = 1;
    public const int H2 = 2;
    public const int Hv2 = 3;
    public const int Generic = 4;

    internal static bool ForceScalar; // TEMPORARY: A/B switch for the benchmark, removed before commit

    /// <summary>
    /// Runs the selected kernel. <paramref name="inNear"/>/<paramref name="inFar"/>
    /// are row offsets into <paramref name="plane"/>; <paramref name="w"/> is the
    /// low-resolution sample count; <paramref name="hs"/> the horizontal expansion.
    /// </summary>
    public static void Row(int kind, Span<byte> dst, ReadOnlySpan<byte> plane, int inNear, int inFar, int w, int hs)
    {
        if (!ForceScalar && Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
            RowSimd(kind, dst, plane, inNear, inFar, w, hs);
        else
            RowScalar(kind, dst, plane, inNear, inFar, w, hs);
    }

    /// <summary>
    /// The stb transliteration, and the reference <see cref="RowSimd"/> is held byte-equal to. It is also the whole
    /// of <see cref="Row"/> where there is no Vector128 hardware.
    /// </summary>
    internal static void RowScalar(int kind, Span<byte> dst, ReadOnlySpan<byte> plane, int inNear, int inFar, int w, int hs)
    {
        switch (kind)
        {
            case OneToOne:
                plane.Slice(inNear, w).CopyTo(dst);
                break;

            case V2:
                // Vertical 2x: 3/4 near + 1/4 far, round-half-up.
                for (var i = 0; i < w; ++i)
                    dst[i] = (byte)((3 * plane[inNear + i] + plane[inFar + i] + 2) >> 2);
                break;

            case H2:
            {
                // Horizontal 2x: each input sample emits a (3a+b)/4 pair; only
                // the near row participates.
                var input = plane.Slice(inNear);
                if (w == 1)
                {
                    dst[0] = dst[1] = input[0];
                    break;
                }

                dst[0] = input[0];
                dst[1] = (byte)((input[0] * 3 + input[1] + 2) >> 2);
                int i;
                for (i = 1; i < w - 1; ++i)
                {
                    var n = 3 * input[i] + 2;
                    dst[i * 2 + 0] = (byte)((n + input[i - 1]) >> 2);
                    dst[i * 2 + 1] = (byte)((n + input[i + 1]) >> 2);
                }

                dst[i * 2 + 0] = (byte)((input[w - 2] * 3 + input[w - 1] + 2) >> 2);
                dst[i * 2 + 1] = input[w - 1];
                break;
            }

            case Hv2:
            {
                // 2x in both axes: vertical blend first (3 near + 1 far), then the
                // horizontal 3/4–1/4 pair from the running t0/t1 window.
                if (w == 1)
                {
                    dst[0] = dst[1] = (byte)((3 * plane[inNear] + plane[inFar] + 2) >> 2);
                    break;
                }

                var t1 = 3 * plane[inNear] + plane[inFar];
                dst[0] = (byte)((t1 + 2) >> 2);
                for (var i = 1; i < w; ++i)
                {
                    var t0 = t1;
                    t1 = 3 * plane[inNear + i] + plane[inFar + i];
                    dst[i * 2 - 1] = (byte)((3 * t0 + t1 + 8) >> 4);
                    dst[i * 2] = (byte)((3 * t1 + t0 + 8) >> 4);
                }

                dst[w * 2 - 1] = (byte)((t1 + 2) >> 2);
                break;
            }

            default:
                // Nearest-neighbour fallback for exotic sampling factors.
                for (var i = 0; i < w; ++i)
                    for (var j = 0; j < hs; ++j)
                        dst[i * hs + j] = plane[inNear + i];
                break;
        }
    }

    /// <summary>
    /// <see cref="V2"/>, <see cref="H2"/> and <see cref="Hv2"/> in 16-bit lanes of a <c>Vector128</c>, every other kind
    /// (and a row too narrow to fill a vector) through <see cref="RowScalar"/>. Derived from the scalar kernels above
    /// and held byte-equal to them by <c>JpegResampleEquivalenceTests</c>: the lanes widen, so no sum can overflow
    /// (the largest, Hv2's <c>3*t0 + t1 + 8</c> with <c>t</c> up to 1020, is 4088) and the rounding is the scalar's.
    /// The two edge samples keep their own scalar arithmetic: stb's right edge is not the mirror of its left.
    /// </summary>
    internal static void RowSimd(int kind, Span<byte> dst, ReadOnlySpan<byte> plane, int inNear, int inFar, int w, int hs)
    {
        // Bounds are checked once here, so the vector bodies can load and store unchecked.
        switch (kind)
        {
            case V2 when w >= 16:
                _ = plane.Slice(inNear, w);
                _ = plane.Slice(inFar, w);
                _ = dst.Slice(0, w);
                V2Simd(dst, plane, inNear, inFar, w);
                return;
            case H2 when w >= 18:
                _ = plane.Slice(inNear, w);
                _ = dst.Slice(0, 2 * w);
                H2Simd(dst, plane.Slice(inNear), w);
                return;
            case Hv2 when w >= 18:
                _ = plane.Slice(inNear, w);
                _ = plane.Slice(inFar, w);
                _ = dst.Slice(0, 2 * w);
                Hv2Simd(dst, plane, inNear, inFar, w);
                return;
            default:
                RowScalar(kind, dst, plane, inNear, inFar, w, hs);
                return;
        }
    }

    private static void V2Simd(Span<byte> dst, ReadOnlySpan<byte> plane, int inNear, int inFar, int w)
    {
        ref var d = ref MemoryMarshal.GetReference(dst);
        ref var p = ref MemoryMarshal.GetReference(plane);
        var three = Vector128.Create((ushort)3);
        var two = Vector128.Create((ushort)2);
        var i = 0;
        for (; i + 16 <= w; i += 16)
        {
            var n = Vector128.LoadUnsafe(ref p, (nuint)(inNear + i));
            var f = Vector128.LoadUnsafe(ref p, (nuint)(inFar + i));
            var lo = Vector128.ShiftRightLogical(Vector128.WidenLower(n) * three + Vector128.WidenLower(f) + two, 2);
            var hi = Vector128.ShiftRightLogical(Vector128.WidenUpper(n) * three + Vector128.WidenUpper(f) + two, 2);
            Vector128.Narrow(lo, hi).StoreUnsafe(ref d, (nuint)i);
        }

        for (; i < w; ++i)
            dst[i] = (byte)((3 * plane[inNear + i] + plane[inFar + i] + 2) >> 2);
    }

    private static void H2Simd(Span<byte> dst, ReadOnlySpan<byte> input, int w)
    {
        ref var d = ref MemoryMarshal.GetReference(dst);
        ref var p = ref MemoryMarshal.GetReference(input);
        var three = Vector128.Create((ushort)3);
        var two = Vector128.Create((ushort)2);

        dst[0] = input[0];
        dst[1] = (byte)((input[0] * 3 + input[1] + 2) >> 2);
        // Sample i emits (3*in[i] + in[i-1] + 2) >> 2 then (3*in[i] + in[i+1] + 2) >> 2: as one little-endian
        // 16-bit lane that is even | odd << 8, which is the interleave and needs no shuffle. The body covers
        // samples i..i+15, and loads in[i+16], so it runs while i+16 is still inside the row.
        var i = 1;
        for (; i + 17 <= w; i += 16)
        {
            var c = Vector128.LoadUnsafe(ref p, (nuint)i);
            var prev = Vector128.LoadUnsafe(ref p, (nuint)(i - 1));
            var next = Vector128.LoadUnsafe(ref p, (nuint)(i + 1));
            var cl = Vector128.WidenLower(c) * three + two;
            var ch = Vector128.WidenUpper(c) * three + two;
            var lo = Vector128.ShiftRightLogical(cl + Vector128.WidenLower(prev), 2)
                | Vector128.ShiftLeft(Vector128.ShiftRightLogical(cl + Vector128.WidenLower(next), 2), 8);
            var hi = Vector128.ShiftRightLogical(ch + Vector128.WidenUpper(prev), 2)
                | Vector128.ShiftLeft(Vector128.ShiftRightLogical(ch + Vector128.WidenUpper(next), 2), 8);
            lo.AsByte().StoreUnsafe(ref d, (nuint)(i * 2));
            hi.AsByte().StoreUnsafe(ref d, (nuint)(i * 2 + 16));
        }

        for (; i < w - 1; ++i)
        {
            var n = 3 * input[i] + 2;
            dst[i * 2 + 0] = (byte)((n + input[i - 1]) >> 2);
            dst[i * 2 + 1] = (byte)((n + input[i + 1]) >> 2);
        }

        dst[i * 2 + 0] = (byte)((input[w - 2] * 3 + input[w - 1] + 2) >> 2);
        dst[i * 2 + 1] = input[w - 1];
    }

    private static void Hv2Simd(Span<byte> dst, ReadOnlySpan<byte> plane, int inNear, int inFar, int w)
    {
        ref var d = ref MemoryMarshal.GetReference(dst);
        ref var p = ref MemoryMarshal.GetReference(plane);
        var three = Vector128.Create((ushort)3);
        var eight = Vector128.Create((ushort)8);

        // With t[k] = 3*near[k] + far[k], the scalar loop writes, for sample i, dst[2i] = (3*t[i] + t[i-1] + 8) >> 4
        // (i >= 1) and dst[2i+1] = (3*t[i] + t[i+1] + 8) >> 4 (i <= w-2). Only dst[0] and dst[2w-1] differ, and
        // unlike H2 the right edge here is the mirror of the interior formula, so the tail needs no special pair.
        static int T(ReadOnlySpan<byte> pl, int near, int far, int k) => 3 * pl[near + k] + pl[far + k];

        dst[0] = (byte)((T(plane, inNear, inFar, 0) + 2) >> 2);
        dst[1] = (byte)((3 * T(plane, inNear, inFar, 0) + T(plane, inNear, inFar, 1) + 8) >> 4);
        var i = 1;
        for (; i + 17 <= w; i += 16)
        {
            var nc = Vector128.LoadUnsafe(ref p, (nuint)(inNear + i));
            var np = Vector128.LoadUnsafe(ref p, (nuint)(inNear + i - 1));
            var nn = Vector128.LoadUnsafe(ref p, (nuint)(inNear + i + 1));
            var fc = Vector128.LoadUnsafe(ref p, (nuint)(inFar + i));
            var fp = Vector128.LoadUnsafe(ref p, (nuint)(inFar + i - 1));
            var fn = Vector128.LoadUnsafe(ref p, (nuint)(inFar + i + 1));

            var cl = Vector128.WidenLower(nc) * three + Vector128.WidenLower(fc);
            var ch = Vector128.WidenUpper(nc) * three + Vector128.WidenUpper(fc);
            var pl = Vector128.WidenLower(np) * three + Vector128.WidenLower(fp);
            var ph = Vector128.WidenUpper(np) * three + Vector128.WidenUpper(fp);
            var xl = Vector128.WidenLower(nn) * three + Vector128.WidenLower(fn);
            var xh = Vector128.WidenUpper(nn) * three + Vector128.WidenUpper(fn);

            var c3l = cl * three + eight;
            var c3h = ch * three + eight;
            var lo = Vector128.ShiftRightLogical(c3l + pl, 4) | Vector128.ShiftLeft(Vector128.ShiftRightLogical(c3l + xl, 4), 8);
            var hi = Vector128.ShiftRightLogical(c3h + ph, 4) | Vector128.ShiftLeft(Vector128.ShiftRightLogical(c3h + xh, 4), 8);
            lo.AsByte().StoreUnsafe(ref d, (nuint)(i * 2));
            hi.AsByte().StoreUnsafe(ref d, (nuint)(i * 2 + 16));
        }

        for (; i < w; ++i)
        {
            var t = T(plane, inNear, inFar, i);
            dst[i * 2] = (byte)((3 * t + T(plane, inNear, inFar, i - 1) + 8) >> 4);
            if (i < w - 1)
                dst[i * 2 + 1] = (byte)((3 * t + T(plane, inNear, inFar, i + 1) + 8) >> 4);
        }

        dst[w * 2 - 1] = (byte)((T(plane, inNear, inFar, w - 1) + 2) >> 2);
    }
}
