namespace SharpAstro.Jxr;

/// <summary>
/// Reversible YCoCg-R color transform (T.832 §9.6.2.7 / Table 158).
/// Lossless lifting that maps integer R/G/B → Y/Co/Cg and back. JXR uses
/// this when <c>INTERNAL_CLR_FMT = YUV444</c> (and the chroma-subsampled
/// variants) so the codestream operates on a decorrelated luma + two
/// chroma channels rather than three equal RGB channels.
/// </summary>
/// <remarks>
/// <para>Output range expansion: for n-bit signed input, Co and Cg become
/// (n+1)-bit signed (their absolute range can reach 2× input range). Y
/// stays in the input's bit range. The integer-FCT pipeline downstream
/// has more than enough headroom for this widening.</para>
/// <para>The transform is order-sensitive — apply <see cref="ForwardInPlace"/>
/// on the encoder side BEFORE the FCT cascade, and <see cref="InverseInPlace"/>
/// on the decoder side AFTER the ICT cascade. Both operate on a flat
/// interleaved <c>RGB RGB ...</c> buffer of signed ints, in place.</para>
/// </remarks>
public static class YCoCgTransform
{
    /// <summary>
    /// Encoder direction: convert each RGB triple in
    /// <paramref name="rgbInterleaved"/> to the (Y, Co, Cg) representation,
    /// in place. Layout stays interleaved — what was <c>[R, G, B]</c>
    /// becomes <c>[Y, Co, Cg]</c> at the same offsets.
    /// </summary>
    /// <param name="rgbInterleaved">
    /// Signed-int buffer of length <c>3 × pixelCount</c>, samples in <c>R G B R G B ...</c> order.
    /// </param>
    public static void ForwardInPlace(Span<int> rgbInterleaved)
    {
        if (rgbInterleaved.Length % 3 != 0)
            throw new ArgumentException("buffer length must be divisible by 3", nameof(rgbInterleaved));

        for (var i = 0; i < rgbInterleaved.Length; i += 3)
        {
            // T.832 9.6.2.7 — exact lifting in this order. Each step
            // overwrites only the variable on the left; >> on negative
            // values is arithmetic (which the spec assumes).
            int r = rgbInterleaved[i + 0];
            int g = rgbInterleaved[i + 1];
            int b = rgbInterleaved[i + 2];

            int co = r - b;
            int t  = b + (co >> 1);
            int cg = g - t;
            int y  = t + (cg >> 1);

            // Write back as (Y, Co, Cg) — same channel order convention as
            // the JXR YUV444 InternalClrFmt (component 0 = luma, 1 = Co, 2 = Cg).
            rgbInterleaved[i + 0] = y;
            rgbInterleaved[i + 1] = co;
            rgbInterleaved[i + 2] = cg;
        }
    }

    /// <summary>
    /// Decoder direction: exact dual of <see cref="ForwardInPlace"/>.
    /// Converts <c>[Y, Co, Cg]</c> interleaved triples back to
    /// <c>[R, G, B]</c>, in place. ICT(FCT(x)) = x and
    /// Inverse(Forward(rgb)) = rgb, both bit-exact.
    /// </summary>
    public static void InverseInPlace(Span<int> yCoCgInterleaved)
    {
        if (yCoCgInterleaved.Length % 3 != 0)
            throw new ArgumentException("buffer length must be divisible by 3", nameof(yCoCgInterleaved));

        for (var i = 0; i < yCoCgInterleaved.Length; i += 3)
        {
            int y  = yCoCgInterleaved[i + 0];
            int co = yCoCgInterleaved[i + 1];
            int cg = yCoCgInterleaved[i + 2];

            int t = y  - (cg >> 1);
            int g = cg + t;
            int b = t  - (co >> 1);
            int r = b  + co;

            yCoCgInterleaved[i + 0] = r;
            yCoCgInterleaved[i + 1] = g;
            yCoCgInterleaved[i + 2] = b;
        }
    }
}
