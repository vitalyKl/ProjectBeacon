namespace ProjectBeacon.Application.Tests.Context;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Authorization;
using Application.Common;
using Application.Context;
using Application.Proposals;
using Application.Persistence;
using Domain.Entities.Content;
using Domain.Entities.Identity;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ContentVersionRestoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private Guid _actorId;

    public ContentVersionRestoreTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Restore Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();

        var project = Project.Create("Restore Project", "Original description", org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var user = User.Create("restore-owner", "restore-owner@beacon.local", "hash");
        _db.Users.Add(user);
        _db.SaveChanges();
        _actorId = user.Id;
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, _actorId, MemberRole.Owner));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RestoreDeletedLegacySnapshots_UsesLifecycleFieldsFromNewTombstones()
    {
        var section = ContextSection.Create(
            "architecture", "Architecture", "Reviewed section body",
            _projectId, ContextScopeType.Project);
        _db.ContextSections.Add(section);
        var legacySection = ContentVersion.Create(
            _projectId, ContentTargetKind.ContextSection, section.Id, 1,
            """{"sectionId":"architecture","key":"","title":"Architecture","bodyMarkdown":"Reviewed section body","ordinal":0,"scopeType":0,"repoId":null,"taskId":null,"path":""}""",
            "Human", _actorId.ToString("D"));
        _db.ContentVersions.Add(legacySection);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        section.MarkReviewed(_actorId.ToString("D"));
        var writer = new ContentWriter();
        var reviewed = await writer.SaveAsync(
            _db, section, "Human", _actorId.ToString("D"), TestContext.Current.CancellationToken);
        Assert.True(reviewed.Success, reviewed.Error);
        section.Delete();
        var sectionDeleted = await writer.SaveAsync(
            _db, section, "Human", _actorId.ToString("D"),
            TestContext.Current.CancellationToken, deleted: true);
        Assert.True(sectionDeleted.Success, sectionDeleted.Error);
        _db.ContextSections.Remove(section);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var milestone = Milestone.Create(
            "Closed legacy milestone", "Milestone description", _projectId, 4);
        _db.Milestones.Add(milestone);
        var legacyMilestone = ContentVersion.Create(
            _projectId, ContentTargetKind.Milestone, milestone.Id, 1,
            """{"name":"Closed legacy milestone","description":"Milestone description","order":4}""",
            "Human", _actorId.ToString("D"));
        _db.ContentVersions.Add(legacyMilestone);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        milestone.Close();
        var closed = await writer.SaveAsync(
            _db, milestone, "Human", _actorId.ToString("D"), TestContext.Current.CancellationToken);
        Assert.True(closed.Success, closed.Error);
        milestone.Delete();
        var milestoneDeleted = await writer.SaveAsync(
            _db, milestone, "Human", _actorId.ToString("D"),
            TestContext.Current.CancellationToken, deleted: true);
        Assert.True(milestoneDeleted.Success, milestoneDeleted.Error);
        _db.Milestones.Remove(milestone);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.ChangeTracker.Clear();
        var restore = CreateRestoreHandler();
        var restoredSection = await restore.HandleAsync(new RestoreContentVersionCommand(
            _projectId, legacySection.Id, 3, HumanActor(),
            ContentTargetKind.ContextSection, section.Id), TestContext.Current.CancellationToken);
        var restoredMilestone = await restore.HandleAsync(new RestoreContentVersionCommand(
            _projectId, legacyMilestone.Id, 3, HumanActor(),
            ContentTargetKind.Milestone, milestone.Id), TestContext.Current.CancellationToken);

        Assert.True(restoredSection.Success, restoredSection.Error);
        Assert.True(restoredMilestone.Success, restoredMilestone.Error);
        Assert.Equal(4, restoredSection.Value!.Revision);
        Assert.Equal(4, restoredMilestone.Value!.Revision);

        var sectionFromHistory = await _db.ContextSections.AsNoTracking().SingleAsync(
            item => item.Id == section.Id, TestContext.Current.CancellationToken);
        var milestoneFromHistory = await _db.Milestones.AsNoTracking().SingleAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ContextReviewState.Reviewed, sectionFromHistory.ReviewState);
        Assert.NotNull(milestoneFromHistory.ClosedAt);
        Assert.Equal("Closed legacy milestone", milestoneFromHistory.Name);
    }

    [Fact]
    public async Task RestoreLegacySnapshots_DoesNotUpgradeUnknownLifecycleState()
    {
        var section = ContextSection.Create(
            "goals", "Legacy context", "Legacy context body", _projectId,
            ContextScopeType.Project);
        var decision = Decision.Create(
            "Legacy decision", "Context", "Legacy decision body", _projectId);
        var constraint = Constraint.Create(
            "Legacy constraint", ConstraintKind.Must, _projectId, "src/");
        var milestone = Milestone.Create(
            "Legacy milestone", "Legacy milestone body", _projectId, 1);
        _db.ContextSections.Add(section);
        _db.Decisions.Add(decision);
        _db.Constraints.Add(constraint);
        _db.Milestones.Add(milestone);

        var legacyVersions = new[]
        {
            ContentVersion.Create(
                _projectId, ContentTargetKind.ContextSection, section.Id, 1,
                """{"sectionId":"goals","key":"","title":"Legacy context","bodyMarkdown":"Legacy context body","ordinal":0,"scopeType":0,"repoId":null,"taskId":null,"path":""}""",
                "Human", _actorId.ToString("D")),
            ContentVersion.Create(
                _projectId, ContentTargetKind.Decision, decision.Id, 1,
                """{"title":"Legacy decision","context":"Context","decisionBody":"Legacy decision body","consequences":null}""",
                "Human", _actorId.ToString("D")),
            ContentVersion.Create(
                _projectId, ContentTargetKind.Constraint, constraint.Id, 1,
                """{"body":"Legacy constraint","scopePath":"src/","kind":0}""",
                "Human", _actorId.ToString("D")),
            ContentVersion.Create(
                _projectId, ContentTargetKind.Milestone, milestone.Id, 1,
                """{"name":"Legacy milestone","description":"Legacy milestone body","order":1}""",
                "Human", _actorId.ToString("D"))
        };
        _db.ContentVersions.AddRange(legacyVersions);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        section.Update(title: "Changed context", bodyMarkdown: "Changed context body");
        decision.UpdateContent("Changed decision", "Changed context", "Changed decision body", "Changed consequences");
        constraint.UpdateContent("Changed constraint", "tests/");
        milestone.Close();

        var writer = new ContentWriter();
        foreach (var entity in new object[] { section, decision, constraint, milestone })
        {
            var saved = await writer.SaveAsync(
                _db, entity, "Human", _actorId.ToString("D"), TestContext.Current.CancellationToken);
            Assert.True(saved.Success, saved.Error);
        }

        // Keep a closed milestone after a later content edit. Restoring the old snapshot,
        // which predates ClosedAt, must not silently reopen it.
        milestone.Update("Changed milestone", "Changed milestone body", 2);
        var changedMilestone = await writer.SaveAsync(
            _db, milestone, "Human", _actorId.ToString("D"), TestContext.Current.CancellationToken);
        Assert.True(changedMilestone.Success, changedMilestone.Error);
        Assert.NotNull(milestone.ClosedAt);

        var restore = CreateRestoreHandler();
        var expected = new Dictionary<ContentTargetKind, int>
        {
            [ContentTargetKind.ContextSection] = 2,
            [ContentTargetKind.Decision] = 2,
            [ContentTargetKind.Constraint] = 2,
            [ContentTargetKind.Milestone] = 3
        };

        foreach (var kind in expected.Keys)
        {
            var restored = await restore.HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                legacyVersions.Single(version => version.TargetKind == kind).Id,
                expected[kind],
                HumanActor(),
                kind,
                kind switch
                {
                    ContentTargetKind.ContextSection => section.Id,
                    ContentTargetKind.Decision => decision.Id,
                    ContentTargetKind.Constraint => constraint.Id,
                    ContentTargetKind.Milestone => milestone.Id,
                    _ => throw new InvalidOperationException("Unexpected test target kind.")
                }),
                TestContext.Current.CancellationToken);
            Assert.True(restored.Success, restored.Error);
            Assert.Equal(expected[kind] + 1, restored.Value!.Revision);
        }

        await _db.Entry(section).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(decision).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(constraint).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ContextReviewState.NeedsReview, section.ReviewState);
        Assert.Equal("Legacy context", section.Title);
        Assert.Equal(3, section.Revision);

        Assert.Equal(DecisionStatus.Proposed, decision.Status);
        Assert.Equal("Legacy decision", decision.Title);
        Assert.Equal(3, decision.Revision);

        Assert.Equal(ConstraintStatus.Proposed, constraint.Status);
        Assert.Equal("Legacy constraint", constraint.Body);
        Assert.Equal(3, constraint.Revision);

        Assert.Equal("Legacy milestone", milestone.Name);
        Assert.Equal(4, milestone.Revision);
        Assert.NotNull(milestone.ClosedAt);
    }

    [Fact]
    public async Task RestoreProjectDescription_AppendsNewRevision_AndRecordsRestoringActor()
    {
        var project = await _db.Projects.SingleAsync(p => p.Id == _projectId, TestContext.Current.CancellationToken);
        var writer = new ContentWriter();
        writer.AddVersion(_db, project, "Human", Guid.NewGuid().ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var original = await _db.ContentVersions.SingleAsync(
            v => v.TargetKind == ContentTargetKind.ProjectDescription && v.TargetId == _projectId,
            TestContext.Current.CancellationToken);

        project.Update(description: "Changed description");
        var changed = await writer.SaveAsync(
            _db, project, "Human", Guid.NewGuid().ToString("D"), TestContext.Current.CancellationToken);
        Assert.True(changed.Success, changed.Error);
        Assert.Equal(2, changed.Value);

        var restored = await CreateRestoreHandler()
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                original.Id,
                ExpectedRevision: 2,
                Actor: HumanActor()), TestContext.Current.CancellationToken);

        Assert.True(restored.Success, restored.Error);
        Assert.True(restored.Value!.Changed);
        Assert.Equal(3, restored.Value.Revision);

        await _db.Entry(project).ReloadAsync(TestContext.Current.CancellationToken);
        var current = project;
        Assert.Equal("Original description", current.Description);
        Assert.Equal(3, current.Revision);

        var history = await _db.ContentVersions
            .Where(v => v.TargetKind == ContentTargetKind.ProjectDescription && v.TargetId == _projectId)
            .OrderBy(v => v.Revision)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2, 3 }, history.Select(v => v.Revision).ToArray());
        Assert.Equal("Human", history[^1].ChangedByType);
        Assert.Equal(_actorId.ToString("D"), history[^1].ChangedById);
        using var snapshot = JsonDocument.Parse(history[^1].ContentJson);
        Assert.Equal("Original description", snapshot.RootElement.GetProperty("description").GetString());

        var stale = await CreateRestoreHandler()
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                original.Id,
                ExpectedRevision: 2,
                Actor: HumanActor()), TestContext.Current.CancellationToken);
        Assert.False(stale.Success);
        Assert.Equal(ErrorKind.Conflict, stale.Kind);
        Assert.Equal(3, await _db.ContentVersions.CountAsync(
            v => v.TargetKind == ContentTargetKind.ProjectDescription && v.TargetId == _projectId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreHistory_RequiresProjectMembership()
    {
        var project = await _db.Projects.SingleAsync(item => item.Id == _projectId, TestContext.Current.CancellationToken);
        new ContentWriter().AddVersion(_db, project, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var version = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.ProjectDescription && item.TargetId == _projectId,
            TestContext.Current.CancellationToken);
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);

        var result = await CreateRestoreHandler()
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId, version.Id, project.Revision, outsider),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task DeleteContextNode_WritesTombstoneAndAuditAtomically()
    {
        var section = ContextSection.Create(
            "retired", "Retired section", "Content to be removed.",
            _projectId, ContextScopeType.Project);
        _db.ContextSections.Add(section);
        var writer = new ContentWriter();
        writer.AddVersion(_db, section, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new DeleteContextNodeHandler(
                HandlerSqlite.Factory(_connection), writer, new EfAuditWriter())
            .HandleAsync(
                new DeleteContextNodeCommand(
                    new DeleteContextNodeRequest(_projectId, section.Id), HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(await _db.ContextSections.AsNoTracking().AnyAsync(
            item => item.Id == section.Id, TestContext.Current.CancellationToken));
        var tombstone = await _db.ContentVersions.SingleAsync(
            version => version.TargetKind == ContentTargetKind.ContextSection
                && version.TargetId == section.Id
                && version.Revision == 2,
            TestContext.Current.CancellationToken);
        using (var snapshot = JsonDocument.Parse(tombstone.ContentJson))
            Assert.True(snapshot.RootElement.GetProperty("deleted").GetBoolean());

        var audit = await _db.AuditEvents.SingleAsync(
            item => item.Action == "content.deleted"
                && item.TargetType == nameof(ContentTargetKind.ContextSection)
                && item.TargetId == section.Id.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.Equal(_projectId, audit.ProjectId);
        Assert.Null(audit.DetailsJson);
    }

    [Fact]
    public async Task DeleteContextNode_RequiresProjectMembershipAndPreservesContent()
    {
        var section = ContextSection.Create(
            "private", "Protected section", "Protected content.",
            _projectId, ContextScopeType.Project);
        _db.ContextSections.Add(section);
        new ContentWriter().AddVersion(_db, section, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var result = await new DeleteContextNodeHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(
                new DeleteContextNodeCommand(
                    new DeleteContextNodeRequest(_projectId, section.Id), outsider),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.True(await _db.ContextSections.AsNoTracking().AnyAsync(
            item => item.Id == section.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.ContentVersions.CountAsync(
            version => version.TargetKind == ContentTargetKind.ContextSection
                && version.TargetId == section.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await _db.AuditEvents.AnyAsync(
            item => item.Action == "content.deleted"
                && item.TargetId == section.Id.ToString("D"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListContentVersions_RequiresMembershipOrProjectBoundContextReadToken()
    {
        var orgId = _db.Orgs.Single().Id;
        var otherProject = Project.Create("Other restore project", null, orgId);
        _db.Projects.Add(otherProject);
        _db.ContentVersions.AddRange(
            ContentVersion.Create(
                _projectId,
                ContentTargetKind.ProjectDescription,
                _projectId,
                1,
                """{"description":"Private project A history"}""",
                "Human",
                _actorId.ToString("D")),
            ContentVersion.Create(
                otherProject.Id,
                ContentTargetKind.ProjectDescription,
                otherProject.Id,
                1,
                """{"description":"Private project B history"}""",
                "Human",
                _actorId.ToString("D")));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new ListContentVersionsHandler(HandlerSqlite.Factory(_connection));
        var ownerResult = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.ProjectDescription,
            _projectId,
            HumanActor(),
            ct: TestContext.Current.CancellationToken);
        Assert.True(ownerResult.Success, ownerResult.Error);
        Assert.Contains(ownerResult.Value!, item => item.ContentJson.Contains("Private project A history"));

        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var outsiderResult = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.ProjectDescription,
            _projectId,
            outsider,
            ct: TestContext.Current.CancellationToken);
        Assert.False(outsiderResult.Success);
        Assert.Equal(ErrorKind.Forbidden, outsiderResult.Kind);

        var crossProjectHuman = await handler.HandleAsync(
            otherProject.Id,
            ContentTargetKind.ProjectDescription,
            otherProject.Id,
            HumanActor(),
            ct: TestContext.Current.CancellationToken);
        Assert.False(crossProjectHuman.Success);
        Assert.Equal(ErrorKind.Forbidden, crossProjectHuman.Kind);

        var tokenForOtherProject = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            otherProject.Id, null, ApiTokenCapability.ContextRead);
        var crossProjectToken = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.ProjectDescription,
            _projectId,
            tokenForOtherProject,
            ct: TestContext.Current.CancellationToken);
        Assert.False(crossProjectToken.Success);
        Assert.Equal(ErrorKind.Forbidden, crossProjectToken.Kind);

        var tokenForCurrentProject = tokenForOtherProject with { ProjectId = _projectId };
        var tokenResult = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.ProjectDescription,
            _projectId,
            tokenForCurrentProject,
            ct: TestContext.Current.CancellationToken);
        Assert.True(tokenResult.Success, tokenResult.Error);
        Assert.Contains(tokenResult.Value!, item => item.ContentJson.Contains("Private project A history"));

        var tokenWithoutContextRead = tokenForCurrentProject with
        {
            Capabilities = ApiTokenCapability.TaskRead
        };
        var noCapabilityResult = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.ProjectDescription,
            _projectId,
            tokenWithoutContextRead,
            ct: TestContext.Current.CancellationToken);
        Assert.False(noCapabilityResult.Success);
        Assert.Equal(ErrorKind.Forbidden, noCapabilityResult.Kind);
    }

    [Fact]
    public async Task ListContentVersions_TaskDescriptionRequiresTaskReadAndProjectBinding()
    {
        var task = TaskItem.Create("Private task history", _projectId, description: "Task description");
        _db.Tasks.Add(task);
        _db.ContentVersions.Add(ContentVersion.Create(
            _projectId,
            ContentTargetKind.TaskDescription,
            task.Id,
            1,
            """{"description":"Private task description","reviewNotes":null}""",
            "Human",
            _actorId.ToString("D")));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new ListContentVersionsHandler(HandlerSqlite.Factory(_connection));
        var taskReadToken = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.TaskRead);
        var allowed = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.TaskDescription,
            task.Id,
            taskReadToken,
            ct: TestContext.Current.CancellationToken);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Contains(allowed.Value!, item => item.ContentJson.Contains("Private task description"));

        var contextReadToken = taskReadToken with
        {
            Capabilities = ApiTokenCapability.ContextRead
        };
        var wrongCapability = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.TaskDescription,
            task.Id,
            contextReadToken,
            ct: TestContext.Current.CancellationToken);
        Assert.False(wrongCapability.Success);
        Assert.Equal(ErrorKind.Forbidden, wrongCapability.Kind);

        var crossProjectToken = taskReadToken with { ProjectId = Guid.NewGuid() };
        var wrongProject = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.TaskDescription,
            task.Id,
            crossProjectToken,
            ct: TestContext.Current.CancellationToken);
        Assert.False(wrongProject.Success);
        Assert.Equal(ErrorKind.Forbidden, wrongProject.Kind);

        var human = await handler.HandleAsync(
            _projectId,
            ContentTargetKind.TaskDescription,
            task.Id,
            HumanActor(),
            ct: TestContext.Current.CancellationToken);
        Assert.True(human.Success, human.Error);
    }

    [Fact]
    public async Task ListDeletedContentTargets_RequiresProjectMembership()
    {
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);

        var result = await new ListDeletedContentTargetsHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(_projectId, outsider, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task RestoreTaskDescription_RestoresNullableReviewNotes()
    {
        var task = TaskItem.Create("Task", _projectId, description: "Original task description");
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var writer = new ContentWriter();
        writer.AddVersion(_db, task, "Human", Guid.NewGuid().ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var original = await _db.ContentVersions.SingleAsync(
            v => v.TargetKind == ContentTargetKind.TaskDescription && v.TargetId == task.Id,
            TestContext.Current.CancellationToken);

        task.SetReviewNotes("Review notes to remove");
        task.Update(description: "Changed task description");
        var changed = await writer.SaveAsync(
            _db, task, "Human", Guid.NewGuid().ToString("D"), TestContext.Current.CancellationToken);
        Assert.True(changed.Success, changed.Error);
        Assert.Equal(3, changed.Value);

        var restored = await CreateRestoreHandler()
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                original.Id,
                ExpectedRevision: 3,
                Actor: HumanActor(),
                ExpectedTargetKind: ContentTargetKind.TaskDescription,
                ExpectedTargetId: task.Id), TestContext.Current.CancellationToken);

        Assert.True(restored.Success, restored.Error);
        Assert.Equal(4, restored.Value!.Revision);

        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        var current = task;
        Assert.Equal("Original task description", current.Description);
        Assert.Null(current.ReviewNotes);
        Assert.Equal(4, current.ContentRevision);
    }

    [Fact]
    public async Task RestoreContextDecisionConstraintAndMilestone_UsesTheirContentSnapshots()
    {
        var section = ContextSection.Create(
            "goals", "Goals", "Original context", _projectId, ContextScopeType.Project);
        var decision = Decision.Create(
            "Original decision", "Context", "Original body", _projectId, "Original consequences");
        var constraint = Constraint.Create(
            "Original constraint", ConstraintKind.Must, _projectId, "src/");
        var milestone = Milestone.Create(
            "Original milestone", "Original milestone description", _projectId, 1);
        _db.ContextSections.Add(section);
        _db.Decisions.Add(decision);
        _db.Constraints.Add(constraint);
        _db.Milestones.Add(milestone);
        var writer = new ContentWriter();
        writer.AddVersion(_db, section, "Human", Guid.NewGuid().ToString("D"));
        writer.AddVersion(_db, decision, "Human", Guid.NewGuid().ToString("D"));
        writer.AddVersion(_db, constraint, "Human", Guid.NewGuid().ToString("D"));
        writer.AddVersion(_db, milestone, "Human", Guid.NewGuid().ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var originals = await _db.ContentVersions
            .Where(v => v.Revision == 1 && v.ProjectId == _projectId)
            .ToDictionaryAsync(v => v.TargetKind, v => v, TestContext.Current.CancellationToken);

        section.Update(title: "Changed title", bodyMarkdown: "Changed context");
        decision.UpdateContent("Changed decision", "Changed context", "Changed body", "Changed consequences");
        constraint.UpdateContent("Changed constraint", "tests/");
        milestone.Update("Changed milestone", "Changed milestone description", 2);
        writer.AddVersion(_db, section, "Human", _actorId.ToString("D"));
        writer.AddVersion(_db, decision, "Human", _actorId.ToString("D"));
        writer.AddVersion(_db, constraint, "Human", _actorId.ToString("D"));
        writer.AddVersion(_db, milestone, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var restore = CreateRestoreHandler();
        foreach (var kind in new[]
        {
            ContentTargetKind.ContextSection,
            ContentTargetKind.Decision,
            ContentTargetKind.Constraint,
            ContentTargetKind.Milestone
        })
        {
            var version = originals[kind];
            var result = await restore.HandleAsync(new RestoreContentVersionCommand(
                _projectId, version.Id, 2, HumanActor()), TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error);
            Assert.True(result.Value!.Changed);
            Assert.Equal(3, result.Value.Revision);
        }

        await _db.Entry(section).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(decision).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(constraint).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Goals", section.Title);
        Assert.Equal("Original context", section.BodyMarkdown);
        Assert.Equal(3, section.Revision);

        Assert.Equal("Original decision", decision.Title);
        Assert.Equal("Original body", decision.DecisionBody);
        Assert.Equal("Original consequences", decision.Consequences);
        Assert.Equal(3, decision.Revision);

        Assert.Equal("Original constraint", constraint.Body);
        Assert.Equal("src/", constraint.ScopePath);
        Assert.Equal(3, constraint.Revision);

        Assert.Equal("Original milestone", milestone.Name);
        Assert.Equal("Original milestone description", milestone.Description);
        Assert.Equal(1, milestone.Order);
        Assert.Equal(3, milestone.Revision);
    }

    [Fact]
    public async Task CreateMilestoneProposal_AcceptCreatesReservedTargetAndInitialHistory()
    {
        var request = new CreateProposalRequest(
            ContentTargetKind.Milestone,
            Guid.Empty,
            ProposalOperation.Create,
            0,
            """{"name":"Alpha milestone","description":"First release checkpoint","order":1}""",
            "Adds the first roadmap checkpoint.",
            "milestone-create-" + Guid.NewGuid().ToString("N"));

        var creator = new CreateProposalHandler(
            HandlerSqlite.Factory(_connection), new EfAuditWriter());
        var created = await creator.HandleAsync(
            new CreateProposalCommand(_projectId, request, HumanActor()),
            TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        Assert.NotEqual(Guid.Empty, created.Value!.TargetId);
        var replay = await creator.HandleAsync(
            new CreateProposalCommand(_projectId, request, HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(replay.Success, replay.Error);
        Assert.Equal(created.Value.Id, replay.Value!.Id);
        Assert.Equal(created.Value.TargetId, replay.Value.TargetId);
        Assert.Equal(1, await _db.Proposals.CountAsync(
            item => item.IdempotencyKey == request.IdempotencyKey,
            TestContext.Current.CancellationToken));
        Assert.Equal(ProposalOperation.Create, created.Value.Operation);
        Assert.Equal(0, created.Value.BaseRevision);
        Assert.Equal(ProposalStatus.Pending, created.Value.Status);

        var accepted = await new AcceptProposalHandler(
                HandlerSqlite.Factory(_connection),
                new AuthorizationService(),
                new EfAuditWriter(),
                new ContentWriter())
            .HandleAsync(
                new AcceptProposalCommand(_projectId, created.Value.Id, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value!.Status);
        Assert.Equal(1, accepted.Value.AppliedRevision);

        var milestone = await _db.Milestones.SingleAsync(
            item => item.Id == created.Value.TargetId,
            TestContext.Current.CancellationToken);
        Assert.Equal("Alpha milestone", milestone.Name);
        Assert.Equal("First release checkpoint", milestone.Description);
        Assert.Equal(1, milestone.Order);
        Assert.Equal(1, milestone.Revision);

        var version = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, version.Revision);
        Assert.Equal(created.Value.Id, version.ProposalId);
    }

    [Fact]
    public async Task MilestoneUpdateProposal_AcceptPreservesLinkedTasksInNewSnapshot()
    {
        var milestone = Milestone.Create(
            "Original milestone", "Original description", _projectId, 1);
        var linkedTask = TaskItem.Create("Linked milestone task", _projectId);
        linkedTask.SetMilestone(milestone.Id);
        _db.Milestones.Add(milestone);
        _db.Tasks.Add(linkedTask);
        var writer = new ContentWriter();
        writer.AddVersion(_db, milestone, "Human", _actorId.ToString("D"),
            milestoneTaskIds: [linkedTask.Id]);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var baseVersion = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id
                && item.Revision == milestone.Revision,
            TestContext.Current.CancellationToken);
        var baseHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(baseVersion.ContentJson))).ToLowerInvariant();

        var proposed = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(
                    _projectId,
                    new CreateProposalRequest(
                        ContentTargetKind.Milestone,
                        milestone.Id,
                        ProposalOperation.Update,
                        milestone.Revision,
                        baseHash,
                        """{"name":"Updated milestone","description":"Updated description","order":2}""",
                        "Updates the milestone roadmap details.",
                        "milestone-update-linked-task-" + Guid.NewGuid().ToString("N")),
                    HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(proposed.Success, proposed.Error);

        var accepted = await new AcceptProposalHandler(
                HandlerSqlite.Factory(_connection),
                new AuthorizationService(),
                new EfAuditWriter(),
                writer)
            .HandleAsync(
                new AcceptProposalCommand(_projectId, proposed.Value!.Id, HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value!.Status);
        Assert.Equal(2, accepted.Value.AppliedRevision);

        var updatedSnapshot = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id
                && item.Revision == 2,
            TestContext.Current.CancellationToken);
        using var snapshot = JsonDocument.Parse(updatedSnapshot.ContentJson);
        Assert.Equal("Updated milestone", snapshot.RootElement.GetProperty("name").GetString());
        Assert.Contains(
            linkedTask.Id,
            snapshot.RootElement.GetProperty("taskIds").EnumerateArray().Select(item => item.GetGuid()));

        var persistedTask = await _db.Tasks.AsNoTracking().SingleAsync(
            item => item.Id == linkedTask.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(milestone.Id, persistedTask.MilestoneId);
    }

    [Fact]
    public async Task DeleteMilestoneProposal_AcceptWritesTombstoneAndRestoreRecreatesSameId()
    {
        var milestone = Milestone.Create(
            "Release milestone", "Release checkpoint", _projectId, 3);
        var createdAt = milestone.CreatedAt;
        var baseRevision = milestone.Revision;
        var linkedTask = TaskItem.Create(
            "Task assigned to release milestone", _projectId);
        linkedTask.SetMilestone(milestone.Id);
        _db.Milestones.Add(milestone);
        _db.Tasks.Add(linkedTask);
        var writer = new ContentWriter();
        writer.AddVersion(_db, milestone, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var original = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id
                && item.Revision == baseRevision,
            TestContext.Current.CancellationToken);

        var created = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(
                    _projectId,
                    new CreateProposalRequest(
                        ContentTargetKind.Milestone,
                        milestone.Id,
                        ProposalOperation.Delete,
                        baseRevision,
                        "{}",
                        "Removes a completed milestone.",
                        "milestone-delete-" + Guid.NewGuid().ToString("N")),
                    HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var accepted = await new AcceptProposalHandler(
                HandlerSqlite.Factory(_connection),
                new AuthorizationService(),
                new EfAuditWriter(),
                writer)
            .HandleAsync(
                new AcceptProposalCommand(_projectId, created.Value!.Id, HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value!.Status);
        Assert.Equal(baseRevision + 1, accepted.Value.AppliedRevision);
        Assert.False(await _db.Milestones.AsNoTracking().AnyAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken));
        var taskAfterDelete = await _db.Tasks.AsNoTracking().SingleAsync(
            item => item.Id == linkedTask.Id, TestContext.Current.CancellationToken);
        Assert.Null(taskAfterDelete.MilestoneId);

        var tombstone = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id
                && item.Revision == baseRevision + 1,
            TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(tombstone.ContentJson))
        {
            Assert.True(json.RootElement.GetProperty("deleted").GetBoolean());
            Assert.Contains(
                linkedTask.Id,
                json.RootElement.GetProperty("taskIds").EnumerateArray().Select(item => item.GetGuid()));
        }

        var deletedTargets = await new ListDeletedContentTargetsHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(_projectId, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(deletedTargets.Success, deletedTargets.Error);
        var listedDeletion = Assert.Single(deletedTargets.Value!, item =>
            item.TargetKind == ContentTargetKind.Milestone && item.TargetId == milestone.Id);
        Assert.Equal(baseRevision + 1, listedDeletion.DeletedRevision);
        Assert.Equal(original.Id, listedDeletion.SourceVersionId);
        Assert.Contains("Release milestone", listedDeletion.Title);

        var restored = await CreateRestoreHandler()
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                original.Id,
                ExpectedRevision: baseRevision + 1,
                Actor: HumanActor(),
                ExpectedTargetKind: ContentTargetKind.Milestone,
                ExpectedTargetId: milestone.Id),
                TestContext.Current.CancellationToken);

        Assert.True(restored.Success, restored.Error);
        Assert.True(restored.Value!.Changed);
        Assert.Equal(baseRevision + 2, restored.Value.Revision);
        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);
        var recreated = milestone;
        Assert.Equal("Release milestone", recreated.Name);
        Assert.Equal("Release checkpoint", recreated.Description);
        Assert.Equal(3, recreated.Order);
        Assert.Equal(createdAt, recreated.CreatedAt);
        Assert.Equal(baseRevision + 2, recreated.Revision);
        var taskAfterRestore = await _db.Tasks.AsNoTracking().SingleAsync(
            item => item.Id == linkedTask.Id, TestContext.Current.CancellationToken);
        Assert.Equal(milestone.Id, taskAfterRestore.MilestoneId);
        var restoredSnapshot = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id
                && item.Revision == baseRevision + 2,
            TestContext.Current.CancellationToken);
        using (var restoredJson = JsonDocument.Parse(restoredSnapshot.ContentJson))
        {
            Assert.Contains(
                linkedTask.Id,
                restoredJson.RootElement.GetProperty("taskIds").EnumerateArray().Select(item => item.GetGuid()));
        }
        var restoreAudit = await _db.AuditEvents.SingleAsync(
            item => item.Action == "content.restored"
                && item.TargetType == nameof(ContentTargetKind.Milestone)
                && item.TargetId == milestone.Id.ToString("D"),
            TestContext.Current.CancellationToken);
        using (var details = JsonDocument.Parse(restoreAudit.DetailsJson!))
        {
            Assert.Equal(original.Id.ToString("D"), details.RootElement.GetProperty("sourceVersionId").GetString());
            Assert.Equal(baseRevision + 2, details.RootElement.GetProperty("restoredRevision").GetInt32());
            Assert.Equal(1, details.RootElement.GetProperty("restoredTaskLinks").GetInt32());
        }
        Assert.DoesNotContain("Release milestone", restoreAudit.DetailsJson);
        var remainingDeleted = await new ListDeletedContentTargetsHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(_projectId, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(remainingDeleted.Success, remainingDeleted.Error);
        Assert.DoesNotContain(remainingDeleted.Value!, item =>
            item.TargetKind == ContentTargetKind.Milestone && item.TargetId == milestone.Id);
    }

    [Fact]
    public async Task DeleteContextSectionProposal_AcceptWritesTombstoneAndRestoreRecreatesReviewedSection()
    {
        var section = ContextSection.Create(
            "architecture", "Architecture", "Reviewed architecture section",
            _projectId, ContextScopeType.Project);
        section.MarkReviewed(_actorId.ToString("D"));
        var baseRevision = section.Revision;
        _db.ContextSections.Add(section);
        var writer = new ContentWriter();
        writer.AddVersion(_db, section, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var original = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id
                && item.Revision == baseRevision,
            TestContext.Current.CancellationToken);

        var created = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(
                    _projectId,
                    new CreateProposalRequest(
                        ContentTargetKind.ContextSection,
                        section.Id,
                        ProposalOperation.Delete,
                        baseRevision,
                        "{}",
                        "Removes an obsolete reviewed section.",
                        "section-delete-" + Guid.NewGuid().ToString("N")),
                    HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var accepted = await new AcceptProposalHandler(
                HandlerSqlite.Factory(_connection),
                new AuthorizationService(),
                new EfAuditWriter(),
                writer)
            .HandleAsync(
                new AcceptProposalCommand(_projectId, created.Value!.Id, HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value!.Status);
        Assert.False(await _db.ContextSections.AsNoTracking().AnyAsync(
            item => item.Id == section.Id, TestContext.Current.CancellationToken));

        var tombstone = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id
                && item.Revision == baseRevision + 1,
            TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(tombstone.ContentJson))
            Assert.True(json.RootElement.GetProperty("deleted").GetBoolean());

        var deletedTargets = await new ListDeletedContentTargetsHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(_projectId, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(deletedTargets.Success, deletedTargets.Error);
        var listedDeletion = Assert.Single(deletedTargets.Value!, item =>
            item.TargetKind == ContentTargetKind.ContextSection && item.TargetId == section.Id);
        Assert.Equal(baseRevision + 1, listedDeletion.DeletedRevision);
        Assert.Equal(original.Id, listedDeletion.SourceVersionId);
        Assert.Equal("Architecture", listedDeletion.Title);

        var restored = await CreateRestoreHandler()
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                original.Id,
                ExpectedRevision: baseRevision + 1,
                Actor: HumanActor(),
                ExpectedTargetKind: ContentTargetKind.ContextSection,
                ExpectedTargetId: section.Id),
                TestContext.Current.CancellationToken);

        Assert.True(restored.Success, restored.Error);
        Assert.Equal(baseRevision + 2, restored.Value!.Revision);
        await _db.Entry(section).ReloadAsync(TestContext.Current.CancellationToken);
        var recreated = section;
        Assert.Equal("architecture", recreated.SectionId);
        Assert.Equal("Architecture", recreated.Title);
        Assert.Equal("Reviewed architecture section", recreated.BodyMarkdown);
        Assert.Equal(ContextReviewState.Reviewed, recreated.ReviewState);
        Assert.Equal(baseRevision + 2, recreated.Revision);
        var restoreAudit = await _db.AuditEvents.SingleAsync(
            item => item.Action == "content.restored"
                && item.TargetType == nameof(ContentTargetKind.ContextSection)
                && item.TargetId == section.Id.ToString("D"),
            TestContext.Current.CancellationToken);
        using (var details = JsonDocument.Parse(restoreAudit.DetailsJson!))
        {
            Assert.Equal(original.Id.ToString("D"), details.RootElement.GetProperty("sourceVersionId").GetString());
            Assert.Equal(baseRevision + 2, details.RootElement.GetProperty("restoredRevision").GetInt32());
        }
        Assert.DoesNotContain("Reviewed architecture section", restoreAudit.DetailsJson);
        var remainingDeleted = await new ListDeletedContentTargetsHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(_projectId, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(remainingDeleted.Success, remainingDeleted.Error);
        Assert.DoesNotContain(remainingDeleted.Value!, item =>
            item.TargetKind == ContentTargetKind.ContextSection && item.TargetId == section.Id);
    }

    [Fact]
    public async Task ProjectMemberCannotRestoreAcceptedDecisionHistory()
    {
        var decision = Decision.Create(
            "Governance decision", "Context", "Accepted decision body", _projectId);
        decision.Accept();
        _db.Decisions.Add(decision);
        var writer = new ContentWriter();
        writer.AddVersion(_db, decision, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var acceptedVersion = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Decision
                && item.TargetId == decision.Id
                && item.Revision == decision.Revision,
            TestContext.Current.CancellationToken);

        decision.Deprecate();
        var deprecated = await writer.SaveAsync(
            _db, decision, "Human", _actorId.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.True(deprecated.Success, deprecated.Error);

        var member = User.Create(
            "restore-member", "restore-member@beacon.local", "hash");
        _db.Users.Add(member);
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, member.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var memberActor = new ActorContext(
            ActorType.Human, member.Id, false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var result = await CreateRestoreHandler().HandleAsync(
            new RestoreContentVersionCommand(
                _projectId,
                acceptedVersion.Id,
                decision.Revision,
                memberActor,
                ContentTargetKind.Decision,
                decision.Id),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        await _db.Entry(decision).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DecisionStatus.Deprecated, decision.Status);
        Assert.Equal(3, decision.Revision);
    }

    [Fact]
    public async Task DeleteDecisionAndConstraintProposals_TransitionAndRestoreLifecycleStatus()
    {
        var decision = Decision.Create(
            "Accepted decision", "Context", "Decision body", _projectId, "Consequences");
        decision.Accept();
        var constraint = Constraint.Create(
            "Active rule", ConstraintKind.Must, _projectId, "src/");
        constraint.Activate();
        _db.Decisions.Add(decision);
        _db.Constraints.Add(constraint);
        var writer = new ContentWriter();
        writer.AddVersion(_db, decision, "Human", _actorId.ToString("D"));
        writer.AddVersion(_db, constraint, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var decisionBaseRevision = decision.Revision;
        var constraintBaseRevision = constraint.Revision;
        var decisionBase = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Decision
                && item.TargetId == decision.Id && item.Revision == decisionBaseRevision,
            TestContext.Current.CancellationToken);
        var constraintBase = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Constraint
                && item.TargetId == constraint.Id && item.Revision == constraintBaseRevision,
            TestContext.Current.CancellationToken);

        var creator = new CreateProposalHandler(
            HandlerSqlite.Factory(_connection), new EfAuditWriter());
        var decisionProposal = await creator.HandleAsync(
            new CreateProposalCommand(
                _projectId,
                new CreateProposalRequest(
                    ContentTargetKind.Decision, decision.Id, ProposalOperation.Delete, decisionBaseRevision, "{}",
                    "Deprecates an obsolete decision.",
                    "decision-delete-" + Guid.NewGuid().ToString("N")),
                HumanActor()),
            TestContext.Current.CancellationToken);
        var constraintProposal = await creator.HandleAsync(
            new CreateProposalCommand(
                _projectId,
                new CreateProposalRequest(
                    ContentTargetKind.Constraint, constraint.Id, ProposalOperation.Delete, constraintBaseRevision, "{}",
                    "Rejects an obsolete constraint.",
                    "constraint-delete-" + Guid.NewGuid().ToString("N")),
                HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(decisionProposal.Success, decisionProposal.Error);
        Assert.True(constraintProposal.Success, constraintProposal.Error);

        var acceptor = new AcceptProposalHandler(
            HandlerSqlite.Factory(_connection),
            new AuthorizationService(),
            new EfAuditWriter(),
            writer);
        var acceptedDecision = await acceptor.HandleAsync(
            new AcceptProposalCommand(_projectId, decisionProposal.Value!.Id, HumanActor()),
            TestContext.Current.CancellationToken);
        var acceptedConstraint = await acceptor.HandleAsync(
            new AcceptProposalCommand(_projectId, constraintProposal.Value!.Id, HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(acceptedDecision.Success, acceptedDecision.Error);
        Assert.True(acceptedConstraint.Success, acceptedConstraint.Error);

        await _db.Entry(decision).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(constraint).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DecisionStatus.Deprecated, decision.Status);
        Assert.Equal(3, decision.Revision);
        Assert.Equal(ConstraintStatus.Rejected, constraint.Status);
        Assert.Equal(3, constraint.Revision);

        var restore = CreateRestoreHandler();
        var decisionRestored = await restore.HandleAsync(new RestoreContentVersionCommand(
            _projectId, decisionBase.Id, 3, HumanActor(),
            ContentTargetKind.Decision, decision.Id), TestContext.Current.CancellationToken);
        var constraintRestored = await restore.HandleAsync(new RestoreContentVersionCommand(
            _projectId, constraintBase.Id, 3, HumanActor(),
            ContentTargetKind.Constraint, constraint.Id), TestContext.Current.CancellationToken);
        Assert.True(decisionRestored.Success, decisionRestored.Error);
        Assert.True(constraintRestored.Success, constraintRestored.Error);

        await _db.Entry(decision).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(constraint).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DecisionStatus.Accepted, decision.Status);
        Assert.Equal(4, decision.Revision);
        Assert.Equal(ConstraintStatus.Active, constraint.Status);
        Assert.Equal(4, constraint.Revision);
    }

    private RestoreContentVersionHandler CreateRestoreHandler() =>
        new(
            HandlerSqlite.Factory(_connection),
            new ContentWriter(),
            new EfAuditWriter(),
            new AuthorizationService());

    private ActorContext HumanActor() =>
        new(ActorType.Human, _actorId, false, null, null, _projectId, null, ApiTokenCapability.None);
}
