namespace ProjectBeacon.Application.Tests.Context;

using Application.Common;
using Application.Context;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Tests;

public sealed class CompileBriefTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;
    private Guid _orgId;
    private Guid _projectId;

    public CompileBriefTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Test Org", null);
        _db.Orgs.Add(org);
        _db.SaveChanges();
        _orgId = org.Id;

        var project = Project.Create("Test Project", null, _orgId);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task CompileBrief_IncludesProjectLevelSection()
    {
        AddSection("goals", "Goals", "Build a great product");

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var command = new CompileBriefCommand(new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false));

        var result = await handler.HandleAsync(command);

        Assert.True(result.Success);
        Assert.Contains("## Goals", result.Value.BriefMarkdown);
        Assert.Contains("Build a great product", result.Value.BriefMarkdown);
        Assert.True(result.Value.TokenEstimate > 0);
        Assert.False(result.Value.BudgetOverflow);
    }

    [Fact]
    public async Task CompileBrief_NeverDropsSecuritySection()
    {
        AddSection("security", "Security", "No hardcoded passwords");
        AddSection("goals", "Goals " + new string('x', 8000), "Build something " + new string('y', 8000));

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var command = new CompileBriefCommand(new CompileBriefRequest(_projectId, null, null, null, 40, false, false, false));

        var result = await handler.HandleAsync(command);

        Assert.True(result.Success);
        Assert.Contains("## Security", result.Value.BriefMarkdown);
        Assert.Contains("## Tools for this task", result.Value.BriefMarkdown);
        Assert.Contains("`claim_task`", result.Value.BriefMarkdown);
        Assert.DoesNotContain("## Goals", result.Value.BriefMarkdown);
        Assert.Contains("goals", result.Value.DroppedSections);
        Assert.True(result.Value.BudgetOverflow);
    }

    [Fact]
    public async Task CompileBrief_DropsLowPrioritySections_WhenBudgetExceeded()
    {
        AddSection("non_goals", "Non-Goals", "Not building a spaceship");
        AddSection("security", "Security", "No passwords in code");
        AddSection("definition_of_done", "Definition Of Done", "Tests pass and code reviewed");
        AddSection("goals", "Goals", "Ship features");
        AddSection("architecture", "Architecture", "Clean architecture");

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var command = new CompileBriefCommand(new CompileBriefRequest(_projectId, null, null, null, 15, false, false, false));

        var result = await handler.HandleAsync(command);

        Assert.True(result.Success);
        Assert.Contains("## Non-Goals", result.Value.BriefMarkdown);
        Assert.Contains("## Security", result.Value.BriefMarkdown);
        Assert.Contains("## Definition Of Done", result.Value.BriefMarkdown);
        Assert.True(result.Value.BudgetOverflow);
        Assert.Contains("goals", result.Value.DroppedSections.Select(s => s.ToLower()));
    }

    [Fact]
    public async Task CompileBrief_MergesSections_LastWriterWins()
    {
        AddSectionWithScope(ContextScopeType.Project, "goals", "Goals", "Original goals");
        AddSectionWithScope(ContextScopeType.Repo, "goals", "Goals", "placeholder");
        var repoNode = _db.ContextSections.Single(s => s.ScopeType == ContextScopeType.Repo);
        repoNode.Update(bodyMarkdown: "Updated goals from repo node");
        await _db.SaveChangesAsync();

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var command = new CompileBriefCommand(new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false));

        var result = await handler.HandleAsync(command);

        Assert.True(result.Success);
        Assert.Contains("Updated goals from repo node", result.Value.BriefMarkdown);
    }

    [Fact]
    public async Task CompileBrief_StoresRevisionInDatabase()
    {
        AddSection("goals", "Goals", "Test goals");

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var command = new CompileBriefCommand(new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false));

        var result = await handler.HandleAsync(command);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Value.RevisionId);
        Assert.NotEmpty(result.Value.CompiledHash);

        var revision = await _db.ContextRevisions.FirstOrDefaultAsync(r => r.ProjectId == _projectId);
        Assert.NotNull(revision);
        Assert.Equal(result.Value.CompiledHash, revision.CompiledHash);
        Assert.Contains("Goals", revision.BriefMarkdown);
    }

    [Fact]
    public async Task CompileBrief_GeneratesConsistentHash()
    {
        AddSection("goals", "Goals", "Consistent test");

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var command1 = new CompileBriefCommand(new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false));
        var result1 = await handler.HandleAsync(command1);

        var command2 = new CompileBriefCommand(new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false));
        var result2 = await handler.HandleAsync(command2);

        Assert.Equal(result1.Value.CompiledHash, result2.Value.CompiledHash);
    }

    [Fact]
    public async Task UpsertContextNode_CreatesNewSection()
    {
        var handler = new UpsertContextNodeHandler(HandlerSqlite.Factory(_connection));
        var command = new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            _projectId,
            "Goals",
            "Build something great",
            "goals",
            ContextScopeType.Project,
            null,
            null,
            null,
            null,
            ContextSource.Native,
            null));

        var result = await handler.HandleAsync(command);

        Assert.True(result.Success);
        Assert.Equal("Goals", result.Value.Title);
        Assert.Equal("Build something great", result.Value.BodyMarkdown);

        var section = await _db.ContextSections.FirstOrDefaultAsync(s => s.Id == result.Value.Id);
        Assert.NotNull(section);
        Assert.Equal("goals", section.SectionId);
    }

    [Fact]
    public async Task UpsertContextNode_UpdatesExistingSection()
    {
        var upsertHandler = new UpsertContextNodeHandler(HandlerSqlite.Factory(_connection));

        await upsertHandler.HandleAsync(new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            _projectId,
            "Goals",
            "Original text",
            "goals",
            ContextScopeType.Project,
            null,
            null,
            null,
            null,
            ContextSource.Native,
            null)));

        var updateCommand = new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            _projectId,
            "Goals",
            "Updated text",
            "goals",
            ContextScopeType.Project,
            null,
            null,
            null,
            null,
            ContextSource.Native,
            null));

        var updateResult = await upsertHandler.HandleAsync(updateCommand);

        Assert.True(updateResult.Success);
        Assert.Equal("Updated text", updateResult.Value.BodyMarkdown);
    }

    [Fact]
    public async Task ListContextNodes_ReturnsAllSections()
    {
        var upsertHandler = new UpsertContextNodeHandler(HandlerSqlite.Factory(_connection));

        await upsertHandler.HandleAsync(new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            _projectId, "Goals", "Text 1", "goals", ContextScopeType.Project, null, null, null, null, ContextSource.Native, null)));

        await upsertHandler.HandleAsync(new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            _projectId, "Architecture", "Text 2", "architecture", ContextScopeType.Project, null, null, null, null, ContextSource.Native, null)));

        var listHandler = new ListContextNodesHandler(HandlerSqlite.Factory(_connection));
        var command = new ListContextNodesCommand(new ListContextNodesRequest(_projectId));

        var result = await listHandler.HandleAsync(command);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value.Count);
    }

    [Fact]
    public async Task DeleteContextNode_RemovesSection()
    {
        var upsertHandler = new UpsertContextNodeHandler(HandlerSqlite.Factory(_connection));

        var createResult = await upsertHandler.HandleAsync(new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            _projectId, "Goals", "Text", "goals", ContextScopeType.Project, null, null, null, null, ContextSource.Native, null)));

        var deleteHandler = new DeleteContextNodeHandler(HandlerSqlite.Factory(_connection));
        var command = new DeleteContextNodeCommand(new DeleteContextNodeRequest(_projectId, createResult.Value.Id));

        var result = await deleteHandler.HandleAsync(command);

        Assert.True(result.Success);

        var deleted = await _db.ContextSections.FirstOrDefaultAsync(s => s.Id == createResult.Value.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public void Tokenizer_EstimatesAsciiCorrectly()
    {
        var text = "Hello world this is a test";
        var tokens = Tokenizer.EstimateTokens(text);

        Assert.True(tokens > 0);
    }

    [Fact]
    public void Tokenizer_Heuristic_CountsUnicodeAsOneToken()
    {
        var russian = "Привет мир";
        var heuristic = Tokenizer.EstimateHeuristic(russian);
        var real = Tokenizer.CountTokens(russian);
        Assert.Equal(9, heuristic);
        Assert.True(real > 0);
    }

    [Fact]
    public void Tokenizer_SharpTokenDiffersFromHeuristic_OnRuAndEn()
    {
        Assert.Equal("cl100k_base", Tokenizer.TokenizerId);
        var ru = "Определение готовности: тесты должны проходить";
        var en = "Definition of done: tests must pass before merge";
        Assert.NotEqual(Tokenizer.EstimateHeuristic(ru), Tokenizer.CountTokens(ru));
        Assert.True(Tokenizer.CountTokens(ru) > 0);
        Assert.True(Tokenizer.CountTokens(en) > 0);
        Assert.True(Tokenizer.EstimateHeuristic(en) > 0);
    }

    [Fact]
    public async Task CompileBrief_IncludesDecisionConsequences()
    {
        var decision = Decision.Create("Use Postgres", "need a db", "Postgres 16", _projectId, "SQLite tests are not proof");
        decision.Accept();
        _db.Decisions.Add(decision);
        _db.SaveChanges();

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new CompileBriefCommand(
            new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false)));

        Assert.True(result.Success);
        Assert.Contains("SQLite tests are not proof", result.Value.BriefMarkdown);
    }

    [Fact]
    public async Task CompileBrief_DetectsMustMustNotContradiction()
    {
        var must = Constraint.Create("всегда писать тесты", ConstraintKind.Must, _projectId, "src");
        must.Activate();
        var mustNot = Constraint.Create("никогда не писать тесты", ConstraintKind.MustNot, _projectId, "src");
        mustNot.Activate();
        _db.Constraints.AddRange(must, mustNot);
        _db.SaveChanges();

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new CompileBriefCommand(
            new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false)));

        Assert.True(result.Success);
        Assert.Contains("conflicting constraints", result.Value.BriefMarkdown);
    }

    [Fact]
    public async Task CompileBrief_NeverDropsDoDConstraintAndTaskDescription()
    {
        var dod = Constraint.Create("Definition of Done: tests pass", ConstraintKind.Must, _projectId);
        dod.Activate();
        _db.Constraints.Add(dod);
        var task = TaskItem.Create("Ship compiler", _projectId);
        task.Update(description: "Keep never-drop sections");
        _db.Tasks.Add(task);
        AddSection("goals", "Goals", new string('G', 20000));
        _db.SaveChanges();

        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new CompileBriefCommand(
            new CompileBriefRequest(_projectId, null, null, task.Id, 50, false, false, false)));

        Assert.True(result.Success);
        Assert.Contains("Keep never-drop sections", result.Value.BriefMarkdown);
        Assert.Contains("Definition of Done: tests pass", result.Value.BriefMarkdown);
        Assert.Contains("## Tools for this task", result.Value.BriefMarkdown);
        Assert.True(result.Value.BudgetOverflow);
    }

    [Fact]
    public async Task CompileBrief_IncludesTreeWhenRepoLinked()
    {
        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new CompileBriefCommand(
            new CompileBriefRequest(_projectId, Guid.NewGuid(), null, null, 8000, false, false, false)));

        Assert.True(result.Success);
        Assert.Contains("## Tree", result.Value.BriefMarkdown);
        Assert.Contains("## Changed scope", result.Value.BriefMarkdown);
    }

    [Fact]
    public async Task CompileBrief_RevisionIdMatchesStoredEntity()
    {
        AddSection("goals", "Goals", "x");
        var handler = new CompileBriefHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new CompileBriefCommand(
            new CompileBriefRequest(_projectId, null, null, null, 8000, false, false, false)));

        Assert.True(result.Success);
        var stored = await _db.ContextRevisions.FirstAsync(r => r.ProjectId == _projectId);
        Assert.Equal(stored.Id.ToString(), result.Value.RevisionId);
    }

    [Fact]
    public async Task ImportFiles_UsesRouteProjectId()
    {
        var handler = new ImportFilesHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new ImportFilesCommand(_projectId, [
            new ImportFileRequest("AGENTS.md", "# Hello", "/tmp/AGENTS.md")
        ]));

        Assert.True(result.Success);
        var section = Assert.Single(_db.ContextSections);
        Assert.Equal(_projectId, section.ProjectId);
        Assert.NotEqual(Guid.Empty, section.ProjectId);
    }

    [Fact]
    public void Tokenizer_ReturnsEmptyForNull()
    {
        var tokens = Tokenizer.EstimateTokens(null);

        Assert.Equal(0, tokens);
    }

    [Fact]
    public void Tokenizer_ReturnsEmptyForEmpty()
    {
        var tokens = Tokenizer.EstimateTokens(string.Empty);

        Assert.Equal(0, tokens);
    }

    private void AddSection(string sectionId, string title, string body)
    {
        AddSectionWithScope(ContextScopeType.Project, sectionId, title, body);
    }

    private void AddSectionWithScope(ContextScopeType scopeType, string sectionId, string title, string body)
    {
        var section = ContextSection.Create(
            sectionId: sectionId ?? title.ToLowerInvariant().Replace(" ", "_"),
            title: title,
            bodyMarkdown: body,
            projectId: _projectId,
            scopeType: scopeType,
            key: null,
            ordinal: 0);

        _db.ContextSections.Add(section);
        _db.SaveChanges();
    }
}
