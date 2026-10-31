using System.Text;

namespace MMLauncher.Core.Steam;

/// <summary>
/// Minimal reader for Valve's text KeyValues format (.vdf / .acf files), e.g.
///   "libraryfolders" { "0" { "path" "C:\\Program Files (x86)\\Steam" } }
/// A node is either a string value or a dictionary of child nodes.
/// </summary>
public sealed class VdfNode
{
    public string? Value { get; init; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.TryGetValue(key, out var n) ? n : null;

    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new VdfNode();
        ParseInto(root, tokens);
        return root;
    }

    private static void ParseInto(VdfNode node, IEnumerator<string> tokens)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key == "}") return;
            if (key == "{") continue; // stray brace; ignore
            if (!tokens.MoveNext()) return;
            var next = tokens.Current;
            if (next == "{")
            {
                var child = new VdfNode();
                ParseInto(child, tokens);
                node.Children[key] = child;
            }
            else
            {
                node.Children[key] = new VdfNode { Value = next };
            }
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c is '{' or '}') { yield return c.ToString(); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        char e = text[i + 1];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i++]);
                }
                i++; // closing quote
                yield return sb.ToString();
                continue;
            }
            // unquoted token
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            yield return text[start..i];
        }
    }
}
