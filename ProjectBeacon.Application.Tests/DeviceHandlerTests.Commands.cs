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
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", owner.Id)));
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value!.Id, "{}", "{}")));
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(created.Value.Id, owner.Id, WorkstationCommandKind.Probe, null, null)));
        var got = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(queued.Value!.Id, other.Id)));
        Assert.False(got.Success);
    }

    [Fact]
    public async Task CommandQueue_DoesNotExposeLocalRoot_ToNonOwner()
    {
        var (owner, member, project) = await SeedOwnerAndMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), owner.Id)));
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, owner.Id, @"A:\secret\repo")));
        Assert.True(attached.Success, attached.Error);
        Assert.Equal(@"A:\secret\repo", attached.Value!.LocalRoot);
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, owner.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"src","text":"hi"}""", project.Id)));
        Assert.True(queued.Success, queued.Error);
        Assert.Equal(@"A:\secret\repo", queued.Value!.LocalRoot);
        var ownerRead = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(queued.Value.Id, owner.Id)));
        Assert.True(ownerRead.Success, ownerRead.Error);
        Assert.Equal(@"A:\secret\repo", ownerRead.Value!.LocalRoot);
        var memberRead = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(queued.Value.Id, member.Id)));
        Assert.False(memberRead.Success);
        Assert.Null(memberRead.Value);
        var memberList = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, member.Id, 20)));
        Assert.False(memberList.Success);
        var ownerList = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, owner.Id, 20)));
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
                deviceId, user.Id, WorkstationCommandKind.ChatEnsureSession, "{}", project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(CommandSandbox.RuntimeRequired, queued.Error);
    }

    [Fact]
    public async Task ProjectCommand_DoesNotFallBackToProjectsRoot()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync("""{"projectsRoot":"D:\\\\projects"}""");
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ApplyOpencode, """{"path":"."}""", project.Id)));
        Assert.False(queued.Success);
        Assert.Equal(CommandSandbox.RuntimeRequired, queued.Error);
    }

    [Fact]
    public async Task ProjectCommand_RejectsAbsolutePath_AndClaimCarriesRuntimeRoot()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync();
        await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, user.Id, @"A:\work\app")));

        var evalAbsolute = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.RunEvalTurn,
                """{"path":"C:\\\\secret","prompt":"x"}""", project.Id)));
        Assert.False(evalAbsolute.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, evalAbsolute.Error);

        var absolute = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"C:\\\\secret","root":"C:\\\\secret","text":"hi"}""", project.Id)));
        Assert.False(absolute.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, absolute.Error);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.ChatPrompt,
                """{"path":"src","root":"C:\\\\secret","text":"hi"}""", project.Id)));
        Assert.True(queued.Success, queued.Error);
        Assert.Equal(@"A:\work\app", queued.Value!.LocalRoot);
        using var doc = JsonDocument.Parse(queued.Value.PayloadJson);
        Assert.Equal("src", doc.RootElement.GetProperty("path").GetString());
        Assert.False(doc.RootElement.TryGetProperty("root", out _));

        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)));
        Assert.Equal(@"A:\work\app", claimed.Value!.LocalRoot);
    }

    [Fact]
    public async Task ListDir_DoesNotInjectWorkstationRoot()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-list", user.Id)));
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", """{"projectsRoot":"D:\\\\projects"}""")));
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(deviceId, user.Id, WorkstationCommandKind.ListDir, """{"path":""}""", null)));
        Assert.True(queued.Success, queued.Error);
        using var doc = JsonDocument.Parse(queued.Value!.PayloadJson);
        Assert.False(doc.RootElement.TryGetProperty("root", out _));
        Assert.Null(queued.Value.LocalRoot);
    }
}
