namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;
using ProjectBeacon.Domain.Common;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed partial class AgentTaskGitOperationsTests
{
    [Fact]
    public void ResolveGitHubConfigDirectory_PrefersExplicitDirectoryThenXdgConfig()
    {
        Assert.Equal(
            "custom-gh",
            AgentTaskGitOperations.ResolveGitHubConfigDirectory(
                "custom-gh", "xdg", "appdata", "home", isWindows: true));
        Assert.Equal(
            Path.Combine("xdg", "gh"),
            AgentTaskGitOperations.ResolveGitHubConfigDirectory(
                null, "xdg", "appdata", "home", isWindows: true));
    }

    [Fact]
    public void ResolveGitHubConfigDirectory_UsesPlatformDefaultFallbacks()
    {
        Assert.Equal(
            Path.Combine("appdata", "GitHub CLI"),
            AgentTaskGitOperations.ResolveGitHubConfigDirectory(
                null, null, "appdata", "home", isWindows: true));
        Assert.Equal(
            Path.Combine("home", ".config", "gh"),
            AgentTaskGitOperations.ResolveGitHubConfigDirectory(
                null, null, "appdata", "home", isWindows: false));
        Assert.Null(AgentTaskGitOperations.ResolveGitHubConfigDirectory(
            null, null, null, null, isWindows: false));
    }

    [Fact]
    public void RedactCredentialValues_RemovesHostGitHubTokensFromDiagnostics()
    {
        var credentials = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "gh-secret-token",
            ["GITHUB_TOKEN"] = "github-secret-token",
            ["GH_ENTERPRISE_TOKEN"] = "enterprise-secret-token"
        };

        var result = AgentTaskGitOperations.RedactCredentialValues(
            "Git failed using gh-secret-token and github-secret-token; helper enterprise-secret-token",
            name => credentials.TryGetValue(name, out var value) ? value : null);

        Assert.DoesNotContain("gh-secret-token", result, StringComparison.Ordinal);
        Assert.DoesNotContain("github-secret-token", result, StringComparison.Ordinal);
        Assert.DoesNotContain("enterprise-secret-token", result, StringComparison.Ordinal);
        Assert.Equal(3, result.Split("[redacted]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BuildGitHubCliEnvironment_ExcludesApplicationAndUnrelatedCredentials()
    {
        var source = new Dictionary<string, string?>
        {
            ["PATH"] = "trusted-path",
            ["HOME"] = "host-home",
            ["GH_CONFIG_DIR"] = "host-gh",
            ["GH_TOKEN"] = "host-token",
            ["GITHUB_TOKEN"] = "github-token",
            ["DBUS_SESSION_BUS_ADDRESS"] = "host-dbus",
            ["BEACON_API_TOKEN"] = "beacon-secret",
            ["GH_ENTERPRISE_TOKEN"] = "enterprise-secret",
            ["UNRELATED_SECRET"] = "unrelated"
        };

        var environment = AgentTaskGitOperations.BuildGitHubCliEnvironment(
            name => source.TryGetValue(name, out var value) ? value : null);

        Assert.Equal("trusted-path", environment["PATH"]);
        Assert.Equal("host-home", environment["HOME"]);
        Assert.Equal("host-gh", environment["GH_CONFIG_DIR"]);
        Assert.Equal("host-token", environment["GH_TOKEN"]);
        Assert.Equal("github-token", environment["GITHUB_TOKEN"]);
        Assert.Equal("host-dbus", environment["DBUS_SESSION_BUS_ADDRESS"]);
        Assert.Equal("1", environment["GH_PROMPT_DISABLED"]);
        Assert.False(environment.ContainsKey("BEACON_API_TOKEN"));
        Assert.False(environment.ContainsKey("GH_ENTERPRISE_TOKEN"));
        Assert.False(environment.ContainsKey("SSH_AUTH_SOCK"));
        Assert.False(environment.ContainsKey("SSH_AGENT_PID"));
        Assert.False(environment.ContainsKey("UNRELATED_SECRET"));
    }

    [Theory]
    [InlineData(true, null, @"\\.\pipe\openssh-ssh-agent")]
    [InlineData(false, null, null)]
    [InlineData(false, "", null)]
    [InlineData(false, "/tmp/ssh-agent.sock", "/tmp/ssh-agent.sock")]
    [InlineData(true, "/custom/agent.sock", "/custom/agent.sock")]
    public void ResolveSshAgentSocket_UsesWindowsDefaultNamedPipeWhenNeeded(
        bool isWindows, string? sshAgentSocket, string? expected)
    {
        Assert.Equal(expected, AgentTaskGitOperations.ResolveSshAgentSocket(sshAgentSocket: sshAgentSocket, isWindows: isWindows));
    }

    [Theory]
    [InlineData("/home/test user/.ssh/known_hosts", false, "UserKnownHostsFile='/home/test user/.ssh/known_hosts'")]
    [InlineData("C:\\Users\\Test User\\.ssh\\known_hosts", true, "UserKnownHostsFile='C:/Users/Test User/.ssh/known_hosts'")]
    [InlineData("/home/o'neil/.ssh/known_hosts", false, "UserKnownHostsFile='/home/o'\"'\"'neil/.ssh/known_hosts'")]
    public void BuildSshCommand_UsesBatchModeAndStrictHostKeyVerification(
        string knownHostsPath, bool isWindows, string expectedKnownHostsOption)
    {
        var command = AgentTaskGitOperations.BuildSshCommand(knownHostsPath, isWindows);

        Assert.Contains("-o BatchMode=yes", command, StringComparison.Ordinal);
        Assert.Contains("-o StrictHostKeyChecking=yes", command, StringComparison.Ordinal);
        Assert.Contains("-o UpdateHostKeys=no", command, StringComparison.Ordinal);
        Assert.Contains(expectedKnownHostsOption, command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSshCommand_RemainsNonInteractiveWithoutAHostHomeDirectory()
    {
        var command = AgentTaskGitOperations.BuildSshCommand(null, isWindows: false);

        Assert.Contains("-o BatchMode=yes", command, StringComparison.Ordinal);
        Assert.Contains("-o StrictHostKeyChecking=yes", command, StringComparison.Ordinal);
        Assert.DoesNotContain("UserKnownHostsFile=", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSshAgentEnvironment_ForwardsOnlyAgentSocket()
    {
        var environment = AgentTaskGitOperations.BuildSshAgentEnvironment(
            "/tmp/ssh-agent.sock", "1234");

        Assert.Equal(2, environment.Count);
        Assert.Equal("/tmp/ssh-agent.sock", environment["SSH_AUTH_SOCK"]);
        Assert.Equal("1234", environment["SSH_AGENT_PID"]);
        Assert.False(environment.ContainsKey("GH_TOKEN"));
        Assert.False(environment.ContainsKey("GITHUB_TOKEN"));
    }

    [Theory]
    [InlineData("https://github.com/owner/repository.git", true)]
    [InlineData("https://github.com.evil.test/owner/repository.git", false)]
    [InlineData("git@github.com:owner/repository.git", false)]
    [InlineData("ssh://git@github.com/owner/repository.git", false)]
    [InlineData("https://gitlab.com/owner/repository.git", false)]
    public void IsGitHubHttpsRemote_OnlyAllowsValidatedHttpsGitHubUrls(string remote, bool expected)
    {
        Assert.Equal(expected, AgentTaskGitOperations.IsGitHubHttpsRemote(remote));
    }

    [Theory]
    [InlineData("git@github.com:owner/repository.git", true)]
    [InlineData("developer@gitlab.com:group/repository.git", true)]
    [InlineData("ssh://git@github.com/owner/repository.git", true)]
    [InlineData("https://github.com/owner/repository.git", false)]
    [InlineData("C:\\source\\repository", false)]
    public void IsSshRemote_RecognizesSshUriAndScpForms(string remote, bool expected)
    {
        Assert.Equal(expected, AgentTaskGitOperations.IsSshRemote(remote));
    }

    [Fact]
    public void BuildGitHubCredentialEnvironment_ForwardsOnlyScopedHelperContext()
    {
        var environment = AgentTaskGitOperations.BuildGitHubCredentialEnvironment(
            "custom-gh", "xdg", "appdata", "home", "gh-token", "github-token",
            "unix:abstract=/tmp/dbus", "/run/user/1000", isWindows: false);

        Assert.NotNull(environment);
        Assert.Equal("custom-gh", environment["GH_CONFIG_DIR"]);
        Assert.Equal("home", environment["HOME"]);
        Assert.Equal("xdg", environment["XDG_CONFIG_HOME"]);
        Assert.False(environment.ContainsKey("APPDATA"));
        Assert.Equal("gh-token", environment["GH_TOKEN"]);
        Assert.Equal("github-token", environment["GITHUB_TOKEN"]);
        Assert.Equal("1", environment["GH_PROMPT_DISABLED"]);
        Assert.Equal("unix:abstract=/tmp/dbus", environment["DBUS_SESSION_BUS_ADDRESS"]);
        Assert.Equal("/run/user/1000", environment["XDG_RUNTIME_DIR"]);
        Assert.False(environment.ContainsKey("SSH_AUTH_SOCK"));
        Assert.False(environment.ContainsKey("SSH_AGENT_PID"));
        Assert.False(environment.ContainsKey("BEACON_API_TOKEN"));
    }

    [Fact]
    public void BuildGitHubCredentialEnvironment_PreservesWindowsProfileVariables()
    {
        var environment = AgentTaskGitOperations.BuildGitHubCredentialEnvironment(
            null, null,
            "C:\\Users\\test\\AppData\\Roaming",
            "C:\\Users\\test",
            null, null, null, null, isWindows: true);

        Assert.NotNull(environment);
        Assert.Equal("C:\\Users\\test", environment["HOME"]);
        Assert.Equal("C:\\Users\\test", environment["USERPROFILE"]);
        Assert.Equal("C:\\Users\\test\\AppData\\Roaming", environment["APPDATA"]);
        Assert.Equal(
            Path.Combine("C:\\Users\\test\\AppData\\Roaming", "GitHub CLI"),
            environment["GH_CONFIG_DIR"]);
        Assert.False(environment.ContainsKey("XDG_CONFIG_HOME"));
    }

    [Fact]
    public void BuildGitHubCredentialEnvironment_AllowsEnvironmentTokenWithoutConfigDirectory()
    {
        var environment = AgentTaskGitOperations.BuildGitHubCredentialEnvironment(
            null, null, null, null, null, "github-token", null, null, isWindows: true);

        Assert.NotNull(environment);
        Assert.False(environment.ContainsKey("GH_CONFIG_DIR"));
        Assert.False(environment.ContainsKey("HOME"));
        Assert.Equal("github-token", environment["GITHUB_TOKEN"]);
        Assert.False(environment.ContainsKey("GH_TOKEN"));
    }

    [Theory]
    [InlineData("https://github.com/owner/repository.git", "owner/repository")]
    [InlineData("https://github.com/owner/repository", "owner/repository")]
    [InlineData("git@github.com:owner/repository.git", "owner/repository")]
    [InlineData("ssh://git@github.com/owner/repository.git", "owner/repository")]
    public void TryParseGitHubRemote_AcceptsCredentialFreeGitHubUrls(string remote, string expected)
    {
        Assert.True(AgentTaskGitOperations.TryParseGitHubRemote(remote, out var repository));
        Assert.Equal(expected, repository);
    }

    [Theory]
    [InlineData("https://github.com.evil.test/owner/repository.git")]
    [InlineData("https://gitlab.com/owner/repository.git")]
    [InlineData("https://token@github.com/owner/repository.git")]
    [InlineData("https://github.com/owner/repository.git?token=secret")]
    [InlineData("file:///tmp/repository.git")]
    [InlineData("../repository.git")]
    [InlineData("git@github.com:owner/../repository.git")]
    [InlineData("ssh://user:password@github.com/owner/repository.git")]
    public void TryParseGitHubRemote_RejectsUntrustedOrCredentialBearingUrls(string remote)
    {
        Assert.False(AgentTaskGitOperations.TryParseGitHubRemote(remote, out _));
    }

    [Fact]
    public async Task GitPublish_RejectsPushUrlThatDiffersFromValidatedOrigin()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);
            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "task-change.txt"),
                "publish must reject a redirected push URL\\n");
            var committed = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitCommit,
                """{"message":"Prepare redirected push URL test"}""");
            Assert.False(committed.IsError, committed.Text);

            await GitAsync(worktree.WorkingDirectory, "remote", "set-url", "origin",
                "https://github.com/owner/repository.git");
            var localPushUrl = new Uri(repository + "-origin.git").AbsoluteUri;
            await GitAsync(worktree.WorkingDirectory, "remote", "set-url", "--push", "origin", localPushUrl);

            var result = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitPublish,
                """{"action":"publish","title":"Publish task branch","body":"Test only","draft":true}""");
            Assert.True(result.IsError);
            Assert.Contains("push URL", result.Text, StringComparison.OrdinalIgnoreCase);

            var remoteRefs = await GitAsync(repository + "-origin.git", "show-ref", "--heads");
            Assert.DoesNotContain($"refs/heads/beacon/task/{taskId:N}", remoteRefs, StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitPublish_RejectsMirrorPushConfiguration()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);
            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "task-change.txt"),
                "publish must reject mirror origin\\n");
            var committed = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitCommit,
                """{"message":"Prepare mirror origin test"}""");
            Assert.False(committed.IsError, committed.Text);

            await GitAsync(worktree.WorkingDirectory, "remote", "set-url", "origin",
                "https://github.com/owner/repository.git");
            await GitAsync(worktree.WorkingDirectory, "config", "remote.origin.mirror", "true");

            var result = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitPublish,
                """{"action":"publish","title":"Publish task branch","body":"Test only","draft":true}""");
            Assert.True(result.IsError);
            Assert.Contains("mirror=true", result.Text, StringComparison.Ordinal);

            var remoteRefs = await GitAsync(repository + "-origin.git", "show-ref", "--heads");
            Assert.DoesNotContain($"refs/heads/beacon/task/{taskId:N}", remoteRefs, StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitPublish_RefusesLocalOriginWithoutPushingTaskBranch()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "task-change.txt"),
                "publish must reject a local remote\n");
            var committed = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitCommit,
                """{"message":"Prepare task branch for GitHub publish test"}""");
            Assert.False(committed.IsError, committed.Text);

            var invalidArguments = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitPublish,
                """{"action":"publish","title":"Publish task branch","remote":"https://github.com/attacker/repository"}""");
            Assert.True(invalidArguments.IsError);
            Assert.Contains("only action, title, body, and draft", invalidArguments.Text, StringComparison.Ordinal);

            var result = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitPublish,
                """{"action":"publish","title":"Publish task branch","body":"Test only","draft":true}""");
            Assert.True(result.IsError);
            Assert.Contains("github.com/owner/repository", result.Text, StringComparison.Ordinal);

            // A syntactically valid GitHub origin must get past branch, ancestry and
            // changed-path verification before it is rejected for missing host auth.
            await GitAsync(worktree.WorkingDirectory, "remote", "set-url", "origin", "https://github.com/owner/repository.git");
            var unauthenticated = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitPublish,
                """{"action":"publish","title":"Publish task branch","body":"Validation only","draft":true}""");
            Assert.True(unauthenticated.IsError);
            Assert.True(
                unauthenticated.Text.Contains("GitHub CLI (gh)", StringComparison.Ordinal)
                || unauthenticated.Text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase),
                unauthenticated.Text);

            var remoteRefs = await GitAsync(repository + "-origin.git", "show-ref", "--heads");
            Assert.DoesNotContain($"refs/heads/beacon/task/{taskId:N}", remoteRefs, StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GitPublishTool_DoesNotAcceptRemoteBranchOrBaseFromTheAgent()
    {
        var definition = AgentTaskGitOperations.ToolDefinition(PipelineToolPolicy.GitPublish);
        var properties = definition["function"]!["parameters"]!["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("action"));
        Assert.True(properties.ContainsKey("title"));
        Assert.True(properties.ContainsKey("body"));
        Assert.True(properties.ContainsKey("draft"));
        Assert.False(properties.ContainsKey("remote"));
        Assert.False(properties.ContainsKey("branch"));
        Assert.False(properties.ContainsKey("base"));
        Assert.False(properties.ContainsKey("target"));
    }
}
