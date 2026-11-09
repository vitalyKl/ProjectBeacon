namespace ProjectBeacon.Application.Tests.Context;

using System.Text.Json;
using Application.Authorization;
using Application.Common;
using Application.Context;
using Application.Persistence;
using Domain.Entities.Identity;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ContentVersionRestoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _actorId = Guid.NewGuid();

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
                Actor: HumanActor()), TestContext.Current.CancellationToken);

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
        constraint.UpdateBody("Changed constraint");
        constraint.SetScopePath("tests/");
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

    private ActorContext HumanActor() =>
        new(ActorType.Human, _actorId, false, null, null, _projectId, null, ApiTokenCapability.None);
}
