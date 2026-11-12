using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Content;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class TaskDetailRenderTests : BUnitRenderBase
{
    [Fact]
    public void TaskDetail_RendersWithTaskInfo()
    {
        // Arrange
        var task = DbContext.Tasks.First();

        // Act
        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(builder => builder.Add(c => c.TaskId, task.Id));

        // Assert
        var html = cut.Markup;
        Assert.Contains(task.Title, html);
        Assert.Contains("beacon-task-hero", html);
        Assert.Contains("beacon-task-grid", html);
        Assert.Contains("beacon-state", html);
        Assert.Contains("Start task", html);
    }

    [Fact]
    public void TaskDetail_RendersTaskTitle()
    {
        // Arrange
        var task = DbContext.Tasks.First();

        // Act
        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(builder => builder.Add(c => c.TaskId, task.Id));

        // Assert
        var html = cut.Markup;
        Assert.Contains(task.Title, html);
    }

    [Fact]
    public void TaskDetail_ExposesTaskProposalsTab()
    {
        var task = DbContext.Tasks.First();

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        Assert.Contains("Proposals", cut.Markup);
    }

    [Fact]
    public void TaskDetail_RendersPageStructure()
    {
        // Arrange
        var task = DbContext.Tasks.First();

        // Act
        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(builder => builder.Add(c => c.TaskId, task.Id));

        // Assert
        var html = cut.Markup;
        Assert.Contains("task", html);
    }
    [Fact]
    public async Task TaskDetail_RestoresTaskDescriptionFromHistoryAsANewRevision()
    {
        var task = DbContext.Tasks.First();
        var user = DbContext.Users.Single();
        var project = DbContext.Projects.Single();
        var auth = Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>();
        auth.TriggerAuthenticationStateChanged(
            "tester",
            Array.Empty<string>(),
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new Claim("isAdmin", "false"),
                new Claim("project_id", project.Id.ToString("D")),
                new Claim("org_id", project.OrgId.ToString("D"))
            },
            "TestAuth");

        var writer = new ContentWriter();
        task.Update(description: "Original task goal");
        writer.AddVersion(DbContext, task, "Human", user.Id.ToString("D"));
        await DbContext.SaveChangesAsync(CancellationToken.None);

        task.Update(description: "Current task goal");
        var saved = await writer.SaveAsync(
            DbContext, task, "Human", user.Id.ToString("D"), CancellationToken.None);
        Assert.True(saved.Success, saved.Error);
        Assert.Equal(3, saved.Value);

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        cut.Find("[data-testid='task-content-history']").Click();

        Assert.Contains("Current task goal", cut.Markup);
        Assert.DoesNotContain("Original task goal", cut.Markup);
        cut.Find("[data-testid='task-history-panel-2'] .mud-expand-panel-header").Click();

        Assert.Contains("Original task goal", cut.Markup);
        Assert.Contains("restore-task-revision-2", cut.Markup);
        cut.Find("[data-testid='restore-task-revision-2']").Click();

        await DbContext.Entry(task).ReloadAsync(CancellationToken.None);
        Assert.Equal("Original task goal", task.Description);
        Assert.Equal(4, task.ContentRevision);

        var history = await DbContext.ContentVersions
            .Where(version => version.TargetKind == ContentTargetKind.TaskDescription
                && version.TargetId == task.Id)
            .OrderBy(version => version.Revision)
            .ToListAsync(CancellationToken.None);
        Assert.Equal(new[] { 2, 3, 4 }, history.Select(version => version.Revision).ToArray());
        Assert.Contains("Original task goal", cut.Markup);
        Assert.Contains("[data-testid=\u0027task-history-panel-4\u0027]", cut.Markup);
    }

}
