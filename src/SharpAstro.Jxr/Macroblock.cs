namespace SharpAstro.Jxr;

/// <summary>
/// One macroblock's coefficient state — the C# analogue of jxrlib's per-MB slice
/// of <c>CWMImageStrCodec</c> (<c>MBInfo</c> + <c>pPlane[]</c>). For each channel:
/// <see cref="BlockDc"/>[ch][0] is the MB DC, [1..15] the second-stage lowpass (AD)
/// coefficients, and <see cref="Plane"/>[ch] is the 256-int highpass coefficient
/// plane addressed via <see cref="MacroblockLayout.BlkOffset"/>. <see cref="Cbp"/> /
/// <see cref="DiffCbp"/> hold the HP coded-block pattern and its prediction residual.
/// </summary>
internal sealed class Macroblock
{
    public readonly int Channels;
    public readonly int[][] Plane;    // [ch][256] highpass coefficients
    public readonly int[][] BlockDc;  // [ch][16] DC (0) + lowpass AD (1..15)
    public readonly int[] Cbp;        // [ch] highpass coded-block pattern (16 bits)
    public readonly int[] DiffCbp;    // [ch] CBP prediction residual (transmitted)

    /// <summary>0 selects the horizontal HP scan, 1 the vertical (jxrlib <c>iOrientation</c>).</summary>
    public int Orientation;

    public Macroblock(int channels)
    {
        Channels = channels;
        Plane = new int[channels][];
        BlockDc = new int[channels][];
        Cbp = new int[channels];
        DiffCbp = new int[channels];
        for (var c = 0; c < channels; c++)
        {
            Plane[c] = new int[MacroblockLayout.PlaneSize];
            BlockDc[c] = new int[16];
        }
    }
}
