using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Infrastructure.Audit;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Domain.Entities.Content;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class InboxRenderTests : BUnitRenderBase
{
    private (Guid UserId, Guid ProjectId) AuthenticateAsProjectOwner()
    {
        var user = DbContext.Users.Single();
        var project = DbContext.Projects.Single();
        var org = DbContext.Orgs.Single();

        Bunit.Services.AddSingleton<IAuditWriter, EfAuditWriter>();
        var auth = Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>();
        auth.TriggerAuthenticationStateChanged(
            "tester",
            Array.Empty<string>(),
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new Claim("isAdmin", "false"),
                new Claim("project_id", project.Id.ToString("D")),
                new Claim("org_id", org.Id.ToString("D"))
            },
            "TestAuth");

        return (user.Id, project.Id);
    }

    [Fact]
    public void Inbox_EmptyProject_RendersEmptyStateAndZeroCounts()
    {
        AuthenticateAsProjectOwner();

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();

        Assert.Contains("No proposals match these filters.", cut.Markup);
        Assert.Contains("Proposal inbox", cut.Markup);
        Assert.DoesNotContain("beacon-error-state", cut.Markup);
    }

    [Fact]
    public void Inbox_PendingAndConflictedProposals_RendersAttentionCountsAndPendingCard()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var pending = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.ProjectDescription, projectId,
            ProposalOperation.Update, 1, new string('a', 64),
            """{"description":"A clearer project goal"}""",
            "Clarifies the primary project goal.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-render-pending", DateTime.UtcNow);
        var conflicted = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.ProjectDescription, projectId,
            ProposalOperation.Update, 1, new string('b', 64),
            """{"description":"An outdated goal"}""",
            "This proposal was based on an older version.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-render-conflicted", DateTime.UtcNow);
        conflicted.MarkConflicted(2, "BaseRevisionChanged", DateTime.UtcNow.AddSeconds(1));

        DbContext.Proposals.AddRange(pending, conflicted);
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();

        Assert.Contains("Pending: 1", cut.Markup);
        Assert.Contains("Conflicted: 1", cut.Markup);
        Assert.Contains("ProjectDescription", cut.Markup);
        Assert.Contains("Clarifies the primary project goal.", cut.Markup);
        Assert.Contains("Payload to apply when accepted", cut.Markup);
    }
}
