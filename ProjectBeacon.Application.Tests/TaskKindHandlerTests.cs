namespace ProjectBeacon.Application.Tests;

using Application.Agents;
using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed class TaskKindHandlerTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public TaskKindHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Upsert_StoresNote_WithoutChangingLaunchCommand()
    {
        var userId = Guid.NewGuid();
        var handler = new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection));
        var created = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(null, "qwen", ModelBackendType.LlamaCpp, "llama-server -m q.gguf", 4096, 30, userId, null, false, "UI screens")));
        Assert.True(created.Success, created.Error);
        Assert.Equal("llama-server -m q.gguf", created.Value!.LaunchCommand);
        Assert.Equal("UI screens", created.Value.Note);
    }

    [Fact]
    public async Task CreateTask_CopiesBuiltInPhases_AndSubtaskLandsOnDo()
    {
        Guid projectId;
        Guid orgId;
        Guid userId;
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("kind-user", "kind@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            userId = user.Id;
            orgId = org.Id;
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            await _db.SaveChangesAsync();
            projectId = project.Id;
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId, orgId));
        var created = await new CreateTaskHandler(factory).HandleAsync(new CreateTaskCommand(
            new CreateTaskRequest("Look at screens", null, projectId, TaskPriority.Medium, TaskType.Task, null, null, null, null, userId)));
        Assert.True(created.Success, created.Error);

        var phases = await new ListTaskPhasesHandler(factory).HandleAsync(new ListTaskPhasesCommand(created.Value!.Id));
        Assert.Equal(new[] { "understand", "do", "check" }, phases.Value!.Select(p => p.Key).ToList());

        await new StartPipelineHandler(factory, new ManualSessionSpawner(new Infrastructure.LlamaSwap.LlamaSwapOptions()))
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(created.Value.Id)));
        var sub = await new CreateSubtaskHandler(factory).HandleAsync(
            new CreateSubtaskCommand(new CreateSubtaskRequest(created.Value.Id, "Review the settings screen")));
        Assert.True(sub.Success, sub.Error);
        Assert.Equal(phases.Value!.Single(p => p.Key == "do").Id, sub.Value!.TaskPhaseId);
    }

    private static TenantContext Scope(Guid projectId, Guid orgId)
    {
        var tenant = new TenantContext();
        tenant.Assign(projectId, orgId, false);
        return tenant;
    }
}
