namespace ProjectBeacon.Cli.Mcp;

using System.Collections.Concurrent;

internal sealed class McpEnvironment
{
    private readonly Func<string, string?> _lookup;
    private readonly ConcurrentDictionary<string, string> _sessionValues = new(StringComparer.Ordinal);

    private McpEnvironment(Func<string, string?> lookup) => _lookup = lookup;

    // Each MCP server process owns its own overlay. Values discovered during a chat turn
    // (for example the task created by create_task) never leak into another process/session.
    public static McpEnvironment Process => new(name => Environment.GetEnvironmentVariable(name));
    public static McpEnvironment Empty => new(_ => null);

    public static McpEnvironment Of(params (string Name, string? Value)[] vars)
    {
        var map = vars.Where(v => v.Value is not null)
            .ToDictionary(v => v.Name, v => v.Value!, StringComparer.Ordinal);
        return new(name => map.GetValueOrDefault(name));
    }

    public string? Get(string name) =>
        _sessionValues.TryGetValue(name, out var value) ? value : _lookup(name);

    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            _sessionValues.TryRemove(name, out _);
        else
            _sessionValues[name] = value;
    }
}
