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

    /// <summary>
    /// Runs the selected kernel. <paramref name="inNear"/>/<paramref name="inFar"/>
    /// are row offsets into <paramref name="plane"/>; <paramref name="w"/> is the
    /// low-resolution sample count; <paramref name="hs"/> the horizontal expansion.
    /// </summary>
    public static void Row(int kind, Span<byte> dst, ReadOnlySpan<byte> plane, int inNear, int inFar, int w, int hs)
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
}
