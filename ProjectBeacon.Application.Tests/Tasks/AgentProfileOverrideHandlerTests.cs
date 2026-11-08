namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed class AgentProfileOverrideHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly Guid _userId;

    public AgentProfileOverrideHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
        _userId = Guid.NewGuid();
        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(User.Create("profile-user", $"profile-{_userId:N}@beacon.local", "hash"));
            _db.SaveChanges();
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BeaconDbFactory Factory()
    {
        var tenant = new TenantContext();
        tenant.Assign(Guid.NewGuid(), null, unscoped: false);
        return HandlerSqlite.Factory(_connection, tenant);
    }

    [Fact]
    public async Task SaveAndGet_PersistOnlyUserScopedOverride()
    {
        var handler = new SaveAgentProfileOverrideHandler(Factory());
        var profile = BuiltInAgentProfiles.Get("implementer")!;
        var selectedTools = new[] { profile.Tools[0], profile.Tools[1] };

        var saved = await handler.HandleAsync(new SaveAgentProfileOverrideCommand(
            new SaveAgentProfileOverrideRequest(
                _userId, "implementer", "Feature builder", "Custom feature work",
                "Prefer minimal changes and targeted tests.", selectedTools,
                false, true, "one feature area", "medium")),
            TestContext.Current.CancellationToken);

        Assert.True(saved.Success, saved.Error);
        Assert.Equal("Feature builder", saved.Value!.Name);
        Assert.Equal(selectedTools, saved.Value.Tools);
        Assert.False(saved.Value.CommandsEnabled);
        Assert.Equal(profile.PathPolicy.ToString(), saved.Value.PathPolicy);
        Assert.Equal(profile.DefaultPaths, saved.Value.DefaultPaths);

        var listed = await new GetAgentProfileOverridesHandler(Factory())
            .HandleAsync(new GetAgentProfileOverridesCommand(_userId), TestContext.Current.CancellationToken);
        Assert.True(listed.Success, listed.Error);
        var current = Assert.Single(listed.Value!);
        Assert.Equal("implementer", current.Key);

        var anotherAccount = await new GetAgentProfileOverridesHandler(Factory())
            .HandleAsync(new GetAgentProfileOverridesCommand(Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.True(anotherAccount.Success, anotherAccount.Error);
        Assert.Empty(anotherAccount.Value!);
    }

    [Fact]
    public async Task Save_RejectsToolExpansionAndEnablingCommandsForReadOnlyProfile()
    {
        var handler = new SaveAgentProfileOverrideHandler(Factory());
        var explorer = BuiltInAgentProfiles.Get("explorer")!;

        var toolExpansion = await handler.HandleAsync(new SaveAgentProfileOverrideCommand(
            new SaveAgentProfileOverrideRequest(
                _userId, "explorer", explorer.Name, explorer.Description, explorer.Instruction,
                explorer.Tools.Concat(new[] { "task_create_subtask" }).ToArray(),
                false, false, explorer.SizeHint, explorer.Effort)),
            TestContext.Current.CancellationToken);
        Assert.False(toolExpansion.Success);
        Assert.Contains("outside", toolExpansion.Error);

        var enableCommands = await handler.HandleAsync(new SaveAgentProfileOverrideCommand(
            new SaveAgentProfileOverrideRequest(
                _userId, "explorer", explorer.Name, explorer.Description, explorer.Instruction,
                explorer.Tools, true, false, explorer.SizeHint, explorer.Effort)),
            TestContext.Current.CancellationToken);
        Assert.False(enableCommands.Success);
        Assert.Contains("does not permit", enableCommands.Error);
    }

    [Fact]
    public void ApplyOverride_RetainsBuiltInGuidanceAndOnlyNarrowsToolGrants()
    {
        var basis = BuiltInAgentProfiles.Get("implementer")!;
        var userId = Guid.NewGuid();
        var selectedTools = new[] { basis.Tools[0] };
        var custom = Domain.Entities.Agents.AgentProfile.Create(
            userId, basis.Key, "Custom implementer", "Custom description",
            "Prefer small and focused changes.", basis.Mode.ToString(), selectedTools,
            basis.PathPolicy.ToString(), basis.DefaultPaths, false, true, "one file", "low");

        var effective = BuiltInAgentProfiles.ApplyOverride(basis, custom)!;

        Assert.Contains(basis.Instruction, effective.Instruction);
        Assert.Contains("Prefer small and focused changes.", effective.Instruction);
        Assert.Equal(selectedTools.Concat(basis.RequiredTools).Distinct(StringComparer.Ordinal), effective.Tools);
        Assert.False(effective.CommandsEnabled);
        Assert.Equal(basis.PathPolicy, effective.PathPolicy);
        Assert.Equal(basis.DefaultPaths, effective.DefaultPaths);
    }

    [Fact]
    public async Task Reset_RemovesOverrideAndRestoresBuiltInProfile()
    {
        var basis = BuiltInAgentProfiles.Get("docs-writer")!;
        var saved = await new SaveAgentProfileOverrideHandler(Factory()).HandleAsync(
            new SaveAgentProfileOverrideCommand(new SaveAgentProfileOverrideRequest(
                _userId, basis.Key, "Docs expert", basis.Description, basis.Instruction,
                basis.Tools, false, true, basis.SizeHint, basis.Effort)),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.Error);

        var reset = await new ResetAgentProfileOverrideHandler(Factory()).HandleAsync(
            new ResetAgentProfileOverrideCommand(new ResetAgentProfileOverrideRequest(_userId, basis.Key)),
            TestContext.Current.CancellationToken);
        Assert.True(reset.Success, reset.Error);

        var listed = await new GetAgentProfileOverridesHandler(Factory())
            .HandleAsync(new GetAgentProfileOverridesCommand(_userId), TestContext.Current.CancellationToken);
        Assert.True(listed.Success, listed.Error);
        Assert.Empty(listed.Value!);
    }
}
