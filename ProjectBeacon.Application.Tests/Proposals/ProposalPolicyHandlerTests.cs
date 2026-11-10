namespace ProjectBeacon.Application.Tests.Proposals;

using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Proposals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Audit;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ProposalPolicyHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _ownerId;
    private readonly Guid _memberId;

    public ProposalPolicyHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Proposal Policy Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();

        var project = Project.Create("Policy Project", "Original description", org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var owner = User.Create("policy-owner", "policy-owner@beacon.local", "hash");
        var member = User.Create("policy-member", "policy-member@beacon.local", "hash");
        _db.Users.AddRange(owner, member);
        _db.SaveChanges();
        _ownerId = owner.Id;
        _memberId = member.Id;

        _db.ProjectMembers.AddRange(
            ProjectMember.Create(_projectId, _ownerId, MemberRole.Owner),
            ProjectMember.Create(_projectId, _memberId, MemberRole.Member));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private ActorContext OwnerActor() =>
        new(ActorType.Human, _ownerId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private ActorContext MemberActor() =>
        new(ActorType.Human, _memberId, false, null, null, _projectId, null, ApiTokenCapability.None);

    [Fact]
    public async Task MatrixDefaultsToManual_AndAcceptedContentUpdatesCannotBeAutoApplied()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var list = new ListProposalPoliciesHandler(factory);
        var initial = await list.HandleAsync(_projectId, MemberActor(), TestContext.Current.CancellationToken);

        Assert.True(initial.Success, initial.Error);
        Assert.Equal(18, initial.Value!.Count);
        Assert.All(initial.Value!, item =>
            Assert.Equal(ProposalDecisionMode.Manual, item.Mode));

        var repoPatch = Assert.Single(initial.Value!, item =>
            item.TargetKind == ContentTargetKind.RepoPatch
            && item.Operation == ProposalOperation.Update);
        Assert.True(repoPatch.AutoAllowed);
        Assert.Null(repoPatch.RestrictionReason);
        Assert.All(initial.Value!.Where(item => item != repoPatch), item => Assert.False(item.AutoAllowed));

        var projectDescription = Assert.Single(initial.Value!, item =>
            item.TargetKind == ContentTargetKind.ProjectDescription
            && item.Operation == ProposalOperation.Update);
        var taskDescription = Assert.Single(initial.Value!, item =>
            item.TargetKind == ContentTargetKind.TaskDescription
            && item.Operation == ProposalOperation.Update);
        Assert.Contains("manual human review", projectDescription.RestrictionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual human review", taskDescription.RestrictionReason, StringComparison.OrdinalIgnoreCase);

        var setter = new SetProposalPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());
        var attempt = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.ProjectDescription,
            ProposalOperation.Update,
            ProposalDecisionMode.Auto,
            projectDescription.PolicyRevision,
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(attempt.Success);
        Assert.Equal(ErrorKind.Validation, attempt.Kind);

        var refreshed = await list.HandleAsync(_projectId, OwnerActor(), TestContext.Current.CancellationToken);
        Assert.True(refreshed.Success, refreshed.Error);
        Assert.All(refreshed.Value!, item => Assert.Equal(ProposalDecisionMode.Manual, item.Mode));
        Assert.Empty(await _db.AuditEvents.Where(item => item.Action == "proposal.policy.updated")
            .ToListAsync(TestContext.Current.CancellationToken));

        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.Equal(1, project.ProposalPolicyRevision);
        Assert.Equal("{}", project.ProposalPoliciesJson);
    }

    [Fact]
    public async Task PolicyChangesRequireOwnerAdmin_AndStaleRevisionIsRejected()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var setter = new SetProposalPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());

        var memberAttempt = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.ProjectDescription,
            ProposalOperation.Update,
            ProposalDecisionMode.Manual,
            1,
            MemberActor()), TestContext.Current.CancellationToken);
        Assert.False(memberAttempt.Success);
        Assert.Equal(ErrorKind.Forbidden, memberAttempt.Kind);

        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        project.SetProposalPoliciesJson("""{"ProjectDescription:Update":"Manual"}""");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stale = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.ProjectDescription,
            ProposalOperation.Update,
            ProposalDecisionMode.Manual,
            1,
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(stale.Success);
        Assert.Equal(ErrorKind.Conflict, stale.Kind);
    }

    [Fact]
    public async Task AutoModeIsRejectedForGovernanceAndUnsupportedOperations()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var setter = new SetProposalPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());

        var governance = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.Decision,
            ProposalOperation.Update,
            ProposalDecisionMode.Auto,
            1,
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(governance.Success);

        var acceptedContentUpdate = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.ProjectDescription,
            ProposalOperation.Update,
            ProposalDecisionMode.Auto,
            1,
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(acceptedContentUpdate.Success);

        var delete = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.ProjectDescription,
            ProposalOperation.Delete,
            ProposalDecisionMode.Manual,
            1,
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(delete.Success);

        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.Equal(1, project.ProposalPolicyRevision);
        Assert.Equal("{}", project.ProposalPoliciesJson);
    }
    [Fact]
    public async Task RepoPatchAutoMode_CanOnlyBeEnabledByOwnerAdmin()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var list = new ListProposalPoliciesHandler(factory);
        var initial = await list.HandleAsync(_projectId, OwnerActor(), TestContext.Current.CancellationToken);
        Assert.True(initial.Success, initial.Error);

        var repoPatch = Assert.Single(initial.Value!, item =>
            item.TargetKind == ContentTargetKind.RepoPatch
            && item.Operation == ProposalOperation.Update);
        Assert.True(repoPatch.AutoAllowed);
        Assert.Equal(ProposalDecisionMode.Manual, repoPatch.Mode);

        var setter = new SetProposalPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());
        var enabled = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.RepoPatch,
            ProposalOperation.Update,
            ProposalDecisionMode.Auto,
            repoPatch.PolicyRevision,
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.True(enabled.Success, enabled.Error);
        Assert.Equal(ProposalDecisionMode.Auto, enabled.Value!.Mode);
        Assert.True(enabled.Value.AutoAllowed);
        Assert.Equal(repoPatch.PolicyRevision + 1, enabled.Value.PolicyRevision);

        var refreshed = await list.HandleAsync(_projectId, OwnerActor(), TestContext.Current.CancellationToken);
        Assert.True(refreshed.Success, refreshed.Error);
        var refreshedRepoPatch = Assert.Single(refreshed.Value!, item =>
            item.TargetKind == ContentTargetKind.RepoPatch
            && item.Operation == ProposalOperation.Update);
        Assert.Equal(ProposalDecisionMode.Auto, refreshedRepoPatch.Mode);
        Assert.Contains(await _db.AuditEvents.Select(item => item.Action).ToListAsync(TestContext.Current.CancellationToken),
            action => action == "proposal.policy.updated");
    }

}
