using SharpAstro.GainMap;

// Deliberately still SharpAstro.Jpeg: the type moved here from SharpAstro.Jpeg.GainMap, which
// forwards it by full name (TypeForwardedTo), so existing consumers keep compiling and linking.
namespace SharpAstro.Jpeg;

/// <summary>
/// Gain-map parameters — the Adobe hdrgm 1.0 vocabulary, which is also
/// what Android Ultra HDR v1 carries and what ISO/TS 21496-1 standardized.
/// <para>
/// Every ratio field here is a <b>linear</b> value (matching libultrahdr's public
/// metadata struct); the XMP serialization stores <see cref="GainMapMin"/>,
/// <see cref="GainMapMax"/>, <see cref="HdrCapacityMin"/> and
/// <see cref="HdrCapacityMax"/> as log2 — <see cref="GainMapXmp"/> converts on
/// the way in and out. <see cref="Gamma"/> and the offsets are linear in both.
/// </para>
/// <para>
/// Reconstruction (SDR base form, per pixel): <c>recovery = code / 255</c>,
/// <c>logRecovery = lerp(log2(GainMapMin), log2(GainMapMax), recovery^(1/Gamma))</c>,
/// <c>HDR = (SDR_linear + OffsetSdr) · 2^(logRecovery · W) − OffsetHdr</c>, where
/// <c>W</c> ramps 0→1 as the display's linear headroom goes
/// <see cref="HdrCapacityMin"/>→<see cref="HdrCapacityMax"/>.
/// </para>
/// <para>
/// <b>Per channel.</b> ISO/TS 21496-1 lets min, max, gamma and both offsets differ per colour
/// channel. The scalar properties are the one-channel case; when <see cref="Channels"/> is set
/// (three entries, R, G, B) it carries the real per-channel values and the scalar properties
/// mirror channel 0 so a scalar-only consumer still sees something sensible. Read parameters
/// through <see cref="GetChannel"/> and both cases work. Record equality compares
/// <see cref="Channels"/> by reference.
/// </para>
/// </summary>
public sealed record GainMapMetadata
{
    /// <summary>Linear gain encoded by code 0 (hdrgm default: 1.0 — no attenuation).</summary>
    public double GainMapMin { get; init; } = 1.0;

    /// <summary>Linear gain encoded by code 255 — the content's max boost. Required by the format.</summary>
    public required double GainMapMax { get; init; }

    /// <summary>Encoding gamma applied to the normalized recovery value (default 1.0).</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>Offset added to the SDR linear signal before the ratio (default 1/64, avoids log-of-zero).</summary>
    public double OffsetSdr { get; init; } = 1.0 / 64;

    /// <summary>Offset subtracted from the boosted signal (default 1/64; equal offsets make W=0 reproduce the base exactly).</summary>
    public double OffsetHdr { get; init; } = 1.0 / 64;

    /// <summary>Linear display headroom at/below which the base is shown unmodified (default 1.0 = SDR).</summary>
    public double HdrCapacityMin { get; init; } = 1.0;

    /// <summary>Linear display headroom at/above which the full gain map is applied. Required by the format.</summary>
    public required double HdrCapacityMax { get; init; }

    /// <summary>True when the <em>base</em> rendition is the HDR one (Adobe allows it; Ultra HDR
    /// and this package's write path always use the SDR-base form, i.e. false).</summary>
    public bool BaseRenditionIsHdr { get; init; }

    /// <summary>Per-channel parameters (exactly 3 entries: R, G, B), or null for the scalar
    /// one-channel case. When set, the scalar min/max/gamma/offset properties mirror channel 0.</summary>
    public IReadOnlyList<GainMapChannel>? Channels { get; init; }

    /// <summary>
    /// ISO/TS 21496-1 <c>use_base_colour_space</c>: true (the default, and the only form
    /// Adobe/Ultra HDR have) applies the gain in the base image's colour space; false applies it
    /// in the alternate image's. Recorded only — this package never converts between colour
    /// spaces, the caller that knows both gamuts does.
    /// </summary>
    public bool UseBaseColourSpace { get; init; } = true;

    /// <summary>Number of parameter channels: 1 (scalar) or 3.</summary>
    public int ChannelCount => Channels?.Count ?? 1;

    /// <summary>Linear headroom of the baseline rendition (ISO <c>baseline_hdr_headroom</c>, as a
    /// ratio); the SDR-base form's <see cref="HdrCapacityMin"/>.</summary>
    public double BaselineHdrHeadroom => HdrCapacityMin;

    /// <summary>Linear headroom of the alternate rendition (ISO <c>alternate_hdr_headroom</c>, as a
    /// ratio); the SDR-base form's <see cref="HdrCapacityMax"/>.</summary>
    public double AlternateHdrHeadroom => HdrCapacityMax;

    /// <summary>The parameters of one colour channel. A scalar record (or a one-entry
    /// <see cref="Channels"/>) answers every index with its single set.</summary>
    public GainMapChannel GetChannel(int channel)
    {
        if (Channels is { Count: > 1 } channels)
            return channels[channel];
        if (Channels is { Count: 1 } one)
            return one[0];
        return new GainMapChannel(GainMapMin, GainMapMax, Gamma, OffsetSdr, OffsetHdr);
    }

    /// <summary>The gain-application weight W ∈ [0,1] for a display headroom — the log-space
    /// position between <see cref="HdrCapacityMin"/> and <see cref="HdrCapacityMax"/>.</summary>
    public double Weight(double displayHeadroom)
    {
        var logMin = Math.Log2(HdrCapacityMin);
        var logMax = Math.Log2(HdrCapacityMax);
        return Math.Clamp((Math.Log2(displayHeadroom) - logMin) / (logMax - logMin), 0, 1);
    }

    /// <summary>Throws when the parameters are unusable for encode/reconstruction.</summary>
    public void Validate()
    {
        if (Channels is { } channels)
        {
            if (channels.Count is not (1 or 3))
                throw new InvalidOperationException($"Per-channel gain-map parameters must have 1 or 3 channels (got {channels.Count}).");
            foreach (var channel in channels)
                channel.Validate();
        }

        if (!(GainMapMin > 0) || !(GainMapMax > 0) || GainMapMax < GainMapMin)
            throw new InvalidOperationException($"Gain map bounds must satisfy 0 < GainMapMin <= GainMapMax (got {GainMapMin}..{GainMapMax}).");
        if (!(Gamma > 0))
            throw new InvalidOperationException($"Gamma must be positive (got {Gamma}).");
        if (!(OffsetSdr >= 0) || !(OffsetHdr >= 0))
            throw new InvalidOperationException($"Offsets must be non-negative (got SDR {OffsetSdr}, HDR {OffsetHdr}).");
        if (!(HdrCapacityMin >= 1) || !(HdrCapacityMax > HdrCapacityMin))
            throw new InvalidOperationException($"Capacity must satisfy 1 <= HdrCapacityMin < HdrCapacityMax (got {HdrCapacityMin}..{HdrCapacityMax}).");
    }
}
