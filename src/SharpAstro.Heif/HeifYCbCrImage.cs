using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Heif;

/// <summary>
/// A decoded HEIF image as its Y'CbCr planes, before any colour conversion (<see cref="HeifDecoder.DecodeYCbCr"/>):
/// what a consumer converting elsewhere, on a GPU for one, needs, together with how
/// <see cref="HeifDecoder.Decode"/> would have converted it. <see cref="ToRgb"/> is that conversion, so the two
/// paths cannot drift apart unnoticed.
/// </summary>
/// <remarks>
/// <para>
/// The conversion, per pixel, in code units at <see cref="BitDepth"/> (H.273 8.3, as <see cref="HeifDecoder"/>
/// describes): chroma is upsampled by repeating each sample; at full range Y' is taken as is and the chroma is
/// centred on 2^(BitDepth-1); at narrow range Y' is expanded by (Y' - 16·2^(BitDepth-8)) · max / (219·2^(BitDepth-8))
/// and the chroma by (C - 128·2^(BitDepth-8)) · max / (224·2^(BitDepth-8)); then R' = Y' + 2(1 - Kr)Cr,
/// B' = Y' + 2(1 - Kb)Cb, G' = (Y' - Kr·R' - Kb·B') / (1 - Kr - Kb), each rounded half up and clamped to 0..max.
/// With the identity matrix R' = Cr, G' = Y', B' = Cb. A monochrome image has no chroma planes and every
/// matrix gives R' = G' = B' = Y' (expanded at narrow range).
/// </para>
/// </remarks>
public sealed class HeifYCbCrImage
{
    private readonly HeifDecoder.Colour _colour;

    internal HeifYCbCrImage(int width, int height, HeifDecoder.Colour colour, ushort[] luma, ushort[]? cb, ushort[]? cr,
        int chromaWidth, int chromaHeight, byte[]? icc)
    {
        _colour = colour;
        Width = width;
        Height = height;
        Luma = luma;
        Cb = cb;
        Cr = cr;
        ChromaWidth = chromaWidth;
        ChromaHeight = chromaHeight;
        IccProfile = icc;
        (ChromaShiftX, ChromaShiftY) = HeifDecoder.Subsampling(colour.ChromaFormatIdc);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bits per sample of every plane (8 to 12), and of the R'G'B' codes the conversion produces.</summary>
    public int BitDepth => _colour.BitDepth;

    /// <summary>H.265's chroma_format_idc: 0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4.</summary>
    public int ChromaFormatIdc => _colour.ChromaFormatIdc;

    /// <summary>log2 of the horizontal chroma subsampling: chroma sample x / 2^shift serves luma column x.</summary>
    public int ChromaShiftX { get; }

    /// <summary>log2 of the vertical chroma subsampling.</summary>
    public int ChromaShiftY { get; }

    /// <summary>Y', <see cref="Width"/> x <see cref="Height"/>, row-major, top-down.</summary>
    public ushort[] Luma { get; }

    /// <summary>Cb, <see cref="ChromaWidth"/> x <see cref="ChromaHeight"/>; null for a monochrome image.</summary>
    public ushort[]? Cb { get; }

    /// <summary>Cr, laid out as <see cref="Cb"/>; null for a monochrome image.</summary>
    public ushort[]? Cr { get; }

    public int ChromaWidth { get; }
    public int ChromaHeight { get; }

    /// <summary>The red luma weight of the matrix (0.2627 for BT.2020); 0 for the identity matrix.</summary>
    public double Kr => _colour.Kr;

    /// <summary>The blue luma weight of the matrix (0.0593 for BT.2020); 0 for the identity matrix.</summary>
    public double Kb => _colour.Kb;

    /// <summary>True for the identity matrix (GBR, 4:4:4 only).</summary>
    public bool IsIdentityMatrix => _colour.Identity;

    /// <summary>Whether the SAMPLES are full range; the R'G'B' the conversion produces always is.</summary>
    public bool IsFullRange => _colour.FullRange;

    /// <summary>What the converted R'G'B' is in: <see cref="HeifRgbImage.ColorEncoding"/> of the same image.</summary>
    public ColorEncoding ColorEncoding => _colour.Encoding;

    /// <summary>The image's ICC profile when the file carries one.</summary>
    public byte[]? IccProfile { get; }

    /// <summary>
    /// Linear light for each R'G'B' code 0 .. 2^<see cref="BitDepth"/> - 1, on the scale
    /// <see cref="HeifRgbImage.ToLinearRgbaFloat(out ColorEncoding)"/> uses (for PQ, 1.0 = 203 cd/m²): what that
    /// method looks every channel up in, so a converter using it reproduces it exactly.
    /// </summary>
    /// <exception cref="NotSupportedException">The transfer is HLG, whose OOTF couples the channels through their
    /// luminance and so is no per-channel table, or has no linearisation here.</exception>
    public float[] CodeToLinearTable()
    {
        var transfer = ColorEncoding.Transfer;
        if (transfer == TransferFunction.Hlg)
            throw new NotSupportedException("HLG is not a per-channel transfer (its OOTF mixes the channels); convert with HeifRgbImage.ToLinearRgbaFloat.");
        if (!HdrTransfer.IsSupportedTransfer(transfer))
            throw new NotSupportedException($"Transfer characteristics {(int)transfer} ({transfer}) has no float linearisation.");
        var table = new float[1 << BitDepth];
        HdrTransfer.BuildLinearTable(transfer, ColorEncoding.Primaries, BitDepth, fullRange: true, table);
        return table;
    }

    /// <summary>
    /// The planes converted on the CPU: the <see cref="HeifRgbImage"/> <see cref="HeifDecoder.Decode"/> returns for
    /// the same item, sample for sample.
    /// </summary>
    public HeifRgbImage ToRgb()
    {
        var max = (1 << BitDepth) - 1;
        var mid = 1 << (BitDepth - 1);
        var canvas = new ushort[(long)Width * Height * 3];
        Parallel.For(0, Height, y =>
        {
            var output = (long)y * Width * 3;
            var chromaRow = (y >> ChromaShiftY) * ChromaWidth;
            for (var x = 0; x < Width; x++, output += 3)
            {
                var luma = Luma[y * Width + x];
                int cb = mid, cr = mid;
                if (Cb is { } cbPlane && Cr is { } crPlane)
                {
                    var c = chromaRow + (x >> ChromaShiftX);
                    (cb, cr) = (cbPlane[c], crPlane[c]);
                }
                _colour.ToRgb(luma, cb, cr, max, out canvas[output], out canvas[output + 1], out canvas[output + 2]);
            }
        });
        return new HeifRgbImage(Width, Height, BitDepth, canvas, ColorEncoding, IccProfile);
    }
}
