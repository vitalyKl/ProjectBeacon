using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using MudBlazor.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
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
        DbContext.ContentVersions.Add(ContentVersion.Create(
            projectId,
            ContentTargetKind.ProjectDescription,
            projectId,
            1,
            """{"description":"The old project goal"}""",
            "Human",
            userId.ToString("D")));
        DbContext.SaveChanges();

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
        Assert.Contains("Human", cut.Markup);
        Assert.Contains("—", cut.Markup);

        cut.FindAll(".mud-expand-panel-header").First(header =>
            header.TextContent.Contains("Proposal details", StringComparison.OrdinalIgnoreCase)).Click();
        cut.Find("[data-testid='compare-base-revision'] button").Click();

        Assert.Equal("The old project goal",
            cut.Find(".proposal-diff-line.is-removed code").TextContent.Trim());
        Assert.Equal("A clearer project goal",
            cut.Find(".proposal-diff-line.is-added code").TextContent.Trim());
        Assert.Contains("Line-by-line content diff", cut.Markup);
    }

    [Fact]
    public async Task Inbox_BatchAcceptance_RefreshesConflictsAndShowsEachItemResult()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var baseSnapshot = """{"description":null}""";
        var baseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseSnapshot))).ToLowerInvariant();
        DbContext.ContentVersions.Add(ContentVersion.Create(
            projectId,
            ContentTargetKind.ProjectDescription,
            projectId,
            1,
            baseSnapshot,
            "Human",
            userId.ToString("D")));

        var conflicted = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.ProjectDescription, projectId,
            ProposalOperation.Update, 1, new string('a', 64),
            """{"description":"This base is stale"}""",
            "Proposes content based on a stale snapshot.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-batch-conflict-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        var accepted = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.ProjectDescription, projectId,
            ProposalOperation.Update, 1, baseHash,
            """{"description":"The accepted project goal"}""",
            "Clarifies the project goal.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-batch-accepted-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        DbContext.Proposals.AddRange(conflicted, accepted);
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();
        var checkboxes = cut.FindComponents<MudBlazor.MudCheckBox<bool>>();
        Assert.Equal(2, checkboxes.Count);

        await cut.InvokeAsync(async () =>
        {
            foreach (var checkbox in checkboxes)
                await checkbox.Instance.ValueChanged.InvokeAsync(true);
        });

        cut.FindAll("button").First(button =>
            button.TextContent.Contains("Accept selected", StringComparison.OrdinalIgnoreCase)).Click();

        Assert.Contains("Conflicted: 1", cut.Markup);
        Assert.Contains("ProjectDescription " + conflicted.Id.ToString("N")[..8], cut.Markup);
        Assert.Contains("ProjectDescription " + accepted.Id.ToString("N")[..8], cut.Markup);
        Assert.Contains("Conflicted", cut.Markup);
        Assert.Contains("Proposal accepted", cut.Markup, StringComparison.OrdinalIgnoreCase);

        var savedProject = await DbContext.Projects.SingleAsync(
            project => project.Id == projectId, TestContext.Current.CancellationToken);
        await DbContext.Entry(savedProject).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("The accepted project goal", savedProject.Description);

        var savedConflict = await DbContext.Proposals.SingleAsync(
            proposal => proposal.Id == conflicted.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Conflicted, savedConflict.Status);
        var savedAcceptance = await DbContext.Proposals.SingleAsync(
            proposal => proposal.Id == accepted.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Accepted, savedAcceptance.Status);
    }

    [Fact]
    public async Task Inbox_AcceptedProposal_RendersAppliedRevisionAndAcceptedPayload()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var now = DateTime.UtcNow;
        const string acceptedPayload = """{"description":"The accepted project goal"}""";
        DbContext.ContentVersions.Add(ContentVersion.Create(
            projectId,
            ContentTargetKind.ProjectDescription,
            projectId,
            1,
            """{"description":"The previous project goal"}""",
            "Human",
            userId.ToString("D")));
        var accepted = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.ProjectDescription, projectId,
            ProposalOperation.Update, 1, new string('c', 64),
            acceptedPayload,
            "Clarifies the agreed project goal.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-render-accepted", now);
        accepted.AutoAccept(2, acceptedPayload, now);
        DbContext.Proposals.Add(accepted);
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();
        await cut.InvokeAsync(() => cut.FindComponents<MudBlazor.MudSelect<string>>()[0].Instance.ValueChanged.InvokeAsync(string.Empty));

        Assert.Contains("Accepted", cut.Markup);
        Assert.Contains("Applied revision: 2", cut.Markup);
        cut.FindAll(".mud-expand-panel-header").First(header =>
            header.TextContent.Contains("Proposal details", StringComparison.OrdinalIgnoreCase)).Click();
        Assert.Contains("The accepted project goal", cut.Markup);
        Assert.Contains("Clarifies the agreed project goal.", cut.Markup);
        var acceptedPayloadField = cut.FindComponents<MudBlazor.MudTextField<string>>()
            .Single(field => field.Instance.Label == "Payload to apply when accepted (JSON)");
        Assert.True(acceptedPayloadField.Instance.ReadOnly);
        Assert.Equal(acceptedPayload, acceptedPayloadField.Instance.GetState(state => state.Value));
    }

}
