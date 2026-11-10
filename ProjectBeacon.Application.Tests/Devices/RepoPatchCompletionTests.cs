namespace ProjectBeacon.Application.Tests.Devices;

using System.Text.Json;
using Application.Devices;
using Domain.Entities.Content;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class RepoPatchCompletionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public RepoPatchCompletionTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(false, ProposalStatus.Accepted)]
    [InlineData(true, ProposalStatus.Conflicted)]
    public async Task Completion_RequiresResultProjectIdToMatchProposalProject(
        bool wrongProject,
        ProposalStatus expectedStatus)
    {
        var org = Org.Create("RepoPatch completion");
        var project = Project.Create("Docs", null, org.Id);
        var now = DateTime.UtcNow;
        var tokenId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        const string resultHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string baseHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var patch = """
            diff --git a/docs/guide.md b/docs/guide.md
            --- a/docs/guide.md
            +++ b/docs/guide.md
            @@ -1 +1 @@
            -before
            +after
            """;
        var payload = JsonSerializer.Serialize(new
        {
            patch,
            files = new[]
            {
                new
                {
                    gitPath = "docs/guide.md",
                    projectPath = "docs/guide.md",
                    baseHash,
                    resultHash
                }
            }
        });
        var proposal = Proposal.Create(
            project.Id,
            null,
            null,
            null,
            ContentTargetKind.RepoPatch,
            project.Id,
            ProposalOperation.Update,
            1,
            baseHash,
            payload,
            "Update the documentation.",
            ProposalAuthorKind.Agent,
            null,
            tokenId,
            "docs-writer",
            PipelineRole.Actor.ToString(),
            "repo-patch-completion-" + Guid.NewGuid().ToString("N"),
            now);
        var commandId = Guid.NewGuid();
        proposal.StartApply(commandId, reviewerId, now);
        var command = WorkstationCommand.Create(
            Guid.NewGuid(),
            WorkstationCommandKind.ApplyProposalPatch,
            JsonSerializer.Serialize(new { projectId = project.Id, proposalId = proposal.Id }),
            project.Id);

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.Proposals.Add(proposal);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var resultProjectId = wrongProject ? Guid.NewGuid() : project.Id;
            var resultJson = JsonSerializer.Serialize(new
            {
                projectId = resultProjectId,
                proposalId = proposal.Id,
                applied = true,
                files = new[]
                {
                    new
                    {
                        projectPath = "docs/guide.md",
                        resultHash
                    }
                }
            });

            await new RepoPatchCompletion().ApplyAsync(
                _db,
                command,
                true,
                resultJson,
                null,
                TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expectedStatus, proposal.Status);
            if (wrongProject)
                Assert.Equal("ApplyResultInvalid", proposal.ConflictReason);
            else
                Assert.Equal(ProposalStatus.Accepted, proposal.Status);

            var expectedAction = wrongProject
                ? "proposal.patch.apply_conflicted"
                : "proposal.patch.applied";
            Assert.Contains(await _db.AuditEvents.Select(item => item.Action).ToListAsync(
                TestContext.Current.CancellationToken), action => action == expectedAction);
        }
    }
}
