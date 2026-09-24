namespace ProjectBeacon.Cli.Client;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

public sealed class WorkstationSettings
{
    public string? ModelsRoot { get; set; }
    public string? LlamaCppBin { get; set; }
    public string? LlamaSwapBin { get; set; }
    public int LlamaSwapPort { get; set; } = 8080;
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
