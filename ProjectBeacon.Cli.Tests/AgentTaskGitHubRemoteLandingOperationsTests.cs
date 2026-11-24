namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class AgentTaskGitHubRemoteLandingOperationsTests
{
    [Fact]
    public void ParseWorktrees_ExtractsTaskBranchesAndPathsWithSpaces()
    {
        const string output = """
            worktree C:/beacon/projects/Project Beacon
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree C:/beacon/worktrees/task one
            HEAD 2222222222222222222222222222222222222222
            branch refs/heads/beacon/task/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa

            worktree C:/beacon/worktrees/detached
            HEAD 3333333333333333333333333333333333333333
            detached

            """;

        var parsed = AgentTaskGitHubRemoteLandingOperations.ParseWorktrees(output);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("C:/beacon/projects/Project Beacon", parsed[0].Path);
        Assert.Equal("refs/heads/main", parsed[0].Branch);
        Assert.Equal("C:/beacon/worktrees/task one", parsed[1].Path);
        Assert.Equal(
            "refs/heads/beacon/task/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            parsed[1].Branch);
        Assert.Equal("C:/beacon/worktrees/detached", parsed[2].Path);
        Assert.Null(parsed[2].Branch);
    }

    [Fact]
    public void ParseWorktrees_DoesNotCarryBranchIntoFollowingDetachedWorktree()
    {
        const string output = """
            worktree /tmp/task
            HEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            branch refs/heads/beacon/task/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb

            worktree /tmp/recovery
            HEAD cccccccccccccccccccccccccccccccccccccccc
            detached

            """;

        var parsed = AgentTaskGitHubRemoteLandingOperations.ParseWorktrees(output);

        Assert.Equal(2, parsed.Count);
        Assert.NotNull(parsed[0].Branch);
        Assert.Null(parsed[1].Branch);
    }
}
