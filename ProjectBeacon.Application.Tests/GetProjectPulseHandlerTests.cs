namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using ProjectBeacon.Application.Projects;

public sealed class GetProjectPulseHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public GetProjectPulseHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Member_SeesActiveProjectPulse()
    {
        var (alice, project) = await SeedMemberProject("Mine", "A pulse project");

        var todo = TaskItem.Create("ready", project.Id, TaskPriority.High);
        var inProgress = TaskItem.Create("progress", project.Id);
        inProgress.MoveToNextStatus();
        _db.Tasks.AddRange(todo, inProgress);
        await _db.SaveChangesAsync();

        var open = Milestone.Create("M1", null, project.Id, 0);
        var closed = Milestone.Create("M2", null, project.Id, 1);
        closed.Close();
        _db.Milestones.AddRange(open, closed);
        await _db.SaveChangesAsync();

        _db.ContextSections.Add(ContextSection.Create(
            "goals", "Goals", "Ship the pulse", project.Id, ContextScopeType.Project));
        _db.ContextSections.Add(ContextSection.Create(
            "definition_of_done", "DoD", "Tests pass", project.Id, ContextScopeType.Project));
        _db.ContextSections.Add(ContextSection.Create(
            "architecture", "Architecture", "ignored", project.Id, ContextScopeType.Project));
        await _db.SaveChangesAsync();

        var ready = PipelineSession.Create(todo.Id, project.Id, PipelineRole.Planner, "plan");
        var active = PipelineSession.Create(inProgress.Id, project.Id, PipelineRole.Actor, "act");
        active.Launch();
        _db.PipelineSessions.AddRange(ready, active);
        await _db.SaveChangesAsync();

        var handler = new GetProjectPulseHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(alice.Id, isAdmin: false, project.Id);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        var dto = result.Value!;
        Assert.Equal(project.Id, dto.ProjectId);
        Assert.Equal("Mine", dto.Name);
        Assert.Equal("A pulse project", dto.Description);
        Assert.Equal(1, dto.Todo);
        Assert.Equal(1, dto.InProgress);
        Assert.Equal(0, dto.Done);
        Assert.Equal(1, dto.OpenMilestones);
        Assert.Equal(1, dto.ActiveSessions);
        Assert.Single(dto.ReadyPeek);
        Assert.Equal(todo.Id, dto.ReadyPeek[0].Id);
        Assert.Equal(2, dto.BriefSections.Count);
        Assert.Equal("goals", dto.BriefSections[0].SectionId);
        Assert.Equal("Ship the pulse", dto.BriefSections[0].BodyMarkdown);
        Assert.Equal("definition_of_done", dto.BriefSections[1].SectionId);
    }

    [Fact]
    public async Task Peek_TakesThreeHighestPriorityTodos()
    {
        var (alice, project) = await SeedMemberProject("P");

        _db.Tasks.AddRange(
            TaskItem.Create("low", project.Id, TaskPriority.Low),
            TaskItem.Create("medium", project.Id, TaskPriority.Medium),
            TaskItem.Create("high", project.Id, TaskPriority.High),
            TaskItem.Create("critical", project.Id, TaskPriority.Critical));
        await _db.SaveChangesAsync();

        var handler = new GetProjectPulseHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(alice.Id, isAdmin: false, project.Id);

        Assert.True(result.Success);
        var peek = result.Value!.ReadyPeek;
        Assert.Equal(3, peek.Count);
        Assert.Equal("critical", peek[0].Title);
        Assert.Equal("high", peek[1].Title);
        Assert.Equal("medium", peek[2].Title);
        Assert.Equal(4, result.Value.Todo);
    }

    [Fact]
    public async Task Brief_PrefersProjectScopedNonEmptySections()
    {
        var (alice, project) = await SeedMemberProject("P");

        _db.ContextSections.Add(ContextSection.Create(
            "goals", "Repo goals", "repo body", project.Id, ContextScopeType.Repo));
        _db.ContextSections.Add(ContextSection.Create(
            "goals", "Project goals", "project body", project.Id, ContextScopeType.Project));
        _db.ContextSections.Add(ContextSection.Create(
            "definition_of_done", "Empty DoD", "   ", project.Id, ContextScopeType.Project));
        await _db.SaveChangesAsync();

        var handler = new GetProjectPulseHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(alice.Id, isAdmin: false, project.Id);

        Assert.True(result.Success);
        Assert.Single(result.Value!.BriefSections);
        Assert.Equal("goals", result.Value.BriefSections[0].SectionId);
        Assert.Equal("project body", result.Value.BriefSections[0].BodyMarkdown);
    }

    [Fact]
    public async Task UserWithoutProject_ReturnsNull()
    {
        var bob = User.Create("bob", "bob@example.com", "hash");
        _db.Users.Add(bob);
        await _db.SaveChangesAsync();

        var handler = new GetProjectPulseHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(bob.Id, isAdmin: false);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Admin_SeesProjectWithoutMembership()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var root = User.Create("root", "root@example.com", "hash");
        _db.Users.Add(root);
        await _db.SaveChangesAsync();

        var handler = new GetProjectPulseHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(root.Id, isAdmin: true, project.Id);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(project.Id, result.Value!.ProjectId);
    }

    private async Task<(User User, Project Project)> SeedMemberProject(string name, string? description = null)
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var project = Project.Create(name, description, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var alice = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(alice);
        await _db.SaveChangesAsync();

        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, alice.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        return (alice, project);
    }
}
