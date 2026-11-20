namespace ProjectBeacon.Cli.Tests;

using System.Text.Json.Nodes;
using Domain.Common;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class AgentTaskGitHubPullRequestOperationsTests
{
    private static AgentTaskGitOperations.GitContext CreateContext() =>
        new(
            Guid.NewGuid(),
            "/repo",
            "/repo/.beacon/worktrees/task",
            "/repo/.beacon/worktrees/task/src",
            "src",
            "beacon/task/0123456789abcdef0123456789abcdef",
            "main",
            new string('a', 40),
            "/repo/.beacon/worktrees/task.beacon.json",
            []);

    private static AgentTaskGitHubPullRequestOperations.PullRequestView CreateView() =>
        new(
            "OPEN",
            false,
            "main",
            new string('b', 40),
            "MERGEABLE",
            "CLEAN",
            "APPROVED",
            "https://github.com/example/repo/pull/1",
            null,
            null);

    [Fact]
    public void GitPullRequestPreflight_AllowsOnlyTheExactApprovedCleanPassingHead()
    {
        var context = CreateContext();
        var view = CreateView();
        var checks = new[]
        {
            new AgentTaskGitHubPullRequestOperations.RequiredCheck(
                "CI", "SUCCESS", "pass", "https://github.com/example/repo/actions/runs/1", "CI")
        };

        var blockers = AgentTaskGitHubPullRequestOperations.GetMergeBlockers(
            context,
            view.HeadSha,
            worktreeClean: true,
            remoteBaseSha: context.BaseSha,
            view,
            headMatches: true,
            baseUnchanged: true,
            checks,
            requiredChecksPassed: true);

        Assert.Empty(blockers);
    }

    [Fact]
    public void GitPullRequestPreflight_FailsClosedOnDirtyStaleUnapprovedOrPendingState()
    {
        var context = CreateContext();
        var view = CreateView() with
        {
            IsDraft = true,
            Mergeable = "UNKNOWN",
            MergeStateStatus = "BLOCKED",
            ReviewDecision = "REVIEW_REQUIRED"
        };

        var blockers = AgentTaskGitHubPullRequestOperations.GetMergeBlockers(
            context,
            new string('c', 40),
            worktreeClean: false,
            remoteBaseSha: new string('d', 40),
            view,
            headMatches: false,
            baseUnchanged: false,
            checks: [],
            requiredChecksPassed: false);

        Assert.Contains("PR is still a draft", blockers);
        Assert.Contains("task worktree has uncommitted changes", blockers);
        Assert.Contains(blockers, value => value.Contains("does not match local task HEAD", StringComparison.Ordinal));
        Assert.Contains(blockers, value => value.Contains("review base advanced", StringComparison.Ordinal));
        Assert.Contains(blockers, value => value.Contains("does not report the PR as MERGEABLE", StringComparison.Ordinal));
        Assert.Contains(blockers, value => value.Contains("not CLEAN", StringComparison.Ordinal));
        Assert.Contains(blockers, value => value.Contains("not APPROVED", StringComparison.Ordinal));
        Assert.Contains("GitHub reports no required status checks", blockers);

        var pendingChecks = new[]
        {
            new AgentTaskGitHubPullRequestOperations.RequiredCheck(
                "CI", "PENDING", "pending", "https://github.com/example/repo/actions/runs/2", "CI")
        };
        var pendingBlockers = AgentTaskGitHubPullRequestOperations.GetMergeBlockers(
            context,
            view.HeadSha,
            worktreeClean: true,
            remoteBaseSha: context.BaseSha,
            CreateView(),
            headMatches: true,
            baseUnchanged: true,
            pendingChecks,
            requiredChecksPassed: false);
        Assert.Contains("one or more required status checks are not passing", pendingBlockers);
    }

    [Fact]
    public void GitPullRequest_IsARegisteredStructuredGitTool()
    {
        Assert.True(AgentTaskGitOperations.IsGitTool(PipelineToolPolicy.GitPullRequest));
    }

    [Theory]
    [InlineData("Actor", true)]
    [InlineData("Planner", false)]
    [InlineData("Review", false)]
    [InlineData("Eval", false)]
    [InlineData(null, false)]
    public void OnlyTheActorMayMarkItsTaskPullRequestReady(string? role, bool expected)
    {
        Assert.Equal(expected, AgentTaskGitHubPullRequestOperations.CanMarkReady(role));
    }

    [Fact]
    public void GitPullRequestDefinition_OnlyExposesTaskBoundStatusAndReadyActions()
    {
        var definition = AgentTaskGitOperations.ToolDefinition(PipelineToolPolicy.GitPullRequest);
        var parameters = definition["function"]?["parameters"] as JsonObject;
        var properties = parameters?["properties"] as JsonObject;
        var action = properties?["action"]?["enum"] as JsonArray;

        Assert.NotNull(properties);
        Assert.NotNull(action);
        Assert.Equal(2, action.Count);
        Assert.Equal(new[] { "status", "ready" },
            action.Select(value => value?.GetValue<string>()).ToArray());
        Assert.False(properties.ContainsKey("pullRequestNumber"));
        Assert.False(properties.ContainsKey("repository"));
        Assert.False(properties.ContainsKey("remote"));
        Assert.False(properties.ContainsKey("branch"));
        Assert.False(properties.ContainsKey("base"));
        Assert.False(properties.ContainsKey("merge"));
    }
}
