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

    [Fact]
    public void ForgeRead_IsRegisteredAndDoesNotAcceptCallerSuppliedRepositoryOrPullRequest()
    {
        Assert.True(AgentTaskGitOperations.IsGitTool(PipelineToolPolicy.ForgeRead));

        var definition = AgentTaskGitOperations.ToolDefinition(PipelineToolPolicy.ForgeRead);
        var parameters = definition["function"]?["parameters"] as JsonObject;
        var properties = parameters?["properties"] as JsonObject;
        Assert.NotNull(properties);
        Assert.Equal(
            new[] { "all", "pull_request", "reviews", "workflows" },
            properties["view"]?["enum"]?.AsArray().Select(value => value!.GetValue<string>()).ToArray());
        Assert.Equal(10, properties["limit"]?["maximum"]?.GetValue<int>());
        Assert.DoesNotContain("repository", properties.Select(pair => pair.Key));
        Assert.DoesNotContain("pullRequestNumber", properties.Select(pair => pair.Key));
        Assert.DoesNotContain("branch", properties.Select(pair => pair.Key));
        Assert.DoesNotContain("remote", properties.Select(pair => pair.Key));
    }

    [Fact]
    public void ForgeRead_ParsesReviewDecisionsAndPreservesBoundedEvidence()
    {
        var commitSha = new string('a', 40);
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                id = 7,
                user = new { login = "reviewer" },
                state = "CHANGES_REQUESTED",
                submitted_at = "2026-11-24T10:00:00Z",
                commit_id = commitSha,
                html_url = "https://github.com/example/repo/pull/1#pullrequestreview-7",
                body = "Please handle the null branch before retrying."
            }
        });

        var result = AgentTaskGitHubPullRequestOperations.ParseForgeCollection(json, "reviews", 5);

        Assert.Null(result.Error);
        var review = Assert.Single(result.Items!);
        Assert.Equal(7L, review["id"]!.GetValue<long>());
        Assert.Equal("reviewer", review["author"]!.GetValue<string>());
        Assert.Equal("CHANGES_REQUESTED", review["state"]!.GetValue<string>());
        Assert.Equal(commitSha, review["commitSha"]!.GetValue<string>());
        Assert.Contains("null branch", review["body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForgeRead_ParsesConversationAndInlineCommentsWithoutReturningDiffHunks()
    {
        var conversationJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                id = 11,
                user = new { login = "maintainer" },
                html_url = "https://github.com/example/repo/issues/1#issuecomment-11",
                created_at = "2026-11-24T10:00:00Z",
                updated_at = "2026-11-24T10:01:00Z",
                body = "The public API should keep the previous response field."
            }
        });
        var inlineJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                id = 12,
                user = new { login = "reviewer" },
                html_url = "https://github.com/example/repo/pull/1#discussion_r12",
                created_at = "2026-11-24T10:02:00Z",
                updated_at = "2026-11-24T10:02:00Z",
                body = "This assignment can be null.",
                path = "src/Version.cs",
                line = 42,
                original_line = 41,
                diff_hunk = "@@ untrusted diff content"
            }
        });

        var conversation = AgentTaskGitHubPullRequestOperations.ParseForgeCollection(
            conversationJson, "comments", 5);
        var inline = AgentTaskGitHubPullRequestOperations.ParseForgeCollection(
            inlineJson, "inline", 5);

        Assert.Null(conversation.Error);
        var conversationComment = Assert.Single(conversation.Items!);
        Assert.Equal(11L, conversationComment["id"]!.GetValue<long>());
        Assert.Equal("maintainer", conversationComment["author"]!.GetValue<string>());
        Assert.Null(inline.Error);
        var comment = Assert.Single(inline.Items!);
        Assert.Equal(12L, comment["id"]!.GetValue<long>());
        Assert.Equal("src/Version.cs", comment["path"]!.GetValue<string>());
        Assert.Equal(42, comment["line"]!.GetValue<int>());
        Assert.False(comment.AsObject().ContainsKey("diff_hunk"));
    }

    [Fact]
    public void ForgeRead_ClipsExternalBodiesAndBoundsReturnedCollections()
    {
        var longBody = new string('x', 1200);
        var json = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 12).Select(id => new
        {
            id,
            user = new { login = "reviewer" },
            state = "COMMENTED",
            submitted_at = "2026-11-24T10:00:00Z",
            commit_id = new string('b', 40),
            html_url = $"https://github.com/example/repo/pull/1#review-{id}",
            body = longBody
        }));

        var result = AgentTaskGitHubPullRequestOperations.ParseForgeCollection(json, "reviews", 3);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Items!.Count);
        Assert.Equal(800 + "\n[comment truncated]".Length, result.Items[0]!["body"]!.GetValue<string>().Length);
        Assert.Contains("[comment truncated]", result.Items[0]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForgeRead_ParsesOnlyValidWorkflowRunSummaries()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            total_count = 1,
            workflow_runs = new[]
            {
                new
                {
                    name = "CI",
                    @event = "pull_request",
                    status = "completed",
                    conclusion = "failure",
                    head_branch = "beacon/task/0123456789abcdef0123456789abcdef",
                    head_sha = new string('c', 40),
                    run_number = 1529,
                    html_url = "https://github.com/example/repo/actions/runs/1529",
                    created_at = "2026-11-24T10:00:00Z",
                    updated_at = "2026-11-24T10:05:00Z"
                }
            }
        });

        var result = AgentTaskGitHubPullRequestOperations.ParseForgeCollection(json, "workflows", 5);

        Assert.Null(result.Error);
        var run = Assert.Single(result.Items!);
        Assert.Equal("failure", run["conclusion"]!.GetValue<string>());
        Assert.Equal("pull_request", run["event"]!.GetValue<string>());
        Assert.Equal("1529", run["runNumber"]!.ToJsonString());
    }

    [Theory]
    [InlineData("not json", "reviews")]
    [InlineData("{}", "reviews")]
    [InlineData("[]", "workflows")]
    [InlineData("[]", "unsupported")]
    public void ForgeRead_RejectsMalformedOrUnsupportedCollections(string json, string collection)
    {
        var result = AgentTaskGitHubPullRequestOperations.ParseForgeCollection(json, collection, 5);

        Assert.Null(result.Items);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ForgeRead_RejectsLimitsOutsideTenItems()
    {
        Assert.NotNull(AgentTaskGitHubPullRequestOperations.ParseForgeCollection("[]", "reviews", 0).Error);
        Assert.NotNull(AgentTaskGitHubPullRequestOperations.ParseForgeCollection("[]", "reviews", 11).Error);
    }

    [Fact]
    public void RequiredCheckLookup_ReadsPendingStateFromJsonWithSuccessfulExitCode()
    {
        const string passedJson = """[{"name":"CI","state":"SUCCESS","bucket":"pass","link":"https://github.com/example/repo/actions/runs/1","workflow":"CI"}]""";
        const string pendingJson = """[{"name":"CI","state":"PENDING","bucket":"pending","link":"https://github.com/example/repo/actions/runs/2","workflow":"CI"}]""";

        var passed = AgentTaskGitHubPullRequestOperations.ParseRequiredChecksResult(
            0, timedOut: false, passedJson, string.Empty);
        var pending = AgentTaskGitHubPullRequestOperations.ParseRequiredChecksResult(
            0, timedOut: false, pendingJson, string.Empty);

        Assert.Null(passed.Error);
        Assert.Equal("pass", Assert.Single(passed.Checks!).Bucket);
        Assert.Null(pending.Error);
        Assert.Equal("pending", Assert.Single(pending.Checks!).Bucket);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void RequiredCheckLookup_RejectsEveryNonzeroJsonExitCodeEvenWhenOutputLooksValid(int exitCode)
    {
        const string plausibleJson = """[{"name":"CI","state":"SUCCESS","bucket":"pass","link":"https://github.com/example/repo/actions/runs/1","workflow":"CI"}]""";

        var result = AgentTaskGitHubPullRequestOperations.ParseRequiredChecksResult(
            exitCode, timedOut: false, plausibleJson, "GitHub CLI request failed.");

        Assert.Null(result.Checks);
        Assert.Contains("Could not read required GitHub status checks", result.Error ?? string.Empty);
    }

    [Fact]
    public void RequiredCheckLookup_AllowsAnEmptySuccessfulResultToFailClosedAtTheMergePolicy()
    {
        var result = AgentTaskGitHubPullRequestOperations.ParseRequiredChecksResult(
            0, timedOut: false, "[]", string.Empty);

        Assert.Null(result.Error);
        Assert.Empty(result.Checks!);
    }

    [Fact]
    public void PullRequestStatusSnapshot_RequiresTheSameHeadBaseAndReviewState()
    {
        var original = CreateView();

        Assert.True(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { Url = "https://github.com/example/repo/pull/1" }));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { HeadSha = new string('c', 40) }));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { BaseBranch = "release" }));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { State = "MERGED" }));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { IsDraft = true }));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { Mergeable = "UNKNOWN" }));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { MergeStateStatus = "BEHIND" }));
        Assert.False(AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(
            original, original with { ReviewDecision = "REVIEW_REQUIRED" }));
    }
}
