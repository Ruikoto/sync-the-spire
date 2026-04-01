namespace SyncTheSpire.Helpers;

/// <summary>
/// Minimal VDF/ACF parser for Steam config files.
/// Handles "key" "value" and "key" { ... } structure.
/// </summary>
public static class VdfParser
{
    public static Dictionary<string, object> Parse(string content)
    {
        var tokens = Tokenize(content);
        var pos = 0;
        return ParseSection(tokens, ref pos);
    }

    private static List<string> Tokenize(string content)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == '"')
            {
                var end = content.IndexOf('"', i + 1);
                if (end == -1) break;
                // handle escaped quotes (rare in VDF but just in case)
                while (end > 0 && content[end - 1] == '\\')
                    end = content.IndexOf('"', end + 1);
                if (end == -1) break;
                tokens.Add(content[(i + 1)..end]);
                i = end + 1;
            }
            else if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                // line comment
                var nl = content.IndexOf('\n', i);
                i = nl == -1 ? content.Length : nl + 1;
            }
            else
            {
                i++;
            }
        }
        return tokens;
    }

    private static Dictionary<string, object> ParseSection(List<string> tokens, ref int pos)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (pos < tokens.Count)
        {
            var token = tokens[pos];
            if (token == "}") { pos++; return dict; }

            var key = token;
            pos++;
            if (pos >= tokens.Count) break;

            if (tokens[pos] == "{")
            {
                pos++;
                dict[key] = ParseSection(tokens, ref pos);
            }
            else
            {
                dict[key] = tokens[pos];
                pos++;
            }
        }
        return dict;
    }
}
