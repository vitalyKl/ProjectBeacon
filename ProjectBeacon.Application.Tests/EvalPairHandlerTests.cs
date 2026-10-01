namespace ProjectBeacon.Application.Tests;

using Application.Context;
using Application.Devices;
using Application.Evals;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

public sealed class EvalPairHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public EvalPairHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _unscoped.Dispose();
    }

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    private EvalPairHandler CreateHandler() =>
        new(Factory(), new CompileBriefHandler(Factory()), new RecordEvalRunHandler(Factory()), new EnqueueCommandHandler(Factory()));

    private async Task<(User User, Project Project, TaskItem Task, DaemonDevice Device)> SeedAsync()
    {
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("dev", "dev@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();

            var project = Project.Create("BriefProject", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));

            var task = TaskItem.Create("Eval task", project.Id);
            _db.Tasks.Add(task);

            var device = DaemonDevice.Create("eval", user.Id, "fp", "hash", "bcd_eval");
            _db.DaemonDevices.Add(device);

            await _db.SaveChangesAsync();
            return (user, project, task, device);
        }
    }

    private async Task MakeOnlineAsync(DaemonDevice device, Project project, User user)
    {
        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(device.Id, "{}", "{}")));
        Assert.True(beat.Success, beat.Error);

        var attach = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, device.Id, user.Id, @"A:\work\eval")));
        Assert.True(attach.Success, attach.Error);
    }

    [Fact]
    public async Task Pair_CreatesTwoRunsAndTwoCommands()
    {
        var (user, project, task, device) = await SeedAsync();
        await MakeOnlineAsync(device, project, user);

        var handler = CreateHandler();
        var result = await handler.HandleAsync(new EvalPairCommand(Pinned(project.Id, task.Id, device.Id, user.Id, "Do the work")));

        Assert.True(result.Success, result.Error);
        var value = result.Value!;
        Assert.Equal(32, value.PairId.Length);
        Assert.NotEqual(value.WithBriefRunId, value.WithoutBriefRunId);
        Assert.NotEqual(value.WithBriefCommandId, value.WithoutBriefCommandId);

        var list = await new ListEvalRunsHandler(Factory()).HandleAsync(new ListEvalRunsRequest(project.Id, task.Id));
        Assert.True(list.Success, list.Error);
        Assert.Equal(2, list.Value!.Count);

        var withBriefRun = list.Value!.FirstOrDefault(r => r.Id == value.WithBriefRunId);
        Assert.NotNull(withBriefRun);
        Assert.Equal(EvalCondition.WithBrief.ToString(), withBriefRun!.Condition);
        Assert.Equal(value.PairId, withBriefRun.PairId);

        var withoutBriefRun = list.Value!.FirstOrDefault(r => r.Id == value.WithoutBriefRunId);
        Assert.NotNull(withoutBriefRun);
        Assert.Equal(EvalCondition.WithoutBrief.ToString(), withoutBriefRun!.Condition);
        Assert.Equal(value.PairId, withoutBriefRun.PairId);

        var withCmd = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(value.WithBriefCommandId, user.Id)));
        Assert.True(withCmd.Success, withCmd.Error);
        Assert.Equal(WorkstationCommandKind.RunEvalTurn, withCmd.Value!.Kind);

        var withPayload = JsonNode.Parse(withCmd.Value!.PayloadJson)!.AsObject();
        Assert.Equal(value.WithBriefRunId.ToString("D"), withPayload["evalRunId"]!.GetValue<string>());
        Assert.False(withPayload.ContainsKey("root"));
        Assert.Equal(@"A:\work\eval", withCmd.Value.LocalRoot);
        var withPrompt = withPayload["prompt"]!.GetValue<string>();
        Assert.Contains("BriefProject", withPrompt);
        Assert.Contains("Do the work", withPrompt);

        var withoutCmd = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(value.WithoutBriefCommandId, user.Id)));
        Assert.True(withoutCmd.Success, withoutCmd.Error);

        var withoutPayload = JsonNode.Parse(withoutCmd.Value!.PayloadJson)!.AsObject();
        Assert.Equal(value.WithoutBriefRunId.ToString("D"), withoutPayload["evalRunId"]!.GetValue<string>());
        var withoutPrompt = withoutPayload["prompt"]!.GetValue<string>();
        Assert.Equal("Do the work", withoutPrompt);
        Assert.DoesNotContain("BriefProject", withoutPrompt);

        Assert.Equal(withPayload["controls"]!.ToJsonString(), withoutPayload["controls"]!.ToJsonString());
        var controls = withPayload["controls"]!.AsObject();
        Assert.Equal(180, controls["timeoutSeconds"]!.GetValue<int>());
        Assert.Equal("opencode/test", controls["model"]!.GetValue<string>());
        Assert.Equal("dotnet test", controls["checkCommand"]!.GetValue<string>());
        Assert.Equal(0.2, controls["temperature"]!.GetValue<double>());

        var stored = await _db.EvalRuns.SingleAsync(r => r.Id == value.WithBriefRunId);
        Assert.Equal("opencode/test", stored.Model);
        Assert.Equal(0.2, stored.Temperature);
        Assert.Equal("abc123", stored.RepoRevision);
        Assert.Equal("dotnet test", stored.CheckCommand);
    }

    [Fact]
    public async Task Pair_MissingControls_Fails()
    {
        var (user, project, task, device) = await SeedAsync();
        await MakeOnlineAsync(device, project, user);

        var result = await CreateHandler().HandleAsync(new EvalPairCommand(new EvalPairRequest(
            project.Id, task.Id, device.Id, user.Id, "Do the work")));

        Assert.False(result.Success);
        Assert.Contains("Eval controls", result.Error);
    }

    private static EvalPairRequest Pinned(Guid projectId, Guid taskId, Guid deviceId, Guid actorId, string prompt) =>
        new(projectId, taskId, deviceId, actorId, prompt,
            Model: "opencode/test",
            Temperature: 0.2,
            ReasoningEffort: "low",
            ToolPermissions: "read",
            TimeoutSeconds: 180,
            RepoRevision: "abc123",
            CheckCommand: "dotnet test");

    [Fact]
    public async Task Pair_OfflineDevice_Fails()
    {
        var (user, project, task, device) = await SeedAsync();

        var handler = CreateHandler();
        var result = await handler.HandleAsync(new EvalPairCommand(Pinned(project.Id, task.Id, device.Id, user.Id, "Do the work")));

        Assert.False(result.Success);
        Assert.Equal("Device is not connected.", result.Error);

        var list = await new ListEvalRunsHandler(Factory()).HandleAsync(new ListEvalRunsRequest(project.Id));
        Assert.True(list.Success);
        Assert.Empty(list.Value!);
    }

    [Fact]
    public async Task Pair_MissingTask_Fails()
    {
        var (user, project, _, device) = await SeedAsync();
        await MakeOnlineAsync(device, project, user);

        var handler = CreateHandler();
        var result = await handler.HandleAsync(new EvalPairCommand(Pinned(project.Id, Guid.NewGuid(), device.Id, user.Id, "Do the work")));

        Assert.False(result.Success);
        Assert.Equal("Task not found.", result.Error);
    }

    [Fact]
    public async Task Pair_NotProjectMember_Fails()
    {
        var (user, project, task, device) = await SeedAsync();
        await MakeOnlineAsync(device, project, user);

        var otherUser = User.Create("other", "other@beacon.local", "hash");
        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(otherUser);
            await _db.SaveChangesAsync();
        }

        var handler = CreateHandler();
        var result = await handler.HandleAsync(new EvalPairCommand(Pinned(project.Id, task.Id, device.Id, otherUser.Id, "Do the work")));

        Assert.False(result.Success);
    }
}
