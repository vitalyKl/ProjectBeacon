namespace ProjectBeacon.Application.Tests.Proposals;

using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Proposals;
using Application.Tasks;
using Domain.Entities.Content;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Context;
using Domain.Entities.Tasks;
using Domain.Enums;
using Domain.Common;
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
    private Guid _orgId;
    private readonly Guid _projectId;

    public ProposalHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Proposal Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();
        _orgId = org.Id;

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
        await _db.Entry(project).ReloadAsync(TestContext.Current.CancellationToken);
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
    public async Task Create_RejectsCredentialLikePayloadBeforePersistence()
    {
        var request = Request() with
        {
            PayloadJson = """{"description":"ghp_123456789012345678901234567890"}"""
        };

        var result = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(_projectId, request, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("credential", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _db.Proposals.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_RejectsPrivateKeyInRationaleBeforePersistence()
    {
        var request = Request() with
        {
            RationaleMarkdown = "Accidentally included -----BEGIN PRIVATE KEY----- material."
        };

        var result = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(_projectId, request, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("credential", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _db.Proposals.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IdempotencyKeyCannotBeUsedToReadAnotherAuthorsProposal()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var firstActor = HumanActor();
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), firstActor), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var otherUser = User.Create("proposal-second-author", "proposal-second-author@beacon.local", "hash");
        _db.Users.Add(otherUser);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, otherUser.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var otherActor = new ActorContext(
            ActorType.Human, otherUser.Id, false, null, null, _projectId, _orgId, ApiTokenCapability.None);
        var replay = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), otherActor), TestContext.Current.CancellationToken);

        Assert.False(replay.Success);
        Assert.Equal(ErrorKind.Conflict, replay.Kind);
        Assert.Equal(created.Value!.Id, (await _db.Proposals.SingleAsync(TestContext.Current.CancellationToken)).Id);
    }

    [Fact]
    public async Task CreateMilestoneProposal_IdempotentReplayKeepsReservedTargetId()
    {
        var request = new CreateProposalRequest(
            ContentTargetKind.Milestone,
            Guid.Empty,
            ProposalOperation.Create,
            0,
            """{"name":"Alpha milestone","description":"Initial milestone","order":1}""",
            "Creates the first roadmap milestone.",
            "milestone-create-idempotency-" + Guid.NewGuid().ToString("N"));
        var command = new CreateProposalCommand(_projectId, request, HumanActor());
        var handler = new CreateProposalHandler(
            HandlerSqlite.Factory(_connection), new EfAuditWriter());

        var first = await handler.HandleAsync(command, TestContext.Current.CancellationToken);
        var replay = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Error);
        Assert.True(replay.Success, replay.Error);
        Assert.NotEqual(Guid.Empty, first.Value!.TargetId);
        Assert.Equal(first.Value.Id, replay.Value!.Id);
        Assert.Equal(first.Value.TargetId, replay.Value.TargetId);
        Assert.Equal(0, replay.Value.BaseRevision);

        var persisted = await _db.Proposals
            .Where(item => item.ProjectId == _projectId
                && item.IdempotencyKey == request.IdempotencyKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(persisted);
        Assert.Equal(first.Value.TargetId, persisted[0].TargetId);
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
    public async Task Accept_WhenContentVersionInsertFails_RollsBackTargetProposalAndAudit()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var actor = HumanActor();
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), actor), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER fail_proposal_content_version
            BEFORE INSERT ON "ContentVersions"
            WHEN NEW."ProposalId" IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'forced proposal content version failure');
            END;
            """,
            TestContext.Current.CancellationToken);

        var accepted = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, created.Value!.Id, actor), TestContext.Current.CancellationToken);

        Assert.False(accepted.Success);
        Assert.Equal(ErrorKind.Conflict, accepted.Kind);

        await using var verify = HandlerSqlite.Factory(_connection).CreateDbContext();
        var project = await verify.Projects.SingleAsync(item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.Equal("Original description", project.Description);
        Assert.Equal(1, project.Revision);

        var proposal = await verify.Proposals.SingleAsync(
            item => item.Id == created.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Null(proposal.AppliedRevision);

        var versions = await verify.ContentVersions
            .Where(version => version.TargetKind == ContentTargetKind.ProjectDescription
                && version.TargetId == _projectId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(versions);
        Assert.Null(versions[0].ProposalId);

        Assert.DoesNotContain(await verify.AuditEvents
            .Where(item => item.ProjectId == _projectId)
            .Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken), action => action == "proposal.accepted");
    }

    [Fact]
    public async Task ActorOwnProposalList_IsBoundToCurrentSubtaskSessionAndToken()
    {
        var projectTask = TaskItem.Create("Review project context.", _projectId);
        _db.Tasks.Add(projectTask);
        _db.SaveChanges();

        var grants = new[]
        {
            McpToolNames.KnowledgePropose,
            McpToolNames.KnowledgeProposals,
            McpToolNames.KnowledgeWithdraw
        };
        var subtask = Subtask.Create(
            "Review the project description and suggest a precise update.",
            projectTask.Id,
            _projectId,
            allowedMcpTools: grants,
            profileKey: "knowledge-curator");
        subtask.Start();
        _db.Subtasks.Add(subtask);
        var session = PipelineSession.Create(
            projectTask.Id, _projectId, PipelineRole.Actor, "Actor test session.", subtask.Id);
        session.Launch();
        _db.PipelineSessions.Add(session);
        _db.SaveChanges();

        var tokenId = Guid.NewGuid();
        var actor = new ActorContext(
            ActorType.ApiToken, _userId, false, tokenId, null,
            _projectId, _orgId, ApiTokenCapability.ContextRead);
        var command = new CreateProposalCommand(
            _projectId,
            Request(),
            actor,
            projectTask.Id,
            subtask.Id,
            session.Id,
            PipelineRole.Actor.ToString());
        var created = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(command, TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var creationAudit = await _db.AuditEvents.SingleAsync(item =>
            item.TargetId == created.Value!.Id.ToString("D")
            && item.Action == "proposal.created",
            TestContext.Current.CancellationToken);
        Assert.Equal(Domain.Entities.Audit.AuditActorKind.ApiToken, creationAudit.ActorKind);
        Assert.Null(creationAudit.ActorUserId);
        Assert.Equal(tokenId, creationAudit.ActorTokenId);

        var handler = new ListOwnProposalsHandler(
            HandlerSqlite.Factory(_connection), new EfAuditWriter());
        var listed = await handler.HandleAsync(
            _projectId, actor, projectTask.Id, subtask.Id, session.Id,
            PipelineRole.Actor.ToString(), ct: TestContext.Current.CancellationToken);

        Assert.True(listed.Success, listed.Error);
        var ownProposal = Assert.Single(listed.Value!);
        Assert.Equal(created.Value!.Id, ownProposal.Id);
        Assert.Equal(ProposalStatus.Pending, ownProposal.Status);
        Assert.Equal(created.Value.TargetKind, ownProposal.TargetKind);

        var otherToken = actor with { TokenId = Guid.NewGuid() };
        var otherTokenList = await handler.HandleAsync(
            _projectId, otherToken, projectTask.Id, subtask.Id, session.Id,
            PipelineRole.Actor.ToString(), ct: TestContext.Current.CancellationToken);
        Assert.True(otherTokenList.Success, otherTokenList.Error);
        Assert.Empty(otherTokenList.Value!);

        var noListGrant = Subtask.Create(
            "No proposal inbox grant.",
            projectTask.Id,
            _projectId,
            allowedMcpTools: [McpToolNames.KnowledgePropose, McpToolNames.KnowledgeWithdraw],
            profileKey: "knowledge-curator");
        noListGrant.Start();
        _db.Subtasks.Add(noListGrant);
        var noListSession = PipelineSession.Create(
            projectTask.Id, _projectId, PipelineRole.Actor, "Actor without list grant.", noListGrant.Id);
        noListSession.Launch();
        _db.PipelineSessions.Add(noListSession);
        _db.SaveChanges();

        var denied = await handler.HandleAsync(
            _projectId, actor, projectTask.Id, noListGrant.Id, noListSession.Id,
            PipelineRole.Actor.ToString(), ct: TestContext.Current.CancellationToken);
        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);
    }

    [Fact]
    public async Task NewSubmissionMaterializesExpiredPendingRowsAndAuditsExpiry()
    {
        var expired = Proposal.Create(
            _projectId,
            null,
            null,
            null,
            ContentTargetKind.ProjectDescription,
            _projectId,
            ProposalOperation.Update,
            1,
            new string('a', 64),
            """{"description":"old proposal"}""",
            "No longer current.",
            ProposalAuthorKind.Human,
            _userId,
            null,
            null,
            "Human",
            "expired-proposal-key",
            DateTime.UtcNow.AddDays(-31));
        _db.Proposals.Add(expired);
        _db.SaveChanges();
        var expiredId = expired.Id;

        var result = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(_projectId, Request(), HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        await _db.Entry(expired).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Expired, expired.Status);

        var expiryAudit = await _db.AuditEvents.SingleAsync(
            item => item.Action == "proposal.expired" && item.TargetId == expiredId.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.Equal("Proposal", expiryAudit.TargetType);
    }

    [Fact]
    public async Task AcceptExpiredProposal_PersistsExpiryAndReturnsConflict()
    {
        var expired = Proposal.Create(
            _projectId, null, null, null,
            ContentTargetKind.ProjectDescription, _projectId,
            ProposalOperation.Update, 1, new string('a', 64),
            """{"description":"Expired content"}""",
            "This proposal has passed its review window.",
            ProposalAuthorKind.Human, _userId, null, null, "Human",
            "accept-expired-" + Guid.NewGuid().ToString("N"),
            DateTime.UtcNow.AddDays(-31));
        _db.Proposals.Add(expired);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new AcceptProposalHandler(
                HandlerSqlite.Factory(_connection),
                new AuthorizationService(),
                new EfAuditWriter(),
                new ContentWriter())
            .HandleAsync(
                new AcceptProposalCommand(_projectId, expired.Id, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        await _db.Entry(expired).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Expired, expired.Status);
        Assert.Contains(await _db.AuditEvents
            .Where(item => item.TargetId == expired.Id.ToString("D"))
            .Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken), action => action == "proposal.expired");
    }

    [Fact]
    public async Task RejectExpiredProposal_PersistsExpiryAndReturnsConflict()
    {
        var expired = Proposal.Create(
            _projectId, null, null, null,
            ContentTargetKind.ProjectDescription, _projectId,
            ProposalOperation.Update, 1, new string('b', 64),
            """{"description":"Expired content"}""",
            "This proposal has passed its review window.",
            ProposalAuthorKind.Human, _userId, null, null, "Human",
            "reject-expired-" + Guid.NewGuid().ToString("N"),
            DateTime.UtcNow.AddDays(-31));
        _db.Proposals.Add(expired);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new RejectProposalHandler(
                HandlerSqlite.Factory(_connection),
                new AuthorizationService(),
                new EfAuditWriter())
            .HandleAsync(
                new RejectProposalCommand(_projectId, expired.Id, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        await _db.Entry(expired).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Expired, expired.Status);
        Assert.Contains(await _db.AuditEvents
            .Where(item => item.TargetId == expired.Id.ToString("D"))
            .Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken), action => action == "proposal.expired");
    }

    [Fact]
    public async Task ContextSectionHeadingEdits_AreAppliedAgainstTheProposalBaseRevision()
    {
        var section = ContextSection.Create(
            "architecture",
            "Architecture",
            "# Architecture\n\nOverview.\n\n## Security\n\nOld security details.\n\n## Operations\n\nOperational details.",
            _projectId,
            ContextScopeType.Project,
            key: "architecture");
        section.MarkReviewed(_userId.ToString("D"));
        _db.ContextSections.Add(section);
        var writer = new ContentWriter();
        writer.AddVersion(_db, section, "Human", _userId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var baseRevision = section.Revision;

        var payload = """
            {
              "edits": [
                {
                  "heading": "## Security",
                  "op": "replace",
                  "content": "Reviewed security details.\n\n### Verification\n\nVerify all trust boundaries."
                },
                {
                  "heading": "## Operations",
                  "op": "insert_after",
                  "content": "## Recovery\n\nDocument the recovery process."
                }
              ]
            }
            """;
        var created = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(
                    _projectId,
                    new CreateProposalRequest(
                        ContentTargetKind.ContextSection,
                        section.Id,
                        ProposalOperation.Update,
                        baseRevision,
                        payload,
                        "Updates security guidance and adds recovery instructions.",
                        "context-heading-edits-" + Guid.NewGuid().ToString("N")),
                    HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        Assert.Equal(ProposalStatus.Pending, created.Value!.Status);

        var accepted = await new AcceptProposalHandler(
                HandlerSqlite.Factory(_connection),
                new AuthorizationService(),
                new EfAuditWriter(),
                writer)
            .HandleAsync(
                new AcceptProposalCommand(_projectId, created.Value.Id, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value!.Status);
        Assert.Equal(baseRevision + 1, accepted.Value.AppliedRevision);

        await _db.Entry(section).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Contains("## Security", section.BodyMarkdown);
        Assert.Contains("Reviewed security details.", section.BodyMarkdown);
        Assert.Contains("### Verification", section.BodyMarkdown);
        Assert.DoesNotContain("Old security details.", section.BodyMarkdown);
        Assert.Contains("## Operations", section.BodyMarkdown);
        Assert.Contains("## Recovery", section.BodyMarkdown);
        Assert.Contains("Document the recovery process.", section.BodyMarkdown);
        Assert.Equal(baseRevision + 1, section.Revision);

        var history = await _db.ContentVersions
            .Where(item => item.TargetKind == ContentTargetKind.ContextSection
                && item.TargetId == section.Id)
            .OrderBy(item => item.Revision)
            .Select(item => item.Revision)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { baseRevision, baseRevision + 1 }, history);
    }

    [Fact]
    public async Task ProjectMemberCanCreateListAndAcceptOrdinaryProposal()
    {
        var member = User.Create("proposal-member", "proposal-member@beacon.local", "hash");
        _db.Users.Add(member);
        _db.SaveChanges();
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, member.Id, MemberRole.Member));
        _db.SaveChanges();

        var actor = new ActorContext(
            ActorType.Human, member.Id, false, null, null, _projectId, null, ApiTokenCapability.None);
        var factory = HandlerSqlite.Factory(_connection);
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, Request(), actor), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var listed = await new ListProposalsHandler(factory, new EfAuditWriter())
            .HandleAsync(new ListProposalsQuery(_projectId), actor, TestContext.Current.CancellationToken);
        Assert.True(listed.Success, listed.Error);
        Assert.Single(listed.Value!);

        var accepted = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, created.Value!.Id, actor), TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value!.Status);
        Assert.Equal(2, accepted.Value.AppliedRevision);
    }

    [Fact]
    public async Task ProjectMemberCannotAcceptOrRejectGovernanceProposal()
    {
        var member = User.Create("governance-member", "governance-member@beacon.local", "hash");
        _db.Users.Add(member);
        _db.SaveChanges();
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, member.Id, MemberRole.Member));
        _db.SaveChanges();

        var decision = Decision.Create(
            "Existing decision", "Current context", "Accepted decision body", _projectId, null);
        decision.Accept();
        _db.Decisions.Add(decision);
        _db.SaveChanges();
        new ContentWriter().AddVersion(_db, decision, "Human", _userId.ToString("D"));
        _db.SaveChanges();

        var actor = new ActorContext(
            ActorType.Human, member.Id, false, null, null, _projectId, null, ApiTokenCapability.None);
        var request = new CreateProposalRequest(
            ContentTargetKind.Decision,
            decision.Id,
            ProposalOperation.Update,
            2,
            """{"title":"Revised decision","context":"Updated context","decisionBody":"Updated decision","consequences":null}""",
            "Clarifies the decision.",
            "governance-decision-update-v1");
        var factory = HandlerSqlite.Factory(_connection);
        var created = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, request, actor), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var accept = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, created.Value!.Id, actor), TestContext.Current.CancellationToken);
        Assert.False(accept.Success);
        Assert.Equal(ErrorKind.Forbidden, accept.Kind);

        var reject = await new RejectProposalHandler(factory, new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new RejectProposalCommand(_projectId, created.Value.Id, actor), TestContext.Current.CancellationToken);
        Assert.False(reject.Success);
        Assert.Equal(ErrorKind.Forbidden, reject.Kind);

        var stillPending = await _db.Proposals.SingleAsync(
            item => item.Id == created.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Pending, stillPending.Status);

        var ownerAccept = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, created.Value.Id, HumanActor()), TestContext.Current.CancellationToken);
        Assert.True(ownerAccept.Success, ownerAccept.Error);
        Assert.Equal(ProposalStatus.Accepted, ownerAccept.Value!.Status);

        var acceptedReplay = await new AcceptProposalHandler(
                factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter())
            .HandleAsync(new AcceptProposalCommand(_projectId, created.Value.Id, actor), TestContext.Current.CancellationToken);
        Assert.False(acceptedReplay.Success);
        Assert.Equal(ErrorKind.Forbidden, acceptedReplay.Kind);

        var secondDecision = Decision.Create(
            "Second decision", "Current context", "Second accepted decision", _projectId, null);
        secondDecision.Accept();
        _db.Decisions.Add(secondDecision);
        _db.SaveChanges();
        new ContentWriter().AddVersion(_db, secondDecision, "Human", _userId.ToString("D"));
        _db.SaveChanges();

        var secondRequest = request with
        {
            TargetId = secondDecision.Id,
            IdempotencyKey = "governance-decision-update-v2"
        };
        var secondProposal = await new CreateProposalHandler(factory, new EfAuditWriter())
            .HandleAsync(new CreateProposalCommand(_projectId, secondRequest, HumanActor()), TestContext.Current.CancellationToken);
        Assert.True(secondProposal.Success, secondProposal.Error);

        var ownerReject = await new RejectProposalHandler(factory, new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new RejectProposalCommand(_projectId, secondProposal.Value!.Id, HumanActor()), TestContext.Current.CancellationToken);
        Assert.True(ownerReject.Success, ownerReject.Error);
        Assert.Equal(ProposalStatus.Rejected, ownerReject.Value!.Status);

        var rejectedReplay = await new RejectProposalHandler(factory, new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new RejectProposalCommand(_projectId, secondProposal.Value.Id, actor), TestContext.Current.CancellationToken);
        Assert.False(rejectedReplay.Success);
        Assert.Equal(ErrorKind.Forbidden, rejectedReplay.Kind);
    }

    [Fact]
    public async Task OrganizationMemberWithoutProjectMembershipCannotViewInbox()
    {
        var orgMember = User.Create("proposal-org-member", "proposal-org-member@beacon.local", "hash");
        _db.Users.Add(orgMember);
        _db.SaveChanges();
        _db.OrgMembers.Add(OrgMember.Create(_orgId, orgMember.Id, MemberRole.Member));
        _db.SaveChanges();

        var actor = new ActorContext(
            ActorType.Human, orgMember.Id, false, null, null, _projectId, _orgId, ApiTokenCapability.None);
        var listed = await new ListProposalsHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(new ListProposalsQuery(_projectId), actor, TestContext.Current.CancellationToken);

        Assert.False(listed.Success);
        Assert.Equal(ErrorKind.Forbidden, listed.Kind);
    }

    [Fact]
    public async Task ApiTokenWithoutContextReadCannotCreateProposal()
    {
        var tokenActor = new ActorContext(
            ActorType.ApiToken, _userId, false, Guid.NewGuid(), null,
            _projectId, _orgId, ApiTokenCapability.TaskRead);

        var result = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
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

    [Fact]
    public async Task InboxSummaryCountsAllLegacyAndRevisionBoundReviewItemsForProjectMember()
    {
        var pending = Proposal.Create(
            _projectId, null, null, null, ContentTargetKind.ProjectDescription, _projectId,
            ProposalOperation.Update, 1, new string('a', 64),
            """{"description":"pending"}""", "A pending change.",
            ProposalAuthorKind.Human, _userId, null, null, "Human",
            "inbox-summary-pending", DateTime.UtcNow);
        var conflicted = Proposal.Create(
            _projectId, null, null, null, ContentTargetKind.ProjectDescription, _projectId,
            ProposalOperation.Update, 1, new string('b', 64),
            """{"description":"conflicted"}""", "A stale change.",
            ProposalAuthorKind.Human, _userId, null, null, "Human",
            "inbox-summary-conflicted", DateTime.UtcNow);
        conflicted.MarkConflicted(2, "BaseRevisionChanged", DateTime.UtcNow.AddSeconds(1));
        var expired = Proposal.Create(
            _projectId, null, null, null, ContentTargetKind.ProjectDescription, _projectId,
            ProposalOperation.Update, 1, new string('c', 64),
            """{"description":"expired"}""", "An obsolete change.",
            ProposalAuthorKind.Human, _userId, null, null, "Human",
            "inbox-summary-expired", DateTime.UtcNow.AddDays(-31));
        var decision = Decision.Create("Proposed decision", "Context", "Body", _projectId);
        var constraint = Constraint.Create("A proposed rule.", ConstraintKind.Must, _projectId);
        var context = ContextSection.Create(
            "security", "Security", "Draft security policy.", _projectId,
            ContextScopeType.Project, updatedByType: "agent", updatedById: "agent-session");

        _db.Proposals.AddRange(pending, conflicted, expired);
        _db.Decisions.Add(decision);
        _db.Constraints.Add(constraint);
        _db.ContextSections.Add(context);
        _db.SaveChanges();

        var result = await new GetProposalInboxSummaryHandler(
                HandlerSqlite.Factory(_connection))
            .HandleAsync(_projectId, HumanActor(), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Value!.PendingProposals);
        Assert.Equal(1, result.Value.ConflictedProposals);
        Assert.Equal(1, result.Value.ProposedDecisions);
        Assert.Equal(1, result.Value.ProposedConstraints);
        Assert.Equal(1, result.Value.NeedsReviewContextSections);
        Assert.Equal(4, result.Value.TotalPending);
    }

    [Fact]
    public async Task InboxSummaryAndContextDraftReview_RequireProjectMembershipAndExpectedRevision()
    {
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, _projectId, null, ApiTokenCapability.None);
        var context = ContextSection.Create(
            "architecture", "Architecture", "Draft architecture note.", _projectId,
            ContextScopeType.Project, updatedByType: "agent", updatedById: "source-agent");
        _db.ContextSections.Add(context);
        new ContentWriter().AddVersion(_db, context, "Agent", "source-agent");
        _db.SaveChanges();

        var factory = HandlerSqlite.Factory(_connection);
        var deniedSummary = await new GetProposalInboxSummaryHandler(factory)
            .HandleAsync(_projectId, outsider, TestContext.Current.CancellationToken);
        Assert.False(deniedSummary.Success);
        Assert.Equal(ErrorKind.Forbidden, deniedSummary.Kind);

        var deniedReview = await new ReviewContextDraftHandler(factory, new ContentWriter(), new EfAuditWriter())
            .HandleAsync(new ReviewContextDraftCommand(
                _projectId, context.Id, context.Revision, true, outsider),
                TestContext.Current.CancellationToken);
        Assert.False(deniedReview.Success);
        Assert.Equal(ErrorKind.Forbidden, deniedReview.Kind);

        var staleReview = await new ReviewContextDraftHandler(factory, new ContentWriter(), new EfAuditWriter())
            .HandleAsync(new ReviewContextDraftCommand(
                _projectId, context.Id, context.Revision + 1, true, HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.False(staleReview.Success);
        Assert.Equal(ErrorKind.Conflict, staleReview.Kind);

        var approved = await new ReviewContextDraftHandler(factory, new ContentWriter(), new EfAuditWriter())
            .HandleAsync(new ReviewContextDraftCommand(
                _projectId, context.Id, context.Revision, true, HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(approved.Success, approved.Error);
        Assert.Equal(ContextReviewState.Reviewed, approved.Value!.ReviewState);
        Assert.False(approved.Value.Deleted);
        var approvedHistory = await _db.ContentVersions
            .Where(version => version.TargetKind == ContentTargetKind.ContextSection
                && version.TargetId == context.Id)
            .OrderBy(version => version.Revision)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 }, approvedHistory.Select(version => version.Revision).ToArray());
        using (var approvedSnapshot = System.Text.Json.JsonDocument.Parse(approvedHistory[^1].ContentJson))
            Assert.Equal((int)ContextReviewState.Reviewed,
                approvedSnapshot.RootElement.GetProperty("reviewState").GetInt32());
        await _db.Entry(context).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, context.Revision);
        Assert.Contains(await _db.AuditEvents.Select(item => item.Action).ToListAsync(TestContext.Current.CancellationToken),
            action => action == "context.draft.approved");

        var rejectedContext = ContextSection.Create(
            "testing", "Testing", "Draft testing rules.", _projectId,
            ContextScopeType.Project, updatedByType: "agent", updatedById: "other-agent");
        _db.ContextSections.Add(rejectedContext);
        new ContentWriter().AddVersion(_db, rejectedContext, "Agent", "other-agent");
        _db.SaveChanges();

        var rejected = await new ReviewContextDraftHandler(factory, new ContentWriter(), new EfAuditWriter())
            .HandleAsync(new ReviewContextDraftCommand(
                _projectId, rejectedContext.Id, rejectedContext.Revision, false, HumanActor()),
                TestContext.Current.CancellationToken);
        Assert.True(rejected.Success, rejected.Error);
        Assert.True(rejected.Value!.Deleted);
        Assert.Null(await _db.ContextSections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == rejectedContext.Id, TestContext.Current.CancellationToken));

        var tombstone = await _db.ContentVersions
            .Where(version => version.TargetId == rejectedContext.Id
                && version.TargetKind == ContentTargetKind.ContextSection)
            .OrderByDescending(version => version.Revision)
            .FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, tombstone.Revision);
        Assert.Contains("\"deleted\":true", tombstone.ContentJson, StringComparison.OrdinalIgnoreCase);
        var auditActions = await _db.AuditEvents.Select(item => item.Action).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("context.draft.rejected", auditActions);
    }

    [Fact]
    public async Task ManuallyInjectedAutoPolicy_DoesNotAutoApplyAcceptedContent()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        // Only documentation RepoPatch has an Auto path. Corrupt or legacy policy
        // values for accepted content must fail closed to human review.
        project.SetProposalPoliciesJson(ProposalPolicyCatalog.Serialize(
            new Dictionary<string, string>
            {
                [ProposalPolicyCatalog.Key(ContentTargetKind.ProjectDescription, ProposalOperation.Update)] =
                    ProposalDecisionMode.Auto.ToString()
            }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(_projectId, Request(), HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ProposalStatus.Pending, result.Value!.Status);
        Assert.Null(result.Value.DecidedByUserId);
        Assert.Null(result.Value.AppliedRevision);

        await _db.Entry(project).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Original description", project.Description);
        Assert.Equal(1, project.Revision);

        var actions = await _db.AuditEvents
            .Where(item => item.TargetId == result.Value.Id.ToString("D"))
            .Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("proposal.created", actions);
        Assert.DoesNotContain("proposal.auto_accepted", actions);
    }

    [Fact]
    public async Task AutoPolicy_DoesNotBypassManualGovernanceForDecision()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        // Even a corrupted/manual database value cannot turn governance into Auto.
        project.SetProposalPoliciesJson("""{"Decision:Update":"Auto"}""");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var decision = Decision.Create(
            "Existing decision", "Context", "Accepted content", _projectId);
        decision.Accept();
        _db.Decisions.Add(decision);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        new ContentWriter().AddVersion(_db, decision, "Human", _userId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new CreateProposalRequest(
            ContentTargetKind.Decision,
            decision.Id,
            ProposalOperation.Update,
            2,
            """{"title":"Updated decision","context":"Updated context","decisionBody":"Updated body","consequences":null}""",
            "Proposes a governance change.",
            "auto-policy-governance-manual");
        var result = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(_projectId, request, HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ProposalStatus.Pending, result.Value!.Status);
        Assert.Null(result.Value.AppliedRevision);

        await _db.Entry(decision).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Accepted content", decision.DecisionBody);
        Assert.Equal(2, decision.Revision);
    }

    [Fact]
    public async Task Accept_WhenRepoPatchIsAlreadyApplying_ReturnsExistingCommandWithoutQueueingDuplicate()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var now = DateTime.UtcNow;
        var proposal = Proposal.Create(
            _projectId,
            null,
            null,
            null,
            ContentTargetKind.RepoPatch,
            _projectId,
            ProposalOperation.Update,
            1,
            new string('a', 64),
            "{}",
            "Applies the reviewed documentation update.",
            ProposalAuthorKind.Human,
            _userId,
            null,
            null,
            "Human",
            "repo-patch-accept-retry-" + Guid.NewGuid().ToString("N"),
            now);
        var device = DaemonDevice.Create(
            "proposal-test-device",
            _userId,
            "proposal-test-fingerprint",
            "proposal-test-token-hash",
            "proposal-test");
        _db.DaemonDevices.Add(device);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.ApplyProposalPatch,
            System.Text.Json.JsonSerializer.Serialize(new { projectId = _projectId, proposalId = proposal.Id }),
            _projectId,
            _userId);
        proposal.StartApply(command.Id, _userId, now);
        _db.Proposals.Add(proposal);
        _db.WorkstationCommands.Add(command);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new AcceptProposalHandler(
            factory, new AuthorizationService(), new EfAuditWriter(), new ContentWriter());
        var retry = await handler.HandleAsync(
            new AcceptProposalCommand(_projectId, proposal.Id, HumanActor()),
            TestContext.Current.CancellationToken);

        Assert.True(retry.Success, retry.Error);
        Assert.Equal(ProposalStatus.Applying, retry.Value!.Status);

        var queuedCommands = await _db.WorkstationCommands
            .Where(item => item.ProjectId == _projectId
                && item.Kind == WorkstationCommandKind.ApplyProposalPatch)
            .ToListAsync(TestContext.Current.CancellationToken);
        var singleCommand = Assert.Single(queuedCommands);
        Assert.Equal(command.Id, singleCommand.Id);
        Assert.Equal(WorkstationCommandStatus.Pending, singleCommand.Status);

        // A terminal command must not be mistaken for a live idempotent retry.
        command.Fail("The workstation rejected the command.");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var staleRetry = await handler.HandleAsync(
            new AcceptProposalCommand(_projectId, proposal.Id, HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.False(staleRetry.Success);
        Assert.Equal(ErrorKind.Conflict, staleRetry.Kind);
    }

    [Fact]
    public async Task StaleBaseRevisionAtSubmission_MarksProposalConflictedWithoutApplyingTarget()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        project.Update(description: "Changed before proposal");
        var saved = await new ContentWriter().SaveAsync(
            _db, project, "Human", _userId.ToString("D"), TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.Error);
        Assert.Equal(2, saved.Value);

        var result = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(_projectId, Request(), HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ProposalStatus.Conflicted, result.Value!.Status);
        Assert.Equal(2, result.Value.ConflictRevision);
        Assert.Null(result.Value.AppliedRevision);

        await _db.Entry(project).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Changed before proposal", project.Description);
        Assert.Equal(2, project.Revision);
        Assert.DoesNotContain(await _db.ContentVersions.ToListAsync(TestContext.Current.CancellationToken),
            item => item.ProposalId == result.Value.Id);
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
