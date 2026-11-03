namespace SharpAstro.Png;

/// <summary>What happens to a frame's rectangle once its delay is over, before the next frame is drawn
/// (APNG <c>fcTL.dispose_op</c>).</summary>
public enum PngDisposeOp : byte
{
    /// <summary>The canvas is left as the frame drew it.</summary>
    None = 0,

    /// <summary>The frame's rectangle is cleared to fully transparent black.</summary>
    Background = 1,

    /// <summary>The frame's rectangle is put back to what it held before the frame was drawn. On the first
    /// frame the specification treats it as <see cref="Background"/>.</summary>
    Previous = 2,
}

/// <summary>How a frame's pixels are combined with the canvas under its rectangle
/// (APNG <c>fcTL.blend_op</c>).</summary>
public enum PngBlendOp : byte
{
    /// <summary>The frame's pixels, alpha included, replace the canvas's.</summary>
    Source = 0,

    /// <summary>The frame's pixels are alpha-composited over the canvas's.</summary>
    Over = 1,
}

/// <summary>
/// One animation frame's <c>fcTL</c> chunk: where its sub-image sits on the canvas, how long it shows, and what
/// is done with it afterwards. Every field is as stored, after validation against the image header: the rectangle
/// is non-empty and inside the canvas, and the two operations are ones the specification defines.
/// </summary>
/// <param name="SequenceNumber">The chunk's number in the shared <c>fcTL</c>/<c>fdAT</c> sequence, which runs 0, 1, 2, ... in file order.</param>
/// <param name="Width">Width of the frame's sub-image in pixels.</param>
/// <param name="Height">Height of the frame's sub-image in pixels.</param>
/// <param name="XOffset">Pixels from the canvas's left edge to the sub-image's left edge.</param>
/// <param name="YOffset">Pixels from the canvas's top edge to the sub-image's top edge.</param>
/// <param name="DelayNumerator">Numerator of the frame's display time, in seconds.</param>
/// <param name="DelayDenominator">Denominator of the frame's display time, in seconds; 0 stands for 100.</param>
/// <param name="DisposeOp">What happens to the frame's rectangle afterwards.</param>
/// <param name="BlendOp">How the frame is combined with the canvas.</param>
public sealed record PngFrameControl(
    int SequenceNumber,
    int Width,
    int Height,
    int XOffset,
    int YOffset,
    ushort DelayNumerator,
    ushort DelayDenominator,
    PngDisposeOp DisposeOp,
    PngBlendOp BlendOp)
{
    /// <summary>The frame's display time in seconds, with the specification's rule that a zero denominator means 100.</summary>
    public double DelaySeconds => DelayNumerator / (double)(DelayDenominator == 0 ? 100 : DelayDenominator);
}

/// <summary>
/// The animation a PNG declares with <c>acTL</c>, <c>fcTL</c> and <c>fdAT</c> chunks (APNG), parsed and validated
/// but not rendered: it says what the frames are, not what the canvas looks like after each. See
/// <see cref="PngImage.Animation"/>.
/// </summary>
/// <remarks>
/// <para>Reading the file as an ordinary PNG always gives the <em>default image</em>, the <c>IDAT</c> pixels in
/// <see cref="PngImage.Pixels"/>. When <see cref="DefaultImageIsFirstFrame"/> is true that image is also the first
/// frame (its <c>fcTL</c> precedes the <c>IDAT</c>); when it is false the default image is a cover shown by readers
/// that do not know APNG and every frame is in <c>fdAT</c> chunks.</para>
/// <para>A file that carries an <c>acTL</c> and then breaks the rules for the animation chunks is refused as a whole
/// (<see cref="InvalidDataException"/>), not handed back as its default image: see <see cref="PngReader.Decode"/>.
/// Chunks that make an animation up but have no <c>acTL</c> in front of the <c>IDAT</c> are not an animation, and are
/// skipped as unknown ancillary chunks always were.</para>
/// </remarks>
/// <param name="FrameCount">The <c>acTL</c>'s frame count, which equals <see cref="Frames"/>'s length once validated. 1 is legal: a still image that still carries APNG chunks.</param>
/// <param name="PlayCount">How many times the animation plays, as the 32-bit unsigned number <c>acTL</c> stores; 0 means for ever.</param>
/// <param name="DefaultImageIsFirstFrame">True when the <c>IDAT</c> image is the first frame, false when it is not part of the animation.</param>
/// <param name="Frames">One <see cref="PngFrameControl"/> per frame, in play order.</param>
public sealed record PngAnimation(
    int FrameCount,
    uint PlayCount,
    bool DefaultImageIsFirstFrame,
    IReadOnlyList<PngFrameControl> Frames)
{
    /// <summary>
    /// Where each frame's compressed data lies in the file the animation was read from, one list per frame, in the
    /// order of <see cref="Frames"/>. These are the <c>IDAT</c> payloads for the first frame when
    /// <see cref="DefaultImageIsFirstFrame"/> is true and otherwise <c>fdAT</c> payloads <em>after</em> their 4-byte
    /// sequence number, so a range is always plain zlib data to be joined in order.
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<(int Offset, int Length)>> FrameData { get; init; } = [];
}
