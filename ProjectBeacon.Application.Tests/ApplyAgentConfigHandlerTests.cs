namespace ProjectBeacon.Application.Tests;

using Application.Agents;
using Application.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ApplyAgentConfigHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public ApplyAgentConfigHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static TenantContext Scope(Guid projectId)
    {
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, unscoped: false);
        return tenant;
    }

    [Fact]
    public async Task Solo_BindsAllRoles_AndEnqueuesApply()
    {
        var (user, project, deviceId, backendId) = await SeedAsync();
        var factory = HandlerSqlite.Factory(_connection, Scope(project.Id));
        var handler = new ApplyAgentConfigHandler(factory, new SetRoleBindingHandler(factory), new EnqueueCommandHandler(factory));

        var result = await handler.HandleAsync(new ApplyAgentConfigCommand(new ApplyAgentConfigRequest(
            project.Id, deviceId, user.Id, AgentRunMode.Solo, backendId, null, null, null)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkstationCommandKind.ApplyOpencode, result.Value!.Kind);
        Assert.Contains("beacon-local/", result.Value.PayloadJson);

        using (TenantScope.EnterUnscoped())
        {
            var bindings = await _db.RoleBindings.IgnoreQueryFilters().Where(b => b.ProjectId == project.Id).ToListAsync();
            Assert.Equal(3, bindings.Count);
            Assert.All(bindings, b => Assert.Equal(backendId, b.ModelBackendId));
        }
    }

    [Fact]
    public async Task MissingRuntime_Fails()
    {
        var (user, project, deviceId, backendId) = await SeedAsync(attachRuntime: false);
        var factory = HandlerSqlite.Factory(_connection, Scope(project.Id));
        var handler = new ApplyAgentConfigHandler(factory, new SetRoleBindingHandler(factory), new EnqueueCommandHandler(factory));

        var result = await handler.HandleAsync(new ApplyAgentConfigCommand(new ApplyAgentConfigRequest(
            project.Id, deviceId, user.Id, AgentRunMode.Solo, backendId, null, null, null)));

        Assert.False(result.Success);
        Assert.Equal("Project runtime is not attached to this device.", result.Error);
    }

    private async Task<(User User, Project Project, Guid DeviceId, Guid BackendId)> SeedAsync(bool attachRuntime = true)
    {
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("dev", "dev@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var backend = LocalModelBackend.Create("qwen", ModelBackendType.LlamaCpp, "llama-server -m q.gguf", 4096, 300, project.Id);
            _db.LocalModelBackends.Add(backend);
            await _db.SaveChangesAsync();

            var created = await new CreateDeviceHandler(HandlerSqlite.Factory(_connection))
                .HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
            Assert.True(created.Success, created.Error);
            await new HeartbeatDeviceHandler(HandlerSqlite.Factory(_connection))
                .HandleAsync(new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value!.Id, "{}", "{}")));
            if (attachRuntime)
            {
                await new AttachRuntimeHandler(HandlerSqlite.Factory(_connection))
                    .HandleAsync(new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, created.Value.Id, user.Id, @"A:\work\app")));
            }
            return (user, project, created.Value.Id, backend.Id);
        }
    }
}
