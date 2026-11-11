using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Context;
using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class DeletedContentRenderTests : BUnitRenderBase
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
    public async Task ProjectSettings_ShowsDescriptionHistoryAndRestoresNullableDescription()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var project = DbContext.Projects.Single(item => item.Id == projectId);
        var writer = new ContentWriter();

        writer.AddVersion(DbContext, project, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);
        var originalRevision = project.Revision;

        project.Update(description: "Current project description.");
        var saved = await writer.SaveAsync(
            DbContext, project, "Human", userId.ToString("D"), CancellationToken.None);
        Assert.True(saved.Success, saved.Error);
        Assert.Equal(originalRevision + 1, saved.Value);

        DbContext.ChangeTracker.Clear();
        var cut = Bunit.RenderComponent<Features.Projects.ProjectManage>();

        cut.FindAll("button").First(button =>
            button.TextContent.Contains("Content history", StringComparison.OrdinalIgnoreCase)).Click();

        Assert.Contains("Content history (2)", cut.Markup);
        Assert.Contains("Current project description.", cut.Markup);
        cut.Find($"[data-testid='restore-project-description-revision-{originalRevision}']").Click();

        Assert.Contains("Content history", cut.Markup);
        var restored = await DbContext.Projects.AsNoTracking().SingleAsync(
            item => item.Id == projectId, CancellationToken.None);
        Assert.Null(restored.Description);
        Assert.Equal(originalRevision + 2, restored.Revision);
    }

    [Fact]
    public async Task Context_ShowsDeletedSectionAndRestoresItFromHistory()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var section = ContextSection.Create(
            "architecture",
            "Deleted architecture section",
            "Architecture content from the prior approved revision.",
            projectId,
            ContextScopeType.Project,
            key: "architecture");
        section.MarkReviewed(userId.ToString("D"));
        var sourceRevision = section.Revision;

        var writer = new ContentWriter();
        DbContext.ContextSections.Add(section);
        writer.AddVersion(DbContext, section, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);

        section.Delete();
        writer.AddVersion(
            DbContext, section, "Human", userId.ToString("D"), deleted: true);
        DbContext.ContextSections.Remove(section);
        await DbContext.SaveChangesAsync(CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        var cut = Bunit.RenderComponent<Features.Context.Context>();

        Assert.Contains("Deleted content (1)", cut.Markup);
        Assert.Contains("Deleted architecture section", cut.Markup);
        Assert.Contains("Architecture content from the prior approved revision.", cut.Markup);

        cut.FindAll("button").First(button =>
            button.TextContent.Contains("Restore", StringComparison.OrdinalIgnoreCase)).Click();

        Assert.Contains("Deleted architecture section", cut.Markup);
        Assert.Contains("architecture", cut.Markup);
        Assert.DoesNotContain("Deleted content (1)", cut.Markup);

        var restored = await DbContext.ContextSections.AsNoTracking().SingleAsync(
            item => item.Id == section.Id,
            CancellationToken.None);
        Assert.Equal("Deleted architecture section", restored.Title);
        Assert.Equal(ContextReviewState.Reviewed, restored.ReviewState);
        Assert.Equal(sourceRevision + 2, restored.Revision);
    }

    [Fact]
    public async Task Context_OpensConstraintHistoryAndRestoresPriorLifecycleStatus()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var constraint = Constraint.Create(
            "Must retain audit history", ConstraintKind.Must, projectId, "src/");
        DbContext.Constraints.Add(constraint);
        var writer = new ContentWriter();
        writer.AddVersion(DbContext, constraint, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);
        var proposedRevision = constraint.Revision;

        constraint.Activate();
        var activated = await writer.SaveAsync(
            DbContext, constraint, "Human", userId.ToString("D"), CancellationToken.None);
        Assert.True(activated.Success, activated.Error);
        var activeVersion = await DbContext.ContentVersions.SingleAsync(
            item => item.TargetKind == ContentTargetKind.Constraint
                && item.TargetId == constraint.Id
                && item.Revision == constraint.Revision,
            CancellationToken.None);

        constraint.Reject();
        var rejected = await writer.SaveAsync(
            DbContext, constraint, "Human", userId.ToString("D"), CancellationToken.None);
        Assert.True(rejected.Success, rejected.Error);
        Assert.Equal(ConstraintStatus.Rejected, constraint.Status);

        DbContext.ChangeTracker.Clear();
        var cut = Bunit.RenderComponent<Features.Context.Context>();

        cut.FindAll("button").First(button =>
            button.TextContent.Contains("Content history", StringComparison.OrdinalIgnoreCase)).Click();
        cut.FindAll(".mud-expand-panel-header").First(header =>
            header.TextContent.Contains("Content history:", StringComparison.OrdinalIgnoreCase)).Click();
        var constraintRestoreButtons = cut.FindAll("button")
            .Where(button => button.TextContent.Contains("Restore", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(3, constraintRestoreButtons.Length);
        constraintRestoreButtons[1].Click();

        var restored = await DbContext.Constraints.AsNoTracking().SingleAsync(
            item => item.Id == constraint.Id,
            CancellationToken.None);
        Assert.Equal(ConstraintStatus.Active, restored.Status);
        Assert.Equal(constraint.Revision + 1, restored.Revision);
        Assert.Equal("Must retain audit history", restored.Body);
        Assert.True(proposedRevision < activeVersion.Revision);
    }

    [Fact]
    public async Task Roadmap_ShowsMilestoneHistoryAndRestoresAnEarlierRevision()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var milestone = Milestone.Create(
            "Original milestone",
            "Original milestone description.",
            projectId,
            1);
        var writer = new ContentWriter();
        DbContext.Milestones.Add(milestone);
        writer.AddVersion(DbContext, milestone, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);
        var originalRevision = milestone.Revision;

        milestone.Update("Updated milestone", "Updated milestone description.", 4);
        var updated = await writer.SaveAsync(
            DbContext, milestone, "Human", userId.ToString("D"), CancellationToken.None);
        Assert.True(updated.Success, updated.Error);
        Assert.Equal(originalRevision + 1, updated.Value);

        DbContext.ChangeTracker.Clear();
        var cut = Bunit.RenderComponent<Features.Roadmap.Roadmap>();

        cut.Find($"[data-testid='open-milestone-{milestone.Id:D}']").Click();
        Assert.Contains("Content history (2)", cut.Markup);
        Assert.Contains("Updated milestone", cut.Markup);

        cut.FindAll(".mud-expand-panel-header").First(header =>
            header.TextContent.Contains("Content history", StringComparison.OrdinalIgnoreCase)).Click();
        Assert.Contains("Original milestone description.", cut.Markup);

        cut.Find($"[data-testid='restore-milestone-revision-{originalRevision}']").Click();

        Assert.Contains("Original milestone", cut.Markup);
        var restored = await DbContext.Milestones.AsNoTracking().SingleAsync(
            item => item.Id == milestone.Id, CancellationToken.None);
        Assert.Equal("Original milestone", restored.Name);
        Assert.Equal("Original milestone description.", restored.Description);
        Assert.Equal(1, restored.Order);
        Assert.Equal(originalRevision + 2, restored.Revision);
    }

    [Fact]
    public async Task Roadmap_ShowsDeletedMilestoneAndRestoresItFromHistory()
    {
        var (userId, projectId) = AuthenticateAsProjectOwner();
        var milestone = Milestone.Create(
            "Deleted Alpha milestone",
            "Original milestone description.",
            projectId,
            4);

        var writer = new ContentWriter();
        DbContext.Milestones.Add(milestone);
        writer.AddVersion(DbContext, milestone, "Human", userId.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);

        milestone.Delete();
        writer.AddVersion(
            DbContext, milestone, "Human", userId.ToString("D"), deleted: true);
        DbContext.Milestones.Remove(milestone);
        await DbContext.SaveChangesAsync(CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        var cut = Bunit.RenderComponent<Features.Roadmap.Roadmap>();

        Assert.Contains("Deleted content (1)", cut.Markup);
        Assert.Contains("Deleted Alpha milestone", cut.Markup);
        Assert.Contains("Original milestone description.", cut.Markup);

        cut.FindAll("button").First(button =>
            button.TextContent.Contains("Restore", StringComparison.OrdinalIgnoreCase)).Click();

        Assert.Contains("Deleted Alpha milestone", cut.Markup);
        Assert.DoesNotContain("Deleted content (1)", cut.Markup);

        var restored = await DbContext.Milestones.AsNoTracking().SingleAsync(
            item => item.Id == milestone.Id,
            CancellationToken.None);
        Assert.Equal("Deleted Alpha milestone", restored.Name);
        Assert.Equal("Original milestone description.", restored.Description);
        Assert.Equal(4, restored.Order);
        Assert.Equal(3, restored.Revision);
    }
}
