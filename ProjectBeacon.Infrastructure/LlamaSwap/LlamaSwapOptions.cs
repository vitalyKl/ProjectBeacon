namespace ProjectBeacon.Infrastructure.LlamaSwap;
/// <summary>
/// Bin path, port, config path, and project id for the in-process llama-swap supervisor. Port defaults to 8080.
/// </summary>
public sealed class LlamaSwapOptions
{
    public string? BinPath { get; init; }
    public int Port { get; init; } = 8080;
    public string? ConfigPath { get; init; }
    public Guid? ProjectId { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BinPath) && ProjectId is not null;

    public static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectBeacon", "llama-swap", "config.yaml");

    public static LlamaSwapOptions FromEnvironment()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("BEACON_LLAMASWAP_PORT"), out var p) ? p : 8080;
        var projectId = Guid.TryParse(Environment.GetEnvironmentVariable("BEACON_PROJECT_ID"), out var id) ? (Guid?)id : null;
        return new LlamaSwapOptions
        {
            BinPath = Environment.GetEnvironmentVariable("BEACON_LLAMASWAP_BIN"),
            Port = port,
            ConfigPath = Environment.GetEnvironmentVariable("BEACON_LLAMASWAP_CONFIG") ?? DefaultConfigPath,
            ProjectId = projectId,
        };
    }
}
