namespace ProjectBeacon.Cli.Client;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
/// <summary>
/// Persisted control-plane URL, device token, and device id. Default file is client.json beside the user profile data. NeedsWizard is true until both URL and token are set.
/// </summary>
public sealed class ClientStore
{
    private const int ProtectedVersion = 2;

    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public Guid DeviceId { get; set; }
    public int Version { get; set; }

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Token);

    public static bool NeedsWizard(ClientStore store) => !store.HasCredentials;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon", "client.json");

    public static ClientStore Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new ClientStore();
        var json = File.ReadAllText(path);
        var store = JsonSerializer.Deserialize<ClientStore>(json, JsonOptions) ?? new ClientStore();
        if (store.Version >= ProtectedVersion && OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(store.Token))
            store.Token = TryDecryptToken(store.Token);
        return store;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var snapshot = new ClientStore
        {
            Url = Url,
            Token = string.IsNullOrWhiteSpace(Token)
                ? ""
                : OperatingSystem.IsWindows() ? EncryptToken(Token) : Token,
            DeviceId = DeviceId,
            Version = ProtectedVersion
        };
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(temp, json);
            }
            else
            {
                using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(json);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string EncryptToken(string token)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    [SupportedOSPlatform("windows")]
    private static string TryDecryptToken(string encoded)
    {
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(encoded), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            return "";
        }
        catch (FormatException)
        {
            return "";
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
/// <summary>
/// Local workstation.json: model root, llama binaries, port 8080, own-swapper flag, concurrent port base 9000, and history directory. TryRead returns an error for a corrupt file and does not overwrite it. Load throws when that error is set.
/// </summary>
public sealed class WorkstationSettings
{
    public string? ModelsRoot { get; set; }
    public string? LlamaCppBin { get; set; }
    public string? LlamaSwapBin { get; set; }
    public int LlamaSwapPort { get; set; } = 8080;
    public bool UseOwnSwapper { get; set; }
    /// <summary>Workstation-wide native command policy: off, ask, or auto. Defaults to ask.</summary>
    public string CommandPolicy { get; set; } = "ask";
    public int ConcurrentPortBase { get; set; } = 9000;
    /// <summary>Absolute cap for one native pipeline agent turn. The server lease is 75 minutes.</summary>
    public int PipelineTurnTimeoutMinutes { get; set; } = 60;
    /// <summary>Tokens reserved for the model response. Zero uses an automatic 25% of the context window.</summary>
    public int ContextResponseReserveTokens { get; set; }
    /// <summary>Abort a pipeline turn after this many minutes without transcript/tool/form progress.</summary>
    public int PipelineIdleTimeoutMinutes { get; set; } = 15;
    public string? AgentDataDir { get; set; }
    public string? ProjectsRoot { get; set; }
    public string? BeaconPath { get; set; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon", "workstation.json");

    public static string DefaultModelsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon", "models");

    private static WorkstationSettings Defaults() => new()
    {
        ModelsRoot = DefaultModelsRoot,
        ProjectsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects")
    };

    public static (WorkstationSettings Settings, string? Error) TryRead(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return (Defaults(), null);
        try
        {
            var json = File.ReadAllText(path);
            // Migrate the old transcript directory key. Runtime selection is intentionally
            // ignored: the workstation now has one supported native agent runtime.
            var root = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("Workstation settings must be a JSON object.");
            if (!root.ContainsKey("agentDataDir")
                && root.TryGetPropertyValue("opencodeDataDir", out var legacyDataDirectory))
                root["agentDataDir"] = legacyDataDirectory?.DeepClone();
            root.Remove("opencodeDataDir");
            root.Remove("agentRuntime");
            return (JsonSerializer.Deserialize<WorkstationSettings>(root.ToJsonString(), JsonOptions) ?? new WorkstationSettings(), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (Defaults(), $"{path}: {ex.Message}");
        }
    }

    public static WorkstationSettings Load(string? path = null)
    {
        var (settings, error) = TryRead(path);
        if (error is null)
            return settings;
        throw new InvalidDataException($"Could not load workstation settings: {error}. Delete the file or re-save it from the client TUI (s).");
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
