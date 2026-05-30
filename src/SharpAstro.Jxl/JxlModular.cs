namespace SharpAstro.Jxl;

/// <summary>Shared helpers for the JPEG XL Modular subsystem (ISO/IEC 18181-1 §H).</summary>
internal static class JxlModular
{
    /// <summary>
    /// Zigzag decode used throughout Modular (tree split values/offsets, sample residuals):
    /// 0→0, 1→-1, 2→1, 3→-2, … Faithful port of jxl-bitstream <c>unpack_signed</c>.
    /// </summary>
    public static int UnpackSigned(uint u) => (int)(u >> 1) ^ -(int)(u & 1);

    /// <summary>
    /// Clamped gradient: <c>clamp(n + w - nw, min(n,w), max(n,w))</c> (i64 intermediate to avoid
    /// overflow). Used by the Gradient predictor and the cross-channel error properties.
    /// </summary>
    public static int GradClamped(int n, int w, int nw)
    {
        long lo = Math.Min(n, w);
        long hi = Math.Max(n, w);
        return (int)Math.Clamp((long)n + w - nw, lo, hi);
    }
}
