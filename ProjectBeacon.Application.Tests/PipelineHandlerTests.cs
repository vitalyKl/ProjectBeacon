namespace ProjectBeacon.Application.Tests;

using Application.Devices;
using Application.Tasks;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.Data.Sqlite;

public sealed partial class PipelineHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public PipelineHandlerTests()
    {
        // Handlers' contexts must see the fail-closed tenant filter,
        // so dispose the unscoped token right away.
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<(Guid ProjectId, TaskItem Task)> SeedTaskAsync(string title, string? description = null)
    {
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create("PipelineOrg", null);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("PipelineProject", null, org.Id);
            _db.Projects.Add(project);
            await _db.SaveChangesAsync();
            var task = TaskItem.Create(title, project.Id);
            task.Update(description: description);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync();
            return (project.Id, task);
        }
    }

    private async Task ProveReviewAsync(Guid projectId, Guid taskId, BeaconDbFactory factory, int exitCode)
    {
        User user;
        DaemonDevice device;
        using (TenantScope.EnterUnscoped())
        {
            user = User.Create("rev", $"rev-{Guid.NewGuid():N}@beacon.local", "hash");
            _db.Users.Add(user);
            _db.ProjectMembers.Add(ProjectMember.Create(projectId, user.Id, MemberRole.Owner));
            device = DaemonDevice.Create("review", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash", "bcd_" + Guid.NewGuid().ToString("N"));
            _db.DaemonDevices.Add(device);
            await _db.SaveChangesAsync();
        }

        var beat = await new HeartbeatDeviceHandler(factory).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(device.Id, "{}", "{}")));
        Assert.True(beat.Success, beat.Error);
        var attach = await new AttachRuntimeHandler(factory).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, device.Id, user.Id, @"A:\work\review")));
        Assert.True(attach.Success, attach.Error);

        var check = await new RecordReviewCheckHandler(factory, new EnqueueCommandHandler(factory))
            .HandleAsync(new RecordReviewCheckCommand(new RecordReviewCheckRequest(taskId, device.Id, user.Id, "dotnet test")));
        Assert.True(check.Success, check.Error);

        var stillOpen = await new ApprovePipelineHandler(factory)
            .HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(taskId)));
        Assert.False(stillOpen.Success);

        Guid commandId;
        using (TenantScope.EnterUnscoped())
        {
            commandId = _db.WorkstationCommands
                .Where(c => c.DeviceId == device.Id && c.Kind == WorkstationCommandKind.RunReviewCheck)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => c.Id)
                .First();
        }

        var resultJson = "{\"exitCode\":" + exitCode + ",\"passed\":true,\"checkOutput\":\"build\"}";
        var done = await new CompleteCommandHandler(factory).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(commandId, device.Id, true, resultJson, null)));
        Assert.True(done.Success, done.Error);
    }

    private static TenantContext Scope(Guid projectId)
    {
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, unscoped: false);
        return tenant;
    }

    private sealed class FakeSpawner : ISessionSpawner
    {
        public List<(Guid ProjectId, PipelineRole Role)> Calls { get; } = [];

        public Task<SpawnedSession> SpawnAsync(IBeaconDb db, Guid projectId, PipelineRole role, Guid taskId, CancellationToken ct)
        {
            Calls.Add((projectId, role));
            return Task.FromResult(new SpawnedSession(null, null, null));
        }
    }


}


