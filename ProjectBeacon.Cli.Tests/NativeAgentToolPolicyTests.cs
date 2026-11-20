namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class NativeAgentToolPolicyTests
{
    [Theory]
    [InlineData("repo_sync")]
    [InlineData("git_commit")]
    [InlineData("git_integrate")]
    [InlineData("git_publish")]
    public void RequiresExclusiveWorktreeExecution_ForRepositoryMutations(string tool)
    {
        Assert.True(NativeAgentToolPolicy.RequiresExclusiveWorktreeExecution(tool));
    }

    [Theory]
    [InlineData("git_commit")]
    [InlineData("git_integrate")]
    [InlineData("git_publish")]
    public void PlanMode_BlocksAllMutatingTaskGitTools(string tool)
    {
        Assert.True(NativeAgentToolPolicy.IsPlanModeCommandExecutionBlocked("plan", tool));
        Assert.False(NativeAgentToolPolicy.IsPlanModeCommandExecutionBlocked("act", tool));
    }

    [Fact]
    public void RequiresExclusiveWorktreeExecution_DoesNotSerializeReadOnlyGitInspection()
    {
        Assert.False(NativeAgentToolPolicy.RequiresExclusiveWorktreeExecution("git_info"));
        Assert.False(NativeAgentToolPolicy.RequiresExclusiveWorktreeExecution("list_tasks"));
    }
}
