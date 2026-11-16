using System.Globalization;
using System.Numerics;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

/// <summary>A cursor over attribute text with the token rules SVG's micro-syntaxes share.</summary>
internal ref struct SvgScanner
{
    private readonly ReadOnlySpan<char> _s;
    private int _p;

    public SvgScanner(ReadOnlySpan<char> text)
    {
        _s = text;
        _p = 0;
    }

    public readonly bool AtEnd => _p >= _s.Length;

    public readonly char Peek => _p < _s.Length ? _s[_p] : '\0';

    public void Advance() => _p++;

    public static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    public void SkipWhitespace()
    {
        while (_p < _s.Length && IsWhitespace(_s[_p])) _p++;
    }

    /// <summary>Whitespace, at most one comma, whitespace.</summary>
    public void SkipCommaWhitespace()
    {
        SkipWhitespace();
        if (_p < _s.Length && _s[_p] == ',')
        {
            _p++;
            SkipWhitespace();
        }
    }

    /// <summary>
    /// A number as SVG writes one: sign, digits, fraction, exponent. An <c>e</c> starts an exponent only when a
    /// digit follows it (after an optional sign), so <c>1em</c> is the number 1 and the unit <c>em</c>.
    /// </summary>
    public bool TryNumber(out double value)
    {
        value = 0;
        int i = _p, n = _s.Length;
        if (i < n && (_s[i] is '+' or '-')) i++;
        int digitsStart = i;
        while (i < n && char.IsAsciiDigit(_s[i])) i++;
        int digits = i - digitsStart;
        if (i < n && _s[i] == '.')
        {
            int j = i + 1;
            while (j < n && char.IsAsciiDigit(_s[j])) j++;
            int fraction = j - i - 1;
            if (fraction > 0 || digits > 0) i = j;
            digits += fraction;
        }
        if (digits == 0) return false;
        if (i < n && (_s[i] is 'e' or 'E'))
        {
            int j = i + 1;
            if (j < n && (_s[j] is '+' or '-')) j++;
            if (j < n && char.IsAsciiDigit(_s[j]))
            {
                while (j < n && char.IsAsciiDigit(_s[j])) j++;
                i = j;
            }
        }
        if (!double.TryParse(_s[_p..i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            return false;
        }
        _p = i;
        return true;
    }

    /// <summary>An arc flag: the single character 0 or 1, which may be followed directly by the next number.</summary>
    public bool TryFlag(out bool flag)
    {
        flag = false;
        if (_p >= _s.Length || (_s[_p] is not ('0' or '1'))) return false;
        flag = _s[_p] == '1';
        _p++;
        return true;
    }

    /// <summary>The run of ASCII letters at the cursor, possibly empty.</summary>
    public ReadOnlySpan<char> ReadLetters()
    {
        int start = _p;
        while (_p < _s.Length && char.IsAsciiLetter(_s[_p])) _p++;
        return _s[start.._p];
    }
}

internal enum SvgUnit : byte { None, Px, Pt, Pc, Mm, Cm, In, Q, Em, Ex, Percent }

/// <summary>A length as written: a number and a unit, not yet in user units.</summary>
internal readonly record struct SvgLength(double Value, SvgUnit Unit)
{
    /// <summary>A length that is the whole of <paramref name="text"/> (surrounding whitespace allowed).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out SvgLength length)
    {
        var scanner = new SvgScanner(text);
        scanner.SkipWhitespace();
        if (!TryScan(ref scanner, out length)) return false;
        scanner.SkipWhitespace();
        return scanner.AtEnd;
    }

    public static bool TryScan(ref SvgScanner scanner, out SvgLength length)
    {
        length = default;
        if (!scanner.TryNumber(out double value)) return false;
        SvgUnit unit;
        if (scanner.Peek == '%')
        {
            scanner.Advance();
            unit = SvgUnit.Percent;
        }
        else
        {
            ReadOnlySpan<char> letters = scanner.ReadLetters();
            if (letters.IsEmpty) unit = SvgUnit.None;
            else if (letters.Equals("px", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Px;
            else if (letters.Equals("pt", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Pt;
            else if (letters.Equals("pc", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Pc;
            else if (letters.Equals("mm", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Mm;
            else if (letters.Equals("cm", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Cm;
            else if (letters.Equals("in", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.In;
            else if (letters.Equals("q", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Q;
            else if (letters.Equals("em", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Em;
            else if (letters.Equals("ex", StringComparison.OrdinalIgnoreCase)) unit = SvgUnit.Ex;
            else return false;
        }
        length = new SvgLength(value, unit);
        return true;
    }

    /// <summary>
    /// The length in user units, at CSS's 96 px to the inch. <c>ex</c> is taken as half an em, which is what a
    /// renderer does when it has no font to ask.
    /// </summary>
    public double Resolve(double fontSize, double percentBase) => Unit switch
    {
        SvgUnit.Pt => Value * (96.0 / 72.0),
        SvgUnit.Pc => Value * 16.0,
        SvgUnit.Mm => Value * (96.0 / 25.4),
        SvgUnit.Cm => Value * (96.0 / 2.54),
        SvgUnit.In => Value * 96.0,
        SvgUnit.Q => Value * (96.0 / 101.6),
        SvgUnit.Em => Value * fontSize,
        SvgUnit.Ex => Value * fontSize * 0.5,
        SvgUnit.Percent => Value * percentBase / 100.0,
        _ => Value,
    };
}

/// <summary>
/// A 2D affine map in SVG's own convention: <c>x' = a x + c y + e</c>, <c>y' = b x + d y + f</c>, and
/// <c>A * B</c> applies B first. Kept apart from <see cref="Matrix3x2"/> (row vectors, the other order) so the
/// composition is written once, the way the specification writes it.
/// </summary>
internal readonly record struct SvgAffine(double A, double B, double C, double D, double E, double F)
{
    public static SvgAffine Identity => new(1, 0, 0, 1, 0, 0);

    public static SvgAffine operator *(SvgAffine l, SvgAffine r) => new(
        l.A * r.A + l.C * r.B,
        l.B * r.A + l.D * r.B,
        l.A * r.C + l.C * r.D,
        l.B * r.C + l.D * r.D,
        l.A * r.E + l.C * r.F + l.E,
        l.B * r.E + l.D * r.F + l.F);

    public double Determinant => A * D - B * C;

    public bool IsFinite =>
        double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    public Matrix3x2 ToMatrix() => new((float)A, (float)B, (float)C, (float)D, (float)E, (float)F);

    /// <summary>A <c>transform</c> attribute: a list of matrix / translate / scale / rotate / skewX / skewY.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out SvgAffine result)
    {
        result = Identity;
        var scanner = new SvgScanner(text);
        Span<double> args = stackalloc double[6];
        scanner.SkipWhitespace();
        while (!scanner.AtEnd)
        {
            ReadOnlySpan<char> name = scanner.ReadLetters();
            if (name.IsEmpty) return false;
            scanner.SkipWhitespace();
            if (scanner.Peek != '(') return false;
            scanner.Advance();
            int count = 0;
            scanner.SkipWhitespace();
            while (scanner.Peek != ')')
            {
                if (count == args.Length || !scanner.TryNumber(out args[count])) return false;
                count++;
                scanner.SkipCommaWhitespace();
                if (scanner.AtEnd) return false;
            }
            scanner.Advance();

            SvgAffine step;
            if (name.SequenceEqual("matrix") && count == 6)
            {
                step = new SvgAffine(args[0], args[1], args[2], args[3], args[4], args[5]);
            }
            else if (name.SequenceEqual("translate") && count is 1 or 2)
            {
                step = new SvgAffine(1, 0, 0, 1, args[0], count == 2 ? args[1] : 0);
            }
            else if (name.SequenceEqual("scale") && count is 1 or 2)
            {
                step = new SvgAffine(args[0], 0, 0, count == 2 ? args[1] : args[0], 0, 0);
            }
            else if (name.SequenceEqual("rotate") && count is 1 or 3)
            {
                (double sin, double cos) = Math.SinCos(args[0] * Math.PI / 180.0);
                step = new SvgAffine(cos, sin, -sin, cos, 0, 0);
                if (count == 3)
                {
                    step = new SvgAffine(1, 0, 0, 1, args[1], args[2]) * step * new SvgAffine(1, 0, 0, 1, -args[1], -args[2]);
                }
            }
            else if (name.SequenceEqual("skewX") && count == 1)
            {
                step = new SvgAffine(1, 0, Math.Tan(args[0] * Math.PI / 180.0), 1, 0, 0);
            }
            else if (name.SequenceEqual("skewY") && count == 1)
            {
                step = new SvgAffine(1, Math.Tan(args[0] * Math.PI / 180.0), 0, 1, 0, 0);
            }
            else
            {
                return false;
            }

            result *= step;
            scanner.SkipCommaWhitespace();
        }
        return result.IsFinite;
    }
}

/// <summary>CSS colours as SVG uses them: hex, <c>rgb()</c> / <c>rgba()</c>, <c>hsl()</c> / <c>hsla()</c>, the named colours and <c>transparent</c>.</summary>
internal static class SvgColor
{
    public static bool TryParse(ReadOnlySpan<char> text, out Rgba32 color)
    {
        color = default;
        text = text.Trim();
        if (text.IsEmpty) return false;
        if (text[0] == '#') return TryParseHex(text[1..], out color);

        int open = text.IndexOf('(');
        if (open > 0 && text[^1] == ')')
        {
            ReadOnlySpan<char> name = text[..open].Trim();
            ReadOnlySpan<char> inner = text[(open + 1)..^1];
            bool rgb = name.Equals("rgb", StringComparison.OrdinalIgnoreCase) || name.Equals("rgba", StringComparison.OrdinalIgnoreCase);
            bool hsl = name.Equals("hsl", StringComparison.OrdinalIgnoreCase) || name.Equals("hsla", StringComparison.OrdinalIgnoreCase);
            return (rgb || hsl) && TryParseFunction(inner, hsl, out color);
        }

        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = new Rgba32(0, 0, 0, 0);
            return true;
        }
        return SvgNamedColors.TryGet(text, out color);
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out Rgba32 color)
    {
        color = default;
        Span<byte> v = stackalloc byte[8];
        if (hex.Length is not (3 or 4 or 6 or 8)) return false;
        for (int i = 0; i < hex.Length; i++)
        {
            if (!char.IsAsciiHexDigit(hex[i])) return false;
            v[i] = (byte)Convert.ToInt32(hex.Slice(i, 1).ToString(), 16);
        }
        color = hex.Length <= 4
            ? new Rgba32((byte)(v[0] * 17), (byte)(v[1] * 17), (byte)(v[2] * 17), hex.Length == 4 ? (byte)(v[3] * 17) : (byte)255)
            : new Rgba32((byte)(v[0] * 16 + v[1]), (byte)(v[2] * 16 + v[3]), (byte)(v[4] * 16 + v[5]), hex.Length == 8 ? (byte)(v[6] * 16 + v[7]) : (byte)255);
        return true;
    }

    /// <summary>Three components and an optional alpha, separated by commas or by spaces with <c>/</c> before the alpha.</summary>
    private static bool TryParseFunction(ReadOnlySpan<char> inner, bool hsl, out Rgba32 color)
    {
        color = default;
        var scanner = new SvgScanner(inner);
        Span<double> values = stackalloc double[4];
        Span<bool> percent = stackalloc bool[4];
        int count = 0;
        scanner.SkipWhitespace();
        while (!scanner.AtEnd)
        {
            if (count == 4 || !scanner.TryNumber(out values[count])) return false;
            if (scanner.Peek == '%')
            {
                percent[count] = true;
                scanner.Advance();
            }
            else if (hsl && count == 0)
            {
                ReadOnlySpan<char> unit = scanner.ReadLetters();
                if (!unit.IsEmpty && !unit.Equals("deg", StringComparison.OrdinalIgnoreCase)) return false;
            }
            count++;
            scanner.SkipCommaWhitespace();
            if (scanner.Peek == '/')
            {
                if (count != 3) return false;
                scanner.Advance();
                scanner.SkipWhitespace();
            }
        }
        if (count < 3) return false;

        double alpha = 1;
        if (count == 4) alpha = Math.Clamp(percent[3] ? values[3] / 100.0 : values[3], 0, 1);

        double r, g, b;
        if (hsl)
        {
            if (percent[0] || !percent[1] || !percent[2]) return false;
            HslToRgb(values[0], Math.Clamp(values[1] / 100.0, 0, 1), Math.Clamp(values[2] / 100.0, 0, 1), out r, out g, out b);
        }
        else
        {
            if (percent[0] != percent[1] || percent[1] != percent[2]) return false;
            // v * 255 / 100, in that order: 50% is then exactly 127.5 and rounds up, where v * 2.55 falls just short.
            r = percent[0] ? values[0] * 255.0 / 100.0 : values[0];
            g = percent[0] ? values[1] * 255.0 / 100.0 : values[1];
            b = percent[0] ? values[2] * 255.0 / 100.0 : values[2];
        }
        color = new Rgba32(ToByte(r), ToByte(g), ToByte(b), ToByte(alpha * 255.0));
        return true;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>CSS Color 3's HSL to RGB, results on 0..255.</summary>
    private static void HslToRgb(double hueDegrees, double s, double l, out double r, out double g, out double b)
    {
        double h = hueDegrees % 360.0;
        if (h < 0) h += 360.0;
        h /= 360.0;
        double m2 = l <= 0.5 ? l * (s + 1) : l + s - l * s;
        double m1 = l * 2 - m2;
        r = Hue(m1, m2, h + 1.0 / 3.0) * 255.0;
        g = Hue(m1, m2, h) * 255.0;
        b = Hue(m1, m2, h - 1.0 / 3.0) * 255.0;

        static double Hue(double m1, double m2, double h)
        {
            if (h < 0) h += 1;
            if (h > 1) h -= 1;
            if (h * 6 < 1) return m1 + (m2 - m1) * h * 6;
            if (h * 2 < 1) return m2;
            if (h * 3 < 2) return m1 + (m2 - m1) * (2.0 / 3.0 - h) * 6;
            return m1;
        }
    }
}

/// <summary>The 148 CSS named colours (SVG 1.1's 147 and <c>rebeccapurple</c>).</summary>
internal static class SvgNamedColors
{
    private const string Table =
        "aliceblue=f0f8ff;antiquewhite=faebd7;aqua=00ffff;aquamarine=7fffd4;azure=f0ffff;beige=f5f5dc;bisque=ffe4c4;" +
        "black=000000;blanchedalmond=ffebcd;blue=0000ff;blueviolet=8a2be2;brown=a52a2a;burlywood=deb887;cadetblue=5f9ea0;" +
        "chartreuse=7fff00;chocolate=d2691e;coral=ff7f50;cornflowerblue=6495ed;cornsilk=fff8dc;crimson=dc143c;cyan=00ffff;" +
        "darkblue=00008b;darkcyan=008b8b;darkgoldenrod=b8860b;darkgray=a9a9a9;darkgreen=006400;darkgrey=a9a9a9;" +
        "darkkhaki=bdb76b;darkmagenta=8b008b;darkolivegreen=556b2f;darkorange=ff8c00;darkorchid=9932cc;darkred=8b0000;" +
        "darksalmon=e9967a;darkseagreen=8fbc8f;darkslateblue=483d8b;darkslategray=2f4f4f;darkslategrey=2f4f4f;" +
        "darkturquoise=00ced1;darkviolet=9400d3;deeppink=ff1493;deepskyblue=00bfff;dimgray=696969;dimgrey=696969;" +
        "dodgerblue=1e90ff;firebrick=b22222;floralwhite=fffaf0;forestgreen=228b22;fuchsia=ff00ff;gainsboro=dcdcdc;" +
        "ghostwhite=f8f8ff;gold=ffd700;goldenrod=daa520;gray=808080;green=008000;greenyellow=adff2f;grey=808080;" +
        "honeydew=f0fff0;hotpink=ff69b4;indianred=cd5c5c;indigo=4b0082;ivory=fffff0;khaki=f0e68c;lavender=e6e6fa;" +
        "lavenderblush=fff0f5;lawngreen=7cfc00;lemonchiffon=fffacd;lightblue=add8e6;lightcoral=f08080;lightcyan=e0ffff;" +
        "lightgoldenrodyellow=fafad2;lightgray=d3d3d3;lightgreen=90ee90;lightgrey=d3d3d3;lightpink=ffb6c1;" +
        "lightsalmon=ffa07a;lightseagreen=20b2aa;lightskyblue=87cefa;lightslategray=778899;lightslategrey=778899;" +
        "lightsteelblue=b0c4de;lightyellow=ffffe0;lime=00ff00;limegreen=32cd32;linen=faf0e6;magenta=ff00ff;maroon=800000;" +
        "mediumaquamarine=66cdaa;mediumblue=0000cd;mediumorchid=ba55d3;mediumpurple=9370db;mediumseagreen=3cb371;" +
        "mediumslateblue=7b68ee;mediumspringgreen=00fa9a;mediumturquoise=48d1cc;mediumvioletred=c71585;" +
        "midnightblue=191970;mintcream=f5fffa;mistyrose=ffe4e1;moccasin=ffe4b5;navajowhite=ffdead;navy=000080;" +
        "oldlace=fdf5e6;olive=808000;olivedrab=6b8e23;orange=ffa500;orangered=ff4500;orchid=da70d6;palegoldenrod=eee8aa;" +
        "palegreen=98fb98;paleturquoise=afeeee;palevioletred=db7093;papayawhip=ffefd5;peachpuff=ffdab9;peru=cd853f;" +
        "pink=ffc0cb;plum=dda0dd;powderblue=b0e0e6;purple=800080;rebeccapurple=663399;red=ff0000;rosybrown=bc8f8f;" +
        "royalblue=4169e1;saddlebrown=8b4513;salmon=fa8072;sandybrown=f4a460;seagreen=2e8b57;seashell=fff5ee;" +
        "sienna=a0522d;silver=c0c0c0;skyblue=87ceeb;slateblue=6a5acd;slategray=708090;slategrey=708090;snow=fffafa;" +
        "springgreen=00ff7f;steelblue=4682b4;tan=d2b48c;teal=008080;thistle=d8bfd8;tomato=ff6347;turquoise=40e0d0;" +
        "violet=ee82ee;wheat=f5deb3;white=ffffff;whitesmoke=f5f5f5;yellow=ffff00;yellowgreen=9acd32";

    private static readonly Dictionary<string, Rgba32> Colors = Build();

    /// <summary>Every name in the table, for tests.</summary>
    internal static IReadOnlyCollection<string> Names => Colors.Keys;

    public static bool TryGet(ReadOnlySpan<char> name, out Rgba32 color) =>
        Colors.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out color);

    private static Dictionary<string, Rgba32> Build()
    {
        var colors = new Dictionary<string, Rgba32>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in Table.Split(';'))
        {
            int eq = entry.IndexOf('=');
            uint rgb = Convert.ToUInt32(entry[(eq + 1)..], 16);
            colors.Add(entry[..eq], new Rgba32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        return colors;
    }
}
