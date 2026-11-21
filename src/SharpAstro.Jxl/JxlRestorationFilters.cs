// Portions of this file are ported from jxl-oxide (https://github.com/tirr-c/jxl-oxide), Copyright (c)
// Wonwoo Choi and the jxl-oxide contributors, used under the MIT licence; see THIRD-PARTY-NOTICES.md.

using System.Runtime.InteropServices;

namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT smoothing and restoration filters (ISO/IEC 18181-1 §J), ported from jxl-oxide's jxl-render:
/// adaptive LF smoothing (<c>vardct/generic/mod.rs</c> <c>adaptive_lf_smoothing_impl</c>), the Gabor-like filter
/// (<c>filter/impls/generic/gabor.rs</c>) and the edge-preserving filter (<c>filter/epf.rs</c>,
/// <c>filter/impls/generic/epf.rs</c>).
///
/// <para>
/// The two image filters run in place over the frame planes, each output row computed from input rows above and
/// below it, so they work on horizontal strips in parallel: before a pass, the input rows each strip reads from its
/// neighbours are copied aside, and inside a strip a small ring keeps the original rows it has already overwritten.
/// That holds no second copy of the frame, where jxl-oxide swaps between two full-size buffers. Rows and columns
/// past the image edge are mirrored (jxl-oxide <c>util::mirror</c>).
/// </para>
/// </summary>
internal static class JxlRestorationFilters
{
    private const float ScaleSelf = 0.052262735f;
    private const float ScaleSide = 0.2034514f;
    private const float ScaleDiag = 0.03348292f;

    /// <summary>Adaptive LF smoothing of the three LF planes (no chroma subsampling).</summary>
    public static void AdaptiveLfSmoothing(float[][] lf, int width, int height, float[] lfScale)
    {
        if (width <= 2 || height <= 2)
            return;

        var udsum = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] g = lf[c];
            var u = new float[width * (height - 2)];
            for (int y = 0; y < height - 2; y++)
                for (int x = 0; x < width; x++)
                    u[y * width + x] = g[y * width + x] + g[(y + 2) * width + x];
            udsum[c] = u;
        }

        float[] inX = lf[0], inY = lf[1], inB = lf[2];
        float lfX = lfScale[0], lfY = lfScale[1], lfB = lfScale[2];
        for (int r = 0; r < height - 2; r++)
        {
            int row = (r + 1) * width;
            float[] ux = udsum[0], uy = udsum[1], ub = udsum[2];
            int urow = r * width;
            float xPrev = inX[row], yPrev = inY[row], bPrev = inB[row];
            for (int x = 1; x < width - 1; x++)
            {
                float xSelf = inX[row + x];
                float xSide = xPrev + inX[row + x + 1] + ux[urow + x];
                float xDiag = ux[urow + x - 1] + ux[urow + x + 1];
                float xWa = xSelf * ScaleSelf + xSide * ScaleSide + xDiag * ScaleDiag;
                float xGap = MathF.Abs(xWa - xSelf) / lfX;

                float ySelf = inY[row + x];
                float ySide = yPrev + inY[row + x + 1] + uy[urow + x];
                float yDiag = uy[urow + x - 1] + uy[urow + x + 1];
                float yWa = ySelf * ScaleSelf + ySide * ScaleSide + yDiag * ScaleDiag;
                float yGap = MathF.Abs(yWa - ySelf) / lfY;

                float bSelf = inB[row + x];
                float bSide = bPrev + inB[row + x + 1] + ub[urow + x];
                float bDiag = ub[urow + x - 1] + ub[urow + x + 1];
                float bWa = bSelf * ScaleSelf + bSide * ScaleSide + bDiag * ScaleDiag;
                float bGap = MathF.Abs(bWa - bSelf) / lfB;

                float gap = MathF.Max(MathF.Max(0.5f, xGap), MathF.Max(yGap, bGap));
                float gapScale = MathF.Max(3f - 4f * gap, 0f);

                inX[row + x] = (xWa - xSelf) * gapScale + xSelf;
                inY[row + x] = (yWa - ySelf) * gapScale + ySelf;
                inB[row + x] = (bWa - bSelf) * gapScale + bSelf;
                xPrev = xSelf;
                yPrev = ySelf;
                bPrev = bSelf;
            }
        }
    }

    /// <summary>jxl-oxide <c>util::mirror</c>: reflects an index into [0, len).</summary>
    internal static int Mirror(int offset, int len)
    {
        while (true)
        {
            if (offset < 0)
                offset = -(offset + 1);
            else if (offset >= len)
                offset = 2 * len - offset - 1;
            else
                return offset;
        }
    }



    /// <summary>One input row: a slice of a plane, the ring or the halo, all holding float bits in ints.</summary>
    private struct RowRef
    {
        public int[] A;
        public int O;

        public readonly float this[int x] => BitConverter.Int32BitsToSingle(A[O + x]);
    }

    private delegate void RowFilter(RowRef[][] rows, float[][] output, int y);

    /// <summary>
    /// Runs a row filter of vertical radius <paramref name="radius"/> over the <paramref name="width"/> x
    /// <paramref name="height"/> region of the three planes (floats stored as their bits) in place.
    /// <paramref name="rowFilter"/> gets the 2 * radius + 1 input rows of each channel around output row y
    /// (mirrored at the edges) and writes the output row of each channel.
    /// </summary>
    private static void RunInPlace(
        int[][] planes, int stride, int width, int height, int radius, RowFilter rowFilter)
    {
        // A few strips per core: enough to balance the load, few enough that the halos stay small.
        int stripRows = Math.Max(64, (height + 4 * Environment.ProcessorCount - 1) / (4 * Environment.ProcessorCount));
        int strips = (height + stripRows - 1) / stripRows;
        int window = 2 * radius + 1;

        // Halo rows: for strip s, the input rows above its first row and below its last row, copied before any
        // strip writes. Indexed [s][c], rows 0..radius-1 above, radius..2*radius-1 below.
        var halo = new int[strips][][];
        for (int s = 0; s < strips; s++)
        {
            int y0 = s * stripRows, y1 = Math.Min(height, y0 + stripRows);
            var hs = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                hs[c] = new int[2 * radius * width];
                for (int k = 0; k < radius; k++)
                {
                    int above = Mirror(y0 - radius + k, height);
                    Array.Copy(planes[c], above * stride, hs[c], k * width, width);
                    int below = Mirror(y1 + k, height);
                    Array.Copy(planes[c], below * stride, hs[c], (radius + k) * width, width);
                }
            }
            halo[s] = hs;
        }

        Parallel.For(0, strips, s =>
        {
            int y0 = s * stripRows, y1 = Math.Min(height, y0 + stripRows);
            int[][] hs = halo[s];
            // Ring of the original rows this strip has overwritten: row y lives at slot y % (radius + 1).
            int ringRows = radius + 1;
            var ring = new int[3][];
            var output = new float[3][];
            var rows = new RowRef[3][];
            for (int c = 0; c < 3; c++)
            {
                ring[c] = new int[ringRows * width];
                output[c] = new float[width];
                rows[c] = new RowRef[window];
            }

            for (int y = y0; y < y1; y++)
            {
                for (int k = 0; k < window; k++)
                {
                    int yy = Mirror(y - radius + k, height);
                    for (int c = 0; c < 3; c++)
                    {
                        if (yy >= y && yy < y1)
                            rows[c][k] = new RowRef { A = planes[c], O = yy * stride };
                        else if (yy >= y0 && yy < y)
                            rows[c][k] = new RowRef { A = ring[c], O = (yy % ringRows) * width };
                        else if (yy < y0)
                            rows[c][k] = new RowRef { A = hs[c], O = (yy - (y0 - radius)) * width };
                        else
                            rows[c][k] = new RowRef { A = hs[c], O = (radius + yy - y1) * width };
                    }
                }

                rowFilter(rows, output, y);

                for (int c = 0; c < 3; c++)
                {
                    Array.Copy(planes[c], y * stride, ring[c], (y % ringRows) * width, width);
                    MemoryMarshal.Cast<float, int>(output[c].AsSpan()).CopyTo(planes[c].AsSpan(y * stride, width));
                }
            }
        });
    }

    /// <summary>The Gabor-like 3x3 filter, per channel with its two weights (jxl-oxide <c>run_gabor_row_generic</c>).</summary>
    public static void Gabor(int[][] planes, int stride, int width, int height, float[][] weights)
    {
        RunInPlace(planes, stride, width, height, 1, (rows, output, _) =>
        {
            for (int c = 0; c < 3; c++)
            {
                float w0 = weights[c][0], w1 = weights[c][1];
                float globalWeight = 1f / (1f + w0 * 4f + w1 * 4f);
                RowRef t = rows[c][0], m = rows[c][1], b = rows[c][2];
                float[] o = output[c];
                for (int x = 0; x < width; x++)
                {
                    int xl = x == 0 ? 0 : x - 1;
                    int xr = x + 1 >= width ? width - 1 : x + 1;
                    float sumSide = t[x] + m[xl] + m[xr] + b[x];
                    float sumDiag = t[xl] + t[xr] + b[xl] + b[xr];
                    o[x] = (m[x] + sumSide * w0 + sumDiag * w1) * globalWeight;
                }
            }
        });
    }

    private static readonly (int X, int Y)[] EpfKernel1 = [(0, -1), (0, 1), (-1, 0), (1, 0)];
    private static readonly (int X, int Y)[] EpfKernel2 =
    [
        (0, -2), (-1, -1), (0, -1), (1, -1),
        (-2, 0), (-1, 0), (1, 0), (2, 0),
        (-1, 1), (0, 1), (1, 1), (0, 2),
    ];
    private static readonly (int X, int Y)[] EpfDist0 = [(0, -1), (1, 0), (0, 0), (-1, 0), (0, 1)];
    private static readonly (int X, int Y)[] EpfDist1 = [(0, -1), (0, 0), (0, 1), (-1, 0), (1, 0)];
    private static readonly (int X, int Y)[] EpfDist2 = [(0, 0)];

    /// <summary>
    /// The edge-preserving filter: step 0 when <c>iters</c> is 3, step 1 always, step 2 when <c>iters</c> is 2
    /// or more. <paramref name="sigma"/> holds one sigma per 8x8 block (row length <paramref name="sigmaStride"/>).
    /// </summary>
    public static void Epf(
        int[][] planes, int stride, int width, int height, JxlEpfParams p, float[] sigma, int sigmaStride)
    {
        if (p.Iters == 3)
            EpfStep(planes, stride, width, height, p, sigma, sigmaStride, 0);
        EpfStep(planes, stride, width, height, p, sigma, sigmaStride, 1);
        if (p.Iters >= 2)
            EpfStep(planes, stride, width, height, p, sigma, sigmaStride, 2);
    }

    private static void EpfStep(
        int[][] planes, int stride, int width, int height, JxlEpfParams p, float[] sigma, int sigmaStride, int step)
    {
        (int X, int Y)[] kernel = step == 0 ? EpfKernel2 : EpfKernel1;
        (int X, int Y)[] dist = step switch { 0 => EpfDist0, 1 => EpfDist1, _ => EpfDist2 };
        float stepMultiplier = step switch { 0 => p.Pass0SigmaScale, 2 => p.Pass2SigmaScale, _ => 1f };
        float borderSadMul = p.BorderSadMul;
        float[] channelScale = p.ChannelScale;
        const int Radius = 3;

        RunInPlace(planes, stride, width, height, Radius, (rows, output, y) =>
        {
            bool isYBorder = ((y + 1) & 0b110) == 0;
            Span<float> sm = stackalloc float[8];
            for (int i = 0; i < 8; i++)
                sm[i] = isYBorder ? stepMultiplier * borderSadMul : stepMultiplier;
            if (!isYBorder)
            {
                sm[0] *= borderSadMul;
                sm[7] *= borderSadMul;
            }

            int sigmaRow = (y / 8) * sigmaStride;
            RowRef c0 = rows[0][Radius], c1 = rows[1][Radius], c2 = rows[2][Radius];
            for (int x = 0; x < width; x++)
            {
                float sigmaVal = sigma[sigmaRow + x / 8];
                if (sigmaVal < 0.3f)
                {
                    output[0][x] = c0[x];
                    output[1][x] = c1[x];
                    output[2][x] = c2[x];
                    continue;
                }
                float negInvSigma = 6.6f * (0.70710678118654752f - 1f) / sigmaVal * sm[x & 7];
                bool interior = x >= 3 && x + 3 < width;

                float sumWeights = 1f;
                float sum0 = c0[x], sum1 = c1[x], sum2 = c2[x];
                foreach ((int kx, int ky) in kernel)
                {
                    int kdy = Radius + ky;
                    int kdx = x + kx;
                    float d = 0f;
                    for (int c = 0; c < 3; c++)
                    {
                        RowRef[] rc = rows[c];
                        float acc = 0f;
                        foreach ((int ix, int iy) in dist)
                        {
                            int kxx = interior ? kdx + ix : Mirror(kdx + ix, width);
                            int bxx = interior ? x + ix : Mirror(x + ix, width);
                            acc += MathF.Abs(rc[kdy + iy][kxx] - rc[Radius + iy][bxx]);
                        }
                        d += channelScale[c] * acc;
                    }

                    float weight = MathF.Max(1f + d * negInvSigma, 0f);
                    sumWeights += weight;
                    int kx2 = interior ? kdx : Mirror(kdx, width);
                    sum0 += weight * rows[0][kdy][kx2];
                    sum1 += weight * rows[1][kdy][kx2];
                    sum2 += weight * rows[2][kdy][kx2];
                }

                output[0][x] = sum0 / sumWeights;
                output[1][x] = sum1 / sumWeights;
                output[2][x] = sum2 / sumWeights;
            }
        });
    }
}
