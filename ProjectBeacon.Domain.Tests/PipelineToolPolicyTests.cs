namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Common;

public sealed class PipelineToolPolicyTests
{
    [Fact]
    public void PipelineRoleToolMapIsTheSingleRoleCeiling()
    {
        Assert.True(PipelineToolPolicy.PipelineRoleTools.ContainsKey("Planner"));
        Assert.True(PipelineToolPolicy.PipelineRoleTools.ContainsKey("Actor"));
        Assert.True(PipelineToolPolicy.PipelineRoleTools.ContainsKey("Review"));
        Assert.Same(PipelineToolPolicy.PlannerTools, PipelineToolPolicy.ToolsForRole("planner"));
        Assert.Same(PipelineToolPolicy.ReviewerTools, PipelineToolPolicy.ToolsForRole("REVIEW"));

        var actor = PipelineToolPolicy.ToolsForRole("Actor");
        Assert.Contains(McpToolNames.ReadFile, actor);
        Assert.Contains(McpToolNames.GetTree, actor);
        Assert.Contains(McpToolNames.SearchCode, actor);
        Assert.Contains(McpToolNames.SubtaskReportResult, actor);
        Assert.Contains(McpToolNames.PipelineFailSubtask, actor);
        Assert.Contains(McpToolNames.TaskPipelineStatus, actor);
        Assert.Contains("repo_status", actor);
        Assert.Contains("repo_status", PipelineToolPolicy.ToolsForRole("Planner"));
        Assert.DoesNotContain("repo_status", PipelineToolPolicy.ToolsForRole("Review"));
        Assert.Contains(McpToolNames.TaskListAttachments, actor);
        Assert.Contains(McpToolNames.TaskReadAttachment, actor);
        Assert.Contains(McpToolNames.GetContextNode, actor);
        Assert.Contains(McpToolNames.ListContextNodes, actor);
        Assert.DoesNotContain(McpToolNames.PipelineApprove, actor);
        Assert.Empty(PipelineToolPolicy.ToolsForRole("Unknown"));
    }
}
