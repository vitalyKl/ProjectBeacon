namespace ProjectBeacon.Cli.Mcp;

internal sealed class McpEnvironment
{
    private readonly Func<string, string?> _lookup;

    private McpEnvironment(Func<string, string?> lookup) => _lookup = lookup;

    public static McpEnvironment Process { get; } = new(name => Environment.GetEnvironmentVariable(name));
    public static McpEnvironment Empty { get; } = new(_ => null);

    public static McpEnvironment Of(params (string Name, string? Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return new(name => map.GetValueOrDefault(name));
    }

    public string? Get(string name) => _lookup(name);
}
