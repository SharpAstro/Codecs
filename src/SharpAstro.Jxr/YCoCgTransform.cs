namespace SharpAstro.Jxr;

/// <summary>
/// Reversible RGB ↔ YUV colour-format conversion as specified by T.832
/// FwdColorFmtConvert1 (D.3.2 / Table D.6) and InvColorFmtConvert2
/// (9.10.4.3 / Table 185). Despite the historical class name, this is
/// NOT classical YCoCg-R — the JXR spec uses its own lifting steps with
/// (V = B - R) as the chroma seed and an asymmetric Floor/Ceil rounding
/// scheme. Encoder calls <see cref="ForwardInPlace"/>; decoder calls
/// <see cref="InverseInPlace"/>.
/// </summary>
/// <remarks>
/// <para>Both directions operate on a flat interleaved buffer of signed
/// ints — the encoder writes <c>{Y, U, V}</c> at the same offsets as the
/// input <c>{R, G, B}</c>, and the decoder reverses that. The transform
/// is exact: <c>Inverse(Forward(rgb)) == rgb</c>.</para>
///
/// <para>Why this is in a class named YCoCg: the original implementation
/// followed the YCoCg-R reference (Co = R-B, t = B + Co/2, Cg = G-t, Y = t + Cg/2)
/// which round-trips symmetrically but does NOT match the JXR wire format —
/// reference decoders (WIC, jxrlib JxrDecApp) reconstruct wrong RGB on
/// our output. The lifting steps below are the spec's exact pseudocode.</para>
/// </remarks>
public static class YCoCgTransform
{
    /// <summary>
    /// Encoder direction (T.832 D.3.2 / Table D.6): map each interleaved
    /// <c>{R, G, B}</c> triple to <c>{Y, U, V}</c> in place. Same buffer
    /// layout in and out — only the per-pixel values change.
    /// </summary>
    public static void ForwardInPlace(Span<int> rgbInterleaved)
    {
        if (rgbInterleaved.Length % 3 != 0)
            throw new ArgumentException("buffer length must be divisible by 3", nameof(rgbInterleaved));

        for (var i = 0; i < rgbInterleaved.Length; i += 3)
        {
            int r = rgbInterleaved[i + 0];
            int g = rgbInterleaved[i + 1];
            int b = rgbInterleaved[i + 2];

            // Spec lifting steps (Table D.6, swappedRBflag = 0):
            //   V = B - R
            //   t = R - G + Ceil(V/2)
            //   Y = G + Floor(t/2)
            //   U = -t
            int v = b - r;
            int t = r - g + Ceil2(v);
            int y = g + (t >> 1);   // Floor(t/2): arithmetic >> rounds toward -inf
            int u = -t;

            rgbInterleaved[i + 0] = y;
            rgbInterleaved[i + 1] = u;
            rgbInterleaved[i + 2] = v;
        }
    }

    /// <summary>
    /// Decoder direction (T.832 9.10.4.3 / Table 185): exact dual of
    /// <see cref="ForwardInPlace"/>. <c>{Y, U, V}</c> → <c>{R, G, B}</c>,
    /// in place.
    /// </summary>
    public static void InverseInPlace(Span<int> yuvInterleaved)
    {
        if (yuvInterleaved.Length % 3 != 0)
            throw new ArgumentException("buffer length must be divisible by 3", nameof(yuvInterleaved));

        for (var i = 0; i < yuvInterleaved.Length; i += 3)
        {
            int y = yuvInterleaved[i + 0];
            int u = yuvInterleaved[i + 1];
            int v = yuvInterleaved[i + 2];

            // Spec lifting steps (Table 185):
            //   t = -U
            //   G = Y - Floor(t/2)
            //   R = t + G - Ceil(V/2)
            //   B = V + R
            int t = -u;
            int g = y - (t >> 1);
            int r = t + g - Ceil2(v);
            int b = v + r;

            yuvInterleaved[i + 0] = r;
            yuvInterleaved[i + 1] = g;
            yuvInterleaved[i + 2] = b;
        }
    }

    /// <summary>Ceiling of <paramref name="x"/>/2 — i.e. <c>(x + 1) &gt;&gt; 1</c>
    /// where <c>&gt;&gt;</c> is signed arithmetic right shift.</summary>
    private static int Ceil2(int x) => (x + 1) >> 1;
}
