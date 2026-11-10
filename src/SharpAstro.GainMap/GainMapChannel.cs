namespace SharpAstro.GainMap;

/// <summary>
/// The parameters that may differ per colour channel in ISO/TS 21496-1 (and the
/// per-channel <c>rdf:Seq</c> form of Adobe hdrgm): gain bounds, gamma and the two
/// offsets. All values are <b>linear</b>, the same convention as
/// <see cref="SharpAstro.Jpeg.GainMapMetadata"/>.
/// </summary>
public readonly record struct GainMapChannel(double GainMapMin, double GainMapMax, double Gamma, double OffsetSdr, double OffsetHdr)
{
    /// <summary>Throws when the parameters are unusable for reconstruction.</summary>
    public void Validate()
    {
        if (!(GainMapMin > 0) || !(GainMapMax > 0) || GainMapMax < GainMapMin)
            throw new InvalidOperationException($"Gain map bounds must satisfy 0 < GainMapMin <= GainMapMax (got {GainMapMin}..{GainMapMax}).");
        if (!(Gamma > 0))
            throw new InvalidOperationException($"Gamma must be positive (got {Gamma}).");
        if (!(OffsetSdr >= 0) || !(OffsetHdr >= 0))
            throw new InvalidOperationException($"Offsets must be non-negative (got SDR {OffsetSdr}, HDR {OffsetHdr}).");
    }
}
