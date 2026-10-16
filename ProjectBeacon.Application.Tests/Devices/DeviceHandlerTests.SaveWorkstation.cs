namespace ProjectBeacon.Application.Tests;

using System.Text.Json.Nodes;
using Application.Common;
using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class DeviceHandlerTests
{
    [Fact]
    public async Task SaveWorkstation_PayloadStampedWithWrittenRevision_AndJsonPaired()
    {
        var user = await SeedUserAsync("stamp");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);

        // An external bump lands revision 1 before this save, so the save must write 2.
        await DesiredState.BumpUserAsync(_db, user.Id, TestContext.Current.CancellationToken);
        using (TenantScope.EnterUnscoped())
        {
            var before = await _db.DaemonDevices.SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(1, before.DesiredRevision);
        }

        var saved = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"theme":"dark","revision":1}""", null)), TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.Error);
        var payload = JsonNode.Parse(saved.Value!.PayloadJson!).AsObject();
        Assert.Equal(2L, payload["revision"]!.GetValue<long>());
        Assert.Equal("dark", payload["theme"]!.GetValue<string>());

        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(2, device.DesiredRevision);
            Assert.Equal("""{"theme":"dark"}""", device.DesiredWorkstationJson);
            Assert.Equal(1, await _db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }

        // The same stale base again is a conflict and writes nothing: no revision bump,
        // no new JSON, no queued command.
        var stale = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"theme":"light","revision":1}""", null)), TestContext.Current.CancellationToken);
        Assert.False(stale.Success);
        Assert.Equal(ErrorKind.Conflict, stale.Kind);
        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(2, device.DesiredRevision);
            Assert.Equal("""{"theme":"dark"}""", device.DesiredWorkstationJson);
            Assert.Equal(1, await _db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SaveWorkstation_SequentialSaves_UniqueRevisions_LastJsonWins()
    {
        var user = await SeedUserAsync("seq");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);

        // Each save declares the revision it was based on; the second builds on the first.
        var first = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"n":"a","revision":0}""", null)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        var second = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"n":"b","revision":1}""", null)), TestContext.Current.CancellationToken);
        Assert.True(second.Success, second.Error);

        var revisions = new[]
        {
            JsonNode.Parse(first.Value!.PayloadJson!)["revision"]!.GetValue<long>(),
            JsonNode.Parse(second.Value!.PayloadJson!)["revision"]!.GetValue<long>()
        };
        Assert.Equal(2, revisions.Distinct().Count());
        Assert.Equal(2, revisions.Max());

        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(2, device.DesiredRevision);
            Assert.Equal("""{"n":"b"}""", device.DesiredWorkstationJson);
        }
    }

    [Fact]
    public async Task SaveWorkstation_FutureBaseRevision_Conflicts_WithoutWriting()
    {
        var user = await SeedUserAsync("future-base");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);

        // The device sits at revision 0; a save claiming a later base is stale either way.
        var saved = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"n":"stale","revision":5}""", null)), TestContext.Current.CancellationToken);
        Assert.False(saved.Success);
        Assert.Equal(ErrorKind.Conflict, saved.Kind);
        Assert.Contains("to 0", saved.Error);

        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(0, device.DesiredRevision);
            Assert.Equal(0, await _db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SaveWorkstation_MissingBaseRevision_FailsClosed()
    {
        var user = await SeedUserAsync("no-base");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);

        var saved = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"n":"a"}""", null)), TestContext.Current.CancellationToken);
        Assert.False(saved.Success);
        Assert.Equal(ErrorKind.Conflict, saved.Kind);
        Assert.Contains("revision", saved.Error);

        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(0, device.DesiredRevision);
            Assert.Equal(0, await _db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SaveWorkstation_InsertFailure_RollsBackJsonAndRevision_AllowsRetry()
    {
        var user = await SeedUserAsync("inject");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);

        using (TenantScope.EnterUnscoped())
        {
            var before = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(0, before.DesiredRevision);
            Assert.Equal("{}", before.DesiredWorkstationJson);

            // Fault injection: the DB itself refuses the command insert for marked payloads.
            await _db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER beacon_inject_fail BEFORE INSERT ON \"WorkstationCommands\" " +
                "WHEN NEW.\"PayloadJson\" LIKE '%injectFail%' " +
                "BEGIN SELECT RAISE(ABORT, 'injected insert failure'); END", TestContext.Current.CancellationToken);
        }
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new EnqueueCommandHandler(Factory()).HandleAsync(
                    new EnqueueCommandCommand(new EnqueueCommandRequest(
                        deviceId, user.Id, WorkstationCommandKind.SaveWorkstation,
                        """{"n":"a","revision":0,"injectFail":true}""", null)), TestContext.Current.CancellationToken));
        }
        finally
        {
            using (TenantScope.EnterUnscoped())
            {
                await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS beacon_inject_fail", TestContext.Current.CancellationToken);
            }
        }

        // The guarded UPDATE and the INSERT roll back together: JSON, revision, and the
        // queue are exactly as before the failed save.
        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(0, device.DesiredRevision);
            Assert.Equal("{}", device.DesiredWorkstationJson);
            Assert.Equal(0, await _db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }

        // The old base revision is still valid: the retry without the fault lands revision 1.
        var retry = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"n":"a","revision":0}""", null)), TestContext.Current.CancellationToken);
        Assert.True(retry.Success, retry.Error);
        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(1, device.DesiredRevision);
            Assert.Equal("""{"n":"a"}""", device.DesiredWorkstationJson);
            Assert.Equal(1, await _db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Bumps_ExpressionValueIncrements_TranslateAndApply()
    {
        var user = await SeedUserAsync("bumps");
        var deviceIds = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            var created = await new CreateDeviceHandler(Factory()).HandleAsync(
                new CreateDeviceCommand(new CreateDeviceRequest("d" + i, "fp-" + Guid.NewGuid().ToString("N"), user.Id)), TestContext.Current.CancellationToken);
            deviceIds.Add(created.Value!.Id);
        }
        using (TenantScope.EnterUnscoped())
        {
            var revoked = await _db.DaemonDevices.SingleAsync(d => d.Id == deviceIds[2], TestContext.Current.CancellationToken);
            revoked.Revoke();
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // BumpUserAsync: the two active devices each move +1; the revoked one is untouched.
        await DesiredState.BumpUserAsync(_db, user.Id, TestContext.Current.CancellationToken);
        using (TenantScope.EnterUnscoped())
        {
            var byId = await _db.DaemonDevices.AsNoTracking()
                .Where(d => d.Id == deviceIds[0] || d.Id == deviceIds[1] || d.Id == deviceIds[2])
                .ToDictionaryAsync(d => d.Id, TestContext.Current.CancellationToken);
            Assert.Equal(1, byId[deviceIds[0]].DesiredRevision);
            Assert.Equal(1, byId[deviceIds[1]].DesiredRevision);
            Assert.Equal(0, byId[deviceIds[2]].DesiredRevision);
        }

        // BumpProjectAsync / BumpUserProjectsAsync advance the runtime config revision.
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create("Org");
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            _db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, deviceIds[0], @"A:\work\app"));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await DesiredState.BumpProjectAsync(_db, project.Id, TestContext.Current.CancellationToken);
            var afterProjectBump = await _db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.ProjectId == project.Id, TestContext.Current.CancellationToken);
            Assert.Equal(1, afterProjectBump.ConfigRevision);
            await DesiredState.BumpUserProjectsAsync(_db, user.Id, TestContext.Current.CancellationToken);
            var afterUserBump = await _db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.ProjectId == project.Id, TestContext.Current.CancellationToken);
            Assert.Equal(2, afterUserBump.ConfigRevision);
        }
    }

    [Fact]
    public async Task DesiredStateCompletion_StaleOrClampedAck_NeverMovesBackwards()
    {
        var user = await SeedUserAsync("stale");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;

        // Device state: desired 2, applied 1 (revision 1 already acknowledged).
        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            device.SetDesiredWorkstation("""{"n":"a"}""");
            device.BumpDesired();
            device.MarkApplied(1);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (TenantScope.EnterUnscoped())
        {
            var stale = WorkstationCommand.Create(deviceId, WorkstationCommandKind.SaveWorkstation, """{"revision":1}""", null, user.Id);
            var beyond = WorkstationCommand.Create(deviceId, WorkstationCommandKind.SaveWorkstation, """{"revision":5}""", null, user.Id);
            _db.WorkstationCommands.Add(stale);
            _db.WorkstationCommands.Add(beyond);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.True((await new CompleteCommandHandler(Factory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(stale.Id, deviceId, true, null, null)), TestContext.Current.CancellationToken)).Success);
            Assert.True((await new CompleteCommandHandler(Factory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(beyond.Id, deviceId, true, null, null)), TestContext.Current.CancellationToken)).Success);
        }

        // Neither the stale ack (1 <= applied) nor the ack beyond the live desired (5 > 2) moved it.
        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(1, device.AppliedRevision);
        }

        // Runtime state: config 2, applied-config 1.
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create("Org");
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var runtime = ProjectRuntime.Create(project.Id, deviceId, @"A:\work\app");
            runtime.BumpConfig();
            runtime.BumpConfig();
            runtime.MarkConfigApplied(1);
            _db.ProjectRuntimes.Add(runtime);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var stale = WorkstationCommand.Create(deviceId, WorkstationCommandKind.ApplyOpencode, """{"revision":1}""", project.Id, user.Id);
            var forward = WorkstationCommand.Create(deviceId, WorkstationCommandKind.ApplyOpencode, """{"revision":2}""", project.Id, user.Id);
            var beyond = WorkstationCommand.Create(deviceId, WorkstationCommandKind.ApplyOpencode, """{"revision":3}""", project.Id, user.Id);
            _db.WorkstationCommands.Add(stale);
            _db.WorkstationCommands.Add(forward);
            _db.WorkstationCommands.Add(beyond);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.True((await new CompleteCommandHandler(Factory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(stale.Id, deviceId, true, null, null)), TestContext.Current.CancellationToken)).Success);
            Assert.True((await new CompleteCommandHandler(Factory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(forward.Id, deviceId, true, null, null)), TestContext.Current.CancellationToken)).Success);
            Assert.True((await new CompleteCommandHandler(Factory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(beyond.Id, deviceId, true, null, null)), TestContext.Current.CancellationToken)).Success);
        }

        // The in-range ack (2) landed; the stale (1) and beyond (3 > config 2) acks did not touch it.
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.DeviceId == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(2, runtime.AppliedConfigRevision);
        }
    }
}
