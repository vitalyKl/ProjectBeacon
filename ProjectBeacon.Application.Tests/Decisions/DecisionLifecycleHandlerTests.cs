namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Application.Authorization;
using Application.Common;
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
    private readonly Guid _userId;

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
        var user = User.Create("decision-owner", "decision-owner@beacon.local", "hash");
        _db.Users.Add(user);
        _db.SaveChanges();
        _userId = user.Id;
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, _userId, MemberRole.Owner));
        _db.SaveChanges();
    }

    private ActorContext HumanActor() =>
        new(ActorType.Human, _userId, false, null, null, _projectId, null, ApiTokenCapability.None);

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
            _projectId, "Use a review gate", "Human-owned knowledge", "Keep Proposed until reviewed", null, HumanActor());

        var first = await handler.HandleProposalAsync(request, TestContext.Current.CancellationToken);
        var retry = await handler.HandleProposalAsync(request, TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Error);
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(first.Value!.Id, retry.Value!.Id);
        Assert.Equal(DecisionStatus.Proposed, retry.Value.Status);
        Assert.Equal(1, await _db.Decisions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.ContentVersions.CountAsync(
            v => v.TargetKind == ContentTargetKind.Decision && v.TargetId == retry.Value.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DecisionProposals_RequireProjectMemberOrBoundContextReadToken()
    {
        var handler = new CreateDecisionHandler(HandlerSqlite.Factory(_connection));
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);
        var outsiderResult = await handler.HandleProposalAsync(
            new CreateDecisionRequest(_projectId, "Outsider", "ctx", "body", null, outsider),
            TestContext.Current.CancellationToken);
        Assert.False(outsiderResult.Success);
        Assert.Equal(ErrorKind.Forbidden, outsiderResult.Kind);

        var unboundToken = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            Guid.NewGuid(), null, ApiTokenCapability.ContextRead);
        var unboundResult = await handler.HandleProposalAsync(
            new CreateDecisionRequest(_projectId, "Unbound", "ctx", "body", null, unboundToken),
            TestContext.Current.CancellationToken);
        Assert.False(unboundResult.Success);
        Assert.Equal(ErrorKind.Forbidden, unboundResult.Kind);

        var tokenWithoutCapability = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.TaskRead);
        var capabilityResult = await handler.HandleProposalAsync(
            new CreateDecisionRequest(_projectId, "Wrong capability", "ctx", "body", null, tokenWithoutCapability),
            TestContext.Current.CancellationToken);
        Assert.False(capabilityResult.Success);
        Assert.Equal(ErrorKind.Forbidden, capabilityResult.Kind);

        var agent = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.ContextRead);
        var accepted = await handler.HandleProposalAsync(
            new CreateDecisionRequest(_projectId, "Bound Actor", "ctx", "body", null, agent),
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(DecisionStatus.Proposed, accepted.Value!.Status);
    }

    [Fact]
    public async Task Accept_Then_Supersede_And_Deprecate()
    {
        var create = new CreateDecisionHandler(HandlerSqlite.Factory(_connection));
        var first = await create.HandleAsync(new CreateDecisionRequest(_projectId, "A", "ctx", "body A", "c", HumanActor()), TestContext.Current.CancellationToken);
        var second = await create.HandleAsync(new CreateDecisionRequest(_projectId, "B", "ctx", "body B", null, HumanActor()), TestContext.Current.CancellationToken);
        Assert.True(first.Success);
        Assert.True(second.Success);

        var accept = new AcceptDecisionHandler(HandlerSqlite.Factory(_connection), new AuthorizationService());
        var accepted = await accept.HandleAsync(first.Value!.Id, HumanActor(), TestContext.Current.CancellationToken);
        Assert.Equal(DecisionStatus.Accepted, accepted.Value!.Status);

        var supersede = new SupersedeDecisionHandler(HandlerSqlite.Factory(_connection), new AuthorizationService());
        var replaced = await supersede.HandleAsync(first.Value.Id, second.Value!.Id, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(replaced.Success, replaced.Error);
        Assert.Equal(DecisionStatus.Superseded, replaced.Value!.Status);

        var deprecate = new DeprecateDecisionHandler(HandlerSqlite.Factory(_connection), new AuthorizationService());
        var deprecated = await deprecate.HandleAsync(second.Value.Id, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(deprecated.Success, deprecated.Error);
        Assert.Equal(DecisionStatus.Deprecated, deprecated.Value!.Status);
    }

    [Fact]
    public async Task ProjectMemberCannotAcceptOrDeprecateDecision()
    {
        var member = User.Create("decision-member", "decision-member@beacon.local", "hash");
        _db.Users.Add(member);
        _db.SaveChanges();
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, member.Id, MemberRole.Member));
        _db.SaveChanges();
        var actor = new ActorContext(
            ActorType.Human, member.Id, false, null, null, _projectId, null, ApiTokenCapability.None);
        var decision = await new CreateDecisionHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new CreateDecisionRequest(_projectId, "Member proposal", "ctx", "body", null, actor),
                TestContext.Current.CancellationToken);
        Assert.True(decision.Success, decision.Error);
        var factory = HandlerSqlite.Factory(_connection);

        var accept = await new AcceptDecisionHandler(factory, new AuthorizationService())
            .HandleAsync(decision.Value!.Id, actor, TestContext.Current.CancellationToken);
        Assert.False(accept.Success);
        Assert.Equal(ErrorKind.Forbidden, accept.Kind);

        var deprecate = await new DeprecateDecisionHandler(factory, new AuthorizationService())
            .HandleAsync(decision.Value.Id, actor, TestContext.Current.CancellationToken);
        Assert.False(deprecate.Success);
        Assert.Equal(ErrorKind.Forbidden, deprecate.Kind);

        Assert.Equal(DecisionStatus.Proposed,
            (await _db.Decisions.SingleAsync(item => item.Id == decision.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Supersede_Proposed_Fails()
    {
        var create = new CreateDecisionHandler(HandlerSqlite.Factory(_connection));
        var first = await create.HandleAsync(new CreateDecisionRequest(_projectId, "A", "ctx", "body A", null, HumanActor()), TestContext.Current.CancellationToken);
        var second = await create.HandleAsync(new CreateDecisionRequest(_projectId, "B", "ctx", "body B", null, HumanActor()), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        var supersede = new SupersedeDecisionHandler(HandlerSqlite.Factory(_connection), new AuthorizationService());
        var result = await supersede.HandleAsync(first.Value!.Id, second.Value!.Id, HumanActor(), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
    }
}
