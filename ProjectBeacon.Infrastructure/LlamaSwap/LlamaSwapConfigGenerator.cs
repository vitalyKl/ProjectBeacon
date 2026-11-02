namespace ProjectBeacon.Infrastructure.LlamaSwap;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
/// <summary>
/// One model row for the llama-swap config: name, launch command, context size, TTL, extra flags, and whether it may stay resident.
/// </summary>
public sealed record LlamaSwapModelSpec(
    string Name,
    string LaunchCommand,
    int ContextSize,
    int Ttl,
    IReadOnlyList<string> ExtraFlags,
    bool Concurrent = false);
/// <summary>
/// Writes llama-swap YAML. Concurrent backends go in groups.resident with swap false, exclusive false, and persistent true. Others stay in the swap group.
/// </summary>
public static class LlamaSwapConfigGenerator
{
    private static readonly Regex PlainKey = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex ExecutableToken = new(@"^\s*(?:""(?<double>[^""]+)""|'(?<single>[^']+)'|(?<bare>\S+))", RegexOptions.Compiled);
    private static readonly Regex JinjaFlag = new(@"(?<!\S)--jinja(?!\S)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NoJinjaFlag = new(@"(?<!\S)--no-jinja(?!\S)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        var extraFlags = model.ExtraFlags
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToArray();
        var command = EnsureJinjaForLlamaServer(model.LaunchCommand.Trim(), extraFlags);
        var parts = new List<string> { command };
        if (model.ContextSize > 0 &&
            !model.LaunchCommand.Contains("--ctx-size", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"--ctx-size {model.ContextSize}");
        }
        parts.AddRange(extraFlags);
        return string.Join(" ", parts);
    }


    /// <summary>True when the launch command starts the llama.cpp HTTP server.</summary>
    public static bool IsLlamaServerCommand(string? command)
    {
        var token = ExecutableToken.Match(command ?? string.Empty);
        if (!token.Success)
            return false;

        var executable = ExecutableValue(token).Replace('\\', '/');
        var fileName = executable[(executable.LastIndexOf('/') + 1)..];
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^4];
        return string.Equals(fileName, "llama-server", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks the effective Jinja setting, including an explicit --no-jinja.</summary>
    public static bool HasJinjaEnabled(string? command)
    {
        var value = command ?? string.Empty;
        return JinjaFlag.IsMatch(value) && !NoJinjaFlag.IsMatch(value);
    }

    private static string EnsureJinjaForLlamaServer(string command, IReadOnlyList<string> extraFlags)
    {
        var jinjaInExtraFlags = extraFlags.Any(flag => JinjaFlag.IsMatch(flag));
        var noJinjaInExtraFlags = extraFlags.Any(flag => NoJinjaFlag.IsMatch(flag));
        if (!IsLlamaServerCommand(command) || HasJinjaEnabled(command) || jinjaInExtraFlags
            || NoJinjaFlag.IsMatch(command) || noJinjaInExtraFlags)
            return command;

        // Place the flag immediately after the executable so it precedes options such as
        // --chat-template, for which llama.cpp requires Jinja to be enabled first.
        var token = ExecutableToken.Match(command);
        return token.Success ? command.Insert(token.Index + token.Length, " --jinja") : command;
    }

    private static string ExecutableValue(Match token) =>
        token.Groups["double"].Success ? token.Groups["double"].Value
        : token.Groups["single"].Success ? token.Groups["single"].Value
        : token.Groups["bare"].Value;

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
