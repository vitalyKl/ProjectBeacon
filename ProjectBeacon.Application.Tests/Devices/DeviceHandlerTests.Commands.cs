namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Common;
using Application.Authorization;
using Application.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed partial class DeviceHandlerTests
{
    [Fact]
    public async Task ForeignUser_CannotSeeCommand()
    {
        var owner = await SeedUserAsync("owner");
        var other = await SeedUserAsync("other");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", owner.Id)), TestContext.Current.CancellationToken);
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value!.Id, "{}", "{}")), TestContext.Current.CancellationToken);
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(created.Value.Id, owner.Id, WorkstationCommandKind.Probe, null, null)), TestContext.Current.CancellationToken);
        var got = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(queued.Value!.Id, other.Id)), TestContext.Current.CancellationToken);
        Assert.False(got.Success);
    }

    [Fact]
    public async Task CommandQueue_DoesNotExposeLocalRoot_ToNonOwner()
    {
        var (owner, member, project) = await SeedOwnerAndMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), owner.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, owner.Id, @"A:\secret\repo")), TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);
        Assert.Equal(@"A:\secret\repo", attached.Value!.LocalRoot);
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, owner.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"src","text":"hi"}""", project.Id)), TestContext.Current.CancellationToken);
        Assert.True(queued.Success, queued.Error);
        Assert.Equal(@"A:\secret\repo", queued.Value!.LocalRoot);
        var ownerRead = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(queued.Value.Id, owner.Id)), TestContext.Current.CancellationToken);
        Assert.True(ownerRead.Success, ownerRead.Error);
        Assert.Equal(@"A:\secret\repo", ownerRead.Value!.LocalRoot);
        var memberRead = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(queued.Value.Id, member.Id)), TestContext.Current.CancellationToken);
        Assert.False(memberRead.Success);
        Assert.Null(memberRead.Value);
        var memberList = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, member.Id, 20)), TestContext.Current.CancellationToken);
        Assert.False(memberList.Success);
        var ownerList = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, owner.Id, 20)), TestContext.Current.CancellationToken);
        Assert.True(ownerList.Success, ownerList.Error);
        var listedCmd = Assert.Single(ownerList.Value!, c => c.Id == queued.Value.Id);
        Assert.Equal(@"A:\secret\repo", listedCmd.LocalRoot);
    }

    [Fact]
    public async Task ProjectCommand_WithoutRuntime_Fails()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync();
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatEnsureSession, "{}", project.Id)), TestContext.Current.CancellationToken);
        Assert.False(queued.Success);
        Assert.Equal(CommandSandbox.RuntimeRequired, queued.Error);
    }

    [Fact]
    public async Task ProjectCommand_DoesNotFallBackToProjectsRoot()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync("""{"projectsRoot":"D:\\\\projects"}""");
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ApplyOpencode, """{"path":"."}""", project.Id)), TestContext.Current.CancellationToken);
        Assert.False(queued.Success);
        Assert.Equal(CommandSandbox.RuntimeRequired, queued.Error);
    }

    [Fact]
    public async Task ProjectCommand_RejectsAbsolutePath_AndClaimCarriesRuntimeRoot()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync();
        await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, user.Id, @"A:\work\app")), TestContext.Current.CancellationToken);

        var evalAbsolute = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.RunEvalTurn,
                """{"path":"C:\\\\secret","prompt":"x"}""", project.Id)), TestContext.Current.CancellationToken);
        Assert.False(evalAbsolute.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, evalAbsolute.Error);

        var absolute = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"C:\\\\secret","root":"C:\\\\secret","text":"hi"}""", project.Id)), TestContext.Current.CancellationToken);
        Assert.False(absolute.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, absolute.Error);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"src","root":"C:\\\\secret","text":"hi"}""", project.Id)), TestContext.Current.CancellationToken);
        Assert.True(queued.Success, queued.Error);
        Assert.Equal(@"A:\work\app", queued.Value!.LocalRoot);
        using var doc = JsonDocument.Parse(queued.Value.PayloadJson);
        Assert.Equal("src", doc.RootElement.GetProperty("path").GetString());
        Assert.False(doc.RootElement.TryGetProperty("root", out _));

        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        Assert.Equal(@"A:\work\app", claimed.Value!.LocalRoot);
    }

    [Fact]
    public async Task RuntimeDiagnostics_MemberWithoutPrivilege_Forbidden()
    {
        var (owner, member, project) = await SeedOwnerAndMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), owner.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, owner.Id, @"A:\secret\repo")), TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);
        var got = await new GetRuntimeDiagnosticsHandler(Factory()).HandleAsync(
            new GetRuntimeDiagnosticsCommand(new GetRuntimeDiagnosticsRequest(attached.Value!.Id, member.Id)), TestContext.Current.CancellationToken);
        Assert.False(got.Success);
        Assert.Equal(ErrorKind.Forbidden, got.Kind);
    }

    [Fact]
    public async Task RuntimeDiagnostics_ProjectOwner_Allowed()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), owner.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, owner.Id, @"A:\secret\repo")), TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);
        var got = await new GetRuntimeDiagnosticsHandler(Factory()).HandleAsync(
            new GetRuntimeDiagnosticsCommand(new GetRuntimeDiagnosticsRequest(attached.Value!.Id, owner.Id)), TestContext.Current.CancellationToken);
        Assert.True(got.Success, got.Error);
        Assert.Equal(@"A:\secret\repo", got.Value!.LocalRoot);
    }

    [Fact]
    public async Task RuntimeDiagnostics_DeviceOwnerMember_Allowed()
    {
        var (_, member, project) = await SeedOwnerAndMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("member-laptop", "fp-" + Guid.NewGuid().ToString("N"), member.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, member.Id, @"B:\member\repo")), TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);
        var got = await new GetRuntimeDiagnosticsHandler(Factory()).HandleAsync(
            new GetRuntimeDiagnosticsCommand(new GetRuntimeDiagnosticsRequest(attached.Value!.Id, member.Id)), TestContext.Current.CancellationToken);
        Assert.True(got.Success, got.Error);
        Assert.Equal(@"B:\member\repo", got.Value!.LocalRoot);
    }

    [Fact]
    public async Task RuntimeDiagnostics_NonMember_NotFound()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var outsider = await SeedUserAsync("outsider");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), owner.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, owner.Id, @"A:\secret\repo")), TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);
        var got = await new GetRuntimeDiagnosticsHandler(Factory()).HandleAsync(
            new GetRuntimeDiagnosticsCommand(new GetRuntimeDiagnosticsRequest(attached.Value!.Id, outsider.Id)), TestContext.Current.CancellationToken);
        Assert.False(got.Success);
        Assert.Equal("Runtime not found.", got.Error);
    }

    [Fact]
    public async Task ListDir_DoesNotInjectWorkstationRoot()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-list", user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", """{"projectsRoot":"D:\\\\projects"}""")), TestContext.Current.CancellationToken);
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(deviceId, user.Id, WorkstationCommandKind.ListDir, """{"path":""}""", null)), TestContext.Current.CancellationToken);
        Assert.True(queued.Success, queued.Error);
        using var doc = JsonDocument.Parse(queued.Value!.PayloadJson);
        Assert.False(doc.RootElement.TryGetProperty("root", out _));
        Assert.Null(queued.Value.LocalRoot);
    }

    [Fact]
    public async Task HarnessToken_IsProjectScopedShortLivedAndBoundToRunningChatCommand()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("native-test", "harness-" + Guid.NewGuid().ToString("N"), owner.Id)),
            TestContext.Current.CancellationToken);
        var deviceId = device.Value!.Id;
        var instanceId = "harness-instance-" + Guid.NewGuid().ToString("N");
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}", instanceId)),
            TestContext.Current.CancellationToken);

        var commandId = Guid.NewGuid();
        await using (var db = Factory().CreateDbContext())
        {
            var work = Domain.Entities.Devices.WorkstationCommand.Create(
                deviceId, WorkstationCommandKind.ChatPrompt, """{"chatSessionId":"11111111-1111-1111-1111-111111111111"}""", project.Id, owner.Id);
            work.Claim(TimeSpan.FromMinutes(10), instanceId);
            db.WorkstationCommands.Add(work);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = work.Id;
        }

        var actor = new Application.Authorization.ActorContext(
            ActorType.Device, owner.Id, false, null, deviceId, null, null, ApiTokenCapability.None);
        var handler = new IssueHarnessTokenHandler(Factory());
        var issued = await handler.HandleAsync(
            new IssueHarnessTokenCommand(commandId, 1, actor), TestContext.Current.CancellationToken);

        Assert.True(issued.Success, issued.Error);
        Assert.Equal(project.Id, issued.Value!.ProjectId);
        Assert.Equal(commandId, issued.Value.CommandId);
        Assert.Equal(1, issued.Value.Generation);
        Assert.True(issued.Value.ExpiresAt > DateTime.UtcNow);
        Assert.True(issued.Value.ExpiresAt <= DateTime.UtcNow.AddMinutes(15));

        await using var verify = Factory().CreateDbContext();
        var stored = await verify.ApiTokens.IgnoreQueryFilters()
            .SingleAsync(t => t.CommandId == commandId, TestContext.Current.CancellationToken);
        Assert.Equal(ApiTokenClientKind.Harness, stored.ClientKind);
        Assert.Equal(deviceId, stored.DeviceId);
        Assert.Equal(project.Id, stored.ProjectId);
        Assert.Equal(ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite, stored.Capabilities);
        Assert.NotEqual(issued.Value.Token, stored.TokenHash);
        Assert.Equal(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(issued.Value.Token))), stored.TokenHash);
    }

    [Fact]
    public async Task HarnessToken_ForNativeEvalRequiresPersistedNativeRunScope()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("native-eval", "eval-" + Guid.NewGuid().ToString("N"), owner.Id)),
            TestContext.Current.CancellationToken);
        var deviceId = device.Value!.Id;
        var instanceId = "eval-instance-" + Guid.NewGuid().ToString("N");
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}", instanceId)),
            TestContext.Current.CancellationToken);

        var task = TaskItem.Create("Native eval task", project.Id);
        var run = EvalRun.Create(project.Id, task.Id, Guid.NewGuid().ToString("N"), EvalCondition.Native);
        run.Pin("beacon-local/test", 0.2, "low", "read", 120, "revision", "dotnet test", "native");
        var payload = JsonSerializer.Serialize(new
        {
            evalRunId = run.Id,
            taskId = task.Id,
            runtime = "native",
            prompt = "Use the workspace tools."
        });

        Guid commandId;
        using (TenantScope.EnterUnscoped())
        {
            await using var db = Factory().CreateDbContext();
            db.Tasks.Add(task);
            db.EvalRuns.Add(run);
            var work = Domain.Entities.Devices.WorkstationCommand.Create(
                deviceId, WorkstationCommandKind.RunEvalTurn, payload, project.Id, owner.Id);
            work.Claim(TimeSpan.FromMinutes(10), instanceId);
            db.WorkstationCommands.Add(work);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = work.Id;
        }

        var actor = new Application.Authorization.ActorContext(
            ActorType.Device, owner.Id, false, null, deviceId, null, null, ApiTokenCapability.None);
        var issued = await new IssueHarnessTokenHandler(Factory()).HandleAsync(
            new IssueHarnessTokenCommand(commandId, 1, actor), TestContext.Current.CancellationToken);

        Assert.True(issued.Success, issued.Error);
        Assert.Equal(project.Id, issued.Value!.ProjectId);
        Assert.Equal(commandId, issued.Value.CommandId);
        Assert.Null(issued.Value.AllowedMcpTools);
    }

    [Fact]
    public async Task HarnessToken_ForOpenCodeEvalRequiresMatchingPersistedRuntime()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("opencode-eval", "eval-" + Guid.NewGuid().ToString("N"), owner.Id)),
            TestContext.Current.CancellationToken);
        var deviceId = device.Value!.Id;
        var instanceId = "eval-instance-" + Guid.NewGuid().ToString("N");
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}", instanceId)),
            TestContext.Current.CancellationToken);

        var task = TaskItem.Create("OpenCode eval task", project.Id);
        var run = EvalRun.Create(project.Id, task.Id, Guid.NewGuid().ToString("N"), EvalCondition.OpenCode);
        run.Pin("opencode/test", 0.2, "low", "read", 120, "revision", "dotnet test", "opencode");
        var payload = JsonSerializer.Serialize(new
        {
            evalRunId = run.Id,
            taskId = task.Id,
            runtime = "opencode",
            prompt = "Inspect the workspace."
        });

        Guid commandId;
        using (TenantScope.EnterUnscoped())
        {
            await using var db = Factory().CreateDbContext();
            db.Tasks.Add(task);
            db.EvalRuns.Add(run);
            var work = Domain.Entities.Devices.WorkstationCommand.Create(
                deviceId, WorkstationCommandKind.RunEvalTurn, payload, project.Id, owner.Id);
            work.Claim(TimeSpan.FromMinutes(10), instanceId);
            db.WorkstationCommands.Add(work);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = work.Id;
        }

        var actor = new ActorContext(
            ActorType.Device, owner.Id, false, null, deviceId, null, null, ApiTokenCapability.None);
        var issued = await new IssueHarnessTokenHandler(Factory()).HandleAsync(
            new IssueHarnessTokenCommand(commandId, 1, actor), TestContext.Current.CancellationToken);

        Assert.True(issued.Success, issued.Error);
        Assert.Equal(project.Id, issued.Value!.ProjectId);
        Assert.Null(issued.Value.AllowedMcpTools);

        using var dbCheck = Factory().CreateDbContext();
        var stored = await dbCheck.ApiTokens.IgnoreQueryFilters()
            .SingleAsync(token => token.CommandId == commandId, TestContext.Current.CancellationToken);
        Assert.Equal(ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead, stored.Capabilities);
    }

    [Fact]
    public async Task HarnessToken_RejectsEvalRunThatIsNotPinnedToNativeRuntime()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("opencode-eval", "eval-" + Guid.NewGuid().ToString("N"), owner.Id)),
            TestContext.Current.CancellationToken);
        var deviceId = device.Value!.Id;
        var instanceId = "eval-instance-" + Guid.NewGuid().ToString("N");
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}", instanceId)),
            TestContext.Current.CancellationToken);

        var task = TaskItem.Create("OpenCode eval task", project.Id);
        var run = EvalRun.Create(project.Id, task.Id, Guid.NewGuid().ToString("N"), EvalCondition.OpenCode);
        run.Pin("opencode/test", 0.2, "low", "read", 120, "revision", "dotnet test", "opencode");
        var payload = JsonSerializer.Serialize(new
        {
            evalRunId = run.Id,
            taskId = task.Id,
            runtime = "native",
            prompt = "Try to use native tools."
        });

        Guid commandId;
        using (TenantScope.EnterUnscoped())
        {
            await using var db = Factory().CreateDbContext();
            db.Tasks.Add(task);
            db.EvalRuns.Add(run);
            var work = Domain.Entities.Devices.WorkstationCommand.Create(
                deviceId, WorkstationCommandKind.RunEvalTurn, payload, project.Id, owner.Id);
            work.Claim(TimeSpan.FromMinutes(10), instanceId);
            db.WorkstationCommands.Add(work);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = work.Id;
        }

        var actor = new Application.Authorization.ActorContext(
            ActorType.Device, owner.Id, false, null, deviceId, null, null, ApiTokenCapability.None);
        var issued = await new IssueHarnessTokenHandler(Factory()).HandleAsync(
            new IssueHarnessTokenCommand(commandId, 1, actor), TestContext.Current.CancellationToken);

        Assert.False(issued.Success);
        Assert.Equal(ErrorKind.Forbidden, issued.Kind);
    }

    [Fact]
    public async Task HarnessToken_ForActorPipeline_ReturnsToolsFromPersistedSubtaskScope()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("native-test", "harness-" + Guid.NewGuid().ToString("N"), owner.Id)),
            TestContext.Current.CancellationToken);
        var deviceId = device.Value!.Id;
        var instanceId = "harness-instance-" + Guid.NewGuid().ToString("N");
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}", instanceId)),
            TestContext.Current.CancellationToken);

        var task = TaskItem.Create("Native pipeline task", project.Id);
        var subtask = Subtask.Create(
            "Implement and verify the requested change",
            task.Id,
            project.Id,
            ["read_file", "write_file", "run_command"],
            ["src"]);
        subtask.Start();
        var session = PipelineSession.Create(
            task.Id, project.Id, PipelineRole.Actor, "Work only on the assigned subtask.", subtask.Id);
        session.Launch();
        var payload = JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            taskId = task.Id,
            subtaskId = subtask.Id,
            sessionId = session.Id,
            role = "Actor",
            prompt = session.PromptContext,
            model = "beacon-local/test-model"
        });

        Guid commandId;
        using (TenantScope.EnterUnscoped())
        {
            await using var db = Factory().CreateDbContext();
            db.Tasks.Add(task);
            db.Subtasks.Add(subtask);
            db.PipelineSessions.Add(session);
            var work = Domain.Entities.Devices.WorkstationCommand.Create(
                deviceId, WorkstationCommandKind.RunPipelineSession, payload, project.Id, owner.Id);
            work.Claim(TimeSpan.FromMinutes(10), instanceId);
            db.WorkstationCommands.Add(work);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = work.Id;
        }

        var actor = new Application.Authorization.ActorContext(
            ActorType.Device, owner.Id, false, null, deviceId, null, null, ApiTokenCapability.None);
        var issued = await new IssueHarnessTokenHandler(Factory()).HandleAsync(
            new IssueHarnessTokenCommand(commandId, 1, actor), TestContext.Current.CancellationToken);

        Assert.True(issued.Success, issued.Error);
        Assert.Equal(new[] { "read_file", "write_file", "run_command" }, issued.Value!.AllowedMcpTools);
    }

    [Fact]
    public async Task HarnessToken_RejectsPipelinePayloadThatDoesNotMatchPersistedSession()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("native-test", "harness-" + Guid.NewGuid().ToString("N"), owner.Id)),
            TestContext.Current.CancellationToken);
        var deviceId = device.Value!.Id;
        var instanceId = "harness-instance-" + Guid.NewGuid().ToString("N");
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}", instanceId)),
            TestContext.Current.CancellationToken);

        var task = TaskItem.Create("Native pipeline task", project.Id);
        var session = PipelineSession.Create(task.Id, project.Id, PipelineRole.Planner, "Plan the task.");
        session.Launch();
        var payload = JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            taskId = task.Id,
            sessionId = session.Id,
            subtaskId = Guid.NewGuid(),
            role = "Actor",
            prompt = session.PromptContext,
            model = "beacon-local/test-model"
        });

        Guid commandId;
        using (TenantScope.EnterUnscoped())
        {
            await using var db = Factory().CreateDbContext();
            db.Tasks.Add(task);
            db.PipelineSessions.Add(session);
            var work = Domain.Entities.Devices.WorkstationCommand.Create(
                deviceId, WorkstationCommandKind.RunPipelineSession, payload, project.Id, owner.Id);
            work.Claim(TimeSpan.FromMinutes(10), instanceId);
            db.WorkstationCommands.Add(work);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = work.Id;
        }

        var actor = new Application.Authorization.ActorContext(
            ActorType.Device, owner.Id, false, null, deviceId, null, null, ApiTokenCapability.None);
        var issued = await new IssueHarnessTokenHandler(Factory()).HandleAsync(
            new IssueHarnessTokenCommand(commandId, 1, actor), TestContext.Current.CancellationToken);

        Assert.False(issued.Success);
        Assert.Equal(ErrorKind.Forbidden, issued.Kind);
    }

    [Fact]
    public async Task HarnessToken_RejectsStaleGenerationAndNonDeviceActor()
    {
        var (owner, _, project) = await SeedOwnerAndMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("native-test", "harness-" + Guid.NewGuid().ToString("N"), owner.Id)),
            TestContext.Current.CancellationToken);
        var deviceId = device.Value!.Id;
        var instanceId = "harness-instance-" + Guid.NewGuid().ToString("N");
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}", instanceId)),
            TestContext.Current.CancellationToken);

        Guid commandId;
        await using (var db = Factory().CreateDbContext())
        {
            var work = Domain.Entities.Devices.WorkstationCommand.Create(deviceId, WorkstationCommandKind.ChatPrompt, "{}", project.Id, owner.Id);
            work.Claim(TimeSpan.FromMinutes(10), instanceId);
            db.WorkstationCommands.Add(work);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = work.Id;
        }

        var handler = new IssueHarnessTokenHandler(Factory());
        var deviceActor = new Application.Authorization.ActorContext(
            ActorType.Device, owner.Id, false, null, deviceId, null, null, ApiTokenCapability.None);
        var stale = await handler.HandleAsync(new IssueHarnessTokenCommand(commandId, 2, deviceActor), TestContext.Current.CancellationToken);
        Assert.False(stale.Success);

        var humanActor = new Application.Authorization.ActorContext(
            ActorType.Human, owner.Id, false, null, null, project.Id, null, ApiTokenCapability.None);
        var human = await handler.HandleAsync(new IssueHarnessTokenCommand(commandId, 1, humanActor), TestContext.Current.CancellationToken);
        Assert.False(human.Success);

        await using var verify = Factory().CreateDbContext();
        Assert.Empty(await verify.ApiTokens.IgnoreQueryFilters()
            .Where(t => t.CommandId == commandId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

}
