namespace ProjectBeacon.API.Tests;

using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Proposals;
using Domain.Entities.Content;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Audit;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Exercises proposal quotas against PostgreSQL itself. SQLite tests cover the quota
/// boundaries; this test verifies that Serializable transactions prevent two concurrent
/// submissions from consuming the same final project-wide slot.
/// </summary>
[Collection("postgres-serial")]
public sealed class ProposalQuotaPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public ProposalQuotaPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ConcurrentHumanSubmissions_CannotExceedProjectPendingQuota()
    {
        _postgres.RequirePostgres();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        Guid projectId;
        Guid userId;
        Guid orgId;

        await using (var seed = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var org = Org.Create("Proposal quota race " + Guid.NewGuid().ToString("N"));
                var user = User.Create(
                    "proposal-quota-" + Guid.NewGuid().ToString("N")[..10],
                    "proposal-quota-" + Guid.NewGuid().ToString("N") + "@beacon.local",
                    "test-password-hash");
                seed.Orgs.Add(org);
                seed.Users.Add(user);
                await seed.SaveChangesAsync(ct);

                var project = Project.Create("Proposal quota race project", "Initial description", org.Id);
                seed.Projects.Add(project);
                seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
                await seed.SaveChangesAsync(ct);

                new ContentWriter().AddVersion(seed, project, "Human", user.Id.ToString("D"));

                var occupiedSlots = Enumerable.Range(0, 199)
                    .Select(index => Proposal.Create(
                        project.Id,
                        null,
                        null,
                        null,
                        ContentTargetKind.ProjectDescription,
                        project.Id,
                        ProposalOperation.Update,
                        1,
                        new string('a', 64),
                        """{"description":"Existing pending proposal"}""",
                        "Occupies a project pending-review slot.",
                        ProposalAuthorKind.Human,
                        user.Id,
                        null,
                        null,
                        "Human",
                        "postgres-quota-seed-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        now))
                    .ToArray();

                seed.Proposals.AddRange(occupiedSlots);
                await seed.SaveChangesAsync(ct);

                projectId = project.Id;
                userId = user.Id;
                orgId = org.Id;
            }
        }

        // Match the production provider configuration so a PostgreSQL serialization
        // failure retries the whole handler transaction and re-evaluates the quota.
        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(
                _postgres.ConnectionString,
                npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
            .Options;
        var factory = new BeaconDbFactory(options, null);
        var actor = new ActorContext(
            ActorType.Human,
            userId,
            false,
            null,
            null,
            projectId,
            orgId,
            ApiTokenCapability.None);

        async Task<Result<ProposalDto>> SubmitAsync(string key, string description)
        {
            var request = new CreateProposalRequest(
                ContentTargetKind.ProjectDescription,
                projectId,
                ProposalOperation.Update,
                1,
                System.Text.Json.JsonSerializer.Serialize(new { description }),
                "Competes for the final pending project slot.",
                key);

            return await new CreateProposalHandler(factory, new EfAuditWriter())
                .HandleAsync(new CreateProposalCommand(projectId, request, actor), ct);
        }

        Result<ProposalDto>[] results;
        using (TenantScope.EnterUnscoped())
        {
            results = await Task.WhenAll(
                SubmitAsync("postgres-quota-race-a", "Concurrent description A"),
                SubmitAsync("postgres-quota-race-b", "Concurrent description B"));
        }

        Assert.Single(results.Where(result => result.Success));
        var rejected = Assert.Single(results.Where(result => !result.Success));
        Assert.True(
            rejected.Kind == ErrorKind.Conflict
                || rejected.Error?.Contains("limit of 200 pending", StringComparison.OrdinalIgnoreCase) == true,
            "The losing submission must report a quota or serialization conflict. Actual error: " + rejected.Error);

        await using var verify = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var activePending = await verify.Proposals.CountAsync(proposal =>
                proposal.ProjectId == projectId
                && proposal.Status == ProposalStatus.Pending
                && proposal.ExpiresAt > DateTime.UtcNow, ct);
            Assert.Equal(200, activePending);
        }
    }
}
