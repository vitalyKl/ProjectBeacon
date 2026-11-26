namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class GitHubLandingMergeExecutorTests
{
    [Fact]
    public void MergeApiArguments_PinTheExactTaskHeadAndDelegateQueueSelectionToGitHub()
    {
        const string repository = "acme/project-beacon";
        const int pullRequestNumber = 42;
        var expectedHeadSha = new string('a', 40);

        var arguments = GitHubLandingMergeExecutor.BuildMergeApiArguments(
            repository, pullRequestNumber, expectedHeadSha);

        Assert.Equal(
            new[]
            {
                "api",
                "--method", "PUT",
                "repos/acme/project-beacon/pulls/42/merge-async",
                "-f", "sha=" + expectedHeadSha,
                "-f", "merge_action=default"
            },
            arguments);
        Assert.DoesNotContain("bypass_rules", arguments);
        Assert.DoesNotContain("--admin", arguments);
        Assert.DoesNotContain("--force", arguments);
    }

    [Theory]
    [InlineData("""{"status":"pending","details":{"uuid":"9a9de4e8-6f1c-4f08-a1d0-13a9dfdc7cc1","expected_head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""", "pending", "9a9de4e8-6f1c-4f08-a1d0-13a9dfdc7cc1", null)]
    [InlineData("""{"status":"enqueued","details":{"uuid":"9a9de4e8-6f1c-4f08-a1d0-13a9dfdc7cc1"}}""", "enqueued", "9a9de4e8-6f1c-4f08-a1d0-13a9dfdc7cc1", null)]
    [InlineData("""{"status":"enqueued","details":{"message":"Pull request is already in the merge queue."}}""", "enqueued", null, null)]
    [InlineData("""{"status":"merged","details":{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}""", "merged", null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("""{"status":"failed","details":{"message":"Merge queue failed"}}""", "failed", null, null)]
    public void AsyncMergeResponse_ParsesPendingEnqueuedMergedAndFailedStates(
        string json,
        string expectedStatus,
        string? expectedRequestId,
        string? expectedMergeSha)
    {
        var result = GitHubLandingMergeExecutor.ParseAsyncMergeResponse(json);

        Assert.Null(result.Error);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedRequestId, result.MergeRequestId);
        Assert.Equal(expectedMergeSha, result.MergeCommitSha);
    }

    [Fact]
    public void AsyncMergeResponse_ExtractsStructuredConflictBodyFromGitHubCliStderr()
    {
        const string stderr = "gh: HTTP 409: Conflict\n{\"status\":\"pending\",\"details\":{\"uuid\":\"9a9de4e8-6f1c-4f08-a1d0-13a9dfdc7cc1\"}}";

        var result = GitHubLandingMergeExecutor.ParseAsyncMergeResponse(stderr);

        Assert.Null(result.Error);
        Assert.Equal("pending", result.Status);
        Assert.Equal("9a9de4e8-6f1c-4f08-a1d0-13a9dfdc7cc1", result.MergeRequestId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"status":"unknown","details":{}}""")]
    public void AsyncMergeResponse_RejectsMalformedOrUnsupportedStates(string json)
    {
        var result = GitHubLandingMergeExecutor.ParseAsyncMergeResponse(json);

        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("""{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","merged":true,"message":"Pull Request successfully merged"}""", true)]
    [InlineData("""{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","merged":false,"message":"Pull Request is not mergeable"}""", false)]
    [InlineData("""{"merged":true,"message":"merged"}""", false)]
    [InlineData("""{"sha":"not-a-sha","merged":true}""", false)]
    [InlineData("not json", false)]
    public void MergeApiResponse_RequiresExplicitSuccessAndAValidCommitSha(
        string json,
        bool expectedSuccess)
    {
        var result = GitHubLandingMergeExecutor.ParseMergeApiResponse(json);

        Assert.Equal(expectedSuccess, result.MergeCommitSha is not null);
        if (expectedSuccess)
            Assert.Null(result.Error);
        else
            Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false)]
    [InlineData(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true)]
    [InlineData(null, "not-a-tree-sha", false)]
    [InlineData("invalid-candidate-sha", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false)]
    public void PostMergeTreeValidation_AllowsMergeQueueTreeDivergenceButKeepsStrictModeExact(
        string? expectedCandidateTreeSha,
        string? actualMergeTreeSha,
        bool expected)
    {
        Assert.Equal(
            expected,
            GitHubLandingMergeExecutor.IsPostMergeTreeAcceptable(expectedCandidateTreeSha, actualMergeTreeSha));
    }

    [Fact]
    public void MergeApiArguments_RejectCallerSuppliedInvalidIdentity()
    {
        Assert.Throws<ArgumentException>(() =>
            GitHubLandingMergeExecutor.BuildMergeApiArguments(
                "acme/project-beacon", 0, new string('a', 40)));
        Assert.Throws<ArgumentException>(() =>
            GitHubLandingMergeExecutor.BuildMergeApiArguments(
                "acme/project-beacon", 42, "short"));
    }
}
