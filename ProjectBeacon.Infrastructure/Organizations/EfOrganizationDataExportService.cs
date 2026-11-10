namespace ProjectBeacon.Infrastructure.Organizations;

using System.Data;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Organizations;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Attachments;
using ProjectBeacon.Infrastructure.Data;

public sealed class EfOrganizationDataExportService(
    IBeaconDbFactory dbFactory,
    IAuthorizationService authorization,
    IAuditWriter auditWriter,
    IAttachmentService attachments,
    TimeProvider timeProvider) : IOrganizationDataExportService
{
    private const long MaximumRows = 100_000;
    private const long MaximumAttachmentBytes = 128L * 1024 * 1024;
    private const long MaximumArchiveBytes = 160L * 1024 * 1024;
    private const long MaximumAttachmentLength = 10L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 48,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<Result<OrganizationDataExport>> ExportAsync(
        Guid orgId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (orgId == Guid.Empty)
            return Result.Failure<OrganizationDataExport>("Organization id is required.");
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Failure<OrganizationDataExport>(
                "A signed-in organization owner or administrator is required.",
                ErrorKind.Forbidden);

        // First prove access in the organization's normal tenant scope. Project-scoped
        // entities cannot be queried with a null project id, so the actual archive read
        // uses a short-lived operator-style unscoped context below. Every query in that
        // context is then constrained by this explicitly authorized OrgId/projectId set.
        var tenant = new TenantContext();
        tenant.Assign(null, orgId, unscoped: false);
        await using (var authorizationDb = dbFactory.CreateDbContext(tenant))
        {
            var permission = await authorization.CanAsync(
                authorizationDb, actor, ResourceType.Org, AuthAction.Administer, orgId: orgId, ct: ct);
            var isOrganizationAdmin = await authorizationDb.OrgMembers.AnyAsync(
                member => member.OrgId == orgId && member.UserId == actor.UserId
                    && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin),
                ct);
            if (!permission.Success || !isOrganizationAdmin)
                return Result.Failure<OrganizationDataExport>(
                    "Only an organization owner or administrator can export its data.",
                    ErrorKind.Forbidden);
        }

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var isolation = string.Equals(
            db.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal)
            ? IsolationLevel.RepeatableRead
            : IsolationLevel.Serializable;
        await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);

        // Repeat authorization inside the export snapshot to close the membership-revocation
        // race between the scoped preflight and the unscoped, explicitly constrained reads.
        var permissionInsideSnapshot = await authorization.CanAsync(
            db, actor, ResourceType.Org, AuthAction.Administer, orgId: orgId, ct: ct);
        var isOrganizationAdminInsideSnapshot = await db.OrgMembers.AsNoTracking()
            .AnyAsync(member => member.OrgId == orgId && member.UserId == actor.UserId
                && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin), ct);
        if (!permissionInsideSnapshot.Success || !isOrganizationAdminInsideSnapshot)
            return Result.Failure<OrganizationDataExport>(
                "Only an organization owner or administrator can export its data.",
                ErrorKind.Forbidden);

        var organization = await db.Orgs.IgnoreQueryFilters().AsNoTracking()
            .Where(org => org.Id == orgId)
            .Select(org => new
            {
                org.Id,
                org.Name,
                org.Description,
                org.CreatedAt,
                org.UpdatedAt,
                org.LifecycleStatus
            })
            .SingleOrDefaultAsync(ct);
        if (organization is null)
            return Result.Failure<OrganizationDataExport>("Organization not found.", ErrorKind.NotFound);

        var projectCount = await db.Projects.IgnoreQueryFilters()
            .LongCountAsync(project => project.OrgId == orgId, ct);
        if (projectCount > MaximumRows)
            return TooLarge();

        var projectIds = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(project => project.OrgId == orgId)
            .OrderBy(project => project.CreatedAt)
            .Select(project => project.Id)
            .ToListAsync(ct);
        var taskIds = db.Tasks.AsNoTracking()
            .Where(task => projectIds.Contains(task.ProjectId))
            .Select(task => task.Id);
        var decisionIds = db.Decisions.AsNoTracking()
            .Where(decision => projectIds.Contains(decision.ProjectId))
            .Select(decision => decision.Id);

        var counts = new long[]
        {
            projectCount,
            await db.OrgMembers.LongCountAsync(member => member.OrgId == orgId, ct),
            await db.OrgInvites.LongCountAsync(invite => invite.OrgId == orgId, ct),
            await db.ProjectMembers.LongCountAsync(member => projectIds.Contains(member.ProjectId), ct),
            await db.ProjectInvites.LongCountAsync(invite => projectIds.Contains(invite.ProjectId), ct),
            await db.Labels.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.LabelPaths.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.Milestones.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.Tasks.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.TaskComments.LongCountAsync(item => taskIds.Contains(item.TaskId), ct),
            await db.TaskDependencies.LongCountAsync(item =>
                taskIds.Contains(item.TaskId) && taskIds.Contains(item.DependentTaskId), ct),
            await db.TaskSteps.LongCountAsync(item =>
                projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId), ct),
            await db.Subtasks.LongCountAsync(item =>
                projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId), ct),
            await db.Reports.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ContextSections.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ContextRevisions.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.Decisions.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.DecisionTasks.LongCountAsync(item =>
                decisionIds.Contains(item.DecisionId) && taskIds.Contains(item.TaskId), ct),
            await db.Constraints.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ChatSessions.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ChatParts.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.TaskAttachments.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct)
        };
        var totalRows = counts.Aggregate(0L, (total, count) => checked(total + count));
        if (totalRows > MaximumRows)
            return TooLarge();

        var members = await (
            from member in db.OrgMembers.AsNoTracking()
            join user in db.Users.AsNoTracking() on member.UserId equals user.Id
            where member.OrgId == orgId
            orderby member.JoinedAt
            select new
            {
                member.UserId,
                user.Login,
                user.Email,
                member.Role,
                member.JoinedAt
            }).ToListAsync(ct);

        var orgInvites = await db.OrgInvites.AsNoTracking()
            .Where(invite => invite.OrgId == orgId)
            .OrderBy(invite => invite.CreatedAt)
            .Select(invite => new
            {
                invite.Id,
                invite.Email,
                invite.Role,
                invite.Status,
                invite.InvitedByUserId,
                invite.AcceptedAt,
                invite.CreatedAt,
                invite.ExpiredAt
            }).ToListAsync(ct);

        var projects = await db.Projects.AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .OrderBy(project => project.CreatedAt)
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.Description,
                project.OrgId,
                project.CreatedAt,
                project.UpdatedAt
            }).ToListAsync(ct);

        var projectMembers = await db.ProjectMembers.AsNoTracking()
            .Where(member => projectIds.Contains(member.ProjectId))
            .OrderBy(member => member.JoinedAt)
            .Select(member => new
            {
                member.ProjectId,
                member.UserId,
                member.Role,
                member.JoinedAt
            }).ToListAsync(ct);

        var projectInvites = await db.ProjectInvites.AsNoTracking()
            .Where(invite => projectIds.Contains(invite.ProjectId))
            .OrderBy(invite => invite.CreatedAt)
            .Select(invite => new
            {
                invite.Id,
                invite.ProjectId,
                invite.Email,
                invite.Role,
                invite.Status,
                invite.InvitedByUserId,
                invite.AcceptedAt,
                invite.CreatedAt,
                invite.ExpiredAt
            }).ToListAsync(ct);

        var labels = await db.Labels.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.ProjectId, item.Name, item.Color, item.PathPrefix })
            .ToListAsync(ct);

        var labelPaths = await db.LabelPaths.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Path)
            .Select(item => new { item.Id, item.ProjectId, item.LabelId, item.Path })
            .ToListAsync(ct);

        var milestones = await db.Milestones.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Order)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Name,
                item.Description,
                item.Order,
                item.CreatedAt,
                item.UpdatedAt,
                item.ClosedAt
            }).ToListAsync(ct);

        var tasks = await db.Tasks.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.Description,
                item.Status,
                item.Priority,
                item.Type,
                item.SubStage,
                item.LabelId,
                item.MilestoneId,
                item.ReviewNotes,
                item.CreatedAt,
                item.CompletedAt,
                item.PipelineStage
            }).ToListAsync(ct);

        var taskComments = await db.TaskComments.AsNoTracking()
            .Where(item => taskIds.Contains(item.TaskId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.UserId,
                item.Content,
                item.CreatedAt,
                item.UpdatedAt
            }).ToListAsync(ct);

        var taskDependencies = await db.TaskDependencies.AsNoTracking()
            .Where(item => taskIds.Contains(item.TaskId) && taskIds.Contains(item.DependentTaskId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new { item.Id, item.TaskId, item.DependentTaskId, item.CreatedAt })
            .ToListAsync(ct);

        var taskSteps = await db.TaskSteps.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId))
            .OrderBy(item => item.TaskId)
            .ThenBy(item => item.SortOrder)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.ProjectId,
                item.Title,
                item.SortOrder,
                item.CreatedAt,
                item.DoneAt
            }).ToListAsync(ct);

        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.ProjectId,
                item.TaskPhaseId,
                item.ProfileKey,
                item.WakePlannerAfter,
                item.Instructions,
                item.Status,
                item.DiffRef,
                item.Summary,
                item.ReopenCount,
                item.CreatedAt,
                item.UpdatedAt
            }).ToListAsync(ct);

        var reports = await db.Reports.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.BodyMarkdown,
                item.SnapshotJson,
                item.CreatedByType,
                item.CreatedById,
                item.CreatedAt
            }).ToListAsync(ct);

        var contextSections = await db.ContextSections.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Ordinal)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                RepoId = item.RepoId != null && projectIds.Contains(item.RepoId.Value) ? item.RepoId : null,
                item.TaskId,
                item.SectionId,
                item.Key,
                item.Title,
                item.BodyMarkdown,
                item.Ordinal,
                item.ScopeType,
                item.Path,
                item.Source,
                item.SourcePath,
                item.ReviewState,
                item.UpdatedByType,
                item.UpdatedById,
                item.UpdatedAt
            }).ToListAsync(ct);

        var contextRevisions = await db.ContextRevisions.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.CompiledHash,
                item.CompilerVersion,
                item.TargetRepoId,
                item.TargetPath,
                item.TargetTaskId,
                item.BriefMarkdown,
                item.BriefJson,
                item.TokenEstimate,
                item.SourceNodeIds,
                item.CreatedAt
            }).ToListAsync(ct);

        var decisions = await db.Decisions.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.Context,
                item.DecisionBody,
                item.Consequences,
                item.Status,
                item.SupersededById,
                item.CreatedAt
            }).ToListAsync(ct);

        var decisionTasks = await db.DecisionTasks.AsNoTracking()
            .Where(item => decisionIds.Contains(item.DecisionId) && taskIds.Contains(item.TaskId))
            .Select(item => new { item.DecisionId, item.TaskId })
            .ToListAsync(ct);

        var constraints = await db.Constraints.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Body,
                item.ScopePath,
                item.Kind,
                item.Status,
                item.CreatedAt
            }).ToListAsync(ct);

        var chatSessions = await db.ChatSessions.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.AgentMode,
                item.Status,
                item.CreatedAt,
                item.UpdatedAt,
                item.ArchivedAt,
                item.UsagePromptTokens,
                item.UsageCompletionTokens,
                item.UsageAssistantMessages,
                item.UsageTotalCost,
                item.UsageContextLimit,
                item.UsageGenerationSeconds
            }).ToListAsync(ct);

        var chatParts = await db.ChatParts.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.ProjectId)
            .ThenBy(item => item.SessionId)
            .ThenBy(item => item.SortOrder)
            .Select(item => new
            {
                item.Id,
                item.SessionId,
                item.ProjectId,
                item.Role,
                item.Kind,
                item.Body,
                item.ExternalId,
                item.SortOrder,
                item.CreatedAt,
                item.Tool,
                item.ToolStatus,
                item.ToolInput,
                item.ToolResult,
                item.DurationMs,
                item.Complete
            }).ToListAsync(ct);

        var attachmentRows = await db.TaskAttachments.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.TaskId,
                item.FileName,
                item.ContentType,
                item.Length,
                item.CreatedByType,
                item.CreatedById,
                item.CreatedAt
            }).ToListAsync(ct);

        var attachmentBytes = attachmentRows.Aggregate(
            0L, (total, item) => checked(total + item.Length));
        if (attachmentBytes > MaximumAttachmentBytes)
            return TooLarge();

        var exportedAt = timeProvider.GetUtcNow();
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddTextEntryAsync(archive, "README.txt", BuildReadme(exportedAt), ct);
            await AddJsonEntryAsync(archive, "manifest.json", new
            {
                schemaVersion = 1,
                exportedAtUtc = exportedAt,
                organizationId = orgId,
                projectCount = projects.Count,
                taskCount = tasks.Count,
                attachmentCount = attachmentRows.Count,
                rowCount = totalRows
            }, ct);
            await AddJsonEntryAsync(archive, "organization.json", organization, ct);
            await AddJsonEntryAsync(archive, "members.json", members, ct);
            await AddJsonEntryAsync(archive, "organization-invites.json", orgInvites, ct);
            await AddJsonEntryAsync(archive, "projects.json", projects, ct);
            await AddJsonEntryAsync(archive, "project-members.json", projectMembers, ct);
            await AddJsonEntryAsync(archive, "project-invites.json", projectInvites, ct);
            await AddJsonEntryAsync(archive, "labels.json", labels, ct);
            await AddJsonEntryAsync(archive, "label-paths.json", labelPaths, ct);
            await AddJsonEntryAsync(archive, "milestones.json", milestones, ct);
            await AddJsonEntryAsync(archive, "tasks.json", tasks, ct);
            await AddJsonEntryAsync(archive, "task-comments.json", taskComments, ct);
            await AddJsonEntryAsync(archive, "task-dependencies.json", taskDependencies, ct);
            await AddJsonEntryAsync(archive, "task-steps.json", taskSteps, ct);
            await AddJsonEntryAsync(archive, "subtasks.json", subtasks, ct);
            await AddJsonEntryAsync(archive, "reports.json", reports, ct);
            await AddJsonEntryAsync(archive, "context-sections.json", contextSections, ct);
            await AddJsonEntryAsync(archive, "context-revisions.json", contextRevisions, ct);
            await AddJsonEntryAsync(archive, "decisions.json", decisions, ct);
            await AddJsonEntryAsync(archive, "decision-tasks.json", decisionTasks, ct);
            await AddJsonEntryAsync(archive, "constraints.json", constraints, ct);
            await AddJsonEntryAsync(archive, "chat-sessions.json", chatSessions, ct);
            await AddJsonEntryAsync(archive, "chat-parts.json", chatParts, ct);

            var attachmentManifest = new List<object>(attachmentRows.Count);
            foreach (var attachment in attachmentRows)
            {
                ct.ThrowIfCancellationRequested();
                // Prefer the bytea row in the same repeatable-read transaction. Only use
                // the attachment service for legacy filesystem-backed rows, which are
                // immutable once uploaded and still pass its safe path/content checks.
                var storedContent = await db.TaskAttachmentContents.AsNoTracking()
                    .Where(payload => payload.ProjectId == attachment.ProjectId
                        && payload.TaskAttachmentId == attachment.Id)
                    .Select(payload => payload.Content)
                    .SingleOrDefaultAsync(ct);
                byte[] content;
                var detectedContentType = attachment.ContentType;
                if (storedContent is not null)
                {
                    content = storedContent;
                }
                else
                {
                    var legacy = await attachments.ReadTaskAsync(
                        attachment.ProjectId,
                        attachment.TaskId,
                        attachment.Id,
                        ct,
                        MaximumAttachmentLength);
                    if (legacy is null)
                        return Result.Failure<OrganizationDataExport>(
                            "An attachment could not be read; no partial archive was produced.",
                            ErrorKind.Unavailable);
                    content = legacy.Value.Content;
                    detectedContentType = legacy.Value.Attachment.ContentType;
                }

                if (content.LongLength != attachment.Length || content.LongLength > MaximumAttachmentLength)
                    return Result.Failure<OrganizationDataExport>(
                        "An attachment failed the archive integrity or size check; no partial archive was produced.",
                        ErrorKind.Unavailable);

                var archivePath = $"attachments/{attachment.Id:N}-{SafeFileName(attachment.FileName)}";
                var entry = archive.CreateEntry(archivePath, CompressionLevel.Fastest);
                await using (var entryStream = entry.Open())
                    await entryStream.WriteAsync(content.AsMemory(), ct);

                attachmentManifest.Add(new
                {
                    attachment.Id,
                    attachment.ProjectId,
                    attachment.TaskId,
                    attachment.FileName,
                    ContentType = detectedContentType,
                    attachment.Length,
                    attachment.CreatedByType,
                    attachment.CreatedById,
                    attachment.CreatedAt,
                    ArchivePath = archivePath
                });
            }

            await AddJsonEntryAsync(archive, "task-attachments.json", attachmentManifest, ct);
        }

        if (output.Length > MaximumArchiveBytes)
            return TooLarge();

        // AuditEvent accepts short allowlisted codes and integer counters only.
        var details = JsonSerializer.Serialize(new
        {
            scope = "org_export",
            count = totalRows
        }, JsonOptions);
        auditWriter.Stage(db, AuditEventFactory.ForActor(
            actor,
            "organization.data_exported",
            "Org",
            orgId.ToString("D"),
            orgId,
            null,
            detailsJson: details));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var fileName = $"projectbeacon-org-{orgId:N}-export-{exportedAt:yyyyMMdd'T'HHmmss'Z'}.zip";
        return Result.Ok(new OrganizationDataExport(
            output.ToArray(),
            fileName,
            exportedAt,
            projects.Count,
            attachmentRows.Count));
    }

    private static Result<OrganizationDataExport> TooLarge() =>
        Result.Failure<OrganizationDataExport>(
            "This organization export exceeds the synchronous export limits. Contact support for an assisted export.",
            "export_too_large",
            ErrorKind.Conflict);

    private static async Task AddJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T data,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, data, JsonOptions, ct);
    }

    private static async Task AddTextEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes.AsMemory(), ct);
    }

    private static string SafeFileName(string fileName)
    {
        var candidate = Path.GetFileName(fileName.Replace('\\', '/'));
        var safe = new string(candidate
            .Select(character => char.IsControl(character)
                || character is '/' or '\\' or ':'
                ? '_'
                : character)
            .Take(120)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        return string.IsNullOrWhiteSpace(safe) ? "attachment.bin" : safe;
    }

    private static string BuildReadme(DateTimeOffset exportedAt) =>
        $"""
        ProjectBeacon organization data export
        Exported at (UTC): {exportedAt:O}

        This archive is a read-only snapshot. It does not delete or change source data.

        Included: organization and membership metadata, invitations (without token hashes),
        projects, tasks, comments, dependencies, checklists, subtasks, milestones, labels,
        reports, context, decisions, constraints, chat content, and task attachments.

        Excluded by design: password hashes, password-reset tokens, user sessions, TOTP
        secrets and recovery-code hashes, API-token hashes or raw credentials, device tokens
        and fingerprints, encrypted provider/MCP credentials, local workspace roots and
        external daemon session identifiers, and platform audit/security internals.
        Customer-authored text, chat messages and attachments are included as stored and may
        themselves contain sensitive values that users previously placed in that content.

        Pipeline orchestration lifecycle, review/evaluation runs, operational telemetry and platform audit/security history are not part of this first workspace export version.

        This is an application data export, not a deletion or retention request.
        """;
}
