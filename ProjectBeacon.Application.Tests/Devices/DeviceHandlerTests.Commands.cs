namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Common;
using Application.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
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
}
