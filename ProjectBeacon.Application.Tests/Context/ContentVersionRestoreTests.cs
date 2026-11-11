namespace ProjectBeacon.Application.Tests.Context;

using System.Text.Json;
using Application.Authorization;
using Application.Common;
using Application.Context;
using Application.Proposals;
using Application.Persistence;
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

        var restored = await new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection))
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

        var stale = await new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection))
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

        var result = await new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId, version.Id, project.Revision, outsider),
                TestContext.Current.CancellationToken);

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

        var restored = await new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection))
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

        var restore = new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection));
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

        var created = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(_projectId, request, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        Assert.NotEqual(Guid.Empty, created.Value!.TargetId);
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
    public async Task DeleteMilestoneProposal_AcceptWritesTombstoneAndRestoreRecreatesSameId()
    {
        var milestone = Milestone.Create(
            "Release milestone", "Release checkpoint", _projectId, 3);
        _db.Milestones.Add(milestone);
        var writer = new ContentWriter();
        writer.AddVersion(_db, milestone, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var original = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id
                && item.Revision == 1,
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
                        1,
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
        Assert.Equal(2, accepted.Value.AppliedRevision);
        Assert.False(await _db.Milestones.AsNoTracking().AnyAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken));

        var tombstone = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Milestone
                && item.TargetId == milestone.Id
                && item.Revision == 2,
            TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(tombstone.ContentJson))
            Assert.True(json.RootElement.GetProperty("deleted").GetBoolean());

        var restored = await new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                original.Id,
                ExpectedRevision: 2,
                Actor: HumanActor(),
                ExpectedTargetKind: ContentTargetKind.Milestone,
                ExpectedTargetId: milestone.Id),
                TestContext.Current.CancellationToken);

        Assert.True(restored.Success, restored.Error);
        Assert.True(restored.Value!.Changed);
        Assert.Equal(3, restored.Value.Revision);
        var recreated = await _db.Milestones.SingleAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Release milestone", recreated.Name);
        Assert.Equal("Release checkpoint", recreated.Description);
        Assert.Equal(3, recreated.Order);
        Assert.Equal(3, recreated.Revision);
    }

    [Fact]
    public async Task DeleteContextSectionProposal_AcceptWritesTombstoneAndRestoreRecreatesReviewedSection()
    {
        var section = ContextSection.Create(
            "architecture", "Architecture", "Reviewed architecture section",
            _projectId, ContextScopeType.Project);
        section.MarkReviewed(_actorId.ToString("D"));
        _db.ContextSections.Add(section);
        var writer = new ContentWriter();
        writer.AddVersion(_db, section, "Human", _actorId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var original = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id
                && item.Revision == 1,
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
                        1,
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
                && item.Revision == 2,
            TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(tombstone.ContentJson))
            Assert.True(json.RootElement.GetProperty("deleted").GetBoolean());

        var restored = await new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new RestoreContentVersionCommand(
                _projectId,
                original.Id,
                ExpectedRevision: 2,
                Actor: HumanActor(),
                ExpectedTargetKind: ContentTargetKind.ContextSection,
                ExpectedTargetId: section.Id),
                TestContext.Current.CancellationToken);

        Assert.True(restored.Success, restored.Error);
        Assert.Equal(3, restored.Value!.Revision);
        var recreated = await _db.ContextSections.SingleAsync(
            item => item.Id == section.Id, TestContext.Current.CancellationToken);
        Assert.Equal("architecture", recreated.SectionId);
        Assert.Equal("Architecture", recreated.Title);
        Assert.Equal("Reviewed architecture section", recreated.BodyMarkdown);
        Assert.Equal(ContextReviewState.Reviewed, recreated.ReviewState);
        Assert.Equal(3, recreated.Revision);
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

        var decisionBase = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Decision
                && item.TargetId == decision.Id && item.Revision == 1,
            TestContext.Current.CancellationToken);
        var constraintBase = await _db.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Constraint
                && item.TargetId == constraint.Id && item.Revision == 1,
            TestContext.Current.CancellationToken);

        var creator = new CreateProposalHandler(
            HandlerSqlite.Factory(_connection), new EfAuditWriter());
        var decisionProposal = await creator.HandleAsync(
            new CreateProposalCommand(
                _projectId,
                new CreateProposalRequest(
                    ContentTargetKind.Decision, decision.Id, ProposalOperation.Delete, 1, "{}",
                    "Deprecates an obsolete decision.",
                    "decision-delete-" + Guid.NewGuid().ToString("N")),
                HumanActor()),
            TestContext.Current.CancellationToken);
        var constraintProposal = await creator.HandleAsync(
            new CreateProposalCommand(
                _projectId,
                new CreateProposalRequest(
                    ContentTargetKind.Constraint, constraint.Id, ProposalOperation.Delete, 1, "{}",
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
        Assert.Equal(2, decision.Revision);
        Assert.Equal(ConstraintStatus.Rejected, constraint.Status);
        Assert.Equal(2, constraint.Revision);

        var restore = new RestoreContentVersionHandler(HandlerSqlite.Factory(_connection));
        var decisionRestored = await restore.HandleAsync(new RestoreContentVersionCommand(
            _projectId, decisionBase.Id, 2, HumanActor(),
            ContentTargetKind.Decision, decision.Id), TestContext.Current.CancellationToken);
        var constraintRestored = await restore.HandleAsync(new RestoreContentVersionCommand(
            _projectId, constraintBase.Id, 2, HumanActor(),
            ContentTargetKind.Constraint, constraint.Id), TestContext.Current.CancellationToken);
        Assert.True(decisionRestored.Success, decisionRestored.Error);
        Assert.True(constraintRestored.Success, constraintRestored.Error);

        await _db.Entry(decision).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(constraint).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DecisionStatus.Accepted, decision.Status);
        Assert.Equal(3, decision.Revision);
        Assert.Equal(ConstraintStatus.Active, constraint.Status);
        Assert.Equal(3, constraint.Revision);
    }

    private ActorContext HumanActor() =>
        new(ActorType.Human, _actorId, false, null, null, _projectId, null, ApiTokenCapability.None);
}
