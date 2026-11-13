namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Application.Authorization;
using Application.Devices;
using Application.Tasks;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    private async Task<(Guid ProjectId, TaskItem Task)> SeedTaskAsync(
        string title,
        string? description = null,
        bool seedPipelinePrerequisites = true)
    {
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create("PipelineOrg", null);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var project = Project.Create("PipelineProject", null, org.Id);
            _db.Projects.Add(project);
            var task = TaskItem.Create(title, project.Id);
            task.Update(description: description);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Most pipeline tests exercise a runnable task. Keep their fixture aligned with
            // production prerequisites; tests for missing/offline prerequisites opt out.
            if (seedPipelinePrerequisites)
            {
                var owner = User.Create(
                    "pipeline-" + Guid.NewGuid().ToString("N"),
                    $"pipeline-{Guid.NewGuid():N}@beacon.local",
                    "hash");
                var device = DaemonDevice.Create(
                    "pipeline-native-workstation",
                    owner.Id,
                    "fp-" + Guid.NewGuid().ToString("N"),
                    "hash-" + Guid.NewGuid().ToString("N"),
                    "bcd_" + Guid.NewGuid().ToString("N"));
                device.Heartbeat("{}", """{"agentRuntime":"native"}""");
                var backend = LocalModelBackend.Create(
                    "pipeline-planner-model",
                    ModelBackendType.LlamaCpp,
                    "llama-server -m model.gguf",
                    8192,
                    60,
                    owner.Id);

                _db.Users.Add(owner);
                _db.DaemonDevices.Add(device);
                _db.LocalModelBackends.Add(backend);
                _db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, Path.GetTempPath()));
                _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, backend.Id, project.Id));
                await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            return (project.Id, task);
        }
    }

    private async Task<Guid> SeedNativeRuntimeAsync(Guid projectId)
    {
        var user = User.Create("native", $"native-{Guid.NewGuid():N}@beacon.local", "hash");
        var device = DaemonDevice.Create(
            "native workstation",
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        device.Heartbeat("{}", """{"agentRuntime":"native"}""");
        var backend = LocalModelBackend.Create(
            "native-test-model",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            user.Id);
        var runtime = ProjectRuntime.Create(projectId, device.Id, "/workspace/native");

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(user);
            _db.DaemonDevices.Add(device);
            _db.LocalModelBackends.Add(backend);
            _db.ProjectRuntimes.Add(runtime);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return backend.Id;
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
            .HandleAsync(new RecordReviewCheckCommand(
                new RecordReviewCheckRequest(taskId, device.Id, user.Id, "dotnet test"),
                new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None)));
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

    private sealed class FakeSpawner(Guid? modelBackendId = null) : ISessionSpawner
    {
        public List<(Guid ProjectId, PipelineRole Role)> Calls { get; } = [];

        public async Task<SpawnedSession> SpawnAsync(
            IBeaconDb db, Guid projectId, PipelineRole role, Guid taskId, CancellationToken ct)
        {
            Calls.Add((projectId, role));
            var resolvedBackendId = modelBackendId;
            if (resolvedBackendId is null)
            {
                var phaseKey = role switch
                {
                    PipelineRole.Planner => "understand",
                    PipelineRole.Actor => "do",
                    PipelineRole.Review => "check",
                    _ => string.Empty
                };
                if (phaseKey.Length > 0)
                {
                    resolvedBackendId = await db.TaskPhases.AsNoTracking()
                        .Where(phase => phase.TaskId == taskId && phase.Key == phaseKey)
                        .Select(phase => phase.ModelBackendId)
                        .FirstOrDefaultAsync(ct);
                }

                resolvedBackendId ??= await db.RoleBindings.AsNoTracking()
                    .Where(binding => binding.ProjectId == projectId && binding.Role == role)
                    .Select(binding => (Guid?)binding.ModelBackendId)
                    .FirstOrDefaultAsync(ct);
                resolvedBackendId ??= await db.RoleBindings.AsNoTracking()
                    .Where(binding => binding.ProjectId == projectId && binding.Role == PipelineRole.Planner)
                    .Select(binding => (Guid?)binding.ModelBackendId)
                    .FirstOrDefaultAsync(ct);
            }

            return new SpawnedSession(resolvedBackendId, null, null);
        }
    }


}


