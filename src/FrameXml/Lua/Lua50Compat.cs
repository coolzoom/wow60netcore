using System.Text;

namespace FrameXml.Lua;

/// <summary>
/// Rewrites Lua 5.0 source (the client's FrameScript is Lua 5.0) so a Lua 5.2 interpreter runs it:
/// <list type="bullet">
/// <item><c>for k, v in t do</c> iterating a table directly becomes <c>for k, v in __lua50_iter(t) do</c>.</item>
/// <item>Vararg functions get the implicit <c>arg</c> table (with <c>arg.n</c>).</item>
/// <item><c>goto</c>, a plain identifier in 5.0, is renamed.</item>
/// <item>Escapes 5.0 accepted as the plain character (<c>"\%"</c>, <c>"\["</c>) lose their backslash.</item>
/// </list>
/// Insertions never add newlines, so error line numbers stay correct.
/// </summary>
public static class Lua50Compat
{
    public const string IteratorFunction = "__lua50_iter";
    private const string VarargPrologue = " local arg = {n = select('#', ...), ...};";

    private enum Kind { Name, Symbol, Other }

    private readonly record struct Token(Kind Kind, int Start, int End, string Text);

    public static string Transform(string source)
    {
        var invalidEscapes = new List<int>();
        var tokens = Tokenize(source, invalidEscapes);
        var inserts = new List<(int Position, string Text, int Order)>();
        var replaces = new Dictionary<int, (int End, string Text)>();
        foreach (var backslash in invalidEscapes)
            replaces[backslash] = (backslash + 1, "");

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != Kind.Name)
                continue;
            switch (token.Text)
            {
                case "goto":
                    replaces[token.Start] = (token.End, "goto_");
                    break;
                case "function":
                    if (VarargEnd(tokens, i) is { } close)
                        inserts.Add((tokens[close].End, VarargPrologue, 0));
                    break;
                case "for":
                    if (GenericForRange(tokens, i) is var (first, last))
                    {
                        inserts.Add((tokens[first].Start, IteratorFunction + "(", 1));
                        inserts.Add((tokens[last].End, ")", 0));
                    }
                    break;
            }
        }

        if (inserts.Count == 0 && replaces.Count == 0)
            return source;

        var output = new StringBuilder(source.Length + inserts.Count * 16);
        var ordered = inserts.OrderBy(x => x.Position).ThenBy(x => x.Order).ToList();
        var next = 0;
        for (var pos = 0; pos <= source.Length; pos++)
        {
            while (next < ordered.Count && ordered[next].Position == pos)
                output.Append(ordered[next++].Text);
            if (pos == source.Length)
                break;
            if (replaces.TryGetValue(pos, out var replace))
            {
                output.Append(replace.Text);
                pos = replace.End - 1;
                continue;
            }
            output.Append(source[pos]);
        }
        return output.ToString();
    }

    /// <summary>Index of the ')' closing a parameter list that ends in "...", or null.</summary>
    private static int? VarargEnd(List<Token> tokens, int functionIndex)
    {
        var open = functionIndex + 1;
        while (open < tokens.Count && tokens[open].Text != "(")
        {
            // Only names, '.' and ':' may sit between 'function' and its parameter list.
            if (tokens[open].Kind != Kind.Name && tokens[open].Text is not ("." or ":"))
                return null;
            open++;
        }
        for (var i = open + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Text == ")")
                return i > open + 1 && tokens[i - 1].Text == "..." ? i : null;
            if (tokens[i].Text == "(")
                return null;
        }
        return null;
    }

    /// <summary>For "for names in explist do", the first and last explist tokens.</summary>
    private static (int First, int Last)? GenericForRange(List<Token> tokens, int forIndex)
    {
        var i = forIndex + 1;
        while (i < tokens.Count && (tokens[i].Kind == Kind.Name && tokens[i].Text != "in" || tokens[i].Text == ","))
            i++;
        if (i >= tokens.Count || tokens[i].Text != "in" || i + 1 >= tokens.Count)
            return null;

        var first = i + 1;
        int brackets = 0, blocks = 0;
        for (var j = first; j < tokens.Count; j++)
        {
            var text = tokens[j].Text;
            if (tokens[j].Kind == Kind.Symbol)
            {
                if (text is "(" or "[" or "{") brackets++;
                else if (text is ")" or "]" or "}") brackets--;
                continue;
            }
            if (tokens[j].Kind != Kind.Name)
                continue;
            if (text == "do" && brackets == 0 && blocks == 0)
                return j > first ? (first, j - 1) : null;
            if (text is "function" or "if" or "do" or "repeat") blocks++;
            else if (text is "end" or "until") blocks--;
        }
        return null;
    }

    private static List<Token> Tokenize(string s, List<int> invalidEscapes)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            var start = i;
            if (c == '-' && At(s, i + 1) == '-')
            {
                i += 2;
                if (LongBracketLevel(s, i) is { } level)
                    i = SkipLongBracket(s, i, level);
                else
                    while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c is '"' or '\'')
            {
                i++;
                while (i < s.Length && s[i] != c)
                {
                    if (s[i] == '\\' && i + 1 < s.Length && !IsLua52Escape(s[i + 1]))
                        invalidEscapes.Add(i);
                    i += s[i] == '\\' ? 2 : 1;
                }
                i = Math.Min(i + 1, s.Length);
                tokens.Add(new Token(Kind.Other, start, i, ""));
                continue;
            }
            if (c == '[' && LongBracketLevel(s, i) is { } stringLevel)
            {
                i = SkipLongBracket(s, i, stringLevel);
                tokens.Add(new Token(Kind.Other, start, i, ""));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new Token(Kind.Name, start, i, s[start..i]));
                continue;
            }
            if (char.IsDigit(c) || c == '.' && char.IsDigit(At(s, i + 1)))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.' ||
                                        (s[i] is '+' or '-' && s[i - 1] is 'e' or 'E'))) i++;
                tokens.Add(new Token(Kind.Other, start, i, ""));
                continue;
            }
            if (c == '.' && At(s, i + 1) == '.' && At(s, i + 2) == '.')
            {
                tokens.Add(new Token(Kind.Symbol, i, i + 3, "..."));
                i += 3;
                continue;
            }
            i++;
            tokens.Add(new Token(Kind.Symbol, start, i, c.ToString()));
        }
        return tokens;
    }

    private static bool IsLua52Escape(char c) => c is 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v' or '\\' or '"' or '\'' or '\n' or '\r' || char.IsDigit(c);

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>Level of a long bracket "[==[" starting at <paramref name="i"/>, or null.</summary>
    private static int? LongBracketLevel(string s, int i)
    {
        if (At(s, i) != '[')
            return null;
        var level = 0;
        while (At(s, i + 1 + level) == '=') level++;
        return At(s, i + 1 + level) == '[' ? level : null;
    }

    private static int SkipLongBracket(string s, int i, int level)
    {
        var close = "]" + new string('=', level) + "]";
        var end = s.IndexOf(close, i + level + 2, StringComparison.Ordinal);
        return end < 0 ? s.Length : end + close.Length;
    }
}
