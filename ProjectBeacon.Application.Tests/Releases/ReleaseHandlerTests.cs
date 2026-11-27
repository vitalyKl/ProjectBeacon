namespace ProjectBeacon.Application.Tests.Releases;

using Application.Authorization;
using Application.Devices;
using Application.Common;
using Application.Persistence;
using Application.Projects;
using Application.Releases;
using Application.Tasks;
using Domain.Common;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

public sealed class ReleaseHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _ownerId;
    private readonly Guid _memberId;

    public ReleaseHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Release handlers org");
        _db.Orgs.Add(org);
        var project = Project.Create("Release handlers project", null, org.Id);
        project.SetVersionPolicyJson(ProjectVersionPolicyCatalog.Serialize(
            new ProjectVersionPolicy("NumericComponents", 4, null, "v{version}", false, [], true)));
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var owner = User.Create("release-owner", "release-owner@beacon.local", "hash");
        var member = User.Create("release-member", "release-member@beacon.local", "hash");
        _db.Users.AddRange(owner, member);
        _db.SaveChanges();
        _ownerId = owner.Id;
        _memberId = member.Id;
        _db.ProjectMembers.AddRange(
            ProjectMember.Create(_projectId, _ownerId, MemberRole.Owner),
            ProjectMember.Create(_projectId, _memberId, MemberRole.Member));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private ActorContext OwnerActor() =>
        new(ActorType.Human, _ownerId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private ActorContext MemberActor() =>
        new(ActorType.Human, _memberId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    private CreateReleaseHandler CreateHandler() =>
        new(Factory(), new AuthorizationService(), new EfAuditWriter());

    [Fact]
    public async Task OwnerCreatesRelease_MemberReads_AndMilestonesRemainAfterReleaseDeletion()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("0.7.5.9", "December delivery", "0.7.5.9", "release/0.7.5.9", 1),
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        Assert.Equal(ReleaseStatus.Planned, created.Value!.Status);
        Assert.Equal("0.7.5.9", created.Value.TargetVersion);

        var list = await new ListProjectReleasesHandler(Factory()).HandleAsync(
            new ListProjectReleasesCommand(new ListProjectReleasesRequest(_projectId), MemberActor()),
            TestContext.Current.CancellationToken);
        Assert.True(list.Success, list.Error);
        Assert.Single(list.Value!);

        var milestone = Milestone.Create("Prepare release notes", null, _projectId, 1);
        _db.Milestones.Add(milestone);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var assign = await new SetMilestoneReleaseHandler(Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new SetMilestoneReleaseCommand(
                new SetMilestoneReleaseRequest(milestone.Id, _projectId, created.Value.Id, milestone.Revision),
                OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(assign.Success, assign.Error);

        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(created.Value.Id, milestone.ReleaseId);

        var deleted = await new DeleteReleaseHandler(Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new DeleteReleaseCommand(
                new DeleteReleaseRequest(created.Value.Id, _projectId), OwnerActor()),
                TestContext.Current.CancellationToken);
        Assert.True(deleted.Success, deleted.Error);

        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Null(milestone.ReleaseId);
        Assert.True(await _db.Milestones.AnyAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken));
        Assert.False(await _db.Releases.AnyAsync(
            item => item.Id == created.Value.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseCreationRequiresFeatureFlagAndOwnerAdmin()
    {
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var denied = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Denied", null, null, "main", 1),
            outsider), TestContext.Current.CancellationToken);
        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);

        var invalidVersion = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Invalid", null, "0.7.5", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(invalidVersion.Success);
        Assert.Equal(ErrorKind.Validation, invalidVersion.Kind);
    }

    [Fact]
    public async Task ReleaseCreationIsDisabledWhenProjectFeatureFlagIsOff()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.True(ProjectVersionPolicyCatalog.TryRead(
            project.VersionPolicyJson, out var policy, out var error), error);
        project.SetVersionPolicyJson(
            ProjectVersionPolicyCatalog.Serialize(policy with { ReleaseManagementEnabled = false }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Disabled", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _db.Releases.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlannedReleaseCanBeEditedAndOptionalFieldsCanBeCleared()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Initial name", "Release notes", "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var updated = await new UpdateReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new UpdateReleaseCommand(
                new UpdateReleaseRequest(
                    created.Value!.Id, _projectId, "Updated name", "", "", "release/0.7.5.9", 2,
                    created.Value.Revision),
                OwnerActor()), TestContext.Current.CancellationToken);

        Assert.True(updated.Success, updated.Error);
        Assert.Equal("Updated name", updated.Value!.Name);
        Assert.Null(updated.Value.Description);
        Assert.Null(updated.Value.TargetVersion);
        Assert.Equal("release/0.7.5.9", updated.Value.TargetBranch);
        Assert.Equal(2, updated.Value.Order);
        Assert.Equal(created.Value.Revision + 1, updated.Value.Revision);
    }

    [Fact]
    public async Task ReleaseSnapshotComparisonIgnoresCaptureTimeButRejectsTaskChanges()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        var release = Release.Create("Snapshot", null, _projectId, "0.7.5.9", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        var milestone = Milestone.Create("Included work", null, _projectId, 1);
        milestone.AssignRelease(release.Id);
        var task = TaskItem.Create(
            "Original task scope", _projectId, TaskPriority.High, TaskType.Feature, "Original description");
        task.SetMilestone(milestone.Id);
        _db.Releases.Add(release);
        _db.Milestones.Add(milestone);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(ProjectVersionPolicyCatalog.TryRead(
            project.VersionPolicyJson, out var policy, out var policyError), policyError);
        var prepared = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(prepared.Error);
        Assert.NotNull(prepared.Json);

        var unchanged = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(unchanged.Error);
        Assert.True(ReleasePreparePayload.MatchesSnapshot(prepared.Json, unchanged.Json));

        task.Update(title: "Changed after Prepare", description: "Changed release scope");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var changed = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(changed.Error);
        Assert.False(ReleasePreparePayload.MatchesSnapshot(prepared.Json, changed.Json));
    }

    [Fact]
    public async Task PreparedReleaseFreezesItsMilestoneTaskScope()
    {
        var release = Release.Create("Frozen scope", null, _projectId, "0.7.5.9", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        var milestone = Milestone.Create("Included work", null, _projectId, 1);
        milestone.AssignRelease(release.Id);
        var label = Label.Create("Frozen label", "#52525b", _projectId);
        var task = TaskItem.Create("Included task", _projectId);
        task.SetMilestone(milestone.Id);
        task.AssignLabel(label.Id);
        _db.Releases.Add(release);
        _db.Milestones.Add(milestone);
        _db.Labels.Add(label);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var planned = await ReleaseTaskMutationGuard.GetBlockReasonAsync(
            _db, _projectId, [milestone.Id], TestContext.Current.CancellationToken);
        Assert.Null(planned);

        release.BeginPreparation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), """{"version":"0.7.5.9"}""");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prepared = await ReleaseTaskMutationGuard.GetBlockReasonAsync(
            _db, _projectId, [milestone.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(prepared);
        Assert.Contains("Frozen scope", prepared, StringComparison.Ordinal);

        var detach = await new SetMilestoneReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new SetMilestoneReleaseCommand(
                new SetMilestoneReleaseRequest(milestone.Id, _projectId, null, milestone.Revision),
                OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(detach.Success);
        Assert.Equal(ErrorKind.Conflict, detach.Kind);
        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(release.Id, milestone.ReleaseId);

        var renameProject = await new UpdateProjectHandler(
                Factory(), new AuthorizationService(), new ContentWriter())
            .HandleAsync(
                new UpdateProjectCommand(
                    new UpdateProjectRequest(_projectId, "Renamed after prepare", null),
                    OwnerActor()),
                TestContext.Current.CancellationToken);
        Assert.False(renameProject.Success);
        Assert.Equal(ErrorKind.Conflict, renameProject.Kind);

        var updateTask = await new UpdateTaskHandler(Factory(), new ContentWriter())
            .HandleAsync(
                new UpdateTaskCommand(new UpdateTaskRequest(
                    task.Id, "Changed after prepare", null, null, null, null, milestone.Id), OwnerActor()),
                TestContext.Current.CancellationToken);
        Assert.False(updateTask.Success);
        Assert.Equal(ErrorKind.Conflict, updateTask.Kind);

        var externalTask = TaskItem.Create("External dependency target", _projectId);
        _db.Tasks.Add(externalTask);
        _db.TaskDependencies.Add(TaskDependency.Create(task.Id, externalTask.Id));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleteDependencyTarget = await new DeleteTaskHandler(Factory(), new ContentWriter())
            .HandleAsync(
                new DeleteTaskCommand(new DeleteTaskRequest(externalTask.Id, _projectId), OwnerActor()),
                TestContext.Current.CancellationToken);
        Assert.False(deleteDependencyTarget.Success);
        Assert.Equal(ErrorKind.Conflict, deleteDependencyTarget.Kind);

        var addStep = await new AddTaskStepHandler(Factory())
            .HandleAsync(
                new AddTaskStepCommand(new AddTaskStepRequest(task.Id, "Blocked checklist step")),
                TestContext.Current.CancellationToken);
        Assert.False(addStep.Success);
        Assert.Equal(ErrorKind.Conflict, addStep.Kind);

        var deleteLabel = await new DeleteLabelHandler(Factory())
            .HandleAsync(
                new DeleteLabelRequest(_projectId, label.Id),
                TestContext.Current.CancellationToken);
        Assert.False(deleteLabel.Success);
        Assert.Equal(ErrorKind.Conflict, deleteLabel.Kind);

        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Included task", task.Title);
        Assert.Equal(label.Id, task.LabelId);
        Assert.Empty(await _db.TaskSteps.Where(step => step.TaskId == task.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManualCandidateTransitionIsRejected()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Candidate must be prepared", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var result = await new SetReleaseStatusHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new SetReleaseStatusCommand(
                new SetReleaseStatusRequest(
                    created.Value!.Id, _projectId, ReleaseStatus.Candidate, created.Value.Revision),
                OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Validation, result.Kind);
        Assert.Equal(ReleaseStatus.Planned, (await _db.Releases
            .SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task ReleaseManagementCannotBeDisabledWhileReleaseIsActive()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Active", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var policy = await _db.Projects.Where(project => project.Id == _projectId)
            .Select(project => new { project.VersionPolicyJson, project.VersionPolicyRevision })
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(ProjectVersionPolicyCatalog.TryRead(policy.VersionPolicyJson, out var parsed, out var parseError), parseError);

        var changed = await new SetProjectVersionPolicyHandler(
            Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new SetProjectVersionPolicyCommand(
                _projectId, parsed with { ReleaseManagementEnabled = false },
                null, policy.VersionPolicyRevision, OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(changed.Success);
        Assert.Equal(ErrorKind.Conflict, changed.Kind);
        var stillEnabled = await new GetProjectVersionPolicyHandler(Factory(), new AuthorizationService())
            .HandleAsync(_projectId, OwnerActor(), TestContext.Current.CancellationToken);
        Assert.True(stillEnabled.Success, stillEnabled.Error);
        Assert.True(stillEnabled.Value!.ReleaseManagementEnabled);
    }

    [Fact]
    public async Task PrepareReleaseRequiresProjectOwnerOrAdmin()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Prepare authorization", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var release = await _db.Releases.SingleAsync(
            item => item.Id == created.Value!.Id, TestContext.Current.CancellationToken);
        release.TransitionTo(ReleaseStatus.InProgress);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PrepareReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new PrepareReleaseCommand(
                _projectId, release.Id, release.Revision, MemberActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Equal(ReleasePreparationStatus.NotStarted, release.PreparationStatus);
        Assert.Equal(0, await _db.WorkstationCommands.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrepareReleaseRequiresConfiguredVersionSources()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Missing projections", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var release = await _db.Releases.SingleAsync(
            item => item.Id == created.Value!.Id, TestContext.Current.CancellationToken);
        release.TransitionTo(ReleaseStatus.InProgress);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PrepareReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new PrepareReleaseCommand(
                _projectId, release.Id, release.Revision, OwnerActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Validation, result.Kind);
        Assert.Contains("version-file projections", result.Error, StringComparison.Ordinal);
        Assert.Equal(ReleasePreparationStatus.NotStarted, release.PreparationStatus);
        Assert.Equal(0, await _db.WorkstationCommands.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyReleaseLandingRequiresProjectOwnerOrAdmin()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Verify authorization", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var result = await new VerifyReleaseLandingHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new VerifyReleaseLandingCommand(
                _projectId, created.Value!.Id, created.Value.PreparationRevision, MemberActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Equal(0, await _db.WorkstationCommands.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyReleaseLandingRejectsReleaseThatHasNotBeenPrepared()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Not prepared", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var result = await new VerifyReleaseLandingHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new VerifyReleaseLandingCommand(
                _projectId, created.Value!.Id, created.Value.PreparationRevision, OwnerActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        Assert.Contains("Prepared or VerifyFailed", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, await _db.WorkstationCommands.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishReleaseRequiresProjectOwnerOrAdmin()
    {
        var result = await new PublishReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new PublishReleaseCommand(
                _projectId, Guid.NewGuid(), 1, 1, MemberActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Equal(0, await _db.WorkstationCommands.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishReleaseRejectsAnythingOtherThanVerifiedCandidate()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Not a candidate", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var result = await new PublishReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new PublishReleaseCommand(
                _projectId, created.Value!.Id, created.Value.Revision, 1, OwnerActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        Assert.Contains("verified Candidate", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, await _db.WorkstationCommands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ReleaseStatus.Planned, (await _db.Releases.SingleAsync(
            item => item.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }


    [Fact]
    public async Task PublishAndCompletion_RehydrateCandidateSnapshotAcrossFreshDbContexts()
    {
        const string version = "0.7.5.9";
        const string tagName = "v0.7.5.9";
        var candidateSha = new string('a', 40);
        var deployedAt = DateTime.UtcNow;
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        var policy = new ProjectVersionPolicy(
            "NumericComponents",
            4,
            null,
            "v{version}",
            false,
            [new ProjectVersionSourcePolicy("Directory.Build.props", "Xml", "//Version")],
            ReleaseManagementEnabled: true,
            DeploymentVersionEndpoint: "https://release-test.example/v1/version");
        project.SetVersionPolicyJson(ProjectVersionPolicyCatalog.Serialize(policy));
        project.InitializeOfficialVersion(version);

        var device = DaemonDevice.Create(
            "release-publish-workstation",
            _ownerId,
            "release-publish-fingerprint",
            "release-publish-token-hash",
            "bcd_release");
        device.Heartbeat(
            """{"capabilities":["git-release-prepare-v1","git-release-publish-v1"]}""",
            """{"workspaceMode":"Branch"}""");
        var runtime = ProjectRuntime.Create(_projectId, device.Id, "/workspace/release-project");
        _db.DaemonDevices.Add(device);
        _db.ProjectRuntimes.Add(runtime);

        var release = Release.Create("Verified candidate", null, _projectId, version, "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        var candidateId = Guid.NewGuid();
        var prepareCommandId = Guid.NewGuid();
        var verifyCommandId = Guid.NewGuid();
        release.BeginPreparation(
            device.Id, candidateId, prepareCommandId, """{"version":"0.7.5.9"}""");
        release.RecordPrepareSuccess(prepareCommandId, """{"preparedCommit":"prepared"}""");
        release.BeginVerification(device.Id, verifyCommandId);

        var candidate = ReleaseCandidateSnapshot.Create(
            _projectId,
            release.Id,
            candidateId,
            device.Id,
            version,
            tagName,
            "main",
            new string('1', 40),
            new string('2', 40),
            candidateSha,
            new string('b', 40),
            project.VersionPolicyRevision,
            release.Revision,
            new string('c', 64),
            """{"files":[],"checks":[]}""",
            deployedAt.AddMinutes(-2));
        release.MarkCandidate(candidate);
        _db.Releases.Add(release);
        _db.ReleaseCandidateSnapshots.Add(candidate);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The Candidate snapshot is intentionally stored in a separate table, not
        // materialized through ReleaseConfiguration as a navigation.
        await using (var reloadedDb = Factory().CreateDbContext())
        {
            var reloaded = await reloadedDb.Releases.SingleAsync(
                item => item.Id == release.Id, TestContext.Current.CancellationToken);
            Assert.Null(reloaded.CandidateSnapshot);
            Assert.Equal(ReleaseStatus.Candidate, reloaded.Status);
            var mismatchedSnapshot = ReleaseCandidateSnapshot.Create(
                _projectId,
                release.Id,
                Guid.NewGuid(),
                device.Id,
                version,
                tagName,
                "main",
                new string('1', 40),
                new string('2', 40),
                candidateSha,
                new string('b', 40),
                project.VersionPolicyRevision,
                reloaded.Revision - 1,
                new string('c', 64),
                """{"files":[],"checks":[]}""",
                deployedAt.AddMinutes(-2));
            Assert.Throws<InvalidOperationException>(() => reloaded.AttachCandidateSnapshot(mismatchedSnapshot));
            Assert.Null(reloaded.CandidateSnapshot);
        }

        var queued = await new PublishReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new PublishReleaseCommand(
                _projectId, release.Id, release.Revision, release.PublicationRevision, OwnerActor()),
                TestContext.Current.CancellationToken);

        Assert.True(queued.Success, queued.Error);
        Assert.Equal(ReleasePublicationStatus.Queued, queued.Value!.Release.PublicationStatus);
        Assert.NotEqual(Guid.Empty, queued.Value.Command.Id);

        var result = new JsonObject
        {
            ["action"] = "Publish",
            ["projectId"] = _projectId.ToString("D"),
            ["releaseId"] = release.Id.ToString("D"),
            ["candidateId"] = candidateId.ToString("D"),
            ["version"] = version,
            ["tagName"] = tagName,
            ["candidateSha"] = candidateSha,
            ["tagPushed"] = true,
            ["workflowRunId"] = 12345,
            ["workflowRunUrl"] = "https://github.com/vitalyKl/ProjectBeacon/actions/runs/12345",
            ["workflowStatus"] = "completed",
            ["workflowConclusion"] = "success",
            ["deployedVersion"] = version,
            ["deployedCommitSha"] = candidateSha,
            ["deploymentStatus"] = "in_sync",
            ["deployedAtUtc"] = deployedAt.ToString("O")
        };
        var resultJson = result.ToJsonString();

        await using (var completionDb = Factory().CreateDbContext())
        {
            var command = await completionDb.WorkstationCommands.SingleAsync(
                item => item.Id == queued.Value.Command.Id, TestContext.Current.CancellationToken);
            command.Succeed(resultJson);
            await new ReleasePublishCompletion().ApplyAsync(
                completionDb,
                command,
                success: true,
                resultJson: resultJson,
                error: null,
                ct: TestContext.Current.CancellationToken);
            await completionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verifyDb = Factory().CreateDbContext();
        var published = await verifyDb.Releases.SingleAsync(
            item => item.Id == release.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseStatus.Published, published.Status);
        Assert.Equal(ReleasePublicationStatus.Published, published.PublicationStatus);
        Assert.True(published.PublicationTagPushed);
        Assert.Equal(tagName, published.PublishedTagName);
        Assert.Equal(candidateSha, published.DeployedCommitSha);
        Assert.Equal(version, published.DeployedVersion);
        Assert.Equal(12345, published.DeploymentWorkflowRunId);

        var publishedAudit = await verifyDb.AuditEvents.SingleAsync(
            item => item.Action == "project.release.published"
                && item.TargetId == release.Id.ToString("D"),
            TestContext.Current.CancellationToken);
        using var auditDetails = System.Text.Json.JsonDocument.Parse(publishedAudit.DetailsJson!);
        var auditRoot = auditDetails.RootElement;
        Assert.Equal(candidateId.ToString("D"), auditRoot.GetProperty("candidateId").GetString());
        Assert.True(auditRoot.GetProperty("tagPushed").GetBoolean());
        Assert.Equal(version, auditRoot.GetProperty("targetVersion").GetString());
        Assert.Equal(tagName, auditRoot.GetProperty("tagName").GetString());
        Assert.Equal(candidateSha, auditRoot.GetProperty("candidateSha").GetString());
        Assert.Equal(candidateSha, auditRoot.GetProperty("deployedSha").GetString());
        Assert.Equal(12345L, auditRoot.GetProperty("workflowRunId").GetInt64());
        Assert.False(auditRoot.TryGetProperty("workflowRunUrl", out _));
        Assert.False(auditRoot.TryGetProperty("error", out _));
    }

}
