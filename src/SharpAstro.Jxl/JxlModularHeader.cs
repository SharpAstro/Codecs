namespace SharpAstro.Jxl;

/// <summary>Self-correcting (weighted) predictor parameters (ISO/IEC 18181-1 §H.3, jxl-modular).</summary>
internal readonly struct JxlWpHeader
{
    public required int P1 { get; init; }
    public required int P2 { get; init; }
    public required int P3a { get; init; }
    public required int P3b { get; init; }
    public required int P3c { get; init; }
    public required int P3d { get; init; }
    public required int P3e { get; init; }
    public required int W0 { get; init; }
    public required int W1 { get; init; }
    public required int W2 { get; init; }
    public required int W3 { get; init; }

    public static JxlWpHeader Default => new()
    {
        P1 = 16, P2 = 10, P3a = 7, P3b = 7, P3c = 7, P3d = 0, P3e = 0,
        W0 = 13, W1 = 12, W2 = 12, W3 = 12,
    };

    public static JxlWpHeader Parse(ref JxlBitReader br)
    {
        if (br.ReadBit()) // default_wp
            return Default;
        return new JxlWpHeader
        {
            P1 = (int)br.ReadBits(5),
            P2 = (int)br.ReadBits(5),
            P3a = (int)br.ReadBits(5),
            P3b = (int)br.ReadBits(5),
            P3c = (int)br.ReadBits(5),
            P3d = (int)br.ReadBits(5),
            P3e = (int)br.ReadBits(5),
            W0 = (int)br.ReadBits(4),
            W1 = (int)br.ReadBits(4),
            W2 = (int)br.ReadBits(4),
            W3 = (int)br.ReadBits(4),
        };
    }
}

internal enum JxlTransformType { Rct = 0, Palette = 1, Squeeze = 2 }

internal readonly struct JxlSqueezeParam
{
    public required bool Horizontal { get; init; }
    public required bool InPlace { get; init; }
    public required int BeginC { get; init; }
    public required int NumC { get; init; }
}

/// <summary>A Modular transform descriptor (RCT / Palette / Squeeze), ISO/IEC 18181-1 §H.6.</summary>
internal sealed class JxlTransform
{
    public required JxlTransformType Type { get; init; }
    public int BeginC { get; init; }
    public int RctType { get; init; }
    public int NumC { get; init; }
    public int NbColours { get; init; }
    public int NbDeltas { get; init; }
    public JxlPredictor DPred { get; init; }
    public JxlWpHeader DPredWp { get; init; }
    public JxlSqueezeParam[] SqueezeParams { get; init; } = [];

    private static uint BeginChannel(ref JxlBitReader br) => br.ReadU32((0, 3), (8, 6), (72, 10), (1096, 13));

    public static JxlTransform Parse(ref JxlBitReader br)
    {
        var type = (JxlTransformType)br.ReadBits(2);
        switch (type)
        {
            case JxlTransformType.Rct:
                return new JxlTransform
                {
                    Type = type,
                    BeginC = (int)BeginChannel(ref br),
                    RctType = (int)br.ReadU32((6, 0), (0, 2), (2, 4), (10, 6)),
                };

            case JxlTransformType.Palette:
                int beginC = (int)BeginChannel(ref br);
                int numC = (int)br.ReadU32((1, 0), (3, 0), (4, 0), (1, 13));
                int nbColours = (int)br.ReadU32((0, 8), (256, 10), (1280, 12), (5376, 16));
                int nbDeltas = (int)br.ReadU32((0, 0), (1, 8), (257, 10), (1281, 16));
                var dPred = (JxlPredictor)br.ReadBits(4);
                if ((uint)dPred > 13)
                    throw new InvalidDataException($"JPEG XL invalid palette predictor {(int)dPred}.");
                JxlWpHeader dWp = dPred == JxlPredictor.SelfCorrecting ? JxlWpHeader.Parse(ref br) : JxlWpHeader.Default;
                return new JxlTransform
                {
                    Type = type, BeginC = beginC, NumC = numC, NbColours = nbColours,
                    NbDeltas = nbDeltas, DPred = dPred, DPredWp = dWp,
                };

            case JxlTransformType.Squeeze:
                int numSq = (int)br.ReadU32((0, 0), (1, 4), (9, 6), (41, 8));
                var sp = new JxlSqueezeParam[numSq];
                for (int i = 0; i < numSq; i++)
                    sp[i] = new JxlSqueezeParam
                    {
                        Horizontal = br.ReadBit(),
                        InPlace = br.ReadBit(),
                        BeginC = (int)BeginChannel(ref br),
                        NumC = (int)br.ReadU32((1, 0), (2, 0), (3, 0), (4, 4)),
                    };
                return new JxlTransform { Type = type, SqueezeParams = sp };

            default:
                throw new InvalidDataException("JPEG XL invalid transform id.");
        }
    }
}

/// <summary>
/// JPEG XL Modular local header (ISO/IEC 18181-1 §H.2): the use-global-tree flag, weighted-
/// predictor parameters, and the transform list. The MA tree (local or global) follows.
/// </summary>
internal sealed class JxlModularHeader
{
    public required bool UseGlobalTree { get; init; }
    public required JxlWpHeader Wp { get; init; }
    public required JxlTransform[] Transforms { get; init; }

    public static JxlModularHeader Parse(ref JxlBitReader br)
    {
        bool useGlobalTree = br.ReadBit();
        JxlWpHeader wp = JxlWpHeader.Parse(ref br);
        uint nbTransforms = br.ReadU32((0, 0), (1, 0), (2, 4), (18, 8));
        if (nbTransforms > 512)
            throw new InvalidDataException("JPEG XL too many Modular transforms.");
        var transforms = new JxlTransform[nbTransforms];
        for (int i = 0; i < nbTransforms; i++)
            transforms[i] = JxlTransform.Parse(ref br);
        return new JxlModularHeader { UseGlobalTree = useGlobalTree, Wp = wp, Transforms = transforms };
    }
}
