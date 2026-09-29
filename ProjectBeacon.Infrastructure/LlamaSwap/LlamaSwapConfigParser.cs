using System.Globalization;
using System.Text;

namespace ProjectBeacon.Infrastructure.LlamaSwap;

/// <summary>
/// Deterministic inverse of <see cref="LlamaSwapConfigGenerator.Generate"/>. Reads back the
/// generated YAML into <see cref="LlamaSwapModelSpec"/> so the client-side swapper can drive
/// <c>llama-server</c> directly without an external YAML library.
/// </summary>
public static class LlamaSwapConfigParser
{
    public static IReadOnlyList<LlamaSwapModelSpec> Parse(string yaml)
    {
        var cmds = new Dictionary<string, string>(StringComparer.Ordinal);
        var ttls = new Dictionary<string, int>(StringComparer.Ordinal);
        var resident = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();

        string? section = null;
        string? model = null;
        string? group = null;

        var text = (yaml ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (raw.TrimStart().StartsWith('#'))
                continue;

            var line = raw.TrimEnd();
            var indent = line.Length - line.TrimStart(' ').Length;
            var content = line.TrimStart(' ');

            if (indent == 0)
            {
                section = content.StartsWith("models", StringComparison.Ordinal)
                    ? "models"
                    : content.StartsWith("groups", StringComparison.Ordinal)
                        ? "groups"
                        : null;
                model = null;
                group = null;
                continue;
            }

            if (section == "models")
            {
                if (indent == 2 && content.EndsWith(':'))
                {
                    model = UnquoteScalar(content[..^1].Trim());
                    if (model is not null && !cmds.ContainsKey(model))
                        order.Add(model);
                }
                else if (indent == 4 && model is not null)
                {
                    if (content.StartsWith("cmd:", StringComparison.Ordinal))
                        cmds[model] = UnquoteScalar(content[4..].Trim());
                    else if (content.StartsWith("ttl:", StringComparison.Ordinal))
                        ttls[model] = ParseInt(content[4..].Trim());
                }
            }
            else if (section == "groups")
            {
                if (indent == 2 && content.EndsWith(':'))
                {
                    group = UnquoteScalar(content[..^1].Trim());
                }
                else if (indent == 6 && content.StartsWith("- ", StringComparison.Ordinal))
                {
                    var member = UnquoteScalar(content[2..].Trim());
                    if (group == "resident")
                        resident.Add(member);
                }
            }
        }

        var specs = new List<LlamaSwapModelSpec>();
        foreach (var key in order)
        {
            var cmd = cmds.TryGetValue(key, out var c) ? c : "";
            var ttl = ttls.TryGetValue(key, out var t) ? t : 0;
            specs.Add(new LlamaSwapModelSpec(key, cmd, 0, ttl, Array.Empty<string>(), resident.Contains(key)));
        }
        return specs;
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string UnquoteScalar(string value) =>
        value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"') ? Unescape(value[1..^1]) : value;

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }
            var n = s[++i];
            switch (n)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u' when i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }
}
