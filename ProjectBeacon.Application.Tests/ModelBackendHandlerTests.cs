namespace ProjectBeacon.Application.Tests;

using Application.Agents;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ModelBackendHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public ModelBackendHandlerTests()
    {
        // Dispose the unscoped token right away: the handlers' contexts must see the
        // fail-closed tenant filter, so the AsyncLocal flag must not leak into test bodies.
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static async Task<(Guid OrgId, Guid ProjectId)> SeedProjectAsync(BeaconDbContext db, string orgName, string projectName)
    {
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create(orgName, null);
            db.Orgs.Add(org);
            await db.SaveChangesAsync();
            var project = Project.Create(projectName, null, org.Id);
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            return (org.Id, project.Id);
        }
    }

    private static TenantContext Scope(Guid projectId)
    {
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, unscoped: false);
        return tenant;
    }

    private static TenantContext EmptyGuidScope()
    {
        var tenant = new TenantContext();
        tenant.Assign(Guid.Empty, null, unscoped: false);
        return tenant;
    }

    private async Task<int> CountBackendsAsync()
    {
        using (TenantScope.EnterUnscoped())
            return await _db.LocalModelBackends.CountAsync();
    }

    private async Task<int> CountBindingsAsync()
    {
        using (TenantScope.EnterUnscoped())
            return await _db.RoleBindings.CountAsync();
    }

    [Fact]
    public async Task Upsert_CreatesBackend_InScope()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var handler = new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));

        var result = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(null, "llama-small", ModelBackendType.LlamaCpp, "llama-server -m small.gguf", 8192, 300, new[] { "--no-mmap" })));

        Assert.True(result.Success, result.Error);
        var dto = result.Value!;
        Assert.Equal("llama-small", dto.Name);
        Assert.Equal(ModelBackendType.LlamaCpp, dto.BackendType);
        Assert.Equal("llama-server -m small.gguf", dto.LaunchCommand);
        Assert.Equal(8192, dto.ContextSize);
        Assert.Equal(300, dto.Ttl);
        Assert.Equal(new[] { "--no-mmap" }, dto.ExtraFlags);
        Assert.Equal(projectId, dto.ProjectId);
        Assert.NotNull(dto.UpdatedAt);
        Assert.Equal(1, await CountBackendsAsync());
    }

    [Fact]
    public async Task Upsert_UpdateById_PreservesRow()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var handler = new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));
        var created = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(null, "old", ModelBackendType.LlamaCpp, "old-cmd", 1024, 0)));
        Assert.True(created.Success, created.Error);

        var updated = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(created.Value!.Id, "new", ModelBackendType.OpenAiCompatible, "new-cmd", 2048, 60)));

        Assert.True(updated.Success, updated.Error);
        Assert.Equal(created.Value.Id, updated.Value!.Id);
        Assert.Equal("new", updated.Value.Name);
        Assert.Equal(ModelBackendType.OpenAiCompatible, updated.Value.BackendType);
        Assert.Equal(2048, updated.Value.ContextSize);
        Assert.Equal(1, await CountBackendsAsync());
    }

    [Fact]
    public async Task Upsert_UpdateForeignProject_NotFound()
    {
        var (_, projectA) = await SeedProjectAsync(_db, "OrgA", "A");
        var (_, projectB) = await SeedProjectAsync(_db, "OrgB", "B");

        var foreign = LocalModelBackend.Create("foreign", ModelBackendType.LlamaCpp, "cmd", 1024, 0, projectB);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.Add(foreign);
            await _db.SaveChangesAsync();
        }

        var handler = new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectA)));
        var result = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(foreign.Id, "foreign", ModelBackendType.LlamaCpp, "cmd", 1024, 0)));

        Assert.False(result.Success);
        Assert.Equal("Model backend not found.", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upsert_NoScope_FailClosed(bool emptyGuidScope)
    {
        var request = new UpsertLocalModelBackendRequest(null, "x", ModelBackendType.LlamaCpp, "cmd", 1024, 0);
        var handler = emptyGuidScope
            ? new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection, EmptyGuidScope()))
            : new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection));

        var result = await handler.HandleAsync(new UpsertLocalModelBackendCommand(request));

        Assert.False(result.Success);
        Assert.Equal("Project scope is not resolved.", result.Error);
    }

    [Fact]
    public async Task Upsert_InvalidInput_Fails()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var handler = new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));

        var noName = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(null, "  ", ModelBackendType.LlamaCpp, "cmd", 1024, 0)));
        Assert.False(noName.Success);
        Assert.Equal("Name is required (max 200 characters).", noName.Error);

        var noLaunch = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(null, "name", ModelBackendType.LlamaCpp, "  ", 1024, 0)));
        Assert.False(noLaunch.Success);
        Assert.Equal("LaunchCommand is required (max 1000 characters).", noLaunch.Error);

        var negative = await handler.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(null, "name", ModelBackendType.LlamaCpp, "cmd", 1024, -1)));
        Assert.False(negative.Success);
        Assert.Equal("ContextSize and Ttl must be non-negative.", negative.Error);
    }

    [Fact]
    public async Task Delete_BoundBackend_FailsWithRoles()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var backend = LocalModelBackend.Create("bound", ModelBackendType.LlamaCpp, "cmd", 1024, 0, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.Add(backend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, backend.Id, projectId));
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Actor, backend.Id, projectId));
            await _db.SaveChangesAsync();
        }

        var handler = new DeleteLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));
        var result = await handler.HandleAsync(new DeleteLocalModelBackendCommand(new DeleteLocalModelBackendRequest(backend.Id)));

        Assert.False(result.Success);
        Assert.Contains("planner", result.Error);
        Assert.Contains("actor", result.Error);
        Assert.Contains("Unbind first", result.Error);
        Assert.Equal(1, await CountBackendsAsync());
    }

    [Fact]
    public async Task Delete_UnboundBackend_Ok()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var upsert = new UpsertLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));
        var created = await upsert.HandleAsync(new UpsertLocalModelBackendCommand(
            new UpsertLocalModelBackendRequest(null, "solo", ModelBackendType.FreeToken, "cmd", 1024, 0)));
        Assert.True(created.Success, created.Error);

        var del = new DeleteLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));
        var result = await del.HandleAsync(new DeleteLocalModelBackendCommand(new DeleteLocalModelBackendRequest(created.Value!.Id)));
        Assert.True(result.Success, result.Error);
        Assert.Equal(0, await CountBackendsAsync());
    }

    [Fact]
    public async Task Delete_Missing_Fails()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var handler = new DeleteLocalModelBackendHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));

        var result = await handler.HandleAsync(new DeleteLocalModelBackendCommand(new DeleteLocalModelBackendRequest(Guid.NewGuid())));

        Assert.False(result.Success);
        Assert.Equal("Model backend not found.", result.Error);
    }

    [Fact]
    public async Task SetRoleBinding_CreatesThenRepoints()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var first = LocalModelBackend.Create("first", ModelBackendType.LlamaCpp, "c1", 1024, 0, projectId);
        var second = LocalModelBackend.Create("second", ModelBackendType.LlamaCpp, "c2", 2048, 0, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.AddRange(first, second);
            await _db.SaveChangesAsync();
        }

        var handler = new SetRoleBindingHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));
        var created = await handler.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(PipelineRole.Actor, first.Id)));
        Assert.True(created.Success, created.Error);
        Assert.Equal(projectId, created.Value!.ProjectId);

        var repointed = await handler.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(PipelineRole.Actor, second.Id)));
        Assert.True(repointed.Success, repointed.Error);
        Assert.Equal(created.Value.Id, repointed.Value!.Id);
        Assert.Equal(second.Id, repointed.Value.ModelBackendId);
        Assert.Equal(1, await CountBindingsAsync());
    }

    [Fact]
    public async Task SetRoleBinding_UnknownBackend_Fails()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var handler = new SetRoleBindingHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));

        var result = await handler.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(PipelineRole.Review, Guid.NewGuid())));

        Assert.False(result.Success);
        Assert.Equal("Model backend not found.", result.Error);
    }

    [Fact]
    public async Task RemoveRoleBinding_Toggles()
    {
        var (_, projectId) = await SeedProjectAsync(_db, "OrgA", "A");
        var backend = LocalModelBackend.Create("solo", ModelBackendType.LlamaCpp, "cmd", 1024, 0, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.Add(backend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, backend.Id, projectId));
            await _db.SaveChangesAsync();
        }

        var handler = new RemoveRoleBindingHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));
        var removed = await handler.HandleAsync(new RemoveRoleBindingCommand(new RemoveRoleBindingRequest(PipelineRole.Planner)));
        Assert.True(removed.Success, removed.Error);

        var again = await handler.HandleAsync(new RemoveRoleBindingCommand(new RemoveRoleBindingRequest(PipelineRole.Planner)));
        Assert.False(again.Success);
        Assert.Equal("Role binding not found.", again.Error);
    }

    [Fact]
    public async Task Registry_ScopedAndSorted()
    {
        var (_, projectA) = await SeedProjectAsync(_db, "OrgA", "A");
        var (_, projectB) = await SeedProjectAsync(_db, "OrgB", "B");

        var beta = LocalModelBackend.Create("beta", ModelBackendType.LlamaCpp, "b", 1024, 0, projectA);
        var alpha = LocalModelBackend.Create("alpha", ModelBackendType.LlamaCpp, "a", 2048, 0, projectA);
        var other = LocalModelBackend.Create("other", ModelBackendType.FreeToken, "o", 512, 0, projectB);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.AddRange(beta, alpha, other);
            await _db.SaveChangesAsync();

            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Review, alpha.Id, projectA));
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Actor, beta.Id, projectA));
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, other.Id, projectB));
            await _db.SaveChangesAsync();
        }

        var handler = new GetModelRegistryHandler(HandlerSqlite.Factory(_connection, Scope(projectA)));
        var result = await handler.HandleAsync(new GetModelRegistryCommand());

        Assert.True(result.Success, result.Error);
        Assert.Equal(projectA, result.Value!.ProjectId);
        Assert.Equal(new[] { alpha.Id, beta.Id }, result.Value.Backends.Select(b => b.Id).ToList());
        Assert.Equal(new[] { "alpha", "beta" }, result.Value.Backends.Select(b => b.Name).ToList());
        Assert.Equal(new[] { PipelineRole.Actor, PipelineRole.Review }, result.Value.Bindings.Select(r => r.Role).ToList());
    }

    [Fact]
    public async Task Registry_NoScope_FailClosed()
    {
        var handler = new GetModelRegistryHandler(HandlerSqlite.Factory(_connection));

        var result = await handler.HandleAsync(new GetModelRegistryCommand());

        Assert.False(result.Success);
        Assert.Equal("Project scope is not resolved.", result.Error);
    }

    [Fact]
    public async Task Proxy_Unavailable_DegradesWithoutException()
    {
        var proxy = new UnavailableLlamaSwapProxy();

        var status = await new GetProxyStatusHandler(proxy).HandleAsync(new GetProxyStatusCommand());
        Assert.True(status.Success, status.Error);
        Assert.False(status.Value!.Available);
        Assert.False(status.Value.Healthy);
        Assert.Equal("llama-swap supervisor is not running.", status.Value.Error);

        var reload = await new ReloadProxyHandler(proxy).HandleAsync(new ReloadProxyCommand());
        Assert.False(reload.Success);
        Assert.Equal("llama-swap supervisor is not running.", reload.Error);

        var unload = await new UnloadProxyHandler(proxy).HandleAsync(new UnloadProxyCommand());
        Assert.False(unload.Success);
        Assert.Equal("llama-swap supervisor is not running.", unload.Error);
    }
}
