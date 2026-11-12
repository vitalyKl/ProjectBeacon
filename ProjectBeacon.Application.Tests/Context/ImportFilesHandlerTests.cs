namespace ProjectBeacon.Application.Tests.Context;

using Application.Context;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Content;
using Domain.Entities.Context;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ImportFilesHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;

    public ImportFilesHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Context Import Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();

        var project = Project.Create("Context Import Project", null, org.Id);
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
    public async Task Import_CreatesAndUpdatesVersionedContextSections()
    {
        var handler = new ImportFilesHandler(HandlerSqlite.Factory(_connection));
        var first = await handler.HandleAsync(new ImportFilesCommand(
            _projectId,
            [new ImportFileRequest("AGENTS.md", "Initial guidance.", "repo/AGENTS.md")]),
            TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Error);
        Assert.Equal(1, first.Value);

        var section = await _db.ContextSections.SingleAsync(
            item => item.ProjectId == _projectId && item.SectionId == "agents",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, section.Revision);
        Assert.Equal("Initial guidance.", section.BodyMarkdown);
        Assert.Equal("repo/AGENTS.md", section.SourcePath);

        var updated = await handler.HandleAsync(new ImportFilesCommand(
            _projectId,
            [new ImportFileRequest("AGENTS.md", "Updated guidance.", "repo/AGENTS.md")]),
            TestContext.Current.CancellationToken);

        Assert.True(updated.Success, updated.Error);
        Assert.Equal(0, updated.Value);

        await _db.Entry(section).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, section.Revision);
        Assert.Equal("Updated guidance.", section.BodyMarkdown);

        var history = await _db.ContentVersions
            .Where(version => version.ProjectId == _projectId
                && version.TargetKind == ContentTargetKind.ContextSection
                && version.TargetId == section.Id)
            .OrderBy(version => version.Revision)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 }, history.Select(version => version.Revision).ToArray());
    }

    [Fact]
    public async Task Import_WhenContentRevisionAlreadyExists_ReturnsConflictAndRollsBackWholeBatch()
    {
        var alpha = ContextSection.Create(
            "alpha", "Alpha", "Original alpha.", _projectId, ContextScopeType.Project,
            source: ContextSource.Native, sourcePath: "Alpha.md");
        var beta = ContextSection.Create(
            "beta", "Beta", "Original beta.", _projectId, ContextScopeType.Project,
            source: ContextSource.Native, sourcePath: "Beta.md");
        _db.ContextSections.AddRange(alpha, beta);

        var writer = new ContentWriter();
        writer.AddVersion(_db, alpha, "Human", "import-test");
        writer.AddVersion(_db, beta, "Human", "import-test");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Simulate the unique history revision committed by a racing writer. The import
        // still holds the current target revision, so its attempted N+1 snapshot collides.
        _db.ContentVersions.Add(ContentVersion.Create(
            _projectId,
            ContentTargetKind.ContextSection,
            beta.Id,
            2,
            """{"sectionId":"beta","title":"Beta","bodyMarkdown":"Concurrent beta."}""",
            "Human",
            "concurrent-writer"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new ImportFilesHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new ImportFilesCommand(
                _projectId,
                [
                    new ImportFileRequest("Alpha.md", "Changed alpha.", "repo/Alpha.md"),
                    new ImportFileRequest("Beta.md", "Changed beta.", "repo/Beta.md")
                ]),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);

        await _db.Entry(alpha).ReloadAsync(TestContext.Current.CancellationToken);
        await _db.Entry(beta).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, alpha.Revision);
        Assert.Equal("Original alpha.", alpha.BodyMarkdown);
        Assert.Equal(1, beta.Revision);
        Assert.Equal("Original beta.", beta.BodyMarkdown);

        var alphaHistory = await _db.ContentVersions
            .Where(version => version.ProjectId == _projectId
                && version.TargetKind == ContentTargetKind.ContextSection
                && version.TargetId == alpha.Id)
            .OrderBy(version => version.Revision)
            .Select(version => version.Revision)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1 }, alphaHistory);

        var betaHistory = await _db.ContentVersions
            .Where(version => version.ProjectId == _projectId
                && version.TargetKind == ContentTargetKind.ContextSection
                && version.TargetId == beta.Id)
            .OrderBy(version => version.Revision)
            .Select(version => version.Revision)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 }, betaHistory);
    }
    [Fact]
    public async Task Import_DistinguishesSameFileNameBySourcePath()
    {
        var handler = new ImportFilesHandler(HandlerSqlite.Factory(_connection));
        var imported = await handler.HandleAsync(new ImportFilesCommand(
            _projectId,
            [
                new ImportFileRequest("AGENTS.md", "API guidance.", "services/api/AGENTS.md"),
                new ImportFileRequest("AGENTS.md", "Worker guidance.", "services/worker/AGENTS.md")
            ]),
            TestContext.Current.CancellationToken);

        Assert.True(imported.Success, imported.Error);
        Assert.Equal(2, imported.Value);

        var initial = await _db.ContextSections
            .Where(section => section.ProjectId == _projectId && section.SectionId == "agents")
            .OrderBy(section => section.SourcePath)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, initial.Count);
        Assert.Equal(
            new[] { "services/api/AGENTS.md", "services/worker/AGENTS.md" },
            initial.Select(section => section.SourcePath).ToArray());
        Assert.Equal(
            new[] { "API guidance.", "Worker guidance." },
            initial.Select(section => section.BodyMarkdown).ToArray());

        var apiId = initial[0].Id;
        var workerId = initial[1].Id;
        var updated = await handler.HandleAsync(new ImportFilesCommand(
            _projectId,
            [new ImportFileRequest("AGENTS.md", "Updated API guidance.", "services/api/AGENTS.md")]),
            TestContext.Current.CancellationToken);

        Assert.True(updated.Success, updated.Error);
        Assert.Equal(0, updated.Value);

        _db.ChangeTracker.Clear();
        var refreshed = await _db.ContextSections
            .Where(section => section.ProjectId == _projectId && section.SectionId == "agents")
            .ToDictionaryAsync(section => section.SourcePath, TestContext.Current.CancellationToken);
        Assert.Equal(2, refreshed.Count);
        Assert.Equal(apiId, refreshed["services/api/AGENTS.md"].Id);
        Assert.Equal(2, refreshed["services/api/AGENTS.md"].Revision);
        Assert.Equal("Updated API guidance.", refreshed["services/api/AGENTS.md"].BodyMarkdown);
        Assert.Equal(workerId, refreshed["services/worker/AGENTS.md"].Id);
        Assert.Equal(1, refreshed["services/worker/AGENTS.md"].Revision);
        Assert.Equal("Worker guidance.", refreshed["services/worker/AGENTS.md"].BodyMarkdown);
    }

    [Fact]
    public async Task Import_DoesNotOverwriteNativeContextWithMatchingSectionId()
    {
        var native = ContextSection.Create(
            "agents", "Agents", "Beacon-authored guidance.", _projectId, ContextScopeType.Project,
            source: ContextSource.Native, updatedByType: "user", updatedById: "user-1");
        _db.ContextSections.Add(native);
        new ContentWriter().AddVersion(_db, native, "Human", "user-1");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new ImportFilesHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new ImportFilesCommand(
                _projectId,
                [new ImportFileRequest("AGENTS.md", "Imported guidance.", "repo/AGENTS.md")]),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Value);

        var sections = await _db.ContextSections
            .Where(section => section.ProjectId == _projectId && section.SectionId == "agents")
            .OrderBy(section => section.Source)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, sections.Count);
        Assert.Contains(sections, section =>
            section.Source == ContextSource.Native && section.BodyMarkdown == "Beacon-authored guidance.");
        Assert.Contains(sections, section =>
            section.Source == ContextSource.ImportedAgentsMd
            && section.SourcePath == "repo/AGENTS.md"
            && section.BodyMarkdown == "Imported guidance.");
    }

    [Fact]
    public async Task Import_UpgradesLegacyBasenameIdentityWithoutReplacingTheTarget()
    {
        var legacy = ContextSection.Create(
            "agents", "AGENTS", "Legacy guidance.", _projectId, ContextScopeType.Project,
            source: ContextSource.ImportedAgentsMd, sourcePath: "AGENTS.md");
        _db.ContextSections.Add(legacy);
        new ContentWriter().AddVersion(_db, legacy, "System", "AGENTS.md");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new ImportFilesHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new ImportFilesCommand(
                _projectId,
                [new ImportFileRequest("AGENTS.md", "Refreshed guidance.", "repo/AGENTS.md")]),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Value);

        _db.ChangeTracker.Clear();
        var section = await _db.ContextSections.SingleAsync(
            item => item.ProjectId == _projectId && item.SectionId == "agents",
            TestContext.Current.CancellationToken);
        Assert.Equal(legacy.Id, section.Id);
        Assert.Equal("repo/AGENTS.md", section.SourcePath);
        Assert.Equal(ContextSource.ImportedAgentsMd, section.Source);
        Assert.Equal(2, section.Revision);
        Assert.Equal("Refreshed guidance.", section.BodyMarkdown);

        var revisions = await _db.ContentVersions
            .Where(version => version.ProjectId == _projectId
                && version.TargetKind == ContentTargetKind.ContextSection
                && version.TargetId == legacy.Id)
            .OrderBy(version => version.Revision)
            .Select(version => version.Revision)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 }, revisions);
    }

    [Fact]
    public async Task Import_RejectsAmbiguousLegacyBasenameAcrossMultipleFiles()
    {
        var legacy = ContextSection.Create(
            "agents", "AGENTS", "Legacy guidance.", _projectId, ContextScopeType.Project,
            source: ContextSource.ImportedAgentsMd, sourcePath: "AGENTS.md");
        _db.ContextSections.Add(legacy);
        new ContentWriter().AddVersion(_db, legacy, "System", "AGENTS.md");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new ImportFilesHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new ImportFilesCommand(
                _projectId,
                [
                    new ImportFileRequest("AGENTS.md", "API guidance.", "services/api/AGENTS.md"),
                    new ImportFileRequest("AGENTS.md", "Worker guidance.", "services/worker/AGENTS.md")
                ]),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);

        await _db.Entry(legacy).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("AGENTS.md", legacy.SourcePath);
        Assert.Equal("Legacy guidance.", legacy.BodyMarkdown);
        Assert.Equal(1, legacy.Revision);
        Assert.Single(await _db.ContextSections.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_UsesImportedMarkdownSourceForUnclassifiedFiles()
    {
        var result = await new ImportFilesHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new ImportFilesCommand(
                _projectId,
                [new ImportFileRequest("README.md", "Project overview.", "docs/README.md")]),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Value);

        var section = await _db.ContextSections.SingleAsync(
            item => item.ProjectId == _projectId && item.SectionId == "readme",
            TestContext.Current.CancellationToken);
        Assert.Equal(ContextSource.ImportedMarkdown, section.Source);
        Assert.Equal("docs/README.md", section.SourcePath);
    }

    [Fact]
    public async Task ImportedFileIdentity_IsUniqueButNativeContextIsNotConstrainedByIt()
    {
        var imported = ContextSection.Create(
            "agents", "AGENTS", "Imported.", _projectId, ContextScopeType.Project,
            source: ContextSource.ImportedAgentsMd, sourcePath: "repo/AGENTS.md");
        var native = ContextSection.Create(
            "overview", "Overview", "Native.", _projectId, ContextScopeType.Project,
            source: ContextSource.Native, sourcePath: "repo/AGENTS.md");
        _db.ContextSections.AddRange(imported, native);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var duplicate = ContextSection.Create(
            "agents", "AGENTS", "Duplicate.", _projectId, ContextScopeType.Project,
            source: ContextSource.ImportedMarkdown, sourcePath: "repo/AGENTS.md");
        _db.ContextSections.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            _db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

}
