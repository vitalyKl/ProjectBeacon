namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;
using ProjectBeacon.Domain.Common;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed partial class AgentTaskGitOperationsTests
{
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

            var result = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitPublish,
                """{"action":"publish","title":"Publish task branch","body":"Test only","draft":true}""");
            Assert.True(result.IsError);
            Assert.Contains("github.com/owner/repository", result.Text, StringComparison.Ordinal);

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
