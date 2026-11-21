// Portions of this file are ported from jxl-oxide (https://github.com/tirr-c/jxl-oxide), Copyright (c)
// Wonwoo Choi and the jxl-oxide contributors, used under the MIT licence; see THIRD-PARTY-NOTICES.md.

namespace SharpAstro.Jxl;

/// <summary>
/// The inverse varblock transforms of a VarDCT decode (ISO/IEC 18181-1 §I.9), ported from jxl-oxide's
/// jxl-render <c>vardct/generic/dct.rs</c> (<c>dct_2d</c> and the 1-D <c>dct</c>), <c>vardct/generic/transform.rs</c>
/// (DCT2, DCT4, Hornuss, DCT4x8, AFV) and <c>vardct/transform_common.rs</c> (the low-frequency coefficients of a
/// large block from the LF image, and <c>AFV_BASIS</c>). Every routine works in place on a block of a frame plane
/// given by its offset and stride, as jxl-oxide works on a <c>MutableSubgrid</c>.
/// </summary>
internal static class JxlVarDctInverse
{
    private const float Sqrt2 = 1.41421356237309504880f;

    // ---- 1-D DCT (jxl-oxide dct.rs `dct`, dct_common.rs `sec_half`) ----

    private static readonly float[] SecHalf4 = [0.541196100146197f, 1.3065629648763764f];
    private static readonly float[] SecHalf8 = [0.5097955791041592f, 0.6013448869350453f, 0.8999762231364156f, 2.5629154477415055f];
    private static readonly float[] SecHalf16 =
    [
        0.5024192861881557f, 0.5224986149396889f, 0.5669440348163577f, 0.6468217833599901f,
        0.7881546234512502f, 1.060677685990347f, 1.7224470982383342f, 5.101148618689155f,
    ];
    private static readonly float[] SecHalf32 =
    [
        0.5006029982351963f, 0.5054709598975436f, 0.5154473099226246f, 0.5310425910897841f,
        0.5531038960344445f, 0.5829349682061339f, 0.6225041230356648f, 0.6748083414550057f,
        0.7445362710022984f, 0.8393496454155268f, 0.9725682378619608f, 1.1694399334328847f,
        1.4841646163141662f, 2.057781009953411f, 3.407608418468719f, 10.190008123548033f,
    ];
    private static readonly float[] SecHalf64 = ComputeSecHalf(64);
    private static readonly float[] SecHalf128 = ComputeSecHalf(128);
    private static readonly float[] SecHalf256 = ComputeSecHalf(256);

    private static float[] ComputeSecHalf(int n)
    {
        var table = new float[n / 2];
        for (int k = 0; k < table.Length; k++)
        {
            float theta = (2 * k + 1) / (float)(2 * n) * MathF.PI;
            table[k] = 1f / MathF.Cos(theta) / 2f;
        }
        return table;
    }

    private static float[] SecHalf(int n) => n switch
    {
        4 => SecHalf4,
        8 => SecHalf8,
        16 => SecHalf16,
        32 => SecHalf32,
        64 => SecHalf64,
        128 => SecHalf128,
        256 => SecHalf256,
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    private static void Dct4(Span<float> io, bool forward)
    {
        const float sec0 = 0.5411961f, sec1 = 1.306563f;
        float i0 = io[0], i1 = io[1], i2 = io[2], i3 = io[3];
        if (forward)
        {
            float sum03 = i0 + i3, sum12 = i1 + i2;
            float tmp0 = (i0 - i3) * sec0, tmp1 = (i1 - i2) * sec1;
            float out0 = (tmp0 + tmp1) / 4f, out1 = (tmp0 - tmp1) / 4f;
            io[0] = (sum03 + sum12) / 4f;
            io[1] = out0 * Sqrt2 + out1;
            io[2] = (sum03 - sum12) / 4f;
            io[3] = out1;
        }
        else
        {
            float tmp0 = i1 * Sqrt2, tmp1 = i1 + i3;
            float out0 = (tmp0 + tmp1) * sec0, out1 = (tmp0 - tmp1) * sec1;
            float sum02 = i0 + i2, sub02 = i0 - i2;
            io[0] = sum02 + out0;
            io[1] = sub02 + out1;
            io[2] = sub02 - out1;
            io[3] = sum02 - out0;
        }
    }

    /// <summary>1-D DCT of a power-of-two length (forward: DC is the mean; inverse: its exact inverse).</summary>
    public static void Dct(Span<float> io, Span<float> scratch, bool forward)
    {
        int n = io.Length;
        if (n <= 1)
            return;
        if (n == 2)
        {
            float tmp0 = io[0] + io[1], tmp1 = io[0] - io[1];
            if (forward)
            {
                io[0] = tmp0 / 2f;
                io[1] = tmp1 / 2f;
            }
            else
            {
                io[0] = tmp0;
                io[1] = tmp1;
            }
            return;
        }
        if (n == 4)
        {
            Dct4(io, forward);
            return;
        }
        if (n == 8)
        {
            Dct8(io, forward);
            return;
        }

        int half = n / 2;
        Span<float> input0 = scratch[..half];
        Span<float> input1 = scratch.Slice(half, half);
        float[] sec = SecHalf(n);
        if (forward)
        {
            for (int idx = 0; idx < half; idx++)
            {
                input0[idx] = (io[idx] + io[n - idx - 1]) / 2f;
                input1[idx] = (io[idx] - io[n - idx - 1]) / 2f;
            }
            for (int idx = 0; idx < half; idx++)
                input1[idx] *= sec[idx];
            Dct(input0, io[..half], true);
            Dct(input1, io.Slice(half, half), true);
            input1[0] *= Sqrt2;
            for (int idx = 0; idx < half - 1; idx++)
                input1[idx] += input1[idx + 1];
            for (int idx = 0; idx < half; idx++)
                io[idx * 2] = input0[idx];
            for (int idx = 0; idx < half; idx++)
                io[idx * 2 + 1] = input1[idx];
        }
        else
        {
            for (int idx = 0; idx < half; idx++)
            {
                input0[idx] = io[idx * 2];
                input1[idx] = io[idx * 2 + 1];
            }
            for (int idx = 1; idx < half; idx++)
                input1[half - idx] += input1[half - idx - 1];
            input1[0] *= Sqrt2;
            Dct(input0, io[..half], false);
            Dct(input1, io.Slice(half, half), false);
            for (int idx = 0; idx < half; idx++)
                input1[idx] *= sec[idx];
            for (int idx = 0; idx < half; idx++)
            {
                io[idx] = scratch[idx] + scratch[idx + half];
                io[n - idx - 1] = scratch[idx] - scratch[idx + half];
            }
        }
    }

    private static void Dct8(Span<float> io, bool forward)
    {
        float[] sec = SecHalf8;
        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];
        if (forward)
        {
            a[0] = (io[0] + io[7]) / 2f;
            a[1] = (io[1] + io[6]) / 2f;
            a[2] = (io[2] + io[5]) / 2f;
            a[3] = (io[3] + io[4]) / 2f;
            b[0] = (io[0] - io[7]) * sec[0] / 2f;
            b[1] = (io[1] - io[6]) * sec[1] / 2f;
            b[2] = (io[2] - io[5]) * sec[2] / 2f;
            b[3] = (io[3] - io[4]) * sec[3] / 2f;
            Dct4(a, true);
            for (int idx = 0; idx < 4; idx++)
                io[idx * 2] = a[idx];
            Dct4(b, true);
            b[0] *= Sqrt2;
            for (int idx = 0; idx < 3; idx++)
                io[idx * 2 + 1] = b[idx] + b[idx + 1];
            io[7] = b[3];
        }
        else
        {
            a[0] = io[0]; a[1] = io[2]; a[2] = io[4]; a[3] = io[6];
            b[0] = io[1] * Sqrt2;
            b[1] = io[3] + io[1];
            b[2] = io[5] + io[3];
            b[3] = io[7] + io[5];
            Dct4(a, false);
            Dct4(b, false);
            for (int idx = 0; idx < 4; idx++)
            {
                float r = b[idx] * sec[idx];
                io[idx] = a[idx] + r;
                io[7 - idx] = a[idx] - r;
            }
        }
    }

    /// <summary>
    /// 2-D DCT of a <paramref name="width"/> x <paramref name="height"/> block (jxl-oxide <c>dct_2d</c>): the
    /// 1-D DCT along every row, then along every column. Coefficient (u, v) sits at column u, row v.
    /// </summary>
    public static void Dct2d(Span<float> plane, int offset, int stride, int width, int height, bool forward)
    {
        if (width * height <= 1)
            return;
        int max = Math.Max(width, height);
        Span<float> scratch = max <= 64 ? stackalloc float[64] : new float[max];
        scratch = scratch[..max];
        if (width > 1)
        {
            Span<float> sw = scratch[..width];
            for (int y = 0; y < height; y++)
                Dct(plane.Slice(offset + y * stride, width), sw, forward);
        }
        if (height > 1)
        {
            Span<float> col = max <= 64 ? stackalloc float[64] : new float[max];
            col = col[..height];
            Span<float> sh = scratch[..height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                    col[y] = plane[offset + y * stride + x];
                Dct(col, sh, forward);
                for (int y = 0; y < height; y++)
                    plane[offset + y * stride + x] = col[y];
            }
        }
    }

    // ---- scale_f (dct_common.rs) ----

    private static readonly float[] ScaleF =
    [
        1.0000000000000000f, 0.9996047255830407f, 0.9984194528776054f, 0.9964458326264695f,
        0.9936866130906366f, 0.9901456355893141f, 0.9858278282666936f, 0.9807391980963174f,
        0.9748868211368796f, 0.9682788310563117f, 0.9609244059440204f, 0.9528337534340876f,
        0.9440180941651672f, 0.9344896436056892f, 0.9242615922757944f, 0.9133480844001980f,
        0.9017641950288744f, 0.8895259056651056f, 0.8766500784429904f, 0.8631544288990163f,
        0.8490574973847023f, 0.8343786191696513f, 0.8191378932865928f, 0.8033561501721485f,
        0.7870549181591013f, 0.7702563888779096f, 0.7529833816270532f, 0.7352593067735488f,
        0.7171081282466044f, 0.6985543251889097f, 0.6796228528314652f, 0.6603391026591464f,
    ];

    /// <summary>
    /// Puts the LF of one varblock into its low-frequency coefficients and inverse-transforms it in place
    /// (jxl-oxide <c>transform_varblocks_inner</c> for one block). <paramref name="lf"/> is the LF plane
    /// (one sample per 8x8 block, row length <paramref name="lfStride"/>), at block (<paramref name="bx"/>,
    /// <paramref name="by"/>).
    /// </summary>
    public static void TransformVarblock(
        Span<float> plane, int offset, int stride, JxlVarDctTransform t,
        ReadOnlySpan<float> lf, int lfStride, int bx, int by)
    {
        (int bw, int bh) = t.DctSelectSize();
        if (bw * bh == 1)
        {
            plane[offset] = lf[by * lfStride + bx];
        }
        else
        {
            int logbw = System.Numerics.BitOperations.TrailingZeroCount(bw);
            int logbh = System.Numerics.BitOperations.TrailingZeroCount(bh);
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    plane[offset + y * stride + x] = lf[(by + y) * lfStride + bx + x];
            Dct2d(plane, offset, stride, bw, bh, forward: true);
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    plane[offset + y * stride + x] /= ScaleF[y << (5 - logbh)] * ScaleF[x << (5 - logbw)];
        }

        switch (t)
        {
            case JxlVarDctTransform.Dct2:
                AuxIdct2(plane, offset, stride, 2);
                AuxIdct2(plane, offset, stride, 4);
                AuxIdct2(plane, offset, stride, 8);
                break;
            case JxlVarDctTransform.Dct4:
                TransformDct4(plane, offset, stride);
                break;
            case JxlVarDctTransform.Hornuss:
                TransformHornuss(plane, offset, stride);
                break;
            case JxlVarDctTransform.Dct4x8:
                TransformDct4x8(plane, offset, stride, transpose: false);
                break;
            case JxlVarDctTransform.Dct8x4:
                TransformDct4x8(plane, offset, stride, transpose: true);
                break;
            case JxlVarDctTransform.Afv0:
            case JxlVarDctTransform.Afv1:
            case JxlVarDctTransform.Afv2:
            case JxlVarDctTransform.Afv3:
                TransformAfv(plane, offset, stride, (int)t - (int)JxlVarDctTransform.Afv0);
                break;
            default:
                Dct2d(plane, offset, stride, bw * 8, bh * 8, forward: false);
                break;
        }
    }

    private static void AuxIdct2(Span<float> b, int o, int s, int size)
    {
        int num2x2 = size / 2;
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < num2x2; y++)
            for (int x = 0; x < num2x2; x++)
            {
                float c00 = b[o + y * s + x];
                float c01 = b[o + y * s + x + num2x2];
                float c10 = b[o + (y + num2x2) * s + x];
                float c11 = b[o + (y + num2x2) * s + x + num2x2];
                scratch[2 * y * size + 2 * x] = c00 + c01 + c10 + c11;
                scratch[2 * y * size + 2 * x + 1] = c00 + c01 - c10 - c11;
                scratch[(2 * y + 1) * size + 2 * x] = c00 - c01 + c10 - c11;
                scratch[(2 * y + 1) * size + 2 * x + 1] = c00 - c01 - c10 + c11;
            }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                b[o + y * s + x] = scratch[y * size + x];
    }

    private static void TransformDct4(Span<float> b, int o, int s)
    {
        AuxIdct2(b, o, s, 2);
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                int bo = (y * 2 + x) * 16;
                // scratch.get_mut(iy, ix): column iy, row ix (jxl-oxide transposes here)
                for (int iy = 0; iy < 4; iy++)
                    for (int ix = 0; ix < 4; ix++)
                        scratch[bo + ix * 4 + iy] = b[o + (y + iy * 2) * s + x + ix * 2];
                Dct2d(scratch, bo, 4, 4, 4, forward: false);
            }
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                int bo = (y * 2 + x) * 16;
                for (int iy = 0; iy < 4; iy++)
                    for (int ix = 0; ix < 4; ix++)
                        b[o + (y * 4 + iy) * s + x * 4 + ix] = scratch[bo + iy * 4 + ix];
            }
    }

    private static void TransformHornuss(Span<float> b, int o, int s)
    {
        AuxIdct2(b, o, s, 2);
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                Span<float> sc = scratch.Slice((y * 2 + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                    for (int ix = 0; ix < 4; ix++)
                        sc[iy * 4 + ix] = b[o + (y + iy * 2) * s + x + ix * 2];
                float residualSum = 0;
                for (int k = 1; k < 16; k++)
                    residualSum += sc[k];
                float avg = sc[0] - residualSum / 16f;
                sc[0] = sc[5];
                sc[5] = 0;
                for (int k = 0; k < 16; k++)
                    sc[k] += avg;
            }
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                Span<float> sc = scratch.Slice((y * 2 + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                    for (int ix = 0; ix < 4; ix++)
                        b[o + (y * 4 + iy) * s + x * 4 + ix] = sc[iy * 4 + ix];
            }
    }

    private static void TransformDct4x8(Span<float> b, int o, int s, bool transpose)
    {
        float coeff0 = b[o], coeff1 = b[o + s];
        b[o] = coeff0 + coeff1;
        b[o + s] = coeff0 - coeff1;

        Span<float> scratch = stackalloc float[64];
        for (int idx = 0; idx < 2; idx++)
        {
            int bo = idx * 32;
            for (int iy = 0; iy < 4; iy++)
                for (int ix = 0; ix < 8; ix++)
                    scratch[bo + iy * 8 + ix] = b[o + (iy * 2 + idx) * s + ix];
            Dct2d(scratch, bo, 8, 8, 4, forward: false);
        }

        if (transpose)
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    b[o + x * s + y] = scratch[y * 8 + x];
        }
        else
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    b[o + y * s + x] = scratch[y * 8 + x];
        }
    }

    private static void TransformAfv(Span<float> b, int o, int s, int n)
    {
        int flipX = n % 2, flipY = n / 2;

        Span<float> coeffAfv = stackalloc float[16];
        coeffAfv[0] = (b[o] + b[o + 1] + b[o + s]) * 4f;
        for (int idx = 1; idx < 16; idx++)
        {
            int iy = idx / 4, ix = idx % 4;
            coeffAfv[idx] = b[o + 2 * iy * s + 2 * ix];
        }

        Span<float> samplesAfv = stackalloc float[16];
        for (int k = 0; k < 16; k++)
        {
            float coeff = coeffAfv[k];
            ReadOnlySpan<float> basis = AfvBasis.AsSpan(k * 16, 16);
            for (int i = 0; i < 16; i++)
                samplesAfv[i] = MathF.FusedMultiplyAdd(coeff, basis[i], samplesAfv[i]);
        }

        Span<float> scratch4x4 = stackalloc float[16];
        Span<float> scratch4x8 = stackalloc float[32];

        scratch4x4[0] = b[o] - b[o + 1] + b[o + s];
        for (int iy = 0; iy < 4; iy++)
            for (int ix = 0; ix < 4; ix++)
            {
                if ((ix | iy) == 0) continue;
                scratch4x4[ix * 4 + iy] = b[o + 2 * iy * s + 2 * ix + 1];
            }
        Dct2d(scratch4x4, 0, 4, 4, 4, forward: false);

        scratch4x8[0] = b[o] - b[o + s];
        for (int iy = 0; iy < 4; iy++)
            for (int ix = 0; ix < 8; ix++)
            {
                if ((ix | iy) == 0) continue;
                scratch4x8[iy * 8 + ix] = b[o + (2 * iy + 1) * s + ix];
            }
        Dct2d(scratch4x8, 0, 8, 8, 4, forward: false);

        for (int iy = 0; iy < 4; iy++)
        {
            int afvY = flipY == 0 ? iy : 3 - iy;
            for (int ix = 0; ix < 4; ix++)
            {
                int afvX = flipX == 0 ? ix : 3 - ix;
                b[o + (flipY * 4 + iy) * s + flipX * 4 + ix] = samplesAfv[afvY * 4 + afvX];
            }
        }

        for (int iy = 0; iy < 4; iy++)
        {
            int y = flipY * 4 + iy;
            for (int ix = 0; ix < 4; ix++)
                b[o + y * s + (1 - flipX) * 4 + ix] = scratch4x4[iy * 4 + ix];
        }

        for (int iy = 0; iy < 4; iy++)
        {
            int y = (1 - flipY) * 4 + iy;
            for (int x = 0; x < 8; x++)
                b[o + y * s + x] = scratch4x8[iy * 8 + x];
        }
    }

    // AFV_BASIS (transform_common.rs), 16 basis vectors of 16 samples, row-major.
    private static readonly float[] AfvBasis =
    [
        0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f,
        0.876902929799142f, 0.2206518106944235f, -0.10140050393753763f, -0.1014005039375375f,
        0.2206518106944236f, -0.10140050393753777f, -0.10140050393753772f, -0.10140050393753763f,
        -0.10140050393753758f, -0.10140050393753769f, -0.1014005039375375f, -0.10140050393753768f,
        -0.10140050393753768f, -0.10140050393753759f, -0.10140050393753763f, -0.10140050393753741f,
        0.0f, 0.0f, 0.40670075830260755f, 0.44444816619734445f,
        0.0f, 0.0f, 0.19574399372042936f, 0.2929100136981264f,
        -0.40670075830260716f, -0.19574399372042872f, 0.0f, 0.11379074460448091f,
        -0.44444816619734384f, -0.29291001369812636f, -0.1137907446044814f, 0.0f,
        0.0f, 0.0f, -0.21255748058288748f, 0.3085497062849767f,
        0.0f, 0.4706702258572536f, -0.1621205195722993f, 0.0f,
        -0.21255748058287047f, -0.16212051957228327f, -0.47067022585725277f, -0.1464291867126764f,
        0.3085497062849487f, 0.0f, -0.14642918671266536f, 0.4251149611657548f,
        0.0f, -0.7071067811865474f, 0.0f, 0.0f,
        0.70710678118654752f, 0.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 0.0f,
        -0.4105377591765233f, 0.6235485373547691f, -0.06435071657946274f, -0.06435071657946266f,
        0.6235485373547694f, -0.06435071657946284f, -0.0643507165794628f, -0.06435071657946274f,
        -0.06435071657946272f, -0.06435071657946279f, -0.06435071657946266f, -0.06435071657946277f,
        -0.06435071657946277f, -0.06435071657946273f, -0.06435071657946274f, -0.0643507165794626f,
        0.0f, 0.0f, -0.4517556589999482f, 0.15854503551840063f,
        0.0f, -0.04038515160822202f, 0.0074182263792423875f, 0.39351034269210167f,
        -0.45175565899994635f, 0.007418226379244351f, 0.1107416575309343f, 0.08298163094882051f,
        0.15854503551839705f, 0.3935103426921022f, 0.0829816309488214f, -0.45175565899994796f,
        0.0f, 0.0f, -0.304684750724869f, 0.5112616136591823f,
        0.0f, 0.0f, -0.290480129728998f, -0.06578701549142804f,
        0.304684750724884f, 0.2904801297290076f, 0.0f, -0.23889773523344604f,
        -0.5112616136592012f, 0.06578701549142545f, 0.23889773523345467f, 0.0f,
        0.0f, 0.0f, 0.3017929516615495f, 0.25792362796341184f,
        0.0f, 0.16272340142866204f, 0.09520022653475037f, 0.0f,
        0.3017929516615503f, 0.09520022653475055f, -0.16272340142866173f, -0.35312385449816297f,
        0.25792362796341295f, 0.0f, -0.3531238544981624f, -0.6035859033230976f,
        0.0f, 0.0f, 0.40824829046386274f, 0.0f,
        0.0f, 0.0f, 0.0f, -0.4082482904638628f,
        -0.4082482904638635f, 0.0f, 0.0f, -0.40824829046386296f,
        0.0f, 0.4082482904638634f, 0.408248290463863f, 0.0f,
        0.0f, 0.0f, 0.1747866975480809f, 0.0812611176717539f,
        0.0f, 0.0f, -0.3675398009862027f, -0.307882213957909f,
        -0.17478669754808135f, 0.3675398009862011f, 0.0f, 0.4826689115059883f,
        -0.08126111767175039f, 0.30788221395790305f, -0.48266891150598584f, 0.0f,
        0.0f, 0.0f, -0.21105601049335784f, 0.18567180916109802f,
        0.0f, 0.0f, 0.49215859013738733f, -0.38525013709251915f,
        0.21105601049335806f, -0.49215859013738905f, 0.0f, 0.17419412659916217f,
        -0.18567180916109904f, 0.3852501370925211f, -0.1741941265991621f, 0.0f,
        0.0f, 0.0f, -0.14266084808807264f, -0.3416446842253372f,
        0.0f, 0.7367497537172237f, 0.24627107722075148f, -0.08574019035519306f,
        -0.14266084808807344f, 0.24627107722075137f, 0.14883399227113567f, -0.04768680350229251f,
        -0.3416446842253373f, -0.08574019035519267f, -0.047686803502292804f, -0.14266084808807242f,
        0.0f, 0.0f, -0.13813540350758585f, 0.3302282550303788f,
        0.0f, 0.08755115000587084f, -0.07946706605909573f, -0.4613374887461511f,
        -0.13813540350758294f, -0.07946706605910261f, 0.49724647109535086f, 0.12538059448563663f,
        0.3302282550303805f, -0.4613374887461554f, 0.12538059448564315f, -0.13813540350758452f,
        0.0f, 0.0f, -0.17437602599651067f, 0.0702790691196284f,
        0.0f, -0.2921026642334881f, 0.3623817333531167f, 0.0f,
        -0.1743760259965108f, 0.36238173335311646f, 0.29210266423348785f, -0.4326608024727445f,
        0.07027906911962818f, 0.0f, -0.4326608024727457f, 0.34875205199302267f,
        0.0f, 0.0f, 0.11354987314994337f, -0.07417504595810355f,
        0.0f, 0.19402893032594343f, -0.435190496523228f, 0.21918684838857466f,
        0.11354987314994257f, -0.4351904965232251f, 0.5550443808910661f, -0.25468277124066463f,
        -0.07417504595810233f, 0.2191868483885728f, -0.25468277124066413f, 0.1135498731499429f,
    ];
}
