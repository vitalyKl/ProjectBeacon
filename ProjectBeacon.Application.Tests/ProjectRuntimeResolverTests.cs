namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Common;
using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ProjectRuntimeResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public ProjectRuntimeResolverTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    private async Task<(User User, Project Project, Guid DeviceId)> SeedOnlineProjectDeviceAsync()
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
            await _db.SaveChangesAsync();

            var created = await new CreateDeviceHandler(Factory())
                .HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)));
            var deviceId = created.Value!.Id;
            await new HeartbeatDeviceHandler(Factory())
                .HandleAsync(new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));
            return (user, project, deviceId);
        }
    }

    private async Task<Guid> AttachRuntimeAsync(Guid projectId, Guid deviceId, Guid userId, string localRoot = @"A:\work\app")
    {
        using (TenantScope.EnterUnscoped())
        {
            var result = await new AttachRuntimeHandler(Factory())
                .HandleAsync(new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, deviceId, userId, localRoot)));
            return result.Value!.Id;
        }
    }

    [Fact]
    public async Task ResolveAsync_ValidRuntime_ReturnsRuntime()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        await using var db = Factory().CreateDbContext();
        var result = await ProjectRuntimeResolver.ResolveAsync(db, project.Id, deviceId);
        Assert.True(result.Success, result.Error);
        Assert.Equal(@"A:\work\app", result.Value!.LocalRoot);
    }

    [Fact]
    public async Task ResolveAsync_NoRuntime_Fails()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();

        await using var db = Factory().CreateDbContext();
        var result = await ProjectRuntimeResolver.ResolveAsync(db, project.Id, deviceId);
        Assert.False(result.Success);
        Assert.Equal(CommandSandbox.RuntimeRequired, result.Error);
    }

    [Fact]
    public async Task ResolveAsync_WrongDevice_Fails()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var otherCreated = await new CreateDeviceHandler(Factory())
            .HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("other", "fp-other-" + Guid.NewGuid().ToString("N"), user.Id)));

        await using var db = Factory().CreateDbContext();
        var result = await ProjectRuntimeResolver.ResolveAsync(db, project.Id, otherCreated.Value!.Id);
        Assert.False(result.Success);
        Assert.Equal(CommandSandbox.RuntimeRequired, result.Error);
    }

    [Fact]
    public async Task ResolveAsync_WrongProject_Fails()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        using (TenantScope.EnterUnscoped())
        {
            var other = Project.Create("Other", null, project.OrgId);
            _db.Projects.Add(other);
            await _db.SaveChangesAsync();

            var db = Factory().CreateDbContext();
            var result = await ProjectRuntimeResolver.ResolveAsync(db, other.Id, deviceId);
            Assert.False(result.Success);
            Assert.Equal(CommandSandbox.RuntimeRequired, result.Error);
        }
    }

    [Fact]
    public async Task ResolveRootAsync_NonProjectKind_ReturnsNull()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        using (TenantScope.EnterUnscoped())
        {
            var cmd = WorkstationCommand.Create(deviceId, WorkstationCommandKind.ListDir, "{}");
            _db.WorkstationCommands.Add(cmd);
            await _db.SaveChangesAsync();

            var db = Factory().CreateDbContext();
            var row = await db.WorkstationCommands.FirstAsync(c => c.Id == cmd.Id);
            var root = await ProjectRuntimeResolver.ResolveRootAsync(db, row);
            Assert.Null(root);
        }
    }

    [Fact]
    public async Task ResolveRootsAsync_ReturnsBatch()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        using (TenantScope.EnterUnscoped())
        {
            var second = Project.Create("P2", null, project.OrgId);
            _db.Projects.Add(second);
            _db.ProjectMembers.Add(ProjectMember.Create(second.Id, user.Id, MemberRole.Owner));
            await _db.SaveChangesAsync();
            await AttachRuntimeAsync(second.Id, deviceId, user.Id, @"A:\work\second");
        }

        await using var db = Factory().CreateDbContext();
        var roots = await ProjectRuntimeResolver.ResolveRootsAsync(db, deviceId, new[] { project.Id, Guid.NewGuid() });
        Assert.Single(roots);
        Assert.Equal(@"A:\work\app", roots[project.Id]);
    }

    [Fact]
    public async Task Enqueue_CallerSuppliedRoot_IsStripped()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"src","root":"C:\\\\evil","text":"hi"}""", project.Id)));
        Assert.True(queued.Success, queued.Error);
        Assert.Equal(@"A:\work\app", queued.Value!.LocalRoot);
        using var doc = JsonDocument.Parse(queued.Value.PayloadJson);
        Assert.Equal("src", doc.RootElement.GetProperty("path").GetString());
        Assert.False(doc.RootElement.TryGetProperty("root", out _));
    }

    [Fact]
    public async Task Enqueue_ValidRelativePath_Passes()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.RunEvalTurn,
                """{"path":"src/main.cs","prompt":"review"}""", project.Id)));
        Assert.True(queued.Success, queued.Error);
        Assert.Equal(@"A:\work\app", queued.Value!.LocalRoot);
        using var doc = JsonDocument.Parse(queued.Value.PayloadJson);
        Assert.Equal("src/main.cs", doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Enqueue_DotDotForwardSlash_Rejected()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"../secret","text":"x"}""", project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, queued.Error);
    }

    [Fact]
    public async Task Enqueue_DotDotBackslash_Rejected()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"..\\secret","text":"x"}""", project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, queued.Error);
    }

    [Fact]
    public async Task Enqueue_DotDotNested_Rejected()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"a/../b","text":"x"}""", project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, queued.Error);
    }

    [Fact]
    public async Task Enqueue_DotDotMixedSeparators_Rejected()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"a\\..\\b","text":"x"}""", project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, queued.Error);
    }

    [Fact]
    public async Task Enqueue_DotsInFilename_NotRejected()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"src/file..name.cs","text":"x"}""", project.Id)));
        Assert.True(queued.Success, queued.Error);
    }

    [Fact]
    public async Task Enqueue_RootedPath_Rejected()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.RunEvalTurn,
                """{"path":"C:\\\\secret","prompt":"x"}""", project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, queued.Error);
    }

    [Fact]
    public async Task Enqueue_NullByte_Rejected()
    {
        var (user, project, deviceId) = await SeedOnlineProjectDeviceAsync();
        await AttachRuntimeAsync(project.Id, deviceId, user.Id);

        var payload = """{"path":"src\u0000evil","text":"x"}""";
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt, payload, project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, queued.Error);
    }
}
