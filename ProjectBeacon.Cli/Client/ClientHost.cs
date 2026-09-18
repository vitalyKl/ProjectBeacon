namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Enums;

public static class ClientHost
{
    public static string Fingerprint() =>
        $"{Environment.MachineName}:{Environment.UserName}:{Environment.OSVersion.Platform}";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var url = EnvOrArg(args, "--url", "BEACON_URL");
        var token = EnvOrArg(args, "--token", "BEACON_CLIENT_TOKEN");
        var login = Arg(args, "--login");
        var password = Arg(args, "--password");
        var name = Arg(args, "--name") ?? Environment.MachineName;
        var storePath = Arg(args, "--store");

        var store = ClientStore.Load(storePath);
        if (!string.IsNullOrWhiteSpace(url))
            store.Url = url.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(token))
            store.Token = token;

        if (args.Length > 1 && string.Equals(args[1], "enroll", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password)))
        {
            if (string.IsNullOrWhiteSpace(store.Url))
            {
                Console.Error.WriteLine("beacon client enroll --url <api> --login <user> --password <pass>");
                return 2;
            }
            var enrolled = await EnrollAsync(store.Url, login!, password!, name, ct);
            if (enrolled is null)
                return 1;
            store.Token = enrolled.Value.Token;
            store.DeviceId = enrolled.Value.Id;
            store.Save(storePath);
            Console.Error.WriteLine($"Enrolled device {store.DeviceId}. Token stored in client.json (not printed).");
        }

        if (string.IsNullOrWhiteSpace(store.Url) || string.IsNullOrWhiteSpace(store.Token))
        {
            Console.Error.WriteLine("beacon client --url <api> --token <bcd_…>");
            Console.Error.WriteLine("Or: beacon client enroll --url <api> --login <user> --password <pass>");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = new Uri(store.Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Token);
        await using var llama = new ClientLlamaSwap();

        Console.Error.WriteLine($"beacon client connected to {store.Url}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = WorkstationSettings.Load();
                await SyncLlamaAsync(http, llama, settings, ct);
                var heartbeat = await http.PostAsJsonAsync("/v1/devices/me/heartbeat", new
                {
                    probeJson = WorkstationActions.ProbeJson(llama.StatusWire()),
                    workstationJson = JsonSerializer.Serialize(settings)
                }, ct);
                if (!heartbeat.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"heartbeat {((int)heartbeat.StatusCode)}");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                var claimed = await http.GetAsync("/v1/devices/me/commands?wait=25", ct);
                if (claimed.StatusCode == System.Net.HttpStatusCode.NoContent)
                    continue;
                if (!claimed.IsSuccessStatusCode)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                var command = await claimed.Content.ReadFromJsonAsync<CommandWire>(Json, ct);
                if (command is null)
                    continue;
                var (ok, result, error) = await ExecuteAsync(command, llama, ct);
                await http.PostAsJsonAsync($"/v1/commands/{command.Id}/complete", new
                {
                    success = ok,
                    resultJson = result,
                    error
                }, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
        return 0;
    }

    private static async Task SyncLlamaAsync(HttpClient http, ClientLlamaSwap llama, WorkstationSettings settings, CancellationToken ct)
    {
        try
        {
            var response = await http.GetAsync("/v1/devices/me/llamaswap-config", ct);
            if (!response.IsSuccessStatusCode)
                return;
            var config = await response.Content.ReadFromJsonAsync<LlamaSwapConfigWire>(Json, ct);
            if (config is null)
                return;
            var port = settings.LlamaSwapPort > 0 ? settings.LlamaSwapPort : config.Port;
            await llama.TickAsync(config.Yaml ?? "models: {}\n", port, settings.LlamaSwapBin, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"llama-swap sync: {ex.Message}");
        }
    }

    internal static async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(CommandWire command, ClientLlamaSwap llama, CancellationToken ct)
    {
        try
        {
            var kind = command.Kind;
            var payload = command.PayloadJson ?? "{}";
            if (kind == WorkstationCommandKind.ReloadProxy)
            {
                return (true, JsonSerializer.Serialize(llama.StatusWire()), null);
            }
            if (kind == WorkstationCommandKind.UnloadProxy)
            {
                await llama.UnloadAsync(ct);
                return (true, JsonSerializer.Serialize(llama.StatusWire()), null);
            }
            string result = kind switch
            {
                WorkstationCommandKind.Probe => WorkstationActions.ProbeJson(llama.StatusWire()),
                WorkstationCommandKind.ListDir => WorkstationActions.ListDir(ReadPath(payload)),
                WorkstationCommandKind.ScanGguf => WorkstationActions.ScanGguf(ReadPath(payload) ?? WorkstationSettings.Load().ModelsRoot),
                WorkstationCommandKind.InitProject => WorkstationActions.InitProject(payload),
                WorkstationCommandKind.ApplyOpencode => WorkstationActions.ApplyOpencode(payload),
                WorkstationCommandKind.Install => WorkstationActions.Install(payload),
                _ => throw new InvalidOperationException($"Unknown command {kind}.")
            };
            return (true, result, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string? ReadPath(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(Guid Id, string Token)?> EnrollAsync(string url, string login, string password, string name, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        var loginRes = await http.PostAsJsonAsync("/v1/auth/login", new { login, password }, ct);
        if (!loginRes.IsSuccessStatusCode)
        {
            Console.Error.WriteLine("enroll: login failed");
            return null;
        }
        var loginJson = await loginRes.Content.ReadFromJsonAsync<JsonElement>(ct);
        var jwt = loginJson.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(jwt))
            return null;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var create = await http.PostAsJsonAsync("/v1/devices", new { name, fingerprint = Fingerprint() }, ct);
        if (!create.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"enroll: {(int)create.StatusCode} {await create.Content.ReadAsStringAsync(ct)}");
            return null;
        }
        var device = await create.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = device.GetProperty("id").GetGuid();
        var token = device.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(token))
            return null;
        return (id, token);
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    private static string? EnvOrArg(string[] args, string name, string env) =>
        Arg(args, name) ?? Environment.GetEnvironmentVariable(env);

    public sealed class CommandWire
    {
        public Guid Id { get; set; }
        public WorkstationCommandKind Kind { get; set; }
        public string? PayloadJson { get; set; }
    }

    public sealed class LlamaSwapConfigWire
    {
        public string? Yaml { get; set; }
        public int Port { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
