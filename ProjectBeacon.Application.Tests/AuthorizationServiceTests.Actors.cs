namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed partial class AuthorizationServiceTests
{
    // ── Worker guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_AnyResource_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Worker(), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Worker_TaskRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Worker(), ResourceType.Task, AuthAction.Read);
        Assert.False(r.Success);
    }

    // ── Device guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task Device_DeviceResource_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Device, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Device_WorkstationCommand_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.WorkstationCommand, AuthAction.Execute);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Device_TaskResource_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Task, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Device_ProjectAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    // ── Admin bypass ──────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_ProjectAdminister_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Admin_OrgAdminister_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Org, AuthAction.Administer, orgId: Guid.NewGuid());
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Admin_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    // ── ApiToken policy ───────────────────────────────────────────────────

    [Fact]
    public async Task Token_WithTaskRead_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskRead_TaskRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Task, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskCreate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Create);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskUpdate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Update);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskDelete_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Delete);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskWrite_TaskCreate_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Task, AuthAction.Create);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskRead_TaskStepRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.TaskStep, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskStepCreate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.TaskStep, AuthAction.Create);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithContextRead_ContextRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.ContextRead), ResourceType.Context, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutContextRead_ContextRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Context, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskRead_PipelineRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.Pipeline, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_PipelineExecute_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Pipeline, AuthAction.Execute);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_Admin_ChatSessionRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.ChatSession, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_Admin_ChatSessionExecute_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.ChatSession, AuthAction.Execute);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskRead_PipelineRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Pipeline, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_ProjectAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_OrgAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Org, AuthAction.Administer, orgId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_AdminCapability_BypassesTaskRead()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    // ── Human non-administer (no DB queries) ──────────────────────────────

    [Fact]
    public async Task Human_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_DecisionRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Decision, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_MilestoneRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Milestone, AuthAction.Read);
        Assert.True(r.Success);
    }
}
