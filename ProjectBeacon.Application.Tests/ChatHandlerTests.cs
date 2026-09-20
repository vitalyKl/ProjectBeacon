namespace ProjectBeacon.Application.Tests;

using Application.Chat;
using Application.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ChatHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public ChatHandlerTests()
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
    public async Task AppendPart_AndList_RoundTrip()
    {
        var (projectId, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);

        var appended = await new AppendChatPartHandler(factory).HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "hello", "p1")));
        Assert.True(appended.Success, appended.Error);

        var dup = await new AppendChatPartHandler(factory).HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "hello", "p1")));
        Assert.Equal(appended.Value!.Id, dup.Value!.Id);

        var listed = await new ListChatPartsHandler(factory).HandleAsync(
            new ListChatPartsCommand(new ListChatPartsRequest(sessionId, Guid.Empty)));
        Assert.Single(listed.Value!);
        Assert.Equal("hello", listed.Value![0].Body);
    }

    [Fact]
    public async Task MarkIdle_FromDevice()
    {
        var (projectId, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        await using var db = factory.CreateDbContext();
        var row = await db.ChatSessions.FirstAsync(s => s.Id == sessionId);
        row.SetStreaming();
        await db.SaveChangesAsync();

        var idle = await new MarkChatIdleHandler(factory).HandleAsync(
            new MarkChatIdleCommand(new MarkChatIdleRequest(sessionId, deviceId)));
        Assert.True(idle.Success, idle.Error);
        Assert.Equal(ChatSessionStatus.Idle, idle.Value!.Status);
    }

    private async Task<(Guid ProjectId, Guid DeviceId, Guid SessionId)> SeedSessionAsync()
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
            var created = await new CreateDeviceHandler(HandlerSqlite.Factory(_connection))
                .HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
            var deviceId = created.Value!.Id;
            var session = ChatSession.Create(project.Id, deviceId, "Chat", @"A:\work");
            session.SetExternalSessionId("oc_test");
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync();
            return (project.Id, deviceId, session.Id);
        }
    }
}
