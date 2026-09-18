namespace ProjectBeacon.Application.Tests;

using Application.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class DeviceLlamaSwapProxyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public DeviceLlamaSwapProxyTests()
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
    public void ParseProbe_ReadsStatus()
    {
        var status = DeviceLlamaSwapProxy.ParseProbe("""{"llamaSwapStatus":{"available":true,"healthy":true,"loadedModel":"qwen","memory":"1 GB","error":null}}""");
        Assert.NotNull(status);
        Assert.True(status!.Available);
        Assert.True(status.Healthy);
        Assert.Equal("qwen", status.LoadedModel);
        Assert.Equal("1 GB", status.Memory);
    }

    [Fact]
    public async Task GetStatus_UsesOnlineDeviceProbe()
    {
        Guid projectId;
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
            await _db.SaveChangesAsync();
            projectId = project.Id;
            var created = await new CreateDeviceHandler(HandlerSqlite.Factory(_connection))
                .HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
            await new HeartbeatDeviceHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
                new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value!.Id,
                    """{"llamaSwapStatus":{"available":true,"healthy":true,"loadedModel":"qwen"}}""", "{}")));
            await new AttachRuntimeHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
                new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, created.Value.Id, user.Id, @"A:\work")));
        }

        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var proxy = new DeviceLlamaSwapProxy(factory, new EnqueueCommandHandler(factory), new GetCommandHandler(factory));
        var status = await proxy.GetStatusAsync();
        Assert.True(status.Available);
        Assert.Equal("qwen", status.LoadedModel);
    }

    [Fact]
    public async Task LlamaSwapConfig_FromAttachedBackends()
    {
        Guid deviceId;
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
            _db.LocalModelBackends.Add(LocalModelBackend.Create("qwen", ModelBackendType.LlamaCpp, "llama-server -m q.gguf", 4096, 30, project.Id));
            await _db.SaveChangesAsync();
            var created = await new CreateDeviceHandler(HandlerSqlite.Factory(_connection))
                .HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
            deviceId = created.Value!.Id;
            await new AttachRuntimeHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
                new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, user.Id, @"A:\work")));
        }

        var result = await new GetLlamaSwapConfigHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new GetLlamaSwapConfigCommand(new GetLlamaSwapConfigRequest(deviceId)));
        Assert.True(result.Success, result.Error);
        Assert.Contains("qwen", result.Value!.Yaml);
        Assert.Contains("llama-server", result.Value.Yaml);
        Assert.Equal(8080, result.Value.Port);
    }
}
