namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT dequantization weight matrices (ISO/IEC 18181-1 §K.5.2), generated for the
/// <c>encoding_mode = 0</c> (all-default) DequantMatrixSet — the table an encoder emits and the
/// simplest decoder reads. Faithful port of jxl-oxide's <c>jxl-vardct/src/dequant.rs</c> default
/// path: per transform-type parameters expand to per-band sequences, are radially interpolated
/// across the matrix, then reciprocated so each entry is a multiplicative quant weight.
///
/// <para>
/// The custom (signalled) encodings — Raw modular matrices and per-image overrides — are decode-only
/// and not built here; the default set is sufficient for the VarDCT path we encode.
/// </para>
/// </summary>
internal sealed class JxlDequantMatrices
{
    private const float Sqrt2 = 1.41421356237309504880f;

    private readonly float[][][] _matrices;    // [paramIndex][channel][raster width*height]
    private readonly float[][][] _matricesTr;  // transposed (height-major) variant

    private JxlDequantMatrices(float[][][] matrices, float[][][] matricesTr)
    {
        _matrices = matrices;
        _matricesTr = matricesTr;
    }

    /// <summary>Dequant weights for a channel/transform, in raster (width-major) order.</summary>
    public float[] Get(int channel, JxlVarDctTransform t) => _matrices[t.DequantMatrixParamIndex()][channel];

    /// <summary>Dequant weights for a channel/transform, transposed (height-major) order.</summary>
    public float[] GetTransposed(int channel, JxlVarDctTransform t) => _matricesTr[t.DequantMatrixParamIndex()][channel];

    // The 17 representatives, in param-index order.
    private static readonly JxlVarDctTransform[] SelectList =
    {
        JxlVarDctTransform.Dct8, JxlVarDctTransform.Hornuss, JxlVarDctTransform.Dct2,
        JxlVarDctTransform.Dct4, JxlVarDctTransform.Dct16, JxlVarDctTransform.Dct32,
        JxlVarDctTransform.Dct8x16, JxlVarDctTransform.Dct8x32, JxlVarDctTransform.Dct16x32,
        JxlVarDctTransform.Dct4x8, JxlVarDctTransform.Afv0, JxlVarDctTransform.Dct64,
        JxlVarDctTransform.Dct32x64, JxlVarDctTransform.Dct128, JxlVarDctTransform.Dct64x128,
        JxlVarDctTransform.Dct256, JxlVarDctTransform.Dct128x256,
    };

    public static JxlDequantMatrices BuildDefault()
    {
        var matrices = new float[SelectList.Length][][];
        for (int i = 0; i < SelectList.Length; i++)
        {
            float[][] m = BuildDefaultMatrix(SelectList[i]);
            // Reciprocate (all default encodings need it) and validate.
            foreach (float[] ch in m)
                for (int k = 0; k < ch.Length; k++)
                {
                    float w = 1f / ch[k];
                    if (w >= 1e8f || w <= 0f)
                        throw new InvalidDataException("JPEG XL: dequant matrix element out of range.");
                    ch[k] = w;
                }
            matrices[i] = m;
        }

        // Transposed variant: out[idx] = matrix[(idx % height) * width + (idx / height)].
        var matricesTr = new float[SelectList.Length][][];
        for (int i = 0; i < SelectList.Length; i++)
        {
            (int width, int height) = SelectList[i].DequantMatrixSize();
            var tr = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                float[] src = matrices[i][c];
                var outp = new float[src.Length];
                for (int idx = 0; idx < outp.Length; idx++)
                {
                    int mx = idx % height;
                    int my = idx / height;
                    outp[idx] = src[mx * width + my];
                }
                tr[c] = outp;
            }
            matricesTr[i] = tr;
        }

        return new JxlDequantMatrices(matrices, matricesTr);
    }

    private static float Mult(float x) => x > 0f ? 1f + x : 1f / (1f - x);

    private static float Interpolate(float pos, float max, float[] bands)
    {
        int len = bands.Length;
        if (len == 1)
            return bands[0];

        float scaledPos = pos * (len - 1) / max;
        int scaledIndex = (int)scaledPos;
        float frac = scaledPos - scaledIndex;
        float a = bands[scaledIndex];
        float b = bands[scaledIndex + 1];
        return a * MathF.Pow(b / a, frac);
    }

    private static float[] DctQuantWeights(float[] p, int width, int height)
    {
        var bands = new float[p.Length];
        bands[0] = p[0];
        for (int i = 1; i < p.Length; i++)
        {
            float band = bands[i - 1] * Mult(p[i]);
            if (band <= 0f)
                throw new InvalidDataException("JPEG XL: DCT dequant matrix band <= 0.");
            bands[i] = band;
        }

        var ret = new float[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float dx = x / (float)(width - 1);
                float dy = y / (float)(height - 1);
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                ret[y * width + x] = Interpolate(distance, Sqrt2 + 1e-6f, bands);
            }
        return ret;
    }

    // --- Default parameter tables (post-scale, i.e. used verbatim, no ×64) ---

    private static readonly float[] SeqA = { -1.025f, -0.78f, -0.65012f, -0.19041574f, -0.20819396f, -0.421064f, -0.32733846f };
    private static readonly float[] SeqB = { -0.30419582f, -0.36330363f, -0.3566038f, -0.34430745f, -0.33699593f, -0.30180866f, -0.27321684f };
    private static readonly float[] SeqC = { -1.2f, -1.2f, -0.8f, -0.7f, -0.7f, -0.4f, -0.5f };

    private static readonly float[][] Dct4x8Params =
    {
        new[] { 2198.0505f, -0.96269625f, -0.7619425f, -0.65511405f },
        new[] { 764.36554f, -0.926302f, -0.967523f, -0.2784529f },
        new[] { 527.10754f, -1.4594386f, -1.4500821f, -1.5843723f },
    };
    private static readonly float[][] Dct4Params =
    {
        new[] { 2200.0f, 0.0f, 0.0f, 0.0f },
        new[] { 392.0f, 0.0f, 0.0f, 0.0f },
        new[] { 112.0f, -0.25f, -0.25f, -0.5f },
    };

    private static float[][] DctSeq(float a, float b, float c) => new[]
    {
        Prepend(a, SeqA), Prepend(b, SeqB), Prepend(c, SeqC),
    };

    private static float[] Prepend(float head, float[] tail)
    {
        var r = new float[tail.Length + 1];
        r[0] = head;
        Array.Copy(tail, 0, r, 1, tail.Length);
        return r;
    }

    private static float[][] BuildDefaultMatrix(JxlVarDctTransform t)
    {
        switch (t)
        {
            case JxlVarDctTransform.Dct8:
                return Dct(t, new[]
                {
                    new[] { 3150.0f, 0.0f, -0.4f, -0.4f, -0.4f, -2.0f },
                    new[] { 560.0f, 0.0f, -0.3f, -0.3f, -0.3f, -0.3f },
                    new[] { 512.0f, -2.0f, -1.0f, 0.0f, -1.0f, -2.0f },
                });
            case JxlVarDctTransform.Hornuss:
                return Hornuss(new[]
                {
                    new[] { 280.0f, 3160.0f, 3160.0f },
                    new[] { 60.0f, 864.0f, 864.0f },
                    new[] { 18.0f, 200.0f, 200.0f },
                });
            case JxlVarDctTransform.Dct2:
                return Dct2(new[]
                {
                    new[] { 3840.0f, 2560.0f, 1280.0f, 640.0f, 480.0f, 300.0f },
                    new[] { 960.0f, 640.0f, 320.0f, 180.0f, 140.0f, 120.0f },
                    new[] { 640.0f, 320.0f, 128.0f, 64.0f, 32.0f, 16.0f },
                });
            case JxlVarDctTransform.Dct4:
                return Dct4(new[] { new[] { 1f, 1f }, new[] { 1f, 1f }, new[] { 1f, 1f } }, Dct4Params);
            case JxlVarDctTransform.Dct16:
                return Dct(t, new[]
                {
                    new[] { 8996.873f, -1.3000778f, -0.4942453f, -0.43909377f, -0.6350102f, -0.9017726f, -1.6162099f },
                    new[] { 3191.4836f, -0.67424583f, -0.80745816f, -0.4492584f, -0.3586544f, -0.3132239f, -0.37615025f },
                    new[] { 1157.504f, -2.0531423f, -1.4f, -0.5068713f, -0.4270873f, -1.4856834f, -4.920914f },
                });
            case JxlVarDctTransform.Dct32:
                return Dct(t, new[]
                {
                    new[] { 15718.408f, -1.025f, -0.98f, -0.9012f, -0.4f, -0.48819396f, -0.421064f, -0.27f },
                    new[] { 7305.7637f, -0.8041958f, -0.76330364f, -0.5566038f, -0.49785304f, -0.43699592f, -0.40180868f, -0.27321684f },
                    new[] { 3803.5317f, -3.0607336f, -2.041327f, -2.023565f, -0.54953897f, -0.4f, -0.4f, -0.3f },
                });
            case JxlVarDctTransform.Dct8x16: // shared index 6 (Dct16x8 | Dct8x16)
                return Dct(t, new[]
                {
                    new[] { 7240.7734f, -0.7f, -0.7f, -0.2f, -0.2f, -0.2f, -0.5f },
                    new[] { 1448.1547f, -0.5f, -0.5f, -0.5f, -0.2f, -0.2f, -0.2f },
                    new[] { 506.85413f, -1.4f, -0.2f, -0.5f, -0.5f, -1.5f, -3.6f },
                });
            case JxlVarDctTransform.Dct8x32: // shared index 7
                return Dct(t, new[]
                {
                    new[] { 16283.249f, -1.7812846f, -1.6309059f, -1.0382179f, -0.85f, -0.7f, -0.9f, -1.2360638f },
                    new[] { 5089.1577f, -0.3200494f, -0.3536285f, -0.3034f, -0.61f, -0.5f, -0.5f, -0.6f },
                    new[] { 3397.7761f, -0.32132736f, -0.3450762f, -0.7034f, -0.9f, -1.0f, -1.0f, -1.1754606f },
                });
            case JxlVarDctTransform.Dct16x32: // shared index 8
                return Dct(t, new[]
                {
                    new[] { 13844.971f, -0.971138f, -0.658f, -0.42026f, -0.22712f, -0.2206f, -0.226f, -0.6f },
                    new[] { 4798.964f, -0.6112531f, -0.8377079f, -0.7901486f, -0.26927274f, -0.38272768f, -0.22924222f, -0.20719099f },
                    new[] { 1807.2369f, -1.2f, -1.2f, -0.7f, -0.7f, -0.7f, -0.4f, -0.5f },
                });
            case JxlVarDctTransform.Dct4x8:
                return Dct4x8(new[] { new[] { 1f }, new[] { 1f }, new[] { 1f } }, Dct4x8Params);
            case JxlVarDctTransform.Afv0:
                return Afv(
                    new[]
                    {
                        new[] { 3072.0f, 3072.0f, 256.0f, 256.0f, 256.0f, 414.0f, 0.0f, 0.0f, 0.0f },
                        new[] { 1024.0f, 1024.0f, 50.0f, 50.0f, 50.0f, 58.0f, 0.0f, 0.0f, 0.0f },
                        new[] { 384.0f, 384.0f, 12.0f, 12.0f, 12.0f, 22.0f, -0.25f, -0.25f, -0.25f },
                    },
                    Dct4x8Params, Dct4Params);
            case JxlVarDctTransform.Dct64:
                return Dct(t, DctSeq(23966.166f, 8380.191f, 4493.024f));
            case JxlVarDctTransform.Dct32x64:
                return Dct(t, DctSeq(15358.898f, 5597.3604f, 2919.9617f));
            case JxlVarDctTransform.Dct128:
                return Dct(t, DctSeq(47932.332f, 16760.383f, 8986.048f));
            case JxlVarDctTransform.Dct64x128:
                return Dct(t, DctSeq(30717.797f, 11194.721f, 5839.9233f));
            case JxlVarDctTransform.Dct256:
                return Dct(t, DctSeq(95864.664f, 33520.766f, 17972.096f));
            case JxlVarDctTransform.Dct128x256:
                return Dct(t, DctSeq(61435.594f, 24209.441f, 12979.847f));
            default:
                throw new ArgumentOutOfRangeException(nameof(t));
        }
    }

    private static float[][] Dct(JxlVarDctTransform t, float[][] dctParams)
    {
        (int width, int height) = t.DequantMatrixSize();
        return new[]
        {
            DctQuantWeights(dctParams[0], width, height),
            DctQuantWeights(dctParams[1], width, height),
            DctQuantWeights(dctParams[2], width, height),
        };
    }

    private static float[][] Hornuss(float[][] p)
    {
        var ret = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            var m = new float[64];
            Array.Fill(m, p[c][0]);
            m[0] = 1.0f;
            m[1] = p[c][1];
            m[8] = p[c][1];
            m[9] = p[c][2];
            ret[c] = m;
        }
        return ret;
    }

    private static float[][] Dct2(float[][] p)
    {
        var ret = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            var m = new float[64];
            m[0] = 1.0f;
            for (int idx = 0; idx < 6; idx++)
            {
                float val = p[c][idx];
                int shift = idx / 2;
                int dim = 1 << shift;
                if (idx % 2 == 0)
                {
                    for (int y = 0; y < dim; y++)
                        for (int x = dim; x < dim * 2; x++)
                        {
                            m[y * 8 + x] = val;
                            m[x * 8 + y] = val;
                        }
                }
                else
                {
                    for (int y = dim; y < dim * 2; y++)
                        for (int x = dim; x < dim * 2; x++)
                            m[y * 8 + x] = val;
                }
            }
            ret[c] = m;
        }
        return ret;
    }

    private static float[][] Dct4(float[][] paramsByChannel, float[][] dctParams)
    {
        var ret = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] mat = DctQuantWeights(dctParams[c], 4, 4);
            var m = new float[64];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    float v = mat[y * 4 + x];
                    m[y * 16 + x * 2] = v;
                    m[y * 16 + x * 2 + 1] = v;
                    m[(y * 2 + 1) * 8 + x * 2] = v;
                    m[(y * 2 + 1) * 8 + x * 2 + 1] = v;
                }
            m[1] /= paramsByChannel[c][0];
            m[8] /= paramsByChannel[c][0];
            m[9] /= paramsByChannel[c][1];
            ret[c] = m;
        }
        return ret;
    }

    private static float[][] Dct4x8(float[][] paramsByChannel, float[][] dctParams)
    {
        var ret = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] mat = DctQuantWeights(dctParams[c], 8, 4); // width 8, height 4 -> 32
            var m = new float[64];
            // Each of the 4 source rows (8 wide) is duplicated into two output rows.
            for (int srcY = 0; srcY < 4; srcY++)
                for (int dup = 0; dup < 2; dup++)
                {
                    int dstRow = srcY * 2 + dup;
                    Array.Copy(mat, srcY * 8, m, dstRow * 8, 8);
                }
            m[8] /= paramsByChannel[c][0];
            ret[c] = m;
        }
        return ret;
    }

    private static float[][] Afv(float[][] paramsByChannel, float[][] dctParams, float[][] dct4x4Params)
    {
        float[] freqs =
        {
            0.0f, 0.0f, 0.8517779f, 5.3777843f, 0.0f, 0.0f, 4.734748f, 5.4492455f,
            1.659827f, 4.0f, 7.275749f, 10.423227f, 2.6629324f, 7.6306577f, 8.962389f, 12.971662f,
        };
        const float freqLo = 0.8517779f;   // freqs[2]
        const float freqHi = 12.971662f;   // freqs[15]

        var ret = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] p = paramsByChannel[c];
            float[] weights4x8 = DctQuantWeights(dctParams[c], 8, 4);  // 32
            float[] weights4x4 = DctQuantWeights(dct4x4Params[c], 4, 4); // 16

            var bands = new float[4];
            bands[0] = p[5];
            for (int i = 1; i < 4; i++)
                bands[i] = bands[i - 1] * Mult(p[5 + i]);

            var m = new float[64];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    float v = (x, y) switch
                    {
                        (0, 0) => 1.0f,
                        (0, 1) => p[2],
                        (1, 0) => p[3],
                        (1, 1) => p[4],
                        _ => Interpolate(freqs[y * 4 + x] - freqLo, freqHi - freqLo + 1e-6f, bands),
                    };
                    m[16 * y + 2 * x] = v;
                }

            // Interleave the 4x8 (odd output rows) and 4x4 (even rows, odd cols) DCT weights.
            for (int y = 0; y < 4; y++)
            {
                int baseIdx = y * 16;
                // row1 = odd raster row: all 8 columns get the 4x8 weights.
                for (int x = 0; x < 8; x++)
                    m[baseIdx + 8 + x] = (y == 0 && x == 0) ? p[0] : weights4x8[y * 8 + x];
                // row0 = even raster row: odd columns (2x+1) get the 4x4 weights.
                for (int x = 0; x < 4; x++)
                    m[baseIdx + 2 * x + 1] = (y == 0 && x == 0) ? p[1] : weights4x4[y * 4 + x];
            }
            ret[c] = m;
        }
        return ret;
    }
}
