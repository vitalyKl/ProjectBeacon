namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed partial class AuthorizationServiceTests
{
    // ── Worker guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_AnyResource_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Worker(), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Worker_TaskRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Worker(), ResourceType.Task, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    // ── Device guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task Device_DeviceResource_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Device, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Device_WorkstationCommand_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.WorkstationCommand, AuthAction.Execute, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Device_TaskResource_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Task, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Device_ProjectAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    // ── Admin membership bypass (organization lifecycle still applies) ────

    [Fact]
    public async Task Admin_ProjectAdminister_ReturnsOk()
    {
        var org = Org.Create("Active administrator organization");
        var project = Project.Create("Active administrator project", null, org.Id);
        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Project, AuthAction.Administer,
            projectId: project.Id, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Admin_OrgAdminister_ReturnsOk()
    {
        var org = Org.Create("Active administrator organization");
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Org, AuthAction.Administer,
            orgId: org.Id, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Admin_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Task, AuthAction.Read, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    // ── ApiToken policy ───────────────────────────────────────────────────

    [Fact]
    public async Task Token_WithTaskRead_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.Task, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskRead_TaskRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Task, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskCreate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Create, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskUpdate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Update, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskDelete_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Delete, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskWrite_TaskCreate_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Task, AuthAction.Create, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskRead_TaskStepRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.TaskStep, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskStepCreate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.TaskStep, AuthAction.Create, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskStepUpdate_ReturnsForbidden()
    {
        var result = await _auth.CanAsync(
            _db,
            Token(ApiTokenCapability.TaskWrite),
            ResourceType.TaskStep,
            AuthAction.Update,
            projectId: _projectId,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Token_WithContextRead_ContextRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.ContextRead), ResourceType.Context, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutContextRead_ContextRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Context, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskRead_PipelineRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.Pipeline, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_PipelineExecute_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.SessionDrive), ResourceType.Pipeline, AuthAction.Execute, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_Admin_ChatSessionRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.ChatSession, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_Admin_ChatSessionExecute_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.ChatSession, AuthAction.Execute, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskRead_PipelineRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Pipeline, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_ProjectAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_OrgAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Org, AuthAction.Administer, orgId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_AdminCapability_BypassesTaskRead()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Task, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
    }

    // ── Human non-administer (no DB queries) ──────────────────────────────

    [Fact]
    public async Task Human_TaskRead_WithoutProjectScope_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Task, AuthAction.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Human_ProjectMember_CanReadAndCreateTasks_ButCannotDelete()
    {
        var memberId = await SeedProjectMemberAsync(MemberRole.Member);
        var actor = Human(memberId);

        var read = await _auth.CanAsync(_db, actor, ResourceType.Task, AuthAction.Read, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        var create = await _auth.CanAsync(_db, actor, ResourceType.Task, AuthAction.Create, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        var delete = await _auth.CanAsync(_db, actor, ResourceType.Task, AuthAction.Delete, projectId: _projectId, ct: TestContext.Current.CancellationToken);

        Assert.True(read.Success);
        Assert.True(create.Success);
        Assert.False(delete.Success);
    }

    [Fact]
    public async Task Human_NonMember_CannotReadTaskEvenWhenProjectIdIsKnown()
    {
        var result = await _auth.CanAsync(
            _db, Human(Guid.NewGuid()), ResourceType.Task, AuthAction.Read,
            projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Human_ProjectAdmin_CanDeleteTask()
    {
        var adminId = await SeedProjectMemberAsync(MemberRole.Admin);
        var result = await _auth.CanAsync(
            _db, Human(adminId), ResourceType.Task, AuthAction.Delete,
            projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task OrgMember_CanReadOrg_ButCannotAdministerIt()
    {
        var user = User.Create("org-member", "org-member@example.test", "hash");
        _db.Users.Add(user);
        _db.OrgMembers.Add(OrgMember.Create(_orgId, user.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var read = await _auth.CanAsync(_db, Human(user.Id), ResourceType.Org, AuthAction.Read, orgId: _orgId, ct: TestContext.Current.CancellationToken);
        var administer = await _auth.CanAsync(_db, Human(user.Id), ResourceType.Org, AuthAction.Administer, orgId: _orgId, ct: TestContext.Current.CancellationToken);

        Assert.True(read.Success);
        Assert.False(administer.Success);
    }

    [Fact]
    public async Task TokenBoundToAnotherProject_IsDenied()
    {
        var result = await _auth.CanAsync(
            _db, Token(ApiTokenCapability.TaskRead), ResourceType.Task, AuthAction.Read,
            projectId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UnboundApiToken_IsDeniedEvenWithCapability()
    {
        var token = new ActorContext(ActorType.ApiToken, null, false, Guid.NewGuid(), null, null, null, ApiTokenCapability.TaskRead);
        var result = await _auth.CanAsync(
            _db, token, ResourceType.Task, AuthAction.Read,
            projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UnknownResourceAndAction_AreDenied()
    {
        var actor = Admin(Guid.NewGuid());
        var unknownResource = await _auth.CanAsync(_db, actor, (ResourceType)999, AuthAction.Read, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        var unknownAction = await _auth.CanAsync(_db, actor, ResourceType.Task, (AuthAction)999, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.False(unknownResource.Success);
        Assert.False(unknownAction.Success);
    }

    [Fact]
    public async Task ProjectMember_UnsupportedResourceActionPairs_FailClosed()
    {
        var memberId = await SeedProjectMemberAsync(MemberRole.Member);
        var member = Human(memberId);

        var allowed = new[]
        {
            (Resource: ResourceType.Pipeline, Action: AuthAction.Execute),
            (Resource: ResourceType.Context, Action: AuthAction.Execute),
            (Resource: ResourceType.Constraint, Action: AuthAction.Update)
        };

        foreach (var (resource, action) in allowed)
        {
            var result = await _auth.CanAsync(
                _db, member, resource, action,
                projectId: _projectId,
                ct: TestContext.Current.CancellationToken);
            Assert.True(result.Success, $"Expected member access for {resource}/{action}.");
        }

        var denied = new[]
        {
            (Resource: ResourceType.Project, Action: AuthAction.Execute),
            (Resource: ResourceType.Task, Action: AuthAction.Execute),
            (Resource: ResourceType.TaskStep, Action: AuthAction.Update),
            (Resource: ResourceType.Report, Action: AuthAction.Execute),
            (Resource: ResourceType.ChatSession, Action: AuthAction.Read),
            (Resource: ResourceType.WorkstationConfiguration, Action: AuthAction.Read),
            (Resource: ResourceType.Work, Action: AuthAction.Read)
        };

        foreach (var (resource, action) in denied)
        {
            var result = await _auth.CanAsync(
                _db, member, resource, action,
                projectId: _projectId,
                ct: TestContext.Current.CancellationToken);
            Assert.False(result.Success, $"Unexpected member access for {resource}/{action}.");
        }
    }

    [Fact]
    public async Task SystemAdmin_UnsupportedResourceActionPair_IsDenied()
    {
        var result = await _auth.CanAsync(
            _db,
            Admin(Guid.NewGuid()),
            ResourceType.Project,
            AuthAction.Execute,
            projectId: _projectId,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Human_DecisionRead_WithoutMembership_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Decision, AuthAction.Read, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Human_MilestoneRead_WithoutMembership_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Milestone, AuthAction.Read, projectId: _projectId, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }
}
