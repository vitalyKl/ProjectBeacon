namespace ProjectBeacon.Cli.Tests;

using Domain.Common;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class NativeAgentToolPolicyTests
{
    [Fact]
    public void RequiresExclusiveWorktreeExecution_ForWorkspaceAndRepositoryMutations()
    {
        var mutations = new[]
        {
            "repo_sync",
            McpToolNames.WriteFile,
            McpToolNames.ApplyPatch,
            "git_commit",
            "git_integrate",
            "git_publish",
            PipelineToolPolicy.GitPullRequest
        };

        foreach (var tool in mutations)
            Assert.True(NativeAgentToolPolicy.RequiresExclusiveWorktreeExecution(tool), tool);
    }

    [Theory]
    [InlineData("git_commit")]
    [InlineData("git_integrate")]
    [InlineData("git_publish")]
    [InlineData("git_pr")]
    public void PlanMode_BlocksAllMutatingTaskGitTools(string tool)
    {
        Assert.True(NativeAgentToolPolicy.IsPlanModeCommandExecutionBlocked("plan", tool));
        Assert.False(NativeAgentToolPolicy.IsPlanModeCommandExecutionBlocked("act", tool));
    }

    [Fact]
    public void ForgeRead_IsReadOnlyAndDoesNotReserveTheTaskWorktree()
    {
        Assert.False(NativeAgentToolPolicy.RequiresExclusiveWorktreeExecution(PipelineToolPolicy.ForgeRead));
        Assert.False(NativeAgentToolPolicy.IsPlanModeCommandExecutionBlocked("plan", PipelineToolPolicy.ForgeRead));
        Assert.Contains(PipelineToolPolicy.ForgeRead, PipelineToolPolicy.InteractivePlanTools);
        Assert.DoesNotContain(PipelineToolPolicy.ForgeRead, PipelineToolPolicy.PipelinePlanDeniedTools);
    }

    [Fact]
    public void RequiresExclusiveWorktreeExecution_DoesNotSerializeReadOnlyGitInspection()
    {
        Assert.False(NativeAgentToolPolicy.RequiresExclusiveWorktreeExecution("git_info"));
        Assert.False(NativeAgentToolPolicy.RequiresExclusiveWorktreeExecution("list_tasks"));
    }
}
