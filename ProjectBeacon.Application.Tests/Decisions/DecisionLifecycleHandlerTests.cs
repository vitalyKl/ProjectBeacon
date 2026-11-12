namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Decisions;

public sealed class DecisionLifecycleHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;

    public DecisionLifecycleHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Propose_IsIdempotentAndNeverAcceptsDecision()
    {
        var handler = new CreateDecisionHandler(HandlerSqlite.Factory(_connection));
        var request = new CreateDecisionRequest(
            _projectId, "Use a review gate", "Human-owned knowledge", "Keep Proposed until reviewed", null);

        var first = await handler.HandleProposalAsync(request, TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        var retry = await handler.HandleProposalAsync(request, TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Error);
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(first.Value!.Id, retry.Value!.Id);
        Assert.Equal(DecisionStatus.Proposed, retry.Value.Status);
        Assert.Equal(1, await _db.Decisions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Accept_Then_Supersede_And_Deprecate()
    {
        var create = new CreateDecisionHandler(HandlerSqlite.Factory(_connection));
        var first = await create.HandleAsync(new CreateDecisionRequest(_projectId, "A", "ctx", "body A", "c"), TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        var second = await create.HandleAsync(new CreateDecisionRequest(_projectId, "B", "ctx", "body B", null), TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        Assert.True(first.Success);
        Assert.True(second.Success);

        var accept = new AcceptDecisionHandler(HandlerSqlite.Factory(_connection));
        var accepted = await accept.HandleAsync(first.Value!.Id, TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        Assert.Equal(DecisionStatus.Accepted, accepted.Value!.Status);

        var supersede = new SupersedeDecisionHandler(HandlerSqlite.Factory(_connection));
        var replaced = await supersede.HandleAsync(first.Value.Id, second.Value!.Id, TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        Assert.True(replaced.Success, replaced.Error);
        Assert.Equal(DecisionStatus.Superseded, replaced.Value!.Status);

        var deprecate = new DeprecateDecisionHandler(HandlerSqlite.Factory(_connection));
        var deprecated = await deprecate.HandleAsync(second.Value.Id, TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        Assert.True(deprecated.Success, deprecated.Error);
        Assert.Equal(DecisionStatus.Deprecated, deprecated.Value!.Status);
    }

    [Fact]
    public async Task Supersede_Proposed_Fails()
    {
        var create = new CreateDecisionHandler(HandlerSqlite.Factory(_connection));
        var first = await create.HandleAsync(new CreateDecisionRequest(_projectId, "A", "ctx", "body A", null), TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        var second = await create.HandleAsync(new CreateDecisionRequest(_projectId, "B", "ctx", "body B", null), TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        var supersede = new SupersedeDecisionHandler(HandlerSqlite.Factory(_connection));
        var result = await supersede.HandleAsync(first.Value!.Id, second.Value!.Id, TestActorFactory.AdminFor(_projectId), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
    }
}
