using System.Collections.Immutable;
using System.Numerics;

namespace SharpAstro.VectorGraphics;

/// <summary>
/// How a layer's colour is mixed with the colour of what is below it, where both are there. The names and the
/// arithmetic are those of the W3C Compositing and Blending specification: with <c>Cb</c> the colour below and
/// <c>Cs</c> the layer's, both not premultiplied, the mixed colour is <c>B(Cb, Cs)</c>, and the result is
/// <c>Cs * (1 - ab) + B(Cb, Cs) * ab</c> composited over what is below in the usual way. Where nothing is below
/// (<c>ab</c> is zero) every mode therefore gives the layer as it is.
/// </summary>
public enum VectorBlendMode : byte
{
    /// <summary>The layer's own colour: <c>B = Cs</c>.</summary>
    Normal,
    /// <summary><c>Cb * Cs</c>.</summary>
    Multiply,
    /// <summary><c>Cb + Cs - Cb * Cs</c>.</summary>
    Screen,
    /// <summary><see cref="HardLight"/> with the two colours changed over.</summary>
    Overlay,
    /// <summary>The smaller of the two, channel by channel.</summary>
    Darken,
    /// <summary>The larger of the two, channel by channel.</summary>
    Lighten,
    /// <summary><c>min(1, Cb / (1 - Cs))</c>; 0 where <c>Cb</c> is 0, 1 where <c>Cs</c> is 1.</summary>
    ColorDodge,
    /// <summary><c>1 - min(1, (1 - Cb) / Cs)</c>; 1 where <c>Cb</c> is 1, 0 where <c>Cs</c> is 0.</summary>
    ColorBurn,
    /// <summary><see cref="Multiply"/> with <c>2 Cs</c> where <c>Cs</c> is at most a half, <see cref="Screen"/> with <c>2 Cs - 1</c> above it.</summary>
    HardLight,
    /// <summary>The specification's soft light: darkens below a half of <c>Cs</c> and lightens above it, never to black or white.</summary>
    SoftLight,
    /// <summary><c>|Cb - Cs|</c>.</summary>
    Difference,
    /// <summary><c>Cb + Cs - 2 Cb * Cs</c>.</summary>
    Exclusion,
    /// <summary>The hue of the layer with the saturation and the luminosity of what is below. Not channel by channel.</summary>
    Hue,
    /// <summary>The saturation of the layer with the hue and the luminosity of what is below. Not channel by channel.</summary>
    Saturation,
    /// <summary>The hue and the saturation of the layer with the luminosity of what is below. Not channel by channel.</summary>
    Color,
    /// <summary>The luminosity of the layer with the hue and the saturation of what is below. Not channel by channel.</summary>
    Luminosity,
}

/// <summary>Which picture a filter primitive works on.</summary>
public enum FilterInputKind : byte
{
    /// <summary>The filtered layer as it was drawn.</summary>
    SourceGraphic,
    /// <summary>The filtered layer's alpha, with black for a colour.</summary>
    SourceAlpha,
    /// <summary>What an earlier primitive of the same filter gave.</summary>
    Result,
}

/// <summary>A picture a filter primitive works on.</summary>
/// <param name="Kind">Which picture.</param>
/// <param name="Index">For <see cref="FilterInputKind.Result"/>: which primitive of <see cref="VectorFilter.Primitives"/>, always an earlier one.</param>
public readonly record struct VectorFilterInput(FilterInputKind Kind, int Index = 0)
{
    /// <summary>The filtered layer as it was drawn.</summary>
    public static VectorFilterInput SourceGraphic => new(FilterInputKind.SourceGraphic);

    /// <summary>The filtered layer's alpha.</summary>
    public static VectorFilterInput SourceAlpha => new(FilterInputKind.SourceAlpha);

    /// <summary>What the primitive at <paramref name="index"/> gave.</summary>
    public static VectorFilterInput Result(int index) => new(FilterInputKind.Result, index);
}

/// <summary>
/// One step of a <see cref="VectorFilter"/>: it makes a picture, from none, one or more others. A
/// <see cref="FilterBlur"/>, <see cref="FilterOffset"/>, <see cref="FilterFlood"/>, <see cref="FilterComposite"/>,
/// <see cref="FilterMerge"/>, <see cref="FilterColorMatrix"/>, <see cref="FilterBlend"/> or
/// <see cref="FilterComponentTransfer"/>. A renderer that meets a kind it does not know should say so, not guess.
/// </summary>
public abstract record VectorFilterPrimitive
{
    /// <summary>
    /// Where this step's picture is, in the filter's coordinates (<see cref="VectorFilter.Transform"/>): outside it the
    /// picture is transparent, whatever the step would have put there. Never larger than the filter's own
    /// <see cref="VectorFilter.Region"/>.
    /// </summary>
    public required VectorRect Region { get; init; }

    /// <summary>
    /// Whether the arithmetic is done on linear light (the inputs taken from sRGB to linear before, the result kept
    /// linear until something needs it otherwise) or on the sRGB values as they are. Alpha is the same either way.
    /// </summary>
    public bool LinearRgb { get; init; } = true;
}

/// <summary>
/// A Gaussian blur. The deviations are in the filter's coordinates, along its x and y axes; one of them may be zero,
/// which blurs along the other axis only. (For both zero a reader writes an offset by nothing: the input as it is.)
/// </summary>
/// <param name="Input">What is blurred.</param>
/// <param name="DeviationX">The standard deviation across, zero or more.</param>
/// <param name="DeviationY">The standard deviation down, zero or more.</param>
public sealed record FilterBlur(VectorFilterInput Input, float DeviationX, float DeviationY) : VectorFilterPrimitive;

/// <summary>The input moved, by a distance in the filter's coordinates.</summary>
/// <param name="Input">What is moved.</param>
/// <param name="Dx">How far across.</param>
/// <param name="Dy">How far down.</param>
public sealed record FilterOffset(VectorFilterInput Input, float Dx, float Dy) : VectorFilterPrimitive;

/// <summary>One colour over the whole of the step's <see cref="VectorFilterPrimitive.Region"/>.</summary>
/// <param name="Color">The colour, sRGB and not premultiplied, its opacity in the alpha.</param>
public sealed record FilterFlood(Rgba32 Color) : VectorFilterPrimitive;

/// <summary>How a <see cref="FilterComposite"/> puts its two pictures together (Porter and Duff's operators, on premultiplied colour).</summary>
public enum FilterCompositeOperator : byte
{
    /// <summary>The input over the second.</summary>
    Over,
    /// <summary>The input where the second is: <c>in * a2</c>.</summary>
    In,
    /// <summary>The input where the second is not: <c>in * (1 - a2)</c>.</summary>
    Out,
    /// <summary>The input where the second is, and the second elsewhere: <c>in * a2 + in2 * (1 - a)</c>.</summary>
    Atop,
    /// <summary>Each where the other is not: <c>in * (1 - a2) + in2 * (1 - a)</c>.</summary>
    Xor,
    /// <summary>The sum of the two, limited to 1.</summary>
    Lighter,
    /// <summary><c>K1 * in * in2 + K2 * in + K3 * in2 + K4</c> on every premultiplied channel, alpha included, limited to 0..1 (and the colour to the alpha).</summary>
    Arithmetic,
}

/// <summary>Two pictures put together by one of Porter and Duff's operators.</summary>
/// <param name="Input">The first picture (the one "over", "in", "out of" the other).</param>
/// <param name="Input2">The second picture.</param>
/// <param name="Operator">How.</param>
/// <param name="K1">For <see cref="FilterCompositeOperator.Arithmetic"/>.</param>
/// <param name="K2">For <see cref="FilterCompositeOperator.Arithmetic"/>.</param>
/// <param name="K3">For <see cref="FilterCompositeOperator.Arithmetic"/>.</param>
/// <param name="K4">For <see cref="FilterCompositeOperator.Arithmetic"/>.</param>
public sealed record FilterComposite(VectorFilterInput Input, VectorFilterInput Input2, FilterCompositeOperator Operator,
    float K1 = 0, float K2 = 0, float K3 = 0, float K4 = 0) : VectorFilterPrimitive;

/// <summary>Pictures laid one over the other, the first at the bottom.</summary>
/// <param name="Inputs">The pictures, bottom first. With none the result is transparent.</param>
public sealed record FilterMerge(ImmutableArray<VectorFilterInput> Inputs) : VectorFilterPrimitive;

/// <summary>
/// Every pixel's colour multiplied by a matrix. The colour is taken NOT premultiplied, as the column
/// <c>(R, G, B, A, 1)</c> with every channel 0 to 1; row <c>i</c> of the matrix gives channel <c>i</c> of the result,
/// which is limited to 0..1.
/// </summary>
/// <param name="Input">What is recoloured.</param>
/// <param name="Matrix">Twenty numbers: four rows (R, G, B, A) of five.</param>
public sealed record FilterColorMatrix(VectorFilterInput Input, ImmutableArray<float> Matrix) : VectorFilterPrimitive;

/// <summary>One picture mixed with another by a <see cref="VectorBlendMode"/> and laid over it.</summary>
/// <param name="Input">The picture on top.</param>
/// <param name="Input2">The picture below.</param>
/// <param name="Mode">How the colours are mixed where both are there.</param>
public sealed record FilterBlend(VectorFilterInput Input, VectorFilterInput Input2, VectorBlendMode Mode) : VectorFilterPrimitive;

/// <summary>The shape of a <see cref="FilterTransferFunction"/>.</summary>
public enum FilterTransferKind : byte
{
    /// <summary>The channel as it is.</summary>
    Identity,
    /// <summary>Straight lines between the <see cref="FilterTransferFunction.Table"/>'s values, which are spread evenly over 0..1.</summary>
    Table,
    /// <summary>Steps: the <see cref="FilterTransferFunction.Table"/>'s <c>n</c> values, each for a <c>1/n</c> of 0..1.</summary>
    Discrete,
    /// <summary><c>Slope * C + Intercept</c>.</summary>
    Linear,
    /// <summary><c>Amplitude * C^Exponent + Offset</c>.</summary>
    Gamma,
}

/// <summary>What a <see cref="FilterComponentTransfer"/> does to one channel, taken 0 to 1; the result is limited to 0..1.</summary>
/// <param name="Kind">The shape of the function.</param>
/// <param name="Table">For <see cref="FilterTransferKind.Table"/> and <see cref="FilterTransferKind.Discrete"/>: at least one value. Empty otherwise.</param>
/// <param name="Slope">For <see cref="FilterTransferKind.Linear"/>.</param>
/// <param name="Intercept">For <see cref="FilterTransferKind.Linear"/>.</param>
/// <param name="Amplitude">For <see cref="FilterTransferKind.Gamma"/>.</param>
/// <param name="Exponent">For <see cref="FilterTransferKind.Gamma"/>.</param>
/// <param name="Offset">For <see cref="FilterTransferKind.Gamma"/>.</param>
public sealed record FilterTransferFunction(FilterTransferKind Kind, ImmutableArray<float> Table = default,
    float Slope = 1, float Intercept = 0, float Amplitude = 1, float Exponent = 1, float Offset = 0)
{
    /// <summary>The function that changes nothing.</summary>
    public static FilterTransferFunction Identity { get; } = new(FilterTransferKind.Identity, []);

    /// <summary>The table's values; never default.</summary>
    public ImmutableArray<float> Table { get; init; } = Table.IsDefault ? [] : Table;
}

/// <summary>Every pixel's channels, NOT premultiplied, each put through a function of its own.</summary>
/// <param name="Input">What is recoloured.</param>
/// <param name="Red">The function for red.</param>
/// <param name="Green">The function for green.</param>
/// <param name="Blue">The function for blue.</param>
/// <param name="Alpha">The function for alpha.</param>
public sealed record FilterComponentTransfer(VectorFilterInput Input, FilterTransferFunction Red, FilterTransferFunction Green,
    FilterTransferFunction Blue, FilterTransferFunction Alpha) : VectorFilterPrimitive;

/// <summary>
/// A picture made from a layer by steps, which replaces the layer. The layer is drawn; the
/// <see cref="Primitives"/> are worked out in order, each from the layer or from earlier steps; what the last one gives
/// is the result. With no steps the result is transparent: the layer is not drawn.
/// </summary>
/// <remarks>
/// The filter has coordinates of its own, which <see cref="Transform"/> takes to the scene's: the axes blurs and
/// offsets go along, and the units their distances are in. Nothing outside <see cref="Region"/> is drawn, so a blur
/// or a shadow that would reach further is cut off there. The LAYER is not cut to it: what the layer has outside the
/// region is still there for a step to blur or move in (a shape the region cuts through is blurred as the whole
/// shape, and its cut edge is sharp), which is what a browser does.
/// </remarks>
public sealed class VectorFilter
{
    /// <summary>The filter's area, in its own coordinates. Its width and height are above zero.</summary>
    public required VectorRect Region { get; init; }

    /// <summary>The filter's coordinates to scene coordinates. Always invertible.</summary>
    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;

    /// <summary>The steps, in the order they are worked out. The last one's picture is the result.</summary>
    public required ImmutableArray<VectorFilterPrimitive> Primitives { get; init; }
}
