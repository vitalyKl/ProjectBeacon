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
            "alpha", "Alpha", "Original alpha.", _projectId, ContextScopeType.Project);
        var beta = ContextSection.Create(
            "beta", "Beta", "Original beta.", _projectId, ContextScopeType.Project);
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
}
