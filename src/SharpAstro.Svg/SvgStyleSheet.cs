namespace SharpAstro.Svg;

/// <summary>One declaration: a property, its value as written, and whether it was marked <c>!important</c>.</summary>
internal readonly record struct SvgDeclaration(string Name, string Value, bool Important);

/// <summary>One simple-selector sequence: what a single element has to be for it to match.</summary>
internal sealed class SvgCompoundSelector
{
    public string? Type;
    public string? Id;
    public List<string> Classes = [];
    public List<(string Name, char Operator, string Value)> Attributes = [];
    public bool FirstChild, LastChild, OnlyChild, Root, Link;

    /// <summary>Set by a pseudo-class about interaction (<c>:hover</c> and the like): in a still picture it is never true.</summary>
    public bool Never;

    public bool Matches(SvgElement element)
    {
        if (Never) return false;
        if (Type is not null && element.Name != Type) return false;
        if (Id is not null && element.Get("id") != Id) return false;
        if (Root && element.Parent is not null) return false;
        if (Link && (element.Name != "a" || element.Get("href") is null)) return false;
        if (FirstChild || OnlyChild)
        {
            if (element.Parent is null || element.Index != 0) return false;
        }
        if (LastChild || OnlyChild)
        {
            if (element.Parent is null || element.Index != element.Parent.Children.Count - 1) return false;
        }
        if (Classes.Count > 0)
        {
            if (element.Get("class") is not { } classes) return false;
            foreach (string wanted in Classes)
            {
                if (!HasWord(classes, wanted)) return false;
            }
        }
        foreach ((string name, char op, string value) in Attributes)
        {
            if (element.Get(name) is not { } actual) return false;
            bool ok = op switch
            {
                '\0' => true,
                '=' => actual == value,
                '~' => value.Length > 0 && HasWord(actual, value),
                '|' => actual == value || actual.StartsWith(value + "-", StringComparison.Ordinal),
                '^' => value.Length > 0 && actual.StartsWith(value, StringComparison.Ordinal),
                '$' => value.Length > 0 && actual.EndsWith(value, StringComparison.Ordinal),
                _ => value.Length > 0 && actual.Contains(value, StringComparison.Ordinal),
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Whether a whitespace-separated list contains a word.</summary>
    private static bool HasWord(string list, string word)
    {
        ReadOnlySpan<char> rest = list;
        while (true)
        {
            rest = rest.TrimStart();
            if (rest.IsEmpty) return false;
            int end = rest.IndexOfAny(" \t\n\r\f");
            ReadOnlySpan<char> one = end < 0 ? rest : rest[..end];
            if (one.SequenceEqual(word)) return true;
            if (end < 0) return false;
            rest = rest[end..];
        }
    }
}

/// <summary>A selector with its declarations: compounds left to right, and the combinator before each but the first.</summary>
internal sealed class SvgStyleRule
{
    public required SvgCompoundSelector[] Compounds;

    /// <summary><c>' '</c> descendant, <c>'&gt;'</c> child, <c>'+'</c> next sibling, <c>'~'</c> later sibling; index i is between compounds i and i + 1.</summary>
    public required char[] Combinators;

    public required int Specificity;
    public required int Order;
    public required SvgDeclaration[] Declarations;

    /// <summary>Whether the selector matches the element. Every compound test is charged to <paramref name="budget"/>.</summary>
    public bool Matches(SvgElement element, ref long budget) => MatchesFrom(Compounds.Length - 1, element, ref budget);

    private bool MatchesFrom(int index, SvgElement element, ref long budget)
    {
        if (--budget < 0) return false;
        if (!Compounds[index].Matches(element)) return false;
        if (index == 0) return true;
        switch (Combinators[index - 1])
        {
            case '>':
                return element.Parent is { } parent && MatchesFrom(index - 1, parent, ref budget);
            case '+':
                return element.Parent is { } p && element.Index > 0 && MatchesFrom(index - 1, p.Children[element.Index - 1], ref budget);
            case '~':
                if (element.Parent is { } siblings)
                {
                    for (int i = element.Index - 1; i >= 0; i--)
                    {
                        if (MatchesFrom(index - 1, siblings.Children[i], ref budget)) return true;
                        if (budget < 0) return false;
                    }
                }
                return false;
            default:
                for (SvgElement? ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
                {
                    if (MatchesFrom(index - 1, ancestor, ref budget)) return true;
                    if (budget < 0) return false;
                }
                return false;
        }
    }
}

/// <summary>
/// The CSS this reader takes from a <c>style</c> element: rules whose selectors are built from type, <c>*</c>,
/// <c>.class</c>, <c>#id</c>, <c>[attribute]</c> tests and a few structural pseudo-classes, joined by the four
/// combinators. Anything else in a selector is reported and that selector left out; at-rules are skipped.
/// </summary>
internal static class SvgStyleSheet
{
    /// <summary>Parses a sheet and appends its rules. <paramref name="keep"/> says which properties are worth keeping.</summary>
    public static void Parse(string css, List<SvgStyleRule> rules, Func<string, bool> keep, Action<SvgIssueKind, string> issue)
    {
        css = StripComments(css);
        int p = 0;
        while (true)
        {
            p = SkipWhitespaceAndMarkup(css, p);
            if (p >= css.Length) return;

            if (css[p] == '@')
            {
                int nameEnd = p + 1;
                while (nameEnd < css.Length && (char.IsAsciiLetter(css[nameEnd]) || css[nameEnd] == '-')) nameEnd++;
                string name = css[(p + 1)..nameEnd].ToLowerInvariant();
                int stop = FindAny(css, nameEnd, ';', '{');
                int after = stop < 0 ? css.Length : css[stop] == ';' ? stop + 1 : SkipBlock(css, stop);
                if (name == "media" && stop >= 0 && css[stop] == '{')
                {
                    // A media rule whose queries are plain media types is decided here: for a screen its rules are
                    // read as if they stood outside it, for anything else they are not there. A query with a
                    // condition (a width, a colour scheme) is not evaluated, and is reported.
                    switch (MediaApplies(css.AsSpan((nameEnd)..stop)))
                    {
                        case true:
                            Parse(css[(stop + 1)..(css[after - 1] == '}' ? after - 1 : after)], rules, keep, issue);
                            break;
                        case null:
                            issue(SvgIssueKind.Unsupported, "@media rule with a condition");
                            break;
                    }
                }
                // What changes the picture is said; what cannot (a font for text that is not read, an encoding) is not.
                else if (name is "import" or "supports" or "layer" or "container")
                {
                    issue(SvgIssueKind.Unsupported, $"@{name} rule");
                }
                p = after;
                continue;
            }

            int open = FindAny(css, p, '{', '\0');
            if (open < 0) return;
            string prelude = css[p..open];
            int close = SkipBlock(css, open);
            string body = css[(open + 1)..(css[close - 1] == '}' ? close - 1 : close)];
            p = close;

            var declarations = new List<SvgDeclaration>();
            ParseDeclarations(body, declarations, keep);
            if (declarations.Count == 0) continue;
            SvgDeclaration[] kept = [.. declarations];
            foreach (string selector in SplitTopLevel(prelude, ','))
            {
                if (TryParseSelector(selector.AsSpan().Trim(), out SvgCompoundSelector[]? compounds, out char[]? combinators, out int specificity))
                {
                    rules.Add(new SvgStyleRule
                    {
                        Compounds = compounds!,
                        Combinators = combinators!,
                        Specificity = specificity,
                        Order = rules.Count,
                        Declarations = kept,
                    });
                }
                else
                {
                    string shown = selector.Trim();
                    issue(SvgIssueKind.Unsupported, $"CSS selector '{(shown.Length > 40 ? shown[..40] + "..." : shown)}'");
                }
            }
        }
    }

    /// <summary>The declarations of a block or of a <c>style</c> attribute, in order, lower-cased names, kept when <paramref name="keep"/> says so.</summary>
    public static void ParseDeclarations(string css, List<SvgDeclaration> declarations, Func<string, bool> keep, bool stripComments = false)
    {
        if (stripComments) css = StripComments(css);
        foreach (string declaration in SplitTopLevel(css, ';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            string name = declaration[..colon].Trim();
            // Property names are not case-sensitive; the names of custom properties are.
            if (!name.StartsWith("--", StringComparison.Ordinal)) name = name.ToLowerInvariant();
            if (!keep(name)) continue;
            ReadOnlySpan<char> value = declaration.AsSpan(colon + 1).Trim();
            bool important = false;
            int bang = value.LastIndexOf('!');
            if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..bang].TrimEnd();
            }
            declarations.Add(new SvgDeclaration(name, value.ToString(), important));
        }
    }

    /// <summary>
    /// Whether a media query list applies to a screen: true when one query is <c>all</c> or <c>screen</c> (or the list
    /// is empty), false when every query is another plain media type, null when a query has anything more to it.
    /// </summary>
    internal static bool? MediaApplies(ReadOnlySpan<char> queries)
    {
        if (queries.Trim().IsEmpty) return true;
        bool unknown = false;
        foreach (Range part in queries.Split(','))
        {
            ReadOnlySpan<char> query = queries[part].Trim();
            if (query.StartsWith("only ", StringComparison.OrdinalIgnoreCase)) query = query[5..].Trim();
            if (query.Equals("all", StringComparison.OrdinalIgnoreCase) || query.Equals("screen", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (char c in query)
            {
                if (!char.IsAsciiLetter(c) && c != '-') unknown = true;
            }
        }
        return unknown ? null : false;
    }

    private static string StripComments(string css)
    {
        for (int open = css.IndexOf("/*", StringComparison.Ordinal); open >= 0; open = css.IndexOf("/*", open, StringComparison.Ordinal))
        {
            int close = css.IndexOf("*/", open + 2, StringComparison.Ordinal);
            css = close < 0 ? css[..open] : string.Concat(css.AsSpan(0, open), " ", css.AsSpan(close + 2));
        }
        return css;
    }

    /// <summary>Whitespace, and the <c>&lt;!--</c> and <c>--&gt;</c> that old sheets wrap themselves in.</summary>
    private static int SkipWhitespaceAndMarkup(string css, int p)
    {
        while (p < css.Length)
        {
            if (char.IsWhiteSpace(css[p])) p++;
            else if (string.CompareOrdinal(css, p, "<!--", 0, 4) == 0) p += 4;
            else if (string.CompareOrdinal(css, p, "-->", 0, 3) == 0) p += 3;
            else break;
        }
        return p;
    }

    /// <summary>The first of two characters at or after <paramref name="from"/>, outside strings and brackets; -1 when neither is there.</summary>
    private static int FindAny(string css, int from, char a, char b)
    {
        char quote = '\0';
        int depth = 0;
        for (int i = from; i < css.Length; i++)
        {
            char c = css[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (depth == 0 && (c == a || (b != '\0' && c == b))) return i;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
        }
        return -1;
    }

    /// <summary>The index after the block that opens at <paramref name="open"/>; the end of the text when it never closes.</summary>
    private static int SkipBlock(string css, int open)
    {
        char quote = '\0';
        int depth = 0;
        for (int i = open; i < css.Length; i++)
        {
            char c = css[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i + 1;
        }
        return css.Length;
    }

    /// <summary>Splits at a separator that is outside strings, parentheses and brackets.</summary>
    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        char quote = '\0';
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static bool IsNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c >= 0x80;

    private static bool TryParseSelector(ReadOnlySpan<char> text, out SvgCompoundSelector[]? compounds, out char[]? combinators, out int specificity)
    {
        compounds = null;
        combinators = null;
        specificity = 0;
        var parts = new List<SvgCompoundSelector>();
        var joins = new List<char>();
        int ids = 0, classes = 0, types = 0;
        int p = 0;
        if (text.IsEmpty) return false;
        while (true)
        {
            var compound = new SvgCompoundSelector();
            int start = p;
            if (p < text.Length && text[p] == '*')
            {
                p++;
            }
            else if (p < text.Length && IsNameCharacter(text[p]))
            {
                compound.Type = ReadName(text, ref p);
                types++;
            }
            // A namespace prefix (svg|rect) is not read.
            if (p < text.Length && text[p] == '|') return false;

            while (p < text.Length)
            {
                char c = text[p];
                if (c == '#')
                {
                    p++;
                    if (compound.Id is not null || (compound.Id = ReadName(text, ref p)).Length == 0) return false;
                    ids++;
                }
                else if (c == '.')
                {
                    p++;
                    string name = ReadName(text, ref p);
                    if (name.Length == 0) return false;
                    compound.Classes.Add(name);
                    classes++;
                }
                else if (c == '[')
                {
                    int close = text[p..].IndexOf(']');
                    if (close < 0 || !TryParseAttribute(text[(p + 1)..(p + close)], compound)) return false;
                    p += close + 1;
                    classes++;
                }
                else if (c == ':')
                {
                    p++;
                    if (p < text.Length && text[p] == ':') return false;   // a pseudo-element draws no element of ours
                    string name = ReadName(text, ref p).ToLowerInvariant();
                    if (p < text.Length && text[p] == '(') return false;   // :not(), :nth-child() and the rest
                    switch (name)
                    {
                        case "first-child": compound.FirstChild = true; break;
                        case "last-child": compound.LastChild = true; break;
                        case "only-child": compound.OnlyChild = true; break;
                        case "root": compound.Root = true; break;
                        case "link" or "any-link": compound.Link = true; break;
                        case "hover" or "active" or "focus" or "focus-visible" or "focus-within" or "visited" or "target":
                            compound.Never = true;
                            break;
                        default: return false;
                    }
                    classes++;
                }
                else
                {
                    break;
                }
            }
            if (p == start) return false;
            parts.Add(compound);

            // What follows is the end, or a combinator: whitespace alone is "descendant".
            int beforeSpace = p;
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            if (p >= text.Length) break;
            char join = ' ';
            if (text[p] is '>' or '+' or '~')
            {
                join = text[p++];
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            }
            else if (p == beforeSpace)
            {
                return false;
            }
            joins.Add(join);
        }

        compounds = [.. parts];
        combinators = [.. joins];
        specificity = Math.Min(ids, 255) << 16 | Math.Min(classes, 255) << 8 | Math.Min(types, 255);
        return true;
    }

    private static string ReadName(ReadOnlySpan<char> text, ref int p)
    {
        int start = p;
        while (p < text.Length && IsNameCharacter(text[p])) p++;
        return text[start..p].ToString();
    }

    /// <summary>The inside of <c>[...]</c>: a name, or a name, an operator and a value that may be quoted.</summary>
    private static bool TryParseAttribute(ReadOnlySpan<char> inner, SvgCompoundSelector compound)
    {
        inner = inner.Trim();
        int p = 0;
        string name = ReadName(inner, ref p);
        if (name.Length == 0) return false;
        ReadOnlySpan<char> rest = inner[p..].TrimStart();
        if (rest.IsEmpty)
        {
            compound.Attributes.Add((name, '\0', ""));
            return true;
        }

        char op = '=';
        if (rest[0] != '=')
        {
            if (rest.Length < 2 || rest[1] != '=' || rest[0] is not ('~' or '|' or '^' or '$' or '*')) return false;
            op = rest[0];
            rest = rest[1..];
        }
        rest = rest[1..].Trim();
        if (rest.Length >= 2 && rest[0] is '"' or '\'' && rest[^1] == rest[0]) rest = rest[1..^1];
        else if (rest.IsEmpty || rest.IndexOfAny("\"' ") >= 0) return false;
        compound.Attributes.Add((name, op, rest.ToString()));
        return true;
    }
}
