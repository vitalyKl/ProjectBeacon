namespace ProjectBeacon.Application.Tests;

using Application.Chat;
using Application.Devices;
using Application.Persistence;
using Domain.Entities.Chat;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Error paths must not report success before completion: failed enqueue or device
/// completion leaves no persisted prompt, streaming state, or orphan session row.
/// </summary>
public sealed class ChatErrorPathTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public ChatErrorPathTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Seeds a project with a device that has not heartbeated (offline).</summary>
    private async Task<(Guid ProjectId, Guid DeviceId, Guid UserId)> SeedProjectDeviceAsync()
    {
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("dev", $"dev-{Guid.NewGuid():N}@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var device = DaemonDevice.Create("offline", user.Id,
                "fp-" + Guid.NewGuid().ToString("N"), "hash", "bcd_" + Guid.NewGuid().ToString("N"));
            _db.DaemonDevices.Add(device);
            await _db.SaveChangesAsync();
            return (project.Id, device.Id, user.Id);
        }
    }

    private static BeaconDbFactory ScopedFactory(SqliteConnection connection, Guid projectId)
    {
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        return HandlerSqlite.Factory(connection, tenant);
    }

    [Fact]
    public async Task SendChatPrompt_EnqueueFailure_PersistsNothing()
    {
        var (projectId, deviceId, userId) = await SeedProjectDeviceAsync();
        using (TenantScope.EnterUnscoped())
        {
            var session = ChatSession.Create(projectId, deviceId, "Chat", @"A:\work");
            session.SetExternalSessionId("oc_test");
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var factory = ScopedFactory(_connection, projectId);
            var sent = await new SendChatPromptHandler(factory, new ChatEventSignal())
                .HandleAsync(new SendChatPromptCommand(new SendChatPromptRequest(session.Id, userId, "hello", null)), TestContext.Current.CancellationToken);

            Assert.False(sent.Success);
            Assert.Equal("Device is not connected.", sent.Error);
            Assert.Equal(0, await _db.ChatParts.CountAsync(p => p.SessionId == session.Id, TestContext.Current.CancellationToken));
            var status = await _db.ChatSessions.AsNoTracking()
                .Where(s => s.Id == session.Id)
                .Select(s => s.Status)
                .FirstAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChatSessionStatus.Idle, status);
        }
    }

    [Fact]
    public async Task AbortChat_EnqueueFailure_KeepsPreviousStatus()
    {
        var (projectId, deviceId, userId) = await SeedProjectDeviceAsync();
        using (TenantScope.EnterUnscoped())
        {
            var session = ChatSession.Create(projectId, deviceId, "Chat", @"A:\work");
            session.SetExternalSessionId("oc_test");
            session.SetStreaming();
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var factory = ScopedFactory(_connection, projectId);
            var aborted = await new AbortChatHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal())
                .HandleAsync(new AbortChatCommand(new AbortChatRequest(session.Id, userId)), TestContext.Current.CancellationToken);

            Assert.False(aborted.Success);
            Assert.Equal("Device is not connected.", aborted.Error);
            var status = await _db.ChatSessions.AsNoTracking()
                .Where(s => s.Id == session.Id)
                .Select(s => s.Status)
                .FirstAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChatSessionStatus.Streaming, status);
        }
    }

    /// <summary>
    /// Uses a file database with two connections so the test's completion of the
    /// ensure-session command genuinely races the handler's polling loop.
    /// </summary>
    [Fact]
    public async Task CreateChatSession_DevSessionFailure_LeavesNoRow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beacon-chat-{Guid.NewGuid():N}.db");
        try
        {
            var testConnection = new SqliteConnection($"Data Source={path}");
            testConnection.Open();
            var testDb = new BeaconDbContext(new DbContextOptionsBuilder<BeaconDbContext>()
                .UseSqlite(testConnection).Options);
            using (TenantScope.EnterUnscoped())
            {
                testDb.Database.EnsureCreated();

                var user = User.Create("dev", $"dev-{Guid.NewGuid():N}@beacon.local", "hash");
                var org = Org.Create("Org");
                testDb.Users.Add(user);
                testDb.Orgs.Add(org);
                await testDb.SaveChangesAsync(TestContext.Current.CancellationToken);
                var project = Project.Create("P", null, org.Id);
                testDb.Projects.Add(project);
                testDb.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
                var device = DaemonDevice.Create("online", user.Id,
                    "fp-" + Guid.NewGuid().ToString("N"), "hash", "bcd_" + Guid.NewGuid().ToString("N"));
                testDb.DaemonDevices.Add(device);
                await testDb.SaveChangesAsync(TestContext.Current.CancellationToken);
                var (projectId, deviceId, userId) = (project.Id, device.Id, user.Id);

                var handlerConnection = new SqliteConnection($"Data Source={path}");
                handlerConnection.Open();
                var factory = ScopedFactory(handlerConnection, projectId);
                try
                {
                    var beat = await new HeartbeatDeviceHandler(factory)
                        .HandleAsync(new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
                    Assert.True(beat.Success, beat.Error);
                    var attach = await new AttachRuntimeHandler(factory)
                        .HandleAsync(new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, deviceId, userId, @"A:\work")), TestContext.Current.CancellationToken);
                    Assert.True(attach.Success, attach.Error);

                    using (TenantScope.EnterUnscoped())
                    {
                        var createTask = Task.Run(() => new CreateChatSessionHandler(factory, new EnqueueCommandHandler(factory), new GetCommandHandler(factory))
                            .HandleAsync(new CreateChatSessionCommand(new CreateChatSessionRequest(userId, "Chat"))));

                        // Wait for the ensure-session command to be enqueued, then fail it on the device.
                        Guid commandId = Guid.Empty;
                        var deadline = DateTime.UtcNow.AddSeconds(20);
                        while (commandId == Guid.Empty && DateTime.UtcNow < deadline)
                        {
                            commandId = await testDb.WorkstationCommands
                                .Where(c => c.DeviceId == deviceId && c.Kind == WorkstationCommandKind.ChatEnsureSession)
                                .Select(c => (Guid?)c.Id)
                                .FirstOrDefaultAsync(TestContext.Current.CancellationToken) ?? Guid.Empty;
                            if (commandId == Guid.Empty)
                                await Task.Delay(100, cancellationToken: TestContext.Current.CancellationToken);
                        }
                        Assert.NotEqual(Guid.Empty, commandId);

                        var failed = await new CompleteCommandHandler(factory).HandleAsync(
                            new CompleteCommandCommand(new CompleteCommandRequest(commandId, deviceId, false, null, "boom")), TestContext.Current.CancellationToken);
                        Assert.True(failed.Success, failed.Error);

                        var created = await createTask;
                        Assert.False(created.Success);
                        Assert.Equal("boom", created.Error);
                        Assert.Equal(0, await testDb.ChatSessions.CountAsync(TestContext.Current.CancellationToken));
                    }
                }
                finally
                {
                    handlerConnection.Dispose();
                }
            }
            testDb.Dispose();
            testConnection.Dispose();
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
