namespace SharpAstro.Heif;

/// <summary>
/// The <c>hvcC</c> property: an HEVC decoder configuration record (ISO/IEC 14496-15 8.3.3.1). It
/// summarises the stream's profile, tier, level, chroma format and bit depths, says how many bytes
/// precede each NAL unit in the item data, and carries the parameter-set NAL units (VPS, SPS, PPS, and
/// sometimes SEI) the coded image needs.
/// <para>
/// The summary fields are what the encoder <em>declared</em>; the SPS inside <see cref="NalUnitArrays"/>
/// is what a decoder obeys. They should agree, and the parameter-set parser checks that they do.
/// </para>
/// </summary>
public sealed record HevcDecoderConfiguration : HeifProperty
{
    public HevcDecoderConfiguration() : base("hvcC") { }

    /// <summary><c>general_profile_space</c> (0 for every profile H.265 defines).</summary>
    public required int ProfileSpace { get; init; }

    /// <summary><c>general_tier_flag</c>: false Main tier, true High tier.</summary>
    public required bool HighTier { get; init; }

    /// <summary><c>general_profile_idc</c>: 1 Main, 2 Main 10, 3 Main Still Picture, 4 Format Range Extensions, ...</summary>
    public required int ProfileIdc { get; init; }

    public required uint ProfileCompatibilityFlags { get; init; }

    /// <summary>The 48 constraint-indicator bits, most significant first in the low 48 bits.</summary>
    public required ulong ConstraintIndicatorFlags { get; init; }

    /// <summary><c>general_level_idc</c>: 30 times the level number (level 5.1 is 153).</summary>
    public required int LevelIdc { get; init; }

    public required HevcChromaFormat ChromaFormat { get; init; }

    public required int BitDepthLuma { get; init; }

    public required int BitDepthChroma { get; init; }

    /// <summary>Bytes in the big-endian length that precedes every NAL unit in the item data: 1, 2 or 4.</summary>
    public required int LengthSize { get; init; }

    /// <summary>The parameter-set and SEI NAL units, grouped by NAL unit type, each one without its length prefix.</summary>
    public required IReadOnlyList<HevcNalUnitArray> NalUnitArrays { get; init; }

    /// <summary>The NAL units of <paramref name="nalUnitType"/> (32 VPS, 33 SPS, 34 PPS, 39/40 SEI).</summary>
    public IEnumerable<byte[]> NalUnitsOfType(int nalUnitType) =>
        NalUnitArrays.Where(a => a.NalUnitType == nalUnitType).SelectMany(a => a.NalUnits);
}

/// <summary>One array of an <c>hvcC</c>: NAL units of a single type.</summary>
public sealed record HevcNalUnitArray(int NalUnitType, bool Complete, IReadOnlyList<byte[]> NalUnits);
