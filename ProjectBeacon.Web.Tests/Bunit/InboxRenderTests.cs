using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using ProjectBeacon.Application.Persistence;
using MudBlazor.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Domain.Entities.Content;
using ProjectBeacon.Domain.Entities.Context;
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
    public void Inbox_StructuredTargetDiff_ShowsEveryFieldIncludingClearedAndNumericValues()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var task = DbContext.Tasks.Single(item => item.Title == "Task Three");
        const string taskBase = """{"description":"Current task description","reviewNotes":"Existing review notes"}""";
        const string taskProposal = """{"description":null,"reviewNotes":"Updated review guidance"}""";
        DbContext.ContentVersions.Add(ContentVersion.Create(
            projectId, ContentTargetKind.TaskDescription, task.Id, 1,
            taskBase, "Human", userId.ToString("D")));
        var taskProposalRecord = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.TaskDescription, task.Id,
            ProposalOperation.Update, 1, new string('c', 64),
            taskProposal, "Updates the task description and review guidance.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-structured-task-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);

        var milestone = ProjectBeacon.Domain.Entities.Roadmap.Milestone.Create(
            "Alpha release", "Earlier roadmap note", projectId, 2);
        DbContext.Milestones.Add(milestone);
        const string milestoneBase = """{"name":"Alpha release","description":"Earlier roadmap note","order":2}""";
        const string milestoneProposal = """{"name":"Alpha release","description":null,"order":7}""";
        DbContext.ContentVersions.Add(ContentVersion.Create(
            projectId, ContentTargetKind.Milestone, milestone.Id, 1,
            milestoneBase, "Human", userId.ToString("D")));
        var milestoneProposalRecord = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.Milestone, milestone.Id,
            ProposalOperation.Update, 1, new string('d', 64),
            milestoneProposal, "Moves the milestone to the next roadmap position.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-structured-milestone-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);

        DbContext.Proposals.AddRange(taskProposalRecord, milestoneProposalRecord);
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();
        Assert.Equal(2, cut.FindAll("[data-testid='compare-base-revision'] button").Count);

        cut.FindAll("[data-testid='compare-base-revision'] button").First().Click();
        cut.FindAll("[data-testid='compare-base-revision'] button").Skip(1).First().Click();

        Assert.Contains("reviewNotes", cut.Markup);
        Assert.Contains("Existing review notes", cut.Markup);
        Assert.Contains("Updated review guidance", cut.Markup);
        Assert.Contains("(cleared)", cut.Markup);
        Assert.Contains("order", cut.Markup);
        Assert.Contains(">7<", cut.Markup);
        Assert.Contains("Earlier roadmap note", cut.Markup);
        Assert.Contains("Moves the milestone to the next roadmap position.", cut.Markup);
    }

    [Fact]
    public async Task Inbox_ContextSectionHeadingEdits_RenderResolvedMarkdownDiff()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var section = ContextSection.Create(
            "architecture",
            "Architecture",
            "# Architecture\n\nOverview.\n\n## Security\n\nOld security details.\n\n## Operations\n\nOperational details.",
            projectId,
            ContextScopeType.Project,
            key: "architecture");
        section.MarkReviewed(userId.ToString("D"));
        DbContext.ContextSections.Add(section);
        var writer = new ContentWriter();
        writer.AddVersion(DbContext, section, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);

        var baseVersion = await DbContext.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id,
            CancellationToken.None);
        var baseHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(baseVersion.ContentJson))).ToLowerInvariant();
        var proposal = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.ContextSection, section.Id,
            ProposalOperation.Update, section.Revision, baseHash,
            """
            {
              "edits": [
                {
                  "heading": "## Security",
                  "op": "replace",
                  "content": "New security details."
                }
              ]
            }
            """,
            "Refreshes only the security subsection.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-context-heading-edit-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        DbContext.Proposals.Add(proposal);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();
        cut.FindAll(".mud-expand-panel-header").First(header =>
            header.TextContent.Contains("Proposal details", StringComparison.OrdinalIgnoreCase)).Click();
        cut.Find("[data-testid='compare-base-revision'] button").Click();

        var removedLines = string.Join(
            "\\n", cut.FindAll(".proposal-diff-line.is-removed").Select(line => line.TextContent));
        var addedLines = string.Join(
            "\\n", cut.FindAll(".proposal-diff-line.is-added").Select(line => line.TextContent));
        Assert.Contains("Old security details.", removedLines);
        Assert.Contains("New security details.", addedLines);
        Assert.Contains("## Security", cut.Markup);
        Assert.Contains("## Operations", cut.Markup);
        Assert.DoesNotContain("**bodyMarkdown**", cut.Markup);
    }

    [Fact]
    public async Task Inbox_ContextSectionFullUpdate_RendersTitleAndBodyChangesWithoutJsonFieldNames()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var section = ContextSection.Create(
            "operations",
            "Current operations",
            "Existing runbook text.",
            projectId,
            ContextScopeType.Project,
            key: "operations");
        section.MarkReviewed(userId.ToString("D"));
        DbContext.ContextSections.Add(section);
        new ContentWriter().AddVersion(
            DbContext, section, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);

        var baseVersion = await DbContext.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id,
            CancellationToken.None);
        var baseHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(baseVersion.ContentJson))).ToLowerInvariant();
        var proposal = Proposal.Create(
            projectId, null, null, null,
            ContentTargetKind.ContextSection, section.Id,
            ProposalOperation.Update, section.Revision, baseHash,
            """{"title":"Updated operations","bodyMarkdown":"Updated runbook text."}""",
            "Updates the operations title and runbook.",
            ProposalAuthorKind.Human, userId, null, null, "Human",
            "inbox-context-title-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        DbContext.Proposals.Add(proposal);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();
        cut.FindAll(".mud-expand-panel-header").First(header =>
            header.TextContent.Contains("Proposal details", StringComparison.OrdinalIgnoreCase)).Click();
        cut.Find("[data-testid='compare-base-revision'] button").Click();

        var removed = string.Join(
            "\\n", cut.FindAll(".proposal-diff-line.is-removed").Select(line => line.TextContent));
        var added = string.Join(
            "\\n", cut.FindAll(".proposal-diff-line.is-added").Select(line => line.TextContent));
        Assert.Contains("Current operations", removed);
        Assert.Contains("Existing runbook text.", removed);
        Assert.Contains("Updated operations", added);
        Assert.Contains("Updated runbook text.", added);
        Assert.DoesNotContain("**bodyMarkdown**", cut.Markup);
        Assert.DoesNotContain("**title**", cut.Markup);
    }

    [Fact]
    public async Task Inbox_DeleteProposal_AcceptsWithoutSendingEditedPayload()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var section = ContextSection.Create(
            "architecture",
            "Obsolete architecture",
            "This section is no longer applicable.",
            projectId,
            ContextScopeType.Project,
            key: "architecture");
        section.MarkReviewed(userId.ToString("D"));
        var baseRevision = section.Revision;
        DbContext.ContextSections.Add(section);
        new ContentWriter().AddVersion(
            DbContext, section, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);
        var baseVersion = await DbContext.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id,
            CancellationToken.None);
        var baseHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(baseVersion.ContentJson))).ToLowerInvariant();

        var proposal = Proposal.Create(
            projectId,
            null,
            null,
            null,
            ContentTargetKind.ContextSection,
            section.Id,
            ProposalOperation.Delete,
            section.Revision,
            baseHash,
            "{}",
            "Removes an obsolete reviewed section.",
            ProposalAuthorKind.Human,
            userId,
            null,
            null,
            "Human",
            "inbox-delete-" + Guid.NewGuid().ToString("N"),
            DateTime.UtcNow);
        DbContext.Proposals.Add(proposal);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        var cut = Bunit.RenderComponent<Features.Proposals.Inbox>();
        cut.FindAll(".mud-expand-panel-header").First(header =>
            header.TextContent.Contains("Proposal details", StringComparison.OrdinalIgnoreCase)).Click();
        var deletePayload = cut.FindComponents<MudBlazor.MudTextField<string>>()
            .Single(field => field.Instance.Label == "Payload to apply when accepted (JSON)");
        Assert.True(deletePayload.Instance.ReadOnly);

        cut.FindAll("button").First(button =>
            string.Equals(button.TextContent.Trim(), "Accept", StringComparison.OrdinalIgnoreCase)).Click();

        var savedProposal = await DbContext.Proposals.SingleAsync(
            item => item.Id == proposal.Id, CancellationToken.None);
        await DbContext.Entry(savedProposal).ReloadAsync(CancellationToken.None);
        Assert.Equal(ProposalStatus.Accepted, savedProposal.Status);
        Assert.False(await DbContext.ContextSections.AsNoTracking().AnyAsync(
            item => item.Id == section.Id, CancellationToken.None));
        var history = await DbContext.ContentVersions
            .Where(item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id)
            .OrderBy(item => item.Revision)
            .ToListAsync(CancellationToken.None);
        Assert.Equal(new[] { baseRevision, baseRevision + 1 }, history.Select(item => item.Revision).ToArray());
        Assert.Equal(proposal.Id, history[^1].ProposalId);
        using var tombstone = System.Text.Json.JsonDocument.Parse(history[^1].ContentJson);
        Assert.True(tombstone.RootElement.GetProperty("deleted").GetBoolean());
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

        var savedProject = await DbContext.Projects.SingleAsync(
            project => project.Id == projectId, CancellationToken.None);
        await DbContext.Entry(savedProject).ReloadAsync(CancellationToken.None);
        Assert.Equal("The accepted project goal", savedProject.Description);

        var savedConflict = await DbContext.Proposals.SingleAsync(
            proposal => proposal.Id == conflicted.Id, CancellationToken.None);
        await DbContext.Entry(savedConflict).ReloadAsync(CancellationToken.None);
        Assert.Equal(ProposalStatus.Conflicted, savedConflict.Status);
        var savedAcceptance = await DbContext.Proposals.SingleAsync(
            proposal => proposal.Id == accepted.Id, CancellationToken.None);
        await DbContext.Entry(savedAcceptance).ReloadAsync(CancellationToken.None);
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
