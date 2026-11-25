namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Devices;
using Application.Projects;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Entities.Evals;
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
            await new HeartbeatDeviceHandler(Factory()).HandleAsync(
                new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(
                    firstDeviceId, """{"capabilities":["git-sync-v1","git-auto-sync-v1"]}""", "{}")),
                TestContext.Current.CancellationToken);
            await new HeartbeatDeviceHandler(Factory()).HandleAsync(
                new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(
                    secondDeviceId, """{"capabilities":["git-sync-v1","git-auto-sync-v1"]}""", "{}")),
                TestContext.Current.CancellationToken);

            _db.ProjectRuntimes.AddRange(
                ProjectRuntime.Create(enabledProject.Id, firstDeviceId, firstRoot),
                ProjectRuntime.Create(disabledProject.Id, firstDeviceId, "/workspace/disabled"),
                ProjectRuntime.Create(enabledProject.Id, secondDeviceId, secondRoot));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (TenantScope.EnterUnscoped())
        {
            var handler = new GetDeviceAutoSyncTargetsHandler(Factory());

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
    public async Task ReturnsBeaconManagedVerifiedLandingWhenAutoSyncIsDisabled()
    {
        Guid deviceId;
        Guid projectId;
        Guid taskId;
        Guid reviewRunId;
        string sourceSha;
        const string localRoot = "/workspace/beacon-managed-landing";

        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("beacon-landing", "beacon-landing@beacon.local", "hash");
            var org = Org.Create("Beacon landing without AutoSync");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var project = Project.Create("AutoSync disabled", null, org.Id);
            project.SetGitPolicyJson(ProjectGitPolicyCatalog.Serialize(
                0, [], 200, 20_000, "Off", autoSyncEnabled: false));
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            projectId = project.Id;

            var created = await new CreateDeviceHandler(Factory()).HandleAsync(
                new CreateDeviceCommand(new CreateDeviceRequest("landing-device", "fp-landing-device", user.Id)),
                TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Error);
            deviceId = created.Value!.Id;
            var device = await _db.DaemonDevices.SingleAsync(
                item => item.Id == deviceId, TestContext.Current.CancellationToken);
            device.Heartbeat(
                """{"capabilities":["git-sync-v1","git-auto-sync-v1"]}""", "{}");
            _db.ProjectRuntimes.Add(ProjectRuntime.Create(projectId, deviceId, localRoot));

            var task = TaskItem.Create("Beacon-managed merge", projectId, description: "Reviewed task.");
            task.PinPipelineDevice(deviceId);
            task.StartPipeline();
            task.EnterExecuting();
            task.EnterReview();
            task.SetApproved();
            _db.Tasks.Add(task);
            taskId = task.Id;

            var review = ReviewRun.Start(
                projectId, taskId, ReviewerType.Agent, targetRunId: Guid.NewGuid());
            review.SetCheckCommandHash(new string('a', 64));
            sourceSha = new string('1', 40);
            review.CompleteCheck(
                "Review check passed.", "review-artifact",
                sourceSha, new string('2', 40), deviceId, "main");
            _db.ReviewRuns.Add(review);
            reviewRunId = review.Id;
            _db.ReviewVerdicts.Add(ReviewVerdict.Create(
                taskId, projectId, ReviewVerdictKind.Approve, "Approved."));

            var candidateTree = new string('4', 40);
            var landingAttempt = LandingAttempt.Start(
                projectId, taskId, reviewRunId, user.Id, deviceId,
                "main", sourceSha, new string('2', 40), new string('a', 64),
                "Beacon verified the exact GitHub merge.");
            landingAttempt.RecordPreview(
                "main", new string('3', 40), candidateTree, new string('5', 64), 0, null);
            landingAttempt.MarkExecutePending();
            landingAttempt.RecordLanded(
                new string('6', 40), candidateTree, 0, new string('7', 64));
            _db.LandingAttempts.Add(landingAttempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (TenantScope.EnterUnscoped())
        {
            var handler = new GetDeviceAutoSyncTargetsHandler(Factory());
            var result = await handler.HandleAsync(deviceId, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            var target = Assert.Single(result.Value!);
            Assert.Equal(projectId, target.ProjectId);
            Assert.Equal(localRoot, target.LocalRoot);
            Assert.False(target.AutoSyncEnabled);
            var landing = Assert.Single(target.RemoteLandings!);
            Assert.Equal(taskId, landing.TaskId);
            Assert.Equal(reviewRunId, landing.ReviewRunId);
            Assert.Equal(sourceSha, landing.SourceSha);
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
