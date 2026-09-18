namespace ProjectBeacon.Cli.Client;

using System.Text.Json;

public sealed class ClientStore
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public Guid DeviceId { get; set; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon", "client.json");

    public static ClientStore Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new ClientStore();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClientStore>(json, JsonOptions) ?? new ClientStore();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed class WorkstationSettings
{
    public string? ModelsRoot { get; set; }
    public string? LlamaCppBin { get; set; }
    public string? LlamaSwapBin { get; set; }
    public string? OpencodeDataDir { get; set; }
    public string? ProjectsRoot { get; set; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon", "workstation.json");

    public static string DefaultModelsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon", "models");

    public static WorkstationSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new WorkstationSettings
            {
                ModelsRoot = DefaultModelsRoot,
                ProjectsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects")
            };
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WorkstationSettings>(json, JsonOptions) ?? new WorkstationSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
