namespace ProjectBeacon.Application.Tests.Proposals;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Proposals;
using Application.Persistence;
using Application.Proposals;
using Application.Tasks;
using Domain.Entities.Agents;
using Domain.Entities.Content;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Common;
using Domain.Enums;
using Infrastructure.Audit;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class RepoPatchProposalWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _userId;
    private readonly Guid _projectId;

    public RepoPatchProposalWorkflowTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("RepoPatch workflow org");
        _db.Orgs.Add(org);
        _db.SaveChanges();

        var project = Project.Create("RepoPatch workflow project", null, org.Id);
        _db.Projects.Add(project);

        var user = User.Create("repo-patch-reviewer", "repo-patch-reviewer@beacon.local", "hash");
        _db.Users.Add(user);
        _db.SaveChanges();

        _projectId = project.Id;
        _userId = user.Id;
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, _userId, MemberRole.Owner));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task HumanAcceptance_AtomicallyQueuesApprovedDocumentationPatch()
    {
        const string before = "Before documentation.\n";
        const string after = "Approved documentation.\n";
        const string path = "docs/guide.md";
        var baseHash = Hash(before);
        var resultHash = Hash(after);
        var patch = """
            diff --git a/docs/guide.md b/docs/guide.md
            index 9d3f98b..f5a8c3d 100644
            --- a/docs/guide.md
            +++ b/docs/guide.md
            @@ -1 +1 @@
            -Before documentation.
            +Approved documentation.
            """;
        var payload = JsonSerializer.Serialize(new
        {
            patch,
            files = new[]
            {
                new
                {
                    gitPath = path,
                    projectPath = path,
                    baseHash,
                    resultHash
                }
            }
        });

        var device = DaemonDevice.Create(
            "docs-writer-device",
            _userId,
            "docs-writer-fingerprint-" + Guid.NewGuid().ToString("N"),
            "docs-writer-device-token-hash",
            "bcd_docs_writer");
        device.Heartbeat("{}", "{}", "docs-writer-instance");

        var token = ApiToken.Create(
            "docs-writer-harness",
            "docs-writer-harness-token-hash-" + Guid.NewGuid().ToString("N"),
            "bcn_docs_writer",
            _projectId,
            ApiTokenCapability.ContextRead,
            null,
            _userId,
            ApiTokenClientKind.Harness,
            device.Id);

        var runtime = ProjectRuntime.Create(_projectId, device.Id, "/workspace/project");
        var task = TaskItem.Create("Update the project documentation.", _projectId);
        var subtask = Subtask.Create(
            "Write the approved documentation update.",
            task.Id,
            _projectId,
            allowedMcpTools: [McpToolNames.KnowledgePropose],
            allowedPaths: ["docs"],
            profileKey: "docs-writer");
        subtask.Start();
        subtask.ReportResult("worktree:verified", "Documentation patch is ready for review.");

        var session = PipelineSession.Create(
            task.Id,
            _projectId,
            PipelineRole.Actor,
            "Write a documentation-only patch.",
            subtask.Id);
        session.Launch();
        session.Close();

        var proposal = Proposal.Create(
            _projectId,
            task.Id,
            subtask.Id,
            session.Id,
            ContentTargetKind.RepoPatch,
            _projectId,
            ProposalOperation.Update,
            1,
            Hash($"{path}\\0{baseHash}\\0{resultHash}"),
            payload,
            "Clarifies the setup instructions.",
            ProposalAuthorKind.Agent,
            null,
            token.Id,
            "docs-writer",
            PipelineRole.Actor.ToString(),
            "repo-patch-human-approval-" + Guid.NewGuid().ToString("N"),
            DateTime.UtcNow);

        _db.DaemonDevices.Add(device);
        _db.ApiTokens.Add(token);
        _db.ProjectRuntimes.Add(runtime);
        _db.Tasks.Add(task);
        _db.Subtasks.Add(subtask);
        _db.PipelineSessions.Add(session);
        _db.Proposals.Add(proposal);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new AcceptProposalHandler(
            HandlerSqlite.Factory(_connection),
            new AuthorizationService(),
            new EfAuditWriter(),
            new ContentWriter());
        var result = await handler.HandleAsync(
            new AcceptProposalCommand(
                _projectId,
                proposal.Id,
                new ActorContext(
                    ActorType.Human,
                    _userId,
                    false,
                    null,
                    null,
                    _projectId,
                    null,
                    ApiTokenCapability.None)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ProposalStatus.Applying, result.Value!.Status);
        Assert.NotNull(result.Value.ApplyCommandId);

        var queued = await _db.WorkstationCommands.SingleAsync(
            command => command.Id == result.Value.ApplyCommandId,
            TestContext.Current.CancellationToken);
        Assert.Equal(WorkstationCommandKind.ApplyProposalPatch, queued.Kind);
        Assert.Equal(WorkstationCommandStatus.Pending, queued.Status);
        Assert.Equal(_projectId, queued.ProjectId);

        using var commandPayload = JsonDocument.Parse(queued.PayloadJson);
        Assert.Equal(_projectId, commandPayload.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(proposal.Id, commandPayload.RootElement.GetProperty("proposalId").GetGuid());
        Assert.Equal(patch, commandPayload.RootElement.GetProperty("patch").GetString());
        Assert.Contains("docs", commandPayload.RootElement.GetProperty("allowedPaths")
            .EnumerateArray().Select(value => value.GetString()));

        var persistedProposal = await _db.Proposals.SingleAsync(
            item => item.Id == proposal.Id,
            TestContext.Current.CancellationToken);
        await _db.Entry(persistedProposal).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Applying, persistedProposal.Status);
        Assert.Equal(queued.Id, persistedProposal.ApplyCommandId);
        Assert.Equal(_userId, persistedProposal.DecidedByUserId);

        var auditActions = await _db.AuditEvents
            .Where(item => item.TargetId == proposal.Id.ToString("D"))
            .Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("proposal.patch.apply_queued", auditActions);
    }

    [Theory]
    [InlineData(false, false, ProposalStatus.Pending)]
    [InlineData(true, false, ProposalStatus.Pending)]
    [InlineData(false, true, ProposalStatus.Pending)]
    [InlineData(true, true, ProposalStatus.Applying)]
    public async Task CreateRepoPatch_AutomaticApplicationRequiresProjectAndProfilePolicies(
        bool projectAutoApply,
        bool profileAutoApply,
        ProposalStatus expectedStatus)
    {
        const string projectPath = "docs/guide.md";
        const string before = "Before documentation.\n";
        const string after = "Updated documentation.\n";
        var patch = """
            diff --git a/docs/guide.md b/docs/guide.md
            index 9d3f98b..f5a8c3d 100644
            --- a/docs/guide.md
            +++ b/docs/guide.md
            @@ -1 +1 @@
            -Before documentation.
            +Updated documentation.
            """;
        var payload = JsonSerializer.Serialize(new
        {
            patch,
            files = new[]
            {
                new
                {
                    gitPath = projectPath,
                    projectPath,
                    baseHash = Hash(before),
                    resultHash = Hash(after)
                }
            }
        });

        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        project.SetProposalPoliciesJson(ProposalPolicyCatalog.Serialize(
            new Dictionary<string, string>
            {
                [ProposalPolicyCatalog.Key(ContentTargetKind.RepoPatch, ProposalOperation.Update)] =
                    (projectAutoApply ? ProposalDecisionMode.Auto : ProposalDecisionMode.Manual).ToString()
            }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var device = DaemonDevice.Create(
            "repo-patch-auto-device",
            _userId,
            "repo-patch-auto-fingerprint-" + Guid.NewGuid().ToString("N"),
            "repo-patch-auto-device-token-" + Guid.NewGuid().ToString("N"),
            "bcd_repo_patch_auto");
        device.Heartbeat("{}", "{}", "repo-patch-auto-instance");
        var token = ApiToken.Create(
            "repo-patch-auto-harness",
            "repo-patch-auto-harness-token-" + Guid.NewGuid().ToString("N"),
            "bcn_repo_patch_auto",
            _projectId,
            ApiTokenCapability.ContextRead,
            null,
            _userId,
            ApiTokenClientKind.Harness,
            device.Id);
        var runtime = ProjectRuntime.Create(_projectId, device.Id, "/workspace/project");
        var task = TaskItem.Create("Update the project documentation.", _projectId);

        var docsProfile = BuiltInAgentProfiles.Get("docs-writer")!;
        var profileOverride = AgentProfile.Create(
            _userId,
            docsProfile.Key,
            docsProfile.Name,
            docsProfile.Description,
            docsProfile.Instruction,
            docsProfile.Mode.ToString(),
            docsProfile.Tools,
            docsProfile.PathPolicy.ToString(),
            docsProfile.DefaultPaths,
            docsProfile.CommandsEnabled,
            docsProfile.ReportsDiff,
            docsProfile.SizeHint,
            docsProfile.Effort,
            profileAutoApply ? "Auto" : "Review");
        var profileSnapshot = JsonSerializer.Serialize(
            BuiltInAgentProfiles.ApplyOverride(docsProfile, profileOverride));

        var subtask = Subtask.Create(
            "Prepare a documentation-only update.",
            task.Id,
            _projectId,
            allowedMcpTools: [McpToolNames.KnowledgePropose],
            allowedPaths: ["docs"],
            profileKey: "docs-writer",
            profileSnapshotJson: profileSnapshot);
        subtask.Start();
        var session = PipelineSession.Create(
            task.Id, _projectId, PipelineRole.Actor, "Documentation Actor session.", subtask.Id);
        session.Launch();

        _db.DaemonDevices.Add(device);
        _db.ApiTokens.Add(token);
        _db.ProjectRuntimes.Add(runtime);
        _db.Tasks.Add(task);
        _db.Subtasks.Add(subtask);
        _db.PipelineSessions.Add(session);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = new ActorContext(
            ActorType.ApiToken,
            _userId,
            false,
            token.Id,
            null,
            _projectId,
            null,
            ApiTokenCapability.ContextRead);
        var request = new CreateProposalRequest(
            ContentTargetKind.RepoPatch,
            _projectId,
            ProposalOperation.Update,
            1,
            payload,
            "Updates the setup guide.",
            "repo-patch-auto-" + Guid.NewGuid().ToString("N"));
        var created = await new CreateProposalHandler(
                HandlerSqlite.Factory(_connection), new EfAuditWriter())
            .HandleAsync(
                new CreateProposalCommand(
                    _projectId,
                    request,
                    actor,
                    task.Id,
                    subtask.Id,
                    session.Id,
                    PipelineRole.Actor.ToString()),
                TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        Assert.Equal(expectedStatus, created.Value!.Status);

        var commands = await _db.WorkstationCommands
            .Where(command => command.ProjectId == _projectId
                && command.Kind == WorkstationCommandKind.ApplyProposalPatch)
            .ToListAsync(TestContext.Current.CancellationToken);
        if (projectAutoApply && profileAutoApply)
        {
            var queued = Assert.Single(commands);
            Assert.Equal(ProposalStatus.Applying, created.Value.Status);
            Assert.Equal(queued.Id, created.Value.ApplyCommandId);
            Assert.Equal(WorkstationCommandStatus.Pending, queued.Status);
            Assert.Contains(await _db.AuditEvents.Select(item => item.Action)
                .ToListAsync(TestContext.Current.CancellationToken),
                action => action == "proposal.patch.apply_queued");
        }
        else
        {
            Assert.Empty(commands);
            Assert.Null(created.Value.ApplyCommandId);
            Assert.DoesNotContain(await _db.AuditEvents.Select(item => item.Action)
                .ToListAsync(TestContext.Current.CancellationToken),
                action => action == "proposal.patch.apply_queued");
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
