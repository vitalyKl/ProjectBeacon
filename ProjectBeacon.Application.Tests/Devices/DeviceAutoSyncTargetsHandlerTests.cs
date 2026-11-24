namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Devices;
using Application.Projects;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class DeviceAutoSyncTargetsHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public DeviceAutoSyncTargetsHandlerTests()
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

    [Fact]
    public async Task ReturnsOnlyEnabledRootsBoundToTheAuthenticatedDevice()
    {
        Guid firstDeviceId;
        Guid secondDeviceId;
        Guid enabledProjectId;
        const string firstRoot = "/workspace/device-one";
        const string secondRoot = "/workspace/device-two";

        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("autosync", "autosync@beacon.local", "hash");
            var org = Org.Create("AutoSync");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var enabledProject = Project.Create("Enabled", null, org.Id);
            enabledProject.SetGitPolicyJson(ProjectGitPolicyCatalog.Serialize(
                0, [], 200, 20_000, "Off", autoSyncEnabled: true));
            var disabledProject = Project.Create("Disabled", null, org.Id);
            disabledProject.SetGitPolicyJson(ProjectGitPolicyCatalog.Serialize(
                0, [], 200, 20_000, "Off", autoSyncEnabled: false));
            _db.Projects.AddRange(enabledProject, disabledProject);
            _db.ProjectMembers.AddRange(
                ProjectMember.Create(enabledProject.Id, user.Id, MemberRole.Owner),
                ProjectMember.Create(disabledProject.Id, user.Id, MemberRole.Owner));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            enabledProjectId = enabledProject.Id;

            var firstDevice = await new CreateDeviceHandler(Factory()).HandleAsync(
                new CreateDeviceCommand(new CreateDeviceRequest("one", "fp-one", user.Id)),
                TestContext.Current.CancellationToken);
            var secondDevice = await new CreateDeviceHandler(Factory()).HandleAsync(
                new CreateDeviceCommand(new CreateDeviceRequest("two", "fp-two", user.Id)),
                TestContext.Current.CancellationToken);
            Assert.True(firstDevice.Success, firstDevice.Error);
            Assert.True(secondDevice.Success, secondDevice.Error);
            firstDeviceId = firstDevice.Value!.Id;
            secondDeviceId = secondDevice.Value!.Id;

            _db.ProjectRuntimes.AddRange(
                ProjectRuntime.Create(enabledProject.Id, firstDeviceId, firstRoot),
                ProjectRuntime.Create(disabledProject.Id, firstDeviceId, "/workspace/disabled"),
                ProjectRuntime.Create(enabledProject.Id, secondDeviceId, secondRoot));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (TenantScope.EnterUnscoped())
        {
            var handler = new GetDeviceAutoSyncTargetsHandler(Factory());
            await using var db = Factory().CreateDbContext();

            var first = await handler.HandleAsync(firstDeviceId, TestContext.Current.CancellationToken);
            Assert.True(first.Success, first.Error);
            var firstTarget = Assert.Single(first.Value!);
            Assert.Equal(enabledProjectId, firstTarget.ProjectId);
            Assert.Equal(firstRoot, firstTarget.LocalRoot);
            Assert.True(firstTarget.PolicyRevision >= 1);

            var second = await handler.HandleAsync(secondDeviceId, TestContext.Current.CancellationToken);
            Assert.True(second.Success, second.Error);
            var secondTarget = Assert.Single(second.Value!);
            Assert.Equal(enabledProjectId, secondTarget.ProjectId);
            Assert.Equal(secondRoot, secondTarget.LocalRoot);
        }
    }

    [Fact]
    public async Task RejectsRevokedDevice()
    {
        Guid deviceId;
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("revoked", "revoked@beacon.local", "hash");
            var org = Org.Create("Revoked");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var created = await new CreateDeviceHandler(Factory()).HandleAsync(
                new CreateDeviceCommand(new CreateDeviceRequest("revoked-device", "fp-revoked", user.Id)),
                TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Error);
            deviceId = created.Value!.Id;

            var revoked = await new RevokeDeviceHandler(Factory()).HandleAsync(
                new RevokeDeviceCommand(new RevokeDeviceRequest(deviceId, user.Id)),
                TestContext.Current.CancellationToken);
            Assert.True(revoked.Success, revoked.Error);
        }

        using (TenantScope.EnterUnscoped())
        {
            var result = await new GetDeviceAutoSyncTargetsHandler(Factory())
                .HandleAsync(deviceId, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
        }
    }
}
