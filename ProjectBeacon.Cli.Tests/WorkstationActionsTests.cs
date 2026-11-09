namespace ProjectBeacon.Cli.Tests;

using Application.Common;
using ProjectBeacon.Cli.Client;
using System.Text.Json;

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
    public void InitProject_WritesGitignoreAndKeepsAgentDataOutsideProject()
    {
        var full = Path.Combine(_dir, "app");
        var result = WorkstationSetup.InitProject(_dir, JsonSerializer.Serialize(new { path = "app", createGit = false }));

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(full));
        var gitignore = File.ReadAllText(Path.Combine(full, ".gitignore"));
        Assert.Contains(".env", gitignore);
        Assert.Contains("*.gguf", gitignore);
        Assert.False(Directory.Exists(Path.Combine(full, ".beacon-agent")));
        Assert.False(File.Exists(Path.Combine(full, "opencode.json")));
    }

    [Fact]
    public void SaveWorkstation_WritesSettingsFile()
    {
        var file = Path.Combine(_dir, "workstation.json");
        WorkstationSetup.SaveWorkstation("""{"modelsRoot":"D:\\models","llamaSwapPort":9090}""", file);
        var loaded = WorkstationSettings.Load(file);
        Assert.Equal(@"D:\models", loaded.ModelsRoot);
        Assert.Equal(9090, loaded.LlamaSwapPort);
    }

    [Fact]
    public void SaveWorkstation_PersistsContextResponseReserveTokens()
    {
        var file = Path.Combine(_dir, "workstation.json");
        WorkstationSetup.SaveWorkstation("""{"contextResponseReserveTokens":16384}""", file);
        var loaded = WorkstationSettings.Load(file);
        Assert.Equal(16_384, loaded.ContextResponseReserveTokens);
    }

    [Fact]
    public void SaveWorkstation_MigratesLegacyTranscriptDirectoryAndIgnoresRuntimeSelector()
    {
        var file = Path.Combine(_dir, "workstation.json");
        var legacyDirectory = Path.Combine(_dir, ".opencode", "data");

        var json = WorkstationSetup.SaveWorkstation(
            JsonSerializer.Serialize(new { agentRuntime = "opencode", opencodeDataDir = legacyDirectory }),
            file);

        var loaded = WorkstationSettings.Load(file);
        Assert.Equal(legacyDirectory, loaded.AgentDataDir);
        using var saved = JsonDocument.Parse(json);
        Assert.Equal(legacyDirectory, saved.RootElement.GetProperty("agentDataDir").GetString());
        Assert.False(saved.RootElement.TryGetProperty("agentRuntime", out _));
        Assert.False(saved.RootElement.TryGetProperty("opencodeDataDir", out _));
    }

    [Fact]
    public void SaveWorkstation_OverInvalidExistingFile_UsesDefaultsAndSaves()
    {
        var file = Path.Combine(_dir, "workstation.json");
        File.WriteAllText(file, "<html></html>");

        var json = WorkstationSetup.SaveWorkstation("""{"llamaSwapPort":9091}""", file);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(9091, doc.RootElement.GetProperty("llamaSwapPort").GetInt32());
        var (loaded, error) = WorkstationSettings.TryRead(file);
        Assert.Null(error);
        Assert.Equal(9091, loaded.LlamaSwapPort);
    }

    [Fact]
    public void MergeGitignore_Idempotent()
    {
        var file = Path.Combine(_dir, ".gitignore");
        WorkstationSetup.MergeGitignore(file);
        WorkstationSetup.MergeGitignore(file);
        var lines = File.ReadAllLines(file);
        Assert.Equal(1, lines.Count(line => line == ".env"));
        Assert.Equal(1, lines.Count(line => line == "*.gguf"));
    }

    [Fact]
    public void ListDir_TempExists()
    {
        var result = WorkstationBrowse.ListDir(_dir, _dir);
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Value!);
        Assert.Equal(Path.GetFullPath(_dir), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void ListDir_PathEqualsRoot_Allowed()
    {
        var result = WorkstationBrowse.ListDir(_dir, _dir);
        Assert.True(result.Success);
    }

    [Fact]
    public void ListDir_OutsideRoot_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "beacon-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outside);
            var result = WorkstationBrowse.ListDir(_dir, outside);
            Assert.False(result.Success);
            Assert.Contains("escapes", result.Error);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void ListDir_RelativeChild_StaysInsideRoot()
    {
        var child = Path.Combine(_dir, "nested");
        Directory.CreateDirectory(child);
        var result = WorkstationBrowse.ListDir(_dir, "nested");
        Assert.True(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Value!);
        Assert.Equal(Path.GetFullPath(child), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void ListDir_RelativeEscape_Rejected()
    {
        var result = WorkstationBrowse.ListDir(_dir, "../secret");
        Assert.False(result.Success);
        Assert.Equal(WorkspacePath.ParentSegmentNotAllowed, result.Error);
    }

    [Fact]
    public void ListDir_MissingRoot_Fails()
    {
        var result = WorkstationBrowse.ListDir("", _dir);
        Assert.False(result.Success);
        Assert.Equal("missing root", result.Error);
    }

    [Fact]
    public void ListDir_NonExistentDir_Fails()
    {
        var missing = Path.Combine(_dir, "does-not-exist");
        var result = WorkstationBrowse.ListDir(_dir, missing);
        Assert.False(result.Success);
        Assert.Contains("directory not found", result.Error);
    }

    [Fact]
    public void ScanGguf_MissingRoot_Fails()
    {
        var result = WorkstationBrowse.ScanGguf("", _dir);
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
            var result = WorkstationBrowse.ScanGguf(_dir, outside);
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
        var result = WorkstationBrowse.ScanGguf(_dir, missing);
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
            var payload = JsonSerializer.Serialize(new { path = "../" + Path.GetFileName(outside), createGit = false });
            var result = WorkstationSetup.InitProject(_dir, payload);
            Assert.False(result.Success);
            Assert.Equal(WorkspacePath.ParentSegmentNotAllowed, result.Error);
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
        var result = WorkstationSetup.InitProject("", payload);
        Assert.False(result.Success);
        Assert.Equal("missing root", result.Error);
    }

    [Fact]
    public void InitProject_MissingPath_UsesRoot()
    {
        var payload = JsonSerializer.Serialize(new { createGit = false });
        var result = WorkstationSetup.InitProject(_dir, payload);
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(_dir, ".gitignore")));
    }

    [Fact]
    public void InitProject_AbsolutePath_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { path = _dir, createGit = false });
        var result = WorkstationSetup.InitProject(_dir, payload);
        Assert.False(result.Success);
        Assert.Contains("relative", result.Error);
    }

    [Fact]
    public void ProbeJson_HasOs()
    {
        var json = WorkstationProbe.ProbeJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("os", out _));
        Assert.True(doc.RootElement.TryGetProperty("git", out _));
    }

    [Fact]
    public void ProbeJson_ReportsRuntimeNeutralAgentStatus()
    {
        var json = WorkstationProbe.ProbeJson(agentRuntimeStatus: new { runtime = "native", healthy = true });
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("native", doc.RootElement.GetProperty("agentRuntime").GetProperty("runtime").GetString());
        Assert.True(doc.RootElement.GetProperty("agentRuntime").GetProperty("healthy").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("opencodeServe", out _));
    }

    [Fact]
    public void Install_Unknown_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkstationSetup.Install("""{"id":"not-a-package"}"""));
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
        var found = WorkstationProbe.Which("fakebin", bin);
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
        var found = WorkstationProbe.Which("fakebin", bin);
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
        var found = WorkstationProbe.Which("fakebin", bin);
        Assert.Equal(Path.Combine(bin, "fakebin"), found);
    }

}
