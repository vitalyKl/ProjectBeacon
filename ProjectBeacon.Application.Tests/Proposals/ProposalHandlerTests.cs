namespace ProjectBeacon.Application.Tests.Proposals;

using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Proposals;
using Domain.Entities.Content;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Context;
using Domain.Enums;
using Infrastructure.Audit;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ProposalHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private Guid _userId;
    private readonly Guid _projectId;

    public ProposalHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Proposal Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();

        var project = Project.Create("Proposal Project", "Original description", org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var author = User.Create("proposal-author", "proposal-author@beacon.local", "hash");
        _db.Users.Add(author);
        _db.SaveChanges();
        _userId = author.Id;
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, _userId, MemberRole.Owner));
        _db.SaveChanges();

        var writer = new ContentWriter();
        writer.AddVersion(_db, project, "Human", _userId.ToString("D"));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateThenAccept_AppendsRevisionAndAuditsInSameUnitOfWork()
    {
        var actor = HumanActor();
        var factory = HandlerSqlite.Factory(_connection);
        var create = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), actor), TestContext.Current.CancellationToken);

        Assert.True(create.Success, create.Error);
        Assert.Equal(ProposalStatus.Pending, create.Value!.Status);
        Assert.Equal(30, (create.Value.ExpiresAt - create.Value.CreatedAt).TotalDays);

        var duplicate = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), actor), TestContext.Current.CancellationToken);
        Assert.True(duplicate.Success, duplicate.Error);
        Assert.Equal(create.Value.Id, duplicate.Value!.Id);

        var accepted = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, create.Value.Id, actor), TestContext.Current.CancellationToken);

        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value!.Status);
        Assert.Equal(2, accepted.Value.AppliedRevision);

        var project = await _db.Projects.SingleAsync(item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.Equal("Updated project description", project.Description);
        Assert.Equal(2, project.Revision);

        var versions = await _db.ContentVersions
            .Where(version => version.ProjectId == _projectId
                && version.TargetKind == ContentTargetKind.ProjectDescription
                && version.TargetId == _projectId)
            .OrderBy(version => version.Revision)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 }, versions.Select(version => version.Revision).ToArray());
        Assert.Equal(create.Value.Id, versions[^1].ProposalId);

        var auditActions = await _db.AuditEvents
            .Where(item => item.ProjectId == _projectId)
            .Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("proposal.created", auditActions);
        Assert.Contains("proposal.accepted", auditActions);
    }

    [Fact]
    public async Task IdempotencyKeyCannotBeUsedToReadAnotherAuthorsProposal()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var firstActor = HumanActor();
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), firstActor), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var otherActor = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);
        var replay = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), otherActor), TestContext.Current.CancellationToken);

        Assert.False(replay.Success);
        Assert.Equal(ErrorKind.Conflict, replay.Kind);
        Assert.Equal(created.Value!.Id, (await _db.Proposals.SingleAsync(TestContext.Current.CancellationToken)).Id);
    }

    [Fact]
    public async Task StaleBaseRevision_MarksProposalConflictedWithoutChangingTarget()
    {
        var actor = HumanActor();
        var factory = HandlerSqlite.Factory(_connection);
        var create = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), actor), TestContext.Current.CancellationToken);
        Assert.True(create.Success, create.Error);

        var project = await _db.Projects.SingleAsync(item => item.Id == _projectId, TestContext.Current.CancellationToken);
        project.Update(description: "Changed by another user");
        var changed = await new ContentWriter().SaveAsync(_db, project, "Human", _userId.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.True(changed.Success, changed.Error);
        Assert.Equal(2, changed.Value);

        var accepted = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, create.Value!.Id, actor), TestContext.Current.CancellationToken);

        Assert.False(accepted.Success);
        Assert.Equal(ErrorKind.Conflict, accepted.Kind);

        var proposal = await _db.Proposals.SingleAsync(item => item.Id == create.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Conflicted, proposal.Status);
        Assert.Equal(2, proposal.ConflictRevision);

        await _db.Entry(project).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Changed by another user", project.Description);
        Assert.Equal(2, project.Revision);

        var versions = await _db.ContentVersions
            .Where(version => version.TargetId == _projectId && version.TargetKind == ContentTargetKind.ProjectDescription)
            .OrderBy(version => version.Revision)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 }, versions.Select(version => version.Revision).ToArray());
        Assert.DoesNotContain(versions, version => version.ProposalId == create.Value.Id);
    }

    [Fact]
    public async Task ApiTokenCannotAcceptEvenWhenItCanCreateProposals()
    {
        var human = HumanActor();
        var factory = HandlerSqlite.Factory(_connection);
        var create = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), human), TestContext.Current.CancellationToken);
        Assert.True(create.Success, create.Error);

        var tokenActor = new ActorContext(
            ActorType.ApiToken,
            _userId,
            false,
            Guid.NewGuid(),
            null,
            _projectId,
            null,
            ApiTokenCapability.ContextRead);

        var result = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, create.Value!.Id, tokenActor), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        var proposal = await _db.Proposals.SingleAsync(item => item.Id == create.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
    }

    [Fact]
    public async Task AgentCannotProposeFromUnboundOrNonCuratorSession()
    {
        var tokenActor = new ActorContext(
            ActorType.ApiToken, _userId, false, Guid.NewGuid(), null, _projectId, null, ApiTokenCapability.ContextRead);
        var result = await new CreateProposalHandler(HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), tokenActor), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Empty(await _db.Proposals.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonMemberCannotCreateOrListProjectProposals()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);

        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), outsider), TestContext.Current.CancellationToken);
        Assert.False(created.Success);
        Assert.Equal(ErrorKind.Forbidden, created.Kind);

        var listed = await new ListProposalsHandler(factory, new EfAuditWriter())
            .HandleAsync(new ListProposalsQuery(_projectId), outsider, TestContext.Current.CancellationToken);
        Assert.False(listed.Success);
        Assert.Equal(ErrorKind.Forbidden, listed.Kind);
    }

    [Fact]
    public async Task ProjectMemberCanListProjectProposals()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var actor = HumanActor();
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), actor), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var listed = await new ListProposalsHandler(factory, new EfAuditWriter())
            .HandleAsync(new ListProposalsQuery(_projectId), actor, TestContext.Current.CancellationToken);

        Assert.True(listed.Success, listed.Error);
        Assert.Single(listed.Value!);
        Assert.Equal(created.Value!.Id, listed.Value![0].Id);
    }

    [Fact]
    public async Task NonMemberCannotAcceptOrRejectProjectProposals()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var author = HumanActor();
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), author), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var accepted = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, created.Value!.Id, outsider), TestContext.Current.CancellationToken);
        Assert.False(accepted.Success);
        Assert.Equal(ErrorKind.Forbidden, accepted.Kind);

        var rejected = await new RejectProposalHandler(factory, new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new RejectProposalCommand(_projectId, created.Value.Id, outsider), TestContext.Current.CancellationToken);
        Assert.False(rejected.Success);
        Assert.Equal(ErrorKind.Forbidden, rejected.Kind);

        var proposal = await _db.Proposals.SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
    }

    [Fact]
    public async Task NonAuthorCannotReplayWithdrawnProposalAndReadItsDetails()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var author = HumanActor();
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), author), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var withdrawn = await new WithdrawProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new WithdrawProposalCommand(_projectId, created.Value!.Id, author), TestContext.Current.CancellationToken);
        Assert.True(withdrawn.Success, withdrawn.Error);
        Assert.Equal(ProposalStatus.Withdrawn, withdrawn.Value!.Status);

        var replay = await new WithdrawProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new WithdrawProposalCommand(_projectId, created.Value.Id, outsider), TestContext.Current.CancellationToken);
        Assert.False(replay.Success);
        Assert.Equal(ErrorKind.Forbidden, replay.Kind);
    }

    private ActorContext HumanActor() =>
        new(ActorType.Human, _userId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private CreateProposalRequest Request() => new(
        ContentTargetKind.ProjectDescription,
        _projectId,
        ProposalOperation.Update,
        1,
        """{"description":"Updated project description"}""",
        "Makes the project purpose clearer.",
        "proposal-create-project-description-v1");
}
