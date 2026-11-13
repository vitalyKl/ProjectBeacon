using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Content;
using ProjectBeacon.Domain.Entities.Agents;
using ProjectBeacon.Domain.Entities.Devices;
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
    public void TaskDetail_HidesGitSyncActionFromProjectMembersWithoutAdminRole()
    {
        var task = DbContext.Tasks.First();
        var project = DbContext.Projects.Single();
        var owner = DbContext.ProjectMembers.Single(
            item => item.ProjectId == project.Id && item.Role == MemberRole.Owner);
        var member = ProjectBeacon.Domain.Entities.Identity.User.Create(
            "regular-member", "regular-member@example.test", "hash");
        DbContext.Users.Add(member);
        DbContext.ProjectMembers.Add(
            ProjectBeacon.Domain.Entities.Projects.ProjectMember.Create(project.Id, member.Id, MemberRole.Member));

        var device = DbContext.Set<DaemonDevice>().Single();
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, "/workspace/project"));
        DbContext.WorkstationCommands.Add(WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            System.Text.Json.JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
            project.Id,
            owner.UserId));
        DbContext.SaveChanges();

        var auth = Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>();
        auth.TriggerAuthenticationStateChanged(
            "regular-member",
            Array.Empty<string>(),
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, member.Id.ToString("D")),
                new Claim("isAdmin", "false"),
                new Claim("project_id", project.Id.ToString("D")),
                new Claim("org_id", project.OrgId.ToString("D"))
            },
            "TestAuth");

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        Assert.DoesNotContain("task-repository-sync", cut.Markup);
        Assert.Contains("The Git operation is still running. This page will keep checking its status automatically.", cut.Markup);
    }

    [Fact]
    public void TaskDetail_RestoresPendingGitSyncCommandAfterPageReload()
    {
        var task = DbContext.Tasks.First();
        var project = DbContext.Projects.Single();
        var user = DbContext.Users.Single();

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

        var device = DbContext.Set<DaemonDevice>().Single();
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, "/workspace/project"));
        DbContext.WorkstationCommands.Add(WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            System.Text.Json.JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
            project.Id,
            user.Id));
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        Assert.Contains("task-repository-sync", cut.Markup);
        Assert.Contains("The Git operation is still running. This page will keep checking its status automatically.", cut.Markup);
    }

    [Fact]
    public void TaskDetail_DoesNotBlockPipelineForGitSyncOnAnotherWorkstation()
    {
        var task = DbContext.Tasks.First();
        var project = DbContext.Projects.Single();
        var user = DbContext.Users.Single();

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

        var plannerBackend = LocalModelBackend.Create(
            "task-detail-planner-other-sync",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            user.Id);
        DbContext.LocalModelBackends.Add(plannerBackend);
        DbContext.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, plannerBackend.Id, project.Id));

        var selectedDevice = DbContext.Set<DaemonDevice>().Single();
        selectedDevice.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, selectedDevice.Id, "/workspace/selected"));

        var otherUser = ProjectBeacon.Domain.Entities.Identity.User.Create(
            "other-workstation", "other-workstation@example.test", "hash");
        var otherDevice = DaemonDevice.Create(
            "other-workstation",
            otherUser.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        otherDevice.Heartbeat(
            """{"capabilities":["git-sync-v1"]}""",
            """{"agentRuntime":"external"}""");
        DbContext.Users.Add(otherUser);
        DbContext.DaemonDevices.Add(otherDevice);
        DbContext.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, otherDevice.Id, "/workspace/other"));
        var command = WorkstationCommand.Create(
            otherDevice.Id,
            WorkstationCommandKind.GitSync,
            System.Text.Json.JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
            project.Id,
            user.Id);
        command.Claim(TimeSpan.FromMinutes(5), "other-workstation-instance");
        DbContext.WorkstationCommands.Add(command);
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        Assert.Contains("task-repository-sync", cut.Markup);
        Assert.False(cut.Find("[data-testid='task-pipeline-start']").HasAttribute("disabled"));
    }

    [Fact]
    public void TaskDetail_ShowsBehindAndDirtyWarningsWithoutBlocking()
    {
        var task = DbContext.Tasks.First();
        var project = DbContext.Projects.Single();
        var user = DbContext.Users.Single();
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

        var plannerBackend = LocalModelBackend.Create(
            "task-detail-planner",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            user.Id);
        DbContext.LocalModelBackends.Add(plannerBackend);
        DbContext.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, plannerBackend.Id, project.Id));

        var device = DbContext.Set<DaemonDevice>().Single();
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, "/workspace/project"));
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            System.Text.Json.JsonSerializer.Serialize(new { projectId = project.Id, mode = "Status" }),
            project.Id,
            user.Id);
        command.Succeed(
            """{"branch":"feature/task-work","upstream":"origin/feature/task-work","ahead":0,"behind":3,"clean":false,"detached":false,"hasUpstream":true,"diverged":false,"headBefore":"0123456789abcdef0123456789abcdef01234567","headAfter":"0123456789abcdef0123456789abcdef01234567","outcome":"UpToDate","reasonCode":null,"stderr":null}""");
        DbContext.WorkstationCommands.Add(command);
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        Assert.Contains("repository-behind-warning", cut.Markup);
        Assert.Contains("3 commit(s) behind", cut.Markup);
        Assert.Contains("Local changes are present.", cut.Markup);
        Assert.Contains("feature/task-work", cut.Markup);
        Assert.Contains("href=\"/project/settings\"", cut.Markup);
        Assert.Contains("task-repository-sync", cut.Markup);
        Assert.Contains("Start task", cut.Markup);
        Assert.False(cut.Find("[data-testid='task-pipeline-start']").HasAttribute("disabled"));
    }

    [Fact]
    public void TaskDetail_ShowsNonBlockingWarningBeforeFirstGitSync()
    {
        var task = DbContext.Tasks.First();
        var project = DbContext.Projects.Single();
        var user = DbContext.Users.Single();
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

        var plannerBackend = LocalModelBackend.Create(
            "task-detail-first-sync-planner",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            user.Id);
        DbContext.LocalModelBackends.Add(plannerBackend);
        DbContext.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, plannerBackend.Id, project.Id));

        var device = DbContext.Set<DaemonDevice>().Single();
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, "/workspace/project"));
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        Assert.Contains("repository-sync-reason", cut.Markup);
        Assert.Contains("This repository has not been checked yet.", cut.Markup);
        Assert.Contains("task-repository-sync", cut.Markup);
        Assert.False(cut.Find("[data-testid='task-pipeline-start']").HasAttribute("disabled"));
    }

    [Fact]
    public void TaskDetail_ShowsNoUpstreamReasonWhenRepositoryIsNotBehind()
    {
        var task = DbContext.Tasks.First();
        var project = DbContext.Projects.Single();
        var user = DbContext.Users.Single();
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

        var device = DbContext.Set<DaemonDevice>().Single();
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, "/workspace/project"));
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            System.Text.Json.JsonSerializer.Serialize(new { projectId = project.Id, mode = "Status" }),
            project.Id,
            user.Id);
        var head = "0123456789abcdef0123456789abcdef01234567";
        command.Succeed(
            $$"""{"branch":"main","upstream":null,"ahead":0,"behind":0,"clean":true,"detached":false,"hasUpstream":false,"diverged":false,"headBefore":"{{head}}","headAfter":"{{head}}","outcome":"UpToDate","reasonCode":null,"stderr":null}""");
        DbContext.WorkstationCommands.Add(command);
        DbContext.SaveChanges();

        var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(
            builder => builder.Add(component => component.TaskId, task.Id));

        Assert.Contains("repository-sync-reason", cut.Markup);
        Assert.Contains("The current branch has no configured upstream.", cut.Markup);
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
        var project = DbContext.Projects.Single();
        var task = ProjectBeacon.Domain.Entities.Tasks.TaskItem.Create("Task history restore", project.Id);
        DbContext.Tasks.Add(task);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        var user = DbContext.Users.Single();
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
        cut.Find("[data-testid='task-history-panel-4']");
    }

}
