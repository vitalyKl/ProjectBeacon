namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class GitHubLandingMergeExecutorTests
{
    [Fact]
    public void MergeApiArguments_PinTheExactTaskHeadAndUseTheRepositoryMergeMethod()
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
                "repos/acme/project-beacon/pulls/42/merge",
                "-f", "sha=" + expectedHeadSha,
                "-f", "merge_method=merge"
            },
            arguments);
        Assert.DoesNotContain("--admin", arguments);
        Assert.DoesNotContain("--force", arguments);
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
