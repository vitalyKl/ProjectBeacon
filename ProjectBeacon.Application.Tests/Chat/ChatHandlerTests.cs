namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Application.Chat;
using Application.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Chat;
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
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);

        var appended = await new AppendChatPartHandler(factory, new ChatEventSignal()).HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "hello", "p1")), TestContext.Current.CancellationToken);
        Assert.True(appended.Success, appended.Error);

        var dup = await new AppendChatPartHandler(factory, new ChatEventSignal()).HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "hello", "p1")), TestContext.Current.CancellationToken);
        Assert.Equal(appended.Value!.Id, dup.Value!.Id);

        var listed = await new ListChatPartsHandler(factory).HandleAsync(
            new ListChatPartsCommand(new ListChatPartsRequest(sessionId, Guid.Empty)), TestContext.Current.CancellationToken);
        Assert.Single(listed.Value!);
        Assert.Equal("hello", listed.Value![0].Body);
    }

    [Fact]
    public async Task MarkIdle_FromDevice()
    {
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        await using var db = factory.CreateDbContext();
        var row = await db.ChatSessions.FirstAsync(s => s.Id == sessionId, TestContext.Current.CancellationToken);
        row.SetStreaming();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var idle = await new MarkChatIdleHandler(factory, new ChatEventSignal()).HandleAsync(
            new MarkChatIdleCommand(new MarkChatIdleRequest(sessionId, deviceId)), TestContext.Current.CancellationToken);
        Assert.True(idle.Success, idle.Error);
        Assert.Equal(ChatSessionStatus.Idle, idle.Value!.Status);
    }

    [Fact]
    public async Task AppendToolPart_RoundTrip_AndCapsFields()
    {
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;

        var appended = await new AppendChatPartHandler(factory, new ChatEventSignal()).HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "tool",
                "", "p2", new string('t', 50), "completed", "{\"query\":1}", "found 3", -5)), ct);
        Assert.True(appended.Success, appended.Error);
        Assert.Equal(new string('t', 40), appended.Value!.Tool);
        Assert.Equal("completed", appended.Value!.ToolStatus);
        Assert.Equal("{\"query\":1}", appended.Value!.ToolInput);
        Assert.Equal("found 3", appended.Value!.ToolResult);
        Assert.Null(appended.Value!.DurationMs);

        var listed = await new ListChatPartsHandler(factory).HandleAsync(
            new ListChatPartsCommand(new ListChatPartsRequest(sessionId, Guid.Empty)), ct);
        Assert.True(listed.Success, listed.Error);
        Assert.Equal(new string('t', 40), listed.Value![0].Tool);
        Assert.Equal("found 3", listed.Value![0].ToolResult);
        Assert.Equal(0, listed.Value![0].SortOrder);
    }

    [Fact]
    public async Task RenameChatSession_RenamesAndReturnsProjectName()
    {
        var (projectId, userId, orgId, _, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, orgId, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;

        var renamed = await new RenameChatSessionHandler(factory).HandleAsync(
            new RenameChatSessionCommand(new RenameChatSessionRequest(sessionId, userId, "  Renamed Chat  ")), ct);
        Assert.True(renamed.Success, renamed.Error);
        Assert.Equal("Renamed Chat", renamed.Value!.Title);
        Assert.Equal("P", renamed.Value!.ProjectName);

        var blank = await new RenameChatSessionHandler(factory).HandleAsync(
            new RenameChatSessionCommand(new RenameChatSessionRequest(sessionId, userId, "   ")), ct);
        Assert.False(blank.Success);
        Assert.Equal("Title is required.", blank.Error);
    }

    [Fact]
    public async Task ListChatSessions_IncludesProjectName()
    {
        var (projectId, userId, orgId, _, _) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, orgId, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);

        var listed = await new ListChatSessionsHandler(factory).HandleAsync(
            new ListChatSessionsCommand(new ListChatSessionsRequest(userId)), TestContext.Current.CancellationToken);
        Assert.True(listed.Success, listed.Error);
        Assert.Single(listed.Value!);
        Assert.Equal("P", listed.Value![0].ProjectName);
    }

    [Fact]
    public async Task ArchiveChatSession_HidesFromLists()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;
        var archive = new ArchiveChatSessionHandler(factory, new AbortChatHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal()), new ChatEventSignal());

        var archived = await archive.HandleAsync(
            new ArchiveChatSessionCommand(new ArchiveChatSessionRequest(sessionId, userId)), ct);
        Assert.True(archived.Success, archived.Error);
        Assert.Equal(ChatSessionStatus.Idle, archived.Value!.Status);

        var listed = await new ListChatSessionsHandler(factory).HandleAsync(
            new ListChatSessionsCommand(new ListChatSessionsRequest(userId)), ct);
        Assert.Empty(listed.Value!);

        var got = await new GetChatSessionHandler(factory).HandleAsync(
            new GetChatSessionCommand(new GetChatSessionRequest(sessionId, userId)), ct);
        Assert.False(got.Success);
        Assert.Equal("Chat session not found.", got.Error);

        var parts = await new ListChatPartsHandler(factory).HandleAsync(
            new ListChatPartsCommand(new ListChatPartsRequest(sessionId, userId)), ct);
        Assert.False(parts.Success);
        Assert.Equal("Chat session not found.", parts.Error);
    }

    [Fact]
    public async Task ArchiveChatSession_AbortsStreamingFirst_AndBlocksWhenDeviceOffline()
    {
        var (projectId, userId, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;
        await using var db = factory.CreateDbContext();
        var row = await db.ChatSessions.FirstAsync(s => s.Id == sessionId, ct);
        row.SetStreaming();
        await db.SaveChangesAsync(ct);

        var archive = new ArchiveChatSessionHandler(factory, new AbortChatHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal()), new ChatEventSignal());
        var blocked = await archive.HandleAsync(
            new ArchiveChatSessionCommand(new ArchiveChatSessionRequest(sessionId, userId)), ct);
        Assert.False(blocked.Success);
        Assert.Equal("Device is not connected.", blocked.Error);

        var listed = await new ListChatSessionsHandler(factory).HandleAsync(
            new ListChatSessionsCommand(new ListChatSessionsRequest(userId)), ct);
        Assert.Single(listed.Value!);
    }
    private async Task<(Guid ProjectId, Guid UserId, Guid OrgId, Guid DeviceId, Guid SessionId)> SeedSessionAsync()
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
            return (project.Id, user.Id, org.Id, deviceId, session.Id);
        }
    }
    [Fact]
    public async Task ArchiveChatSession_SignalsChange()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;
        var signal = new ChatEventSignal();
        var archive = new ArchiveChatSessionHandler(factory, new AbortChatHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal()), signal);

        var wait = signal.WaitAsync(sessionId, TimeSpan.FromSeconds(5));
        var archived = await archive.HandleAsync(
            new ArchiveChatSessionCommand(new ArchiveChatSessionRequest(sessionId, userId)), ct);
        Assert.True(archived.Success, archived.Error);

        var changed = await wait;
        Assert.True(changed);
    }

    [Fact]
    public async Task ArchiveChatSession_ForgetsSignalEntry()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;
        var signal = new ChatEventSignal();
        signal.Signal(sessionId);
        Assert.Equal(1, signal.ActiveSessions);
        var archive = new ArchiveChatSessionHandler(factory, new AbortChatHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal()), signal);

        var archived = await archive.HandleAsync(
            new ArchiveChatSessionCommand(new ArchiveChatSessionRequest(sessionId, userId)), ct);
        Assert.True(archived.Success, archived.Error);
        Assert.Equal(0, signal.ActiveSessions);
    }
}
