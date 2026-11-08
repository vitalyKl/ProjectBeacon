namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Common;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task CreateSubtask_ProfileCannotExpandBaseToolsAndOptionalAttachmentIsExplicit()
    {
        var (projectId, task) = await SeedTaskAsync("Create profile-scoped subtasks");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var started = await new StartPipelineHandler(factory, new FakeSpawner())
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(started.Success, started.Error);

        var handler = new CreateSubtaskHandler(factory);
        var created = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Implement a feature", AllowedPaths: ["src/"],
            Profile: "implementer", OptionalMcpTools: [McpToolNames.TaskAttachFile])),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);
        Assert.Equal("implementer", created.Value!.ProfileKey);
        Assert.Contains(McpToolNames.WorkspaceWrite, created.Value.AllowedMcpTools);
        Assert.Contains(McpToolNames.TaskAttachFile, created.Value.AllowedMcpTools);
        Assert.Contains(McpToolNames.WriteFile, created.Value.AllowedMcpTools);

        var expansion = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Try to expand grants", AllowedMcpTools: [McpToolNames.TaskAttachFile],
            AllowedPaths: ["src/"], Profile: "implementer")),
            TestContext.Current.CancellationToken);
        Assert.False(expansion.Success);
        Assert.Contains("cannot expand", expansion.Error!, StringComparison.OrdinalIgnoreCase);

        var optionalExpansion = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Try an unknown optional grant", AllowedPaths: ["src/"],
            Profile: "implementer", OptionalMcpTools: [McpToolNames.PipelineStartReview])),
            TestContext.Current.CancellationToken);
        Assert.False(optionalExpansion.Success);
        Assert.Contains("optional grant", optionalExpansion.Error!, StringComparison.OrdinalIgnoreCase);

        var unknown = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Unknown profile", Profile: "unknown")),
            TestContext.Current.CancellationToken);
        Assert.False(unknown.Success);
        Assert.Contains("Unknown subtask profile", unknown.Error);
    }
}
