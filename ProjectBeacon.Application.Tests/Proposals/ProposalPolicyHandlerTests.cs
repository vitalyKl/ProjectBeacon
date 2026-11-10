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
    public async Task MatrixDefaultsToManual_AndOwnerCanEnableAutoForOrdinaryUpdate()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var list = new ListProposalPoliciesHandler(factory);
        var initial = await list.HandleAsync(_projectId, MemberActor(), TestContext.Current.CancellationToken);

        Assert.True(initial.Success, initial.Error);
        var projectDescription = Assert.Single(initial.Value!, item =>
            item.TargetKind == ContentTargetKind.ProjectDescription
            && item.Operation == ProposalOperation.Update);
        Assert.Equal(ProposalDecisionMode.Manual, projectDescription.Mode);
        Assert.True(projectDescription.AutoAllowed);

        var governance = Assert.Single(initial.Value!, item =>
            item.TargetKind == ContentTargetKind.Decision
            && item.Operation == ProposalOperation.Update);
        Assert.Equal(ProposalDecisionMode.Manual, governance.Mode);
        Assert.False(governance.AutoAllowed);
        Assert.Equal(18, initial.Value!.Count);
        Assert.All(initial.Value!, item => Assert.Equal(ProposalDecisionMode.Manual, item.Mode));

        var set = new SetProposalPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());
        var enabled = await set.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.ProjectDescription,
            ProposalOperation.Update,
            ProposalDecisionMode.Auto,
            projectDescription.PolicyRevision,
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.True(enabled.Success, enabled.Error);
        Assert.Equal(ProposalDecisionMode.Auto, enabled.Value!.Mode);
        Assert.Equal(projectDescription.PolicyRevision + 1, enabled.Value.PolicyRevision);

        var refreshed = await list.HandleAsync(_projectId, OwnerActor(), TestContext.Current.CancellationToken);
        Assert.True(refreshed.Success, refreshed.Error);
        Assert.Equal(ProposalDecisionMode.Auto, Assert.Single(refreshed.Value!, item =>
            item.TargetKind == ContentTargetKind.ProjectDescription
            && item.Operation == ProposalOperation.Update).Mode);

        var audit = await _db.AuditEvents.SingleAsync(
            item => item.Action == "proposal.policy.updated",
            TestContext.Current.CancellationToken);
        Assert.Equal("ProposalPolicy", audit.TargetType);
        Assert.Equal(_projectId, audit.ProjectId);
        Assert.Contains("Auto", audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyChangesRequireOwnerAdminAndExpectedPolicyRevision()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var setter = new SetProposalPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());

        var memberAttempt = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.TaskDescription,
            ProposalOperation.Update,
            ProposalDecisionMode.Auto,
            1,
            MemberActor()), TestContext.Current.CancellationToken);
        Assert.False(memberAttempt.Success);
        Assert.Equal(ErrorKind.Forbidden, memberAttempt.Kind);

        var ownerAttempt = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.TaskDescription,
            ProposalOperation.Update,
            ProposalDecisionMode.Auto,
            1,
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(ownerAttempt.Success, ownerAttempt.Error);

        var stale = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.TaskDescription,
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

        var create = await setter.HandleAsync(new SetProposalPolicyCommand(
            _projectId,
            ContentTargetKind.ProjectDescription,
            ProposalOperation.Create,
            ProposalDecisionMode.Auto,
            1,
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(create.Success);

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
}
