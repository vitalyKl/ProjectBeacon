namespace ProjectBeacon.Application.Tests;

using Application.Agents;
using Application.Authorization;
using Application.Runtime;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public sealed class ProjectMcpServerHandlerTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly AuthorizationService _authorization = new();
    private readonly ISecretProtector _secrets = TestProtectors.Dev();

    public ProjectMcpServerHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Save_EncryptsCredentials_AndListNeverReturnsSecretValues()
    {
        var (projectId, actor) = await SeedProject();
        var save = new SaveProjectMcpServersHandler(_factory, _authorization, _secrets);
        var list = new ListProjectMcpServersHandler(_factory, _authorization, _secrets);
        var server = new McpServerConfiguration(
            "github", "stdio", "npx", ["-y", "@modelcontextprotocol/server-github"],
            Environment: new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "super-secret-value" });

        var saved = await save.HandleAsync(new SaveProjectMcpServersCommand(
            projectId, [new ProjectMcpServerSaveItem(server, ReplaceSecrets: true)], actor));
        Assert.True(saved.Success, saved.Error);

        var cipher = _db.Projects.IgnoreQueryFilters().AsNoTracking().Single(project => project.Id == projectId).McpServersCipher;
        Assert.DoesNotContain("super-secret-value", cipher);
        Assert.StartsWith(ISecretProtector.V2Prefix, cipher);

        var result = await list.HandleAsync(new ListProjectMcpServersCommand(projectId, actor));
        Assert.True(result.Success, result.Error);
        var view = Assert.Single(result.Value!);
        Assert.True(view.HasSecrets);
        Assert.Contains("GITHUB_PERSONAL_ACCESS_TOKEN", view.EnvironmentKeys);
        Assert.DoesNotContain("super-secret-value", JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task Save_UpdatingCommandWithoutCredentials_PreservesStoredCredentials()
    {
        var (projectId, actor) = await SeedProject();
        var save = new SaveProjectMcpServersHandler(_factory, _authorization, _secrets);
        var original = new McpServerConfiguration(
            "github", "stdio", "npx", ["-y", "server-github"],
            Environment: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "keep-me" });
        var first = await save.HandleAsync(new SaveProjectMcpServersCommand(
            projectId, [new ProjectMcpServerSaveItem(original, ReplaceSecrets: true)], actor));
        Assert.True(first.Success, first.Error);

        var edited = original with { Command = "node", Arguments = ["server.js"] };
        var second = await save.HandleAsync(new SaveProjectMcpServersCommand(
            projectId, [new ProjectMcpServerSaveItem(edited)], actor));
        Assert.True(second.Success, second.Error);

        var stored = _secrets.Open(_db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Single(project => project.Id == projectId).McpServersCipher);
        var parsed = JsonSerializer.Deserialize<List<McpServerConfiguration>>(
            stored, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("keep-me", Assert.Single(parsed!).Environment!["GITHUB_TOKEN"]);
        Assert.Equal("node", Assert.Single(second.Value!).Command);
    }

    [Fact]
    public async Task Save_RejectsInsecureRemoteEndpointsAndDuplicateNames()
    {
        var (projectId, actor) = await SeedProject();
        var save = new SaveProjectMcpServersHandler(_factory, _authorization, _secrets);
        var insecure = new McpServerConfiguration("remote", "streamable-http", Endpoint: "http://example.com/mcp");
        var insecureResult = await save.HandleAsync(new SaveProjectMcpServersCommand(
            projectId, [new ProjectMcpServerSaveItem(insecure)], actor));
        Assert.False(insecureResult.Success);

        var first = new McpServerConfiguration("github", "stdio", "npx");
        var duplicate = first with { Name = "GITHUB" };
        var duplicateResult = await save.HandleAsync(new SaveProjectMcpServersCommand(
            projectId, [new ProjectMcpServerSaveItem(first), new ProjectMcpServerSaveItem(duplicate)], actor));
        Assert.False(duplicateResult.Success);
    }

    [Fact]
    public async Task Save_CanExplicitlyClearPreviouslyStoredCredentials()
    {
        var (projectId, actor) = await SeedProject();
        var save = new SaveProjectMcpServersHandler(_factory, _authorization, _secrets);
        var original = new McpServerConfiguration(
            "github", "stdio", "npx",
            Environment: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "remove-me" });
        var first = await save.HandleAsync(new SaveProjectMcpServersCommand(
            projectId, [new ProjectMcpServerSaveItem(original, ReplaceSecrets: true)], actor));
        Assert.True(first.Success, first.Error);

        var cleared = await save.HandleAsync(new SaveProjectMcpServersCommand(
            projectId, [new ProjectMcpServerSaveItem(original with { Environment = null }, ReplaceSecrets: true)], actor));
        Assert.True(cleared.Success, cleared.Error);
        Assert.False(Assert.Single(cleared.Value!).HasSecrets);
    }

    private async Task<(Guid ProjectId, ActorContext Actor)> SeedProject()
    {
        var org = Org.Create("MCP tests", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var project = Project.Create("MCP project", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var user = User.Create("mcp-owner", "mcp-owner@example.test", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        var actor = new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None);
        return (project.Id, actor);
    }
}
