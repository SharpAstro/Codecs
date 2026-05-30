namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL reversible colour transform (RCT, ISO/IEC 18181-1 §H.6, jxl-modular rct.rs). Operates
/// in place on three equal-size channels at <c>beginC</c>. <c>rct_type</c> splits into a channel
/// permutation (<c>/7</c>) and a transform type (<c>%7</c>); type 6 is the YCoCg-like transform,
/// 0–5 are linear. All arithmetic is wrapping at the sample width (here i32) and every shift is
/// arithmetic. <see cref="Forward"/> is the exact inverse of <see cref="Inverse"/> (encode side).
/// </summary>
internal static class JxlRct
{
    /// <summary>Decode: reconstruct (d,e,f) from the residual channels, then apply the permutation.</summary>
    public static void Inverse(int rctType, int[][] channels, int beginC)
    {
        int permutation = rctType / 7;
        int ty = rctType % 7;
        int[] a = channels[beginC], b = channels[beginC + 1], c = channels[beginC + 2];

        for (int i = 0; i < a.Length; i++)
        {
            unchecked
            {
                int av = a[i], bv = b[i], cv = c[i];
                int d, e, f;
                if (ty == 6)
                {
                    int tmp = av - (cv >> 1);
                    e = cv + tmp;
                    f = tmp - (bv >> 1);
                    d = f + bv;
                }
                else
                {
                    d = av;
                    f = (ty & 1) != 0 ? cv + av : cv;
                    int t2 = ty >> 1;
                    e = t2 == 1 ? bv + av
                      : t2 == 2 ? bv + ((av + f) >> 1)
                      : bv;
                }
                a[i] = d;
                b[i] = e;
                c[i] = f;
            }
        }

        Permute(permutation, channels, beginC);
    }

    /// <summary>Encode: undo the permutation, then apply the forward (residual-producing) transform.</summary>
    public static void Forward(int rctType, int[][] channels, int beginC)
    {
        int permutation = rctType / 7;
        int ty = rctType % 7;

        InversePermute(permutation, channels, beginC);
        int[] a = channels[beginC], b = channels[beginC + 1], c = channels[beginC + 2];

        for (int i = 0; i < a.Length; i++)
        {
            unchecked
            {
                int d = a[i], e = b[i], f = c[i];
                int av, bv, cv;
                if (ty == 6)
                {
                    bv = d - f;
                    int tmp = f + (bv >> 1);
                    cv = e - tmp;
                    av = tmp + (cv >> 1);
                }
                else
                {
                    av = d;
                    cv = (ty & 1) != 0 ? f - d : f;
                    int t2 = ty >> 1;
                    bv = t2 == 1 ? e - d
                       : t2 == 2 ? e - ((d + f) >> 1)
                       : e;
                }
                a[i] = av;
                b[i] = bv;
                c[i] = cv;
            }
        }
    }

    // Decode permutation: reorders the three channels (swaps applied in this order).
    private static void Permute(int permutation, int[][] ch, int b0)
    {
        switch (permutation)
        {
            case 1: Swap(ch, b0, b0 + 1); Swap(ch, b0, b0 + 2); break;
            case 2: Swap(ch, b0, b0 + 1); Swap(ch, b0 + 1, b0 + 2); break;
            case 3: Swap(ch, b0 + 1, b0 + 2); break;
            case 4: Swap(ch, b0, b0 + 1); break;
            case 5: Swap(ch, b0, b0 + 2); break;
        }
    }

    // Encode: the inverse permutation (the decode swaps applied in reverse order).
    private static void InversePermute(int permutation, int[][] ch, int b0)
    {
        switch (permutation)
        {
            case 1: Swap(ch, b0, b0 + 2); Swap(ch, b0, b0 + 1); break;
            case 2: Swap(ch, b0 + 1, b0 + 2); Swap(ch, b0, b0 + 1); break;
            case 3: Swap(ch, b0 + 1, b0 + 2); break;
            case 4: Swap(ch, b0, b0 + 1); break;
            case 5: Swap(ch, b0, b0 + 2); break;
        }
    }

    private static void Swap(int[][] ch, int i, int j) => (ch[i], ch[j]) = (ch[j], ch[i]);
}
