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
        Assert.Equal(3, restored.Revision);
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
