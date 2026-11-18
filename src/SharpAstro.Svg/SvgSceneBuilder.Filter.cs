using System.Collections.Immutable;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

// Filters: what a filter property becomes for the element that has it. Everything that depends on the element (its
// bounding box, the units, which result a name means) is worked out here, so that a renderer is handed plain steps.
internal sealed partial class SvgSceneBuilder
{
    /// <summary>The filter that gives nothing. An element filtered by it is not drawn.</summary>
    private static readonly VectorFilter NoResult = new() { Region = default, Primitives = [] };

    /// <summary>
    /// A <c>filter</c> element as steps, for an element whose user space is <paramref name="user"/> and whose bounding
    /// box there is <paramref name="box"/>. A filter that cannot give a picture (no area, no steps) is
    /// <see cref="NoResult"/>, and the element is then not drawn. Null is a filter that is not read (a step this reader
    /// does not know, a picture it does not have): it is reported, and the element is drawn as it is.
    /// </summary>
    private VectorFilter? Filter(SvgElement element, SvgAffine user, VectorRect? box, in SvgViewport viewport, int depth)
    {
        Charge(depth);
        if (element.Get("href") is not null)
        {
            Issue(SvgIssueKind.Unsupported, "filter: href to another filter");
            return null;
        }
        SvgStyle style = InheritedStyle(element, viewport);
        double fontSize = style.FontSize;

        bool regionInBox = true, stepsInBox = false;
        switch (element.Get("filterUnits")?.Trim())
        {
            case null or "objectBoundingBox": break;
            case "userSpaceOnUse": regionInBox = false; break;
            default: Issue(SvgIssueKind.Invalid, "filterUnits"); break;
        }
        switch (element.Get("primitiveUnits")?.Trim())
        {
            case null or "userSpaceOnUse": break;
            case "objectBoundingBox": stepsInBox = true; break;
            default: Issue(SvgIssueKind.Invalid, "primitiveUnits"); break;
        }
        if (regionInBox || stepsInBox)
        {
            if (_boxOfText)
            {
                // The box is that of text, which is not known here: the area cannot be placed. The text is drawn as it is.
                Issue(SvgIssueKind.Unsupported, "a filter in the units of a text's bounding box");
                return null;
            }
            // A box with no width or no height (a straight line): its units mean nothing, and nothing is drawn.
            if (box is not { Width: > 0, Height: > 0 }) return NoResult;
        }
        VectorRect b = box ?? default;

        double x, y, w, h;
        if (regionInBox)
        {
            x = b.X + Fraction(element, "x", -0.1) * b.Width;
            y = b.Y + Fraction(element, "y", -0.1) * b.Height;
            w = Fraction(element, "width", 1.2) * b.Width;
            h = Fraction(element, "height", 1.2) * b.Height;
        }
        else
        {
            x = Region(element, "x", -10, viewport.Width, fontSize);
            y = Region(element, "y", -10, viewport.Height, fontSize);
            w = Region(element, "width", 120, viewport.Width, fontSize);
            h = Region(element, "height", 120, viewport.Height, fontSize);
        }
        if (!(w > 0) || !(h > 0) || !double.IsFinite(x + y + w + h)) return NoResult;
        var region = new VectorRect((float)x, (float)y, (float)w, (float)h);

        var steps = ImmutableArray.CreateBuilder<VectorFilterPrimitive>();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SvgElement child in element.Children)
        {
            if (!child.Name.StartsWith("fe", StringComparison.Ordinal)) continue;
            Charge(depth + 1);
            SvgStyle childStyle = ComputeStyle(child, style, viewport);
            bool linear = childStyle.LinearFilters;
            int first = steps.Count;

            switch (child.Name)
            {
                case "feGaussianBlur":
                {
                    if (!TryInput(child, "in", names, steps.Count, out VectorFilterInput input)) return null;
                    (double sx, double sy) = NumberPair(child, "stdDeviation", 0);
                    if (child.Get("edgeMode")?.Trim() is { } edge and not "none")
                    {
                        Issue(SvgIssueKind.Unsupported, $"feGaussianBlur: edgeMode {edge}");
                        return null;
                    }
                    if (stepsInBox) (sx, sy) = (sx * b.Width, sy * b.Height);
                    // A deviation below zero, or both zero, is the input as it is (and still only what is in this step's area).
                    steps.Add(sx < 0 || sy < 0 || (sx == 0 && sy == 0) || !double.IsFinite(sx + sy)
                        ? new FilterOffset(input, 0, 0) { Region = default }
                        : new FilterBlur(input, (float)sx, (float)sy) { Region = default });
                    break;
                }
                case "feOffset":
                {
                    if (!TryInput(child, "in", names, steps.Count, out VectorFilterInput input)) return null;
                    double dx = Number(child, "dx", 0), dy = Number(child, "dy", 0);
                    if (stepsInBox) (dx, dy) = (dx * b.Width, dy * b.Height);
                    steps.Add(new FilterOffset(input, (float)dx, (float)dy) { Region = default });
                    break;
                }
                case "feFlood":
                    steps.Add(new FilterFlood(FloodColor(child, childStyle)) { Region = default });
                    break;
                case "feComposite":
                {
                    if (!TryInput(child, "in", names, steps.Count, out VectorFilterInput input)) return null;
                    if (!TryInput(child, "in2", names, steps.Count, out VectorFilterInput input2)) return null;
                    FilterCompositeOperator op = FilterCompositeOperator.Over;
                    switch (child.Get("operator")?.Trim())
                    {
                        case null or "over": break;
                        case "in": op = FilterCompositeOperator.In; break;
                        case "out": op = FilterCompositeOperator.Out; break;
                        case "atop": op = FilterCompositeOperator.Atop; break;
                        case "xor": op = FilterCompositeOperator.Xor; break;
                        case "lighter": op = FilterCompositeOperator.Lighter; break;
                        case "arithmetic": op = FilterCompositeOperator.Arithmetic; break;
                        default: Issue(SvgIssueKind.Invalid, "feComposite: operator"); break;
                    }
                    steps.Add(new FilterComposite(input, input2, op, (float)Number(child, "k1", 0), (float)Number(child, "k2", 0),
                        (float)Number(child, "k3", 0), (float)Number(child, "k4", 0)) { Region = default });
                    break;
                }
                case "feMerge":
                {
                    var inputs = ImmutableArray.CreateBuilder<VectorFilterInput>();
                    foreach (SvgElement node in child.Children)
                    {
                        if (node.Name != "feMergeNode") continue;
                        Charge(depth + 2);
                        if (!TryInput(node, "in", names, steps.Count, out VectorFilterInput input)) return null;
                        inputs.Add(input);
                    }
                    steps.Add(new FilterMerge(inputs.ToImmutable()) { Region = default });
                    break;
                }
                case "feColorMatrix":
                {
                    if (!TryInput(child, "in", names, steps.Count, out VectorFilterInput input)) return null;
                    steps.Add(new FilterColorMatrix(input, ColorMatrix(child)) { Region = default });
                    break;
                }
                case "feBlend":
                {
                    if (!TryInput(child, "in", names, steps.Count, out VectorFilterInput input)) return null;
                    if (!TryInput(child, "in2", names, steps.Count, out VectorFilterInput input2)) return null;
                    var mode = VectorBlendMode.Normal;
                    if (child.Get("mode") is { } text && !TryBlendMode(text.AsSpan().Trim(), out mode))
                    {
                        Issue(SvgIssueKind.Invalid, "feBlend: mode");
                        mode = VectorBlendMode.Normal;
                    }
                    steps.Add(new FilterBlend(input, input2, mode) { Region = default });
                    break;
                }
                case "feComponentTransfer":
                {
                    if (!TryInput(child, "in", names, steps.Count, out VectorFilterInput input)) return null;
                    FilterTransferFunction red = FilterTransferFunction.Identity, green = red, blue = red, alpha = red;
                    foreach (SvgElement function in child.Children)
                    {
                        // Of several for one channel the last stands.
                        switch (function.Name)
                        {
                            case "feFuncR": red = TransferFunction(function); break;
                            case "feFuncG": green = TransferFunction(function); break;
                            case "feFuncB": blue = TransferFunction(function); break;
                            case "feFuncA": alpha = TransferFunction(function); break;
                        }
                    }
                    steps.Add(new FilterComponentTransfer(input, red, green, blue, alpha) { Region = default });
                    break;
                }
                case "feDropShadow":
                {
                    // By its definition: the input blurred, moved, its alpha filled with the flood colour, and the input over that.
                    // (The blur is of the whole input and not of its alpha alone: only its alpha is used, and a blur's alpha
                    // does not depend on the colours.)
                    if (!TryInput(child, "in", names, steps.Count, out VectorFilterInput input)) return null;
                    (double sx, double sy) = NumberPair(child, "stdDeviation", 2);
                    double dx = Number(child, "dx", 2), dy = Number(child, "dy", 2);
                    if (stepsInBox) (sx, sy, dx, dy) = (sx * b.Width, sy * b.Height, dx * b.Width, dy * b.Height);
                    VectorFilterInput shadow = input;
                    if (sx >= 0 && sy >= 0 && (sx > 0 || sy > 0) && double.IsFinite(sx + sy))
                    {
                        steps.Add(new FilterBlur(input, (float)sx, (float)sy) { Region = default });
                        shadow = VectorFilterInput.Result(steps.Count - 1);
                    }
                    steps.Add(new FilterOffset(shadow, (float)dx, (float)dy) { Region = default });
                    steps.Add(new FilterFlood(FloodColor(child, childStyle)) { Region = default });
                    steps.Add(new FilterComposite(VectorFilterInput.Result(steps.Count - 1), VectorFilterInput.Result(steps.Count - 2), FilterCompositeOperator.In) { Region = default });
                    steps.Add(new FilterMerge([VectorFilterInput.Result(steps.Count - 1), input]) { Region = default });
                    break;
                }
                default:
                    Issue(SvgIssueKind.Unsupported, $"<{child.Name}>");
                    return null;
            }

            // The step's area: what it says, and for what it does not say the area of what it works on (the filter's
            // whole area for the layer, and for a step that works on nothing). Then no larger than the filter's.
            // (One element may have become several steps: only what they take from before it counts.)
            VectorRect fallback = region;
            VectorRect? union = null;
            bool whole = false;
            for (int i = first; i < steps.Count && !whole; i++)
            {
                foreach (VectorFilterInput input in InputsOf(steps[i]))
                {
                    if (input.Kind != FilterInputKind.Result) whole = true;
                    else if (input.Index < first) union = union is { } u ? Union(u, steps[input.Index].Region) : steps[input.Index].Region;
                }
            }
            if (!whole && union is { } tight) fallback = tight;
            double px = fallback.X, py = fallback.Y, pw = fallback.Width, ph = fallback.Height;
            if (stepsInBox)
            {
                if (child.Get("x") is not null) px = b.X + Fraction(child, "x", 0) * b.Width;
                if (child.Get("y") is not null) py = b.Y + Fraction(child, "y", 0) * b.Height;
                if (child.Get("width") is not null) pw = Fraction(child, "width", 0) * b.Width;
                if (child.Get("height") is not null) ph = Fraction(child, "height", 0) * b.Height;
            }
            else
            {
                if (child.Get("x") is not null) px = Region(child, "x", 0, viewport.Width, fontSize);
                if (child.Get("y") is not null) py = Region(child, "y", 0, viewport.Height, fontSize);
                if (child.Get("width") is not null) pw = Region(child, "width", 0, viewport.Width, fontSize);
                if (child.Get("height") is not null) ph = Region(child, "height", 0, viewport.Height, fontSize);
            }
            VectorRect own = Intersect(region, px, py, pw, ph);
            for (int i = first; i < steps.Count; i++) steps[i] = steps[i] with { Region = own, LinearRgb = linear };

            if (child.Get("result") is { Length: > 0 } name) names[name] = steps.Count - 1;
        }
        if (steps.Count == 0) return NoResult;
        return new VectorFilter { Region = region, Transform = user.ToMatrix(), Primitives = steps.ToImmutable() };
    }

    private static VectorRect Union(VectorRect a, VectorRect b)
    {
        float left = MathF.Min(a.X, b.X), top = MathF.Min(a.Y, b.Y);
        return new VectorRect(left, top, MathF.Max(a.Right, b.Right) - left, MathF.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>The part of <paramref name="region"/> that is in a rectangle; with no width and no height when there is none.</summary>
    private static VectorRect Intersect(VectorRect region, double x, double y, double w, double h)
    {
        if (!(w > 0) || !(h > 0) || !double.IsFinite(x + y + w + h)) return new VectorRect(region.X, region.Y, 0, 0);
        double left = Math.Max(region.X, x), top = Math.Max(region.Y, y);
        double right = Math.Min(region.Right, x + w), bottom = Math.Min(region.Bottom, y + h);
        return right > left && bottom > top ? new VectorRect((float)left, (float)top, (float)(right - left), (float)(bottom - top)) : new VectorRect(region.X, region.Y, 0, 0);
    }

    private static ImmutableArray<VectorFilterInput> InputsOf(VectorFilterPrimitive step) => step switch
    {
        FilterBlur blur => [blur.Input],
        FilterOffset offset => [offset.Input],
        FilterComposite composite => [composite.Input, composite.Input2],
        FilterMerge merge => merge.Inputs,
        FilterColorMatrix matrix => [matrix.Input],
        FilterBlend blend => [blend.Input, blend.Input2],
        FilterComponentTransfer transfer => [transfer.Input],
        _ => [],
    };

    /// <summary>
    /// Which picture an <c>in</c> or <c>in2</c> means. Not written, or a name no earlier step has given its result: what
    /// the step before gave, and the layer for the first step. False for a picture this reader does not have.
    /// </summary>
    private bool TryInput(SvgElement element, string attribute, Dictionary<string, int> names, int index, out VectorFilterInput input)
    {
        input = index == 0 ? VectorFilterInput.SourceGraphic : VectorFilterInput.Result(index - 1);
        switch (element.Get(attribute)?.Trim())
        {
            case null or "":
                return true;
            case "SourceGraphic":
                input = VectorFilterInput.SourceGraphic;
                return true;
            case "SourceAlpha":
                input = VectorFilterInput.SourceAlpha;
                return true;
            case "BackgroundImage" or "BackgroundAlpha" or "FillPaint" or "StrokePaint":
                Issue(SvgIssueKind.Unsupported, $"filter input {element.Get(attribute)!.Trim()}");
                return false;
            case { } name:
                if (names.TryGetValue(name, out int result)) input = VectorFilterInput.Result(result);
                else Issue(SvgIssueKind.Invalid, $"{element.Name}: {attribute} names no earlier result");
                return true;
        }
    }

    private double Number(SvgElement element, string attribute, double fallback)
    {
        if (element.Get(attribute) is not { } text) return fallback;
        var scanner = new SvgScanner(text);
        scanner.SkipWhitespace();
        if (scanner.TryNumber(out double value))
        {
            scanner.SkipWhitespace();
            if (scanner.AtEnd && double.IsFinite(value)) return value;
        }
        Issue(SvgIssueKind.Invalid, $"{element.Name}: {attribute}");
        return fallback;
    }

    /// <summary>One number for both, or two.</summary>
    private (double X, double Y) NumberPair(SvgElement element, string attribute, double fallback)
    {
        if (element.Get(attribute) is not { } text) return (fallback, fallback);
        if (TryNumbers(text, out List<double> values) && values.Count is 1 or 2) return (values[0], values[^1]);
        Issue(SvgIssueKind.Invalid, $"{element.Name}: {attribute}");
        return (fallback, fallback);
    }

    private static bool TryNumbers(ReadOnlySpan<char> text, out List<double> values)
    {
        values = [];
        var scanner = new SvgScanner(text);
        scanner.SkipWhitespace();
        while (!scanner.AtEnd)
        {
            if (!scanner.TryNumber(out double value) || !double.IsFinite(value)) return false;
            values.Add(value);
            scanner.SkipCommaWhitespace();
        }
        return true;
    }

    /// <summary>A number that is a share of a bounding box: <c>0.5</c> or <c>50%</c>.</summary>
    private double Fraction(SvgElement element, string attribute, double fallback)
    {
        if (element.Get(attribute) is not { } text) return fallback;
        if (SvgLength.TryParse(text, out SvgLength length)) return length.Unit == SvgUnit.Percent ? length.Value / 100.0 : length.Value;
        Issue(SvgIssueKind.Invalid, attribute);
        return fallback;
    }

    /// <summary>A length in user space; not written, a percentage of <paramref name="percentBase"/>.</summary>
    private double Region(SvgElement element, string attribute, double fallbackPercent, double percentBase, double fontSize)
    {
        if (element.Get(attribute) is { } text)
        {
            if (SvgLength.TryParse(text, out SvgLength length)) return length.Resolve(fontSize, percentBase);
            Issue(SvgIssueKind.Invalid, attribute);
        }
        return fallbackPercent * percentBase / 100.0;
    }

    /// <summary><c>flood-color</c> and <c>flood-opacity</c>: properties of the one element, the last one standing wins.</summary>
    private Rgba32 FloodColor(SvgElement element, in SvgStyle style)
    {
        var color = new Rgba32(0, 0, 0);
        double opacity = 1;
        // The declarations are copied out first: substituting a var() may work a style out, which reuses the list they are in.
        SvgDeclaration[] declarations = [.. Cascade(element).Where(d => d.Name is "flood-color" or "flood-opacity")];
        foreach (SvgDeclaration declaration in declarations)
        {
            string raw = declaration.Value;
            if (HasVar(raw) && !TrySubstitute(raw, style.Variables, null, 0, out raw))
            {
                // Unusable once looked up: the initial value, black and opaque.
                Issue(SvgIssueKind.Invalid, "var() with no value and no fallback");
                if (declaration.Name == "flood-color") color = new Rgba32(0, 0, 0);
                else opacity = 1;
                continue;
            }
            ReadOnlySpan<char> v = raw.AsSpan().Trim();
            if (declaration.Name == "flood-color")
            {
                if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) color = style.Color;
                // An invalid colour is as if it were not written: what was there before stays.
                else if (SvgColor.TryParse(v, out Rgba32 parsed)) color = parsed;
                else Issue(SvgIssueKind.Invalid, "flood-color");
            }
            else if (!TryOpacity(v, out opacity))
            {
                Issue(SvgIssueKind.Invalid, "flood-opacity");
                opacity = 1;
            }
        }
        return color with { A = (byte)Math.Clamp(Math.Round(color.A * opacity, MidpointRounding.AwayFromZero), 0, 255) };
    }

    private static readonly ImmutableArray<float> IdentityMatrix = [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    /// <summary>A <c>feColorMatrix</c> as the twenty numbers, whichever way it is written. What cannot be read changes nothing.</summary>
    private ImmutableArray<float> ColorMatrix(SvgElement element)
    {
        string? text = element.Get("values");
        List<double> values = [];
        if (text is not null && !TryNumbers(text, out values))
        {
            Issue(SvgIssueKind.Invalid, "feColorMatrix: values");
            return IdentityMatrix;
        }
        switch (element.Get("type")?.Trim())
        {
            case null or "matrix":
                if (text is null) return IdentityMatrix;
                if (values.Count == 20) return [.. values.Select(value => (float)value)];
                Issue(SvgIssueKind.Invalid, "feColorMatrix: values");
                return IdentityMatrix;
            case "saturate":
            {
                if (values.Count > 1) Issue(SvgIssueKind.Invalid, "feColorMatrix: values");
                float s = values.Count == 1 ? (float)values[0] : 1;
                return
                [
                    0.213f + 0.787f * s, 0.715f - 0.715f * s, 0.072f - 0.072f * s, 0, 0,
                    0.213f - 0.213f * s, 0.715f + 0.285f * s, 0.072f - 0.072f * s, 0, 0,
                    0.213f - 0.213f * s, 0.715f - 0.715f * s, 0.072f + 0.928f * s, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case "hueRotate":
            {
                if (values.Count > 1) Issue(SvgIssueKind.Invalid, "feColorMatrix: values");
                double angle = (values.Count == 1 ? values[0] : 0) * Math.PI / 180.0;
                float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
                return
                [
                    0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f, 0, 0,
                    0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f, 0, 0,
                    0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case "luminanceToAlpha":
                return [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.2125f, 0.7154f, 0.0721f, 0, 0];
            default:
                Issue(SvgIssueKind.Invalid, "feColorMatrix: type");
                return IdentityMatrix;
        }
    }

    /// <summary>A <c>feFuncR</c> and its like. What cannot be read changes nothing.</summary>
    private FilterTransferFunction TransferFunction(SvgElement element)
    {
        switch (element.Get("type")?.Trim())
        {
            case "identity":
                return FilterTransferFunction.Identity;
            case "table" or "discrete":
            {
                bool table = element.Get("type")!.Trim() == "table";
                if (element.Get("tableValues") is not { } text) return FilterTransferFunction.Identity;
                if (!TryNumbers(text, out List<double> values))
                {
                    Issue(SvgIssueKind.Invalid, $"{element.Name}: tableValues");
                    return FilterTransferFunction.Identity;
                }
                // A table with nothing in it changes nothing.
                if (values.Count == 0) return FilterTransferFunction.Identity;
                return new FilterTransferFunction(table ? FilterTransferKind.Table : FilterTransferKind.Discrete, [.. values.Select(value => (float)value)]);
            }
            case "linear":
                return new FilterTransferFunction(FilterTransferKind.Linear, [], Slope: (float)Number(element, "slope", 1), Intercept: (float)Number(element, "intercept", 0));
            case "gamma":
                return new FilterTransferFunction(FilterTransferKind.Gamma, [], Amplitude: (float)Number(element, "amplitude", 1),
                    Exponent: (float)Number(element, "exponent", 1), Offset: (float)Number(element, "offset", 0));
            default:
                Issue(SvgIssueKind.Invalid, $"{element.Name}: type");
                return FilterTransferFunction.Identity;
        }
    }

    /// <summary>A blend mode by its CSS name.</summary>
    private static bool TryBlendMode(ReadOnlySpan<char> name, out VectorBlendMode mode)
    {
        (mode, bool known) = name switch
        {
            "normal" => (VectorBlendMode.Normal, true),
            "multiply" => (VectorBlendMode.Multiply, true),
            "screen" => (VectorBlendMode.Screen, true),
            "overlay" => (VectorBlendMode.Overlay, true),
            "darken" => (VectorBlendMode.Darken, true),
            "lighten" => (VectorBlendMode.Lighten, true),
            "color-dodge" => (VectorBlendMode.ColorDodge, true),
            "color-burn" => (VectorBlendMode.ColorBurn, true),
            "hard-light" => (VectorBlendMode.HardLight, true),
            "soft-light" => (VectorBlendMode.SoftLight, true),
            "difference" => (VectorBlendMode.Difference, true),
            "exclusion" => (VectorBlendMode.Exclusion, true),
            "hue" => (VectorBlendMode.Hue, true),
            "saturation" => (VectorBlendMode.Saturation, true),
            "color" => (VectorBlendMode.Color, true),
            "luminosity" => (VectorBlendMode.Luminosity, true),
            _ => (VectorBlendMode.Normal, false),
        };
        return known;
    }
}
