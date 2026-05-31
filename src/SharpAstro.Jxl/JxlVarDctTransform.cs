namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT per-block transform types ("DctSelect", ISO/IEC 18181-1 §J.2), in the exact spec
/// ordinal order so the numeric values match the bitstream. A block picks one of these; the type
/// fixes the covered area (in 8×8 blocks), the DCT shape, and which dequant matrix applies.
/// </summary>
internal enum JxlVarDctTransform
{
    Dct8 = 0,
    Hornuss,
    Dct2,
    Dct4,
    Dct16,
    Dct32,
    Dct16x8,
    Dct8x16,
    Dct32x8,
    Dct8x32,
    Dct32x16,
    Dct16x32,
    Dct4x8,
    Dct8x4,
    Afv0,
    Afv1,
    Afv2,
    Afv3,
    Dct64,
    Dct64x32,
    Dct32x64,
    Dct128,
    Dct128x64,
    Dct64x128,
    Dct256,
    Dct256x128,
    Dct128x256,
}

internal static class JxlVarDctTransformExtensions
{
    /// <summary>Covered area in 8×8 blocks: (width, height).</summary>
    public static (int W, int H) DctSelectSize(this JxlVarDctTransform t) => t switch
    {
        JxlVarDctTransform.Dct8 or JxlVarDctTransform.Hornuss or JxlVarDctTransform.Dct2
            or JxlVarDctTransform.Dct4 or JxlVarDctTransform.Dct4x8 or JxlVarDctTransform.Dct8x4
            or JxlVarDctTransform.Afv0 or JxlVarDctTransform.Afv1 or JxlVarDctTransform.Afv2
            or JxlVarDctTransform.Afv3 => (1, 1),
        JxlVarDctTransform.Dct16 => (2, 2),
        JxlVarDctTransform.Dct32 => (4, 4),
        JxlVarDctTransform.Dct16x8 => (1, 2),
        JxlVarDctTransform.Dct8x16 => (2, 1),
        JxlVarDctTransform.Dct32x8 => (1, 4),
        JxlVarDctTransform.Dct8x32 => (4, 1),
        JxlVarDctTransform.Dct32x16 => (2, 4),
        JxlVarDctTransform.Dct16x32 => (4, 2),
        JxlVarDctTransform.Dct64 => (8, 8),
        JxlVarDctTransform.Dct64x32 => (4, 8),
        JxlVarDctTransform.Dct32x64 => (8, 4),
        JxlVarDctTransform.Dct128 => (16, 16),
        JxlVarDctTransform.Dct128x64 => (8, 16),
        JxlVarDctTransform.Dct64x128 => (16, 8),
        JxlVarDctTransform.Dct256 => (32, 32),
        JxlVarDctTransform.Dct256x128 => (16, 32),
        JxlVarDctTransform.Dct128x256 => (32, 16),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Dequant-matrix dimensions in samples: (width, height).</summary>
    public static (int W, int H) DequantMatrixSize(this JxlVarDctTransform t) => t switch
    {
        JxlVarDctTransform.Dct8 or JxlVarDctTransform.Hornuss or JxlVarDctTransform.Dct2
            or JxlVarDctTransform.Dct4 or JxlVarDctTransform.Dct4x8 or JxlVarDctTransform.Dct8x4
            or JxlVarDctTransform.Afv0 or JxlVarDctTransform.Afv1 or JxlVarDctTransform.Afv2
            or JxlVarDctTransform.Afv3 => (8, 8),
        JxlVarDctTransform.Dct16 => (16, 16),
        JxlVarDctTransform.Dct32 => (32, 32),
        JxlVarDctTransform.Dct16x8 or JxlVarDctTransform.Dct8x16 => (16, 8),
        JxlVarDctTransform.Dct32x8 or JxlVarDctTransform.Dct8x32 => (32, 8),
        JxlVarDctTransform.Dct32x16 or JxlVarDctTransform.Dct16x32 => (32, 16),
        JxlVarDctTransform.Dct64 => (64, 64),
        JxlVarDctTransform.Dct64x32 or JxlVarDctTransform.Dct32x64 => (64, 32),
        JxlVarDctTransform.Dct128 => (128, 128),
        JxlVarDctTransform.Dct128x64 or JxlVarDctTransform.Dct64x128 => (128, 64),
        JxlVarDctTransform.Dct256 => (256, 256),
        JxlVarDctTransform.Dct256x128 or JxlVarDctTransform.Dct128x256 => (256, 128),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Index (0…16) into the dequant-matrix set; sizes/aspect-flips share an index.</summary>
    public static int DequantMatrixParamIndex(this JxlVarDctTransform t) => t switch
    {
        JxlVarDctTransform.Dct8 => 0,
        JxlVarDctTransform.Hornuss => 1,
        JxlVarDctTransform.Dct2 => 2,
        JxlVarDctTransform.Dct4 => 3,
        JxlVarDctTransform.Dct16 => 4,
        JxlVarDctTransform.Dct32 => 5,
        JxlVarDctTransform.Dct16x8 or JxlVarDctTransform.Dct8x16 => 6,
        JxlVarDctTransform.Dct32x8 or JxlVarDctTransform.Dct8x32 => 7,
        JxlVarDctTransform.Dct32x16 or JxlVarDctTransform.Dct16x32 => 8,
        JxlVarDctTransform.Dct4x8 or JxlVarDctTransform.Dct8x4 => 9,
        JxlVarDctTransform.Afv0 or JxlVarDctTransform.Afv1 or JxlVarDctTransform.Afv2
            or JxlVarDctTransform.Afv3 => 10,
        JxlVarDctTransform.Dct64 => 11,
        JxlVarDctTransform.Dct64x32 or JxlVarDctTransform.Dct32x64 => 12,
        JxlVarDctTransform.Dct128 => 13,
        JxlVarDctTransform.Dct128x64 or JxlVarDctTransform.Dct64x128 => 14,
        JxlVarDctTransform.Dct256 => 15,
        JxlVarDctTransform.Dct256x128 or JxlVarDctTransform.Dct128x256 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Coefficient-order group id (0…12) used to pick the natural coefficient order.</summary>
    public static int OrderId(this JxlVarDctTransform t) => t switch
    {
        JxlVarDctTransform.Dct8 => 0,
        JxlVarDctTransform.Hornuss or JxlVarDctTransform.Dct2 or JxlVarDctTransform.Dct4
            or JxlVarDctTransform.Dct4x8 or JxlVarDctTransform.Dct8x4 or JxlVarDctTransform.Afv0
            or JxlVarDctTransform.Afv1 or JxlVarDctTransform.Afv2 or JxlVarDctTransform.Afv3 => 1,
        JxlVarDctTransform.Dct16 => 2,
        JxlVarDctTransform.Dct32 => 3,
        JxlVarDctTransform.Dct16x8 or JxlVarDctTransform.Dct8x16 => 4,
        JxlVarDctTransform.Dct32x8 or JxlVarDctTransform.Dct8x32 => 5,
        JxlVarDctTransform.Dct32x16 or JxlVarDctTransform.Dct16x32 => 6,
        JxlVarDctTransform.Dct64 => 7,
        JxlVarDctTransform.Dct64x32 or JxlVarDctTransform.Dct32x64 => 8,
        JxlVarDctTransform.Dct128 => 9,
        JxlVarDctTransform.Dct128x64 or JxlVarDctTransform.Dct64x128 => 10,
        JxlVarDctTransform.Dct256 => 11,
        JxlVarDctTransform.Dct256x128 or JxlVarDctTransform.Dct128x256 => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Whether the decoded DCT coefficients are stored transposed (tall blocks).</summary>
    public static bool NeedTranspose(this JxlVarDctTransform t)
    {
        switch (t)
        {
            case JxlVarDctTransform.Hornuss:
            case JxlVarDctTransform.Dct2:
            case JxlVarDctTransform.Dct4:
            case JxlVarDctTransform.Dct4x8:
            case JxlVarDctTransform.Dct8x4:
            case JxlVarDctTransform.Afv0:
            case JxlVarDctTransform.Afv1:
            case JxlVarDctTransform.Afv2:
            case JxlVarDctTransform.Afv3:
                return false;
            default:
                (int w, int h) = t.DctSelectSize();
                return h >= w;
        }
    }
}
