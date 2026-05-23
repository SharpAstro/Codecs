namespace SharpAstro.Jxr;

/// <summary>
/// Per-MB payload passed between the transform/prediction pipeline and the
/// codestream orchestrator <see cref="TileSpatial"/>. Holds the
/// post-prediction coefficients for one macroblock; <em>not</em> raw pixel
/// data — colour conversion, FCT/POT, and DC/LP/HP prediction must have
/// already produced these values upstream.
/// </summary>
/// <remarks>
/// <para>Storage layout (all arrays are component-major, then position):</para>
/// <list type="bullet">
///   <item><see cref="Dc"/>: <c>numComponents</c> super-DC values.</item>
///   <item><see cref="Lp"/>: <c>numComponents × 16</c> LP coefficients (position 0 is
///         the super-DC slot and ignored; positions 1..15 carry the 15 LP coefficients).
///         Used only when BANDS_PRESENT is not DcOnly.</item>
///   <item><see cref="Hp"/>: <c>numComponents × 256</c> HP coefficients (16 blocks of
///         16 coefficients each per component). Used only when HP band is present.</item>
///   <item><see cref="MbHpMode"/>: 0=horizontal-dominant, 1=vertical-dominant adaptive
///         scan (T.832 §8.7.18). Computed by upstream block analysis.</item>
/// </list>
/// </remarks>
public sealed class Macroblock
{
    public int[] Dc = [];
    public int[] Lp = [];
    public int[] Hp = [];
    public int MbHpMode;
}
