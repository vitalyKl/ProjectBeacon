namespace ProjectBeacon.Infrastructure.LlamaSwap;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public sealed record LlamaSwapModelSpec(
    string Name,
    string LaunchCommand,
    int ContextSize,
    int Ttl,
    IReadOnlyList<string> ExtraFlags,
    bool Concurrent = false);

public static class LlamaSwapConfigGenerator
{
    private static readonly Regex PlainKey = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    public static string Generate(IReadOnlyList<LlamaSwapModelSpec> models)
    {
        if (models.Count == 0)
            return "models: {}\n";

        // \n regardless of platform: the file is consumed by llama-swap on any OS.
        var used = new Dictionary<string, int>();
        var emitted = new List<(string Key, bool Concurrent)>();
        var sb = new StringBuilder();
        sb.Append("models:\n");
        foreach (var model in models.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var key = ResolveKey(model.Name, used);
            emitted.Add((key, model.Concurrent));
            sb.Append("  ").Append(QuoteKey(key)).Append(":\n");
            sb.Append("    cmd: ").Append(QuoteScalar(BuildCommand(model))).Append('\n');
            if (model.Ttl > 0)
                sb.Append("    ttl: ").Append(model.Ttl.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var resident = emitted.Where(e => e.Concurrent).Select(e => e.Key).ToList();
        if (resident.Count == 0)
            return sb.ToString();

        sb.Append("groups:\n");
        sb.Append("  resident:\n");
        sb.Append("    swap: false\n");
        sb.Append("    exclusive: false\n");
        sb.Append("    persistent: true\n");
        sb.Append("    members:\n");
        foreach (var key in resident)
            sb.Append("      - ").Append(QuoteKey(key)).Append('\n');

        var swap = emitted.Where(e => !e.Concurrent).Select(e => e.Key).ToList();
        if (swap.Count == 0)
            return sb.ToString();

        sb.Append("  swap:\n");
        sb.Append("    swap: true\n");
        sb.Append("    exclusive: true\n");
        sb.Append("    members:\n");
        foreach (var key in swap)
            sb.Append("      - ").Append(QuoteKey(key)).Append('\n');
        return sb.ToString();
    }

    public static string BuildCommand(LlamaSwapModelSpec model)
    {
        var parts = new List<string> { model.LaunchCommand.Trim() };
        if (model.ContextSize > 0 &&
            !model.LaunchCommand.Contains("--ctx-size", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"--ctx-size {model.ContextSize}");
        }
        parts.AddRange(model.ExtraFlags
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim()));
        return string.Join(" ", parts);
    }

    private static string ResolveKey(string name, IDictionary<string, int> used)
    {
        if (!used.TryGetValue(name, out var count))
        {
            used[name] = 1;
            return name;
        }

        count++;
        var candidate = $"{name}-{count}";
        while (used.ContainsKey(candidate))
        {
            count++;
            candidate = $"{name}-{count}";
        }
        used[name] = count;
        used[candidate] = 1;
        return candidate;
    }

    private static string QuoteKey(string key) => PlainKey.IsMatch(key) ? key : QuoteScalar(key);

    private static string QuoteScalar(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
