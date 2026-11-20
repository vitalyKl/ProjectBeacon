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
    public void PublishedPullRequest_MustMatchExactStateBaseAndTaskHead()
    {
        var view = CreateView();

        Assert.True(AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
            view,
            "OPEN",
            "main",
            view.HeadSha));
        Assert.True(AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
            view with { HeadSha = view.HeadSha.ToUpperInvariant() },
            "OPEN",
            "main",
            view.HeadSha));

        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
            view,
            "MERGED",
            "main",
            view.HeadSha));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
            view,
            "OPEN",
            "release",
            view.HeadSha));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
            view,
            "OPEN",
            "main",
            new string('c', 40)));
    }

    [Fact]
    public void AlreadyReadyPullRequest_StillRequiresCleanCurrentTaskHeadAndPinnedBase()
    {
        Assert.Empty(AgentTaskGitHubPullRequestOperations.GetReadyBlockers(
            worktreeClean: true,
            headMatches: true,
            baseUnchanged: true));

        var blockers = AgentTaskGitHubPullRequestOperations.GetReadyBlockers(
            worktreeClean: false,
            headMatches: false,
            baseUnchanged: false);

        Assert.Contains("task worktree has uncommitted changes", blockers);
        Assert.Contains("PR head does not match the local task HEAD", blockers);
        Assert.Contains("review base advanced from the pinned base", blockers);
    }

    [Fact]
    public void GitHubPullRequestLookup_UsesSupportedLocalHeadBranchFilter()
    {
        var arguments = AgentTaskGitOperations.BuildListPullRequestsArguments(
            "example/repository",
            "beacon/task/0123456789abcdef0123456789abcdef");

        Assert.Equal(
            new[]
            {
                "pr", "list", "--repo", "example/repository",
                "--head", "beacon/task/0123456789abcdef0123456789abcdef",
                "--state", "all",
                "--json", "number,url,baseRefName,state,isDraft"
            },
            arguments);
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
