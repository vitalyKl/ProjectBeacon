namespace ProjectBeacon.API.Tests;

using Application.Devices;
using Domain.Common;
using Domain.Entities.Context;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Http;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

[Collection("postgres-serial")]
public sealed class TenantIsolationPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public TenantIsolationPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ProjectScope_HidesOtherProjectTasks()
    {
        _postgres.RequirePostgres();

        Guid projectA;
        Guid projectB;
        Guid taskA;
        Guid taskB;

        await using (var seed = _postgres.CreateContext())
        {
            var orgA = Org.Create("Org A");
            var orgB = Org.Create("Org B");
            seed.Orgs.AddRange(orgA, orgB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var pA = Project.Create("Project A", null, orgA.Id);
            var pB = Project.Create("Project B", null, orgB.Id);
            seed.Projects.AddRange(pA, pB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var tA = TaskItem.Create("Task A", pA.Id, TaskPriority.Medium, TaskType.Task);
            var tB = TaskItem.Create("Task B", pB.Id, TaskPriority.Medium, TaskType.Task);
            seed.Tasks.AddRange(tA, tB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            projectA = pA.Id;
            projectB = pB.Id;
            taskA = tA.Id;
            taskB = tB.Id;
        }

        await using var db = _postgres.CreateContext();

        using (TenantScope.EnterProjectScope(projectA))
        {
            var visible = await db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains(taskA, visible);
            Assert.DoesNotContain(taskB, visible);
        }

        using (TenantScope.EnterProjectScope(projectB))
        {
            var visible = await db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains(taskB, visible);
            Assert.DoesNotContain(taskA, visible);
        }

        var none = await db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(taskA, none);
        Assert.DoesNotContain(taskB, none);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData(MemberRole.Owner)]
    [InlineData(MemberRole.Admin)]
    public async Task OrganizationOwnerOrAdmin_EntersProjectScopeAndReadsThroughPostgresRlsWithoutProjectMembership(
        MemberRole role)
    {
        _postgres.RequirePostgres();

        Guid orgId;
        Guid projectId;
        Guid userId;
        Guid sectionId;

        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("Inherited tenant RLS org " + Guid.NewGuid().ToString("N"));
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var project = Project.Create("Inherited tenant RLS project", null, org.Id);
            var unrelatedOrg = Org.Create("Unrelated tenant RLS org " + Guid.NewGuid().ToString("N"));
            seed.Orgs.Add(unrelatedOrg);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var unrelatedProject = Project.Create("Unrelated tenant RLS project", null, unrelatedOrg.Id);
            var user = User.Create(
                "inherited-tenant-" + Guid.NewGuid().ToString("N"),
                "inherited-tenant-" + Guid.NewGuid().ToString("N") + "@example.test",
                "hash");
            var section = ContextSection.Create(
                "organization-admin-context",
                "Organization admin context",
                "Visible only in the managed project.",
                project.Id,
                ContextScopeType.Project);
            var unrelatedSection = ContextSection.Create(
                "unrelated-context",
                "Unrelated context",
                "Must remain hidden.",
                unrelatedProject.Id,
                ContextScopeType.Project);

            seed.Projects.AddRange(project, unrelatedProject);
            seed.Users.Add(user);
            seed.OrgMembers.Add(OrgMember.Create(org.Id, user.Id, role));
            seed.ContextSections.AddRange(section, unrelatedSection);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            orgId = org.Id;
            projectId = project.Id;
            userId = user.Id;
            sectionId = section.Id;

            Assert.False(await seed.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(member => member.ProjectId == projectId && member.UserId == userId,
                    TestContext.Current.CancellationToken));
        }

        Guid? resolvedProject = null;
        Guid? resolvedOrg = null;
        IReadOnlyList<Guid> visibleSections = [];
        await using var requestDb = _postgres.CreateContext();
        var middleware = new TenantIsolationMiddleware(async _ =>
        {
            resolvedProject = TenantScope.CurrentProjectId;
            resolvedOrg = TenantScope.CurrentOrgId;
            visibleSections = await requestDb.ContextSections
                .Select(section => section.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
        });
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString("D")),
                new Claim("isAdmin", "false")
            ], "Cookies"))
        };
        http.Request.RouteValues["projectId"] = projectId.ToString("D");

        await middleware.InvokeAsync(http, requestDb);

        Assert.Equal(projectId, resolvedProject);
        Assert.Equal(orgId, resolvedOrg);
        Assert.Equal(new[] { sectionId }, visibleSections);
    }

    [Fact]
    public async Task NoScope_ProjectScopedQuery_ReturnsEmpty()
    {
        _postgres.RequirePostgres();

        Guid taskA;
        Guid taskB;

        await using (var seed = _postgres.CreateContext())
        {
            var orgA = Org.Create("Empty Scope Org A");
            var orgB = Org.Create("Empty Scope Org B");
            seed.Orgs.AddRange(orgA, orgB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var pA = Project.Create("Empty Scope A", null, orgA.Id);
            var pB = Project.Create("Empty Scope B", null, orgB.Id);
            seed.Projects.AddRange(pA, pB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var tA = TaskItem.Create("Keep", pA.Id);
            var tB = TaskItem.Create("Secret", pB.Id);
            seed.Tasks.AddRange(tA, tB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskA = tA.Id;
            taskB = tB.Id;
        }

        await using var db = _postgres.CreateContext();
        Assert.Empty(await db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken));
        using (TenantScope.EnterUnscoped())
        {
            var all = await db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains(taskA, all);
            Assert.Contains(taskB, all);
        }
    }

    [Fact]
    public async Task OrgScope_HidesOtherOrgProjects()
    {
        _postgres.RequirePostgres();

        Guid orgA;
        Guid orgB;
        Guid projectA;
        Guid projectB;

        await using (var seed = _postgres.CreateContext())
        {
            var a = Org.Create("Tenant Org A");
            var b = Org.Create("Tenant Org B");
            seed.Orgs.AddRange(a, b);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var pA = Project.Create("Tenant Project A", null, a.Id);
            var pB = Project.Create("Tenant Project B", null, b.Id);
            seed.Projects.AddRange(pA, pB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            orgA = a.Id;
            orgB = b.Id;
            projectA = pA.Id;
            projectB = pB.Id;
        }

        await using var db = _postgres.CreateContext();

        using (TenantScope.EnterOrgScope(orgA))
        {
            var visible = await db.Projects.Select(p => p.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains(projectA, visible);
            Assert.DoesNotContain(projectB, visible);
        }

        using (TenantScope.EnterOrgScope(orgB))
        {
            var visible = await db.Projects.Select(p => p.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains(projectB, visible);
            Assert.DoesNotContain(projectA, visible);
        }
    }

    [Fact]
    public async Task DeviceCommandCompletion_UsesCommandProjectForForcedRlsWorkspaceReport()
    {
        _postgres.RequirePostgres();

        Guid deviceId;
        Guid projectA;
        Guid projectB;
        Guid taskId;
        Guid subtaskId;
        Guid sessionId;
        Guid commandId;

        await using (var seed = _postgres.CreateAdminContext())
        {
            var orgA = Org.Create("Device completion scope A");
            var orgB = Org.Create("Device completion scope B");
            seed.Orgs.AddRange(orgA, orgB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var projectAEntity = Project.Create("Device project A", null, orgA.Id);
            var projectBEntity = Project.Create("Device project B", null, orgB.Id);
            seed.Projects.AddRange(projectAEntity, projectBEntity);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var user = User.Create("device-owner", $"device-owner-{Guid.NewGuid():N}@example.test", "hash");
            var device = DaemonDevice.Create(
                "multi-project workstation", user.Id, "fp-" + Guid.NewGuid().ToString("N"),
                "hash-" + Guid.NewGuid().ToString("N"), "bcd_test_prefix");
            var task = TaskItem.Create("Fail actor and publish report", projectBEntity.Id);
            var subtask = Subtask.Create("Inspect the target project failure.", task.Id, projectBEntity.Id,
                [Domain.Common.McpToolNames.WorkspaceIndex, Domain.Common.McpToolNames.WorkspaceRead,
                 Domain.Common.McpToolNames.WorkspaceWrite]);
            subtask.Start();
            var session = PipelineSession.Create(
                task.Id, projectBEntity.Id, PipelineRole.Actor, "Actor prompt", subtask.Id);
            session.Launch();
            var command = Domain.Entities.Devices.WorkstationCommand.Create(
                device.Id, WorkstationCommandKind.RunPipelineSession,
                System.Text.Json.JsonSerializer.Serialize(new { sessionId = session.Id }),
                projectBEntity.Id, user.Id);
            command.Claim(TimeSpan.FromMinutes(15), "test-instance");

            seed.Users.Add(user);
            seed.DaemonDevices.Add(device);
            seed.Tasks.Add(task);
            seed.Subtasks.Add(subtask);
            seed.PipelineSessions.Add(session);
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            deviceId = device.Id;
            projectA = projectAEntity.Id;
            projectB = projectBEntity.Id;
            taskId = task.Id;
            subtaskId = subtask.Id;
            sessionId = session.Id;
            commandId = command.Id;
        }

        // The device owner's default membership resolves to project A at the API
        // boundary. Completion must switch to the immutable project binding on the
        // workstation command before mutating the project-B subtask or inserting
        // the FORCE-RLS-protected workspace report.
        using (TenantScope.EnterProjectScope(projectA))
        {
            var handler = new CompleteCommandHandler(_postgres.CreateFactory());
            var result = await handler.HandleAsync(new CompleteCommandCommand(
                new CompleteCommandRequest(
                    commandId, deviceId, Success: false, ResultJson: null,
                    Error: "simulated actor failure", Generation: 1)),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
        }

        await using var verify = _postgres.CreateAdminContext();
        var failedSubtask = await verify.Subtasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == subtaskId, TestContext.Current.CancellationToken);
        Assert.Equal(SubtaskStatus.Failed, failedSubtask.Status);

        var failedSession = await verify.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == sessionId, TestContext.Current.CancellationToken);
        Assert.Equal(SessionStatus.Failed, failedSession.Status);

        var report = await verify.TaskWorkspaceSections.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(section => section.TaskId == taskId
                && section.ProjectId == projectB
                && section.AuthorSubtaskId == subtaskId
                && section.Kind == "Report", TestContext.Current.CancellationToken);
        Assert.Equal("Server", report.AuthorType);
        Assert.Equal("Actor", report.AuthorRole);
        Assert.Contains("simulated actor failure", report.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterAppliesWithoutIgnoreQueryFilters()
    {
        _postgres.RequirePostgres();

        Guid projectA;
        Guid taskB;

        await using (var seed = _postgres.CreateContext())
        {
            var orgA = Org.Create("Filter Org A");
            var orgB = Org.Create("Filter Org B");
            seed.Orgs.AddRange(orgA, orgB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var pA = Project.Create("Filter Project A", null, orgA.Id);
            var pB = Project.Create("Filter Project B", null, orgB.Id);
            seed.Projects.AddRange(pA, pB);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            seed.Tasks.Add(TaskItem.Create("Keep", pA.Id));
            var other = TaskItem.Create("Secret", pB.Id);
            seed.Tasks.Add(other);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            projectA = pA.Id;
            taskB = other.Id;
        }

        await using var db = _postgres.CreateContext();
        using (TenantScope.EnterProjectScope(projectA))
        {
            Assert.DoesNotContain(taskB, await db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Contains(taskB, await db.Tasks.IgnoreQueryFilters().Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken));
        }
    }
}
