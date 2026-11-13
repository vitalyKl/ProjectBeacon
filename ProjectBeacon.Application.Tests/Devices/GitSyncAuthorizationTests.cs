namespace ProjectBeacon.Application.Tests.Devices;

using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;
using Infrastructure.Data;
using ProjectBeacon.Application.Tests;
using System.Text.Json;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

public sealed class GitSyncAuthorizationTests
{

    [Fact]
    public async Task GitSyncPermissionCheck_MatchesProjectOwnerAndAdminPolicy()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncAuthOrg", null);
            var project = Project.Create("GitSyncAuthProject", null, org.Id);
            var owner = User.Create("git-owner", $"git-owner-{Guid.NewGuid():N}@beacon.local", "hash");
            var member = User.Create("git-member", $"git-member-{Guid.NewGuid():N}@beacon.local", "hash");
            var orgAdmin = User.Create("git-org-admin", $"git-org-admin-{Guid.NewGuid():N}@beacon.local", "hash");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.AddRange(owner, member, orgAdmin);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, member.Id, MemberRole.Member));
            db.OrgMembers.Add(ProjectBeacon.Domain.Entities.Identity.OrgMember.Create(
                org.Id, orgAdmin.Id, MemberRole.Admin));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var handler = new GetProjectGitSyncCommandHandler(
                HandlerSqlite.Factory(connection, tenant),
                new AuthorizationService());
            var ownerActor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var memberActor = new ActorContext(
                ActorType.Human, member.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var orgAdminActor = new ActorContext(
                ActorType.Human, orgAdmin.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);

            Assert.True(await handler.CanAdministerAsync(project.Id, ownerActor, TestContext.Current.CancellationToken));
            Assert.False(await handler.CanAdministerAsync(project.Id, memberActor, TestContext.Current.CancellationToken));
            Assert.True(await handler.CanAdministerAsync(project.Id, orgAdminActor, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_ReusesPendingCommandAndCanRestoreItAfterReload()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncQueueOrg", null);
            var project = Project.Create("GitSyncQueueProject", null, org.Id);
            var owner = User.Create("git-queue-owner", $"git-queue-owner-{Guid.NewGuid():N}@beacon.local", "hash");
            var device = DaemonDevice.Create(
                "git-queue-device",
                owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.Add(device);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, Path.GetTempPath()));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var factory = HandlerSqlite.Factory(connection, tenant);
            var authorization = new AuthorizationService();
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var enqueue = new EnqueueProjectGitSyncHandler(factory, authorization);
            var inspect = new GetProjectGitSyncCommandHandler(factory, authorization);

            var first = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken);

            // A newer, separately attached workstation must not receive a second project-level
            // GitSync while the first workstation still owns a pending command.
            await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
            var newerDevice = DaemonDevice.Create(
                "newer-git-queue-device",
                owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            newerDevice.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
            using (TenantScope.EnterUnscoped())
            {
                db.DaemonDevices.Add(newerDevice);
                db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, newerDevice.Id, Path.GetTempPath()));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var pendingForDifferentTarget = await inspect.GetPendingAsync(
                project.Id,
                actor,
                TestContext.Current.CancellationToken,
                targetDeviceId: newerDevice.Id);
            Assert.True(pendingForDifferentTarget.Success, pendingForDifferentTarget.Error);
            Assert.Null(pendingForDifferentTarget.Value);

            var rerouted = await enqueue.HandleAsync(
                project.Id,
                actor,
                GitSyncMode.PullFastForward,
                TestContext.Current.CancellationToken,
                targetDeviceId: newerDevice.Id);
            var second = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken);
            var restored = await inspect.GetPendingAsync(project.Id, actor, TestContext.Current.CancellationToken);

            Assert.True(first.Success, first.Error);
            Assert.True(rerouted.Success, rerouted.Error);
            Assert.True(second.Success, second.Error);
            Assert.True(restored.Success, restored.Error);
            Assert.NotNull(first.Value);
            Assert.NotNull(rerouted.Value);
            Assert.NotEqual(first.Value!.Id, rerouted.Value!.Id);
            Assert.Equal(newerDevice.Id, rerouted.Value.DeviceId);
            Assert.Equal(rerouted.Value.Id, second.Value!.Id);
            Assert.Equal(rerouted.Value.Id, restored.Value!.Id);
            using (TenantScope.EnterUnscoped())
            {
                var commands = await db.WorkstationCommands.IgnoreQueryFilters()
                    .Where(command => command.ProjectId == project.Id
                        && command.Kind == WorkstationCommandKind.GitSync)
                    .OrderBy(command => command.CreatedAt)
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, commands.Count);
                Assert.Equal(WorkstationCommandStatus.Cancelled, commands[0].Status);
                Assert.Equal(WorkstationCommandStatus.Pending, commands[1].Status);
            }
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_ReplacesPendingCommandWhenItsWorkstationIsNoLongerEligible()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncRerouteOrg", null);
            var project = Project.Create("GitSyncRerouteProject", null, org.Id);
            var owner = User.Create("git-reroute-owner", $"git-reroute-{Guid.NewGuid():N}@beacon.local", "hash");
            var oldDevice = DaemonDevice.Create(
                "old-git-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            oldDevice.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.Add(oldDevice);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, oldDevice.Id, Path.GetTempPath()));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var factory = HandlerSqlite.Factory(connection, tenant);
            var authorization = new AuthorizationService();
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var enqueue = new EnqueueProjectGitSyncHandler(factory, authorization);

            var first = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken);
            Assert.True(first.Success, first.Error);
            Assert.NotNull(first.Value);

            var replacement = DaemonDevice.Create(
                "replacement-git-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            replacement.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
            using (TenantScope.EnterUnscoped())
            {
                oldDevice.Revoke();
                db.DaemonDevices.Add(replacement);
                db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, replacement.Id, Path.GetTempPath()));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var pendingReader = new GetProjectGitSyncCommandHandler(factory, authorization);
            var restoredPending = await pendingReader.GetPendingAsync(
                project.Id, actor, TestContext.Current.CancellationToken);
            Assert.True(restoredPending.Success, restoredPending.Error);
            Assert.Null(restoredPending.Value);

            var second = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward,
                TestContext.Current.CancellationToken, replacement.Id);

            Assert.True(second.Success, second.Error);
            Assert.NotNull(second.Value);
            Assert.NotEqual(first.Value!.Id, second.Value!.Id);
            Assert.Equal(replacement.Id, second.Value.DeviceId);

            using (TenantScope.EnterUnscoped())
            {
                var oldCommand = await db.WorkstationCommands.IgnoreQueryFilters()
                    .SingleAsync(command => command.Id == first.Value.Id, TestContext.Current.CancellationToken);
                var newCommand = await db.WorkstationCommands.IgnoreQueryFilters()
                    .SingleAsync(command => command.Id == second.Value.Id, TestContext.Current.CancellationToken);
                Assert.Equal(WorkstationCommandStatus.Cancelled, oldCommand.Status);
                Assert.Equal(WorkstationCommandStatus.Pending, newCommand.Status);
            }
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_DoesNotReusePendingCommandWithMismatchedProjectPayload()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncPayloadOrg", null);
            var project = Project.Create("GitSyncPayloadProject", null, org.Id);
            var owner = User.Create("git-payload-owner", $"git-payload-{Guid.NewGuid():N}@beacon.local", "hash");
            var device = DaemonDevice.Create(
                "git-payload-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
            var malformed = WorkstationCommand.Create(
                device.Id,
                WorkstationCommandKind.GitSync,
                JsonSerializer.Serialize(new { projectId = Guid.NewGuid(), mode = "PullFastForward" }),
                project.Id,
                owner.Id);

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.Add(device);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, Path.GetTempPath()));
            db.WorkstationCommands.Add(malformed);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var enqueue = new EnqueueProjectGitSyncHandler(
                HandlerSqlite.Factory(connection, tenant), new AuthorizationService());

            var result = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward,
                TestContext.Current.CancellationToken, device.Id);

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.Value);
            Assert.NotEqual(malformed.Id, result.Value!.Id);
            using (TenantScope.EnterUnscoped())
            {
                var oldCommand = await db.WorkstationCommands.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(command => command.Id == malformed.Id, TestContext.Current.CancellationToken);
                var newCommand = await db.WorkstationCommands.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(command => command.Id == result.Value.Id, TestContext.Current.CancellationToken);
                Assert.Equal(WorkstationCommandStatus.Cancelled, oldCommand.Status);
                Assert.Equal(WorkstationCommandStatus.Pending, newCommand.Status);
                using var payload = JsonDocument.Parse(newCommand.PayloadJson);
                Assert.Equal(project.Id.ToString(), payload.RootElement.GetProperty("projectId").GetString());
            }
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_RunningStatusCannotSatisfyPullButRunningPullCanSatisfyStatus()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncRunningModeOrg", null);
            var project = Project.Create("GitSyncRunningModeProject", null, org.Id);
            var owner = User.Create("git-running-mode-owner", $"git-running-mode-{Guid.NewGuid():N}@beacon.local", "hash");
            var device = DaemonDevice.Create(
                "git-running-mode-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
            var runningStatus = WorkstationCommand.Create(
                device.Id,
                WorkstationCommandKind.GitSync,
                JsonSerializer.Serialize(new { projectId = project.Id, mode = "Status" }),
                project.Id,
                owner.Id);
            runningStatus.Claim(TimeSpan.FromMinutes(15), "status-running-instance");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.Add(device);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, Path.GetTempPath()));
            db.WorkstationCommands.Add(runningStatus);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var enqueue = new EnqueueProjectGitSyncHandler(
                HandlerSqlite.Factory(connection, tenant), new AuthorizationService());

            var pullWhileStatusRuns = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken, device.Id);
            Assert.False(pullWhileStatusRuns.Success);
            Assert.Equal(ErrorKind.Conflict, pullWhileStatusRuns.Kind);

            var statusAgain = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.Status, TestContext.Current.CancellationToken, device.Id);
            Assert.True(statusAgain.Success, statusAgain.Error);
            Assert.Equal(runningStatus.Id, statusAgain.Value!.Id);

            var sha = new string('a', 40);
            using (TenantScope.EnterUnscoped())
            {
                runningStatus.Succeed(JsonSerializer.Serialize(
                    new GitSyncResultDto(
                        "main", "origin/main", 0, 0, true, false, true, false,
                        sha, sha, GitSyncOutcome.UpToDate, null, null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var pull = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken, device.Id);
            Assert.True(pull.Success, pull.Error);
            Assert.NotNull(pull.Value);
            Assert.NotEqual(runningStatus.Id, pull.Value!.Id);

            using (TenantScope.EnterUnscoped())
            {
                var pullCommand = await db.WorkstationCommands.IgnoreQueryFilters()
                    .SingleAsync(command => command.Id == pull.Value.Id, TestContext.Current.CancellationToken);
                pullCommand.Claim(TimeSpan.FromMinutes(15), "pull-running-instance");
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var statusWhilePullRuns = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.Status, TestContext.Current.CancellationToken, device.Id);
            Assert.True(statusWhilePullRuns.Success, statusWhilePullRuns.Error);
            Assert.Equal(pull.Value.Id, statusWhilePullRuns.Value!.Id);

            using (TenantScope.EnterUnscoped())
            {
                var commands = await db.WorkstationCommands.IgnoreQueryFilters()
                    .Where(command => command.ProjectId == project.Id
                        && command.Kind == WorkstationCommandKind.GitSync)
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, commands.Count);
                Assert.Contains(commands, command => command.Id == runningStatus.Id
                    && command.Status == WorkstationCommandStatus.Succeeded);
                Assert.Contains(commands, command => command.Id == pull.Value.Id
                    && command.Status == WorkstationCommandStatus.Running);
            }
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_PullReplacesPendingStatusButStatusReusesPendingPull()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncModeOrg", null);
            var project = Project.Create("GitSyncModeProject", null, org.Id);
            var owner = User.Create("git-mode-owner", $"git-mode-{Guid.NewGuid():N}@beacon.local", "hash");
            var device = DaemonDevice.Create(
                "git-mode-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.Add(device);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, Path.GetTempPath()));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var enqueue = new EnqueueProjectGitSyncHandler(
                HandlerSqlite.Factory(connection, tenant), new AuthorizationService());

            var status = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.Status, TestContext.Current.CancellationToken, device.Id);
            Assert.True(status.Success, status.Error);
            Assert.NotNull(status.Value);

            var pull = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken, device.Id);
            Assert.True(pull.Success, pull.Error);
            Assert.NotNull(pull.Value);
            Assert.NotEqual(status.Value!.Id, pull.Value!.Id);

            // A later read-only Status request may monitor the already queued Pull, but may
            // not downgrade it or create a competing command.
            var readStatus = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.Status, TestContext.Current.CancellationToken, device.Id);
            Assert.True(readStatus.Success, readStatus.Error);
            Assert.Equal(pull.Value.Id, readStatus.Value!.Id);

            using (TenantScope.EnterUnscoped())
            {
                var commands = await db.WorkstationCommands.IgnoreQueryFilters()
                    .Where(command => command.ProjectId == project.Id
                        && command.Kind == WorkstationCommandKind.GitSync)
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, commands.Count);
                var cancelledStatus = commands.Single(command => command.Id == status.Value.Id);
                var queuedPull = commands.Single(command => command.Id == pull.Value.Id);
                Assert.Equal(WorkstationCommandStatus.Cancelled, cancelledStatus.Status);
                Assert.Equal(WorkstationCommandStatus.Pending, queuedPull.Status);
                using var payload = JsonDocument.Parse(queuedPull.PayloadJson);
                Assert.Equal("PullFastForward", payload.RootElement.GetProperty("mode").GetString());
            }
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_DefaultTargetPrefersNativePipelineWorkstationOverNewerExternalRuntime()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncNativePreferenceOrg", null);
            var project = Project.Create("GitSyncNativePreferenceProject", null, org.Id);
            var owner = User.Create("git-native-owner", $"git-native-{Guid.NewGuid():N}@beacon.local", "hash");
            var nativeDevice = DaemonDevice.Create(
                "native-git-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            nativeDevice.Heartbeat(
                """{"capabilities":["git-sync-v1"]}""",
                """{"agentRuntime":"native"}""");
            await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
            var externalDevice = DaemonDevice.Create(
                "external-git-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            externalDevice.Heartbeat(
                """{"capabilities":["git-sync-v1"]}""",
                """{"agentRuntime":"opencode"}""");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.AddRange(nativeDevice, externalDevice);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, nativeDevice.Id, Path.GetTempPath()));
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, externalDevice.Id, Path.GetTempPath()));
            var externalPending = WorkstationCommand.Create(
                externalDevice.Id,
                WorkstationCommandKind.GitSync,
                JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
                project.Id,
                owner.Id);
            db.WorkstationCommands.Add(externalPending);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var enqueue = new EnqueueProjectGitSyncHandler(
                HandlerSqlite.Factory(connection, tenant), new AuthorizationService());

            var result = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.Value);
            Assert.Equal(nativeDevice.Id, result.Value.DeviceId);

            using (TenantScope.EnterUnscoped())
            {
                var commands = await db.WorkstationCommands.IgnoreQueryFilters().AsNoTracking()
                    .Where(command => command.ProjectId == project.Id
                        && command.Kind == WorkstationCommandKind.GitSync)
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, commands.Count);
                Assert.Contains(commands, command => command.Id == externalPending.Id
                    && command.Status == WorkstationCommandStatus.Cancelled);
                Assert.Contains(commands, command => command.Id == result.Value.Id
                    && command.DeviceId == nativeDevice.Id
                    && command.Status == WorkstationCommandStatus.Pending);
            }
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_DoesNotReportRunningExternalSyncAsDefaultNativeSync()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncExternalRunningOrg", null);
            var project = Project.Create("GitSyncExternalRunningProject", null, org.Id);
            var owner = User.Create("git-external-owner", $"git-external-{Guid.NewGuid():N}@beacon.local", "hash");
            var nativeDevice = DaemonDevice.Create(
                "native-default-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            nativeDevice.Heartbeat(
                """{"capabilities":["git-sync-v1"]}""",
                """{"agentRuntime":"native"}""");
            var externalDevice = DaemonDevice.Create(
                "external-running-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            externalDevice.Heartbeat(
                """{"capabilities":["git-sync-v1"]}""",
                """{"agentRuntime":"opencode"}""");
            var running = WorkstationCommand.Create(
                externalDevice.Id,
                WorkstationCommandKind.GitSync,
                JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
                project.Id,
                owner.Id);
            running.Claim(TimeSpan.FromMinutes(15), "external-running-instance");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.AddRange(nativeDevice, externalDevice);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, nativeDevice.Id, Path.GetTempPath()));
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, externalDevice.Id, Path.GetTempPath()));
            db.WorkstationCommands.Add(running);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var enqueue = new EnqueueProjectGitSyncHandler(
                HandlerSqlite.Factory(connection, tenant), new AuthorizationService());

            var result = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Conflict, result.Kind);
            using (TenantScope.EnterUnscoped())
            {
                var persisted = await db.WorkstationCommands.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(command => command.Id == running.Id, TestContext.Current.CancellationToken);
                Assert.Equal(WorkstationCommandStatus.Running, persisted.Status);
            }
        }
    }

    [Fact]
    public async Task GitSyncEnqueue_DoesNotCreateAnotherCommandWhenOneIsRunningElsewhere()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        {
            var org = Org.Create("GitSyncRunningOrg", null);
            var project = Project.Create("GitSyncRunningProject", null, org.Id);
            var owner = User.Create("git-running-owner", $"git-running-{Guid.NewGuid():N}@beacon.local", "hash");
            var runningDevice = DaemonDevice.Create(
                "git-running-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            var pendingDevice = DaemonDevice.Create(
                "git-pending-device", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            runningDevice.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
            pendingDevice.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");

            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.Users.Add(owner);
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            db.DaemonDevices.AddRange(runningDevice, pendingDevice);
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, runningDevice.Id, Path.GetTempPath()));
            db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, pendingDevice.Id, Path.GetTempPath()));

            var running = WorkstationCommand.Create(
                runningDevice.Id,
                WorkstationCommandKind.GitSync,
                JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
                project.Id,
                owner.Id);
            running.Claim(TimeSpan.FromMinutes(15), "running-instance");
            var pending = WorkstationCommand.Create(
                pendingDevice.Id,
                WorkstationCommandKind.GitSync,
                JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
                project.Id,
                owner.Id);
            db.WorkstationCommands.AddRange(running, pending);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            unscoped.Dispose();

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var enqueue = new EnqueueProjectGitSyncHandler(
                HandlerSqlite.Factory(connection, tenant), new AuthorizationService());
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);

            var result = await enqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward,
                TestContext.Current.CancellationToken, pendingDevice.Id);

            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Conflict, result.Kind);
            using (TenantScope.EnterUnscoped())
            {
                var active = await db.WorkstationCommands.IgnoreQueryFilters()
                    .Where(command => command.ProjectId == project.Id
                        && command.Kind == WorkstationCommandKind.GitSync
                        && (command.Status == WorkstationCommandStatus.Pending
                            || command.Status == WorkstationCommandStatus.Running))
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, active.Count);
                Assert.Contains(active, command => command.Id == running.Id
                    && command.Status == WorkstationCommandStatus.Running);
                Assert.Contains(active, command => command.Id == pending.Id
                    && command.Status == WorkstationCommandStatus.Pending);
            }
        }
    }

    [Fact]
    public async Task EnqueueAcrossWorkstations_RetargetsSingleActiveProjectCommand()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "beacon-gitsync-retarget-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<BeaconDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False;Default Timeout=30")
                .Options;

            var org = Org.Create("GitSyncConcurrencyOrg", null);
            var project = Project.Create("GitSyncConcurrencyProject", null, org.Id);
            var owner = User.Create("git-concurrency-owner", $"git-concurrency-{Guid.NewGuid():N}@beacon.local", "hash");
            var firstDevice = DaemonDevice.Create(
                "git-concurrency-first", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            var secondDevice = DaemonDevice.Create(
                "git-concurrency-second", owner.Id,
                "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N"));
            firstDevice.Heartbeat(
                """{"capabilities":["git-sync-v1"]}""",
                """{"agentRuntime":"native"}""");
            secondDevice.Heartbeat(
                """{"capabilities":["git-sync-v1"]}""",
                """{"agentRuntime":"native"}""");

            await using (var seed = new BeaconDbContext(options))
            {
                seed.Database.EnsureCreated();
                using (TenantScope.EnterUnscoped())
                {
                    seed.Orgs.Add(org);
                    seed.Projects.Add(project);
                    seed.Users.Add(owner);
                    seed.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
                    seed.DaemonDevices.AddRange(firstDevice, secondDevice);
                    seed.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, firstDevice.Id, Path.GetTempPath()));
                    seed.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, secondDevice.Id, Path.GetTempPath()));
                    await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
                }
            }

            var tenant = new TenantContext();
            tenant.Assign(project.Id, org.Id, unscoped: false);
            var factory = new BeaconDbFactory(options, tenant);
            var actor = new ActorContext(
                ActorType.Human, owner.Id, false, null, null, project.Id, org.Id, ApiTokenCapability.None);
            var authorization = new AuthorizationService();
            var firstEnqueue = new EnqueueProjectGitSyncHandler(factory, authorization);
            var secondEnqueue = new EnqueueProjectGitSyncHandler(factory, authorization);

            // SQLite is not a faithful test of PostgreSQL row-lock serialization. This test
            // verifies the deterministic state transition; the parallel race is tested on PostgreSQL.
            var first = await firstEnqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward,
                TestContext.Current.CancellationToken, firstDevice.Id);
            var second = await secondEnqueue.HandleAsync(
                project.Id, actor, GitSyncMode.PullFastForward,
                TestContext.Current.CancellationToken, secondDevice.Id);

            Assert.True(first.Success, first.Error);
            Assert.True(second.Success, second.Error);
            Assert.NotNull(first.Value);
            Assert.NotNull(second.Value);
            Assert.NotEqual(first.Value!.Id, second.Value!.Id);

            await using var verify = factory.CreateDbContext();
            var activeCommands = await verify.WorkstationCommands.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(command => command.ProjectId == project.Id
                    && command.Kind == WorkstationCommandKind.GitSync
                    && (command.Status == WorkstationCommandStatus.Pending
                        || command.Status == WorkstationCommandStatus.Running))
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Single(activeCommands);
            Assert.Equal(second.Value.Id, activeCommands[0].Id);

            var allCommands = await verify.WorkstationCommands.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(command => command.ProjectId == project.Id
                    && command.Kind == WorkstationCommandKind.GitSync)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, allCommands.Count);
            Assert.Contains(allCommands, command => command.Id == first.Value.Id
                && command.Status == WorkstationCommandStatus.Cancelled);
            Assert.Contains(allCommands, command => command.Id == second.Value.Id
                && command.Status == WorkstationCommandStatus.Pending);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(databasePath + suffix); } catch { }
            }
        }
    }

    [Fact]
    public void RuntimeSummary_ReportsCapabilityOfTheAttachedDeviceWithoutExposingProbeJson()
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var device = DaemonDevice.Create("runtime-device", userId, "fingerprint", "token-hash", "bcd_test");
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        var runtime = ProjectRuntime.Create(projectId, device.Id, Path.GetTempPath());

        var summary = AttachRuntimeHandler.MapSummary(runtime, device, DateTime.UtcNow);

        Assert.True(summary.GitSyncSupported);
        Assert.True(summary.DeviceOnline);
        Assert.DoesNotContain("capabilities", JsonSerializer.Serialize(summary), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenericDeviceCommandEnqueue_RejectsGitSyncOutsideProjectAdminHandler()
    {
        // The guard must run before the database is touched. The generic device-command
        // endpoint is intentionally not an alternative path around project Owner/Admin
        // authorization enforced by EnqueueProjectGitSyncHandler.
        var handler = new EnqueueCommandHandler(dbFactory: null!);
        var projectId = Guid.NewGuid();

        var result = await handler.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkstationCommandKind.GitSync,
                """{"projectId":"00000000-0000-0000-0000-000000000001","mode":"Status"}""",
                projectId)));

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Contains("project GitSync endpoint", result.Error, StringComparison.Ordinal);
    }
}
