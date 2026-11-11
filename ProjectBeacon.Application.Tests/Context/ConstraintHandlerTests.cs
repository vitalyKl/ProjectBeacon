namespace ProjectBeacon.Application.Tests;

using Application.Context;
using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ConstraintHandlerTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _userId;

    public ConstraintHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;
        var user = User.Create("constraint-owner", "constraint-owner@beacon.local", "hash");
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
    public async Task Propose_IsIdempotentAndNeverActivatesConstraint()
    {
        var handler = new CreateConstraintHandler(HandlerSqlite.Factory(_connection));
        var request = new CreateConstraintRequest(_projectId, "Agents may only propose constraints", ConstraintKind.Must, HumanActor());

        var first = await handler.HandleProposalAsync(request, TestContext.Current.CancellationToken);
        var retry = await handler.HandleProposalAsync(request, TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Error);
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(first.Value!.Id, retry.Value!.Id);
        Assert.Equal(ConstraintStatus.Proposed, retry.Value.Status);
        Assert.Equal(1, await _db.Constraints.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.ContentVersions.CountAsync(
            v => v.TargetKind == ContentTargetKind.Constraint && v.TargetId == retry.Value.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConstraintProposals_RequireProjectMemberOrBoundContextReadToken()
    {
        var handler = new CreateConstraintHandler(HandlerSqlite.Factory(_connection));
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);
        var outsiderResult = await handler.HandleProposalAsync(
            new CreateConstraintRequest(_projectId, "Outsider rule", ConstraintKind.Must, outsider),
            TestContext.Current.CancellationToken);
        Assert.False(outsiderResult.Success);
        Assert.Equal(ErrorKind.Forbidden, outsiderResult.Kind);

        var unboundToken = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            Guid.NewGuid(), null, ApiTokenCapability.ContextRead);
        var unboundResult = await handler.HandleProposalAsync(
            new CreateConstraintRequest(_projectId, "Unbound rule", ConstraintKind.Must, unboundToken),
            TestContext.Current.CancellationToken);
        Assert.False(unboundResult.Success);
        Assert.Equal(ErrorKind.Forbidden, unboundResult.Kind);

        var tokenWithoutCapability = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.TaskRead);
        var capabilityResult = await handler.HandleProposalAsync(
            new CreateConstraintRequest(_projectId, "Wrong capability rule", ConstraintKind.Must, tokenWithoutCapability),
            TestContext.Current.CancellationToken);
        Assert.False(capabilityResult.Success);
        Assert.Equal(ErrorKind.Forbidden, capabilityResult.Kind);

        var agent = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.ContextRead);
        var accepted = await handler.HandleProposalAsync(
            new CreateConstraintRequest(_projectId, "Bound Actor rule", ConstraintKind.Must, agent),
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ConstraintStatus.Proposed, accepted.Value!.Status);
    }

    [Fact]
    public async Task ProjectMemberCannotActivateOrRejectProposedConstraint()
    {
        var member = User.Create("constraint-member", "constraint-member@beacon.local", "hash");
        _db.Users.Add(member);
        _db.SaveChanges();
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, member.Id, MemberRole.Member));
        _db.SaveChanges();

        var actor = new ActorContext(
            ActorType.Human, member.Id, false, null, null, _projectId, null, ApiTokenCapability.None);
        var created = await new CreateConstraintHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
            new CreateConstraintRequest(_projectId, "Owner approval required.", ConstraintKind.Must, actor),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);
        var factory = HandlerSqlite.Factory(_connection);

        var activate = await new ActivateConstraintHandler(factory, new AuthorizationService())
            .HandleAsync(created.Value!.Id, actor, TestContext.Current.CancellationToken);
        Assert.False(activate.Success);
        Assert.Equal(ErrorKind.Forbidden, activate.Kind);

        var reject = await new RejectConstraintHandler(factory, new AuthorizationService())
            .HandleAsync(created.Value.Id, actor, TestContext.Current.CancellationToken);
        Assert.False(reject.Success);
        Assert.Equal(ErrorKind.Forbidden, reject.Kind);

        Assert.Equal(ConstraintStatus.Proposed,
            (await _db.Constraints.SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Activate_ThenReject_UpdatesStatus()
    {
        var created = await new CreateConstraintHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
            new CreateConstraintRequest(_projectId, "must ship tests", ConstraintKind.Must, HumanActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success);
        Assert.Equal(ConstraintStatus.Proposed, created.Value!.Status);

        var activated = await new ActivateConstraintHandler(HandlerSqlite.Factory(_connection), new AuthorizationService()).HandleAsync(created.Value!.Id, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(activated.Success);
        Assert.Equal(ConstraintStatus.Active, activated.Value!.Status);
        Assert.Equal(2, activated.Value.Revision);

        var rejected = await new RejectConstraintHandler(HandlerSqlite.Factory(_connection), new AuthorizationService()).HandleAsync(created.Value!.Id, HumanActor(), TestContext.Current.CancellationToken);
        Assert.True(rejected.Success);
        Assert.Equal(ConstraintStatus.Rejected, rejected.Value!.Status);
        Assert.Equal(3, rejected.Value.Revision);

        var history = await _db.ContentVersions
            .Where(item => item.TargetKind == ContentTargetKind.Constraint && item.TargetId == created.Value.Id)
            .OrderBy(item => item.Revision)
            .Select(item => item.Revision)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2, 3 }, history);
    }
}
