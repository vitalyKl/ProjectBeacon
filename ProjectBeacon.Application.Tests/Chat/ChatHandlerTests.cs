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
using System.Text.Json;

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
    [Fact]
    public async Task SwitchChatAgent_EnqueuesCommandWithPayload()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSendableAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var ct = TestContext.Current.CancellationToken;
        var factory = HandlerSqlite.Factory(_connection, tenant);

        var switched = await new SwitchChatAgentHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal())
            .HandleAsync(new SwitchChatAgentCommand(new SwitchChatAgentRequest(sessionId, userId, "  plan ")), ct);
        Assert.True(switched.Success, switched.Error);

        await using var check = factory.CreateDbContext();
        var command = await check.WorkstationCommands
            .Where(c => c.ProjectId == projectId && c.Kind == WorkstationCommandKind.ChatSwitchAgent).SingleAsync(ct);
        using var payload = JsonDocument.Parse(command.PayloadJson);
        Assert.Equal("oc_test", payload.RootElement.GetProperty("externalSessionId").GetString());
        Assert.Equal(sessionId, payload.RootElement.GetProperty("chatSessionId").GetGuid());
        Assert.Equal(projectId, payload.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal("plan", payload.RootElement.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task SwitchChatAgent_RejectsEmptyAgentAndUnknownSession()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var ct = TestContext.Current.CancellationToken;
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var handler = new SwitchChatAgentHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal());

        var blank = await handler.HandleAsync(
            new SwitchChatAgentCommand(new SwitchChatAgentRequest(sessionId, userId, "   ")), ct);
        Assert.False(blank.Success);
        Assert.Equal("Agent is required.", blank.Error);

        var unknown = await handler.HandleAsync(
            new SwitchChatAgentCommand(new SwitchChatAgentRequest(Guid.NewGuid(), userId, "plan")), ct);
        Assert.False(unknown.Success);
        Assert.Equal("Chat session not found.", unknown.Error);
    }

    [Fact]
    public async Task ReplyChatForm_RecordsAnswerAndEnqueues()
    {
        var (projectId, userId, _, deviceId, sessionId) = await SeedSendableAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var ct = TestContext.Current.CancellationToken;
        var factory = HandlerSqlite.Factory(_connection, tenant);

        var questionBody = JsonSerializer.Serialize(new
        {
            id = "f1",
            title = "Pick",
            metadata = "{}",
            fields = new object[] { new { key = "choice", type = "string" } }
        });
        var question = await new AppendChatPartHandler(factory, new ChatEventSignal()).HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "question",
                questionBody, "form:f1", Complete: true)), ct);
        Assert.True(question.Success, question.Error);

        var answer = JsonSerializer.Serialize(new { choice = "b" });
        var handler = new ReplyChatFormHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal());
        var replied = await handler.HandleAsync(
            new ReplyChatFormCommand(new ReplyChatFormRequest(sessionId, userId, "f1", answer)), ct);
        Assert.True(replied.Success, replied.Error);

        await using var check = factory.CreateDbContext();
        var part = await check.ChatParts.SingleAsync(pp => pp.Id == question.Value!.Id, ct);
        using var body = JsonDocument.Parse(part.Body);
        Assert.Equal("b", body.RootElement.GetProperty("answer").GetProperty("choice").GetString());

        var command = await check.WorkstationCommands
            .Where(c => c.ProjectId == projectId && c.Kind == WorkstationCommandKind.ChatReplyForm).SingleAsync(ct);
        using var payload = JsonDocument.Parse(command.PayloadJson);
        Assert.Equal("oc_test", payload.RootElement.GetProperty("externalSessionId").GetString());
        Assert.Equal("f1", payload.RootElement.GetProperty("formId").GetString());
        Assert.Equal(answer, payload.RootElement.GetProperty("answerJson").GetString());

        var again = await handler.HandleAsync(
            new ReplyChatFormCommand(new ReplyChatFormRequest(sessionId, userId, "f1",
                JsonSerializer.Serialize(new { choice = "a" }))), ct);
        Assert.False(again.Success);
        Assert.Equal("Question already answered.", again.Error);
    }

    [Fact]
    public async Task ReplyChatForm_ValidatesAnswerJson()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var ct = TestContext.Current.CancellationToken;
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var handler = new ReplyChatFormHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal());

        var bad = await handler.HandleAsync(
            new ReplyChatFormCommand(new ReplyChatFormRequest(sessionId, userId, "f1", "not-json")), ct);
        Assert.False(bad.Success);
        Assert.Equal("Answer must be a JSON object.", bad.Error);

        var array = await handler.HandleAsync(
            new ReplyChatFormCommand(new ReplyChatFormRequest(sessionId, userId, "f1", "[1]")), ct);
        Assert.False(array.Success);
        Assert.Equal("Answer must be a JSON object.", array.Error);

        var missing = await handler.HandleAsync(
            new ReplyChatFormCommand(new ReplyChatFormRequest(sessionId, userId, "nope", "{}")), ct);
        Assert.False(missing.Success);
        Assert.Equal("Question not found.", missing.Error);
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

    /// <summary>Seeds a session whose device is online and has an attached runtime, so the send handler can enqueue.</summary>
    private async Task<(Guid ProjectId, Guid UserId, Guid OrgId, Guid DeviceId, Guid SessionId)> SeedSendableAsync()
    {
        var (projectId, userId, orgId, deviceId, sessionId) = await SeedSessionAsync();
        using (TenantScope.EnterUnscoped())
        {
            var factory = HandlerSqlite.Factory(_connection);
            var beat = await new HeartbeatDeviceHandler(factory)
                .HandleAsync(new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
            Assert.True(beat.Success, beat.Error);
            var attach = await new AttachRuntimeHandler(factory)
                .HandleAsync(new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, deviceId, userId, @"A:\work")), TestContext.Current.CancellationToken);
            Assert.True(attach.Success, attach.Error);
        }
        return (projectId, userId, orgId, deviceId, sessionId);
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

    [Fact]
    public async Task SendChatPrompt_PersistsPartCommandAndStreaming()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSendableAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var ct = TestContext.Current.CancellationToken;
        var factory = HandlerSqlite.Factory(_connection, tenant);

        var sent = await new SendChatPromptHandler(factory, new ChatEventSignal())
            .HandleAsync(new SendChatPromptCommand(new SendChatPromptRequest(sessionId, userId, "hello", null)), ct);
        Assert.True(sent.Success, sent.Error);
        Assert.Equal(ChatSessionStatus.Streaming, sent.Value!.Status);

        await using var check = factory.CreateDbContext();
        var parts = await check.ChatParts.Where(p => p.SessionId == sessionId).ToListAsync(ct);
        Assert.Single(parts);
        Assert.Equal("user", parts[0].Role);
        Assert.Equal("hello", parts[0].Body);
        var command = await check.WorkstationCommands
            .Where(c => c.ProjectId == projectId && c.Kind == WorkstationCommandKind.ChatPrompt).SingleAsync(ct);
        Assert.Contains("hello", command.PayloadJson);
        using var payload = JsonDocument.Parse(command.PayloadJson);
        Assert.Equal(projectId, payload.RootElement.GetProperty("projectId").GetGuid());
    }

    [Fact]
    public async Task SendChatPrompt_InsertFails_LeavesNoPartAndNoStreaming()
    {
        var (projectId, userId, _, _, sessionId) = await SeedSendableAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var ct = TestContext.Current.CancellationToken;

        // A database trigger fails every command insert: the part and the streaming flag
        // must roll back with it in the same transaction.
        await _db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER chat_prompt_guard BEFORE INSERT ON WorkstationCommands " +
            "BEGIN INSERT INTO chat_test_blocker (x) VALUES (1); END;", ct);
        try
        {
            var factory = HandlerSqlite.Factory(_connection, tenant);
            var sent = await new SendChatPromptHandler(factory, new ChatEventSignal())
                .HandleAsync(new SendChatPromptCommand(new SendChatPromptRequest(sessionId, userId, "hello", null)), ct);
            Assert.False(sent.Success);

            await using var check = factory.CreateDbContext();
            Assert.Equal(0, await check.ChatParts.CountAsync(p => p.SessionId == sessionId, ct));
            Assert.Equal(0, await check.WorkstationCommands.CountAsync(c => c.ProjectId == projectId, ct));
            var status = await check.ChatSessions.AsNoTracking()
                .Where(s => s.Id == sessionId).Select(s => s.Status).FirstAsync(ct);
            Assert.Equal(ChatSessionStatus.Idle, status);
        }
        finally
        {
            await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER chat_prompt_guard;", ct);
        }
    }

    [Fact]
    public async Task AppendPart_FinalOverwritesStreamedSnapshot()
    {
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;
        var append = new AppendChatPartHandler(factory, new ChatEventSignal());

        var streamed = await append.HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "Hel", "m1:text:0")), ct);
        Assert.True(streamed.Success, streamed.Error);
        Assert.False(streamed.Value!.Complete);

        var final = await append.HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "Hello", "m1:text:0", Complete: true)), ct);
        Assert.True(final.Success, final.Error);
        Assert.Equal(streamed.Value!.Id, final.Value!.Id);
        Assert.True(final.Value!.Complete);
        Assert.Equal("Hello", final.Value!.Body);

        var listed = await new ListChatPartsHandler(factory).HandleAsync(
            new ListChatPartsCommand(new ListChatPartsRequest(sessionId, Guid.Empty)), ct);
        Assert.True(listed.Success, listed.Error);
        Assert.Single(listed.Value!);
        Assert.Equal("Hello", listed.Value![0].Body);
    }

    [Fact]
    public async Task AppendPart_FinalToolSnapshotUpdatesStructuredFieldsAndIgnoresLateStream()
    {
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;
        var append = new AppendChatPartHandler(factory, new ChatEventSignal());

        var streamed = await append.HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(
                sessionId, deviceId, "assistant", "tool", "read_file: loading", "m1:tool:0")), ct);
        Assert.True(streamed.Success, streamed.Error);
        Assert.False(streamed.Value!.Complete);
        Assert.Null(streamed.Value.Tool);

        var final = await append.HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(
                sessionId,
                deviceId,
                "assistant",
                "tool",
                "read_file: README content",
                "m1:tool:0",
                Tool: "read_file",
                ToolStatus: "completed",
                ToolInput: "{\\"path\\":\\"README.md\\"}",
                ToolResult: "README content",
                DurationMs: 24,
                Complete: true)), ct);
        Assert.True(final.Success, final.Error);
        Assert.Equal(streamed.Value.Id, final.Value!.Id);
        Assert.True(final.Value.Complete);
        Assert.Equal("read_file", final.Value.Tool);
        Assert.Equal("completed", final.Value.ToolStatus);
        Assert.Equal("{\\"path\\":\\"README.md\\"}", final.Value.ToolInput);
        Assert.Equal("README content", final.Value.ToolResult);
        Assert.Equal(24, final.Value.DurationMs);

        var late = await append.HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(
                sessionId, deviceId, "assistant", "tool", "read_file: stale snapshot", "m1:tool:0")), ct);
        Assert.True(late.Success, late.Error);
        Assert.Equal(final.Value.Id, late.Value!.Id);
        Assert.True(late.Value.Complete);
        Assert.Equal("read_file", late.Value.Tool);
        Assert.Equal("completed", late.Value.ToolStatus);
        Assert.Equal("{\\"path\\":\\"README.md\\"}", late.Value.ToolInput);
        Assert.Equal("README content", late.Value.ToolResult);
        Assert.Equal(24, late.Value.DurationMs);

        var listed = await new ListChatPartsHandler(factory).HandleAsync(
            new ListChatPartsCommand(new ListChatPartsRequest(sessionId, Guid.Empty)), ct);
        Assert.True(listed.Success, listed.Error);
        var part = Assert.Single(listed.Value!);
        Assert.Equal(final.Value.Id, part.Id);
        Assert.Equal("read_file", part.Tool);
        Assert.Equal("README content", part.ToolResult);
        Assert.True(part.Complete);
    }

    [Fact]
    public async Task AppendPart_LateStreamedSnapshotDoesNotClobberFinalPart()
    {
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;
        var append = new AppendChatPartHandler(factory, new ChatEventSignal());

        var final = await append.HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "Hello", "m1:text:0", Complete: true)), ct);
        Assert.True(final.Success, final.Error);

        var late = await append.HandleAsync(
            new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "Hell", "m1:text:0")), ct);
        Assert.True(late.Success, late.Error);
        Assert.Equal(final.Value!.Id, late.Value!.Id);
        Assert.True(late.Value!.Complete);
        Assert.Equal("Hello", late.Value!.Body);

        var listed = await new ListChatPartsHandler(factory).HandleAsync(
            new ListChatPartsCommand(new ListChatPartsRequest(sessionId, Guid.Empty)), ct);
        Assert.True(listed.Success, listed.Error);
        Assert.Single(listed.Value!);
        Assert.Equal("Hello", listed.Value![0].Body);
    }

    [Fact]
    public async Task ReportUsage_StoresTotalsAndContextLimit()
    {
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);
        var ct = TestContext.Current.CancellationToken;

        var reported = await new ReportChatUsageHandler(factory, new ChatEventSignal()).HandleAsync(
            new ReportChatUsageCommand(new ReportChatUsageRequest(sessionId, deviceId, 1200, 350, 4, 0.0123, 262144, 12.5)), ct);
        Assert.True(reported.Success, reported.Error);
        Assert.Equal(1200, reported.Value!.PromptTokens);
        Assert.Equal(350, reported.Value!.CompletionTokens);
        Assert.Equal(4, reported.Value!.AssistantMessages);
        Assert.Equal(0.0123, reported.Value!.TotalCost);
        Assert.Equal(262144, reported.Value!.ContextLimit);
        Assert.Equal(12.5, reported.Value!.GenerationSeconds);

        var fetched = await new GetChatSessionHandler(factory).HandleAsync(
            new GetChatSessionCommand(new GetChatSessionRequest(sessionId, Guid.Empty)), ct);
        Assert.True(fetched.Success, fetched.Error);
        Assert.Equal(1200, fetched.Value!.PromptTokens);
        Assert.Equal(262144, fetched.Value!.ContextLimit);
        Assert.Equal(12.5, fetched.Value!.GenerationSeconds);
    }

    [Fact]
    public async Task ReportUsage_WrongDevice_Fails()
    {
        var (projectId, _, _, deviceId, sessionId) = await SeedSessionAsync();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = HandlerSqlite.Factory(_connection, tenant);

        var result = await new ReportChatUsageHandler(factory, new ChatEventSignal()).HandleAsync(
            new ReportChatUsageCommand(new ReportChatUsageRequest(sessionId, Guid.NewGuid(), 1, 1, 1, 0, null)), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
    }
}
