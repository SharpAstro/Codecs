namespace SharpAstro.Exr;

/// <summary>
/// One channel in an OpenEXR image (an entry of the <c>channels</c> / <c>chlist</c>
/// header attribute). Channels are stored and listed in case-sensitive ascending
/// name order (so an RGB image lists as B, G, R).
/// </summary>
public sealed class ExrChannel
{
    /// <summary>Channel name, e.g. <c>R</c>/<c>G</c>/<c>B</c>/<c>A</c> or <c>Y</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Sample type (HALF / FLOAT / UINT).</summary>
    public required ExrPixelType Type { get; init; }

    /// <summary>The <c>pLinear</c> hint (perceptually-linear sub-sampling). Almost always false.</summary>
    public bool PLinear { get; init; }

    /// <summary>Horizontal sub-sampling. 1 = full resolution (the only value this codec emits).</summary>
    public int XSampling { get; init; } = 1;

    /// <summary>Vertical sub-sampling. 1 = full resolution.</summary>
    public int YSampling { get; init; } = 1;

    /// <summary>Bytes per sample for this channel's <see cref="Type"/>.</summary>
    public int BytesPerSample => ExrFormat.BytesPerSample(Type);
}
