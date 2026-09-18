namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class WorkstationActionsTests : IDisposable
{
    private readonly string _dir;

    public WorkstationActionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "beacon-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void InitProject_WritesGitignoreAndOpencode()
    {
        var path = Path.Combine(_dir, "app");
        var payload = JsonSerializer.Serialize(new
        {
            path,
            createGit = false,
            historyInProject = true,
            mcp = new
            {
                beacon = new
                {
                    type = "local",
                    command = new[] { "beacon", "mcp", "--root", "." },
                    enabled = true
                }
            },
            model = "beacon-local/qwen"
        });

        var result = WorkstationActions.InitProject(payload);
        Assert.True(Directory.Exists(Path.Combine(path, ".opencode", "data")));
        var gitignore = File.ReadAllText(Path.Combine(path, ".gitignore"));
        Assert.Contains(".opencode/data/", gitignore);
        Assert.Contains(".env", gitignore);
        var opc = File.ReadAllText(Path.Combine(path, "opencode.json"));
        Assert.Contains("beacon", opc);
        Assert.Contains("\"read\": \"deny\"", opc);
        Assert.Contains("beacon-local/qwen", opc);
        Assert.Contains(path.Replace("\\", "\\\\"), result.Replace("/", "\\"));
    }

    [Fact]
    public void ApplyOpencode_MergesMcp()
    {
        var path = Path.Combine(_dir, "merge");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "opencode.json"), """{"$schema":"https://opencode.ai/config.json","autoupdate":false}""");
        WorkstationActions.ApplyOpencode(JsonSerializer.Serialize(new
        {
            path,
            mcp = new { context7 = new { type = "remote", url = "https://mcp.context7.com/mcp", enabled = true } }
        }));
        var opc = File.ReadAllText(Path.Combine(path, "opencode.json"));
        Assert.Contains("autoupdate", opc);
        Assert.Contains("context7", opc);
        Assert.Contains("deny", opc);
    }

    [Fact]
    public void ApplyOpencode_MergesProviderAndAgents()
    {
        var path = Path.Combine(_dir, "prov");
        Directory.CreateDirectory(path);
        var payload = new JsonObject
        {
            ["path"] = path,
            ["model"] = "beacon-local/qwen",
            ["provider"] = new JsonObject
            {
                ["beacon-local"] = new JsonObject
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["options"] = new JsonObject { ["baseURL"] = "http://127.0.0.1:8080/v1" }
                }
            },
            ["agent"] = new JsonObject { ["build"] = new JsonObject { ["model"] = "beacon-local/qwen" } }
        };
        WorkstationActions.ApplyOpencode(payload.ToJsonString());
        var opc = File.ReadAllText(Path.Combine(path, "opencode.json"));
        Assert.Contains("beacon-local", opc);
        Assert.Contains("127.0.0.1:8080", opc);
        Assert.Contains("\"build\"", opc);
    }

    [Fact]
    public void MergeGitignore_Idempotent()
    {
        var file = Path.Combine(_dir, ".gitignore");
        WorkstationActions.MergeGitignore(file);
        WorkstationActions.MergeGitignore(file);
        var lines = File.ReadAllLines(file).Where(l => l.StartsWith(".opencode/data")).Count();
        Assert.Equal(1, lines);
    }

    [Fact]
    public void ListDir_TempExists()
    {
        var json = WorkstationActions.ListDir(_dir);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(Path.GetFullPath(_dir), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void ProbeJson_HasOs()
    {
        var json = WorkstationActions.ProbeJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("os", out _));
        Assert.True(doc.RootElement.TryGetProperty("git", out _));
    }

    [Fact]
    public void Install_Unknown_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkstationActions.Install("""{"id":"not-a-package"}"""));
        Assert.Contains("allowlist", ex.Message);
    }
}
