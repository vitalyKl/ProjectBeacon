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

        var result = WorkstationActions.InitProject(_dir, payload);
        Assert.True(result.Success);
        Assert.True(Directory.Exists(Path.Combine(path, ".opencode", "data")));
        var gitignore = File.ReadAllText(Path.Combine(path, ".gitignore"));
        Assert.Contains(".opencode/data/", gitignore);
        Assert.Contains(".env", gitignore);
        var opc = File.ReadAllText(Path.Combine(path, "opencode.json"));
        Assert.Contains("beacon", opc);
        Assert.Contains("\"read\": \"deny\"", opc);
        Assert.Contains("beacon-local/qwen", opc);
        Assert.Contains(path.Replace("\\", "\\\\"), result.Value!.Replace("/", "\\"));
    }

    [Fact]
    public void ApplyOpencode_MergesMcp()
    {
        var path = Path.Combine(_dir, "merge");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "opencode.json"), """{"$schema":"https://opencode.ai/config.json","autoupdate":false}""");
        var result = WorkstationActions.ApplyOpencode(_dir, JsonSerializer.Serialize(new
        {
            path,
            mcp = new { context7 = new { type = "remote", url = "https://mcp.context7.com/mcp", enabled = true } }
        }));
        Assert.True(result.Success);
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
        var result = WorkstationActions.ApplyOpencode(_dir, payload.ToJsonString());
        Assert.True(result.Success);
        var opc = File.ReadAllText(Path.Combine(path, "opencode.json"));
        Assert.Contains("beacon-local", opc);
        Assert.Contains("127.0.0.1:8080", opc);
        Assert.Contains("\"build\"", opc);
    }

    [Fact]
    public void ApplyOpencode_McpReplace_DropsPreviousServers()
    {
        var path = Path.Combine(_dir, "replace");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "opencode.json"), """{"mcp":{"old":{"type":"remote","url":"https://old"}}}""");
        var payload = new JsonObject
        {
            ["path"] = path,
            ["mcpReplace"] = true,
            ["mcp"] = new JsonObject
            {
                ["beacon"] = new JsonObject { ["type"] = "local", ["command"] = new JsonArray("beacon", "mcp") }
            }
        };
        var result = WorkstationActions.ApplyOpencode(_dir, payload.ToJsonString());
        Assert.True(result.Success);
        var opc = File.ReadAllText(Path.Combine(path, "opencode.json"));
        Assert.Contains("beacon", opc);
        Assert.DoesNotContain("https://old", opc);
    }

    [Fact]
    public void SaveWorkstation_WritesSettingsFile()
    {
        var file = Path.Combine(_dir, "workstation.json");
        WorkstationActions.SaveWorkstation("""{"modelsRoot":"D:\\models","llamaSwapPort":9090}""", file);
        var loaded = WorkstationSettings.Load(file);
        Assert.Equal(@"D:\models", loaded.ModelsRoot);
        Assert.Equal(9090, loaded.LlamaSwapPort);
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
        var result = WorkstationActions.ListDir(_dir, _dir);
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Value!);
        Assert.Equal(Path.GetFullPath(_dir), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void ListDir_PathEqualsRoot_Allowed()
    {
        var result = WorkstationActions.ListDir(_dir, _dir);
        Assert.True(result.Success);
    }

    [Fact]
    public void ListDir_OutsideRoot_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "beacon-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outside);
            var result = WorkstationActions.ListDir(_dir, outside);
            Assert.False(result.Success);
            Assert.Contains("escapes", result.Error);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void ListDir_MissingRoot_Fails()
    {
        var result = WorkstationActions.ListDir("", _dir);
        Assert.False(result.Success);
        Assert.Equal("missing root", result.Error);
    }

    [Fact]
    public void ListDir_NonExistentDir_Fails()
    {
        var missing = Path.Combine(_dir, "does-not-exist");
        var result = WorkstationActions.ListDir(_dir, missing);
        Assert.False(result.Success);
        Assert.Contains("directory not found", result.Error);
    }

    [Fact]
    public void ScanGguf_MissingRoot_Fails()
    {
        var result = WorkstationActions.ScanGguf("", _dir);
        Assert.False(result.Success);
        Assert.Equal("missing root", result.Error);
    }

    [Fact]
    public void ScanGguf_OutsideRoot_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "beacon-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outside);
            var result = WorkstationActions.ScanGguf(_dir, outside);
            Assert.False(result.Success);
            Assert.Contains("escapes", result.Error);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void ScanGguf_MissingDir_ReturnsEmpty()
    {
        var missing = Path.Combine(_dir, "no-gguf-here");
        var result = WorkstationActions.ScanGguf(_dir, missing);
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Value!);
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal(0, files.GetArrayLength());
    }

    [Fact]
    public void InitProject_OutsideRoot_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "beacon-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            var payload = JsonSerializer.Serialize(new { path = outside, createGit = false });
            var result = WorkstationActions.InitProject(_dir, payload);
            Assert.False(result.Success);
            Assert.Contains("escapes", result.Error);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void InitProject_MissingRoot_Fails()
    {
        var path = Path.Combine(_dir, "app");
        var payload = JsonSerializer.Serialize(new { path, createGit = false });
        var result = WorkstationActions.InitProject("", payload);
        Assert.False(result.Success);
        Assert.Equal("missing root", result.Error);
    }

    [Fact]
    public void InitProject_MissingPath_Fails()
    {
        var payload = JsonSerializer.Serialize(new { createGit = false });
        var result = WorkstationActions.InitProject(_dir, payload);
        Assert.False(result.Success);
        Assert.Equal("path is required.", result.Error);
    }

    [Fact]
    public void ApplyOpencode_OutsideRoot_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "beacon-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outside);
            var payload = JsonSerializer.Serialize(new { path = outside });
            var result = WorkstationActions.ApplyOpencode(_dir, payload);
            Assert.False(result.Success);
            Assert.Contains("escapes", result.Error);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void ApplyOpencode_MissingRoot_Fails()
    {
        var path = Path.Combine(_dir, "merge");
        Directory.CreateDirectory(path);
        var payload = JsonSerializer.Serialize(new { path });
        var result = WorkstationActions.ApplyOpencode("", payload);
        Assert.False(result.Success);
        Assert.Equal("missing root", result.Error);
    }

    [Fact]
    public void ApplyOpencode_MissingPath_Fails()
    {
        var result = WorkstationActions.ApplyOpencode(_dir, "{}");
        Assert.False(result.Success);
        Assert.Equal("path is required.", result.Error);
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

    [Fact]
    public void Which_PrefersCmdTwinOverPosixShim()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var bin = Path.Combine(_dir, "binrank");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "fakebin"), "#!/bin/sh\nexec true\n");
        File.WriteAllText(Path.Combine(bin, "fakebin.cmd"), "@echo off\r\n");
        var found = WorkstationActions.Which("fakebin", bin);
        Assert.Equal(Path.Combine(bin, "fakebin.cmd"), found);
    }

    [Fact]
    public void Which_PrefersExeOverCmdAndShim()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var bin = Path.Combine(_dir, "binrank-exe");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "fakebin"), "#!/bin/sh\nexec true\n");
        File.WriteAllText(Path.Combine(bin, "fakebin.cmd"), "@echo off\r\n");
        File.WriteAllText(Path.Combine(bin, "fakebin.exe"), "MZ");
        var found = WorkstationActions.Which("fakebin", bin);
        Assert.Equal(Path.Combine(bin, "fakebin.exe"), found);
    }

    [Fact]
    public void Which_FallsBackToShimWhenNoLaunchableTwin()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var bin = Path.Combine(_dir, "binrank-shim");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "fakebin"), "#!/bin/sh\nexec true\n");
        var found = WorkstationActions.Which("fakebin", bin);
        Assert.Equal(Path.Combine(bin, "fakebin"), found);
    }
}
