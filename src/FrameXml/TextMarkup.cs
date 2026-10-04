using System.Globalization;
using System.Text;
using FrameXml.Objects;

namespace FrameXml;

/// <summary>Escape sequences in UI text: |cAARRGGBB ... |r colors, |Hlink|htext|h hyperlinks, |n newlines and || for a bar.</summary>
public static class TextMarkup
{
    public readonly record struct Run(string Text, Color4? Color);

    public static IEnumerable<Run> Parse(string text)
    {
        var current = new StringBuilder();
        Color4? color = null;
        var runs = new List<Run>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '|' || i + 1 >= text.Length)
            {
                current.Append(c);
                continue;
            }
            var code = text[i + 1];
            switch (code)
            {
                case '|':
                    current.Append('|');
                    i++;
                    continue;
                case 'n':
                    current.Append('\n');
                    i++;
                    continue;
                case 'c' or 'C' when i + 9 < text.Length && uint.TryParse(text.AsSpan(i + 2, 8), NumberStyles.HexNumber, null, out var argb):
                    Flush();
                    color = new Color4(((argb >> 16) & 0xff) / 255f, ((argb >> 8) & 0xff) / 255f, (argb & 0xff) / 255f, ((argb >> 24) & 0xff) / 255f);
                    i += 9;
                    continue;
                case 'r' or 'R':
                    Flush();
                    color = null;
                    i++;
                    continue;
                case 'H':
                    var linkEnd = text.IndexOf("|h", i + 2, StringComparison.Ordinal);
                    if (linkEnd >= 0)
                    {
                        i = linkEnd + 1;
                        continue;
                    }
                    break;
                case 'h':
                    i++;
                    continue;
                case 'T':
                    var textureEnd = text.IndexOf("|t", i + 2, StringComparison.Ordinal);
                    if (textureEnd >= 0)
                    {
                        i = textureEnd + 1;
                        continue;
                    }
                    break;
            }
            current.Append(c);
        }
        Flush();
        return runs;

        void Flush()
        {
            if (current.Length > 0)
                runs.Add(new Run(current.ToString(), color));
            current.Clear();
        }
    }

    public static string Strip(string text) => string.Concat(Parse(text).Select(r => r.Text));
}
